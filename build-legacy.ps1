$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
python -m PyInstaller --noconfirm --clean --onefile --windowed --name PingCandidateFinder-Material3 --paths src run.py
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $PSScriptRoot\dist\PingCandidateFinder-Material3.exe"
