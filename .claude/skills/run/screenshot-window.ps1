<#
Background screenshot of a native window (e.g. FileLocker.exe's WPF main window) using the
PrintWindow API. Does NOT call SetForegroundWindow/ShowWindow, so it never steals focus or
interrupts whatever the user is doing.

Requires the target process to already have a MainWindowHandle (window is actually open, not
minimized to tray / not created yet).

Usage:
  pwsh screenshot-window.ps1 -ProcessName FileLocker -OutputPath C:\path\to\out.png
#>
param(
  [Parameter(Mandatory = $true)][string]$ProcessName,
  [Parameter(Mandatory = $true)][string]$OutputPath
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ScreenshotWin32 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
  Write-Error "Process not found: $ProcessName"
  exit 1
}
if ($proc.MainWindowHandle -eq [IntPtr]::Zero) {
  Write-Error "$ProcessName has no main window handle (window may not exist yet, or was closed to tray)"
  exit 1
}

$hwnd = $proc.MainWindowHandle
$rect = New-Object ScreenshotWin32+RECT
[ScreenshotWin32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

if ($width -le 0 -or $height -le 0) {
  Write-Error "Unreasonable window size ($width x $height) - window may be minimized, PrintWindow can't capture that"
  exit 1
}

$bmp = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT (2): also captures DWM-composited content (e.g. hardware-accelerated
# WebView2 rendering) - the older PW_CLIENTONLY (1) flag often just captures a blank area
# for windows like that.
$ok = [ScreenshotWin32]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)

if (-not $ok) {
  Write-Error "PrintWindow call failed"
  $g.Dispose(); $bmp.Dispose()
  exit 1
}

$bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Output "Saved: $OutputPath"
