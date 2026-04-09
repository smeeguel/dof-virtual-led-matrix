using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

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
        var document = LoadCabinetDocument(cabinetXmlPath);
        var ledStrips = document.Descendants().Where(x => x.Name.LocalName == "LedStrip");
        return ledStrips
            .Select(x => x.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Conversational note: this inventory parser gives the UI a single, read-only view of toys grouped by
    // virtual-vs-hardware controller backing without forcing callers to understand Cabinet.xml internals.
    public CabinetToyInventory GetToyInventory(string cabinetXmlPath)
    {
        var document = LoadCabinetDocument(cabinetXmlPath);

        var controllerKindsByName = GetControllerKindsByName(document);

        var toysRoot = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Toys");
        if (toysRoot is null)
        {
            return new CabinetToyInventory([], []);
        }

        var toyEntries = toysRoot
            .Elements()
            .Select(x =>
            {
                var toyName = x.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                var outputController = x.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputControllerName")?.Value;
                var controllerKind = !string.IsNullOrWhiteSpace(outputController) && controllerKindsByName.TryGetValue(outputController, out var value)
                    ? value
                    : string.Empty;

                var isVirtual = controllerKind.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

                return new CabinetToyEntry(
                    Name: string.IsNullOrWhiteSpace(toyName) ? "(unnamed)" : toyName!,
                    Kind: x.Name.LocalName,
                    ControllerName: outputController ?? string.Empty,
                    ControllerKind: controllerKind,
                    IsVirtual: isVirtual);
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var virtualToys = toyEntries.Where(x => x.IsVirtual).ToArray();
        var hardwareToys = toyEntries.Where(x => !x.IsVirtual).ToArray();

        return new CabinetToyInventory(virtualToys, hardwareToys);
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

        var mergePlan = BuildVirtualToyMergePlan(cabinetXmlPath, [new VirtualLedToyDefinition(toyName, width, height)], removeMissingManagedToys: false);
        if (!mergePlan.PlannedChanges.Any())
        {
            return;
        }

        ApplyVirtualToyMerge(cabinetXmlPath, mergePlan, dryRun: false);
    }

    // Conversational note: this planner computes an explicit patch set first so callers can dry-run and review
    // every virtual-toy edit before we touch Cabinet.xml on disk.
    public CabinetXmlMergePlan BuildVirtualToyMergePlan(
        string cabinetXmlPath,
        IReadOnlyCollection<VirtualLedToyDefinition> desiredVirtualToys,
        bool removeMissingManagedToys)
    {
        var document = LoadCabinetDocument(cabinetXmlPath);
        var controllerKindsByName = GetControllerKindsByName(document);
        var toysRoot = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Toys");
        if (toysRoot is null)
        {
            throw new InvalidOperationException("Cabinet.xml is missing the <Toys> section.");
        }

        ValidateDesiredVirtualToys(desiredVirtualToys);

        var existingManaged = toysRoot
            .Elements()
            .Where(x => x.Name.LocalName == "LedStrip")
            .Select(x => new
            {
                Element = x,
                Name = GetChildValue(x, "Name"),
                OutputControllerName = GetChildValue(x, "OutputControllerName"),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.OutputControllerName))
            .Where(x => controllerKindsByName.TryGetValue(x.OutputControllerName!, out var kind)
                && kind.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                x => x.Name!,
                x => x.Element,
                StringComparer.OrdinalIgnoreCase);

        var desiredByName = desiredVirtualToys
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase);

        var changes = new List<CabinetXmlMergeChange>();

        foreach (var desired in desiredByName.Values)
        {
            if (!existingManaged.TryGetValue(desired.Name, out var current))
            {
                changes.Add(new CabinetXmlMergeChange(
                    ChangeType: CabinetXmlMergeChangeType.Added,
                    ToyName: desired.Name,
                    Field: "LedStrip",
                    OldValue: null,
                    NewValue: $"Width={desired.Width}, Height={desired.Height}, Controller={desired.OutputControllerName}"));
                continue;
            }

            var widthText = GetChildValue(current, "Width");
            var heightText = GetChildValue(current, "Height");
            var controllerText = GetChildValue(current, "OutputControllerName");
            var oldWidth = ParseIntOrNull(widthText);
            var oldHeight = ParseIntOrNull(heightText);

            if (oldWidth != desired.Width)
            {
                changes.Add(new CabinetXmlMergeChange(CabinetXmlMergeChangeType.Updated, desired.Name, "Width", widthText, desired.Width.ToString()));
            }

            if (oldHeight != desired.Height)
            {
                changes.Add(new CabinetXmlMergeChange(CabinetXmlMergeChangeType.Updated, desired.Name, "Height", heightText, desired.Height.ToString()));
            }

            if (!string.Equals(controllerText, desired.OutputControllerName, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new CabinetXmlMergeChange(CabinetXmlMergeChangeType.Updated, desired.Name, "OutputControllerName", controllerText, desired.OutputControllerName));
            }
        }

        if (removeMissingManagedToys)
        {
            foreach (var existing in existingManaged.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (desiredByName.ContainsKey(existing))
                {
                    continue;
                }

                changes.Add(new CabinetXmlMergeChange(
                    ChangeType: CabinetXmlMergeChangeType.Removed,
                    ToyName: existing,
                    Field: "LedStrip",
                    OldValue: "existing virtual toy",
                    NewValue: null));
            }
        }

        var managedToyOrdering = desiredByName.Keys
            .Concat(existingManaged.Keys.Where(name => !desiredByName.ContainsKey(name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CabinetXmlMergePlan(changes, managedToyOrdering);
    }

    public CabinetXmlMergeResult ApplyVirtualToyMerge(string cabinetXmlPath, CabinetXmlMergePlan plan, bool dryRun)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        var document = LoadCabinetDocument(cabinetXmlPath);
        var controllerKindsByName = GetControllerKindsByName(document);
        var toysRoot = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Toys");
        if (toysRoot is null)
        {
            throw new InvalidOperationException("Cabinet.xml is missing the <Toys> section.");
        }

        if (!plan.PlannedChanges.Any())
        {
            return new CabinetXmlMergeResult(false, FormatMergeSummary(plan, dryRun));
        }

        var ns = toysRoot.GetDefaultNamespace();

        // Conversational note: managed = virtual LedStrip toys only; everything else in <Toys> stays untouched.
        var managedExisting = toysRoot
            .Elements()
            .Where(x => x.Name.LocalName == "LedStrip")
            .Where(x =>
            {
                var toyName = GetChildValue(x, "Name");
                var controllerName = GetChildValue(x, "OutputControllerName");
                if (string.IsNullOrWhiteSpace(toyName) || string.IsNullOrWhiteSpace(controllerName))
                {
                    return false;
                }

                return controllerKindsByName.TryGetValue(controllerName!, out var kind)
                    && kind.Contains("Virtual", StringComparison.OrdinalIgnoreCase);
            })
            .ToDictionary(x => GetChildValue(x, "Name")!, StringComparer.OrdinalIgnoreCase);

        foreach (var change in plan.PlannedChanges)
        {
            if (change.ChangeType == CabinetXmlMergeChangeType.Added)
            {
                var parsed = ParseAddPayload(change.NewValue, change.ToyName);
                if (!controllerKindsByName.ContainsKey(parsed.OutputControllerName))
                {
                    throw new InvalidOperationException($"Unknown output controller '{parsed.OutputControllerName}' for virtual toy '{change.ToyName}'.");
                }

                var ledStrip = new XElement(ns + "LedStrip",
                    new XElement(ns + "Name", change.ToyName),
                    new XElement(ns + "Width", parsed.Width),
                    new XElement(ns + "Height", parsed.Height),
                    new XElement(ns + "OutputControllerName", parsed.OutputControllerName));

                managedExisting[change.ToyName] = ledStrip;
                continue;
            }

            if (!managedExisting.TryGetValue(change.ToyName, out var targetToy))
            {
                continue;
            }

            if (change.ChangeType == CabinetXmlMergeChangeType.Removed)
            {
                managedExisting.Remove(change.ToyName);
                continue;
            }

            SetOrCreateChildValue(targetToy, change.Field, change.NewValue ?? string.Empty);
        }

        // Conversational note: deterministic ordering is only enforced for managed virtual toys; non-managed toys
        // keep their original order so unrelated Cabinet.xml sections do not churn.
        var allChildren = toysRoot.Elements().ToList();
        var nonManagedChildren = allChildren
            .Where(node => node.Name.LocalName != "LedStrip"
                || string.IsNullOrWhiteSpace(GetChildValue(node, "Name"))
                || !managedExisting.ContainsKey(GetChildValue(node, "Name")!))
            .ToList();

        var managedChildrenOrdered = plan.ManagedToyOrder
            .Where(managedExisting.ContainsKey)
            .Select(name => managedExisting[name])
            .ToList();

        toysRoot.RemoveNodes();
        var firstManagedIndex = allChildren.FindIndex(node =>
        {
            var name = GetChildValue(node, "Name");
            return !string.IsNullOrWhiteSpace(name) && managedExisting.ContainsKey(name!);
        });

        if (firstManagedIndex < 0)
        {
            firstManagedIndex = nonManagedChildren.Count;
        }

        for (var i = 0; i < nonManagedChildren.Count; i++)
        {
            if (i == firstManagedIndex)
            {
                foreach (var managed in managedChildrenOrdered)
                {
                    toysRoot.Add(managed);
                }
            }

            toysRoot.Add(nonManagedChildren[i]);
        }

        if (firstManagedIndex >= nonManagedChildren.Count)
        {
            foreach (var managed in managedChildrenOrdered)
            {
                toysRoot.Add(managed);
            }
        }

        if (!dryRun)
        {
            var backupPath = $"{cabinetXmlPath}.bak.{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(cabinetXmlPath, backupPath, overwrite: false);

            var tempPath = Path.Combine(Path.GetDirectoryName(cabinetXmlPath)!, $"{Path.GetFileName(cabinetXmlPath)}.tmp");
            using var writer = XmlWriter.Create(tempPath, new XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Indent = false,
                NewLineHandling = NewLineHandling.None,
            });
            document.Save(writer);
            File.Move(tempPath, cabinetXmlPath, overwrite: true);
        }

        return new CabinetXmlMergeResult(!dryRun, FormatMergeSummary(plan, dryRun));
    }

    public string FormatMergeSummary(CabinetXmlMergePlan plan, bool dryRun)
    {
        var builder = new StringBuilder();
        builder.AppendLine(dryRun
            ? "Cabinet.xml dry-run summary (no file changes written):"
            : "Cabinet.xml merge summary:");

        if (!plan.PlannedChanges.Any())
        {
            builder.AppendLine("- No managed virtual-toy changes detected.");
            return builder.ToString().TrimEnd();
        }

        foreach (var group in plan.PlannedChanges.GroupBy(x => x.ChangeType))
        {
            builder.AppendLine($"- {group.Key}: {group.Count()}");
            foreach (var change in group.OrderBy(x => x.ToyName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Field, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("  • ");
                builder.Append(change.ToyName);
                builder.Append(" :: ");
                builder.Append(change.Field);

                if (!string.IsNullOrWhiteSpace(change.OldValue) || !string.IsNullOrWhiteSpace(change.NewValue))
                {
                    builder.Append(" | ");
                    builder.Append(string.IsNullOrWhiteSpace(change.OldValue) ? "(none)" : change.OldValue);
                    builder.Append(" -> ");
                    builder.Append(string.IsNullOrWhiteSpace(change.NewValue) ? "(none)" : change.NewValue);
                }

                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static XDocument LoadCabinetDocument(string cabinetXmlPath)
    {
        return XDocument.Load(cabinetXmlPath, LoadOptions.PreserveWhitespace);
    }

    private static Dictionary<string, string> GetControllerKindsByName(XDocument document)
    {
        return document
            .Descendants()
            .Where(x => x.Parent?.Name.LocalName == "OutputControllers")
            .Select(x => new
            {
                ControllerName = x.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? string.Empty,
                ControllerKind = x.Name.LocalName,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ControllerName))
            .ToDictionary(
                x => x.ControllerName,
                x => x.ControllerKind,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateDesiredVirtualToys(IReadOnlyCollection<VirtualLedToyDefinition> desiredVirtualToys)
    {
        foreach (var toy in desiredVirtualToys)
        {
            if (string.IsNullOrWhiteSpace(toy.Name))
            {
                throw new InvalidOperationException("Managed toy definitions require a non-empty name.");
            }

            if (toy.Width <= 0 || toy.Height <= 0)
            {
                throw new InvalidOperationException($"Managed toy '{toy.Name}' must have positive Width and Height.");
            }

            var ledTotal = toy.Width * toy.Height;
            if (ledTotal > SafeMaxLedTotal)
            {
                throw new InvalidOperationException($"Managed toy '{toy.Name}' exceeds safe LED total ({SafeMaxLedTotal}).");
            }

            if (string.IsNullOrWhiteSpace(toy.OutputControllerName))
            {
                throw new InvalidOperationException($"Managed toy '{toy.Name}' requires OutputControllerName.");
            }
        }
    }

    private static string? GetChildValue(XElement element, string childName)
    {
        return element.Elements().FirstOrDefault(x => x.Name.LocalName == childName)?.Value;
    }

    private static int? ParseIntOrNull(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static (int Width, int Height, string OutputControllerName) ParseAddPayload(string? payload, string toyName)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException($"Missing add payload for virtual toy '{toyName}'.");
        }

        var parts = payload.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim();
            parsed[key] = value;
        }

        if (!parsed.TryGetValue("Width", out var widthText) || !int.TryParse(widthText, out var width))
        {
            throw new InvalidOperationException($"Add payload for '{toyName}' is missing Width.");
        }

        if (!parsed.TryGetValue("Height", out var heightText) || !int.TryParse(heightText, out var height))
        {
            throw new InvalidOperationException($"Add payload for '{toyName}' is missing Height.");
        }

        if (!parsed.TryGetValue("Controller", out var controllerName) || string.IsNullOrWhiteSpace(controllerName))
        {
            throw new InvalidOperationException($"Add payload for '{toyName}' is missing Controller.");
        }

        return (width, height, controllerName);
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
}

public sealed record VirtualLedToyDefinition(
    string Name,
    int Width,
    int Height,
    string OutputControllerName = "Virtual Controller");

public sealed record CabinetXmlMergePlan(
    IReadOnlyList<CabinetXmlMergeChange> PlannedChanges,
    IReadOnlyList<string> ManagedToyOrder);

public sealed record CabinetXmlMergeChange(
    CabinetXmlMergeChangeType ChangeType,
    string ToyName,
    string Field,
    string? OldValue,
    string? NewValue);

public enum CabinetXmlMergeChangeType
{
    Added,
    Updated,
    Removed,
}

public sealed record CabinetXmlMergeResult(
    bool WroteChanges,
    string Summary);

public sealed record CabinetToyInventory(
    IReadOnlyList<CabinetToyEntry> VirtualToys,
    IReadOnlyList<CabinetToyEntry> HardwareToys);

public sealed record CabinetToyEntry(
    string Name,
    string Kind,
    string ControllerName,
    string ControllerKind,
    bool IsVirtual);
