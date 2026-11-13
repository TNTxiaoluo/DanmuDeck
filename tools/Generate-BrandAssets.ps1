param(
    [string]$ProjectRoot = (Join-Path $PSScriptRoot '..\src\BiliXbox'),
    [string]$PngEncoder = (Join-Path $PSScriptRoot '..\.tools\recording-media\imageio_ffmpeg\binaries\ffmpeg-win-x86_64-v7.1.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# BrandIcon is the user's unmodified artwork. BrandMark is the selected transparent
# background-extraction edit. Keep both masters; generation only resamples/pads them.
$assetDirectory = Join-Path $ProjectRoot 'Assets'
$iconPath = Join-Path $assetDirectory 'BrandIcon.png'
$markPath = Join-Path $assetDirectory 'BrandMark.png'
foreach ($masterPath in @($iconPath, $markPath)) {
    if (-not (Test-Path -LiteralPath $masterPath -PathType Leaf)) {
        throw "Missing brand master: $masterPath"
    }
}
$icon = [Drawing.Bitmap]::new($iconPath)
$mark = [Drawing.Bitmap]::new($markPath)

function Save-BrandAsset {
    param([Drawing.Image]$Source, [string]$Name, [int]$Width, [int]$Height, [double]$Fill = 1.0)
    $canvas = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $edgeWrap = [Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $edgeWrap.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $ratio = [Math]::Min($Width / [double]$Source.Width, $Height / [double]$Source.Height) * $Fill
        $drawWidth = [int][Math]::Round($Source.Width * $ratio)
        $drawHeight = [int][Math]::Round($Source.Height * $ratio)
        $destination = [Drawing.Rectangle]::new([int](($Width - $drawWidth) / 2), [int](($Height - $drawHeight) / 2), $drawWidth, $drawHeight)
        $graphics.DrawImage($Source, $destination, 0, 0, $Source.Width, $Source.Height, [Drawing.GraphicsUnit]::Pixel, $edgeWrap)
        $canvas.Save((Join-Path $assetDirectory $Name), [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $edgeWrap.Dispose()
        $graphics.Dispose()
        $canvas.Dispose()
    }
    # Store validation caps manifest logo files at 200 KiB. Splash screens have
    # a separate allowance. Optimize only the derived logo, never either master.
    if ($Name -notlike 'SplashScreen*') {
        Optimize-BrandLogo -Path (Join-Path $assetDirectory $Name)
    }
}

function Optimize-BrandLogo {
    param([string]$Path)
    $maximumBytes = 204800
    if ((Get-Item -LiteralPath $Path).Length -le $maximumBytes) { return }
    if (-not (Test-Path -LiteralPath $PngEncoder -PathType Leaf)) {
        throw "A PNG encoder is required to meet the Store logo size limit: $PngEncoder"
    }
    $temporaryPath = $Path + '.encoding.tmp'
    try {
        # Start with a lossless recode; all RGBA samples, including soft glow
        # transparency, are preserved by the PNG codec.
        & $PngEncoder -hide_banner -loglevel error -i $Path -frames:v 1 -c:v png -compression_level 9 -pred mixed -f image2 -update 1 -y $temporaryPath
        if ($LASTEXITCODE -ne 0) { throw "Lossless PNG encoding failed: $Path" }
        if ((Get-Item -LiteralPath $temporaryPath).Length -gt $maximumBytes) {
            # For opaque artwork, round RGB channels to the nearest 4/255 step
            # before PNG compression. This keeps far more gradient colors than
            # a 256-color palette, with a maximum 2/255 channel error. Alpha is
            # never quantized and transparent logos stay on the lossless path.
            $checkImage = [Drawing.Bitmap]::new($Path)
            try {
                $opaque = $true
                for ($y = 0; $y -lt $checkImage.Height -and $opaque; $y++) {
                    for ($x = 0; $x -lt $checkImage.Width; $x++) {
                        if ($checkImage.GetPixel($x, $y).A -ne 255) { $opaque = $false; break }
                    }
                }
                if (-not $opaque) { throw "Transparent logo still exceeds the Store size limit after lossless encoding: $Path" }
            } finally { $checkImage.Dispose() }
            & $PngEncoder -hide_banner -loglevel error -i $Path -vf 'lutrgb=r=round(val/4)*4:g=round(val/4)*4:b=round(val/4)*4' -frames:v 1 -c:v png -compression_level 9 -pred mixed -f image2 -update 1 -y $temporaryPath
            if ($LASTEXITCODE -ne 0) { throw "RGB PNG encoding failed: $Path" }
        }
        if ((Get-Item -LiteralPath $temporaryPath).Length -gt $maximumBytes) {
            throw "Logo exceeds the Store maximum of $maximumBytes bytes: $Path"
        }
        Copy-Item -LiteralPath $temporaryPath -Destination $Path -Force
        Write-Output ("Optimized {0}: {1} bytes" -f [IO.Path]::GetFileName($Path), (Get-Item -LiteralPath $Path).Length)
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

try {
    $assets = @(
        @{ Name = 'Square44x44Logo'; Width = 44; Height = 44; Source = $icon; Fill = 1.0 },
        @{ Name = 'Square150x150Logo'; Width = 150; Height = 150; Source = $icon; Fill = 1.0 },
        @{ Name = 'StoreLogo'; Width = 50; Height = 50; Source = $icon; Fill = 1.0 },
        @{ Name = 'Wide310x150Logo'; Width = 310; Height = 150; Source = $mark; Fill = 0.96 },
        @{ Name = 'SplashScreen'; Width = 620; Height = 300; Source = $mark; Fill = 0.90 }
    )
    foreach ($asset in $assets) {
        foreach ($scale in @(100, 125, 150, 200, 400)) {
            $qualifier = if ($scale -eq 100) { '' } else { ".scale-$scale" }
            # Microsoft scale tables round fractional physical dimensions up
            # (e.g. StoreLogo 50 at 125% is 63, not banker's-rounded 62).
            $width = [int][Math]::Ceiling($asset.Width * $scale / 100.0)
            $height = [int][Math]::Ceiling($asset.Height * $scale / 100.0)
            Save-BrandAsset -Source $asset.Source -Name ($asset.Name + $qualifier + '.png') -Width $width -Height $height -Fill $asset.Fill
        }
    }
    foreach ($targetSize in @(16, 24, 32, 48, 64, 96, 128, 256)) {
        Save-BrandAsset -Source $icon -Name "Square44x44Logo.targetsize-$targetSize.png" -Width $targetSize -Height $targetSize
        Save-BrandAsset -Source $mark -Name "Square44x44Logo.targetsize-${targetSize}_altform-unplated.png" -Width $targetSize -Height $targetSize
    }
} finally {
    $icon.Dispose()
    $mark.Dispose()
}
Write-Output 'Generated 41 brand assets, preserving the original icon and transparent mark masters.'
