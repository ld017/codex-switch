using CodexSwitch.Core.Diagnostics;

namespace CodexSwitch.Core.Tests.Diagnostics;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_removes_json_tokens_bearer_values_and_control_characters()
    {
        const string input = "{\"access_token\":\"secret-a\",\"refreshToken\":\"secret-r\"} Authorization: Bearer secret-b\r\nforged";

        var result = SecretRedactor.Redact(input);

        Assert.DoesNotContain("secret-a", result);
        Assert.DoesNotContain("secret-r", result);
        Assert.DoesNotContain("secret-b", result);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\n', result);
    }

    [Fact]
    public void Redact_bounds_output_to_two_thousand_characters()
    {
        var result = SecretRedactor.Redact(new string('a', 3_000));

        Assert.True(result.Length <= 2_000);
    }
}
