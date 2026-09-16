# Screenshot the FenBrowser window from its own backing store.
#
#   pwsh scripts/capture_window_printwindow.ps1 -Out logs/shot.png
#
# capture_window.ps1 raises the window and copies screen pixels, so it captures
# whatever is actually on screen at those coordinates and steals focus doing it.
# PrintWindow with PW_RENDERFULLCONTENT asks the window to render itself, so the
# result contains only that window - correct when it is occluded, and it never
# picks up anything else the user has open.

param([string]$Out = "logs/window.png")

Add-Type -AssemblyName System.Drawing

$sig = @'
using System;
using System.Runtime.InteropServices;
public class WinPrint {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
'@
Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue

$proc = Get-Process -Name FenBrowser.Host -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "FenBrowser window not found"; exit 1 }

$r = New-Object WinPrint+RECT
[void][WinPrint]::GetWindowRect($proc.MainWindowHandle, [ref]$r)
$w = $r.right - $r.left; $h = $r.bottom - $r.top

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT (0x2) is what makes this work for a GPU-composited window.
$ok = [WinPrint]::PrintWindow($proc.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc)

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
$full = Join-Path (Resolve-Path -LiteralPath $dir).Path (Split-Path -Leaf $Out)
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("saved {0} ({1}x{2}) printWindow={3} from '{4}'" -f $Out, $w, $h, $ok, $proc.MainWindowTitle)
