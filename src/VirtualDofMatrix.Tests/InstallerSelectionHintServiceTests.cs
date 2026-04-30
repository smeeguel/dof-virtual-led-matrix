using VirtualDofMatrix.App.Configuration;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class InstallerSelectionHintServiceTests
{
    [Fact]
    public void TryResolveDofConfigFolder_ShouldReturnNull_WhenHintFileMissing()
    {
        var tempRoot = CreateTempDirectory();
        var sut = new InstallerSelectionHintService();

        var resolved = sut.TryResolveDofConfigFolder(tempRoot);

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveDofConfigFolder_ShouldReadDofConfigPathFromInstallerHint()
    {
        var tempRoot = CreateTempDirectory();
        var hintPath = Path.Combine(tempRoot, InstallerSelectionHintService.HintFileName);
        File.WriteAllText(hintPath, "{\n  \"dofConfigPath\": \"D:\\\\Pinball\\\\DirectOutput\\\\Config\"\n}");
        var sut = new InstallerSelectionHintService();

        var resolved = sut.TryResolveDofConfigFolder(tempRoot);

        Assert.Equal(@"D:\Pinball\DirectOutput\Config", resolved);
    }

    [Fact]
    public void TryResolveDofConfigFolder_ShouldReturnNull_WhenHintFileIsMalformed()
    {
        var tempRoot = CreateTempDirectory();
        var hintPath = Path.Combine(tempRoot, InstallerSelectionHintService.HintFileName);
        File.WriteAllText(hintPath, "{ this-is-not-json");
        var sut = new InstallerSelectionHintService();

        var resolved = sut.TryResolveDofConfigFolder(tempRoot);

        Assert.Null(resolved);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "VirtualDofMatrix.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
