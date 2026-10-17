# Claude Switch installer for Windows (per-user, no admin).
#
#   irm https://raw.githubusercontent.com/Ngoni-Sama/claude-switch/main/install.ps1 | iex
#
# Downloads the latest setup from GitHub Releases and runs it silently.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$release = Invoke-RestMethod 'https://api.github.com/repos/Ngoni-Sama/claude-switch/releases/latest'
$asset = $release.assets | Where-Object { $_.name -like '*windows-x64-setup.exe' } | Select-Object -First 1
if (-not $asset) { throw 'No Windows setup in the latest release.' }
$file = Join-Path $env:TEMP $asset.name
Write-Host "Downloading $($asset.name)..." -ForegroundColor Yellow
Invoke-WebRequest $asset.browser_download_url -OutFile $file -UseBasicParsing
Write-Host 'Installing...' -ForegroundColor Yellow
Start-Process $file -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
Remove-Item $file -ErrorAction SilentlyContinue
Start-Process "$env:LOCALAPPDATA\Programs\Claude Switch\ClaudeSwitch.exe"
Write-Host 'Claude Switch is installed and open.' -ForegroundColor Green
