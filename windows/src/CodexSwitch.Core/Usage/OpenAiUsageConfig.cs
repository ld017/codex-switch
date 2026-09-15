using CodexSwitch.Core.Configuration;

namespace CodexSwitch.Core.Usage;

public static class OpenAiUsageConfig
{
    public static string Force(string source) => ProviderConfigEditor.Replace(source, Provider.OpenAI);
}
