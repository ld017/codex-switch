using CodexSwitch.Core.Configuration;

namespace CodexSwitch.Core.Tests.Configuration;

public sealed class ProviderConfigEditorTests
{
    [Fact]
    public void Read_defaults_to_openai_when_model_exists_without_provider()
    {
        Assert.Equal(Provider.OpenAI, ProviderConfigEditor.Read("model = \"gpt-5\"\n"));
    }

    [Fact]
    public void Read_rejects_missing_model_and_provider()
    {
        Assert.Throws<ProviderConfigException>(() => ProviderConfigEditor.Read("sandbox_mode = \"workspace-write\"\n"));
    }

    [Fact]
    public void Read_rejects_duplicate_top_level_provider()
    {
        const string source = "model_provider = \"openai\"\nmodel_provider = \"sub2api\"\nmodel = \"gpt-5\"\n";

        Assert.Throws<ProviderConfigException>(() => ProviderConfigEditor.Read(source));
    }

    [Fact]
    public void Read_rejects_unknown_provider()
    {
        const string source = "model = \"gpt-5\"\nmodel_provider = \"other\"\n";

        Assert.Throws<ProviderConfigException>(() => ProviderConfigEditor.Read(source));
    }

    [Fact]
    public void Replace_preserves_crlf_comment_bom_and_nested_provider()
    {
        const string source = "\uFEFFmodel = \"gpt-5\"\r\nmodel_provider = \"openai\" # live\r\n\r\n[profiles.demo]\r\nmodel_provider = \"leave-me\"\r\n";

        var result = ProviderConfigEditor.Replace(source, Provider.Sub2Api);

        Assert.Equal("\uFEFFmodel = \"gpt-5\"\r\nmodel_provider = \"sub2api\" # live\r\n\r\n[profiles.demo]\r\nmodel_provider = \"leave-me\"\r\n", result);
    }

    [Fact]
    public void Replace_inserts_provider_after_top_level_model_using_existing_line_ending()
    {
        const string source = "model = \"gpt-5\"\r\n[features]\r\njs_repl = true\r\n";

        var result = ProviderConfigEditor.Replace(source, Provider.Sub2Api);

        Assert.Equal("model = \"gpt-5\"\r\nmodel_provider = \"sub2api\"\r\n[features]\r\njs_repl = true\r\n", result);
    }

    [Fact]
    public void Replace_does_not_treat_nested_provider_as_top_level()
    {
        const string source = "model = \"gpt-5\"\n[profiles.demo]\nmodel_provider = \"sub2api\"\n";

        var result = ProviderConfigEditor.Replace(source, Provider.OpenAI);

        Assert.Equal("model = \"gpt-5\"\nmodel_provider = \"openai\"\n[profiles.demo]\nmodel_provider = \"sub2api\"\n", result);
    }

    [Fact]
    public void HasSub2ApiConfiguration_requires_provider_and_auth_tables()
    {
        const string complete = "[model_providers.sub2api]\nbase_url = \"http://127.0.0.1:8080\"\n[model_providers.sub2api.auth]\nenv_key = \"SUB2API_KEY\"\n";
        const string incomplete = "[model_providers.sub2api]\nbase_url = \"http://127.0.0.1:8080\"\n";

        Assert.True(ProviderConfigEditor.HasSub2ApiConfiguration(complete));
        Assert.False(ProviderConfigEditor.HasSub2ApiConfiguration(incomplete));
    }
}
