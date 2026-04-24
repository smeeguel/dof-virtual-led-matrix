using System.Globalization;
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
            var explicitPath = session["DOFCONFIGPATH"]?.Trim();
            var selectedPath = string.IsNullOrWhiteSpace(explicitPath) ? DefaultDofConfigPath : explicitPath;

            if (Directory.Exists(DefaultDofConfigPath))
            {
                selectedPath = DefaultDofConfigPath;
            }

            session["DOFCONFIGPATH"] = selectedPath;
            session["BACKUP_ENABLED"] = string.IsNullOrWhiteSpace(session["BACKUP_ENABLED"]) ? "1" : session["BACKUP_ENABLED"];
            session["TOY_TEMPLATE"] = string.IsNullOrWhiteSpace(session["TOY_TEMPLATE"]) ? DefaultTemplate : session["TOY_TEMPLATE"];

            // Compute a backup target under the inferred DOF root unless the user already supplied an explicit location.
            if (string.IsNullOrWhiteSpace(session["BACKUP_PATH"]))
            {
                var dofRoot = ResolveDofRootFromConfigPath(selectedPath ?? DefaultDofConfigPath);
                var stampedName = $"ConfigBackup-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
                session["BACKUP_PATH"] = Path.Combine(dofRoot, "Backups", stampedName);
            }

            session.Log("DetectDofDefaults selected DOFCONFIGPATH='{0}', BACKUP_ENABLED='{1}', TOY_TEMPLATE='{2}'.",
                session["DOFCONFIGPATH"], session["BACKUP_ENABLED"], session["TOY_TEMPLATE"]);

            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            session.Log("DetectDofDefaults failed: {0}", ex);
            return ActionResult.Failure;
        }
    }

    [CustomAction]
    public static ActionResult PersistInstallerSelections(Session session)
    {
        try
        {
            // Deferred actions can only read CustomActionData; be defensive so missing keys don't crash the installer.
            var customActionData = session.CustomActionData;
            var dofConfigPath = ReadCustomActionDataValue(customActionData, "DOFCONFIGPATH");
            var backupEnabled = ReadCustomActionDataValue(customActionData, "BACKUP_ENABLED");
            var backupPath = ReadCustomActionDataValue(customActionData, "BACKUP_PATH");
            var toyTemplate = ReadCustomActionDataValue(customActionData, "TOY_TEMPLATE");

            session.Log("PersistInstallerSelections DOFCONFIGPATH='{0}', BACKUP_ENABLED='{1}', BACKUP_PATH='{2}', TOY_TEMPLATE='{3}'.",
                dofConfigPath, backupEnabled, backupPath, toyTemplate);

            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            session.Log("PersistInstallerSelections failed: {0}", ex);
            return ActionResult.Failure;
        }
    }

    private static string ReadCustomActionDataValue(CustomActionData customActionData, string key) =>
        customActionData.ContainsKey(key) ? customActionData[key] : string.Empty;

    private static string ResolveDofRootFromConfigPath(string configPath)
    {
        var normalizedPath = (configPath ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var configDirectoryName = Path.GetFileName(normalizedPath);

        if (configDirectoryName.Equals("Config", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(normalizedPath) ?? @"C:\DirectOutput";
        }

        return @"C:\DirectOutput";
    }
}
