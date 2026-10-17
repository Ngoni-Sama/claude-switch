<#
.SYNOPSIS
    Builds the Windows release: setup.exe, a zipped setup, and a portable zip.

.DESCRIPTION
    1. dotnet publish -> one self-contained ClaudeSwitch.exe (nothing else to install)
    2. Inno Setup     -> ClaudeSwitch-<ver>-windows-x64-setup.exe (per-user, no admin)
    3. Zips           -> ...-setup.zip and ...-portable.zip (run the exe from anywhere)

    Needs the .NET 10 SDK and Inno Setup 6.3+/7 (winget install JRSoftware.InnoSetup).

.PARAMETER Version
    Version to stamp, e.g. 2.0.0. Default: from the csproj.

.PARAMETER ExtraArgs
    Extra MSBuild arguments (e.g. a custom compiler path on unusual machines).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$proj = Join-Path $root 'src\ClaudeSwitch\ClaudeSwitch.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$art = Join-Path $root 'artifacts'
$pub = Join-Path $art 'win-x64'
New-Item -ItemType Directory -Force -Path $pub | Out-Null

Write-Host "Publishing Claude Switch $Version for win-x64..." -ForegroundColor Cyan
& dotnet publish $proj -c Release -r win-x64 --self-contained -o $pub "-p:Version=$Version" @ExtraArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) not found. Install it: winget install JRSoftware.InnoSetup' }

& $iscc /Qp "/DAppVersion=$Version" "/DSourceExe=$pub\ClaudeSwitch.exe" (Join-Path $PSScriptRoot 'ClaudeSwitch.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

$setup = Join-Path $art "ClaudeSwitch-$Version-windows-x64-setup.exe"
$readme = Join-Path $pub 'README.txt'
Set-Content -Path $readme -Encoding UTF8 -Value @"
Claude Switch $Version (portable) - https://github.com/Ngoni-Sama/claude-switch

Run ClaudeSwitch.exe from any folder. Nothing is installed; settings live in
%APPDATA%\Claude Switch. Prefer the setup for Start-menu shortcuts and an
Add/Remove Programs entry.

Unofficial; not affiliated with Anthropic. MIT License, (c) 2026 Ngoni Maphosa.
"@
Compress-Archive -Path "$pub\ClaudeSwitch.exe", $readme -DestinationPath (Join-Path $art "ClaudeSwitch-$Version-windows-x64-portable.zip") -Force
Compress-Archive -Path $setup -DestinationPath (Join-Path $art "ClaudeSwitch-$Version-windows-x64-setup.zip") -Force

Get-ChildItem $art -File | ForEach-Object { '{0,-55} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
