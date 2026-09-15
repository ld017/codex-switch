using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexSwitch.Core.Auth;

public sealed class AuthBlobException : Exception
{
    public AuthBlobException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class AuthBlob
{
    private readonly byte[] _data;
    private readonly AuthIdentity _identity;

    private AuthBlob(byte[] data, AuthIdentity identity)
    {
        _data = data;
        _identity = identity;
    }

    public static AuthBlob Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            using var document = JsonDocument.Parse(data.ToArray());
            var root = document.RootElement;
            if (TryString(root, "OPENAI_API_KEY", out var apiKey))
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
                return new AuthBlob(data.ToArray(), new AuthIdentity(AuthKind.ApiKey, ApiKeyHash: hash));
            }

            if (!root.TryGetProperty("tokens", out var tokens)
                || !TryEitherString(tokens, "access_token", "accessToken", out _)
                || !TryEitherString(tokens, "refresh_token", "refreshToken", out _))
            {
                throw new AuthBlobException("auth.json does not contain usable credentials.");
            }

            TryEitherString(tokens, "account_id", "accountId", out var accountId);
            TryEitherString(tokens, "id_token", "idToken", out var idToken);
            var claims = ParseIdToken(idToken);
            var identity = new AuthIdentity(
                AuthKind.OAuth,
                EmptyToNull(accountId),
                claims.Subject,
                claims.UserId,
                claims.Email);
            if (identity.ClaimsAreEmpty())
            {
                throw new AuthBlobException("auth.json does not contain a stable account identity.");
            }

            return new AuthBlob(data.ToArray(), identity);
        }
        catch (AuthBlobException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new AuthBlobException("auth.json is not valid JSON.", error);
        }
    }

    public AuthIdentity GetIdentity() => _identity;

    public bool IdentityMatches(AuthBlob replacement) => _identity.Matches(replacement._identity);

    public byte[] ToBytes() => _data.ToArray();

    private static (string? Subject, string? UserId, string? Email) ParseIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return default;
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return default;
        }

        try
        {
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var root = document.RootElement;
            TryString(root, "sub", out var subject);
            TryString(root, "https://api.openai.com/user_id", out var userId);
            TryString(root, "email", out var email);
            return (EmptyToNull(subject), EmptyToNull(userId), EmptyToNull(email));
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return default;
        }
    }

    private static bool TryEitherString(JsonElement element, string snakeCase, string camelCase, out string value)
        => TryString(element, snakeCase, out value) || TryString(element, camelCase, out value);

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

internal static class AuthIdentityExtensions
{
    public static bool ClaimsAreEmpty(this AuthIdentity identity)
        => identity.Kind == AuthKind.OAuth
           && identity.AccountId is null
           && identity.Subject is null
           && identity.UserId is null
           && identity.Email is null;
}
