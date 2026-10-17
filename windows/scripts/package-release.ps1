[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'windows\src\CodexSwitch.App\CodexSwitch.App.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected release version.' }
& (Join-Path $PSScriptRoot 'build-installer.ps1') -SkipTests:$SkipTests

$executable = Join-Path $repositoryRoot 'artifacts\publish\CodexSwitch.exe'
$setup = Join-Path $repositoryRoot 'artifacts\installer\CodexSwitch-Setup-x64.exe'
$releaseDirectory = Join-Path $repositoryRoot "artifacts\release\windows-v$version"
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
if ((Get-Item -LiteralPath $executable).VersionInfo.FileVersion -ne "$version.0") { throw 'Published executable has an unexpected version.' }

# Explicit file allowlist: never recurse through the workspace or runtime data.
$versionedSetup = Join-Path $releaseDirectory "CodexSwitch-Setup-$version-x64.exe"
$portable = Join-Path $releaseDirectory "CodexSwitch-$version-win-x64.zip"
Copy-Item -LiteralPath $setup -Destination $versionedSetup -Force
Compress-Archive -LiteralPath $executable -DestinationPath $portable -CompressionLevel Optimal -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($portable)
try {
    if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -ne 'CodexSwitch.exe') { throw 'Unexpected portable archive contents.' }
} finally { $archive.Dispose() }
$checksums = foreach ($path in @($versionedSetup, $portable)) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($path))"
}
$checksums | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output $releaseDirectory
