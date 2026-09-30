$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-winui.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
