param([switch]$SkipRestore)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot '.nuget-http-cache'
$env:NUGET_SCRATCH = Join-Path $PSScriptRoot '.nuget-scratch'
$env:DOTNET_NOLOGO = '1'
if (-not $SkipRestore) {
    dotnet restore .\winui-tests\WinUITests.csproj --configfile .\NuGet.Config --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
dotnet run --project .\winui-tests\WinUITests.csproj --no-restore -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
