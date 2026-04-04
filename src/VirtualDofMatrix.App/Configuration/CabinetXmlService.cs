using System.Xml.Linq;
using System.IO;

namespace VirtualDofMatrix.App.Configuration;

// Overview: CabinetXmlService keeps Cabinet.xml discovery and toy resolution edits in one place for safer automation.
public sealed class CabinetXmlService
{
    public const int SafeMaxLedTotal = 8800;

    public string? ResolveCabinetXmlPath(string? savedPath)
    {
        if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
        {
            return savedPath;
        }

        const string fallback = @"C:\DirectOutput\Config\Cabinet.xml";
        return File.Exists(fallback) ? fallback : null;
    }

    public IReadOnlyList<string> GetLedStripToyNames(string cabinetXmlPath)
    {
        var document = XDocument.Load(cabinetXmlPath);
        var ledStrips = document.Descendants().Where(x => x.Name.LocalName == "LedStrip");
        return ledStrips
            .Select(x => x.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void UpdateLedStripResolution(string cabinetXmlPath, string toyName, int width, int height)
    {
        // We validate aggressively before touching XML so accidental bad UI values cannot corrupt cabinet config.
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Width and height must be positive values.");
        }

        var totalLeds = width * height;
        if (totalLeds > SafeMaxLedTotal)
        {
            throw new InvalidOperationException($"Resolution exceeds safe LED total ({SafeMaxLedTotal}).");
        }

        var document = XDocument.Load(cabinetXmlPath);
        var ledStrip = document
            .Descendants()
            .Where(x => x.Name.LocalName == "LedStrip")
            .FirstOrDefault(x => string.Equals(
                x.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value,
                toyName,
                StringComparison.OrdinalIgnoreCase));

        if (ledStrip is null)
        {
            throw new InvalidOperationException($"Could not find LedStrip toy '{toyName}'.");
        }

        SetOrCreateChildValue(ledStrip, "Width", width.ToString());
        SetOrCreateChildValue(ledStrip, "Height", height.ToString());
        SyncControllerLedCount(document, ledStrip, totalLeds);

        var backupPath = $"{cabinetXmlPath}.bak.{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Copy(cabinetXmlPath, backupPath, overwrite: false);

        var tempPath = Path.Combine(Path.GetDirectoryName(cabinetXmlPath)!, $"{Path.GetFileName(cabinetXmlPath)}.tmp");
        document.Save(tempPath);
        File.Move(tempPath, cabinetXmlPath, overwrite: true);
    }

    private static void SetOrCreateChildValue(XElement parent, string elementName, string value)
    {
        var child = parent.Elements().FirstOrDefault(x => x.Name.LocalName == elementName);
        if (child is null)
        {
            parent.Add(new XElement(parent.GetDefaultNamespace() + elementName, value));
            return;
        }

        child.Value = value;
    }

    private static void SyncControllerLedCount(XDocument document, XElement ledStrip, int totalLeds)
    {
        var outputControllerName = ledStrip.Elements().FirstOrDefault(x => x.Name.LocalName == "OutputControllerName")?.Value;
        if (string.IsNullOrWhiteSpace(outputControllerName))
        {
            return;
        }

        var controller = document
            .Descendants()
            .FirstOrDefault(x =>
            {
                if (!x.Name.LocalName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var name = x.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                return string.Equals(name, outputControllerName, StringComparison.OrdinalIgnoreCase);
            });

        if (controller is null)
        {
            return;
        }

        SetOrCreateChildValue(controller, "NumberOfLedsStrip1", totalLeds.ToString());
    }
}
