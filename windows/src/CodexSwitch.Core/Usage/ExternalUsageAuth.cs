using System.Text.Json;
using CodexSwitch.Core.Auth;

namespace CodexSwitch.Core.Usage;

internal sealed class ExternalUsageAuth
{
    internal const string FileName = "usage-external-auth.json";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string AccessToken { get; init; } = string.Empty;
    public string ChatgptAccountId { get; init; } = string.Empty;

    internal static ExternalUsageAuth FromAuth(byte[] authJson)
    {
        var accountId = AuthBlob.Parse(authJson).GetIdentity().AccountId;
        using var document = JsonDocument.Parse(authJson);
        if (accountId is null || !document.RootElement.TryGetProperty("tokens", out var tokens))
            throw new UsageException("Authentication required: current Codex login has no ChatGPT account ID.");
        var accessToken = tokens.TryGetProperty("access_token", out var value) ? value.GetString()
            : tokens.TryGetProperty("accessToken", out value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new UsageException("Authentication required: current Codex login has no access token.");
        return new ExternalUsageAuth { AccessToken = accessToken, ChatgptAccountId = accountId };
    }
}
