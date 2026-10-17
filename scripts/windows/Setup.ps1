<#
.SYNOPSIS
    Installs "clones" of the Claude Desktop app: one isolated profile + one
    desktop shortcut per account.

.DESCRIPTION
    For each profile name you pass, this script:
      * creates an isolated Chromium data directory under %APPDATA% (required so
        the Cowork VM works -- see the note in the loop below),
      * copies the launcher scripts into -InstallDir\bin (so the shortcuts keep
        working even if you delete this repo),
      * creates a "Claude (<Name>)" shortcut on your Desktop that opens the app
        with that profile, using the real Claude icon,
      * records the profile in bin\profiles.json (merged by name, so re-running
        with other names adds to the list instead of replacing it),
      * builds the tray switcher (bin\ClaudeProfiles.exe) and a "Claude Switcher"
        Desktop shortcut for it: one tray icon listing every profile, with a
        marker on the running ones; click to focus or launch.

    The special profile name "Work" can reuse the account the normally-installed
    app is already signed into (-ReuseDefaultForWork), so you don't have to log
    in again for your main account.

.PARAMETER Profile
    One or more profile names. Default: Work, Personal.

.PARAMETER InstallDir
    Where the launcher scripts (bin\) live. Default: %USERPROFILE%\ClaudeProfiles.
    NOTE: profile *data* always lives under %APPDATA%\<name>, not here -- this is
    required for the Cowork VM to start (the native VM service resolves rootfs.vhdx
    under %APPDATA% regardless of --user-data-dir).

.PARAMETER ReuseDefaultForWork
    Make the "Work" profile reuse the existing %APPDATA%\Claude login instead of
    a fresh, separate one.

.PARAMETER ConfigDir
    Optional hashtable mapping a profile name to a Claude Code / Cowork config
    directory (CLAUDE_CONFIG_DIR). Use this to give a profile its own isolated
    memory + settings store, separate from any other account.

.PARAMETER NoTray
    Skip building the tray switcher and its shortcut.

.PARAMETER TrayAtLogin
    Also start the tray switcher automatically when you sign in to Windows
    (adds a shortcut to your Startup folder; no admin needed).

.EXAMPLE
    .\Setup.ps1
    # Creates "Claude (Work)" and "Claude (Personal)" on the Desktop.

.EXAMPLE
    .\Setup.ps1 -Profile Work,Personal -ReuseDefaultForWork -TrayAtLogin
    # Work keeps the existing %APPDATA%\Claude login; switcher starts at sign-in.

.EXAMPLE
    .\Setup.ps1 -Profile Personal,Side,Client -ReuseDefaultForWork

.EXAMPLE
    # Personal profile gets its own isolated Claude Code memory store:
    .\Setup.ps1 -ConfigDir @{ Personal = "$env:USERPROFILE\.claude-personal" }
#>
[CmdletBinding()]
param(
    [string[]]$Profile = @('Work', 'Personal'),
    [string]$InstallDir = (Join-Path $env:USERPROFILE 'ClaudeProfiles'),
    [switch]$ReuseDefaultForWork,
    [hashtable]$ConfigDir = @{},
    [switch]$NoTray,
    [switch]$TrayAtLogin
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

# Export-AppIcon: extracts the Claude icon to a stable .ico (see that file).
. (Join-Path $scriptRoot 'Export-AppIcon.ps1')

# Stores paths in profiles.json as %APPDATA%\... / %USERPROFILE%\... so the
# file stays readable and portable; the launcher and switcher expand them.
function Compress-EnvPath {
    param([string]$Path)
    if (-not $Path) { return $null }
    foreach ($var in 'APPDATA', 'USERPROFILE') {   # APPDATA first: it's inside USERPROFILE
        $root = [Environment]::GetEnvironmentVariable($var).TrimEnd('\')
        if ($Path -ieq $root -or $Path.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase)) {
            return "%$var%" + $Path.Substring($root.Length)
        }
    }
    return $Path
}

# --- Verify the Claude Desktop app is installed and find its icon ---------
$pkg = Get-AppxPackage -Name '*Claude*' -ErrorAction SilentlyContinue |
    Sort-Object Version -Descending | Select-Object -First 1
if (-not $pkg) {
    throw "Claude Desktop app not found. Install it from https://claude.ai/download (or the Microsoft Store) first."
}
$iconExe = Join-Path $pkg.InstallLocation 'app\Claude.exe'
Write-Host "Found Claude $($pkg.Version) at $($pkg.InstallLocation)" -ForegroundColor Green

# --- Copy launcher scripts to a stable location ---------------------------
$binDir = Join-Path $InstallDir 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
Copy-Item (Join-Path $scriptRoot 'Launch-Claude.ps1') $binDir -Force
Copy-Item (Join-Path $scriptRoot 'launch.vbs') $binDir -Force
$vbs = Join-Path $binDir 'launch.vbs'

# --- Extract the Claude icon to a STABLE path (survives app updates) -------
# See Export-AppIcon above for why pointing IconLocation at the versioned
# WindowsApps .exe makes icons disappear after updates/reboots.
$icoPath = Join-Path $binDir 'claude.ico'
if (Export-AppIcon -ExePath $iconExe -IcoPath $icoPath) {
    $iconLocation = "$icoPath,0"
    Write-Host "Extracted stable icon to $icoPath" -ForegroundColor Green
} else {
    # Last resort: fall back to the versioned exe (may go blank after an update).
    $iconLocation = "$iconExe,0"
    Write-Warning "Could not extract a standalone icon; falling back to the app exe (icon may break after updates)."
}

# --- Create a profile + desktop shortcut for each name --------------------
$desktop = [Environment]::GetFolderPath('Desktop')
$wsh = New-Object -ComObject WScript.Shell
$profileEntries = @()

foreach ($name in $Profile) {
    if ($ReuseDefaultForWork -and $name -ieq 'Work') {
        $dataDir = Join-Path $env:APPDATA 'Claude'
        Write-Host "  [$name] reuses existing login at $dataDir"
    } else {
        # IMPORTANT: isolated profiles must live under %APPDATA% (NOT an arbitrary
        # folder such as -InstallDir). Claude Desktop's "Cowork" feature runs its
        # agent inside a Hyper-V VM, and the native VM service resolves the VM image
        # (rootfs.vhdx) at  %APPDATA%\<profile-name>\vm_bundles  -- it ignores the
        # --user-data-dir flag. If the data dir lives elsewhere, Electron provisions
        # the VM under the data dir but the VM service looks under %APPDATA% and dies
        # with "VHDX file not found", so the Cowork workspace never starts. Putting
        # the data dir under %APPDATA% makes both components agree (exactly how the
        # -ReuseDefaultForWork "Work" profile already behaves).
        $dataDir = Join-Path $env:APPDATA $name
        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        Write-Host "  [$name] isolated profile at $dataDir"
    }

    $lnkPath = Join-Path $desktop "Claude ($name).lnk"
    $sc = $wsh.CreateShortcut($lnkPath)
    $sc.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
    $cfg = $null
    if ($ConfigDir.ContainsKey($name)) {
        $cfg = $ConfigDir[$name]
        New-Item -ItemType Directory -Force -Path $cfg | Out-Null
        $sc.Arguments = '"{0}" "{1}" "{2}"' -f $vbs, $dataDir, $cfg
        Write-Host "      memory/config dir: $cfg"
    } else {
        $sc.Arguments = '"{0}" "{1}"' -f $vbs, $dataDir
    }
    $sc.IconLocation = $iconLocation
    $sc.Description = "Claude Desktop - $name profile"
    $sc.WorkingDirectory = $binDir
    $sc.Save()
    Write-Host "  -> created shortcut: $lnkPath" -ForegroundColor Cyan

    $profileEntries += [pscustomobject]@{
        Name      = $name
        DataDir   = Compress-EnvPath $dataDir
        ConfigDir = Compress-EnvPath $cfg
    }
}

# --- profiles.json (read by the tray switcher) ----------------------------
# Merge by name: keep profiles from earlier runs, replace the ones set up now.
$profilesJson = Join-Path $binDir 'profiles.json'
$merged = @()
if (Test-Path $profilesJson) {
    try {
        # Piping enumerates the array (PS 5.1 emits a JSON array as one object).
        $merged = @(Get-Content $profilesJson -Raw | ConvertFrom-Json | ForEach-Object { $_ } |
            Where-Object { $Profile -notcontains $_.Name })
    } catch {
        Write-Warning "Ignoring unreadable $profilesJson ($($_.Exception.Message))."
    }
}
$merged += $profileEntries
[System.IO.File]::WriteAllText($profilesJson, (ConvertTo-Json -InputObject @($merged)),
    (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $profilesJson ($($merged.Count) profile(s))" -ForegroundColor Green

# --- Tray switcher ---------------------------------------------------------
if (-not $NoTray -and (Test-Path (Join-Path $scriptRoot 'Build-App.ps1'))) {
    $switcherExe = Join-Path $binDir 'ClaudeProfiles.exe'
    # A running switcher locks its exe; stop it, rebuild, then bring it back.
    $wasRunning = @(Get-Process -Name 'ClaudeProfiles' -ErrorAction SilentlyContinue)
    $wasRunning | Stop-Process -Force -ErrorAction SilentlyContinue
    $wasRunning | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    try {
        & (Join-Path $scriptRoot 'Build-App.ps1') -OutputPath $switcherExe -IconPath $icoPath

        $trayLinks = @(Join-Path $desktop 'Claude Switcher.lnk')
        $startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Switcher.lnk'
        if ($TrayAtLogin) { $trayLinks += $startupLnk }
        foreach ($lnk in $trayLinks) {
            $sc = $wsh.CreateShortcut($lnk)
            $sc.TargetPath = $switcherExe
            $sc.WorkingDirectory = $binDir
            $sc.IconLocation = $iconLocation
            $sc.Description = 'Claude Desktop - profile switcher (tray)'
            $sc.Save()
            Write-Host "  -> created shortcut: $lnk" -ForegroundColor Cyan
        }

        Start-Process -FilePath $switcherExe -WorkingDirectory $binDir
        Write-Host "Tray switcher running (look for the Claude icon in the notification area)." -ForegroundColor Green
    } catch {
        Write-Warning "Tray switcher not built: $($_.Exception.Message). Profile shortcuts still work."
    }
}

Write-Host ""
Write-Host "Done. Open each shortcut and sign in to the matching account." -ForegroundColor Green
Write-Host "Tip: clicking a shortcut again just focuses that profile's window (single instance per profile)."
