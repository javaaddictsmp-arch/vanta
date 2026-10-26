# Renders the Vanta logo (same geometry as the VantaMark resource in Themes/Theme.xaml)
# into PNGs and a multi-size .ico. Run from Windows PowerShell:  powershell -File Assets\make-icon.ps1
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$out = Split-Path -Parent $MyInvocation.MyCommand.Path

$vPath    = 'M 14,20 L 34,20 L 50,63 L 66,20 L 86,20 L 59,84 L 41,84 Z'
$slashCut = 'M -5,51 L 105,33 L 105,40 L -5,58 Z'   # diagonal "speed" cut through both arms of the V

function New-Mark([bool]$withCut) {
    $v = [System.Windows.Media.Geometry]::Parse($vPath)
    if ($withCut) {
        $cut = [System.Windows.Media.Geometry]::Parse($slashCut)
        $v = New-Object System.Windows.Media.CombinedGeometry([System.Windows.Media.GeometryCombineMode]::Exclude, $v, $cut)
    }
    $v
}

function Render([int]$size, [bool]$tile) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = $size / 100.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($s, $s)))

    if ($tile) {
        $bg = New-Object System.Windows.Media.LinearGradientBrush(
            [System.Windows.Media.Color]::FromRgb(0x1B, 0x16, 0x33), [System.Windows.Media.Color]::FromRgb(0x0C, 0x10, 0x1C), 90)
        $dc.DrawRoundedRectangle($bg, $null, (New-Object System.Windows.Rect(0, 0, 100, 100)), 22, 22)
        # inset the mark inside the tile
        $dc.PushTransform((New-Object System.Windows.Media.TransformGroup))
        $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform(12, 13)))
        $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform(0.76, 0.76)))
    }

    $grad = New-Object System.Windows.Media.LinearGradientBrush
    $grad.StartPoint = New-Object System.Windows.Point(0, 0)
    $grad.EndPoint = New-Object System.Windows.Point(1, 1)
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xA7, 0x8B, 0xFA), 0)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x7C, 0x5C, 0xFF), 0.45)))
    $grad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x22, 0xD3, 0xEE), 1)))
    $dc.DrawGeometry($grad, $null, (New-Mark ($size -ge 32)))   # the cut is invisible below 32 px
    $dc.Close()

    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

# PNGs for README / website / installer
[IO.File]::WriteAllBytes((Join-Path $out 'vanta-logo.png'), (Render 512 $true))
[IO.File]::WriteAllBytes((Join-Path $out 'vanta-mark.png'), (Render 512 $false))

# Multi-size ICO with PNG-compressed entries (supported since Windows Vista)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (Render $_ $true) }
$fs = [IO.File]::Create((Join-Path $out 'vanta.ico'))
$w = New-Object IO.BinaryWriter($fs)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $images[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Close()
"Wrote vanta.ico, vanta-logo.png, vanta-mark.png to $out"
