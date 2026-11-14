# Gera assets/Vapt.ico (multi-tamanho, entradas PNG) e assets/Vapt.png.
# Desenho: quadrado arredondado em degradê preto -> roxo com um raio branco pequeno no meio ("vapt vupt").
param([string]$OutDir = (Join-Path $PSScriptRoot '..\assets'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size
    $m = [single]([math]::Max(1, $s * 0.06))
    $bg = New-RoundedPath $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    # Degradê preto -> roxo na diagonal.
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 12, 10, 18)), ([System.Drawing.Color]::FromArgb(255, 124, 58, 237))
    $blend = New-Object System.Drawing.Drawing2D.Blend
    $blend.Positions = [single[]](0, 0.3, 0.6, 1)
    $blend.Factors = [single[]](0, 0.04, 0.4, 1)
    $grad.Blend = $blend
    $g.FillPath($grad, $bg)

    # Raio pequeno e centralizado (minimalista), em coordenadas relativas (0..1).
    $pts = @(
        @(0.555, 0.27), @(0.36, 0.53), @(0.485, 0.53),
        @(0.445, 0.73), @(0.64, 0.47), @(0.515, 0.47), @(0.555, 0.27)
    )
    $poly = foreach ($p in $pts) { New-Object System.Drawing.PointF ([single]($p[0] * $s)), ([single]($p[1] * $s)) }
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($white, [System.Drawing.PointF[]]$poly)
    $white.Dispose(); $grad.Dispose(); $bg.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256) { $bmp.Save((Join-Path $OutDir 'Vapt.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'Vapt.ico'), $out.ToArray())
Write-Host "Icone gerado em $OutDir"
