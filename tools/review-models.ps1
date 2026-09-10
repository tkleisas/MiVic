<#
.SYNOPSIS
    Renders every model slot on its own and tiles the results into contact sheets.

.DESCRIPTION
    The acceptance test for the art is "is this identifiable, as itself and as its
    faction", and that is a question you answer by looking at all of them side by
    side under the same camera. Doing that by hand is two dozen command lines and a
    montage, every time anything changes, so this does it in one.

    It asks the game for the slot list and each model's real size (--inspect-models),
    works out a camera distance from that size, and renders two views per model: a
    three-quarter view, which is how a model is judged, and a steep view, which is
    how the game is actually played. Sheet one is the three-quarter views, sheet two
    the steep ones.

.EXAMPLE
    pwsh tools/review-models.ps1
    pwsh tools/review-models.ps1 -Filter 'Infantry|Tank' -Columns 2
#>
[CmdletBinding()]
param(
    [string] $Exe = "src/MiVic.Game/bin/Debug/net9.0/MiVic.Game.exe",
    [string] $Out = "artifacts/review",
    [string] $Filter = "",
    [int] $Columns = 4,
    [int] $CellWidth = 480,
    [int] $CellHeight = 440,
    [int] $Width = 1920,
    [int] $Height = 1080
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) {
    throw "$Exe not found. Build first: dotnet build src/MiVic.Game/MiVic.Game.csproj"
}

$Exe = (Resolve-Path $Exe).Path
$Out = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# The inspector is the single source of truth for which slots exist and how big each
# model is, so this never has to be kept in step by hand.
#
# Its output goes to a file rather than into a pipeline: capturing a child process's
# stdout through a pipe is not available everywhere this script has to run, and a
# silent empty result is a confusing way to find that out.
$inspect = Join-Path ([System.IO.Path]::GetTempPath()) "mivic-inspect.txt"

Start-Process -FilePath $Exe -ArgumentList "--inspect-models" -NoNewWindow -Wait `
    -RedirectStandardOutput $inspect | Out-Null

$lines = Get-Content $inspect
$slots = @()

foreach ($line in $lines) {
    if ($line -match '^(\w+)/(\w+)\s+\d+\s+\d+\s+([\d.]+),\s*([\d.]+),\s*([\d.]+)') {
        # Pull the groups out immediately. $Matches is clobbered by the *next*
        # -match or -notmatch, and the filter below is one — so reading it after the
        # filter silently yields the filter's own (empty) groups instead of these.
        $name = "$($Matches[1])/$($Matches[2])"
        $size = @([double]$Matches[3], [double]$Matches[4], [double]$Matches[5])

        if ($Filter -ne "" -and $name -notmatch $Filter) {
            continue
        }

        $longest = ($size | Measure-Object -Maximum).Maximum

        $slots += [pscustomobject]@{ Name = $name; Longest = $longest }
    }
}

if ($slots.Count -eq 0) {
    throw "No model slots matched. Filter: '$Filter'"
}

Write-Output "rendering $($slots.Count) slots into $Out"

# Two cameras. A three-quarter view at 32 degrees is the one a modeller judges, and a
# 76-degree view is close to what the player actually has: the roof and the footprint.
$views = @(
    [pscustomobject]@{ Tag = "iso"; Pitch = 32; Angle = 45 },
    [pscustomobject]@{ Tag = "top"; Pitch = 76; Angle = 45 }
)

$sheets = @()

foreach ($view in $views) {
    $rendered = @()
    $labels = @()

    foreach ($slot in $slots) {
        $tag = ($slot.Name -replace '/', '_')
        $shot = Join-Path $Out "$tag`_$($view.Tag).png"
        $crop = Join-Path $Out "crop_$tag`_$($view.Tag).png"

        # Framed from the model's own longest axis, so a soldier and a headquarters
        # both fill their cell instead of one being a speck and the other a wall.
        $distance = [Math]::Round(($slot.Longest * 2.4) + 3.0, 1)

        & $Exe --viewer-model $slot.Name --viewer-shot $shot `
            --viewer-distance $distance --viewer-pitch $view.Pitch --viewer-angle $view.Angle `
            --width $Width --height $Height | Out-Null

        if (-not (Test-Path $shot)) {
            Write-Warning "$($slot.Name): no screenshot produced"
            continue
        }

        # Crop to the middle of the frame: the fixtures put the model at the centre
        # of the grid, and the empty half of the picture is only grass.
        & tools/crop.ps1 -Path $shot -Out $crop -X 620 -Y 200 -Width 680 -Height 640 | Out-Null

        $rendered += $crop
        $labels += "$($slot.Name)  $($slot.Longest)m  d=$distance"
    }

    if ($rendered.Count -eq 0) {
        continue
    }

    $sheet = Join-Path $Out "sheet_$($view.Tag).png"
    & tools/montage.ps1 -Out $sheet -Paths $rendered -Labels $labels -Columns $Columns -Cell "$($CellWidth)x$($CellHeight)"
    $sheets += $sheet
}

Write-Output ""
Write-Output "sheets:"
$sheets | ForEach-Object { Write-Output "  $_" }
