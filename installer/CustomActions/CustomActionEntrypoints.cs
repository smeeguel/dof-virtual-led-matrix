using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using WixToolset.Dtf.WindowsInstaller;

namespace VirtualDofMatrix.CustomActions;

public static class CustomActionEntrypoints
{
    private const string DefaultDofConfigPath = @"C:\DirectOutput\Config";
    private const string DefaultTemplate = "single_matrix";

    [CustomAction]
    public static ActionResult DetectDofDefaults(Session session)
    {
        try
        {
            // Keep path detection deterministic and readable so installer logs can be used for support requests.
            var explicitPath = session["DOFCONFIGPATH"] == null ? null : session["DOFCONFIGPATH"].Trim();
            var selectedPath = IsNullOrWhiteSpace(explicitPath) ? DefaultDofConfigPath : explicitPath;

            if (Directory.Exists(DefaultDofConfigPath))
            {
                selectedPath = DefaultDofConfigPath;
            }

            session["DOFCONFIGPATH"] = selectedPath;
            session["BACKUP_ENABLED"] = IsNullOrWhiteSpace(session["BACKUP_ENABLED"]) ? "1" : session["BACKUP_ENABLED"];
            session["TOY_TEMPLATE"] = IsNullOrWhiteSpace(session["TOY_TEMPLATE"]) ? DefaultTemplate : session["TOY_TEMPLATE"];

            // Compute a backup target under the inferred DOF root unless the user already supplied an explicit location.
            if (IsNullOrWhiteSpace(session["BACKUP_PATH"]))
            {
                var dofRoot = ResolveDofRootFromConfigPath(selectedPath ?? DefaultDofConfigPath);
                var stampedName = BuildTimestampedBackupFolderName();
                session["BACKUP_PATH"] = Path.Combine(Path.Combine(dofRoot, "Backups"), stampedName);
            }

            session.Log("DetectDofDefaults selected DOFCONFIGPATH='{0}', BACKUP_ENABLED='{1}', TOY_TEMPLATE='{2}', BACKUP_PATH='{3}'.",
                session["DOFCONFIGPATH"], session["BACKUP_ENABLED"], session["TOY_TEMPLATE"], session["BACKUP_PATH"]);

            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            session.Log("DetectDofDefaults failed: {0}", ex);
            return ActionResult.Failure;
        }
    }

    [CustomAction]
    public static ActionResult ValidateInstallerSelections(Session session)
    {
        try
        {
            var requestedPath = NormalizePath(session["DOFCONFIGPATH"]);
            if (IsNullOrWhiteSpace(requestedPath))
            {
                return FailWithUserMessage(session, "DOF config path is required.",
                    "Please select your DirectOutput Config folder (for example C:\\DirectOutput\\Config) and try again.");
            }

            if (!HasConfigSuffix(requestedPath))
            {
                return FailWithUserMessage(session, "DOF config path must end with \\Config.",
                    "The DOF config path must end with \\Config (for example C:\\DirectOutput\\Config).");
            }

            var dofRoot = ResolveDofRootFromConfigPath(requestedPath);
            if (IsNullOrWhiteSpace(dofRoot) || !Path.IsPathRooted(dofRoot))
            {
                return FailWithUserMessage(session, "Could not derive DOF root folder from DOFCONFIGPATH.",
                    "Installer could not derive the DirectOutput root folder from the selected Config path.");
            }

            session["DOFCONFIGPATH"] = requestedPath;
            session["DOFROOTPATH"] = dofRoot;

            if (IsNullOrWhiteSpace(session["TOY_TEMPLATE"]))
            {
                session["TOY_TEMPLATE"] = DefaultTemplate;
            }

            if (IsNullOrWhiteSpace(session["BACKUP_PATH"]))
            {
                session["BACKUP_PATH"] = Path.Combine(Path.Combine(dofRoot, "Backups"), BuildTimestampedBackupFolderName());
            }

            session.Log("ValidateInstallerSelections accepted DOFCONFIGPATH='{0}', DOFROOTPATH='{1}', BACKUP_ENABLED='{2}', BACKUP_PATH='{3}', TOY_TEMPLATE='{4}'.",
                session["DOFCONFIGPATH"], session["DOFROOTPATH"], session["BACKUP_ENABLED"], session["BACKUP_PATH"], session["TOY_TEMPLATE"]);

            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            session.Log("ValidateInstallerSelections failed: {0}", ex);
            return FailWithUserMessage(session, "Unexpected validation failure.",
                "Unable to validate installer selections. Please review the installer log for details.");
        }
    }

    [CustomAction]
    public static ActionResult ApplyDofTemplateAndBinaries(Session session)
    {
        try
        {
            var customActionData = session.CustomActionData;
            var dofConfigPath = NormalizePath(ReadCustomActionDataValue(customActionData, "DOFCONFIGPATH"));
            var dofRootPath = NormalizePath(ReadCustomActionDataValue(customActionData, "DOFROOTPATH"));
            var backupEnabled = ReadCustomActionDataValue(customActionData, "BACKUP_ENABLED");
            var backupPath = NormalizePath(ReadCustomActionDataValue(customActionData, "BACKUP_PATH"));
            var toyTemplate = NormalizeTemplate(ReadCustomActionDataValue(customActionData, "TOY_TEMPLATE"));
            var installFolder = NormalizePath(ReadCustomActionDataValue(customActionData, "INSTALLFOLDER"));

            session.Log("ApplyDofTemplateAndBinaries started with DOFCONFIGPATH='{0}', DOFROOTPATH='{1}', BACKUP_ENABLED='{2}', BACKUP_PATH='{3}', TOY_TEMPLATE='{4}', INSTALLFOLDER='{5}'.",
                dofConfigPath, dofRootPath, backupEnabled, backupPath, toyTemplate, installFolder);

            if (IsNullOrWhiteSpace(dofConfigPath) || !HasConfigSuffix(dofConfigPath))
            {
                return FailWithUserMessage(session, "DOFCONFIGPATH is invalid in execute sequence.",
                    "DirectOutput Config path is invalid. It must end with \\Config.");
            }

            if (IsNullOrWhiteSpace(dofRootPath))
            {
                dofRootPath = ResolveDofRootFromConfigPath(dofConfigPath);
            }

            EnsureDirectoryExists(dofConfigPath, session, "DOF config destination");
            EnsureDirectoryExists(dofRootPath, session, "DOF root destination");

            var templateDirectoryName = ResolveTemplateDirectoryName(toyTemplate);
            var sourceDofRoot = ResolveDofPayloadRoot(installFolder, session);
            var templateSourceFolder = Path.Combine(sourceDofRoot, Path.Combine("Config\\templates", templateDirectoryName));

            session.Log("Resolved template source folder: '{0}'.", templateSourceFolder);
            session.Log("Resolved DOF payload source root: '{0}'.", sourceDofRoot);

            if (!Directory.Exists(templateSourceFolder))
            {
                return FailWithUserMessage(session, "Template source folder not found.",
                    "Template files were not found in the installer payload. Re-run setup from a complete installer package.");
            }

            var overwriteCandidates = ComputeOverwriteCandidates(templateSourceFolder, dofConfigPath);
            session.Log("Computed overwrite candidates: {0} file(s).", overwriteCandidates.Count);

            if (IsBackupEnabled(backupEnabled) && overwriteCandidates.Count > 0)
            {
                if (IsNullOrWhiteSpace(backupPath))
                {
                    backupPath = Path.Combine(Path.Combine(dofRootPath, "Backups"), BuildTimestampedBackupFolderName());
                }

                CreateBackup(overwriteCandidates, dofConfigPath, backupPath, session);
            }
            else
            {
                session.Log("Skipping backup creation. Enabled='{0}', overwrite candidates='{1}'.", backupEnabled, overwriteCandidates.Count);
            }

            CopyTemplateFiles(templateSourceFolder, dofConfigPath, session);

            // Ensure architecture folders exist before copying DirectOutput DLLs expected by DOF tooling.
            var x64Destination = Path.Combine(dofRootPath, "x64");
            var x86Destination = Path.Combine(dofRootPath, "x86");
            EnsureDirectoryExists(x64Destination, session, "DOF x64 folder");
            EnsureDirectoryExists(x86Destination, session, "DOF x86 folder");

            var sourceX64Dll = Path.Combine(Path.Combine(sourceDofRoot, "x64"), "DirectOutput.dll");
            var sourceX86Dll = Path.Combine(Path.Combine(sourceDofRoot, "x86"), "DirectOutput.dll");
            var destinationX64Dll = Path.Combine(x64Destination, "DirectOutput.dll");
            var destinationX86Dll = Path.Combine(x86Destination, "DirectOutput.dll");

            CopyFileWithValidation(sourceX64Dll, destinationX64Dll, session, "DirectOutput x64 DLL");
            CopyFileWithValidation(sourceX86Dll, destinationX86Dll, session, "DirectOutput x86 DLL");

            session.Log("ApplyDofTemplateAndBinaries completed successfully.");
            return ActionResult.Success;
        }
        catch (UnauthorizedAccessException ex)
        {
            session.Log("ApplyDofTemplateAndBinaries permission error: {0}", ex);
            return FailWithUserMessage(session, "Access denied while writing DOF files.",
                "Installer does not have permission to write to the selected DirectOutput folder. Run setup as administrator and verify folder permissions.");
        }
        catch (IOException ex)
        {
            session.Log("ApplyDofTemplateAndBinaries I/O error: {0}", ex);
            return FailWithUserMessage(session, "File copy failed due to an I/O error.",
                "Installer could not copy DirectOutput files. Verify the path exists and is not locked by another process.");
        }
        catch (Exception ex)
        {
            session.Log("ApplyDofTemplateAndBinaries failed: {0}", ex);
            return FailWithUserMessage(session, "Unexpected installer failure while applying DOF files.",
                "Installer hit an unexpected error while applying DOF template files. Check the MSI log for details.");
        }
    }

    private static List<string> ComputeOverwriteCandidates(string templateSourceFolder, string dofConfigPath)
    {
        var candidates = new List<string>();
        var files = Directory.GetFiles(templateSourceFolder, "*", SearchOption.AllDirectories);

        for (var i = 0; i < files.Length; i++)
        {
            var sourceFile = files[i];
            var relativePath = sourceFile.Substring(templateSourceFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destinationFile = Path.Combine(dofConfigPath, relativePath);

            if (File.Exists(destinationFile))
            {
                candidates.Add(destinationFile);
            }
        }

        return candidates;
    }

    private static void CreateBackup(List<string> overwriteCandidates, string dofConfigPath, string backupPath, Session session)
    {
        session.Log("Creating backup at '{0}' for {1} file(s).", backupPath, overwriteCandidates.Count);
        Directory.CreateDirectory(backupPath);

        for (var i = 0; i < overwriteCandidates.Count; i++)
        {
            var existingFile = overwriteCandidates[i];
            var relativePath = existingFile.Substring(dofConfigPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var backupFile = Path.Combine(backupPath, relativePath);
            var backupFolder = Path.GetDirectoryName(backupFile);

            if (!IsNullOrWhiteSpace(backupFolder))
            {
                Directory.CreateDirectory(backupFolder);
            }

            File.Copy(existingFile, backupFile, true);
            session.Log("Backed up '{0}' to '{1}'.", existingFile, backupFile);
        }
    }

    private static void CopyTemplateFiles(string templateSourceFolder, string dofConfigPath, Session session)
    {
        var files = Directory.GetFiles(templateSourceFolder, "*", SearchOption.AllDirectories);
        session.Log("Copying {0} template file(s) from '{1}' to '{2}'.", files.Length, templateSourceFolder, dofConfigPath);

        for (var i = 0; i < files.Length; i++)
        {
            var sourceFile = files[i];
            var relativePath = sourceFile.Substring(templateSourceFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destinationFile = Path.Combine(dofConfigPath, relativePath);
            var destinationFolder = Path.GetDirectoryName(destinationFile);

            if (!IsNullOrWhiteSpace(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            File.Copy(sourceFile, destinationFile, true);
            session.Log("Copied template file '{0}' to '{1}'.", sourceFile, destinationFile);
        }
    }

    private static void CopyFileWithValidation(string sourceFile, string destinationFile, Session session, string description)
    {
        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException(string.Format(CultureInfo.InvariantCulture,
                "{0} source file was not found at '{1}'.", description, sourceFile));
        }

        var destinationFolder = Path.GetDirectoryName(destinationFile);
        if (!IsNullOrWhiteSpace(destinationFolder))
        {
            Directory.CreateDirectory(destinationFolder);
        }

        File.Copy(sourceFile, destinationFile, true);
        session.Log("Copied {0}: '{1}' -> '{2}'.", description, sourceFile, destinationFile);
    }

    private static void EnsureDirectoryExists(string path, Session session, string description)
    {
        if (IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                "{0} path is empty.", description));
        }

        Directory.CreateDirectory(path);
        session.Log("Ensured {0} exists at '{1}'.", description, path);
    }

    private static string ResolveDofPayloadRoot(string installFolder, Session session)
    {
        // Some WiX harvest layouts preserve a top-level "DOF" folder while others place "Config/x64/x86"
        // directly under INSTALLFOLDER. Probe both shapes so runtime copy logic is resilient across packaging modes.
        var payloadRootWithDofFolder = Path.Combine(installFolder, "DOF");
        if (Directory.Exists(payloadRootWithDofFolder))
        {
            session.Log("Using DOF payload root with explicit folder: '{0}'.", payloadRootWithDofFolder);
            return payloadRootWithDofFolder;
        }

        var flatPayloadConfigFolder = Path.Combine(installFolder, "Config");
        if (Directory.Exists(flatPayloadConfigFolder))
        {
            session.Log("Using DOF payload root without explicit DOF folder: '{0}'.", installFolder);
            return installFolder;
        }

        session.Log("DOF payload root probe failed for INSTALLFOLDER '{0}'.", installFolder);
        return payloadRootWithDofFolder;
    }

    private static string ResolveTemplateDirectoryName(string templateValue)
    {
        if (templateValue.Equals("matrix_plus_3_strips", StringComparison.OrdinalIgnoreCase))
        {
            return "02-matrix-and-flasher-strips";
        }

        if (templateValue.Equals("single_matrix", StringComparison.OrdinalIgnoreCase))
        {
            return "01-single-matrix";
        }

        throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
            "Unsupported TOY_TEMPLATE value '{0}'.", templateValue));
    }

    private static string NormalizeTemplate(string templateValue)
    {
        return IsNullOrWhiteSpace(templateValue) ? DefaultTemplate : templateValue.Trim();
    }

    private static bool IsBackupEnabled(string backupEnabledValue)
    {
        return string.Equals(backupEnabledValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backupEnabledValue, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backupEnabledValue, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTimestampedBackupFolderName()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "ConfigBackup-{0}",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    }

    private static string ReadCustomActionDataValue(CustomActionData customActionData, string key)
    {
        return customActionData.ContainsKey(key) ? customActionData[key] : string.Empty;
    }

    private static ActionResult FailWithUserMessage(Session session, string logMessage, string userMessage)
    {
        session.Log("ERROR: {0}", logMessage);
        session.Log("USER_MESSAGE: {0}", userMessage);

        try
        {
            using (var record = new Record(1))
            {
                record[1] = userMessage;
                session.Message(InstallMessage.Error, record);
            }
        }
        catch
        {
            // Avoid masking the root cause if UI message dispatch is unavailable in silent/basic UI modes.
        }

        return ActionResult.Failure;
    }

    private static bool IsNullOrWhiteSpace(string value)
    {
        if (value == null)
        {
            return true;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsWhiteSpace(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        return IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool HasConfigSuffix(string configPath)
    {
        var normalizedPath = NormalizePath(configPath);
        var configDirectoryName = Path.GetFileName(normalizedPath);
        return configDirectoryName.Equals("Config", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDofRootFromConfigPath(string configPath)
    {
        var normalizedPath = NormalizePath(configPath);
        if (HasConfigSuffix(normalizedPath))
        {
            return Path.GetDirectoryName(normalizedPath) ?? @"C:\DirectOutput";
        }

        return string.Empty;
    }
}
