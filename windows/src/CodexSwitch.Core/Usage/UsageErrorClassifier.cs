namespace CodexSwitch.Core.Usage;

public static class UsageErrorClassifier
{
    private static readonly string[] AuthenticationTerms =
    [
        "401 unauthorized",
        "authentication required",
        "login required",
        "log in",
        "token_revoked",
        "invalid_grant",
        "refresh token",
        "token expired",
    ];

    public static bool IsAuthenticationRequired(string message)
        => AuthenticationTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
}
