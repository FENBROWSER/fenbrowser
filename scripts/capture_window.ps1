# Screenshot the FenBrowser window so a rendering change can be checked against
# a reference instead of described from memory.
#
#   pwsh scripts/capture_window.ps1 -Out logs/shot.png

param([string]$Out = "logs/window.png")

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$sig = @'
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue

$proc = Get-Process -Name FenBrowser.Host -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "FenBrowser window not found"; exit 1 }

[void][WinCap]::SetForegroundWindow($proc.MainWindowHandle)
Start-Sleep -Milliseconds 600

$r = New-Object WinCap+RECT
[void][WinCap]::GetWindowRect($proc.MainWindowHandle, [ref]$r)
$w = $r.right - $r.left; $h = $r.bottom - $r.top

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.left, $r.top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
$bmp.Save((Resolve-Path -LiteralPath (Split-Path -Parent $Out)).Path + "\" + (Split-Path -Leaf $Out),
          [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("saved {0} ({1}x{2}) from '{3}'" -f $Out, $w, $h, $proc.MainWindowTitle)
