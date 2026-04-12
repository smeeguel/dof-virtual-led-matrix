namespace VirtualDofMatrix.Core;

public static class PopperLaunchOptions
{
    public const string ShowVirtualLedToken = "ShowVirtualLED";
    public const string HideVirtualLedToken = "HideVirtualLED";
    private static readonly string[] ShowTokenAliases =
    [
        NormalizeToken(ShowVirtualLedToken),
        NormalizeToken("ShowLEDs"),
        NormalizeToken("ShowLed"),
        NormalizeToken("show-virtual-led"),
        NormalizeToken("show_virtual_led")
    ];

    private static readonly string[] HideTokenAliases =
    [
        NormalizeToken(HideVirtualLedToken),
        NormalizeToken("HideLEDs"),
        NormalizeToken("HideLed"),
        NormalizeToken("hide-virtual-led"),
        NormalizeToken("hide_virtual_led")
    ];

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
        // Note: Popper launch placeholders come in with mixed separators and naming styles,
        // so we normalize aggressively and then compare against the known compatibility aliases.
        var aliases = expectedToken.Equals(ShowVirtualLedToken, StringComparison.OrdinalIgnoreCase)
            ? ShowTokenAliases
            : HideTokenAliases;

        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var rawNormalized = NormalizeToken(raw);
            if (aliases.Contains(rawNormalized, StringComparer.Ordinal))
            {
                return true;
            }

            var tokens = raw.Split([' ', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                var normalizedToken = NormalizeToken(token);
                if (aliases.Contains(normalizedToken, StringComparer.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length];
        var position = 0;
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c))
            {
                continue;
            }

            buffer[position++] = char.ToUpperInvariant(c);
        }

        return new string(buffer[..position]);
    }
}
