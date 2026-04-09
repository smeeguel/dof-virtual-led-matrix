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

        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, xml);
            var service = new CabinetXmlService();

            var inventory = service.GetToyInventory(tempPath);

            Assert.Contains(inventory.VirtualToys, x => x.Name == "VirtualMatrix");
            Assert.Contains(inventory.HardwareToys, x => x.Name == "HardwareStrip");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
