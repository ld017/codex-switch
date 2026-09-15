using System.Text;
using System.Text.Json;
using CodexSwitch.Core.Auth;

namespace CodexSwitch.Core.Tests.Auth;

public sealed class AuthBlobTests
{
    [Theory]
    [InlineData("access_token", "refresh_token")]
    [InlineData("accessToken", "refreshToken")]
    public void Parse_accepts_supported_oauth_token_shapes(string accessName, string refreshName)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["auth_mode"] = "chatgpt",
            ["tokens"] = new Dictionary<string, object?>
            {
                [accessName] = "access-value",
                [refreshName] = "refresh-value",
                ["account_id"] = "acct-1",
            },
        });

        var blob = AuthBlob.Parse(json);

        Assert.Equal("acct-1", blob.GetIdentity()!.AccountId);
    }

    [Fact]
    public void Parse_accepts_api_key_auth()
    {
        var blob = AuthBlob.Parse(Encoding.UTF8.GetBytes("{\"OPENAI_API_KEY\":\"sk-test-value\"}"));

        Assert.Equal(AuthKind.ApiKey, blob.GetIdentity()!.Kind);
    }

    [Fact]
    public void Parse_rejects_missing_refresh_token()
    {
        var data = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"access\"}}");

        Assert.Throws<AuthBlobException>(() => AuthBlob.Parse(data));
    }

    [Fact]
    public void IdentityMatches_accepts_same_account_when_replacement_adds_id_token()
    {
        var original = AuthBlob.Parse(TestAuth.OAuth("access-1", "refresh-1", "acct-1"));
        var replacement = AuthBlob.Parse(TestAuth.OAuth("access-2", "refresh-2", "acct-1", TestAuth.IdToken("user-1", "mail@example.com")));

        Assert.True(original.IdentityMatches(replacement));
    }

    [Fact]
    public void IdentityMatches_rejects_different_account()
    {
        var original = AuthBlob.Parse(TestAuth.OAuth("access-1", "refresh-1", "acct-1"));
        var replacement = AuthBlob.Parse(TestAuth.OAuth("access-2", "refresh-2", "acct-2"));

        Assert.False(original.IdentityMatches(replacement));
    }
}

internal static class TestAuth
{
    public static byte[] OAuth(string access, string refresh, string accountId, string? idToken = null)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["auth_mode"] = "chatgpt",
            ["tokens"] = new Dictionary<string, object?>
            {
                ["access_token"] = access,
                ["refresh_token"] = refresh,
                ["account_id"] = accountId,
                ["id_token"] = idToken,
            },
        });
    }

    public static string IdToken(string subject, string email)
    {
        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { sub = subject, email }));
        return $"{header}.{payload}.signature";
    }
}
