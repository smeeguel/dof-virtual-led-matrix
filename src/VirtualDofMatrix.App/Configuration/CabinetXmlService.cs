using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using VirtualDofMatrix.Core;

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

    public string? GetLedStripOutputControllerName(string cabinetXmlPath, string toyName)
    {
        var document = LoadCabinetDocument(cabinetXmlPath);
        var toy = document
            .Descendants()
            .Where(x => x.Name.LocalName == "LedStrip")
            .FirstOrDefault(x => string.Equals(GetChildValue(x, "Name"), toyName, StringComparison.OrdinalIgnoreCase));
        return toy is null ? null : GetChildValue(toy, "OutputControllerName");
    }

    public string? GetDefaultVirtualControllerName(string cabinetXmlPath)
    {
        var document = LoadCabinetDocument(cabinetXmlPath);
        var controllerKindsByName = GetControllerKindsByName(document);
        return controllerKindsByName
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Value.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .Key;
    }

    // Conversational note: this adapter lets routing toy edits drive Cabinet.xml managed virtual-toy sync in one pass.
    public CabinetXmlMergePlan BuildVirtualToyMergePlanFromRouting(
        string cabinetXmlPath,
        IEnumerable<ToyRouteConfig> routingToys,
        bool removeMissingManagedToys)
    {
        var defaultVirtualController = GetDefaultVirtualControllerName(cabinetXmlPath);
        if (string.IsNullOrWhiteSpace(defaultVirtualController))
        {
            throw new InvalidOperationException("Cabinet.xml does not define a virtual output controller.");
        }

        var desired = routingToys
            .Where(toy => toy.Enabled)
            .Select(toy =>
            {
                var toyName = !string.IsNullOrWhiteSpace(toy.Name) ? toy.Name : toy.Id;
                if (string.IsNullOrWhiteSpace(toyName))
                {
                    throw new InvalidOperationException("Routing toy requires Name or Id.");
                }

                // Prefer an existing controller link for this toy if one already exists in Cabinet.xml.
                var controllerName = GetLedStripOutputControllerName(cabinetXmlPath, toyName)
                    ?? defaultVirtualController;

                return new VirtualLedToyDefinition(
                    Name: toyName,
                    Width: toy.Mapping.Width,
                    Height: toy.Mapping.Height,
                    OutputControllerName: controllerName,
                    FirstLedNumber: toy.Source.CanonicalStart.HasValue ? toy.Source.CanonicalStart.Value + 1 : null,
                    LedCount: toy.Source.Length > 0 ? toy.Source.Length : toy.Mapping.Width * toy.Mapping.Height,
                    ToyId: toy.Id);
            })
            .ToArray();

        // Conversational note: routing sync should never rename an existing Cabinet.xml toy solely because
        // FirstLedNumber happens to match; new routing toys should be added as new managed entries.
        return BuildVirtualToyMergePlan(
            cabinetXmlPath,
            desired,
            removeMissingManagedToys,
            allowFirstLedNameRebind: false);
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

        var knownToyNames = GetLedStripToyNames(cabinetXmlPath);
        if (!knownToyNames.Contains(toyName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not find LedStrip toy '{toyName}'.");
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
        bool removeMissingManagedToys,
        bool allowFirstLedNameRebind = true)
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
                FirstLedNumber = ParseIntOrNull(GetChildValue(x, "FirstLedNumber")),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.OutputControllerName))
            .Where(x => controllerKindsByName.TryGetValue(x.OutputControllerName!, out var kind)
                && kind.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var existingByName = existingManaged.ToDictionary(
            x => x.Name!,
            x => x,
            StringComparer.OrdinalIgnoreCase);
        var unmatchedExistingNames = new HashSet<string>(existingByName.Keys, StringComparer.OrdinalIgnoreCase);

        var desiredByName = desiredVirtualToys
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase);
        var duplicatedFirstLedNumbers = desiredByName.Values
            .Where(x => x.FirstLedNumber.HasValue)
            .GroupBy(x => x.FirstLedNumber!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        var changes = new List<CabinetXmlMergeChange>();

        foreach (var desired in desiredByName.Values)
        {
            var lookupName = desired.Name;
            if (!existingByName.TryGetValue(lookupName, out var current)
                && allowFirstLedNameRebind
                && desired.FirstLedNumber.HasValue
                && !duplicatedFirstLedNumbers.Contains(desired.FirstLedNumber.Value))
            {
                current = existingManaged.FirstOrDefault(x =>
                    x.FirstLedNumber == desired.FirstLedNumber
                    && unmatchedExistingNames.Contains(x.Name!));
                if (current is not null)
                {
                    lookupName = current.Name!;
                    changes.Add(new CabinetXmlMergeChange(
                        CabinetXmlMergeChangeType.Updated,
                        lookupName,
                        "Name",
                        current.Name,
                        desired.Name));
                }
            }

            if (current is null)
            {
                changes.Add(new CabinetXmlMergeChange(
                    ChangeType: CabinetXmlMergeChangeType.Added,
                    ToyName: desired.Name,
                    Field: "LedStrip",
                    OldValue: null,
                    NewValue: "new managed virtual toy"));
                continue;
            }

            unmatchedExistingNames.Remove(current.Name!);

            var widthText = GetChildValue(current.Element, "Width");
            var heightText = GetChildValue(current.Element, "Height");
            var controllerText = GetChildValue(current.Element, "OutputControllerName");
            var firstLedText = GetChildValue(current.Element, "FirstLedNumber");
            var ledCountText = GetChildValue(current.Element, "LedCount");
            var oldWidth = ParseIntOrNull(widthText);
            var oldHeight = ParseIntOrNull(heightText);
            var oldLedCount = ParseIntOrNull(ledCountText);
            var desiredLedCount = desired.LedCount ?? Math.Max(1, desired.Width * desired.Height);

            if (oldWidth != desired.Width)
            {
                changes.Add(new CabinetXmlMergeChange(CabinetXmlMergeChangeType.Updated, lookupName, "Width", widthText, desired.Width.ToString()));
            }

            if (oldHeight != desired.Height)
            {
                changes.Add(new CabinetXmlMergeChange(CabinetXmlMergeChangeType.Updated, lookupName, "Height", heightText, desired.Height.ToString()));
            }

            if (!string.Equals(controllerText, desired.OutputControllerName, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new CabinetXmlMergeChange(CabinetXmlMergeChangeType.Updated, lookupName, "OutputControllerName", controllerText, desired.OutputControllerName));
            }

            if (desired.FirstLedNumber.HasValue
                && !duplicatedFirstLedNumbers.Contains(desired.FirstLedNumber.Value)
                && ParseIntOrNull(firstLedText) != desired.FirstLedNumber)
            {
                changes.Add(new CabinetXmlMergeChange(
                    CabinetXmlMergeChangeType.Updated,
                    lookupName,
                    "FirstLedNumber",
                    firstLedText,
                    desired.FirstLedNumber.Value.ToString()));
            }

            if (oldLedCount != desiredLedCount)
            {
                changes.Add(new CabinetXmlMergeChange(
                    CabinetXmlMergeChangeType.Updated,
                    lookupName,
                    "LedCount",
                    ledCountText,
                    desiredLedCount.ToString()));
            }
        }

        if (removeMissingManagedToys)
        {
            foreach (var existing in unmatchedExistingNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
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

        var managedToyOrdering = desiredByName.Values
            .OrderBy(x => x.FirstLedNumber ?? int.MaxValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name)
            .Concat(existingByName.Keys.Where(name => !desiredByName.ContainsKey(name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CabinetXmlMergePlan(changes, managedToyOrdering)
        {
            DesiredVirtualToysByName = desiredByName,
        };
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
        var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in plan.PlannedChanges)
        {
            if (change.ChangeType == CabinetXmlMergeChangeType.Added)
            {
                if (!plan.DesiredVirtualToysByName.TryGetValue(change.ToyName, out var desiredToy))
                {
                    throw new InvalidOperationException($"Could not resolve add payload for virtual toy '{change.ToyName}'.");
                }

                if (!controllerKindsByName.TryGetValue(desiredToy.OutputControllerName, out var controllerKind)
                    || !controllerKind.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Output controller '{desiredToy.OutputControllerName}' is missing or not virtual for toy '{change.ToyName}'.");
                }

                var ledCount = desiredToy.LedCount ?? Math.Max(1, desiredToy.Width * desiredToy.Height);
                var nextAvailableFirstLed = GetNextAvailableFirstLedNumber(managedExisting.Values, desiredToy.OutputControllerName);
                var firstLedNumber = desiredToy.FirstLedNumber ?? nextAvailableFirstLed;
                if (!IsLedRangeAvailable(managedExisting.Values, desiredToy.OutputControllerName, firstLedNumber, ledCount))
                {
                    // Conversational note: if a routing toy comes in with a default/overlapping first LED (for
                    // example 1), move it to the next free slot rather than stacking ranges at the same address.
                    firstLedNumber = nextAvailableFirstLed;
                }

                var ledStrip = new XElement(ns + "LedStrip",
                    new XElement(ns + "Name", change.ToyName),
                    new XElement(ns + "Width", desiredToy.Width),
                    new XElement(ns + "Height", desiredToy.Height),
                    new XElement(ns + "LedStripArrangement", "TopDownAlternateRightLeft"),
                    new XElement(ns + "ColorOrder", "RGB"),
                    new XElement(ns + "FirstLedNumber", firstLedNumber),
                    new XElement(ns + "FadingCurveName", "SwissLizardsLedCurve"),
                    new XElement(ns + "Brightness", "100"),
                    new XElement(ns + "OutputControllerName", desiredToy.OutputControllerName));

                // Conversational note: keep an explicit LED count hint so downstream total calculations can use
                // source length if it differs from Width*Height for non-matrix strip toys.
                SetOrCreateChildValue(ledStrip, "LedCount", ledCount.ToString());

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

            if (string.Equals(change.Field, "Name", StringComparison.OrdinalIgnoreCase))
            {
                renameMap[change.ToyName] = change.NewValue ?? change.ToyName;
            }

            SetOrCreateChildValue(targetToy, change.Field, change.NewValue ?? string.Empty);
        }

        var managedByCurrentName = managedExisting.Values
            .Select(x => new { Element = x, Name = GetChildValue(x, "Name") })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .ToDictionary(x => x.Name!, x => x.Element, StringComparer.OrdinalIgnoreCase);

        // Conversational note: deterministic ordering is only enforced for managed virtual toys; non-managed toys
        // keep their original order so unrelated Cabinet.xml sections do not churn.
        var allChildren = toysRoot.Elements().ToList();
        var nonManagedChildren = allChildren
            .Where(node => node.Name.LocalName != "LedStrip"
                || string.IsNullOrWhiteSpace(GetChildValue(node, "Name"))
                || !managedByCurrentName.ContainsKey(GetChildValue(node, "Name")!))
            .ToList();

        var managedChildrenOrdered = plan.ManagedToyOrder
            .Where(managedByCurrentName.ContainsKey)
            .Select(name => managedByCurrentName[name])
            .ToList();

        toysRoot.RemoveNodes();
        var firstManagedIndex = allChildren.FindIndex(node =>
        {
            var name = GetChildValue(node, "Name");
            return !string.IsNullOrWhiteSpace(name) && managedByCurrentName.ContainsKey(name!);
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
            SyncVirtualControllerLedCounts(document, managedByCurrentName.Values);
            SyncLedWizEquivalentOutputs(toysRoot, renameMap, plan.DesiredVirtualToysByName.Values);
            RemoveWhitespaceTextNodes(toysRoot);

            var backupPath = $"{cabinetXmlPath}.bak.{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(cabinetXmlPath, backupPath, overwrite: false);

            var tempPath = Path.Combine(Path.GetDirectoryName(cabinetXmlPath)!, $"{Path.GetFileName(cabinetXmlPath)}.tmp");
            var writerSettings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "\t",
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = false,
            };
            // Conversational note: dispose the XmlWriter before replacing Cabinet.xml so Windows does not keep
            // the temp file handle open and trigger "file is being used by another process" on File.Move.
            using (var writer = XmlWriter.Create(tempPath, writerSettings))
            {
                document.Save(writer);
            }
            ApplyReadableLedStripSpacing(tempPath);
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
        var raw = File.ReadAllText(cabinetXmlPath);
        var normalized = NormalizeEscapedNewlines(raw);
        return XDocument.Parse(normalized, LoadOptions.PreserveWhitespace);
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

    private static int GetNextAvailableFirstLedNumber(IEnumerable<XElement> managedLedStrips, string controllerName)
    {
        var lastUsed = managedLedStrips
            .Where(toy => string.Equals(GetChildValue(toy, "OutputControllerName"), controllerName, StringComparison.OrdinalIgnoreCase))
            .Select(toy =>
            {
                var firstLed = ParseIntOrNull(GetChildValue(toy, "FirstLedNumber")) ?? 1;
                var ledCount = ParseIntOrNull(GetChildValue(toy, "LedCount"))
                    ?? ((ParseIntOrNull(GetChildValue(toy, "Width")) ?? 0) * (ParseIntOrNull(GetChildValue(toy, "Height")) ?? 0));
                return firstLed + Math.Max(ledCount - 1, 0);
            })
            .DefaultIfEmpty(0)
            .Max();

        return lastUsed + 1;
    }

    private static bool IsLedRangeAvailable(
        IEnumerable<XElement> managedLedStrips,
        string controllerName,
        int firstLedNumber,
        int ledCount)
    {
        var candidateStart = firstLedNumber;
        var candidateEnd = firstLedNumber + Math.Max(ledCount - 1, 0);

        foreach (var toy in managedLedStrips)
        {
            if (!string.Equals(GetChildValue(toy, "OutputControllerName"), controllerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingStart = ParseIntOrNull(GetChildValue(toy, "FirstLedNumber")) ?? 1;
            var existingCount = ParseIntOrNull(GetChildValue(toy, "LedCount"))
                ?? ((ParseIntOrNull(GetChildValue(toy, "Width")) ?? 0) * (ParseIntOrNull(GetChildValue(toy, "Height")) ?? 0));
            var existingEnd = existingStart + Math.Max(existingCount - 1, 0);

            var overlaps = candidateStart <= existingEnd && existingStart <= candidateEnd;
            if (overlaps)
            {
                return false;
            }
        }

        return true;
    }

    private static void SyncVirtualControllerLedCounts(XDocument document, IEnumerable<XElement> managedLedStrips)
    {
        var totalsByController = managedLedStrips
            .Select(toy =>
            {
                var controllerName = GetChildValue(toy, "OutputControllerName");
                var firstLed = ParseIntOrNull(GetChildValue(toy, "FirstLedNumber")) ?? 1;
                var width = ParseIntOrNull(GetChildValue(toy, "Width")) ?? 0;
                var height = ParseIntOrNull(GetChildValue(toy, "Height")) ?? 0;
                var ledCount = ParseIntOrNull(GetChildValue(toy, "LedCount"))
                    ?? (width > 0 && height > 0 ? width * height : 0);
                var lastLed = firstLed + Math.Max(ledCount - 1, 0);
                return new { controllerName, lastLed };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.controllerName))
            .GroupBy(x => x.controllerName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(x => x.lastLed), StringComparer.OrdinalIgnoreCase);

        foreach (var controller in document
            .Descendants()
            .Where(x => x.Name.LocalName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)))
        {
            var controllerName = GetChildValue(controller, "Name");
            if (string.IsNullOrWhiteSpace(controllerName) || !totalsByController.TryGetValue(controllerName, out var maxLed))
            {
                continue;
            }

            SetOrCreateChildValue(controller, "NumberOfLedsStrip1", Math.Max(maxLed, 0).ToString());
        }
    }

    private static void SyncLedWizEquivalentOutputs(
        XElement toysRoot,
        IReadOnlyDictionary<string, string> renameMap,
        IEnumerable<VirtualLedToyDefinition> desiredToys)
    {
        var desiredByName = desiredToys
            .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var equivalent in toysRoot.Elements().Where(x => x.Name.LocalName == "LedWizEquivalent"))
        {
            var outputsNode = equivalent.Elements().FirstOrDefault(x => x.Name.LocalName == "Outputs");
            if (outputsNode is null)
            {
                continue;
            }

            foreach (var output in outputsNode.Elements().Where(x => x.Name.LocalName == "LedWizEquivalentOutput"))
            {
                var outputName = GetChildValue(output, "OutputName");
                if (!string.IsNullOrWhiteSpace(outputName) && renameMap.TryGetValue(outputName, out var renamed))
                {
                    SetOrCreateChildValue(output, "OutputName", renamed);
                }
            }

            var outputsByName = outputsNode
                .Elements()
                .Where(x => x.Name.LocalName == "LedWizEquivalentOutput")
                .Select(x => new { Element = x, Name = GetChildValue(x, "OutputName") })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .ToDictionary(x => x.Name!, x => x.Element, StringComparer.OrdinalIgnoreCase);
            var newlyAddedNames = new List<string>();

            foreach (var desired in desiredByName.Values)
            {
                if (outputsByName.ContainsKey(desired.Name))
                {
                    continue;
                }

                var output = new XElement(outputsNode.GetDefaultNamespace() + "LedWizEquivalentOutput",
                    new XElement(outputsNode.GetDefaultNamespace() + "OutputName", desired.Name),
                    new XElement(outputsNode.GetDefaultNamespace() + "LedWizEquivalentOutputNumber", 0));
                outputsNode.Add(output);
                outputsByName[desired.Name] = output;
                newlyAddedNames.Add(desired.Name);
            }

            // Conversational note: keep existing LedWiz output numbers stable for existing toys, and only append
            // new toys at RGB-aligned starts (1,4,7,...) so DOF Config Tool mappings stay channel-aligned.
            var occupiedSlots = outputsByName
                .Where(pair => !newlyAddedNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .Select(pair => ParseIntOrNull(GetChildValue(pair.Value, "LedWizEquivalentOutputNumber")) ?? 0)
                .Where(number => number > 0)
                .ToHashSet();

            const int rgbChannelWidth = 3;
            var nextSlot = occupiedSlots.Count == 0 ? 1 : occupiedSlots.Max() + 1;
            while (!IsRgbAlignedSlotAvailable(occupiedSlots, nextSlot, rgbChannelWidth))
            {
                nextSlot++;
            }

            foreach (var name in newlyAddedNames)
            {
                if (!outputsByName.TryGetValue(name, out var output))
                {
                    continue;
                }

                while (!IsRgbAlignedSlotAvailable(occupiedSlots, nextSlot, rgbChannelWidth))
                {
                    nextSlot++;
                }

                SetOrCreateChildValue(output, "LedWizEquivalentOutputNumber", nextSlot.ToString());
                occupiedSlots.Add(nextSlot);
                occupiedSlots.Add(nextSlot + 1);
                occupiedSlots.Add(nextSlot + 2);
                nextSlot += rgbChannelWidth;
            }
        }
    }

    private static bool IsRgbAlignedSlotAvailable(HashSet<int> occupiedSlots, int candidateStart, int rgbChannelWidth)
    {
        if (candidateStart < 1)
        {
            return false;
        }

        // Conversational note: RGB outputs occupy a contiguous triplet (R,G,B), so all three positions must be
        // free and the start index must follow LedWiz-style channel boundaries (1,4,7,...).
        if ((candidateStart - 1) % rgbChannelWidth != 0)
        {
            return false;
        }

        for (var offset = 0; offset < rgbChannelWidth; offset++)
        {
            if (occupiedSlots.Contains(candidateStart + offset))
            {
                return false;
            }
        }

        return true;
    }

    private static void RemoveWhitespaceTextNodes(XElement element)
    {
        foreach (var node in element.Nodes().ToList())
        {
            if (node is XText textNode && string.IsNullOrWhiteSpace(textNode.Value))
            {
                textNode.Remove();
                continue;
            }

            if (node is XElement child)
            {
                RemoveWhitespaceTextNodes(child);
            }
        }
    }

    private static void ApplyReadableLedStripSpacing(string xmlPath)
    {
        var xml = NormalizeEscapedNewlines(File.ReadAllText(xmlPath));

        // Conversational note: keep one blank line between adjacent LedStrip blocks so newly inserted toys are
        // visually separated and easy to review in Cabinet.xml.
        var normalized = Regex.Replace(
            xml,
            @"(</LedStrip>(?:\r\n|\n))([ \t]*<LedStrip>)",
            "$1\r\n$2",
            RegexOptions.CultureInvariant);

        File.WriteAllText(xmlPath, normalized);
    }

    private static string NormalizeEscapedNewlines(string raw)
    {
        if (!raw.Contains("\\n", StringComparison.Ordinal))
        {
            return raw;
        }

        return raw
            .Replace("\\r\\n", "\r\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);
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
    string OutputControllerName = "Virtual Controller",
    int? FirstLedNumber = null,
    int? LedCount = null,
    string? ToyId = null);

public sealed record CabinetXmlMergePlan(
    IReadOnlyList<CabinetXmlMergeChange> PlannedChanges,
    IReadOnlyList<string> ManagedToyOrder)
{
    // Conversational note: keep desired toy definitions on the plan so Apply does not reparse string payloads.
    public IReadOnlyDictionary<string, VirtualLedToyDefinition> DesiredVirtualToysByName { get; init; }
        = new Dictionary<string, VirtualLedToyDefinition>(StringComparer.OrdinalIgnoreCase);
}

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
