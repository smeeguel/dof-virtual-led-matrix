using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VirtualDofMatrix.App.Configuration;

// Note: this service owns the lightweight, idempotent GlobalConfig bootstrap flow so end users
// never need to manually copy GlobalConfig_B2SServer.xml just to register directoutputconfig30.ini.
public sealed class GlobalConfigBootstrapService
{
    public const string GlobalConfigFileName = "GlobalConfig_B2SServer.xml";
    public const string RequiredTableConfigPattern = @"{InstallDir}\Config\directoutputconfig30.ini";

    public GlobalConfigBootstrapResult EnsureRequiredTableConfigPattern(string configFolderPath)
    {
        if (string.IsNullOrWhiteSpace(configFolderPath))
        {
            return GlobalConfigBootstrapResult.Failed("DOF config folder path is empty.");
        }

        var globalConfigPath = Path.Combine(configFolderPath, GlobalConfigFileName);
        try
        {
            if (!File.Exists(globalConfigPath))
            {
                var canonicalDocument = CreateCanonicalGlobalConfigDocument();
                SaveDocument(globalConfigPath, canonicalDocument);
                return GlobalConfigBootstrapResult.Created(globalConfigPath);
            }

            var document = XDocument.Load(globalConfigPath, LoadOptions.PreserveWhitespace);
            if (document.Root is null || !string.Equals(document.Root.Name.LocalName, "GlobalConfig", StringComparison.Ordinal))
            {
                return GlobalConfigBootstrapResult.Failed(
                    $"Existing {GlobalConfigFileName} root node is invalid; expected <GlobalConfig>.");
            }

            var patternsNode = document.Root.Element("TableConfigFilePatterns");
            if (patternsNode is null)
            {
                patternsNode = new XElement("TableConfigFilePatterns");
                document.Root.Add(patternsNode);
            }

            var hasRequiredPattern = patternsNode.Elements("TableConfigFilePattern")
                .Any(element => string.Equals(element.Value.Trim(), RequiredTableConfigPattern, StringComparison.OrdinalIgnoreCase));
            if (hasRequiredPattern)
            {
                return GlobalConfigBootstrapResult.Unchanged(globalConfigPath);
            }

            patternsNode.Add(new XElement("TableConfigFilePattern", RequiredTableConfigPattern));
            CreateBackup(globalConfigPath);
            SaveDocument(globalConfigPath, document);
            return GlobalConfigBootstrapResult.Merged(globalConfigPath);
        }
        catch (Exception ex)
        {
            return GlobalConfigBootstrapResult.Failed(
                $"Unable to update {GlobalConfigFileName} in '{configFolderPath}': {ex.Message}");
        }
    }

    private static void SaveDocument(string filePath, XDocument document)
    {
        // Note: use indented UTF-8 output so created/merged config files stay readable in Notepad/Notepad++.
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            NewLineChars = Environment.NewLine,
            NewLineHandling = NewLineHandling.Replace,
        };

        using var writer = XmlWriter.Create(filePath, settings);
        document.Save(writer);
    }

    private static void CreateBackup(string filePath)
    {
        // Note: timestamped backups keep this startup merge path beginner-safe and easy to roll back manually.
        var backupPath = $"{filePath}.bak.{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}";
        File.Copy(filePath, backupPath, overwrite: false);
    }

    private static XDocument CreateCanonicalGlobalConfigDocument()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("GlobalConfig",
                new XElement("LedWizDefaultMinCommandIntervalMs", "1"),
                new XElement("LedControlMinimumEffectDurationMs", "60"),
                new XElement("LedControlMinimumRGBEffectDurationMs", "120"),
                new XElement("IniFilesPath", @"{InstallDir}\Config"),
                new XElement("EnableLogging", "true"),
                new XElement("ClearLogOnSessionStart", "true"),
                new XElement("LogFilePattern", @"{InstallDir}\Log\DirectOutput.log"),
                new XElement("CabinetConfigFilePattern", @"{InstallDir}\Config\Cabinet.xml"),
                new XElement("TableConfigFilePatterns",
                    new XElement("TableConfigFilePattern", RequiredTableConfigPattern))));
    }
}

public enum GlobalConfigBootstrapStatus
{
    Created,
    Merged,
    Unchanged,
    Failed,
}

public sealed record GlobalConfigBootstrapResult(GlobalConfigBootstrapStatus Status, string Details)
{
    public static GlobalConfigBootstrapResult Created(string path) =>
        new(GlobalConfigBootstrapStatus.Created, $"Created {GlobalConfigBootstrapService.GlobalConfigFileName} at '{path}'.");

    public static GlobalConfigBootstrapResult Merged(string path) =>
        new(GlobalConfigBootstrapStatus.Merged, $"Merged required table config pattern into '{path}'.");

    public static GlobalConfigBootstrapResult Unchanged(string path) =>
        new(GlobalConfigBootstrapStatus.Unchanged, $"{GlobalConfigBootstrapService.GlobalConfigFileName} already contained required table config pattern in '{path}'.");

    public static GlobalConfigBootstrapResult Failed(string details) =>
        new(GlobalConfigBootstrapStatus.Failed, details);
}
