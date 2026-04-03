namespace VirtualDofMatrix.Core;

public static class PopperLaunchOptions
{
    public const string ShowVirtualLedToken = "ShowVirtualLED";

    public static bool ContainsShowVirtualLedToken(IEnumerable<string> args)
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
                if (token.Equals(ShowVirtualLedToken, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
