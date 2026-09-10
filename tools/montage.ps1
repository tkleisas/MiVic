<#
.SYNOPSIS
    Tiles screenshots into one contact sheet, with a caption per cell.

.DESCRIPTION
    Fifteen models is fifteen round trips to look at, and the question being asked
    of them — do the three factions look different, does each shape read — is a
    question about comparison rather than about any one of them. A grid answers it
    in one look.

.EXAMPLE
    pwsh tools/montage.ps1 -Columns 5 -Cell 400x333 -Out sheet.png `
        -Paths a.png,b.png -Labels Soviet,Chinese
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Out,
    [Parameter(Mandatory)][string[]] $Paths,
    [string[]] $Labels = @(),
    [int] $Columns = 5,
    [string] $Cell = "400x333",
    [int] $LabelHeight = 26
)

Add-Type -AssemblyName System.Drawing

$cellParts = $Cell -split 'x'
$cellW = [int] $cellParts[0]
$cellH = [int] $cellParts[1]
$count = $Paths.Count
$rows = [Math]::Ceiling($count / $Columns)
$sheetW = $cellW * [Math]::Min($Columns, $count)
$sheetH = ($cellH + $LabelHeight) * $rows

$sheet = New-Object System.Drawing.Bitmap($sheetW, $sheetH)
$graphics = [System.Drawing.Graphics]::FromImage($sheet)
$font = New-Object System.Drawing.Font("Consolas", 11)
$brush = [System.Drawing.Brushes]::White

try {
    $graphics.Clear([System.Drawing.Color]::FromArgb(20, 22, 24))

    for ($i = 0; $i -lt $count; $i++) {
        $column = $i % $Columns
        $row = [Math]::Floor($i / $Columns)
        $x = $column * $cellW
        $y = $row * ($cellH + $LabelHeight)

        $image = [System.Drawing.Image]::FromFile((Resolve-Path $Paths[$i]))

        try {
            # Fit rather than stretch: a squashed tank is worse than a small one.
            $scale = [Math]::Min($cellW / $image.Width, $cellH / $image.Height)
            $w = [int] ($image.Width * $scale)
            $h = [int] ($image.Height * $scale)

            $graphics.DrawImage($image, $x + (($cellW - $w) / 2), $y + (($cellH - $h) / 2), $w, $h)
        }
        finally {
            $image.Dispose()
        }

        $label = if ($i -lt $Labels.Count) { $Labels[$i] } else { [System.IO.Path]::GetFileNameWithoutExtension($Paths[$i]) }

        $graphics.DrawString($label, $font, $brush, $x + 4, $y + $cellH + 4)
    }

    $sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output "$Out  ($sheetW x $sheetH, $count cells)"
}
finally {
    $graphics.Dispose()
    $sheet.Dispose()
    $font.Dispose()
}
