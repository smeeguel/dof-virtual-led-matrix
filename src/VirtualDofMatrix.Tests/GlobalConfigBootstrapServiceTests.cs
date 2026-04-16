using System.Xml.Linq;
using VirtualDofMatrix.App.Configuration;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class GlobalConfigBootstrapServiceTests
{
    [Fact]
    public void EnsureRequiredTableConfigPattern_ShouldCreateCanonicalFileWhenMissing()
    {
        using var temp = new TempConfigFolder();
        var service = new GlobalConfigBootstrapService();

        var result = service.EnsureRequiredTableConfigPattern(temp.Path);
        var globalConfigPath = Path.Combine(temp.Path, GlobalConfigBootstrapService.GlobalConfigFileName);

        Assert.Equal(GlobalConfigBootstrapStatus.Created, result.Status);
        Assert.True(File.Exists(globalConfigPath));

        var document = XDocument.Load(globalConfigPath);
        var pattern = document.Root?
            .Element("TableConfigFilePatterns")?
            .Elements("TableConfigFilePattern")
            .Select(x => x.Value.Trim())
            .ToArray();
        Assert.NotNull(pattern);
        Assert.Contains(GlobalConfigBootstrapService.RequiredTableConfigPattern, pattern!);
    }

    [Fact]
    public void EnsureRequiredTableConfigPattern_ShouldMergeRequiredEntryWithoutRemovingExistingPatterns()
    {
        using var temp = new TempConfigFolder();
        var globalConfigPath = Path.Combine(temp.Path, GlobalConfigBootstrapService.GlobalConfigFileName);
        File.WriteAllText(globalConfigPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <GlobalConfig>
              <TableConfigFilePatterns>
                <TableConfigFilePattern>{InstallDir}\Config\custom.ini</TableConfigFilePattern>
              </TableConfigFilePatterns>
            </GlobalConfig>
            """);

        var service = new GlobalConfigBootstrapService();
        var result = service.EnsureRequiredTableConfigPattern(temp.Path);

        Assert.Equal(GlobalConfigBootstrapStatus.Merged, result.Status);
        Assert.Single(Directory.GetFiles(temp.Path, $"{GlobalConfigBootstrapService.GlobalConfigFileName}.bak.*"));

        var mergedPatterns = XDocument.Load(globalConfigPath).Root?
            .Element("TableConfigFilePatterns")?
            .Elements("TableConfigFilePattern")
            .Select(x => x.Value.Trim())
            .ToArray();
        Assert.NotNull(mergedPatterns);
        Assert.Contains(@"{InstallDir}\Config\custom.ini", mergedPatterns!);
        Assert.Contains(GlobalConfigBootstrapService.RequiredTableConfigPattern, mergedPatterns!);
    }

    [Fact]
    public void EnsureRequiredTableConfigPattern_ShouldBeIdempotentWhenPatternAlreadyExists()
    {
        using var temp = new TempConfigFolder();
        var globalConfigPath = Path.Combine(temp.Path, GlobalConfigBootstrapService.GlobalConfigFileName);
        File.WriteAllText(globalConfigPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <GlobalConfig>
              <TableConfigFilePatterns>
                <TableConfigFilePattern>{InstallDir}\Config\directoutputconfig30.ini</TableConfigFilePattern>
              </TableConfigFilePatterns>
            </GlobalConfig>
            """);

        var service = new GlobalConfigBootstrapService();

        var first = service.EnsureRequiredTableConfigPattern(temp.Path);
        var second = service.EnsureRequiredTableConfigPattern(temp.Path);

        Assert.Equal(GlobalConfigBootstrapStatus.Unchanged, first.Status);
        Assert.Equal(GlobalConfigBootstrapStatus.Unchanged, second.Status);
        Assert.Empty(Directory.GetFiles(temp.Path, $"{GlobalConfigBootstrapService.GlobalConfigFileName}.bak.*"));
        var patternCount = XDocument.Load(globalConfigPath).Root?
            .Element("TableConfigFilePatterns")?
            .Elements("TableConfigFilePattern")
            .Count(x => string.Equals(x.Value.Trim(), GlobalConfigBootstrapService.RequiredTableConfigPattern, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, patternCount);
    }

    [Fact]
    public void EnsureRequiredTableConfigPattern_ShouldFailForInvalidRoot()
    {
        using var temp = new TempConfigFolder();
        var globalConfigPath = Path.Combine(temp.Path, GlobalConfigBootstrapService.GlobalConfigFileName);
        File.WriteAllText(globalConfigPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <NotGlobalConfig />
            """);

        var service = new GlobalConfigBootstrapService();
        var result = service.EnsureRequiredTableConfigPattern(temp.Path);

        Assert.Equal(GlobalConfigBootstrapStatus.Failed, result.Status);
        Assert.Contains("expected <GlobalConfig>", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempConfigFolder : IDisposable
    {
        public TempConfigFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vdm-global-config-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
