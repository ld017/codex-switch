using System.Text;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class ProviderSwitchServiceTests
{
    [Fact]
    public async Task SwitchAsync_changes_provider_after_successful_doctor_validation()
    {
        using var directory = new TestDirectory();
        var configPath = directory.File("config.toml");
        await File.WriteAllTextAsync(configPath, CompleteConfig("openai"), Encoding.UTF8);
        var runner = new StubProcessRunner(new ProcessResult(0, "{\"checks\":{\"config.load\":{\"status\":\"ok\"}}}", string.Empty));
        var service = CreateService(directory, configPath, runner);

        var result = await service.SwitchAsync(Provider.Sub2Api, default);

        Assert.Equal(Provider.OpenAI, result.Previous);
        Assert.Equal(Provider.Sub2Api, result.Current);
        Assert.Equal(Provider.Sub2Api, ProviderConfigEditor.Read(await File.ReadAllTextAsync(configPath)));
        Assert.True(File.Exists(result.BackupPath));
    }

    [Fact]
    public async Task SwitchAsync_restores_original_bytes_when_doctor_rejects_config()
    {
        using var directory = new TestDirectory();
        var configPath = directory.File("config.toml");
        var original = Encoding.UTF8.GetBytes(CompleteConfig("openai").Replace("\n", "\r\n", StringComparison.Ordinal));
        await File.WriteAllBytesAsync(configPath, original);
        var runner = new StubProcessRunner(new ProcessResult(1, "{\"checks\":{\"config.load\":{\"status\":\"error\"}}}", "bad config"));
        var service = CreateService(directory, configPath, runner);

        var error = await Assert.ThrowsAsync<ProviderSwitchException>(() => service.SwitchAsync(Provider.Sub2Api, default));

        Assert.Equal(ProviderSwitchStage.Validation, error.Stage);
        Assert.Equal(original, await File.ReadAllBytesAsync(configPath));
    }

    [Fact]
    public async Task SwitchAsync_rejects_incomplete_sub2api_configuration_before_writing()
    {
        using var directory = new TestDirectory();
        var configPath = directory.File("config.toml");
        const string original = "model = \"gpt-5\"\nmodel_provider = \"openai\"\n";
        await File.WriteAllTextAsync(configPath, original);
        var runner = new StubProcessRunner(new ProcessResult(0, string.Empty, string.Empty));
        var service = CreateService(directory, configPath, runner);

        var error = await Assert.ThrowsAsync<ProviderSwitchException>(() => service.SwitchAsync(Provider.Sub2Api, default));

        Assert.Equal(ProviderSwitchStage.Prerequisites, error.Stage);
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        Assert.Equal(0, runner.CallCount);
    }

    private static ProviderSwitchService CreateService(TestDirectory directory, string configPath, IProcessRunner runner)
    {
        var files = new AtomicFileStore();
        return new ProviderSwitchService(
            configPath,
            "codex.exe",
            files,
            new BackupManager(directory.File("backups"), 10),
            runner);
    }

    private static string CompleteConfig(string provider) => $"model = \"gpt-5\"\nmodel_provider = \"{provider}\"\n[model_providers.sub2api]\nbase_url = \"http://127.0.0.1:8080\"\n[model_providers.sub2api.auth]\nenv_key = \"SUB2API_KEY\"\n";

    private sealed class StubProcessRunner(ProcessResult result) : IProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessSpec specification, CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.Equal("codex.exe", specification.FileName);
            Assert.Equal(["doctor", "--json"], specification.Arguments);
            return Task.FromResult(result);
        }
    }
}
