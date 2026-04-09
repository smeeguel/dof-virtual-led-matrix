using System.IO;
using VirtualDofMatrix.App.Configuration;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class CabinetXmlServiceTests
{
    [Fact]
    public void GetToyInventory_ShouldSplitVirtualAndHardwareToysByControllerType()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>Virtual Controller</Name>
                </VirtualLEDStripController>
                <TeensyStripController>
                  <Name>Hardware Controller</Name>
                </TeensyStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>VirtualMatrix</Name>
                  <OutputControllerName>Virtual Controller</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>HardwareStrip</Name>
                  <OutputControllerName>Hardware Controller</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();

        var inventory = service.GetToyInventory(temp.Path);

        Assert.Contains(inventory.VirtualToys, x => x.Name == "VirtualMatrix");
        Assert.Contains(inventory.HardwareToys, x => x.Name == "HardwareStrip");
    }

    [Fact]
    public void BuildVirtualToyMergePlan_ShouldOnlyIncludeVirtualManagedChanges()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController><Name>Virtual Controller</Name></VirtualLEDStripController>
                <TeensyStripController><Name>Hardware Controller</Name></TeensyStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>VirtualA</Name>
                  <Width>32</Width>
                  <Height>8</Height>
                  <OutputControllerName>Virtual Controller</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>HardwareA</Name>
                  <Width>16</Width>
                  <Height>16</Height>
                  <OutputControllerName>Hardware Controller</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();

        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [new VirtualLedToyDefinition("VirtualA", 64, 16, "Virtual Controller")],
            removeMissingManagedToys: false);

        Assert.Equal(2, plan.PlannedChanges.Count);
        Assert.All(plan.PlannedChanges, change => Assert.Equal("VirtualA", change.ToyName));
        Assert.DoesNotContain(plan.PlannedChanges, change => change.ToyName == "HardwareA");
        Assert.Equal(["VirtualA"], plan.ManagedToyOrder);
    }

    [Fact]
    public void ApplyVirtualToyMerge_DryRunShouldReportChangesWithoutWritingFile()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController><Name>Virtual Controller</Name></VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>VirtualA</Name>
                  <Width>32</Width>
                  <Height>8</Height>
                  <OutputControllerName>Virtual Controller</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();

        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [new VirtualLedToyDefinition("VirtualA", 128, 32, "Virtual Controller")],
            removeMissingManagedToys: false);

        var original = File.ReadAllText(temp.Path);
        var result = service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: true);

        Assert.False(result.WroteChanges);
        Assert.Contains("dry-run", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, File.ReadAllText(temp.Path));
    }

    [Fact]
    public void ApplyVirtualToyMerge_ShouldKeepHardwareNodesAndUseDeterministicManagedOrder()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController><Name>Virtual Controller</Name></VirtualLEDStripController>
                <TeensyStripController><Name>Hardware Controller</Name></TeensyStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>VirtualZ</Name>
                  <Width>32</Width>
                  <Height>8</Height>
                  <OutputControllerName>Virtual Controller</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>HardwareA</Name>
                  <Width>16</Width>
                  <Height>16</Height>
                  <OutputControllerName>Hardware Controller</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>VirtualA</Name>
                  <Width>32</Width>
                  <Height>8</Height>
                  <OutputControllerName>Virtual Controller</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();

        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [
                new VirtualLedToyDefinition("VirtualA", 40, 10, "Virtual Controller"),
                new VirtualLedToyDefinition("VirtualB", 50, 10, "Virtual Controller"),
                new VirtualLedToyDefinition("VirtualZ", 60, 10, "Virtual Controller"),
            ],
            removeMissingManagedToys: false);

        var result = service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);
        var mergedXml = File.ReadAllText(temp.Path);

        Assert.True(result.WroteChanges);
        Assert.Contains("HardwareA", mergedXml);

        var indexVirtualA = mergedXml.IndexOf("<Name>VirtualA</Name>", StringComparison.Ordinal);
        var indexVirtualB = mergedXml.IndexOf("<Name>VirtualB</Name>", StringComparison.Ordinal);
        var indexVirtualZ = mergedXml.IndexOf("<Name>VirtualZ</Name>", StringComparison.Ordinal);
        Assert.True(indexVirtualA >= 0 && indexVirtualB >= 0 && indexVirtualZ >= 0);
        Assert.True(indexVirtualA < indexVirtualB && indexVirtualB < indexVirtualZ);

        Assert.Contains(".bak.", string.Join('\n', Directory.GetFiles(System.IO.Path.GetDirectoryName(temp.Path)!, System.IO.Path.GetFileName(temp.Path) + ".bak.*")));
    }

    private sealed class TempCabinetXml : IDisposable
    {
        public TempCabinetXml(string xml)
        {
            // Conversational note: tests use a dedicated temp folder so backup-file assertions can be deterministic.
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vdm-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(folder);
            Path = System.IO.Path.Combine(folder, "Cabinet.xml");
            File.WriteAllText(Path, xml);
            _folder = folder;
        }

        public string Path { get; }

        private readonly string _folder;

        public void Dispose()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
    }
}
