<#
.SYNOPSIS
    Removes the desktop shortcuts created by Setup.ps1 and drops the profiles
    from the tray switcher's profiles.json. Optionally deletes the profile data
    (logins/history) too. Once no profiles remain, the switcher is stopped and
    its Desktop/Startup shortcuts are removed as well.

.PARAMETER Profile
    Profile names to remove. Default: Work, Personal.

.PARAMETER InstallDir
    Where the launcher scripts (bin\) were installed. Default:
    %USERPROFILE%\ClaudeProfiles. (Profile *data* lives under %APPDATA%\<name>.)

.PARAMETER RemoveData
    Also delete the isolated profile data directories (this signs you out and
    erases that profile's local history). The "Work" profile that reuses
    %APPDATA%\Claude is never deleted.
#>
[CmdletBinding()]
param(
    [string[]]$Profile = @('Work', 'Personal'),
    [string]$InstallDir = (Join-Path $env:USERPROFILE 'ClaudeProfiles'),
    [switch]$RemoveData
)

$desktop = [Environment]::GetFolderPath('Desktop')

foreach ($name in $Profile) {
    $lnk = Join-Path $desktop "Claude ($name).lnk"
    if (Test-Path $lnk) {
        Remove-Item $lnk -Force
        Write-Host "Removed shortcut: $lnk" -ForegroundColor Yellow
    }

    if ($RemoveData) {
        # Profile data lives under %APPDATA%\<name> (see Setup.ps1), not -InstallDir.
        $dataDir = Join-Path $env:APPDATA $name
        if ($name -ieq 'Work' -or $dataDir -ieq (Join-Path $env:APPDATA 'Claude')) {
            # %APPDATA%\Claude is the main login shared with the Start-menu app.
            Write-Host "Skipping data removal for '$name' (shared main login)." -ForegroundColor DarkYellow
        } elseif (Test-Path $dataDir) {
            Remove-Item $dataDir -Recurse -Force
            Write-Host "Removed profile data: $dataDir" -ForegroundColor Yellow
        }
    }
}

# --- Tray switcher: drop these profiles from profiles.json ------------------
# When no profiles are left, remove the switcher's shortcuts and stop it too.
$binDir = Join-Path $InstallDir 'bin'
$profilesJson = Join-Path $binDir 'profiles.json'
$remaining = @()
if (Test-Path $profilesJson) {
    try {
        # Piping enumerates the array (PS 5.1 emits a JSON array as one object).
        $remaining = @(Get-Content $profilesJson -Raw | ConvertFrom-Json | ForEach-Object { $_ } |
            Where-Object { $Profile -notcontains $_.Name })
        [System.IO.File]::WriteAllText($profilesJson, (ConvertTo-Json -InputObject @($remaining)),
            (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Updated $profilesJson ($($remaining.Count) profile(s) left)" -ForegroundColor Yellow
    } catch {
        Write-Warning "Could not update $profilesJson ($($_.Exception.Message))."
        $remaining = @('unknown')   # don't tear down the switcher on a read error
    }
}

if ($remaining.Count -eq 0) {
    Get-Process -Name 'ClaudeProfiles' -ErrorAction SilentlyContinue | Stop-Process -Force
    $trayLinks = @(
        (Join-Path $desktop 'Claude Switcher.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Switcher.lnk')
    )
    foreach ($lnk in $trayLinks) {
        if (Test-Path $lnk) {
            Remove-Item $lnk -Force
            Write-Host "Removed shortcut: $lnk" -ForegroundColor Yellow
        }
    }
}

Write-Host "Done."
