# 从 PNG 生成多尺寸 .ico（16/24/32/48/64/128/256）。
# 用法: powershell -NoProfile -File tools\gen-icon.ps1 -PngPath assets\icon.png -OutIco VirtualDesktopTaskbar\app.ico
# 256 档直接内嵌 PNG（Vista+ 标准），其余档为 32bpp BMP DIB（含全零 AND 掩码，透明由 alpha 通道承担）。
param(
    [Parameter(Mandatory = $true)][string]$PngPath,
    [Parameter(Mandatory = $true)][string]$OutIco
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile((Resolve-Path $PngPath))
$sizes = 16, 24, 32, 48, 64, 128, 256

# 取 32bpp BGRA 逐像素数据，并转为 DIB 要求的自下而上行序（去掉 stride 填充）
function Get-BgraBottomUp([System.Drawing.Bitmap]$bmp) {
    $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $raw = New-Object byte[] ($data.Stride * $bmp.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
        $w = $bmp.Width; $h = $bmp.Height
        $out = New-Object byte[] ($w * $h * 4)
        for ($y = 0; $y -lt $h; $y++) {
            [Array]::Copy($raw, $y * $data.Stride, $out, ($h - 1 - $y) * $w * 4, $w * 4)
        }
        return $out
    }
    finally { $bmp.UnlockBits($data) }
}

$blobs = @()
$entries = @()
$offset = 6 + 16 * $sizes.Count

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()

    if ($size -eq 256) {
        # 256 档内嵌 PNG（Vista+ 标准做法，体积小且无损）
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
        $ms.Dispose()
        $bitCount = 32
    }
    else {
        # 小尺寸为 32bpp BMP DIB：40 字节头（biHeight = 2*高，XOR+AND 两段）+ 像素 + 全零 AND 掩码
        $xor = Get-BgraBottomUp $bmp
        $andRow = (($size + 31) -shr 5) * 4
        $and = New-Object byte[] ($andRow * $size)
        $header = New-Object byte[] 40
        [Array]::Copy([BitConverter]::GetBytes([uint32]40), 0, $header, 0, 4)                       # biSize
        [Array]::Copy([BitConverter]::GetBytes([int32]$size), 0, $header, 4, 4)                     # biWidth
        [Array]::Copy([BitConverter]::GetBytes([int32]($size * 2)), 0, $header, 8, 4)               # biHeight
        [Array]::Copy([BitConverter]::GetBytes([uint16]1), 0, $header, 12, 2)                       # biPlanes
        [Array]::Copy([BitConverter]::GetBytes([uint16]32), 0, $header, 14, 2)                      # biBitCount
        [Array]::Copy([BitConverter]::GetBytes([uint32]($xor.Length + $and.Length)), 0, $header, 20, 4) # biSizeImage
        $data = New-Object byte[] (40 + $xor.Length + $and.Length)
        [Array]::Copy($header, 0, $data, 0, 40)
        [Array]::Copy($xor, 0, $data, 40, $xor.Length)
        [Array]::Copy($and, 0, $data, 40 + $xor.Length, $and.Length)
        $bitCount = 32
    }

    $entries += [byte[]]@(
        ($size -band 0xFF), ($size -band 0xFF), 0, 0,
        0, 1,
        0, 32,
        ($data.Length -band 0xFF), (($data.Length -shr 8) -band 0xFF), (($data.Length -shr 16) -band 0xFF), (($data.Length -shr 24) -band 0xFF),
        ($offset -band 0xFF), (($offset -shr 8) -band 0xFF), (($offset -shr 16) -band 0xFF), (($offset -shr 24) -band 0xFF)
    )
    $blobs += ,$data
    $offset += $data.Length
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = [System.IO.BinaryWriter]::new($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)  # ICONDIR
foreach ($e in $entries) { $w.Write($e) }
foreach ($b in $blobs) { $w.Write($b) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path (Get-Location) $OutIco), $out.ToArray())
$w.Dispose()
$src.Dispose()
Write-Host ("生成 {0}（{1} 字节，{2} 档）" -f $OutIco, $out.Length, $sizes.Count)
