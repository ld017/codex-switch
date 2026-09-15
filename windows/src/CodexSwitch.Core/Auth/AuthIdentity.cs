using System.Security.Cryptography;
using System.Text;

namespace CodexSwitch.Core.Auth;

public enum AuthKind
{
    OAuth,
    ApiKey,
}

public sealed record AuthIdentity(
    AuthKind Kind,
    string? AccountId = null,
    string? Subject = null,
    string? UserId = null,
    string? Email = null,
    string? ApiKeyHash = null)
{
    public string Fingerprint()
    {
        var normalized = string.Join(
            "\n",
            Kind.ToString(),
            AccountId ?? string.Empty,
            Subject ?? string.Empty,
            UserId ?? string.Empty,
            Email ?? string.Empty,
            ApiKeyHash ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    public bool Matches(AuthIdentity replacement)
    {
        if (Kind != replacement.Kind)
        {
            return false;
        }

        if (Kind == AuthKind.ApiKey)
        {
            return !string.IsNullOrEmpty(ApiKeyHash)
                && string.Equals(ApiKeyHash, replacement.ApiKeyHash, StringComparison.Ordinal);
        }

        var matched = 0;
        foreach (var pair in Claims().Join(
                     replacement.Claims(),
                     left => left.Key,
                     right => right.Key,
                     (left, right) => (Current: left.Value, Replacement: right.Value)))
        {
            if (!string.Equals(pair.Current, pair.Replacement, StringComparison.Ordinal))
            {
                return false;
            }

            matched++;
        }

        return matched > 0;
    }

    private IEnumerable<KeyValuePair<string, string>> Claims()
    {
        if (!string.IsNullOrEmpty(AccountId)) yield return new("accountId", AccountId);
        if (!string.IsNullOrEmpty(Subject)) yield return new("subject", Subject);
        if (!string.IsNullOrEmpty(UserId)) yield return new("userId", UserId);
        if (!string.IsNullOrEmpty(Email)) yield return new("email", Email);
    }
}
