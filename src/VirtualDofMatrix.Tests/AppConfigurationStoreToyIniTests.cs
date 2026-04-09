using VirtualDofMatrix.App.Configuration;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class AppConfigurationStoreToyIniTests
{
    [Fact]
    public void Load_WhenToyIniExists_PrunesStaleRoutingToysNotInIni()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var iniPath = Path.Combine(tempRoot, "toys.ini");

            File.WriteAllText(settingsPath, """
            {
              "routing": {
                "toyConfigIniPath": "toys.ini",
                "toys": [
                  { "id": "backglass-main", "kind": "matrix", "enabled": true },
                  { "id": "flasher-5bulb", "kind": "flasher", "enabled": false },
                  { "id": "Matrix1", "kind": "matrix", "enabled": true },
                  { "id": "Flasher5", "kind": "flasher", "enabled": true }
                ]
              }
            }
            """);

            File.WriteAllText(iniPath, """
            [toy:backglass-main]
            enabled = true
            kind = matrix
            width = 128
            height = 32
            mapping = TopDownAlternateRightLeft
            sourceCanonicalStart = 0
            sourceLength = 4096
            outputTargets = viewer

            [toy:flasher-5bulb]
            enabled = false
            kind = flasher
            width = 5
            height = 1
            mapping = RowMajor
            sourceCanonicalStart = 0
            sourceLength = 5
            outputTargets = viewer
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);

            // Conversational note: toys.ini is canonical, so stale toy IDs from settings.json should be removed.
            Assert.Equal(2, loaded.Routing.Toys.Count);
            Assert.Contains(loaded.Routing.Toys, toy => toy.Id.Equals("backglass-main", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(loaded.Routing.Toys, toy => toy.Id.Equals("flasher-5bulb", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(loaded.Routing.Toys, toy => toy.Id.Equals("Matrix1", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(loaded.Routing.Toys, toy => toy.Id.Equals("Flasher5", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
