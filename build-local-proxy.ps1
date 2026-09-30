$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$python = (Get-Command python -ErrorAction Stop).Source
$proxy = Start-Process -FilePath $python -ArgumentList (Join-Path $PSScriptRoot 'tools\nuget_proxy.py') `
    -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 1
    $proxy.Refresh()
    if ($proxy.HasExited) { throw 'NuGet proxy failed to start.' }
    & (Join-Path $PSScriptRoot 'build.ps1') `
        -NuGetConfig (Join-Path $PSScriptRoot 'tools\NuGet.LocalProxy.Config') -SkipAudit
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    $proxy.Refresh()
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
}
