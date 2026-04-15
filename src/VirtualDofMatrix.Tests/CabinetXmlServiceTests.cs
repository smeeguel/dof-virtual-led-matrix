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
    public void GetVirtualLedStripToys_ShouldReturnOnlyLedStripsBoundToVirtualControllers()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController><Name>Virtual Controller</Name></VirtualLEDStripController>
                <TeensyStripController><Name>Hardware Controller</Name></TeensyStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>128</Width>
                  <Height>32</Height>
                  <LedStripArrangement>TopDownAlternateRightLeft</LedStripArrangement>
                  <FirstLedNumber>1</FirstLedNumber>
                  <LedCount>4096</LedCount>
                  <OutputControllerName>Virtual Controller</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>HardwareStrip</Name>
                  <Width>16</Width>
                  <Height>1</Height>
                  <OutputControllerName>Hardware Controller</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();

        var toys = service.GetVirtualLedStripToys(temp.Path);
        var toy = Assert.Single(toys);

        Assert.Equal("Matrix1", toy.Name);
        Assert.Equal(128, toy.Width);
        Assert.Equal(32, toy.Height);
        Assert.Equal(1, toy.FirstLedNumber);
        Assert.Equal(4096, toy.LedCount);
        Assert.Equal("TopDownAlternateRightLeft", toy.LedStripArrangement);
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
    public void ApplyVirtualToyMerge_ShouldRespectIncomingToyOrderWhenFirstLedNumbersAreDuplicated()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController><Name>Virtual Controller</Name></VirtualLEDStripController>
              </OutputControllers>
              <Toys>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();

        // Note: this reproduces a "new toys all default to canonicalStart=0" session where
        // every desired toy maps to FirstLedNumber=1 and deterministic fallback ordering is required.
        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [
                new VirtualLedToyDefinition("StripTop", 8, 1, "Virtual Controller", FirstLedNumber: 1, LedCount: 8),
                new VirtualLedToyDefinition("StripLeft", 8, 1, "Virtual Controller", FirstLedNumber: 1, LedCount: 8),
                new VirtualLedToyDefinition("StripRight", 8, 1, "Virtual Controller", FirstLedNumber: 1, LedCount: 8),
            ],
            removeMissingManagedToys: false);

        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);
        var mergedXml = File.ReadAllText(temp.Path);

        var indexTop = mergedXml.IndexOf("<Name>StripTop</Name>", StringComparison.Ordinal);
        var indexLeft = mergedXml.IndexOf("<Name>StripLeft</Name>", StringComparison.Ordinal);
        var indexRight = mergedXml.IndexOf("<Name>StripRight</Name>", StringComparison.Ordinal);

        Assert.True(indexTop >= 0 && indexLeft >= 0 && indexRight >= 0);
        Assert.True(indexTop < indexLeft && indexLeft < indexRight);
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
    public void ApplyVirtualToyMerge_FromRoutingShouldNotRenameExistingMatrixWhenAddingNewToy()
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
                Id = "top-strip",
                Name = "TopStrip",
                Enabled = true,
                Mapping = new ToyMappingConfig { Width = 5, Height = 1 },
                Source = new ToySourceConfig { CanonicalStart = 0, Length = 5 },
            },
        };

        var plan = service.BuildVirtualToyMergePlanFromRouting(temp.Path, routing, removeMissingManagedToys: false);
        // Note: routing additions should append new managed toys; they should not "rename over"
        // an existing Matrix1 entry just because both point at FirstLedNumber=1.
        Assert.DoesNotContain(plan.PlannedChanges, change =>
            change.ChangeType == CabinetXmlMergeChangeType.Updated
            && change.Field.Equals("Name", StringComparison.OrdinalIgnoreCase)
            && change.ToyName.Equals("Matrix1", StringComparison.OrdinalIgnoreCase));
        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);

        var merged = File.ReadAllText(temp.Path);
        Assert.Contains("\r\n", merged);
        Assert.Contains("\t<", merged);
        Assert.Contains("<Name>Matrix1</Name>", merged);
        Assert.Contains("<Name>TopStrip</Name>", merged);
        Assert.Contains("</LedStrip>\r\n\r\n\t\t<LedStrip>", merged);
        Assert.Contains("<FirstLedNumber>1</FirstLedNumber>", merged);
        Assert.Contains("<FirstLedNumber>4097</FirstLedNumber>", merged);
        Assert.Contains("<NumberOfLedsStrip1>4101</NumberOfLedsStrip1>", merged);
        Assert.Contains("<OutputName>Matrix1</OutputName>", merged);
        Assert.Contains("<OutputName>TopStrip</OutputName>", merged);
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

    [Fact]
    public void ApplyVirtualToyMerge_ShouldKeepExistingLedWizOrderAndAppendNewToys()
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
        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [
                new VirtualLedToyDefinition("Matrix1", 128, 32, "LED Strips 0", FirstLedNumber: 1, LedCount: 4096),
                new VirtualLedToyDefinition("FlasherTop", 16, 1, "LED Strips 0", FirstLedNumber: 4097, LedCount: 16),
                new VirtualLedToyDefinition("FlasherLeft", 16, 1, "LED Strips 0", FirstLedNumber: 4113, LedCount: 16),
            ],
            removeMissingManagedToys: false);

        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);
        var merged = File.ReadAllText(temp.Path);

        Assert.Contains("<OutputName>Matrix1</OutputName>", merged);
        Assert.Contains("<LedWizEquivalentOutputNumber>1</LedWizEquivalentOutputNumber>", merged);
        Assert.Contains("<OutputName>FlasherTop</OutputName>", merged);
        Assert.Contains("<LedWizEquivalentOutputNumber>4</LedWizEquivalentOutputNumber>", merged);
        Assert.Contains("<OutputName>FlasherLeft</OutputName>", merged);
        Assert.Contains("<LedWizEquivalentOutputNumber>7</LedWizEquivalentOutputNumber>", merged);
        Assert.Contains("</LedStrip>\r\n\r\n\t\t<LedStrip>", merged);
    }

    [Fact]
    public void ApplyVirtualToyMerge_ShouldNormalizeLiteralEscapedNewlinesBeforeFormatting()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\\n"
            + "<Cabinet><OutputControllers><VirtualLEDStripController><Name>LED Strips 0</Name><NumberOfLedsStrip1>4096</NumberOfLedsStrip1></VirtualLEDStripController></OutputControllers>"
            + "<Toys><LedStrip><Name>Matrix1</Name><Width>128</Width><Height>32</Height><FirstLedNumber>1</FirstLedNumber><OutputControllerName>LED Strips 0</OutputControllerName></LedStrip></Toys></Cabinet>";

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();
        var plan = service.BuildVirtualToyMergePlan(
            temp.Path,
            [
                new VirtualLedToyDefinition("Matrix1", 128, 32, "LED Strips 0", FirstLedNumber: 1, LedCount: 4096),
                new VirtualLedToyDefinition("FlasherTop", 16, 1, "LED Strips 0", FirstLedNumber: 4097, LedCount: 16),
            ],
            removeMissingManagedToys: false);

        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);
        var merged = File.ReadAllText(temp.Path);

        Assert.DoesNotContain("\\n<Cabinet>", merged, StringComparison.Ordinal);
        Assert.Contains("\r\n", merged);
        Assert.Contains("<Toys>", merged);
        Assert.Contains("</LedStrip>\r\n\r\n\t\t<LedStrip>", merged);
    }

    [Fact]
    public void FormatMergeSummary_ShouldCallOutManagedToyRemovals()
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
            [],
            removeMissingManagedToys: true);

        var summary = service.FormatMergeSummary(plan, dryRun: true);

        Assert.Contains("Removal warning: 1 managed virtual toy(s)", summary);
        Assert.Contains("VirtualA", summary);
        Assert.DoesNotContain("HardwareA", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyVirtualToyMerge_WhenManagedToyRemoved_ShouldDeleteLedWizOutputAndReindexFirstLedNumbers()
    {
        var xml = """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                  <NumberOfLedsStrip1>4152</NumberOfLedsStrip1>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip><Name>Matrix1</Name><Width>128</Width><Height>32</Height><FirstLedNumber>1</FirstLedNumber><OutputControllerName>LED Strips 0</OutputControllerName><LedCount>4096</LedCount></LedStrip>
                <LedStrip><Name>Strip1</Name><Width>32</Width><Height>1</Height><FirstLedNumber>1</FirstLedNumber><OutputControllerName>LED Strips 0</OutputControllerName><LedCount>32</LedCount></LedStrip>
                <LedStrip><Name>Strip3</Name><Width>1</Width><Height>16</Height><FirstLedNumber>4137</FirstLedNumber><OutputControllerName>LED Strips 0</OutputControllerName><LedCount>16</LedCount></LedStrip>
                <LedStrip><Name>Strip2</Name><Width>1</Width><Height>16</Height><FirstLedNumber>4129</FirstLedNumber><OutputControllerName>LED Strips 0</OutputControllerName><LedCount>16</LedCount></LedStrip>
                <LedWizEquivalent>
                  <Name>LedWizEquivalent 30</Name>
                  <LedWizNumber>30</LedWizNumber>
                  <Outputs>
                    <LedWizEquivalentOutput><OutputName>Matrix1</OutputName><LedWizEquivalentOutputNumber>1</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>
                    <LedWizEquivalentOutput><OutputName>Strip1</OutputName><LedWizEquivalentOutputNumber>4</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>
                    <LedWizEquivalentOutput><OutputName>Strip2</OutputName><LedWizEquivalentOutputNumber>7</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>
                    <LedWizEquivalentOutput><OutputName>Strip3</OutputName><LedWizEquivalentOutputNumber>10</LedWizEquivalentOutputNumber></LedWizEquivalentOutput>
                  </Outputs>
                </LedWizEquivalent>
              </Toys>
            </Cabinet>
            """;

        using var temp = new TempCabinetXml(xml);
        var service = new CabinetXmlService();
        var desired = new[]
        {
            new VirtualLedToyDefinition("Matrix1", 128, 32, "LED Strips 0", FirstLedNumber: 1, LedCount: 4096),
            new VirtualLedToyDefinition("Strip1", 32, 1, "LED Strips 0", FirstLedNumber: 1, LedCount: 32),
            new VirtualLedToyDefinition("Strip3", 1, 16, "LED Strips 0", FirstLedNumber: 4137, LedCount: 16),
        };
        var plan = service.BuildVirtualToyMergePlan(temp.Path, desired, removeMissingManagedToys: true);

        service.ApplyVirtualToyMerge(temp.Path, plan, dryRun: false);
        var merged = File.ReadAllText(temp.Path);

        Assert.DoesNotContain("<Name>Strip2</Name>", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("<OutputName>Strip2</OutputName>", merged, StringComparison.Ordinal);
        Assert.Contains("<Name>Strip3</Name>", merged);
        Assert.Contains("<FirstLedNumber>4129</FirstLedNumber>", merged);
    }

    private sealed class TempCabinetXml : IDisposable
    {
        public TempCabinetXml(string xml)
        {
            // Note: tests use a dedicated temp folder so backup-file assertions can be deterministic.
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
