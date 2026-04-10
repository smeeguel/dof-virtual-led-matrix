using System.IO;
using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.Core;
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
        Assert.True(plan.DesiredVirtualToysByName.ContainsKey("VirtualA"));
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
        Assert.False(File.Exists(temp.Path + ".tmp"));
    }

    [Fact]
    public void ApplyVirtualToyMerge_ShouldRejectAdditionsAgainstNonVirtualControllers()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <TeensyStripController><Name>Hardware Controller</Name></TeensyStripController>
              </OutputControllers>
              <Toys>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();
        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [new VirtualLedToyDefinition("VirtualA", 32, 8, "Hardware Controller")],
            removeMissingManagedToys: false);

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false));

        Assert.Contains("not virtual", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildVirtualToyMergePlanFromRouting_ShouldMapEnabledRoutingToysToManagedDefinitions()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController><Name>Virtual Controller</Name></VirtualLEDStripController>
                <TeensyStripController><Name>Hardware Controller</Name></TeensyStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>BackglassMain</Name>
                  <Width>32</Width>
                  <Height>8</Height>
                  <OutputControllerName>Virtual Controller</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>HardwareA</Name>
                  <Width>10</Width>
                  <Height>10</Height>
                  <OutputControllerName>Hardware Controller</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();

        var routingToys = new[]
        {
            new ToyRouteConfig
            {
                Id = "backglass-main",
                Name = "BackglassMain",
                Enabled = true,
                Mapping = new ToyMappingConfig { Width = 128, Height = 32 },
            },
            new ToyRouteConfig
            {
                Id = "topper-2",
                Name = "Topper2",
                Enabled = true,
                Mapping = new ToyMappingConfig { Width = 64, Height = 16 },
            },
            new ToyRouteConfig
            {
                Id = "disabled-ignored",
                Name = "Ignored",
                Enabled = false,
                Mapping = new ToyMappingConfig { Width = 8, Height = 8 },
            },
        };

        var plan = service.BuildVirtualToyMergePlanFromRouting(temp.Path, routingToys, removeMissingManagedToys: false);

        Assert.Contains(plan.DesiredVirtualToysByName.Keys, key => key == "BackglassMain");
        Assert.Contains(plan.DesiredVirtualToysByName.Keys, key => key == "Topper2");
        Assert.DoesNotContain(plan.DesiredVirtualToysByName.Keys, key => key == "Ignored");
        Assert.DoesNotContain(plan.PlannedChanges, change => change.ToyName == "HardwareA");
    }

    [Fact]
    public void BuildVirtualToyMergePlanFromRouting_ShouldFailWhenNoVirtualControllerExists()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <TeensyStripController><Name>Hardware Controller</Name></TeensyStripController>
              </OutputControllers>
              <Toys></Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();

        var routingToys = new[]
        {
            new ToyRouteConfig
            {
                Id = "matrix",
                Name = "Matrix",
                Enabled = true,
                Mapping = new ToyMappingConfig { Width = 32, Height = 8 },
            },
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.BuildVirtualToyMergePlanFromRouting(temp.Path, routingToys, removeMissingManagedToys: false));

        Assert.Contains("virtual output controller", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyVirtualToyMerge_FromRoutingShouldHandleRename_AddLedWizOutput_AndRealignControllerTotals()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                  <NumberOfLedsStrip1>4101</NumberOfLedsStrip1>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>128</Width>
                  <Height>32</Height>
                  <FirstLedNumber>1</FirstLedNumber>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
                <LedWizEquivalent>
                  <Name>LedWizEquivalent 30</Name>
                  <LedWizNumber>30</LedWizNumber>
                  <Outputs>
                    <LedWizEquivalentOutput>
                      <OutputName>Matrix1</OutputName>
                      <LedWizEquivalentOutputNumber>1</LedWizEquivalentOutputNumber>
                    </LedWizEquivalentOutput>
                  </Outputs>
                </LedWizEquivalent>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();
        var routing = new[]
        {
            new ToyRouteConfig
            {
                Id = "matrix-main",
                Name = "BackglassMain",
                Enabled = true,
                Mapping = new ToyMappingConfig { Width = 128, Height = 32 },
                Source = new ToySourceConfig { CanonicalStart = 0, Length = 4096 },
            },
            new ToyRouteConfig
            {
                Id = "topper",
                Name = "Topper2",
                Enabled = true,
                Mapping = new ToyMappingConfig { Width = 5, Height = 1 },
                Source = new ToySourceConfig { CanonicalStart = 4096, Length = 5 },
            },
        };

        var plan = service.BuildVirtualToyMergePlanFromRouting(temp.Path, routing, removeMissingManagedToys: false);
        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);

        var merged = File.ReadAllText(temp.Path);
        Assert.Contains("\r\n", merged);
        Assert.Contains("\t<", merged);
        Assert.Contains("<Name>BackglassMain</Name>", merged);
        Assert.DoesNotContain("<Name>Matrix1</Name>", merged);
        Assert.Contains("<Name>Topper2</Name>", merged);
        Assert.Contains("</LedStrip>\r\n\r\n\t\t<LedStrip>", merged);
        Assert.Contains("<FirstLedNumber>1</FirstLedNumber>", merged);
        Assert.Contains("<FirstLedNumber>4097</FirstLedNumber>", merged);
        Assert.Contains("<NumberOfLedsStrip1>4101</NumberOfLedsStrip1>", merged);
        Assert.Contains("<OutputName>BackglassMain</OutputName>", merged);
        Assert.Contains("<OutputName>Topper2</OutputName>", merged);
    }

    [Fact]
    public void BuildVirtualToyMergePlanFromRouting_ShouldNotForceFirstLedRenumberWhenRoutingStartsCollide()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController><Name>LED Strips 0</Name></VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>128</Width>
                  <Height>32</Height>
                  <FirstLedNumber>1</FirstLedNumber>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>Matrix2</Name>
                  <Width>5</Width>
                  <Height>1</Height>
                  <FirstLedNumber>4097</FirstLedNumber>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();
        var routing = new[]
        {
            new ToyRouteConfig
            {
                Id = "matrix-1",
                Name = "Matrix1",
                Enabled = true,
                Mapping = new ToyMappingConfig { Width = 128, Height = 32 },
                Source = new ToySourceConfig { CanonicalStart = 0, Length = 4096 },
            },
            new ToyRouteConfig
            {
                Id = "matrix-2",
                Name = "Matrix2",
                Enabled = true,
                Mapping = new ToyMappingConfig { Width = 5, Height = 1 },
                Source = new ToySourceConfig { CanonicalStart = 0, Length = 5 },
            },
        };

        var plan = service.BuildVirtualToyMergePlanFromRouting(temp.Path, routing, removeMissingManagedToys: false);
        Assert.DoesNotContain(plan.PlannedChanges, change =>
            change.ToyName == "Matrix2"
            && change.Field == "FirstLedNumber");
    }

    [Fact]
    public void ApplyVirtualToyMerge_ShouldAssignFirstUnusedLedAndIncreaseControllerTotalForNewToy()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                  <NumberOfLedsStrip1>4101</NumberOfLedsStrip1>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>128</Width>
                  <Height>32</Height>
                  <FirstLedNumber>1</FirstLedNumber>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>Matrix2</Name>
                  <Width>5</Width>
                  <Height>1</Height>
                  <FirstLedNumber>4097</FirstLedNumber>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();
        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [
                new VirtualLedToyDefinition("Matrix1", 128, 32, "LED Strips 0", FirstLedNumber: 1, LedCount: 4096),
                new VirtualLedToyDefinition("Matrix2", 5, 1, "LED Strips 0", FirstLedNumber: 4097, LedCount: 5),
                new VirtualLedToyDefinition("VerticalStrip3", 1, 16, "LED Strips 0", FirstLedNumber: null, LedCount: 16),
            ],
            removeMissingManagedToys: false);

        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);
        var merged = File.ReadAllText(temp.Path);

        Assert.Contains("<Name>VerticalStrip3</Name>", merged);
        Assert.Contains("<FirstLedNumber>4102</FirstLedNumber>", merged);
        Assert.Contains("<NumberOfLedsStrip1>4117</NumberOfLedsStrip1>", merged);
    }

    [Fact]
    public void ApplyVirtualToyMerge_ShouldShiftNewToyWhenRequestedFirstLedOverlapsExistingRange()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                  <NumberOfLedsStrip1>4096</NumberOfLedsStrip1>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>128</Width>
                  <Height>32</Height>
                  <FirstLedNumber>1</FirstLedNumber>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();
        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [
                new VirtualLedToyDefinition("Matrix1", 128, 32, "LED Strips 0", FirstLedNumber: 1, LedCount: 4096),
                new VirtualLedToyDefinition("FlasherTop", 5, 1, "LED Strips 0", FirstLedNumber: 1, LedCount: 16),
            ],
            removeMissingManagedToys: false);

        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);
        var merged = File.ReadAllText(temp.Path);

        Assert.Contains("<Name>FlasherTop</Name>", merged);
        Assert.Contains("<FirstLedNumber>4097</FirstLedNumber>", merged);
        Assert.Contains("<NumberOfLedsStrip1>4112</NumberOfLedsStrip1>", merged);
    }

    [Fact]
    public void ApplyVirtualToyMerge_ShouldReduceControllerTotalWhenToyLedCountIsReduced()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                  <NumberOfLedsStrip1>4112</NumberOfLedsStrip1>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>128</Width>
                  <Height>32</Height>
                  <FirstLedNumber>1</FirstLedNumber>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>FlasherTop</Name>
                  <Width>5</Width>
                  <Height>1</Height>
                  <FirstLedNumber>4097</FirstLedNumber>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                  <LedCount>16</LedCount>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();
        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [
                new VirtualLedToyDefinition("Matrix1", 128, 32, "LED Strips 0", FirstLedNumber: 1, LedCount: 4096),
                new VirtualLedToyDefinition("FlasherTop", 5, 1, "LED Strips 0", FirstLedNumber: 4097, LedCount: 5),
            ],
            removeMissingManagedToys: false);

        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);
        var merged = File.ReadAllText(temp.Path);

        Assert.Contains("<LedCount>5</LedCount>", merged);
        Assert.Contains("<NumberOfLedsStrip1>4101</NumberOfLedsStrip1>", merged);
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
