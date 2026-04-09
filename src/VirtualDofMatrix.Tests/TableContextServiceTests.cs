using VirtualDofMatrix.App.Configuration;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class TableContextServiceTests
{
    [Fact]
    public void ParseExplicitArgs_ShouldSupportSwitchAndKeyValueForms()
    {
        var (tableName, romName) = TableContextService.ParseExplicitArgs([
            "--table",
            "Attack_from_Mars.vpx",
            "rom=afm_113b",
        ]);

        Assert.Equal("Attack from Mars", tableName);
        Assert.Equal("afm_113b", romName);
    }

    [Fact]
    public void ResolveCurrentContext_ShouldInferRomFromMappingsForExplicitTable()
    {
        var tempConfigDir = CreateTempDofConfigWithMappings(("Attack from Mars", "afm_113"));
        try
        {
            var service = new TableContextService(() => Array.Empty<string>());
            service.UpdateExplicitContext("Attack_from_Mars", null);

            var context = service.ResolveCurrentContext(tempConfigDir);

            Assert.Equal("Attack from Mars", context.TableName);
            Assert.Equal("afm_113", context.RomName);
            Assert.Equal("mapping", context.Source);
            Assert.Equal("medium", context.Confidence);
        }
        finally
        {
            Directory.Delete(tempConfigDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveCurrentContext_ShouldInferFromVpxCommandLineWhenExplicitIsMissing()
    {
        var tempConfigDir = CreateTempDofConfigWithMappings(("Baywatch", "baywatch"));
        try
        {
            var service = new TableContextService(() =>
            [
                "C:\\VPX\\VPinballX64.exe -play \"C:\\Tables\\Baywatch.vpx\"",
            ]);

            var context = service.ResolveCurrentContext(tempConfigDir);

            Assert.Equal("Baywatch", context.TableName);
            Assert.Equal("baywatch", context.RomName);
            Assert.Equal("mapping", context.Source);
        }
        finally
        {
            Directory.Delete(tempConfigDir, recursive: true);
        }
    }

    private static string CreateTempDofConfigWithMappings(params (string TableName, string RomName)[] mappings)
    {
        var root = Path.Combine(Path.GetTempPath(), $"vdm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var xml = "<TableNameMappings>" + string.Join(string.Empty,
            mappings.Select(x => $"<Mapping><TableName>{x.TableName}</TableName><RomName>{x.RomName}</RomName></Mapping>")) +
            "</TableNameMappings>";

        File.WriteAllText(Path.Combine(root, "tablemappings.xml"), xml);
        return root;
    }
}
