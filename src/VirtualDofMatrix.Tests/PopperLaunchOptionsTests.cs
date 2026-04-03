using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public class PopperLaunchOptionsTests
{
    [Fact]
    public void ContainsShowVirtualLedToken_ReturnsTrue_ForExactToken()
    {
        var args = new[] { "ShowVirtualLED" };

        var result = PopperLaunchOptions.ContainsShowVirtualLedToken(args);

        Assert.True(result);
    }

    [Fact]
    public void ContainsShowVirtualLedToken_ReturnsTrue_WhenTokenIsEmbeddedInCustomField()
    {
        var args = new[] { "foo,showvirtualled;bar" };

        var result = PopperLaunchOptions.ContainsShowVirtualLedToken(args);

        Assert.True(result);
    }

    [Fact]
    public void ContainsShowVirtualLedToken_ReturnsFalse_WhenNotPresent()
    {
        var args = new[] { "customA", "customB" };

        var result = PopperLaunchOptions.ContainsShowVirtualLedToken(args);

        Assert.False(result);
    }

    [Fact]
    public void ContainsHideVirtualLedToken_ReturnsTrue_ForExactToken()
    {
        var args = new[] { "HideVirtualLED" };

        var result = PopperLaunchOptions.ContainsHideVirtualLedToken(args);

        Assert.True(result);
    }

    [Fact]
    public void ResolveTableLaunchVisibility_DefaultHidden_ShowsWhenShowTokenIsPresent()
    {
        var visible = PopperLaunchOptions.ResolveTableLaunchVisibility(["ShowVirtualLED"], defaultVisible: false);

        Assert.True(visible);
    }

    [Fact]
    public void ResolveTableLaunchVisibility_DefaultVisible_HidesWhenHideTokenIsPresent()
    {
        var visible = PopperLaunchOptions.ResolveTableLaunchVisibility(["HideVirtualLED"], defaultVisible: true);

        Assert.False(visible);
    }
}
