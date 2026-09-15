using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class OpenAiUsageConfigTests
{
    [Fact]
    public void Force_preserves_other_configuration_and_sets_top_level_provider_to_openai()
    {
        const string source = "model = \"gpt-5\"\r\nmodel_provider = \"sub2api\"\r\n[model_providers.sub2api]\r\nmodel_provider = \"nested\"\r\n";

        var result = OpenAiUsageConfig.Force(source);

        Assert.Equal(Provider.OpenAI, ProviderConfigEditor.Read(result));
        Assert.Contains("model_provider = \"nested\"", result, StringComparison.Ordinal);
        Assert.Contains("\r\n", result, StringComparison.Ordinal);
    }
}
