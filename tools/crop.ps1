<#
.SYNOPSIS
    Crops a region out of a screenshot, so a model can be looked at closely.

.DESCRIPTION
    Screenshots are taken at 1920x1080 or larger because a reviewer needs the
    pixels; image viewers and review tools then downscale them back to something
    a few hundred pixels wide, which defeats the point. Cropping to the region of
    interest before anyone looks at it keeps the detail that was paid for.

.EXAMPLE
    pwsh tools/crop.ps1 -Path shot.png -Out close.png -X 700 -Y 300 -Width 900 -Height 700
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [Parameter(Mandatory)][string] $Out,
    [int] $X = 0,
    [int] $Y = 0,
    [int] $Width = 900,
    [int] $Height = 700
)

Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Image]::FromFile((Resolve-Path $Path))

try {
    $x = [Math]::Max(0, [Math]::Min($X, $source.Width - 1))
    $y = [Math]::Max(0, [Math]::Min($Y, $source.Height - 1))
    $w = [Math]::Min($Width, $source.Width - $x)
    $h = [Math]::Min($Height, $source.Height - $y)

    $target = New-Object System.Drawing.Bitmap($w, $h)

    try {
        $graphics = [System.Drawing.Graphics]::FromImage($target)

        try {
            $graphics.DrawImage(
                $source,
                (New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
                (New-Object System.Drawing.Rectangle($x, $y, $w, $h)),
                [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $graphics.Dispose()
        }

        $target.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "$Out  ($w x $h from $($source.Width) x $($source.Height))"
    }
    finally {
        $target.Dispose()
    }
}
finally {
    $source.Dispose()
}
