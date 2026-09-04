# Crop a region out of a screenshot and scale it up, so small spacing defects
# can be judged instead of guessed at.
#
#   pwsh scripts/crop_zoom.ps1 -In logs/hn.png -Out logs/hn_header.png -X 95 -Y 85 -W 1090 -H 35 -Scale 3

param(
  [string]$In, [string]$Out,
  [int]$X = 0, [int]$Y = 0, [int]$W = 200, [int]$H = 100, [int]$Scale = 3
)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $In).Path)
$rect = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
$crop = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($crop)
$g.DrawImage($src, (New-Object System.Drawing.Rectangle(0,0,$W,$H)), $rect, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$big = New-Object System.Drawing.Bitmap(($W*$Scale), ($H*$Scale))
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g2.DrawImage($crop, 0, 0, ($W*$Scale), ($H*$Scale))
$g2.Dispose()
$dir = Split-Path -Parent $Out
$big.Save((Resolve-Path -LiteralPath $dir).Path + "\" + (Split-Path -Leaf $Out), [System.Drawing.Imaging.ImageFormat]::Png)
$src.Dispose(); $crop.Dispose(); $big.Dispose()
Write-Output ("cropped {0} -> {1} ({2}x{3} @{4}x)" -f $In, $Out, $W, $H, $Scale)
