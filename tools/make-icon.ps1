# Renders the Origins shield logo (same shapes as the site's favicon.svg) into a multi-size
# PNG-compressed .ico for the launcher.
param([string]$Out = "$PSScriptRoot\..\src\OriginsLauncher\logo.ico")
Add-Type -AssemblyName System.Drawing

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0
    $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $size), ([System.Drawing.Color]::FromArgb(246, 217, 139)), ([System.Drawing.Color]::FromArgb(184, 134, 47))
    # shield: M32 3 L56 14 V31 C56 46 46 56 32 61 C18 56 8 46 8 31 V14 Z
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddLine(32*$s, 3*$s, 56*$s, 14*$s)
    $p.AddLine(56*$s, 14*$s, 56*$s, 31*$s)
    $p.AddBezier(56*$s, 31*$s, 56*$s, 46*$s, 46*$s, 56*$s, 32*$s, 61*$s)
    $p.AddBezier(32*$s, 61*$s, 18*$s, 56*$s, 8*$s, 46*$s, 8*$s, 31*$s)
    $p.AddLine(8*$s, 31*$s, 8*$s, 14*$s)
    $p.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 16, 10))), $p)
    $g.DrawPath((New-Object System.Drawing.Pen $gold, ([Math]::Max(1.0, 3*$s))), $p)
    # diamond: M32 14 L40 30 L32 50 L24 30 Z
    $d = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d.AddPolygon([System.Drawing.PointF[]]@((New-Object System.Drawing.PointF (32*$s), (14*$s)), (New-Object System.Drawing.PointF (40*$s), (30*$s)), (New-Object System.Drawing.PointF (32*$s), (50*$s)), (New-Object System.Drawing.PointF (24*$s), (30*$s))))
    $g.FillPath($gold, $d)
    $pen = New-Object System.Drawing.Pen $gold, ([Math]::Max(1.0, 3*$s))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, 18*$s, 27*$s, 46*$s, 27*$s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Vista+ icons: PNG for the 256 px entry
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Classic DIB entry (System.Drawing/WinForms cannot read PNG entries): BITMAPINFOHEADER,
        # 32bpp BGRA bottom-up pixels, then an all-zero AND mask (alpha carries transparency).
        $w = New-Object System.IO.BinaryWriter $ms
        $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2))
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        $w.Write([uint32]($size * $size * 4 + $maskStride * $size))
        $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            }
        }
        $w.Write((New-Object byte[] ($maskStride * $size)))
        $w.Flush()
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @(foreach ($sz in $sizes) { ,(Render $sz) })
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Output "icon written: $Out"
