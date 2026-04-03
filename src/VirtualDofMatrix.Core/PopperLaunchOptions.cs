namespace VirtualDofMatrix.Core;

public static class PopperLaunchOptions
{
    public const string ShowVirtualLedToken = "ShowVirtualLED";
    public const string HideVirtualLedToken = "HideVirtualLED";

    public static bool ContainsShowVirtualLedToken(IEnumerable<string> args)
        => ContainsToken(args, ShowVirtualLedToken);

    public static bool ContainsHideVirtualLedToken(IEnumerable<string> args)
        => ContainsToken(args, HideVirtualLedToken);

    public static bool ResolveTableLaunchVisibility(IEnumerable<string> args, bool defaultVisible = false)
    {
        var hasHide = ContainsHideVirtualLedToken(args);
        if (hasHide)
        {
            return false;
        }

        var hasShow = ContainsShowVirtualLedToken(args);
        if (hasShow)
        {
            return true;
        }

        return defaultVisible;
    }

    private static bool ContainsToken(IEnumerable<string> args, string expectedToken)
    {
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var tokens = raw.Split([' ', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                if (token.Equals(expectedToken, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
