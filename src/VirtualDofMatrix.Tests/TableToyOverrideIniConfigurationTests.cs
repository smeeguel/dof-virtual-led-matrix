using VirtualDofMatrix.App.Configuration;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class TableToyOverrideIniConfigurationTests
{
    [Fact]
    public void Load_WhenIniContainsUnknownFutureKeys_ParsesKnownFieldsWithoutFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var tableOverridePath = Path.Combine(tempRoot, "table-toy-overrides.ini");

            File.WriteAllText(settingsPath, """
            {
              "routing": {
                "tableOverrideIniPath": "table-toy-overrides.ini"
              }
            }
            """);

            File.WriteAllText(tableOverridePath, """
            [table:Test Table]
            toy:matrix-main.enabled = true
            toy:matrix-main.window.left = 100
            toy:matrix-main.futureOpacity = 0.5
            toy:matrix-topper.experimental.flag = yes
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);

            var tableOverride = Assert.Single(loaded.Routing.TableToyVisibilityOverrides);
            Assert.Equal("Test Table", tableOverride.TableKey);
            Assert.True(tableOverride.ToyOverrides.TryGetValue("matrix-main", out var matrixOverride));
            Assert.True(matrixOverride.Enabled.HasValue && matrixOverride.Enabled.Value);
            Assert.Equal(100d, matrixOverride.Window.Left.GetValueOrDefault());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Save_WhenIniContainsReservedWindowKeys_RoundTripsKnownFields()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var tableOverridePath = Path.Combine(tempRoot, "table-toy-overrides.ini");

            File.WriteAllText(settingsPath, """
            {
              "routing": {
                "tableOverrideIniPath": "table-toy-overrides.ini"
              }
            }
            """);

            File.WriteAllText(tableOverridePath, """
            [table:Future Geometry Table]
            toy:matrix-main.enabled = false
            toy:matrix-main.window.left = 110
            toy:matrix-main.window.top = 220
            toy:matrix-main.window.width = 1280
            toy:matrix-main.window.height = 320
            toy:matrix-main.futureBrightnessBias = 1.2
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);
            store.Save(settingsPath, loaded);

            var rewrittenIni = File.ReadAllText(tableOverridePath);
            Assert.Contains("toy:matrix-main.enabled = false", rewrittenIni);
            Assert.Contains("toy:matrix-main.window.left = 110", rewrittenIni);
            Assert.Contains("toy:matrix-main.window.top = 220", rewrittenIni);
            Assert.Contains("toy:matrix-main.window.width = 1280", rewrittenIni);
            Assert.Contains("toy:matrix-main.window.height = 320", rewrittenIni);
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
