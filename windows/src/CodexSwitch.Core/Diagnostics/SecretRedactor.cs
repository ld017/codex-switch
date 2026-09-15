using System.Text.RegularExpressions;

namespace CodexSwitch.Core.Diagnostics;

public static partial class SecretRedactor
{
    private const int MaximumLength = 2_000;

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = JsonSecretRegex().Replace(value, "$1<redacted>$3");
        redacted = BearerRegex().Replace(redacted, "$1<redacted>");
        redacted = new string(redacted.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return redacted.Length <= MaximumLength ? redacted : redacted[^MaximumLength..];
    }

    [GeneratedRegex("(\\\"(?:access_token|accessToken|refresh_token|refreshToken|id_token|idToken|OPENAI_API_KEY)\\\"\\s*:\\s*\\\")([^\\\"]*)(\\\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex("(Authorization\\s*:\\s*Bearer\\s+)[^\\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();
}
