using VirtualDofMatrix.App.Configuration;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class AppConfigurationStoreToyIniTests
{
    [Fact]
    public void Save_WhenConfigHasNoRoutingToys_WritesDefaultBackglassMatrixSection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var iniPath = Path.Combine(tempRoot, "toys.ini");
            var config = new VirtualDofMatrix.Core.AppConfig();

            var store = new AppConfigurationStore();
            store.Save(settingsPath, config);

            var iniContents = File.ReadAllText(iniPath);
            Assert.Contains("[toy:backglass-main]", iniContents);
            Assert.Contains("name = Matrix1", iniContents);
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
    public void Load_WhenSettingsFileMissing_StillCreatesToyIni()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var iniPath = Path.Combine(tempRoot, "toys.ini");

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);

            Assert.NotNull(loaded);
            Assert.True(File.Exists(iniPath));

            var iniContents = File.ReadAllText(iniPath);
            Assert.Contains("[toy:backglass-main]", iniContents);
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
    public void Load_WhenToyIniMissing_BootstrapsEnabledToysFromCabinetXmlVirtualLedStrips()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var cabinetPath = Path.Combine(tempRoot, "Cabinet.xml");
            var iniPath = Path.Combine(tempRoot, "toys.ini");

            File.WriteAllText(cabinetPath, """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>128</Width>
                  <Height>32</Height>
                  <LedStripArrangement>TopDownAlternateRightLeft</LedStripArrangement>
                  <FirstLedNumber>1</FirstLedNumber>
                  <LedCount>4096</LedCount>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>Strip2</Name>
                  <Width>1</Width>
                  <Height>16</Height>
                  <LedStripArrangement>TopDownAlternateRightLeft</LedStripArrangement>
                  <FirstLedNumber>4129</FirstLedNumber>
                  <LedCount>16</LedCount>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """);

            File.WriteAllText(settingsPath, $$"""
            {
              "settings": {
                "dofConfigFolderPath": "{{tempRoot.Replace("\\", "\\\\")}}",
                "cabinetXmlPath": "{{cabinetPath.Replace("\\", "\\\\")}}"
              },
              "routing": {
                "toyConfigIniPath": "toys.ini"
              }
            }
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);

            Assert.True(File.Exists(iniPath));
            Assert.Equal(2, loaded.Routing.Toys.Count);
            Assert.Contains(loaded.Routing.Toys, toy => toy.Id.Equals("backglass-main", StringComparison.OrdinalIgnoreCase)
                && toy.Enabled
                && toy.Kind.Equals("matrix", StringComparison.OrdinalIgnoreCase)
                && toy.Source.CanonicalStart == 0
                && toy.Source.Length == 4096);
            Assert.Contains(loaded.Routing.Toys, toy => toy.Id.Equals("strip2", StringComparison.OrdinalIgnoreCase)
                && toy.Enabled
                && toy.Kind.Equals("strip", StringComparison.OrdinalIgnoreCase)
                && toy.Mapping.Mode.Equals("ColumnMajor", StringComparison.OrdinalIgnoreCase)
                && toy.Source.CanonicalStart == 0
                && toy.Source.Length == 16
                && toy.Source.StripIndex is null
                && toy.Source.StripOffset is null
                && toy.Window.LockAspectRatio == false
                && toy.Window.BackgroundVisible == false
                && toy.Window.Width == 52
                && toy.Window.Height == 544);
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
    public void Load_WhenBootstrapImportsMixedOrientations_AssignsUniqueAxisAwareWindowPositions()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var cabinetPath = Path.Combine(tempRoot, "Cabinet.xml");

            File.WriteAllText(cabinetPath, """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>MatrixWideA</Name>
                  <Width>64</Width>
                  <Height>16</Height>
                  <FirstLedNumber>1</FirstLedNumber>
                  <LedCount>1024</LedCount>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>MatrixWideB</Name>
                  <Width>32</Width>
                  <Height>8</Height>
                  <FirstLedNumber>1025</FirstLedNumber>
                  <LedCount>256</LedCount>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>MatrixTallA</Name>
                  <Width>8</Width>
                  <Height>32</Height>
                  <FirstLedNumber>1281</FirstLedNumber>
                  <LedCount>256</LedCount>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
                <LedStrip>
                  <Name>MatrixTallB</Name>
                  <Width>4</Width>
                  <Height>24</Height>
                  <FirstLedNumber>1537</FirstLedNumber>
                  <LedCount>96</LedCount>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """);

            File.WriteAllText(settingsPath, $$"""
            {
              "window": {
                "left": 50,
                "top": 20,
                "width": 300,
                "height": 120
              },
              "settings": {
                "dofConfigFolderPath": "{{tempRoot.Replace("\\", "\\\\")}}",
                "cabinetXmlPath": "{{cabinetPath.Replace("\\", "\\\\")}}"
              },
              "routing": {
                "toyConfigIniPath": "toys.ini"
              }
            }
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);

            var toys = loaded.Routing.Toys;
            Assert.Equal(4, toys.Count);
            Assert.Equal(4, toys.Select(toy => $"{toy.Window.Left}:{toy.Window.Top}").Distinct(StringComparer.Ordinal).Count());

            var horizontal = toys.Where(toy => toy.Mapping.Width >= toy.Mapping.Height).ToArray();
            var vertical = toys.Where(toy => toy.Mapping.Height > toy.Mapping.Width).ToArray();

            Assert.Equal(2, horizontal.Length);
            Assert.Equal(2, vertical.Length);

            Assert.All(horizontal, toy => Assert.Equal(50, toy.Window.Left));
            Assert.True(horizontal[0].Window.Top < horizontal[1].Window.Top);

            Assert.All(vertical, toy => Assert.Equal(20, toy.Window.Top));
            Assert.True(vertical[0].Window.Left < vertical[1].Window.Left);
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
    public void Save_WhenRoutingContainsToys_PersistsToyDefinitionsOnlyToIniNotSettingsJson()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var iniPath = Path.Combine(tempRoot, "toys.ini");
            var config = new VirtualDofMatrix.Core.AppConfig
            {
                Routing = new VirtualDofMatrix.Core.RoutingConfig
                {
                    ToyConfigIniPath = "toys.ini",
                    Toys =
                    [
                        new VirtualDofMatrix.Core.ToyRouteConfig
                        {
                            Id = "backglass-main",
                            Name = "Matrix1",
                            Kind = "matrix",
                            Enabled = true,
                            Source = new VirtualDofMatrix.Core.ToySourceConfig { CanonicalStart = 0, Length = 4096 },
                            Mapping = new VirtualDofMatrix.Core.ToyMappingConfig { Width = 128, Height = 32, Mode = "TopDownAlternateRightLeft" },
                            OutputTargets = [new VirtualDofMatrix.Core.ToyAdapterTargetConfig { Adapter = "viewer", Enabled = true }],
                        },
                        new VirtualDofMatrix.Core.ToyRouteConfig
                        {
                            Id = "strip1",
                            Name = "Strip1",
                            Kind = "strip",
                            Enabled = true,
                            Source = new VirtualDofMatrix.Core.ToySourceConfig { CanonicalStart = 4096, Length = 32 },
                            Mapping = new VirtualDofMatrix.Core.ToyMappingConfig { Width = 32, Height = 1, Mode = "RowMajor" },
                            OutputTargets = [new VirtualDofMatrix.Core.ToyAdapterTargetConfig { Adapter = "viewer", Enabled = true }],
                        },
                    ],
                },
            };

            var store = new AppConfigurationStore();
            store.Save(settingsPath, config);

            var settingsJson = File.ReadAllText(settingsPath);
            var iniContents = File.ReadAllText(iniPath);

            Assert.Contains("\"toys\": []", settingsJson);
            Assert.DoesNotContain("\"id\": \"strip1\"", settingsJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[toy:backglass-main]", iniContents);
            Assert.Contains("[toy:strip1]", iniContents);
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

            // Note: toys.ini is canonical, so stale toy IDs from settings.json should be removed.
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

    [Fact]
    public void Load_WhenToyIniUsesEscapedNewlines_StillParsesAndPrunesStaleToys()
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

            var escapedIni = "[toy:backglass-main]\\nenabled = true\\nkind = matrix\\nwidth = 128\\nheight = 32\\nsourceLength = 4096\\noutputTargets = viewer\\n\\n[toy:flasher-5bulb]\\nenabled = false\\nkind = flasher\\nwidth = 5\\nheight = 1\\nsourceLength = 5\\noutputTargets = viewer\\n";
            File.WriteAllText(iniPath, escapedIni);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);

            Assert.Equal(2, loaded.Routing.Toys.Count);
            Assert.Contains(loaded.Routing.Toys, toy => toy.Id.Equals("backglass-main", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(loaded.Routing.Toys, toy => toy.Id.Equals("flasher-5bulb", StringComparison.OrdinalIgnoreCase));
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
    public void Load_AssignsAndNormalizesUniqueToyNames()
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
                  { "id": "backglass-main", "kind": "matrix", "enabled": true, "name": "Matrix1" },
                  { "id": "flasher-5bulb", "kind": "flasher", "enabled": true, "name": "Matrix1" },
                  { "id": "topper-main", "kind": "topper", "enabled": true }
                ]
              }
            }
            """);

            File.WriteAllText(iniPath, """
            [toy:backglass-main]
            name = Matrix1
            enabled = true
            kind = matrix
            width = 128
            height = 32
            sourceLength = 4096
            outputTargets = viewer

            [toy:flasher-5bulb]
            name = Matrix1
            enabled = true
            kind = flasher
            width = 5
            height = 1
            sourceLength = 5
            outputTargets = viewer

            [toy:topper-main]
            enabled = true
            kind = topper
            width = 64
            height = 8
            sourceLength = 512
            outputTargets = viewer
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);
            var names = loaded.Routing.Toys.Select(t => t.Name).ToArray();

            Assert.Equal(3, names.Length);
            Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains(names, name => name.Equals("Matrix1", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, name => name.Equals("Matrix2", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, name => name.Equals("Matrix3", StringComparison.OrdinalIgnoreCase));
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
    public void Load_WhenSourceStripIndexExceedsCompatibilityTarget_ClampsToStripSeven()
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
                "policy": {
                  "defaultStripLength": 1100
                }
              }
            }
            """);

            File.WriteAllText(iniPath, """
            [toy:strip-overflow]
            enabled = true
            kind = strip
            width = 8
            height = 1
            sourceLength = 8
            sourceStripIndex = 12
            sourceStripOffset = 4
            outputTargets = viewer
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);
            var toy = Assert.Single(loaded.Routing.Toys);

            Assert.Equal(7, toy.Source.StripIndex);
            Assert.Equal((7 * 1100) + 4, toy.Source.CanonicalStart);
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
    public void Load_WhenSourceStripIndexIsNegative_ClampsToZero()
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
                "policy": {
                  "defaultStripLength": 1100
                }
              }
            }
            """);

            File.WriteAllText(iniPath, """
            [toy:strip-negative]
            enabled = true
            kind = strip
            width = 8
            height = 1
            sourceLength = 8
            sourceStripIndex = -3
            sourceStripOffset = 5
            outputTargets = viewer
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);
            var toy = Assert.Single(loaded.Routing.Toys);

            Assert.Equal(0, toy.Source.StripIndex);
            Assert.Equal(5, toy.Source.CanonicalStart);
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
    public void Load_WhenStripToyUsesNonZeroCanonicalStartWithoutStripIndex_ResetsCanonicalStartToZero()
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
                "toyConfigIniPath": "toys.ini"
              }
            }
            """);

            File.WriteAllText(iniPath, """
            [toy:strip1]
            enabled = true
            kind = strip
            width = 32
            height = 1
            sourceCanonicalStart = 4096
            sourceLength = 32
            outputTargets = viewer
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);
            var toy = Assert.Single(loaded.Routing.Toys);

            Assert.Equal("strip", toy.Kind);
            Assert.Equal(0, toy.Source.CanonicalStart);
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
    public void Load_WhenCabinetToysDifferFromToyIni_AutoResyncsAndCreatesBackup()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var cabinetPath = Path.Combine(tempRoot, "Cabinet.xml");
            var iniPath = Path.Combine(tempRoot, "toys.ini");

            File.WriteAllText(cabinetPath, """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>64</Width>
                  <Height>16</Height>
                  <LedStripArrangement>TopDownAlternateRightLeft</LedStripArrangement>
                  <FirstLedNumber>1</FirstLedNumber>
                  <LedCount>1024</LedCount>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """);

            File.WriteAllText(settingsPath, $$"""
            {
              "settings": {
                "dofConfigFolderPath": "{{tempRoot.Replace("\\", "\\\\")}}",
                "cabinetXmlPath": "{{cabinetPath.Replace("\\", "\\\\")}}"
              },
              "routing": {
                "toyConfigIniPath": "toys.ini"
              }
            }
            """);

            File.WriteAllText(iniPath, """
            [toy:backglass-main]
            name = Matrix1
            kind = matrix
            enabled = true
            width = 32
            height = 8
            sourceCanonicalStart = 0
            sourceLength = 256
            outputTargets = viewer

            [toy:stale-toy]
            name = OldToy
            kind = matrix
            enabled = true
            width = 16
            height = 16
            sourceCanonicalStart = 256
            sourceLength = 256
            outputTargets = viewer
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);

            Assert.Single(loaded.Routing.Toys);
            var onlyToy = loaded.Routing.Toys[0];
            Assert.Equal("Matrix1", onlyToy.Name);
            Assert.Equal(64, onlyToy.Mapping.Width);
            Assert.Equal(16, onlyToy.Mapping.Height);
            Assert.Equal(1024, onlyToy.Source.Length);

            Assert.DoesNotContain("stale-toy", File.ReadAllText(iniPath), StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.EnumerateFiles(tempRoot, "toys.ini.backup_*", SearchOption.TopDirectoryOnly).Any());
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
    public void Load_WhenToyIniResyncs_PreservesExistingWindowAndRenderOptionsForMatchingToy()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vdm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var cabinetPath = Path.Combine(tempRoot, "Cabinet.xml");
            var iniPath = Path.Combine(tempRoot, "toys.ini");

            File.WriteAllText(cabinetPath, """
            <Cabinet>
              <OutputControllers>
                <VirtualLEDStripController>
                  <Name>LED Strips 0</Name>
                </VirtualLEDStripController>
              </OutputControllers>
              <Toys>
                <LedStrip>
                  <Name>Matrix1</Name>
                  <Width>128</Width>
                  <Height>32</Height>
                  <LedStripArrangement>TopDownAlternateRightLeft</LedStripArrangement>
                  <FirstLedNumber>1</FirstLedNumber>
                  <LedCount>4096</LedCount>
                  <OutputControllerName>LED Strips 0</OutputControllerName>
                </LedStrip>
              </Toys>
            </Cabinet>
            """);

            File.WriteAllText(settingsPath, $$"""
            {
              "settings": {
                "dofConfigFolderPath": "{{tempRoot.Replace("\\", "\\\\")}}",
                "cabinetXmlPath": "{{cabinetPath.Replace("\\", "\\\\")}}"
              },
              "routing": {
                "toyConfigIniPath": "toys.ini"
              }
            }
            """);

            File.WriteAllText(iniPath, """
            [toy:backglass-main]
            name = Matrix1
            kind = matrix
            enabled = false
            width = 32
            height = 8
            sourceCanonicalStart = 0
            sourceLength = 256
            windowLeft = 222
            renderBrightness = 0.65
            outputTargets = viewer
            """);

            var store = new AppConfigurationStore();
            var loaded = store.Load(settingsPath);

            var toy = Assert.Single(loaded.Routing.Toys);
            Assert.Equal("Matrix1", toy.Name);
            Assert.False(toy.Enabled);
            Assert.Equal(128, toy.Mapping.Width);
            Assert.Equal(32, toy.Mapping.Height);
            Assert.Equal(222, toy.Window.Left);
            Assert.Equal(0.65, toy.Render.Brightness, precision: 3);
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
