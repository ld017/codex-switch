using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageErrorClassifierTests
{
    [Theory]
    [InlineData("401 Unauthorized: token_revoked")]
    [InlineData("authentication required; please log in")]
    [InlineData("invalid_grant: refresh token expired")]
    public void IsAuthenticationRequired_recognizes_invalid_login(string message)
    {
        Assert.True(UsageErrorClassifier.IsAuthenticationRequired(message));
    }

    [Fact]
    public void IsAuthenticationRequired_does_not_misclassify_network_failure()
    {
        Assert.False(UsageErrorClassifier.IsAuthenticationRequired("connection timed out"));
    }
}
