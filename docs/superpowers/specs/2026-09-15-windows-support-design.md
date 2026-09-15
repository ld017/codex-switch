# Codex Switch Windows Support Design

## Summary

Add a native Windows version of Codex Switch while preserving the existing macOS Swift application. The Windows application will provide a system-tray menu, OpenAI/Sub2API provider switching, multiple OpenAI account login and switching, per-account usage monitoring, install/uninstall support, Start Menu integration, and optional launch at sign-in.

The Windows implementation will live under `windows/` and use .NET 8 with WinForms. It will publish as a self-contained `win-x64` application and be packaged with Inno Setup, so end users do not need to install .NET separately.

## Goals

- Run as a native Windows 10/11 x64 system-tray application.
- Show and switch the active Codex provider (`openai` or `sub2api`).
- Add, rename, remove, and switch between multiple OpenAI accounts.
- Display each account's primary and secondary usage windows, reset times, available reset-credit count, and nearest reset-credit expiration.
- Preserve the user's Codex configuration and authentication state with atomic writes, backups, validation, and rollback.
- Protect stored account credentials with Windows DPAPI for the current user.
- Provide an installer with Start Menu and optional launch-at-sign-in integration.
- Build and test the Windows implementation in GitHub Actions.

## Non-goals

- Rewriting or restructuring the existing macOS Swift implementation.
- Porting the macOS Apple Container helper or automatically starting a Windows Sub2API deployment. The Windows app reports Sub2API readiness and explains how to resolve an offline service.
- Porting the macOS LaunchServices URL scheme or shared-history proxy in the first Windows release.
- Supporting Windows on ARM or 32-bit Windows in the first release.
- Synchronizing stored accounts across operating systems or Windows users.

## Repository Layout

```text
windows/
  CodexSwitch.sln
  Directory.Build.props
  src/
    CodexSwitch.Core/
    CodexSwitch.App/
  tests/
    CodexSwitch.Core.Tests/
    CodexSwitch.IntegrationTests/
  installer/
    CodexSwitch.iss
  scripts/
    build-installer.ps1
.github/workflows/windows.yml
```

`CodexSwitch.Core` contains platform services and business logic without UI dependencies. `CodexSwitch.App` owns the WinForms tray icon, menus, dialogs, account-management window, and application lifetime. Tests use interfaces around the filesystem, processes, clock, network probes, and DPAPI boundary so failure paths can be exercised deterministically.

## Tray User Experience

The application starts without a main window and creates a `NotifyIcon`. Its menu contains:

- Product heading and current provider.
- Sub2API online/offline state.
- An OpenAI account section showing the active account and cached usage for every account.
- Commands to switch to OpenAI or Sub2API.
- Commands to log in a new account, refresh usage, and open account management.
- Commands to open Codex, enable or disable launch at sign-in, open the log directory, and exit.

The active provider and account use checked menu items. Account rows show primary and secondary used percentages and reset times when available, plus the available reset-credit count and nearest expiration. Refreshing, expired-login, network-error, and unavailable states are explicit and do not expose tokens.

Account management provides rename, switch, and remove operations. Removing an account deletes only its encrypted stored copy. If the removed account is active, the user must select another account first unless it is the only account; deleting the only account leaves Codex's live authentication unchanged and reports that no managed account is active.

Usage refresh runs once after startup and every five minutes thereafter. Failures use capped exponential backoff while manual refresh remains available.

## Paths and Persistent Data

- Live Codex home: `%USERPROFILE%\.codex` unless `CODEX_HOME` is explicitly set for the application.
- Application data: `%LOCALAPPDATA%\CodexSwitch`.
- Encrypted accounts: `%LOCALAPPDATA%\CodexSwitch\accounts`.
- Non-secret metadata and usage cache: `%LOCALAPPDATA%\CodexSwitch\config.json` and `cache.json`.
- Temporary isolated Codex homes: `%LOCALAPPDATA%\CodexSwitch\tmp`.
- Provider configuration backups: `%USERPROFILE%\.codex\provider-switcher\backups`, retaining the ten newest files.
- Logs: `%LOCALAPPDATA%\CodexSwitch\logs`, with token-shaped and auth-field values redacted.

Application-data directories are restricted to the current user, Administrators, and SYSTEM. Credential blobs receive an additional DPAPI `CurrentUser` protection layer. The live `%USERPROFILE%\.codex\auth.json` remains in the format required by Codex.

## Codex Discovery

The CLI resolver checks:

1. An explicit `CODEX_CLI` environment variable.
2. `codex.exe` on `PATH`.
3. Versioned directories matching `%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe`, choosing the newest usable binary.

The desktop launcher first uses the installed AppUserModelID `OpenAI.Codex_2p2nqsd0c76g0!App` through `shell:AppsFolder`. If unavailable, it falls back to a discovered running executable path and then reports an actionable error.

## Provider Switching

The provider editor changes only the top-level `model_provider` field in `%USERPROFILE%\.codex\config.toml`. It preserves BOM, line endings, comments, indentation, and all unrelated settings. A missing top-level provider means OpenAI when a top-level `model` exists. A missing provider is inserted immediately after that model. Multiple top-level provider fields, an unknown current provider, or an ambiguous file cause a safe failure.

Before switching to Sub2API, the app verifies:

- `[model_providers.sub2api]` exists.
- `[model_providers.sub2api.auth]` exists.
- TCP `127.0.0.1:8080` accepts a connection within the timeout.

The Windows version does not assume a macOS Keychain service and does not start Apple Container. Authentication-specific validation is delegated to `codex doctor --json` after the edit.

The switch transaction is:

1. Read and parse the source configuration.
2. Create a timestamped backup.
3. Write the changed file to a sibling temporary file and atomically replace the live file.
4. Run `codex doctor --json` and require `checks.config.load.status == "ok"`.
5. If any step after backup fails, restore the exact backup and verify byte equality.
6. Restart Codex only after the configuration is valid.

## Account Login and Storage

Logging in creates a private isolated `CODEX_HOME`, then launches `codex login` in a visible console so the normal browser and device-login flow remains recognizable. A successful process must produce a plausible `auth.json` containing either an API key or usable OAuth access and refresh tokens.

The application extracts only non-secret identity claims needed for duplicate detection and display. It fingerprints the account ID and stable ID-token claims; adding identity fields during token renewal does not create a false mismatch. The full auth blob is encrypted with DPAPI `CurrentUser` scope before it enters the account store, and the temporary plaintext directory is removed in a `finally` path.

The metadata file records profile IDs, user labels, the active profile ID, and schema version. It never records tokens. Duplicate identity imports update the existing account only after user confirmation rather than silently creating a second profile.

## Account Switching

Account switching is allowed only while the active provider is OpenAI. The transaction is:

1. Decrypt and validate the selected stored auth blob.
2. Capture a backup of the live `auth.json` if present.
3. Atomically replace the live file.
4. Read it back, validate its shape, and verify the intended identity.
5. Update the active profile metadata.
6. Restart Codex.

If validation or restart preparation fails, the application restores the original live file and active-profile metadata. Stored profiles remain unchanged.

## Usage Monitoring and Token Renewal

Each usage request creates an isolated temporary `CODEX_HOME`, writes the decrypted account auth blob there, and writes a copy of the user's configuration whose top-level provider is forced to `openai`. The app launches:

```text
codex app-server
```

It communicates through newline-delimited JSON-RPC over standard input/output, initializes the client, and requests account rate limits. The response maps to:

- Plan type.
- Credits balance when supplied.
- Primary and secondary used percentages, window durations, and reset times.
- `rateLimitResetCredits.availableCount`.
- The earliest `expiresAt` among credits whose status is `available`.

RPC startup, initialization, and request operations have explicit timeouts. Standard error is bounded and redacted before appearing in diagnostics.

If Codex updates the temporary `auth.json`, the application accepts it only when the old and new identity claims agree. The renewed blob is then encrypted and saved transactionally. A changed or indeterminate identity is rejected, leaving the stored credential untouched.

## Codex Restart Safety

Provider and account switches warn that active tasks will be interrupted. The app captures the IDs of currently running `ChatGPT.exe` processes, requests graceful close, and waits. If captured processes remain, it asks separately before forced termination. Only the captured process IDs are eligible for termination, so a newly launched Codex instance is not killed accidentally.

After the captured processes exit, the app launches Codex and waits for a `ChatGPT.exe` process to appear. Failure to launch is reported separately from configuration or account-write failures. A launch failure does not roll back a configuration that was already validated; the UI offers an explicit retry to open Codex.

## Error Handling and Diagnostics

Operations are serialized so two provider/account switches cannot overlap. Menu items that mutate state are disabled during an operation. Errors identify the failed stage: prerequisites, backup, write, validation, normal shutdown, forced shutdown, or launch.

Logs use structured stage names, timestamps, exit codes, and redacted excerpts. They do not record auth blobs, access tokens, refresh tokens, ID tokens, API keys, authorization headers, or raw RPC requests containing credentials. Temporary directories and partial files are cleaned after both success and failure.

## Installer and Startup Integration

The Release build uses `dotnet publish` for `win-x64`, self-contained and single-file where compatible with WinForms. Inno Setup installs per user, creates a Start Menu shortcut and uninstaller entry, and offers a launch-at-sign-in task. The app also exposes the same setting from its tray menu using a per-user Run registry entry that points to the installed executable.

Upgrades replace binaries but retain `%LOCALAPPDATA%\CodexSwitch` and `%USERPROFILE%\.codex` data. Uninstall removes installed binaries and shortcuts; encrypted account data is retained by default to prevent accidental credential loss, with an explicit user-selected removal option.

## Testing

Development follows test-driven implementation. Core tests cover:

- Provider parsing, insertion, replacement, duplicate detection, UTF-8 BOM, CRLF, comments, and table boundaries.
- Atomic replacement, backup retention, exact restoration, and injected failures.
- Auth-blob validation, identity matching, duplicate import, and active-account rules.
- DPAPI encrypt/decrypt round trips and wrong-scope failure behavior on Windows.
- CLI discovery precedence and versioned-directory selection.
- JSON-RPC framing, timeouts, malformed responses, rate-limit mapping, reset-credit selection, and redaction.
- Captured-process-only termination logic and launch fallback selection.

Integration tests use a fake `codex.exe` fixture to exercise login, updated auth files, usage responses, timeouts, non-zero exits, and hostile diagnostic strings without network access or real credentials.

Manual local acceptance covers installation, Start Menu launch, tray startup, launch-at-sign-in, login in a browser, account switching, provider switching in both directions, quota refresh, rollback behavior, upgrade preservation, and uninstall.

## Continuous Integration and Release Artifact

`.github/workflows/windows.yml` runs restore, build, tests, and Release publish on `windows-latest`. When Inno Setup is available in the runner, it also builds the installer and uploads it as a workflow artifact. The workflow does not publish a GitHub Release automatically; release publication remains an explicit maintainer action.

## Compatibility and Migration

The macOS application and tests continue to use `Package.swift` unchanged. Windows account storage has its own schema version and no automatic import from the macOS Keychain or macOS development file vault. Existing live Windows `auth.json` can be offered as the first managed account after explicit confirmation, then copied into encrypted storage without changing the live file.

## Acceptance Criteria

- A clean Windows 10/11 x64 user account can install and launch Codex Switch without a preinstalled .NET runtime.
- The tray app accurately reports and safely switches OpenAI/Sub2API provider state.
- A user can add at least two OpenAI accounts, switch between them, and see independent usage/reset data.
- Stored managed credentials are DPAPI-encrypted and tokens never appear in UI or logs.
- Failed provider or account writes restore the previous bytes and leave Codex usable.
- The installer creates the requested Start Menu and launch-at-sign-in integration and preserves data across upgrade.
- Automated Windows tests pass in GitHub Actions and the workflow produces an installer artifact.
