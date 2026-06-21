# Rasterizes the Molca family SVG icons to PNG for use with [Icon] attributes.
# Outputs 128x128 PNGs next to the svg/ folder. Run from anywhere.
#
#   pwsh ./rasterize-icons.ps1            # uses ImageMagick if present, else Inkscape
#   pwsh ./rasterize-icons.ps1 -Size 256  # custom edge size

param([int]$Size = 128)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$svgDir  = Join-Path $root 'svg'
$outDir  = $root

$magick   = Get-Command magick   -ErrorAction SilentlyContinue
$inkscape = Get-Command inkscape -ErrorAction SilentlyContinue

# Fall back to a Program Files install if not on PATH.
if (-not $magick) {
    $found = Get-ChildItem 'C:\Program Files','C:\Program Files (x86)' -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -match 'ImageMagick' |
        ForEach-Object { Join-Path $_.FullName 'magick.exe' } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) { $magick = $found }
}

if (-not $magick -and -not $inkscape) {
    throw "Neither ImageMagick ('magick') nor Inkscape found on PATH or in Program Files."
}

$magickExe = if ($magick -is [System.Management.Automation.CommandInfo]) { $magick.Source } else { $magick }

Get-ChildItem -Path $svgDir -Filter '*.svg' | ForEach-Object {
    $svg = $_.FullName
    $png = Join-Path $outDir ($_.BaseName + '.png')

    if ($magick) {
        & $magickExe -background none -density 384 $svg -resize "${Size}x${Size}" $png
    } else {
        & inkscape $svg --export-type=png --export-width=$Size --export-height=$Size --export-filename=$png | Out-Null
    }
    Write-Host "  $($_.BaseName).png  ($Size x $Size)"
}

Write-Host "Done. Re-import in Unity, then set Texture Type = 'Editor GUI and Legacy GUI'."
