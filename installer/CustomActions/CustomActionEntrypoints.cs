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

    [CustomAction]
    public static ActionResult PersistInstallerSelections(Session session)
    {
        // Deferred actions can only read CustomActionData, so parse the staged key/value list for clear logging.
        var customActionData = session.CustomActionData;
        var dofConfigPath = customActionData["DOFCONFIGPATH"];
        var backupEnabled = customActionData["BACKUP_ENABLED"];
        var backupPath = customActionData["BACKUP_PATH"];
        var toyTemplate = customActionData["TOY_TEMPLATE"];

        session.Log("PersistInstallerSelections DOFCONFIGPATH='{0}', BACKUP_ENABLED='{1}', BACKUP_PATH='{2}', TOY_TEMPLATE='{3}'.",
            dofConfigPath, backupEnabled, backupPath, toyTemplate);

        return ActionResult.Success;
    }

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
