using VirtualDofMatrix.App.Configuration;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class InstallerSelectionRegistryHintServiceTests
{
    [Fact]
    public void TryResolveDofConfigFolder_ShouldReturnNull_WhenRegistryValueMissing()
    {
        var sut = new InstallerSelectionRegistryHintService(() => null);

        var resolved = sut.TryResolveDofConfigFolder();

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveDofConfigFolder_ShouldTrimRegistryValue_WhenPresent()
    {
        var sut = new InstallerSelectionRegistryHintService(() => "  D:\\DirectOutput\\Config  ");

        var resolved = sut.TryResolveDofConfigFolder();

        Assert.Equal(@"D:\DirectOutput\Config", resolved);
    }

    [Fact]
    public void TryResolveDofConfigFolder_ShouldReturnNull_WhenReaderThrows()
    {
        var sut = new InstallerSelectionRegistryHintService(
            () => throw new InvalidOperationException("simulated registry read failure"));

        var resolved = sut.TryResolveDofConfigFolder();

        Assert.Null(resolved);
    }
}
