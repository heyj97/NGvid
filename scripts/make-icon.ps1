# 앱 로고(포인트 컬러의 둥근 사각형 + 재생 삼각형)를 여러 크기로 그려 .ico 하나로 묶는다.
param([string]$Out, [string]$Preview)
Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $s = $size / 24.0
    $r = 6 * $s; $d = 2 * $r; $e = $size - $d
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($e, 0, $d, $d, 270, 90)
    $path.AddArc($e, $e, $d, $d, 0, 90); $path.AddArc(0, $e, $d, $d, 90, 90); $path.CloseFigure()
    $accent = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0xD6, 0xF6, 0x76))
    $dark = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0x10, 0x11, 0x14))
    $g.FillPath($accent, $path)
    $points = [Drawing.PointF[]]@(
        (New-Object Drawing.PointF (9.2 * $s), (7.2 * $s)),
        (New-Object Drawing.PointF (17.2 * $s), (12 * $s)),
        (New-Object Drawing.PointF (9.2 * $s), (16.8 * $s)))
    $g.FillPolygon($dark, $points)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256 -and $Preview) { $bmp.Save($Preview) }
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (Draw $_) }

$file = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
[IO.File]::WriteAllBytes($Out, $file.ToArray())
"wrote $Out ($($file.Length) bytes, $($sizes.Count) sizes)"
