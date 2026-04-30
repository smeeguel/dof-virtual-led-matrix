using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using WixToolset.Dtf.WindowsInstaller;

namespace VirtualDofMatrix.CustomActions;

public static class CustomActionEntrypoints
{
    private const string DofDownloadUrl = "http://mjrnet.org/pinscape/dll-updates.html#DOF";
    private const string DefaultDofRootPath = @"C:\DirectOutput";
    private const string DefaultDofConfigPath = @"C:\DirectOutput\Config";
    private const string DefaultTemplate = "single_matrix";
    private const string InstallerSelectionRegistryPath = @"Software\VirtualDofMatrix";
    private const string InstallerSelectionRegistryValueDofRootPath = "DofRootPath";
    private const string InstallerSelectionRegistryValueDofConfigPath = "DofConfigPath";
    // Keep supported installer template IDs centralized so UI, validation, and deferred apply logic remain consistent.
    private static readonly IDictionary<string, string> TemplateToFolderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "single_matrix", "01-single-matrix" },
        { "matrix_plus_3_strips", "02-matrix-and-flasher-strips" },
    };

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCom { }

    [CustomAction]
    public static ActionResult SelectInstallFolder(Session session)
    {
        try
        {
            var currentInstallFolder = NormalizePath(session["INSTALLFOLDER"]);
            var selectedPath = TryShowModernFolderPicker(currentInstallFolder);
            if (!IsNullOrWhiteSpace(selectedPath))
            {
                session["INSTALLFOLDER"] = EnsureTrailingBackslash(selectedPath);
            }

            session.Log("SelectInstallFolder result INSTALLFOLDER='{0}'.", session["INSTALLFOLDER"]);
            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            session.Log("SelectInstallFolder failed: {0}", ex);
            return ActionResult.Success;
        }
    }

    [CustomAction]
    public static ActionResult DetectDofInstall(Session session)
    {
        try
        {
            session.Log("DetectDofInstall stage: starting detection.");

            // Check default location first so common installs advance without extra prompts.
            var selectedRootPath = string.Empty;
            if (IsValidDofRoot(DefaultDofRootPath))
            {
                selectedRootPath = DefaultDofRootPath;
                session.Log("DetectDofInstall stage: default hit at '{0}'.", selectedRootPath);
            }
            else
            {
                // Fallback: support custom user-selected DOF roots from the browse flow.
                var explicitRootPath = NormalizePath(session["DOFROOTPATH"]);
                if (IsValidDofRoot(explicitRootPath))
                {
                    selectedRootPath = explicitRootPath;
                    session.Log("DetectDofInstall stage: explicit hit at '{0}'.", selectedRootPath);
                }
                else
                {
                    // Last resort: derive likely roots from uninstall metadata before optional filesystem probing.
                    selectedRootPath = DetectDofRootFromRegistryAndFilesystem(session);
                    if (!IsNullOrWhiteSpace(selectedRootPath))
                    {
                        session.Log("DetectDofInstall stage: selected root '{0}'.", selectedRootPath);
                    }
                }
            }

            if (!IsNullOrWhiteSpace(selectedRootPath))
            {
                var selectedConfigPath = Path.Combine(selectedRootPath, "Config");
                session["DOFROOTPATH"] = selectedRootPath;
                session["DOFCONFIGPATH"] = selectedConfigPath;
                session["DOF_DETECTED"] = "1";

                if (IsNullOrWhiteSpace(session["BACKUP_PATH"]))
                {
                    var stampedName = BuildTimestampedBackupFolderName();
                    session["BACKUP_PATH"] = Path.Combine(Path.Combine(selectedRootPath, "Backups"), stampedName);
                }
            }
            else
            {
                // Keep installer-bound path properties populated so PathEdit controls never receive empty values.
                var fallbackRootPath = NormalizePath(session["DOFROOTPATH"]);
                var fallbackConfigPath = NormalizePath(session["DOFCONFIGPATH"]);
                EnsureDetectPropertiesHaveSafeDefaults(session, ref fallbackRootPath, ref fallbackConfigPath);

                session["DOFROOTPATH"] = fallbackRootPath;
                session["DOFCONFIGPATH"] = fallbackConfigPath;
                session["DOF_DETECTED"] = "0";
                session.Log("DetectDofInstall miss path: DOFROOTPATH='{0}', DOFCONFIGPATH='{1}', DOF_DETECTED='{2}'.",
                    session["DOFROOTPATH"], session["DOFCONFIGPATH"], session["DOF_DETECTED"]);
            }

            session["BACKUP_ENABLED"] = IsNullOrWhiteSpace(session["BACKUP_ENABLED"]) ? "1" : session["BACKUP_ENABLED"];
            session["TOY_TEMPLATE"] = IsNullOrWhiteSpace(session["TOY_TEMPLATE"]) ? DefaultTemplate : session["TOY_TEMPLATE"];

            session.Log("DetectDofInstall result DOF_DETECTED='{0}', DOFROOTPATH='{1}', DOFCONFIGPATH='{2}', BACKUP_ENABLED='{3}', TOY_TEMPLATE='{4}', BACKUP_PATH='{5}'.",
                session["DOF_DETECTED"], session["DOFROOTPATH"], session["DOFCONFIGPATH"], session["BACKUP_ENABLED"], session["TOY_TEMPLATE"], session["BACKUP_PATH"]);

            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            session.Log("DetectDofInstall failed: {0}", ex);
            return ActionResult.Failure;
        }
    }

    [CustomAction]
    public static ActionResult ShowInvalidDofSelectionMessage(Session session)
    {
        const string userMessage = "This folder does not appear to be a valid DirectOutput Framework (DOF) install. " +
            "Install DOF first (default location: C:\\DirectOutput), then choose or type a DOF root folder that contains Config plus x64 or x86. " +
            "You can edit the DOF root path on the DOF check screen. DOF installers: " +
            DofDownloadUrl + ".";

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
            // Keep the dialog flow resilient in reduced/silent UI modes.
        }

        session.Log("ShowInvalidDofSelectionMessage displayed guidance for invalid DOF root.");
        return ActionResult.Success;
    }

    [CustomAction]
    public static ActionResult ValidateInstallerSelections(Session session)
    {
        try
        {
            var requestedPath = NormalizePath(session["DOFCONFIGPATH"]);
            var dofRootPath = NormalizePath(session["DOFROOTPATH"]);

            if (IsNullOrWhiteSpace(requestedPath))
            {
                return FailWithUserMessage(session, "DOF config path is required.",
                    BuildDofMissingMessage("DOF must be installed before continuing."));
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
                    BuildDofMissingMessage("Installer could not derive the DirectOutput root folder from the selected Config path."));
            }

            if (!IsNullOrWhiteSpace(dofRootPath) &&
                !dofRoot.Equals(dofRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return FailWithUserMessage(session, "DOFROOTPATH and DOFCONFIGPATH are inconsistent.",
                    BuildDofMissingMessage("The selected DirectOutput root and config folders do not match."));
            }

            // Validation for install should accept a minimal root (Config exists) because the installer itself
            // creates/updates x64 and x86 payload folders later in ApplyDofTemplateAndBinaries.
            if (!IsValidDofRootForInstall(dofRoot))
            {
                return FailWithUserMessage(session, "Detected DOF root is invalid or incomplete for installation.",
                    BuildDofMissingMessage("DirectOutput must include a Config folder at the selected root path."));
            }

            session["DOFCONFIGPATH"] = requestedPath;
            session["DOFROOTPATH"] = dofRoot;
            session["DOF_DETECTED"] = "1";

            if (IsNullOrWhiteSpace(session["TOY_TEMPLATE"]))
            {
                session["TOY_TEMPLATE"] = DefaultTemplate;
            }
            else
            {
                session["TOY_TEMPLATE"] = NormalizeTemplate(session["TOY_TEMPLATE"]);
            }

            if (!TryResolveTemplateDirectoryName(session["TOY_TEMPLATE"], out _))
            {
                var supportedIds = BuildSupportedTemplateListForMessage();
                return FailWithUserMessage(
                    session,
                    string.Format(CultureInfo.InvariantCulture, "Unsupported TOY_TEMPLATE value '{0}'.", session["TOY_TEMPLATE"]),
                    string.Format(CultureInfo.InvariantCulture,
                        "Template ID '{0}' is not supported. Choose one of: {1}.",
                        session["TOY_TEMPLATE"],
                        supportedIds));
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

            if (!TryResolveTemplateDirectoryName(toyTemplate, out var templateDirectoryName))
            {
                return FailWithUserMessage(
                    session,
                    string.Format(CultureInfo.InvariantCulture, "Unsupported TOY_TEMPLATE value '{0}'.", toyTemplate),
                    string.Format(CultureInfo.InvariantCulture,
                        "Template ID '{0}' is not supported. Choose one of: {1}.",
                        toyTemplate,
                        BuildSupportedTemplateListForMessage()));
            }

            var sourceDofRoot = ResolveDofPayloadRoot(installFolder, session);
            var baselineConfigSourceFolder = Path.Combine(sourceDofRoot, "Config");
            var templateSourceFolder = Path.Combine(sourceDofRoot, Path.Combine("Config\\templates", templateDirectoryName));

            session.Log("Resolved template source folder: '{0}'.", templateSourceFolder);
            session.Log("Resolved baseline config source folder: '{0}'.", baselineConfigSourceFolder);
            session.Log("Resolved DOF payload source root: '{0}'.", sourceDofRoot);

            if (!Directory.Exists(templateSourceFolder))
            {
                return FailWithUserMessage(session, "Template source folder not found.",
                    string.Format(CultureInfo.InvariantCulture,
                        "Template '{0}' expects installer payload folder Config\\templates\\{1}, but it was not found. Re-run setup from a complete installer package.",
                        toyTemplate,
                        templateDirectoryName));
            }

            if (!Directory.Exists(baselineConfigSourceFolder))
            {
                return FailWithUserMessage(session, "Baseline DOF Config source folder not found.",
                    "Baseline DOF Config files were not found in the installer payload. Re-run setup from a complete installer package.");
            }

            // Backup intentionally targets the existing DOF Config destination before any installer writes,
            // so recovery includes the full pre-install state (not only files touched by one template).
            if (IsBackupEnabled(backupEnabled))
            {
                if (IsNullOrWhiteSpace(backupPath))
                {
                    backupPath = Path.Combine(Path.Combine(dofRootPath, "Backups"), BuildTimestampedBackupFolderName());
                }

                BackupConfigFolderIfPresent(dofConfigPath, backupPath, session);
            }
            else
            {
                session.Log("Skipping backup creation. Enabled='{0}'.", backupEnabled);
            }

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

            // Baseline copy lands first so template-specific files can deliberately win overwrite order afterward.
            CopyBaselineConfigFiles(baselineConfigSourceFolder, dofConfigPath, session);
            // Template copy must be last by design so selected template values are authoritative.
            CopyTemplateFiles(templateSourceFolder, dofConfigPath, session);
            PersistInstallerSelectionsToRegistry(dofRootPath, dofConfigPath, session);

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

    private static void BackupConfigFolderIfPresent(string dofConfigPath, string backupPath, Session session)
    {
        if (!Directory.Exists(dofConfigPath))
        {
            session.Log("Skipping config backup because destination path does not exist yet: '{0}'.", dofConfigPath);
            return;
        }

        // Copy the complete pre-existing Config tree so users can fully restore their DOF state.
        session.Log("Backing up existing DOF Config folder from '{0}' to '{1}'.", dofConfigPath, backupPath);
        CopyDirectoryContents(dofConfigPath, backupPath, session, "DOF config backup", includePredicate: null);
    }

    private static void CopyTemplateFiles(string templateSourceFolder, string dofConfigPath, Session session)
    {
        CopyDirectoryContents(templateSourceFolder, dofConfigPath, session, "template file",
            includePredicate: delegate(string sourcePath)
            {
                // Template payload should always copy in full to intentionally override baseline files.
                return true;
            });
    }

    private static void CopyBaselineConfigFiles(string baselineConfigSourceFolder, string dofConfigPath, Session session)
    {
        CopyDirectoryContents(baselineConfigSourceFolder, dofConfigPath, session, "baseline config file",
            includePredicate: delegate(string sourcePath)
            {
                // Baseline copy must not include template source payload folders.
                var relativePath = sourcePath.Substring(baselineConfigSourceFolder.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var pathParts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                return pathParts.Length == 0 || !pathParts[0].Equals("templates", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static void CopyDirectoryContents(
        string sourceFolder,
        string destinationFolder,
        Session session,
        string copyLabel,
        Predicate<string> includePredicate)
    {
        var files = Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories);
        var copiedCount = 0;
        session.Log("Evaluating {0} file(s) from '{1}' to '{2}' for copy label '{3}'.",
            files.Length, sourceFolder, destinationFolder, copyLabel);

        for (var i = 0; i < files.Length; i++)
        {
            var sourceFile = files[i];
            if (includePredicate != null && !includePredicate(sourceFile))
            {
                session.Log("Skipped {0} source '{1}' due to include filter.", copyLabel, sourceFile);
                continue;
            }

            var relativePath = sourceFile.Substring(sourceFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destinationFile = Path.Combine(destinationFolder, relativePath);
            var destinationFileFolder = Path.GetDirectoryName(destinationFile);

            if (!IsNullOrWhiteSpace(destinationFileFolder))
            {
                EnsureDirectoryExists(destinationFileFolder, session, "destination folder for " + copyLabel);
            }

            File.Copy(sourceFile, destinationFile, true);
            copiedCount++;
            session.Log("Copied {0} from '{1}' to '{2}'.", copyLabel, sourceFile, destinationFile);
        }

        session.Log("Completed copy label '{0}' with {1} copied file(s).", copyLabel, copiedCount);
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

        var attempt = 0;
        while (true)
        {
            attempt++;

            try
            {
                File.Copy(sourceFile, destinationFile, true);
                session.Log("Copied {0}: '{1}' -> '{2}' on attempt {3}.", description, sourceFile, destinationFile, attempt);
                return;
            }
            catch (IOException ex)
            {
                if (!IsSharingOrLockViolation(ex))
                {
                    throw;
                }

                session.Log("{0} copy hit a file lock on attempt {1}: {2}", description, attempt, ex.Message);

                // Keep installer open and let the user close locking apps (for example Visual Pinball), then retry.
                if (!PromptRetryForLockedFile(session, description, destinationFile))
                {
                    throw new IOException(string.Format(CultureInfo.InvariantCulture,
                        "{0} copy was canceled by user after file-lock prompt for '{1}'.", description, destinationFile), ex);
                }
            }
        }
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

    private static bool PromptRetryForLockedFile(Session session, string description, string destinationFile)
    {
        try
        {
            using (var record = new Record(1))
            {
                record[1] = string.Format(CultureInfo.InvariantCulture,
                    "{0} is in use: {1}\n\nClose apps that may be using DirectOutput (for example Visual Pinball), then click Retry. Click Cancel to abort setup.",
                    description,
                    destinationFile);

                // Compose an MSI error message with Retry/Cancel buttons so the installer can continue in-place.
                var retryCancelErrorMessage = (InstallMessage)((int)InstallMessage.Error | 0x00000005);
                var result = session.Message(retryCancelErrorMessage, record);
                session.Log("File-lock prompt result for '{0}': {1}.", destinationFile, result);
                return result == MessageResult.Retry;
            }
        }
        catch (Exception ex)
        {
            session.Log("PromptRetryForLockedFile failed, defaulting to cancel: {0}", ex);
            return false;
        }
    }

    private static bool IsSharingOrLockViolation(IOException exception)
    {
        var hResult = Marshal.GetHRForException(exception);
        var win32ErrorCode = hResult & 0xFFFF;
        return win32ErrorCode == 32 || win32ErrorCode == 33;
    }

    private static bool TryResolveTemplateDirectoryName(string templateValue, out string templateDirectoryName)
    {
        if (IsNullOrWhiteSpace(templateValue))
        {
            templateDirectoryName = string.Empty;
            return false;
        }

        return TemplateToFolderMap.TryGetValue(templateValue, out templateDirectoryName);
    }

    private static string BuildSupportedTemplateListForMessage()
    {
        var supportedTemplates = new List<string>(TemplateToFolderMap.Keys);
        supportedTemplates.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", supportedTemplates.ToArray());
    }

    private static string NormalizeTemplate(string templateValue)
    {
        var normalizedTemplate = IsNullOrWhiteSpace(templateValue) ? DefaultTemplate : templateValue.Trim();
        // Normalize to canonical ID casing so persisted values are deterministic across UI/deferred flows.
        if (TryResolveTemplateDirectoryName(normalizedTemplate, out _))
        {
            if (normalizedTemplate.Equals("matrix_plus_3_strips", StringComparison.OrdinalIgnoreCase))
            {
                return "matrix_plus_3_strips";
            }

            if (normalizedTemplate.Equals("single_matrix", StringComparison.OrdinalIgnoreCase))
            {
                return "single_matrix";
            }
        }

        return normalizedTemplate;
    }

    private static bool IsBackupEnabled(string backupEnabledValue)
    {
        return string.Equals(backupEnabledValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backupEnabledValue, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(backupEnabledValue, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static void PersistInstallerSelectionsToRegistry(string dofRootPath, string dofConfigPath, Session session)
    {
        if (IsNullOrWhiteSpace(dofConfigPath))
        {
            session.Log("Skipping installer selection registry write because DOFCONFIGPATH is empty.");
            return;
        }

        // Persist validated DOF selections into HKLM so first app launch can bootstrap without an extra file artifact.
        using var key = Registry.LocalMachine.CreateSubKey(InstallerSelectionRegistryPath);
        if (key is null)
        {
            session.Log("Skipping installer selection registry write because key '{0}' could not be created.", InstallerSelectionRegistryPath);
            return;
        }

        key.SetValue(InstallerSelectionRegistryValueDofConfigPath, dofConfigPath, RegistryValueKind.String);
        key.SetValue(InstallerSelectionRegistryValueDofRootPath, IsNullOrWhiteSpace(dofRootPath) ? string.Empty : dofRootPath, RegistryValueKind.String);
        key.SetValue("GeneratedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        session.Log(
            "Persisted installer selection registry hint at 'HKLM\\{0}' with {1}='{2}'.",
            InstallerSelectionRegistryPath,
            InstallerSelectionRegistryValueDofConfigPath,
            dofConfigPath);
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
            return Path.GetDirectoryName(normalizedPath) ?? DefaultDofRootPath;
        }

        return string.Empty;
    }

    private static bool IsValidDofRoot(string rootPath)
    {
        var normalizedRootPath = NormalizePath(rootPath);
        if (IsNullOrWhiteSpace(normalizedRootPath))
        {
            return false;
        }

        var configPath = Path.Combine(normalizedRootPath, "Config");
        var hasConfig = Directory.Exists(configPath);
        var hasX64 = Directory.Exists(Path.Combine(normalizedRootPath, "x64"));
        var hasX86 = Directory.Exists(Path.Combine(normalizedRootPath, "x86"));
        return hasConfig && (hasX64 || hasX86);
    }

    private static bool IsValidDofRootForDetection(string rootPath)
    {
        // Keep detection criteria aligned with installer UX text: a valid root contains Config plus x64 OR x86.
        return IsValidDofRoot(rootPath);
    }

    private static string DetectDofRootFromRegistryAndFilesystem(Session session)
    {
        var registryCandidates = EnumerateRegistryDofRootCandidates(session);
        for (var i = 0; i < registryCandidates.Count; i++)
        {
            var candidate = registryCandidates[i];
            session.Log("DetectDofInstall stage: registry candidate '{0}'.", candidate);
            if (IsValidDofRootForDetection(candidate))
            {
                session.Log("DetectDofInstall stage: registry candidate accepted '{0}'.", candidate);
                return candidate;
            }
        }

        // Final fallback is intentionally narrow (Program Files roots) to stay deterministic and inexpensive.
        var filesystemCandidates = BuildFilesystemDofCandidates();
        for (var i = 0; i < filesystemCandidates.Count; i++)
        {
            var candidate = filesystemCandidates[i];
            session.Log("DetectDofInstall stage: filesystem candidate '{0}'.", candidate);
            if (IsValidDofRootForDetection(candidate))
            {
                session.Log("DetectDofInstall stage: filesystem candidate accepted '{0}'.", candidate);
                return candidate;
            }
        }

        return string.Empty;
    }

    private static IList<string> EnumerateRegistryDofRootCandidates(Session session)
    {
        var uninstallBasePaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        var candidates = new List<string>();
        var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Probe both machine-wide and per-user uninstall hives because DOF installers can register in either.
        EnumerateRegistryDofRootCandidatesFromHive(
            Registry.LocalMachine,
            "HKLM",
            uninstallBasePaths,
            candidates,
            seen,
            session);

        EnumerateRegistryDofRootCandidatesFromHive(
            Registry.CurrentUser,
            "HKCU",
            uninstallBasePaths,
            candidates,
            seen,
            session);

        session.Log("DetectDofInstall stage: collected {0} unique registry candidate(s).", candidates.Count);
        return candidates;
    }

    private static void EnumerateRegistryDofRootCandidatesFromHive(
        RegistryKey hiveRoot,
        string hiveLabel,
        string[] uninstallBasePaths,
        ICollection<string> candidates,
        IDictionary<string, bool> seen,
        Session session)
    {
        for (var i = 0; i < uninstallBasePaths.Length; i++)
        {
            var uninstallBasePath = uninstallBasePaths[i];
            using (var uninstallRoot = hiveRoot.OpenSubKey(uninstallBasePath))
            {
                if (uninstallRoot == null)
                {
                    session.Log("DetectDofInstall stage: uninstall hive path missing '{0}\\{1}'.", hiveLabel, uninstallBasePath);
                    continue;
                }

                var subKeyNames = uninstallRoot.GetSubKeyNames();
                for (var subKeyIndex = 0; subKeyIndex < subKeyNames.Length; subKeyIndex++)
                {
                    var subKeyName = subKeyNames[subKeyIndex];
                    using (var appKey = uninstallRoot.OpenSubKey(subKeyName))
                    {
                        if (appKey == null)
                        {
                            continue;
                        }

                        var displayName = Convert.ToString(appKey.GetValue("DisplayName"), CultureInfo.InvariantCulture) ?? string.Empty;
                        if (!LooksLikeDirectOutputDisplayName(displayName))
                        {
                            continue;
                        }

                        var installLocation = Convert.ToString(appKey.GetValue("InstallLocation"), CultureInfo.InvariantCulture);
                        AddCandidatePath(candidates, seen, installLocation);

                        var displayIcon = Convert.ToString(appKey.GetValue("DisplayIcon"), CultureInfo.InvariantCulture);
                        var iconDerivedRoot = DeriveRootFromDisplayIcon(displayIcon);
                        AddCandidatePath(candidates, seen, iconDerivedRoot);
                    }
                }
            }
        }
    }

    private static IList<string> BuildFilesystemDofCandidates()
    {
        var candidates = new List<string>();
        var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Probe fixed-drive roots first so non-default installs like D:\DirectOutput are discoverable
        // even when uninstall metadata omits InstallLocation/DisplayIcon paths.
        var logicalDrives = Environment.GetLogicalDrives();
        for (var i = 0; i < logicalDrives.Length; i++)
        {
            var driveRoot = logicalDrives[i];
            try
            {
                var driveInfo = new DriveInfo(driveRoot);
                if (driveInfo.DriveType != DriveType.Fixed || !driveInfo.IsReady)
                {
                    continue;
                }

                AddCandidatePath(candidates, seen, Path.Combine(driveInfo.RootDirectory.FullName, "DirectOutput"));
            }
            catch
            {
                // Ignore inaccessible/transitioning drives; candidate discovery should stay best-effort.
            }
        }

        var programFiles = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (!IsNullOrWhiteSpace(programFiles))
        {
            AddCandidatePath(candidates, seen, Path.Combine(programFiles, "DirectOutput"));
        }

        var programFilesX86 = NormalizePath(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
        if (IsNullOrWhiteSpace(programFilesX86))
        {
            // 32-bit operating systems do not expose ProgramFiles(x86); fallback keeps candidate list stable.
            programFilesX86 = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        }
        if (!IsNullOrWhiteSpace(programFilesX86))
        {
            AddCandidatePath(candidates, seen, Path.Combine(programFilesX86, "DirectOutput"));
        }

        return candidates;
    }

    private static bool LooksLikeDirectOutputDisplayName(string displayName)
    {
        if (IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        return displayName.IndexOf("DirectOutput", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string DeriveRootFromDisplayIcon(string displayIcon)
    {
        var normalizedDisplayIcon = NormalizePath(displayIcon);
        if (IsNullOrWhiteSpace(normalizedDisplayIcon))
        {
            return string.Empty;
        }

        // Registry values often append ",0" for icon index; remove it before evaluating filesystem paths.
        var commaIndex = normalizedDisplayIcon.IndexOf(',');
        if (commaIndex >= 0)
        {
            normalizedDisplayIcon = normalizedDisplayIcon.Substring(0, commaIndex);
        }

        var quotedValue = normalizedDisplayIcon.Trim();
        if (quotedValue.Length >= 2 && quotedValue[0] == '"' && quotedValue[quotedValue.Length - 1] == '"')
        {
            quotedValue = quotedValue.Substring(1, quotedValue.Length - 2);
        }

        var normalizedValue = NormalizePath(quotedValue);
        if (IsNullOrWhiteSpace(normalizedValue))
        {
            return string.Empty;
        }

        if (Directory.Exists(normalizedValue))
        {
            return normalizedValue;
        }

        if (File.Exists(normalizedValue))
        {
            return NormalizePath(Path.GetDirectoryName(normalizedValue));
        }

        return NormalizePath(Path.GetDirectoryName(normalizedValue));
    }

    private static void AddCandidatePath(ICollection<string> candidates, IDictionary<string, bool> seen, string path)
    {
        var normalizedPath = NormalizePath(path);
        if (IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        if (!Path.IsPathRooted(normalizedPath))
        {
            return;
        }

        if (!seen.ContainsKey(normalizedPath))
        {
            seen[normalizedPath] = true;
            candidates.Add(normalizedPath);
        }

        var leafName = Path.GetFileName(normalizedPath);
        if (leafName.Equals("x64", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("x86", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("Config", StringComparison.OrdinalIgnoreCase))
        {
            // Some uninstall entries point to architecture/config subfolders; include parent as likely DOF root.
            var parentPath = NormalizePath(Path.GetDirectoryName(normalizedPath));
            if (!IsNullOrWhiteSpace(parentPath) && Path.IsPathRooted(parentPath) && !seen.ContainsKey(parentPath))
            {
                seen[parentPath] = true;
                candidates.Add(parentPath);
            }
        }
    }

    private static bool IsValidDofRootForInstall(string rootPath)
    {
        var normalizedRootPath = NormalizePath(rootPath);
        if (IsNullOrWhiteSpace(normalizedRootPath) || !Path.IsPathRooted(normalizedRootPath))
        {
            return false;
        }

        // Keep install-time validation compatible with CI's fake DOF roots and first-time setups where
        // architecture folders do not exist yet; ApplyDofTemplateAndBinaries creates those folders.
        var configPath = Path.Combine(normalizedRootPath, "Config");
        return Directory.Exists(configPath);
    }

    private static void EnsureDetectPropertiesHaveSafeDefaults(Session session, ref string dofRootPath, ref string dofConfigPath)
    {
        // Normalize miss-path values and repopulate with defaults so installer UI path controls always have valid text.
        dofRootPath = NormalizePath(dofRootPath);
        dofConfigPath = NormalizePath(dofConfigPath);

        if (IsNullOrWhiteSpace(dofRootPath))
        {
            dofRootPath = DefaultDofRootPath;
        }

        if (IsNullOrWhiteSpace(dofConfigPath))
        {
            dofConfigPath = Path.Combine(dofRootPath, "Config");
        }

        dofConfigPath = EnsureConfigPathEndsWithConfig(dofConfigPath);
        dofRootPath = ResolveRootFromPathPair(dofRootPath, dofConfigPath);
        dofConfigPath = EnsureConfigPathEndsWithConfig(dofConfigPath);

        session.Log("DetectDofInstall guard applied: DOFROOTPATH='{0}', DOFCONFIGPATH='{1}'.", dofRootPath, dofConfigPath);
    }

    private static string EnsureConfigPathEndsWithConfig(string configPath)
    {
        var normalizedPath = NormalizePath(configPath);
        if (IsNullOrWhiteSpace(normalizedPath))
        {
            return Path.Combine(DefaultDofRootPath, "Config");
        }

        if (HasConfigSuffix(normalizedPath))
        {
            return normalizedPath;
        }

        return Path.Combine(normalizedPath, "Config");
    }

    private static string ResolveRootFromPathPair(string dofRootPath, string dofConfigPath)
    {
        var normalizedRootPath = NormalizePath(dofRootPath);
        var normalizedConfigPath = NormalizePath(dofConfigPath);
        var resolvedRootFromConfig = ResolveDofRootFromConfigPath(normalizedConfigPath);

        if (!IsNullOrWhiteSpace(resolvedRootFromConfig))
        {
            return resolvedRootFromConfig;
        }

        return IsNullOrWhiteSpace(normalizedRootPath) ? DefaultDofRootPath : normalizedRootPath;
    }

    private static string EnsureTrailingBackslash(string path)
    {
        if (IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return path.EndsWith("\\", StringComparison.Ordinal) ? path : path + "\\";
    }

    private static string TryShowModernFolderPicker(string initialPath)
    {
        IFileOpenDialog dialog = null;
        try
        {
            // Use the Vista-style common dialog in folder-pick mode so installer browsing shows folder contents.
            dialog = (IFileOpenDialog)new FileOpenDialogCom();
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

            if (!IsNullOrWhiteSpace(initialPath))
            {
                var normalizedInitialPath = initialPath.TrimEnd('\\');
                if (Directory.Exists(normalizedInitialPath) &&
                    SHCreateItemFromParsingName(normalizedInitialPath, IntPtr.Zero, typeof(IShellItem).GUID, out var initialFolderShellItem) == 0 &&
                    initialFolderShellItem != null)
                {
                    dialog.SetFolder(initialFolderShellItem);
                }
            }

            const int ErrorCancelled = unchecked((int)0x800704C7);
            var showResult = dialog.Show(IntPtr.Zero);
            if (showResult == ErrorCancelled)
            {
                return null;
            }

            if (showResult != 0)
            {
                Marshal.ThrowExceptionForHR(showResult);
            }

            dialog.GetResult(out var selectedItem);
            selectedItem.GetDisplayName(SIGDN_FILESYSPATH, out var selectedPath);
            return selectedPath;
        }
        finally
        {
            if (dialog != null)
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, uint fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    private interface IFileOpenDialog : IFileDialog
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    private static string BuildDofMissingMessage(string leadIn)
    {
        return leadIn + " Virtual DOF Matrix requires DirectOutput Framework (DOF). DOF is usually installed at C:\\DirectOutput, " +
            "but can be installed elsewhere. Install both the 32-bit and 64-bit DOF installers first: " + DofDownloadUrl;
    }
}
