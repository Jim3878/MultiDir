<#
.SYNOPSIS
  Draws the MultiDir app icon with WPF and writes src/MultiDir/Assets/MultiDir.ico (16..256 px, PNG entries)
  plus a 256 px preview PNG. Re-run after changing the drawing below.

  Design: a blue rounded tile; on it a white folder whose body is split into four panes (Q-dir style grid),
  the top-left pane in amber = the focused pane.
#>
Add-Type -AssemblyName PresentationCore, WindowsBase, PresentationFramework

$assets = Join-Path $PSScriptRoot "..\src\MultiDir\Assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function Color([string]$hex) { [System.Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush([string]$hex) { $b = New-Object System.Windows.Media.SolidColorBrush (Color $hex); $b.Freeze(); $b }

# Draws on a 256 x 256 canvas; smaller sizes are the same drawing scaled down, with thicker
# details for 16/24 px so the grid stays readable.
function Draw-Icon($dc, [int]$size) {
  $small = $size -le 24
  $bg = New-Object System.Windows.Media.LinearGradientBrush ((Color "#4F8BFF"), (Color "#2350C8"), 45.0)
  $dc.DrawRoundedRectangle($bg, $null, (New-Object System.Windows.Rect 8, 8, 240, 240), 56, 56)

  $white = Brush "#FFFFFF"
  # folder tab + body
  $dc.DrawRoundedRectangle($white, $null, (New-Object System.Windows.Rect 40, 62, 82, 40), 12, 12)
  $dc.DrawRoundedRectangle($white, $null, (New-Object System.Windows.Rect 40, 82, 176, 124), 16, 16)

  # 2 x 2 panes inside the body
  $pad = if ($small) { 18 } else { 14 }
  $gap = if ($small) { 14 } else { 10 }
  $x0 = 40 + $pad; $y0 = 82 + $pad
  $w = (176 - 2 * $pad - $gap) / 2
  $h = (124 - 2 * $pad - $gap) / 2
  $pane = Brush "#2F62E0"
  $focus = Brush "#FFB938"
  $r = if ($small) { 4 } else { 6 }
  $dc.DrawRoundedRectangle($focus, $null, (New-Object System.Windows.Rect $x0, $y0, $w, $h), $r, $r)
  $dc.DrawRoundedRectangle($pane, $null, (New-Object System.Windows.Rect ($x0 + $w + $gap), $y0, $w, $h), $r, $r)
  $dc.DrawRoundedRectangle($pane, $null, (New-Object System.Windows.Rect $x0, ($y0 + $h + $gap), $w, $h), $r, $r)
  $dc.DrawRoundedRectangle($pane, $null, (New-Object System.Windows.Rect ($x0 + $w + $gap), ($y0 + $h + $gap), $w, $h), $r, $r)
}

function Render-Png([int]$size) {
  $visual = New-Object System.Windows.Media.DrawingVisual
  $dc = $visual.RenderOpen()
  $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 256.0), ($size / 256.0)))
  Draw-Icon $dc $size
  $dc.Pop()
  $dc.Close()
  $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
  $bmp.Render($visual)
  $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
  $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
  $ms = New-Object System.IO.MemoryStream
  $enc.Save($ms)
  , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @{}
foreach ($s in $sizes) { $images[$s] = Render-Png $s }

# ICO container: header, directory, then PNG blobs.
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
  $data = $images[$s]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
  $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($images[$s]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets "MultiDir.ico"), $ms.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $assets "MultiDir-256.png"), $images[256])
"icon written: $(Join-Path $assets 'MultiDir.ico')"
