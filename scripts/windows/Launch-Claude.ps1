<#
.SYNOPSIS
    Launches the Claude Desktop (Windows MSIX) app with an isolated profile.

.DESCRIPTION
    The Claude Desktop app is built on Electron/Chromium, which accepts the
    standard `--user-data-dir` flag. Each distinct data directory gets its own
    Chromium "singleton" lock, so multiple instances — each signed into a
    different account — can run side by side.

    This script resolves the MSIX executable dynamically via Get-AppxPackage so
    it keeps working after the app updates (the WindowsApps path contains a
    version number that changes on every update).

    Single-instance guard: launches for the same profile are serialised by a
    per-profile named mutex. Whoever holds it first checks whether a Claude
    main process for that data dir is already running; if so it focuses that
    window instead of starting another one, and if not it launches and keeps
    holding the mutex until the new window appears. A double-click (or a
    shortcut + tray click) can therefore never race two launches on one
    --user-data-dir.

.PARAMETER ProfileDir
    Absolute path to the data directory for this instance. A fresh directory =
    a fresh, isolated login. Point it at "$env:APPDATA\Claude" to reuse the
    account that the normally-installed app is already signed into.
    Environment variables (e.g. %APPDATA%) are expanded.

.PARAMETER ConfigDir
    Optional: give this instance its own Claude Code config + memory store by
    pointing CLAUDE_CONFIG_DIR at a dedicated directory.

.EXAMPLE
    .\Launch-Claude.ps1 -ProfileDir "$env:APPDATA\Personal"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ProfileDir,

    # Optional: give this instance its own Claude Code / Cowork config + memory
    # store by pointing CLAUDE_CONFIG_DIR at a dedicated directory. Useful to
    # keep a personal profile's memory separate from a work one.
    [string]$ConfigDir
)

function Show-Error {
    param([string]$Message)
    try {
        Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
        [System.Windows.MessageBox]::Show($Message, 'claude-desktop-clone', 'OK', 'Error') | Out-Null
    } catch {
        Write-Error $Message
    }
}

function ConvertTo-NormalPath {
    param([string]$Path)
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    return [System.IO.Path]::GetFullPath($expanded).TrimEnd('\')
}

# Returns the --user-data-dir an Electron command line was started with, or
# $null if it has none. Handles  --user-data-dir="C:\a b",  --user-data-dir=C:\x
# and  "--user-data-dir=C:\a b".
function Get-UserDataDirArg {
    param([string]$CommandLine)
    if (-not $CommandLine) { return $null }
    if ($CommandLine -match '"--user-data-dir=([^"]+)"') { return $Matches[1] }
    if ($CommandLine -match '--user-data-dir="([^"]+)"') { return $Matches[1] }
    if ($CommandLine -match '--user-data-dir=([^\s"]+)') { return $Matches[1] }
    return $null
}

# Finds the Electron *main* (browser) process running on $DataDir. Child
# processes (renderer, gpu, utility, crashpad) carry --type=...; the main one
# doesn't. The plain Start-menu launch has no --user-data-dir at all, so for the
# default %APPDATA%\Claude dir a main process without the flag also counts.
# Matching on ExecutablePath keeps Claude Code's own claude.exe (same image
# name, lives under %APPDATA%\Claude\claude-code) out of the results.
function Get-ProfileMainProcess {
    param([string]$DataDir, [string]$ExePath)
    $defaultDir = ConvertTo-NormalPath (Join-Path $env:APPDATA 'Claude')
    $isDefault = $DataDir -ieq $defaultDir
    $procs = Get-CimInstance Win32_Process -Filter "Name='Claude.exe'" -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        $path = $p.ExecutablePath
        $isDesktopApp = ($path -and ($path -ieq $ExePath -or
            $path -match '\\WindowsApps\\Claude_[^\\]+\\app\\Claude\.exe$'))
        if (-not $isDesktopApp) { continue }
        if ($p.CommandLine -match '--type=') { continue }

        $arg = Get-UserDataDirArg $p.CommandLine
        $matched = if ($arg) {
            try { (ConvertTo-NormalPath $arg) -ieq $DataDir } catch { $false }
        } else { $isDefault }
        if ($matched) { return $p }
    }
    return $null
}

function Initialize-Win32 {
    if ('ClaudeClone.Win32' -as [type]) { return }
    Add-Type -Namespace ClaudeClone -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
'@
}

# Brings the profile's window to the front. Returns $false when there is no
# visible window (e.g. closed to the tray) or Windows refused the focus change.
function Set-ProfileWindowFocus {
    param([int]$ProcessId)
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc -or $proc.MainWindowHandle -eq [IntPtr]::Zero) { return $false }
    Initialize-Win32
    $hwnd = $proc.MainWindowHandle
    if ([ClaudeClone.Win32]::IsIconic($hwnd)) {
        [ClaudeClone.Win32]::ShowWindowAsync($hwnd, 9) | Out-Null   # SW_RESTORE
    }
    [ClaudeClone.Win32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 150
    return ([ClaudeClone.Win32]::GetForegroundWindow() -eq $hwnd)
}

# 1) Preferred: resolve via the Appx package (robust against version changes).
$exe = $null
try {
    $pkg = Get-AppxPackage -Name '*Claude*' -ErrorAction Stop |
        Sort-Object Version -Descending | Select-Object -First 1
    if ($pkg) {
        $candidate = Join-Path $pkg.InstallLocation 'app\Claude.exe'
        if (Test-Path $candidate) { $exe = $candidate }
    }
} catch { }

# 2) Fallback: scan WindowsApps directly (newest version first).
if (-not $exe) {
    $exe = Get-ChildItem 'C:\Program Files\WindowsApps\Claude_*__*\app\Claude.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $exe -or -not (Test-Path $exe)) {
    Show-Error "Claude Desktop app was not found.`n`nInstall it from the Microsoft Store / claude.ai/download first, then run again."
    exit 1
}

$ProfileDir = ConvertTo-NormalPath $ProfileDir
# Quoted so data dirs containing spaces (e.g. "C:\Users\Jane Doe\...") survive.
$userDataArg = "--user-data-dir=`"$ProfileDir`""

# --- Single-instance guard -------------------------------------------------
# Mutex name = hash of the normalised path (mutex names can't contain '\' and
# paths can exceed the name length limit). Local\ = per logon session, which is
# all we need: two users never share an %APPDATA%.
$sha = [System.Security.Cryptography.SHA1]::Create()
$hash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($ProfileDir.ToLowerInvariant())) |
    ForEach-Object { $_.ToString('x2') })
$mutex = New-Object System.Threading.Mutex($false, "Local\ClaudeDesktopClone_$hash")
$owned = $false
try {
    # Another launcher for this profile may be mid-startup; wait for it to
    # finish rather than launching in parallel.
    try { $owned = $mutex.WaitOne([TimeSpan]::FromSeconds(45)) }
    catch [System.Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) {
        Show-Error "Claude ($ProfileDir) is still starting.`n`nWait for its window to appear."
        exit 1
    }

    $running = Get-ProfileMainProcess -DataDir $ProfileDir -ExePath $exe
    if ($running) {
        if (-not (Set-ProfileWindowFocus -ProcessId $running.ProcessId)) {
            # No visible window (closed to tray) or focus was refused. Hand off
            # through Chromium's process singleton: this Claude.exe sees the lock
            # held, forwards to the running instance (which shows its window) and
            # exits at once. It never becomes a second UI on this data dir.
            Start-Process -FilePath $exe -ArgumentList $userDataArg
        }
        exit 0
    }

    # Ensure the isolated data directory exists.
    New-Item -ItemType Directory -Force -Path $ProfileDir | Out-Null

    # Optionally isolate Claude Code / Cowork config + memory for this instance.
    # Start-Process inherits this process's environment, so the launched app (and
    # any claude-code it spawns) picks up CLAUDE_CONFIG_DIR.
    if ($ConfigDir) {
        $ConfigDir = ConvertTo-NormalPath $ConfigDir
        New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
        $env:CLAUDE_CONFIG_DIR = $ConfigDir
    }

    # Launch. Different --user-data-dir => separate singleton lock => parallel instance.
    $proc = Start-Process -FilePath $exe -ArgumentList $userDataArg -PassThru

    # Keep the mutex until the window exists, so a second click during startup
    # waits above and then focuses this window instead of racing it.
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
        if ($proc.HasExited -or $proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
