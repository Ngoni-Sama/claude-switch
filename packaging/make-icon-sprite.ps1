<#
.SYNOPSIS
    Builds docs/assets/icons.svg, one sprite holding the glass icons the site uses.

.DESCRIPTION
    The source icons are glass: a translucent fill plus a light edge, drawn with
    gradients. They're authored for dark backgrounds, so the hard-coded stop
    colours are swapped for CSS variables and the page sets them per theme.
    Gradient ids are prefixed per icon so they can't collide in one document.
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ClaudeSwitch\Assets\icons'),
    [string]$Out    = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\assets\icons.svg'),
    # Only these icons, so the page inlines just what it shows.
    [string[]]$Include = @('account', 'clock', 'zap', 'shared', 'restore', 'shield', 'folder', 'lock', 'sparkle')
)

$ErrorActionPreference = 'Stop'

$files = Get-ChildItem $Source -Filter *.svg | Where-Object { $Include.Count -eq 0 -or $Include -contains $_.BaseName } | Sort-Object Name
$symbols = foreach ($file in $files) {
    $id = $file.BaseName
    $svg = [IO.File]::ReadAllText($file.FullName)

    # Inner markup only: drop the <svg> wrapper.
    $inner = [regex]::Match($svg, '(?s)<svg[^>]*>(.*)</svg>').Groups[1].Value.Trim()

    # Make every gradient id unique to this icon.
    foreach ($gid in ([regex]::Matches($inner, 'id="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)) {
        $inner = $inner -replace ('id="' + [regex]::Escape($gid) + '"'), ('id="' + $id + '_' + $gid + '"')
        $inner = $inner -replace ('url\(#' + [regex]::Escape($gid) + '\)'), ('url(#' + $id + '_' + $gid + ')')
    }

    # Glass look, themed by the page: fill gradient + light edge.
    $i = 0
    $inner = [regex]::Replace($inner, '<stop([^>]*?)stop-color="[^"]*"([^>]*?)/>', {
        param($m)
        $script:i++
        $var = switch ($script:i % 4) { 1 { '--ico-fill-a' } 2 { '--ico-fill-b' } 3 { '--ico-edge-a' } 0 { '--ico-edge-b' } }
        '<stop' + $m.Groups[1].Value + 'stop-color="var(' + $var + ')"' + ($m.Groups[2].Value -replace '\s*stop-opacity="[^"]*"', '') + '/>'
    })

    "  <symbol id=""ico-$id"" viewBox=""0 0 24 24"" fill=""none"">`n$inner`n  </symbol>"
}

$sprite = @"
<svg xmlns="http://www.w3.org/2000/svg" width="0" height="0" style="position:absolute;width:0;height:0;overflow:hidden" aria-hidden="true" focusable="false">
<!-- Glass icon sprite, built by packaging/make-icon-sprite.ps1. Colours come
     from the page's --ico-* variables so one set works in light and dark.
     Hidden by size, not display:none: browsers skip gradients inside a
     display:none SVG, and these icons are drawn with gradients. -->
$($symbols -join "`n")
</svg>
"@

New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
[IO.File]::WriteAllText($Out, $sprite, (New-Object Text.UTF8Encoding($false)))
Write-Host "Wrote $Out ($(@($symbols).Count) icons)" -ForegroundColor Green
