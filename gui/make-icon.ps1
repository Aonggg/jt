# Regenerates gui\jt.ico: a blue rounded square with "jt" at 16/24/32/48 px
# (32bpp DIB) plus a 256 px PNG entry. Needs only System.Drawing.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $radius = [Math]::Max(2, $size / 5)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $d = $radius * 2
    $path.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
    $g.FillPath($brush, $path)
    $font = New-Object System.Drawing.Font 'Segoe UI', ([float]($size * 0.58)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'
    $textRect = New-Object System.Drawing.RectangleF 0, ([float](-$size * 0.04)), $size, $size
    $g.DrawString('jt', $font, [System.Drawing.Brushes]::White, $textRect, $format)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Vista+ icons may store the big image as PNG.
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Classic 32bpp DIB (BITMAPINFOHEADER with doubled height, bottom-up BGRA, then a 1bpp AND mask):
        # GDI+ Icon.ToBitmap cannot read PNG entries, so the sizes WinForms uses stay uncompressed.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]($size * $size * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int](([Math]::Ceiling($size / 32.0)) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$images = @()
foreach ($s in $sizes) { $images += ,(Draw $s) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'jt.ico'), $out.ToArray())
Write-Host "wrote $(Join-Path $PSScriptRoot 'jt.ico') ($($out.Length) bytes)"
