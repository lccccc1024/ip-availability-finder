param(
    [string]$NuGetConfig = (Join-Path $PSScriptRoot 'NuGet.Config'),
    [switch]$SkipAudit
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot '.nuget-http-cache'
$env:NUGET_SCRATCH = Join-Path $PSScriptRoot '.nuget-scratch'
$env:DOTNET_NOLOGO = '1'

$restoreArgs = @(
    'restore', '.\winui\PingCandidateFinder.WinUI.csproj',
    '--configfile', $NuGetConfig, '--locked-mode', '--verbosity', 'minimal'
)
if ($SkipAudit) { $restoreArgs += '-p:NuGetAudit=false' }
& dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish .\winui\PingCandidateFinder.WinUI.csproj --no-restore -c Release `
    -o .\dist-winui-staging --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Path .\dist -Force | Out-Null
Copy-Item -LiteralPath .\dist-winui-staging\未占用IP查找.exe `
    -Destination .\dist\未占用IP查找.exe -Force
Write-Host "Built: $PSScriptRoot\dist\未占用IP查找.exe"
