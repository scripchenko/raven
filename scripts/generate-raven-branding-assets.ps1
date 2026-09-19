param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$sourceDirectory = Join-Path $RepositoryRoot 'branding\raven'
$outputDirectory = Join-Path $RepositoryRoot 'src\UnifiedMessenger.App\Assets\Branding'
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$visibleAlphaThreshold = 8
$systemIconPaddingRatio = 0.012

function Get-VisibleAlphaBounds {
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [byte]$AlphaThreshold
    )

    $bitmap = [System.Drawing.Bitmap]::new($SourcePath)
    try {
        $canvas = [System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
        $bitmapData = $bitmap.LockBits(
            $canvas,
            [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $bytes = [byte[]]::new([Math]::Abs($bitmapData.Stride) * $bitmap.Height)
            [System.Runtime.InteropServices.Marshal]::Copy(
                $bitmapData.Scan0,
                $bytes,
                0,
                $bytes.Length)

            $left = $bitmap.Width
            $top = $bitmap.Height
            $right = -1
            $bottom = -1
            for ($y = 0; $y -lt $bitmap.Height; $y++) {
                $rowOffset = if ($bitmapData.Stride -ge 0) {
                    $y * $bitmapData.Stride
                }
                else {
                    ($bitmap.Height - 1 - $y) * -$bitmapData.Stride
                }

                for ($x = 0; $x -lt $bitmap.Width; $x++) {
                    $alpha = $bytes[$rowOffset + ($x * 4) + 3]
                    if ($alpha -le $AlphaThreshold) {
                        continue
                    }

                    $left = [Math]::Min($left, $x)
                    $top = [Math]::Min($top, $y)
                    $right = [Math]::Max($right, $x)
                    $bottom = [Math]::Max($bottom, $y)
                }
            }

            if ($right -lt $left -or $bottom -lt $top) {
                throw "Approved branding source has no visible pixels: $SourcePath"
            }

            return [System.Drawing.Rectangle]::FromLTRB(
                $left,
                $top,
                $right + 1,
                $bottom + 1)
        }
        finally {
            $bitmap.UnlockBits($bitmapData)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Get-SystemIconCrop {
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath
    )

    $visibleBounds = Get-VisibleAlphaBounds `
        -SourcePath $SourcePath `
        -AlphaThreshold $visibleAlphaThreshold
    $source = [System.Drawing.Image]::FromFile($SourcePath)
    try {
        $visibleSize = [Math]::Max($visibleBounds.Width, $visibleBounds.Height)
        $padding = [int][Math]::Ceiling($visibleSize * $systemIconPaddingRatio)
        $side = [Math]::Min(
            [Math]::Min($source.Width, $source.Height),
            $visibleSize + (2 * $padding))
        $centerX = $visibleBounds.Left + ($visibleBounds.Width / 2.0)
        $centerY = $visibleBounds.Top + ($visibleBounds.Height / 2.0)
        $left = [int][Math]::Floor($centerX - ($side / 2.0))
        $top = [int][Math]::Floor($centerY - ($side / 2.0))
        $left = [Math]::Clamp($left, 0, $source.Width - $side)
        $top = [Math]::Clamp($top, 0, $source.Height - $side)

        return [System.Drawing.Rectangle]::new($left, $top, $side, $side)
    }
    finally {
        $source.Dispose()
    }
}

function Get-ResizedPngBytes {
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [int]$Size,

        [Parameter(Mandatory)]
        [System.Drawing.Rectangle]$SourceBounds
    )

    $source = [System.Drawing.Image]::FromFile($SourcePath)
    try {
        $bitmap = [System.Drawing.Bitmap]::new(
            $Size,
            $Size,
            [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
        try {
            $bitmap.SetResolution(96, 96)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.DrawImage(
                    $source,
                    [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                    $SourceBounds.X,
                    $SourceBounds.Y,
                    $SourceBounds.Width,
                    $SourceBounds.Height,
                    [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $graphics.Dispose()
            }

            $stream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                Write-Output -NoEnumerate $stream.ToArray()
            }
            finally {
                $stream.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }
}

function Write-MultiResolutionIcon {
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,

        [Parameter(Mandatory)]
        [string]$DestinationPath,

        [switch]$CropTransparentMargins
    )

    $source = [System.Drawing.Image]::FromFile($SourcePath)
    try {
        $sourceBounds = if ($CropTransparentMargins) {
            Get-SystemIconCrop -SourcePath $SourcePath
        }
        else {
            [System.Drawing.Rectangle]::new(0, 0, $source.Width, $source.Height)
        }
    }
    finally {
        $source.Dispose()
    }

    $frames = @($iconSizes | ForEach-Object {
        [pscustomobject]@{
            Size = $_
            Bytes = [byte[]](Get-ResizedPngBytes `
                -SourcePath $SourcePath `
                -Size $_ `
                -SourceBounds $sourceBounds)
        }
    })

    $stream = [System.IO.File]::Open(
        $DestinationPath,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$frames.Count)

            $imageOffset = 6 + (16 * $frames.Count)
            foreach ($frame in $frames) {
                $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
                $writer.Write([byte]$dimension)
                $writer.Write([byte]$dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$frame.Bytes.Length)
                $writer.Write([uint32]$imageOffset)
                $imageOffset += $frame.Bytes.Length
            }

            foreach ($frame in $frames) {
                $writer.Write([byte[]]$frame.Bytes)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    if ($CropTransparentMargins) {
        Write-Host (
            "Tray crop: X={0}, Y={1}, Width={2}, Height={3}" -f
            $sourceBounds.X,
            $sourceBounds.Y,
            $sourceBounds.Width,
            $sourceBounds.Height)
    }
}

$requiredSources = @(
    'raven_logo.png',
    'raven_icon.png',
    'raven_system.png',
    'raven_tile.png'
)

foreach ($fileName in $requiredSources) {
    $sourcePath = Join-Path $sourceDirectory $fileName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Approved Raven branding source is missing: $sourcePath"
    }
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
foreach ($fileName in $requiredSources) {
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $fileName) `
        -Destination (Join-Path $outputDirectory $fileName) -Force
}

Write-MultiResolutionIcon `
    -SourcePath (Join-Path $sourceDirectory 'raven_tile.png') `
    -DestinationPath (Join-Path $outputDirectory 'raven.ico')
Write-MultiResolutionIcon `
    -SourcePath (Join-Path $sourceDirectory 'raven_system.png') `
    -DestinationPath (Join-Path $outputDirectory 'raven_system.ico') `
    -CropTransparentMargins

Write-Host "Generated Raven branding resources in $outputDirectory"
