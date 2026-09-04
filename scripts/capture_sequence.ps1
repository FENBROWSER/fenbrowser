# Capture the FenBrowser window repeatedly so a "loads late" complaint can be
# timed instead of estimated. Each shot is stamped with ms since capture start.
#
#   pwsh scripts/capture_sequence.ps1 -Count 12 -IntervalMs 700 -OutDir logs/seq

param([int]$Count = 10, [int]$IntervalMs = 800, [string]$OutDir = "logs/seq")

Add-Type -AssemblyName System.Drawing
$sig = @'
using System;
using System.Runtime.InteropServices;
public class SeqCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force $OutDir | Out-Null }
$full = (Resolve-Path -LiteralPath $OutDir).Path

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$shot = 0
while ($shot -lt $Count) {
    $proc = Get-Process -Name FenBrowser.Host -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($proc) {
        if ($shot -eq 0) { [void][SeqCap]::SetForegroundWindow($proc.MainWindowHandle) }
        $r = New-Object SeqCap+RECT
        [void][SeqCap]::GetWindowRect($proc.MainWindowHandle, [ref]$r)
        $w = $r.right - $r.left; $h = $r.bottom - $r.top
        if ($w -gt 0 -and $h -gt 0) {
            $bmp = New-Object System.Drawing.Bitmap($w, $h)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($r.left, $r.top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
            $ms = [int]$sw.ElapsedMilliseconds
            $name = "{0}\shot_{1:d2}_{2}ms.png" -f $full, $shot, $ms
            $bmp.Save($name, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            Write-Output ("shot {0} at {1}ms" -f $shot, $ms)
        }
    }
    $shot++
    Start-Sleep -Milliseconds $IntervalMs
}
