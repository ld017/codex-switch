[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$solution = Join-Path $repositoryRoot 'windows\CodexSwitch.sln'
$project = Join-Path $repositoryRoot 'windows\src\CodexSwitch.App\CodexSwitch.App.csproj'
$publishDirectory = Join-Path $repositoryRoot 'artifacts\publish'
$installerScript = Join-Path $repositoryRoot 'windows\installer\CodexSwitch.iss'

if (-not $SkipTests) {
    & dotnet test $solution -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$isccCandidates = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 compiler (ISCC.exe) was not found.' }

& $iscc $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$installer = Join-Path $repositoryRoot 'artifacts\installer\CodexSwitch-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw "Installer was not created: $installer" }
Write-Output $installer
