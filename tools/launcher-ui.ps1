# Test driver for the launcher window on the VPS.
#   launcher-ui.ps1 shot  <png>       screenshot of the launcher window
#   launcher-ui.ps1 click <x> <y>     click at window-relative coordinates
#   launcher-ui.ps1 rect              print the window rectangle
#   launcher-ui.ps1 log   <dir>       tail of <dir>\.originsupdate\launcher.log
param([Parameter(Mandatory)][string]$Action, [string]$A, [string]$B)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not ('LauncherUi' -as [type])) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class LauncherUi {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    public static bool Focus(IntPtr h) {
        if (GetForegroundWindow() == h) return true;
        keybd_event(0x12, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 2, IntPtr.Zero);
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(250);
        return GetForegroundWindow() == h;
    }
    public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2,0,0,0,IntPtr.Zero); mouse_event(4,0,0,0,IntPtr.Zero); }
}
'@
}

function Get-LauncherWindow {
    $p = Get-Process OriginsLauncher -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw 'launcher window not found' }
    $r = New-Object LauncherUi+RECT
    [void][LauncherUi]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    [pscustomobject]@{ Handle = $p.MainWindowHandle; Left = $r.L; Top = $r.T; Width = $r.R - $r.L; Height = $r.B - $r.T; Title = $p.MainWindowTitle }
}

switch ($Action) {
    'rect' { Get-LauncherWindow }
    'shot' {
        $w = Get-LauncherWindow
        [void][LauncherUi]::Focus($w.Handle)
        $bmp = New-Object System.Drawing.Bitmap $w.Width, $w.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($w.Left, $w.Top, 0, 0, $bmp.Size)
        $bmp.Save($A, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        $A
    }
    'click' {
        $w = Get-LauncherWindow
        if (-not [LauncherUi]::Focus($w.Handle)) { throw 'launcher is not in the foreground' }
        [LauncherUi]::Click($w.Left + [int]$A, $w.Top + [int]$B)
    }
    'log' { Get-Content (Join-Path $A '.originsupdate\launcher.log') -Tail 30 }
}
