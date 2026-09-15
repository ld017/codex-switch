# Codex Switch Windows Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an installable Windows 10/11 x64 Codex Switch tray application with safe provider switching, DPAPI-protected OpenAI account management, and per-account usage monitoring.

**Architecture:** Keep the Swift/macOS application intact and add an independent .NET 8 solution under `windows/`. Put deterministic configuration, account, RPC, and transaction behavior in a UI-free core assembly; keep WinForms, process discovery, DPAPI, registry startup, and desktop launch behind narrow Windows adapters. Package a self-contained `win-x64` build with Inno Setup.

**Tech Stack:** C# 12, .NET 8 LTS, WinForms, `System.Text.Json`, Windows DPAPI, xUnit, Inno Setup 6, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-15-windows-support-design.md`

## Global Constraints

- Preserve the existing macOS Swift application and `Package.swift` behavior.
- Support Windows 10/11 x64 only in the first Windows release.
- Publish self-contained so the installed app requires no preinstalled .NET runtime.
- Store managed auth blobs only after DPAPI `CurrentUser` encryption; never log tokens or raw auth/RPC payloads.
- Modify only the top-level `model_provider`; preserve BOM, line endings, comments, indentation, and unrelated TOML.
- Keep ten provider backups and verify byte-identical restoration on failure.
- Account switching is allowed only when the active provider is OpenAI.
- Usage queries run in isolated temporary `CODEX_HOME` directories forced to the OpenAI provider.
- Provider/account mutations are serialized and require a visible warning before restarting Codex.
- Do not automatically publish a GitHub Release.

## File Map

- `windows/Directory.Build.props`: nullable, warnings, language version, deterministic build defaults.
- `windows/CodexSwitch.sln`: solution containing app, core, and test projects.
- `windows/src/CodexSwitch.Core/Configuration/*`: provider parsing and byte-preserving edit decisions.
- `windows/src/CodexSwitch.Core/Files/*`: atomic replacement, backup retention, and rollback.
- `windows/src/CodexSwitch.Core/Auth/*`: auth validation, identity comparison, protection abstraction, and profile persistence.
- `windows/src/CodexSwitch.Core/Codex/*`: CLI discovery, process runner, doctor response, login, provider/account switch orchestration.
- `windows/src/CodexSwitch.Core/Usage/*`: JSON-RPC client, rate-limit models, isolated-home fetcher, and cache.
- `windows/src/CodexSwitch.Core/Diagnostics/SecretRedactor.cs`: bounded secret-safe diagnostic text.
- `windows/src/CodexSwitch.App/Windows/*`: DPAPI, process lifecycle, AppUserModelID launcher, ACL, and registry startup adapters.
- `windows/src/CodexSwitch.Core/Presentation/*`: immutable tray menu projection and operation coordination.
- `windows/src/CodexSwitch.App/Tray/*`: WinForms tray context and menu binding.
- `windows/src/CodexSwitch.App/Accounts/*`: login prompt and account management form.
- `windows/tests/CodexSwitch.Core.Tests/*`: deterministic unit tests.
- `windows/tests/CodexSwitch.IntegrationTests/*`: fake-CLI process tests.
- `windows/installer/CodexSwitch.iss`: per-user installer and startup task.
- `windows/scripts/build-installer.ps1`: restore, verify, publish, and package entry point.
- `.github/workflows/windows.yml`: Windows CI and installer artifact.

---

### Task 1: Solution Skeleton and Byte-Preserving Provider Editor

**Files:**
- Create: `windows/Directory.Build.props`
- Create: `windows/CodexSwitch.sln`
- Create: `windows/src/CodexSwitch.Core/CodexSwitch.Core.csproj`
- Create: `windows/src/CodexSwitch.Core/Configuration/Provider.cs`
- Create: `windows/src/CodexSwitch.Core/Configuration/ProviderConfigEditor.cs`
- Create: `windows/src/CodexSwitch.Core/Configuration/ProviderConfigException.cs`
- Create: `windows/tests/CodexSwitch.Core.Tests/CodexSwitch.Core.Tests.csproj`
- Create: `windows/tests/CodexSwitch.Core.Tests/Configuration/ProviderConfigEditorTests.cs`

**Interfaces:**
- Produces: `enum Provider { OpenAI, Sub2Api }`.
- Produces: `Provider ProviderConfigEditor.Read(string source)`.
- Produces: `string ProviderConfigEditor.Replace(string source, Provider target)`.
- Produces: `bool ProviderConfigEditor.HasSub2ApiConfiguration(string source)`.

- [ ] **Step 1: Create the solution/project files and write failing provider-editor tests**

Use `net8.0` for core/tests, enable nullable and implicit usings, and reference xUnit plus `Microsoft.NET.Test.Sdk`. Add tests with concrete inputs for default OpenAI, replacement, insertion, duplicate top-level entries, ignored nested entries, CRLF, comments, and BOM:

```powershell
dotnet new sln --name CodexSwitch --output windows
dotnet new classlib --name CodexSwitch.Core --output windows/src/CodexSwitch.Core --framework net8.0
dotnet new xunit --name CodexSwitch.Core.Tests --output windows/tests/CodexSwitch.Core.Tests --framework net8.0
dotnet sln windows/CodexSwitch.sln add windows/src/CodexSwitch.Core/CodexSwitch.Core.csproj windows/tests/CodexSwitch.Core.Tests/CodexSwitch.Core.Tests.csproj
dotnet add windows/tests/CodexSwitch.Core.Tests/CodexSwitch.Core.Tests.csproj reference windows/src/CodexSwitch.Core/CodexSwitch.Core.csproj
```

```csharp
[Fact]
public void Replace_preserves_crlf_comment_and_nested_provider()
{
    var source = "\uFEFFmodel = \"gpt-5\"\r\nmodel_provider = \"openai\" # live\r\n\r\n[profiles.demo]\r\nmodel_provider = \"leave-me\"\r\n";

    var result = ProviderConfigEditor.Replace(source, Provider.Sub2Api);

    Assert.Equal("\uFEFFmodel = \"gpt-5\"\r\nmodel_provider = \"sub2api\" # live\r\n\r\n[profiles.demo]\r\nmodel_provider = \"leave-me\"\r\n", result);
}

[Fact]
public void Read_rejects_duplicate_top_level_provider()
{
    var source = "model_provider = \"openai\"\nmodel_provider = \"sub2api\"\nmodel = \"gpt-5\"\n";
    Assert.Throws<ProviderConfigException>(() => ProviderConfigEditor.Read(source));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test windows/tests/CodexSwitch.Core.Tests/CodexSwitch.Core.Tests.csproj --filter FullyQualifiedName~ProviderConfigEditorTests`

Expected: compilation fails because `ProviderConfigEditor` and related types do not exist.

- [ ] **Step 3: Implement the minimal editor**

Implement a line scanner that treats only lines before the first TOML table header as top level. Preserve each line terminator and the original prefix/suffix around the quoted value. Strip an initial BOM only for matching, not output. Map missing `model_provider` plus a top-level `model` to OpenAI; insert a missing provider directly after the model line. Reject duplicates and unknown provider strings.

```csharp
public static class ProviderConfigEditor
{
    public static Provider Read(string source);
    public static string Replace(string source, Provider target);
    public static bool HasSub2ApiConfiguration(string source);
}
```

- [ ] **Step 4: Run the focused and complete test projects and verify GREEN**

Run: `dotnet test windows/tests/CodexSwitch.Core.Tests/CodexSwitch.Core.Tests.csproj`

Expected: all tests pass with zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add windows/Directory.Build.props windows/CodexSwitch.sln windows/src/CodexSwitch.Core windows/tests/CodexSwitch.Core.Tests
git commit -m "feat(windows): add provider configuration core"
```

### Task 2: Atomic Files, Backups, Doctor Validation, and Provider Transaction

**Files:**
- Create: `windows/src/CodexSwitch.Core/Files/IAtomicFileStore.cs`
- Create: `windows/src/CodexSwitch.Core/Files/AtomicFileStore.cs`
- Create: `windows/src/CodexSwitch.Core/Files/BackupManager.cs`
- Create: `windows/src/CodexSwitch.Core/Codex/IProcessRunner.cs`
- Create: `windows/src/CodexSwitch.Core/Codex/ProcessResult.cs`
- Create: `windows/src/CodexSwitch.Core/Codex/DoctorReport.cs`
- Create: `windows/src/CodexSwitch.Core/Codex/ProviderSwitchService.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Files/AtomicFileStoreTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Files/BackupManagerTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Codex/DoctorReportTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Codex/ProviderSwitchServiceTests.cs`

**Interfaces:**
- Consumes: `ProviderConfigEditor.Read`, `Replace`, and `HasSub2ApiConfiguration` from Task 1.
- Produces: `Task<ProviderSwitchResult> ProviderSwitchService.SwitchAsync(Provider target, CancellationToken cancellationToken)`.
- Produces: `Task<ProcessResult> IProcessRunner.RunAsync(ProcessSpec spec, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write failing atomic replace and exact rollback tests**

```csharp
[Fact]
public async Task SwitchAsync_restores_original_bytes_when_doctor_rejects_config()
{
    var original = Encoding.UTF8.GetBytes("model = \"gpt-5\"\r\nmodel_provider = \"openai\"\r\n");
    var fixture = new ProviderSwitchFixture(original) { DoctorStatus = "error" };

    await Assert.ThrowsAsync<ProviderSwitchException>(() => fixture.Service.SwitchAsync(Provider.Sub2Api, default));

    Assert.Equal(original, fixture.FileStore.ReadBytes(fixture.ConfigPath));
}
```

Also test sibling temporary files, backup naming, retention at ten files, successful doctor JSON, malformed JSON, and a restore-write failure reported as `RecoveryFailed`.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test windows/tests/CodexSwitch.Core.Tests/CodexSwitch.Core.Tests.csproj --filter "FullyQualifiedName~AtomicFileStoreTests|FullyQualifiedName~BackupManagerTests|FullyQualifiedName~ProviderSwitchServiceTests|FullyQualifiedName~DoctorReportTests"`

Expected: compilation fails for the new service and storage types.

- [ ] **Step 3: Implement atomic storage and provider transaction**

Use `File.Move(temp, destination, true)` for first creation and `File.Replace(temp, destination, backupFileName: null)` when a destination exists on the same volume. Flush the temp stream before replacement. `DoctorReport.IsConfigValid` must require `checks.config.load.status == "ok"`. `ProviderSwitchService` must restore and byte-compare the backup whenever edit/write/doctor validation fails.

```csharp
public sealed record ProviderSwitchResult(Provider Previous, Provider Current, string BackupPath);
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?>? Environment = null, TimeSpan? Timeout = null, bool Visible = false);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
```

- [ ] **Step 4: Run core tests and verify GREEN**

Run: `dotnet test windows/tests/CodexSwitch.Core.Tests/CodexSwitch.Core.Tests.csproj`

Expected: all tests pass and no temp file remains after injected failures.

- [ ] **Step 5: Commit**

```powershell
git add windows/src/CodexSwitch.Core windows/tests/CodexSwitch.Core.Tests
git commit -m "feat(windows): add safe provider switch transaction"
```

### Task 3: Auth Parsing, Identity, Redaction, DPAPI Boundary, and Account Store

**Files:**
- Create: `windows/src/CodexSwitch.Core/Auth/AuthBlob.cs`
- Create: `windows/src/CodexSwitch.Core/Auth/AuthIdentity.cs`
- Create: `windows/src/CodexSwitch.Core/Auth/ICredentialProtector.cs`
- Create: `windows/src/CodexSwitch.Core/Auth/AccountMetadata.cs`
- Create: `windows/src/CodexSwitch.Core/Auth/AccountStore.cs`
- Create: `windows/src/CodexSwitch.Core/Diagnostics/SecretRedactor.cs`
- Create: `windows/src/CodexSwitch.App/Windows/DpapiCredentialProtector.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Auth/AuthBlobTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Auth/AccountStoreTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Diagnostics/SecretRedactorTests.cs`
- Test: `windows/tests/CodexSwitch.IntegrationTests/Windows/DpapiCredentialProtectorTests.cs`

**Interfaces:**
- Produces: `AuthBlob.Parse(ReadOnlySpan<byte>)`, `AuthIdentity? GetIdentity()`, and `bool IdentityMatches(AuthBlob replacement)`.
- Produces: `byte[] ICredentialProtector.Protect(ReadOnlySpan<byte> plaintext)` and `Unprotect(ReadOnlySpan<byte> ciphertext)`.
- Produces: `Task<AccountProfile> AccountStore.SaveAsync(string label, byte[] authJson, CancellationToken cancellationToken)` plus `ListAsync`, `LoadAuthAsync`, `RenameAsync`, and `RemoveAsync`.

- [ ] **Step 1: Add the app/integration-test project shells and write failing auth/security tests**

Use `net8.0-windows` with `<UseWindowsForms>true</UseWindowsForms>` for the app. In tests, use a reversible fake protector to assert the on-disk account file never contains an access token. Cover snake_case/camelCase token shapes, API keys, missing tokens, ID-token claims, account IDs, same-identity token rotation, different identities, control characters, bearer tokens, JSON auth fields, and 2,000-character diagnostic bounds.

```powershell
dotnet new winforms --name CodexSwitch.App --output windows/src/CodexSwitch.App --framework net8.0
dotnet new xunit --name CodexSwitch.IntegrationTests --output windows/tests/CodexSwitch.IntegrationTests --framework net8.0
dotnet sln windows/CodexSwitch.sln add windows/src/CodexSwitch.App/CodexSwitch.App.csproj windows/tests/CodexSwitch.IntegrationTests/CodexSwitch.IntegrationTests.csproj
dotnet add windows/src/CodexSwitch.App/CodexSwitch.App.csproj reference windows/src/CodexSwitch.Core/CodexSwitch.Core.csproj
dotnet add windows/tests/CodexSwitch.IntegrationTests/CodexSwitch.IntegrationTests.csproj reference windows/src/CodexSwitch.Core/CodexSwitch.Core.csproj windows/src/CodexSwitch.App/CodexSwitch.App.csproj
dotnet add windows/src/CodexSwitch.App/CodexSwitch.App.csproj package System.Security.Cryptography.ProtectedData --version 8.0.0
```

After scaffolding, change the integration-test project's `TargetFramework` to `net8.0-windows` so it can reference the WinForms adapter assembly.

```csharp
[Fact]
public async Task SaveAsync_never_persists_plaintext_token()
{
    var store = AccountStoreFixture.Create();
    var auth = TestAuth.ValidOAuth(accessToken: "secret-access-token");
    var profile = await store.SaveAsync("Work", auth, default);

    var bytes = await File.ReadAllBytesAsync(store.EncryptedPath(profile.Id));
    Assert.DoesNotContain("secret-access-token", Encoding.UTF8.GetString(bytes));
}
```

- [ ] **Step 2: Run auth/security tests and verify RED**

Run: `dotnet test windows/CodexSwitch.sln --filter "FullyQualifiedName~AuthBlobTests|FullyQualifiedName~AccountStoreTests|FullyQualifiedName~SecretRedactorTests|FullyQualifiedName~DpapiCredentialProtectorTests"`

Expected: compilation fails for the missing auth, store, redactor, and DPAPI types.

- [ ] **Step 3: Implement auth parsing and transactional encrypted storage**

Parse with `JsonDocument`; never expose token values through `ToString()`. Compute SHA-256 over normalized stable identity claims. Write encrypted blobs and metadata through `IAtomicFileStore`. On metadata failure, remove the newly written encrypted blob or restore its old bytes. Implement DPAPI with `ProtectedData.Protect/Unprotect(..., DataProtectionScope.CurrentUser)` and fixed application entropy.

```csharp
public sealed record AccountProfile(Guid Id, string Label, string IdentityFingerprint);
public sealed record AccountConfiguration(int SchemaVersion, Guid? ActiveProfileId, IReadOnlyList<AccountProfile> Profiles);
```

- [ ] **Step 4: Run all solution tests and verify GREEN**

Run: `dotnet test windows/CodexSwitch.sln`

Expected: all tests pass; DPAPI round-trip passes only on Windows.

- [ ] **Step 5: Commit**

```powershell
git add windows
git commit -m "feat(windows): add encrypted account storage"
```

### Task 4: CLI Discovery, Login Import, and Account Switching

**Files:**
- Create: `windows/src/CodexSwitch.Core/Codex/CodexCliResolver.cs`
- Create: `windows/src/CodexSwitch.Core/Codex/LoginService.cs`
- Create: `windows/src/CodexSwitch.Core/Codex/AccountSwitchService.cs`
- Create: `windows/src/CodexSwitch.Core/Auth/AccountImportService.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Codex/CodexCliResolverTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Auth/AccountImportServiceTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Codex/AccountSwitchServiceTests.cs`
- Create: `windows/tests/CodexSwitch.IntegrationTests/Fixtures/FakeCodexCli.cs`
- Test: `windows/tests/CodexSwitch.IntegrationTests/Codex/LoginServiceTests.cs`

**Interfaces:**
- Consumes: `AccountStore`, `AuthBlob`, `ProviderConfigEditor`, `IAtomicFileStore`, and `IProcessRunner`.
- Produces: `string CodexCliResolver.Resolve(IReadOnlyDictionary<string,string?> environment)`.
- Produces: `Task<AccountProfile> LoginService.LoginAsync(string label, CancellationToken cancellationToken)`.
- Produces: `Task<AccountSwitchResult> AccountSwitchService.SwitchAsync(Guid profileId, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write failing discovery, duplicate import, login cleanup, and rollback tests**

```csharp
[Fact]
public async Task SwitchAsync_rejects_account_change_when_provider_is_sub2api()
{
    var fixture = AccountSwitchFixture.Create(currentProvider: Provider.Sub2Api);
    await Assert.ThrowsAsync<AccountSwitchException>(() => fixture.Service.SwitchAsync(fixture.ProfileId, default));
    Assert.Equal(fixture.OriginalLiveAuth, await File.ReadAllBytesAsync(fixture.LiveAuthPath));
}
```

Test discovery precedence (`CODEX_CLI`, PATH, newest version directory), visible `codex login`, non-zero login exit, missing/malformed auth, temporary-directory cleanup, duplicate identity import, atomic live-auth replacement, readback identity mismatch, and metadata rollback.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test windows/CodexSwitch.sln --filter "FullyQualifiedName~CodexCliResolverTests|FullyQualifiedName~AccountImportServiceTests|FullyQualifiedName~LoginServiceTests|FullyQualifiedName~AccountSwitchServiceTests"`

Expected: compilation fails for the new services.

- [ ] **Step 3: Implement discovery, login, import, and switching**

Search versioned CLI directories without following reparse points. Login uses a private directory below `%LOCALAPPDATA%\CodexSwitch\tmp`, sets only that child process's `CODEX_HOME`, invokes `codex login` visibly, imports only after exit code zero and auth validation, and deletes plaintext in `finally`. Duplicate identities return a typed conflict for UI confirmation. Account switching backs up live auth bytes and active-profile metadata, verifies readback identity, and restores both on failure before restart is committed.

```csharp
public sealed record AccountSwitchResult(Guid PreviousProfileId, Guid CurrentProfileId, string? LiveAuthBackupPath);
public sealed class DuplicateAccountException(AccountProfile existing) : Exception;
```

- [ ] **Step 4: Run all tests and verify GREEN**

Run: `dotnet test windows/CodexSwitch.sln`

Expected: all tests pass; integration fixtures leave no plaintext auth directory.

- [ ] **Step 5: Commit**

```powershell
git add windows
git commit -m "feat(windows): add account login and switching"
```

### Task 5: Isolated JSON-RPC Usage Monitoring and Cache

**Files:**
- Create: `windows/src/CodexSwitch.Core/Usage/UsageSnapshot.cs`
- Create: `windows/src/CodexSwitch.Core/Usage/UsageSnapshotMapper.cs`
- Create: `windows/src/CodexSwitch.Core/Usage/CodexRpcClient.cs`
- Create: `windows/src/CodexSwitch.Core/Usage/OpenAiUsageConfig.cs`
- Create: `windows/src/CodexSwitch.Core/Usage/UsageFetcher.cs`
- Create: `windows/src/CodexSwitch.Core/Usage/UsageCache.cs`
- Create: `windows/src/CodexSwitch.Core/Usage/UsageRefreshCoordinator.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Usage/OpenAiUsageConfigTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Usage/UsageMappingTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Usage/UsageCacheTests.cs`
- Test: `windows/tests/CodexSwitch.IntegrationTests/Usage/CodexRpcClientTests.cs`

**Interfaces:**
- Consumes: CLI resolver, account store, atomic file store, process abstractions, and secret redactor.
- Produces: `Task<UsageSnapshot> UsageFetcher.FetchAsync(Guid profileId, CancellationToken cancellationToken)`.
- Produces: `Task<IReadOnlyDictionary<Guid, UsageState>> UsageRefreshCoordinator.RefreshAllAsync(CancellationToken cancellationToken)`.
- Produces: immutable `UsageSnapshot` fields matching the design's primary, secondary, credits, and reset-credit data.

- [ ] **Step 1: Write failing mapping, isolation, framing, timeout, and cache tests**

```csharp
[Fact]
public void Map_uses_earliest_available_reset_credit_expiration()
{
    var response = RpcFixtures.RateLimits(availableCount: 2, credits: [
        new("expired", 100), new("available", 300), new("available", 200)
    ]);

    var snapshot = UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch);

    Assert.Equal(2, snapshot.ResetCreditsAvailable);
    Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), snapshot.NextResetCreditExpiresAt);
}
```

Test newline-delimited request IDs, `initialize` then `initialized`, `account/rateLimits/read`, `-s read-only app-server` arguments, malformed JSON, JSON-RPC errors, bounded stderr, per-operation timeouts, forced OpenAI config, temporary cleanup, independent account errors, five-minute schedule, capped exponential retry, and non-secret cache serialization.

- [ ] **Step 2: Run usage tests and verify RED**

Run: `dotnet test windows/CodexSwitch.sln --filter "FullyQualifiedName~Usage|FullyQualifiedName~CodexRpcClientTests|FullyQualifiedName~OpenAiUsageConfigTests"`

Expected: compilation fails for missing usage/RPC types.

- [ ] **Step 3: Implement the minimal RPC and refresh pipeline**

Use redirected standard input/output/error with UTF-8 and one JSON document per line. Match responses by integer request ID and ignore unrelated notifications. Initialize with client name `CodexSwitchWindows` and assembly version. Write decrypted auth/config only inside the isolated directory and accept a changed auth file only after `IdentityMatches` succeeds; persist it through `AccountStore` before deleting the temp home.

```csharp
public sealed record UsageSnapshot(
    string? PlanType,
    decimal? CreditsRemaining,
    int? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetAt,
    int? PrimaryWindowDurationMinutes,
    int? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetAt,
    int? SecondaryWindowDurationMinutes,
    int? ResetCreditsAvailable,
    DateTimeOffset? NextResetCreditExpiresAt,
    DateTimeOffset FetchedAt);
```

- [ ] **Step 4: Run all tests and verify GREEN**

Run: `dotnet test windows/CodexSwitch.sln`

Expected: all unit/integration tests pass and fake RPC stderr contains no unredacted secrets in failures.

- [ ] **Step 5: Commit**

```powershell
git add windows
git commit -m "feat(windows): add per-account usage monitoring"
```

### Task 6: Windows Codex Lifecycle, Sub2API Probe, ACL, and Startup

**Files:**
- Create: `windows/src/CodexSwitch.Core/Codex/ICodexLifecycle.cs`
- Create: `windows/src/CodexSwitch.Core/Codex/CapturedProcessSet.cs`
- Create: `windows/src/CodexSwitch.Core/Codex/Sub2ApiProbe.cs`
- Create: `windows/src/CodexSwitch.App/Windows/WindowsCodexLifecycle.cs`
- Create: `windows/src/CodexSwitch.App/Windows/AppDataSecurity.cs`
- Create: `windows/src/CodexSwitch.App/Windows/StartupRegistration.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Codex/Sub2ApiProbeTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Codex/CapturedProcessShutdownTests.cs`
- Test: `windows/tests/CodexSwitch.IntegrationTests/Windows/StartupRegistrationTests.cs`

**Interfaces:**
- Produces: `Task<CodexShutdownResult> ICodexLifecycle.RequestCloseAsync(TimeSpan timeout, CancellationToken cancellationToken)`.
- Produces: `Task<bool> ICodexLifecycle.ForceCloseCapturedAsync(CancellationToken cancellationToken)`.
- Produces: `Task ICodexLifecycle.LaunchAsync(CancellationToken cancellationToken)`.
- Produces: `Task<bool> Sub2ApiProbe.IsOnlineAsync(CancellationToken cancellationToken)`.
- Produces: `bool StartupRegistration.IsEnabled`, `Enable()`, and `Disable()`.

- [ ] **Step 1: Write failing captured-PID, launch fallback, TCP timeout, and startup tests**

```csharp
[Fact]
public async Task ForceCloseCapturedAsync_never_kills_process_started_after_capture()
{
    var captured = new CapturedProcessSet([11, 12]);

    var targets = captured.RemainingFrom([11, 12, 99]);

    Assert.Equal([11, 12], targets.Order());
    Assert.DoesNotContain(99, targets);
}
```

Also verify `OpenAI.Codex_2p2nqsd0c76g0!App` is the first launch target, discovered executable is fallback only, startup registry is `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, values are quoted, and app-data ACL logic retains current user, Administrators, and SYSTEM.

- [ ] **Step 2: Run lifecycle tests and verify RED**

Run: `dotnet test windows/CodexSwitch.sln --filter "FullyQualifiedName~Sub2ApiProbeTests|FullyQualifiedName~CapturedProcessShutdownTests|FullyQualifiedName~StartupRegistrationTests"`

Expected: compilation fails for missing lifecycle and startup types.

- [ ] **Step 3: Implement Windows adapters**

Capture `ChatGPT.exe` process IDs once, call `CloseMainWindow`, poll until timeout, and make the force path operate on the captured intersection only. Launch with `explorer.exe shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App`; then wait for a new ChatGPT process. Use `TcpClient.ConnectAsync("127.0.0.1", 8080, cancellationToken)` with a linked timeout. Write the Run entry only under HKCU. Apply explicit directory ACLs without weakening inherited SYSTEM/Administrators access.

- [ ] **Step 4: Run all tests and verify GREEN**

Run: `dotnet test windows/CodexSwitch.sln`

Expected: all tests pass on Windows; no real ChatGPT process is terminated by tests.

- [ ] **Step 5: Commit**

```powershell
git add windows
git commit -m "feat(windows): add native lifecycle and startup integration"
```

### Task 7: Tray Application, Account Manager, and Operation Coordination

**Files:**
- Create: `windows/src/CodexSwitch.App/Program.cs`
- Create: `windows/src/CodexSwitch.App/AppPaths.cs`
- Create: `windows/src/CodexSwitch.App/CompositionRoot.cs`
- Create: `windows/src/CodexSwitch.App/Tray/TrayApplicationContext.cs`
- Create: `windows/src/CodexSwitch.Core/Presentation/TrayMenuModel.cs`
- Create: `windows/src/CodexSwitch.Core/Presentation/OperationCoordinator.cs`
- Create: `windows/src/CodexSwitch.App/Accounts/AccountManagerForm.cs`
- Create: `windows/src/CodexSwitch.App/Accounts/LoginPrompt.cs`
- Create: `windows/src/CodexSwitch.App/Resources/AppIcon.ico`
- Test: `windows/tests/CodexSwitch.Core.Tests/Tray/TrayMenuModelTests.cs`
- Test: `windows/tests/CodexSwitch.Core.Tests/Tray/OperationCoordinatorTests.cs`

**Interfaces:**
- Consumes: every service produced by Tasks 1-6.
- Produces: `TrayMenuModel.Build(AppSnapshot snapshot)` returning immutable menu rows independent of WinForms.
- Produces: `Task<bool> OperationCoordinator.TryRunAsync(Func<CancellationToken, Task> operation)` to serialize mutations.

- [ ] **Step 1: Write failing menu projection and operation serialization tests**

```csharp
[Fact]
public void Build_marks_active_provider_and_account_and_formats_reset_credits()
{
    var snapshot = AppSnapshots.TwoAccounts(activeProvider: Provider.OpenAI, activeProfile: AppSnapshots.WorkId);
    var menu = TrayMenuModel.Build(snapshot);

    Assert.True(menu.ProviderItems.Single(x => x.Provider == Provider.OpenAI).Checked);
    Assert.Contains(menu.AccountItems, x => x.ProfileId == AppSnapshots.WorkId && x.Checked);
    Assert.Contains(menu.AccountItems.Single(x => x.ProfileId == AppSnapshots.WorkId).Detail, "重置卡 2");
}
```

Test offline/expired/error/refreshing labels, unknown reset times, disabled mutation items during an operation, concurrent operation rejection, and operation state clearing after exceptions.

- [ ] **Step 2: Run UI-model tests and verify RED**

Run: `dotnet test windows/CodexSwitch.sln --filter "FullyQualifiedName~TrayMenuModelTests|FullyQualifiedName~OperationCoordinatorTests"`

Expected: compilation fails for missing tray model and coordinator.

- [ ] **Step 3: Implement the headless tray lifecycle and dialogs**

Create `ApplicationContext` with `NotifyIcon` and rebuild the context menu from `TrayMenuModel` on open/state changes. Wire explicit confirmation before restart and a second confirmation before force termination. The login command asks for a label, shows the visible CLI login process, handles duplicate confirmation, and refreshes the account list. The manager form uses a read-only account list with Rename, Switch, Remove, and Close buttons. UI event handlers catch typed service errors and show Chinese actionable messages; raw exception text is redacted first.

Convert the existing repository icon into a multi-resolution Windows icon and embed it in the app:

```powershell
magick Resources/AppIcon.icns -define icon:auto-resize=256,128,64,48,32,16 windows/src/CodexSwitch.App/Resources/AppIcon.ico
```

- [ ] **Step 4: Add startup import behavior**

When no managed account exists but live `%USERPROFILE%\.codex\auth.json` is valid, show a one-time prompt to import it as `默认账号`. Do not modify the live file during import. On startup, refresh usage immediately and schedule five-minute refreshes; update menu state on the UI synchronization context.

- [ ] **Step 5: Run tests and a Release build**

Run: `dotnet test windows/CodexSwitch.sln`

Run: `dotnet build windows/CodexSwitch.sln -c Release -warnaserror`

Expected: tests pass; Release build exits zero with no warnings.

- [ ] **Step 6: Launch a disposable manual smoke instance**

Run with `CODEX_SWITCH_DATA_HOME` and `CODEX_HOME` pointing at disposable directories and a fake CLI. Verify the tray appears, menu opens, account manager renders, concurrent actions disable correctly, and Exit removes the tray icon. Do not switch the real provider or terminate the real Codex during this smoke test.

- [ ] **Step 7: Commit**

```powershell
git add windows
git commit -m "feat(windows): add tray and account management UI"
```

### Task 8: Self-Contained Publish, Installer, CI, and Documentation

**Files:**
- Create: `windows/installer/CodexSwitch.iss`
- Create: `windows/scripts/build-installer.ps1`
- Create: `.github/workflows/windows.yml`
- Modify: `.gitignore`
- Modify: `README.md`
- Create: `windows/README.md`
- Test: `windows/tests/CodexSwitch.IntegrationTests/Packaging/InstallerContractTests.cs`

**Interfaces:**
- Consumes: `CodexSwitch.App` output from Task 7.
- Produces: `artifacts/publish/CodexSwitch.exe` and `artifacts/installer/CodexSwitch-Setup-x64.exe`.

- [ ] **Step 1: Write failing packaging contract tests**

Tests read the `.iss`, project, and workflow as text and require per-user privilege mode, Start Menu shortcut, optional startup task, uninstall behavior that retains data by default, `win-x64`, self-contained publish, test-before-package ordering, Windows runner, and uploaded installer artifact.

```csharp
[Fact]
public void Installer_is_per_user_and_preserves_account_data_by_default()
{
    var script = File.ReadAllText(RepositoryPaths.WindowsInstaller);
    Assert.Contains("PrivilegesRequired=lowest", script);
    Assert.DoesNotContain(@"{localappdata}\CodexSwitch; Flags: uninsdelete", script);
}
```

- [ ] **Step 2: Run packaging tests and verify RED**

Run: `dotnet test windows/CodexSwitch.sln --filter FullyQualifiedName~InstallerContractTests`

Expected: tests fail because installer, workflow, and publish script do not exist.

- [ ] **Step 3: Implement the publish and installer pipeline**

`build-installer.ps1` must stop on errors, run restore/test/build, publish `CodexSwitch.App` as self-contained `win-x64`, locate `ISCC.exe`, and compile the installer. The Inno script installs below `{localappdata}\Programs\Codex Switch`, creates a Start Menu item, offers a startup task, launches after install when selected, and leaves `%LOCALAPPDATA%\CodexSwitch` untouched on uninstall unless the user explicitly confirms the uninstaller's data-removal prompt.

- [ ] **Step 4: Add Windows CI**

Use `actions/checkout@v4`, `actions/setup-dotnet@v4` with `.NET 8.x`, `dotnet test`, `dotnet publish`, Chocolatey Inno Setup installation, and `actions/upload-artifact@v4`. Trigger on pull requests and pushes that touch `windows/**`, `.github/workflows/windows.yml`, or Windows documentation.

- [ ] **Step 5: Update documentation and ignore rules**

Add a platform table to the root README and link `windows/README.md`. Document requirements, installer use, tray commands, data paths, security model, Sub2API prerequisites, build/test commands, and limitations. Ignore `windows/**/bin`, `windows/**/obj`, `.vs`, and `artifacts` while retaining the committed design and plan files already force-added.

- [ ] **Step 6: Run tests, build, publish, and package**

Run: `dotnet test windows/CodexSwitch.sln -c Release`

Run: `powershell -ExecutionPolicy Bypass -File windows/scripts/build-installer.ps1`

Expected: all tests pass, build/publish exit zero, and `artifacts/installer/CodexSwitch-Setup-x64.exe` exists.

- [ ] **Step 7: Install and verify with disposable app data**

Install for the current user. Verify the installed binary launches from the Start Menu, the tray icon and account manager open, the launch-at-sign-in entry toggles, and uninstall removes binaries/shortcuts while retaining encrypted app data. Reinstall and verify the retained metadata is readable. Avoid provider/account mutations against the live Codex profile until the disposable-path smoke passes.

- [ ] **Step 8: Commit**

```powershell
git add .github/workflows/windows.yml .gitignore README.md windows
git commit -m "build(windows): add installer CI and documentation"
```

### Task 9: Final Regression, Security Audit, and Release Candidate

**Files:**
- Modify only files required by failures found in this task.

**Interfaces:**
- Consumes: complete Windows solution and installer.
- Produces: verified Release installer and clean branch ready for review.

- [ ] **Step 1: Run the full automated suite from a clean Release build**

Run: `dotnet clean windows/CodexSwitch.sln -c Release`

Run: `dotnet test windows/CodexSwitch.sln -c Release --no-restore`

Run: `powershell -ExecutionPolicy Bypass -File windows/scripts/build-installer.ps1`

Expected: zero failed tests, zero warnings, and a newly generated installer.

- [ ] **Step 2: Audit the tracked tree for secret leakage and unsafe process calls**

Run: `rg -n "access_token|refresh_token|id_token|OPENAI_API_KEY|Authorization" windows --glob '!**/obj/**' --glob '!**/bin/**'`

Expected: matches occur only in parsers, test fixtures, and redaction rules; no real token values exist.

Run: `rg -n "Process\.Kill|taskkill|Stop-Process" windows/src`

Expected: termination calls exist only in the captured-PID lifecycle adapter.

- [ ] **Step 3: Exercise transaction failures with disposable paths**

Use fake CLI modes for malformed doctor output, failed login, malformed auth, RPC timeout, identity-changing token refresh, and launch failure. Confirm byte-identical config/auth rollback where the transaction is not committed, no plaintext temp directory remains, and launch failure offers retry without reverting validated provider state.

- [ ] **Step 4: Verify real read-only integration**

Against the installed local Codex CLI, run only read-only discovery, `codex --version`, `codex doctor --json`, current-provider display, and current managed-account usage refresh. Do not switch the real provider/account or restart Codex until the user-facing confirmation path is manually exercised.

- [ ] **Step 5: Review the diff and commit final fixes**

Run: `git diff origin/main...HEAD --check`

Run: `git status --short`

If verification fixes were required, commit only those files:

```powershell
git add windows README.md .gitignore .github/workflows/windows.yml
git commit -m "fix(windows): address release verification findings"
```

If no fixes were required, do not create an empty commit.
