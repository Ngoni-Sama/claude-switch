<#
.SYNOPSIS
    Defines Export-AppIcon. Dot-source it:  . .\Export-AppIcon.ps1

.DESCRIPTION
    Shared by Setup.ps1 (stable shortcut icon) and Build-Installer.ps1 (exe +
    installer icon).
#>

# Extracts the highest-resolution icon out of an .exe into a standalone .ico.
# This is what makes shortcut icons survive app updates: the Claude .exe lives
# under C:\Program Files\WindowsApps\Claude_<version>__...\app\Claude.exe, and
# that <version> folder is DELETED whenever Claude auto-updates. A shortcut whose
# IconLocation points straight at that versioned path goes blank after the next
# update + icon-cache rebuild (typically noticed after a reboot). Copying the
# icon once to a stable file under bin\ and pointing every shortcut there avoids
# the dangling path entirely.
function Export-AppIcon {
    param([string]$ExePath, [string]$IcoPath, [int]$Size = 256)
    Add-Type -AssemblyName System.Drawing
    $sig = @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)]
public static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon, IntPtr[] phicon, int[] piconid, int nIcons, int flags);
[DllImport("user32.dll")]
public static extern bool DestroyIcon(IntPtr hIcon);
'@
    $api = Add-Type -MemberDefinition $sig -Name 'IconExtract' -Namespace 'Win32Native' -PassThru

    # Writes a 32-bit, full-colour, PNG-compressed .ico (Vista+ format) from a
    # Bitmap. We deliberately do NOT use Icon.Save(): saving an Icon created from
    # an HICON drops the colour plane and writes only the 1-bit AND mask, which is
    # why the icon came out grey. Rendering to a Bitmap and packing the PNG
    # ourselves preserves colour and alpha.
    function Write-IcoFromBitmap {
        param([System.Drawing.Bitmap]$Bmp, [string]$Path)
        $ms = New-Object System.IO.MemoryStream
        $Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $png = $ms.ToArray(); $ms.Dispose()
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
        $bw = New-Object System.IO.BinaryWriter($fs)
        $bw.Write([uint16]0)            # reserved
        $bw.Write([uint16]1)            # type = icon
        $bw.Write([uint16]1)            # image count
        $dim = if ($Bmp.Width -ge 256) { 0 } else { [byte]$Bmp.Width }   # 0 means 256
        $bw.Write([byte]$dim)           # width
        $bw.Write([byte]$dim)           # height
        $bw.Write([byte]0)              # palette count
        $bw.Write([byte]0)              # reserved
        $bw.Write([uint16]1)            # colour planes
        $bw.Write([uint16]32)           # bits per pixel
        $bw.Write([uint32]$png.Length)  # image size
        $bw.Write([uint32]22)           # offset = 6 (dir) + 16 (entry)
        $bw.Write($png)
        $bw.Flush(); $bw.Close()
    }

    $h = New-Object IntPtr[] 1
    $id = New-Object int[] 1
    try {
        $n = $api::PrivateExtractIcons($ExePath, 0, $Size, $Size, $h, $id, 1, 0)
        if ($n -gt 0 -and $h[0] -ne [IntPtr]::Zero) {
            $ico = [System.Drawing.Icon]::FromHandle($h[0])
            $bmp = $ico.ToBitmap()       # full 32bpp colour + alpha
            Write-IcoFromBitmap -Bmp $bmp -Path $IcoPath
            $bmp.Dispose(); $ico.Dispose()
            return $true
        }
    } catch {
    } finally {
        if ($h[0] -ne [IntPtr]::Zero) { $api::DestroyIcon($h[0]) | Out-Null }
    }
    # Fallback: managed helper (lower-res, but still full colour and a stable file).
    try {
        $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($ExePath)
        $bmp = $ico.ToBitmap()
        Write-IcoFromBitmap -Bmp $bmp -Path $IcoPath
        $bmp.Dispose(); $ico.Dispose()
        return $true
    } catch { return $false }
}
