<#
.SYNOPSIS
    Draws assets/icon.ico from code. Run it after changing the artwork.

.DESCRIPTION
    The mark is a monitor with a keyboard under it: the two things Deskside
    looks after. It is drawn as flat shapes with generous margins so it stays
    readable at 16 px, where most tray icons are actually seen.

    System.Drawing cannot write a multi-size .ico, so the file is assembled by
    hand: an ICONDIR header followed by one PNG per size. PNG-compressed icon
    entries are supported from Windows Vista onwards.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\assets\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutFile,
    [int[]]$Sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
)

Add-Type -AssemblyName System.Drawing

if (-not $OutFile) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $OutFile = Join-Path $here 'icon.ico'
}

# Screen face and body: a light face on a dark body reads as "a monitor" even
# when the whole thing is 16 px across.
$body   = [System.Drawing.Color]::FromArgb(255, 32, 38, 46)
$face   = [System.Drawing.Color]::FromArgb(255, 86, 182, 194)
$keys   = [System.Drawing.Color]::FromArgb(255, 60, 70, 82)
$keycap = [System.Drawing.Color]::FromArgb(255, 224, 232, 238)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF $x, $y, $w, $h)); return $p }
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # everything is expressed on a 100x100 grid, then scaled
    $u = $size / 100.0
    function S([single]$v) { return [single]($v * $u) }

    $bodyBrush   = New-Object System.Drawing.SolidBrush $body
    $faceBrush   = New-Object System.Drawing.SolidBrush $face
    $keysBrush   = New-Object System.Drawing.SolidBrush $keys
    $keycapBrush = New-Object System.Drawing.SolidBrush $keycap

    # monitor body
    $p = New-RoundedPath (S 8) (S 10) (S 84) (S 56) (S 9)
    $g.FillPath($bodyBrush, $p); $p.Dispose()

    # screen
    $p = New-RoundedPath (S 16) (S 18) (S 68) (S 40) (S 4)
    $g.FillPath($faceBrush, $p); $p.Dispose()

    # stand
    $g.FillRectangle($bodyBrush, (S 44), (S 66), (S 12), (S 6))

    # keyboard
    $p = New-RoundedPath (S 12) (S 74) (S 76) (S 18) (S 4)
    $g.FillPath($keysBrush, $p); $p.Dispose()

    # keycaps: only drawn when there are enough pixels to keep them distinct
    if ($size -ge 32) {
        $kw = S 9; $kh = S 4; $gap = S 3.2
        $x0 = S 18; $y0 = S 79
        for ($row = 0; $row -lt 2; $row++) {
            for ($col = 0; $col -lt 5; $col++) {
                $x = $x0 + $col * ($kw + $gap) + ($(if ($row -eq 1) { S 4 } else { 0 }))
                $y = $y0 + $row * ($kh + $gap)
                if ($x + $kw -gt (S 84)) { continue }
                $p = New-RoundedPath $x $y $kw $kh (S 1)
                $g.FillPath($keycapBrush, $p); $p.Dispose()
            }
        }
    } else {
        $g.FillRectangle($keycapBrush, (S 20), (S 80), (S 60), (S 6))
    }

    $bodyBrush.Dispose(); $faceBrush.Dispose(); $keysBrush.Dispose(); $keycapBrush.Dispose()
    $g.Dispose()
    return $bmp
}

# --- assemble the .ico -------------------------------------------------------
# Entries below 256 px are written as classic DIB bitmaps: GDI+ (and therefore
# System.Drawing.Icon on .NET Framework) cannot decode PNG-compressed entries
# at those sizes. Only the 256 px entry uses PNG, which is the convention.

function Get-DibEntry([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)   # AND mask rows are 4-byte aligned
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms

    # BITMAPINFOHEADER: height counts the colour bitmap plus the AND mask
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)                                  # BI_RGB
    $bw.Write([uint32]($w * $h * 4 + $maskRow * $h))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # colour data, bottom-up, already BGRA in memory
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $w * 4) }

    # AND mask: unused with an alpha channel, but the format requires it
    $zero = New-Object byte[] $maskRow
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($zero) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $bytes
}

function Get-PngEntry([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

$entries = @()
foreach ($s in $Sizes) {
    $bmp = New-IconBitmap $s
    $bytes = if ($s -ge 256) { Get-PngEntry $bmp } else { Get-DibEntry $bmp }
    $entries += , @{ Size = $s; Bytes = $bytes }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0)                 # reserved
$w.Write([uint16]1)                 # type: icon
$w.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim)            # width  (0 means 256)
    $w.Write([byte]$dim)            # height
    $w.Write([byte]0)               # palette colours
    $w.Write([byte]0)               # reserved
    $w.Write([uint16]1)             # colour planes
    $w.Write([uint16]32)            # bits per pixel
    $w.Write([uint32]$e.Bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $w.Write($e.Bytes) }
$w.Flush()

[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
$w.Dispose(); $out.Dispose()

"Written {0} ({1} bytes, {2} sizes: {3})" -f $OutFile, (Get-Item $OutFile).Length, $entries.Count, ($Sizes -join ', ')
