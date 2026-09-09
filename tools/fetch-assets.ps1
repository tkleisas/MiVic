<#
.SYNOPSIS
    Downloads the CC0 / QAL 3D models MiVic uses and places them in the content folder.

.DESCRIPTION
    Model files are deliberately NOT committed to the repository. The asset licence
    (Quaternius Asset License v1.0) allows use inside a finished game but forbids
    redistributing the assets themselves as assets, and a public repository would
    do exactly that. This script rebuilds the asset set on any machine instead.

    All models come from Quaternius via Poly Pizza. See
    src/MiVic.Game/Content/Models/README.md for provenance and licence notes.

.PARAMETER Force
    Re-download files that already exist.

.EXAMPLE
    pwsh ./tools/fetch-assets.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$modelRoot = Join-Path $repoRoot 'src/MiVic.Game/Content/Models'
$cdnBase = 'https://static.poly.pizza'

# faction folder, file name, asset id on Poly Pizza
$manifest = @(
    # --- Σοβιετικοί: heavy armour, industrial structures ---
    @{ Faction = 'Soviet';  File = 'tank_heavy.glb';  Id = '52977e64-f4b3-4845-9d44-fe50ec8154e3' }
    @{ Faction = 'Soviet';  File = 'tank_medium.glb'; Id = '58c387b2-636f-49dc-a900-13b0852717d6' }
    @{ Faction = 'Soviet';  File = 'tank_light.glb';  Id = '4a40c214-87f9-4cdb-bc72-003c96f49f76' }
    @{ Faction = 'Soviet';  File = 'tank_apc.glb';    Id = 'c0135fb4-3307-4c0f-a439-86ceafedc4c7' }
    @{ Faction = 'Soviet';  File = 'soldier.glb';     Id = '66a55d04-4286-44a3-b289-0d774c27db5b' }
    @{ Faction = 'Soviet';  File = 'turret.glb';      Id = '5669a490-372e-41a6-ac5f-b8ca5b69e4a5' }
    @{ Faction = 'Soviet';  File = 'aircraft.glb';    Id = 'e8817981-bfc4-448d-822f-5b76a5983675' }
    @{ Faction = 'Soviet';  File = 'hq.glb';          Id = '52b9ad24-fe0a-4f6f-a44d-66f2bdff6f08' }

    # --- Κινέζοι: light hulls, mass-produced patterns ---
    @{ Faction = 'Chinese'; File = 'tank_heavy.glb';  Id = '52977e64-f4b3-4845-9d44-fe50ec8154e3' }
    @{ Faction = 'Chinese'; File = 'tank_medium.glb'; Id = '58c387b2-636f-49dc-a900-13b0852717d6' }
    @{ Faction = 'Chinese'; File = 'tank_light.glb';  Id = '4a40c214-87f9-4cdb-bc72-003c96f49f76' }
    @{ Faction = 'Chinese'; File = 'tank_apc.glb';    Id = 'c0135fb4-3307-4c0f-a439-86ceafedc4c7' }
    # The Chinese soldier deliberately reuses the Soviet model: the two powers
    # share licence-produced kit, and this asset also happens to be the cleanest
    # character export of the three.
    @{ Faction = 'Chinese'; File = 'soldier.glb';     Id = '66a55d04-4286-44a3-b289-0d774c27db5b' }
    @{ Faction = 'Chinese'; File = 'turret.glb';      Id = '58ce64fc-b698-4b78-af75-05bb2dfad2ed' }
    @{ Faction = 'Chinese'; File = 'aircraft.glb';    Id = 'a6789133-a4b2-447b-9eda-4c44b84240ca' }
    @{ Faction = 'Chinese'; File = 'hq.glb';          Id = '2845deb2-647b-422c-90b7-7b4c0065b985' }

    # --- Δυτικοί: the most refined vehicles and buildings ---
    @{ Faction = 'Western'; File = 'tank_heavy.glb';  Id = '52977e64-f4b3-4845-9d44-fe50ec8154e3' }
    @{ Faction = 'Western'; File = 'tank_medium.glb'; Id = '58c387b2-636f-49dc-a900-13b0852717d6' }
    @{ Faction = 'Western'; File = 'tank_light.glb';  Id = '4a40c214-87f9-4cdb-bc72-003c96f49f76' }
    @{ Faction = 'Western'; File = 'tank_apc.glb';    Id = 'c0135fb4-3307-4c0f-a439-86ceafedc4c7' }
    @{ Faction = 'Western'; File = 'soldier.glb';     Id = '713f6535-f4f3-4367-a4c6-ced126ae0936' }
    @{ Faction = 'Western'; File = 'turret.glb';      Id = '730a54af-3785-4d23-8efc-1560ed61e0d3' }
    @{ Faction = 'Western'; File = 'aircraft.glb';    Id = 'e2fa6756-de8d-43cf-bc7b-297d100ca5c4' }
    @{ Faction = 'Western'; File = 'hq.glb';          Id = '6c49f4dd-c032-4e50-b257-68bbe01bcf16' }
)

Write-Host "MiVic asset fetch" -ForegroundColor Cyan
Write-Host "  target: $modelRoot"

$downloaded = 0
$skipped = 0
$failed = @()

foreach ($entry in $manifest) {
    $directory = Join-Path $modelRoot $entry.Faction
    $target = Join-Path $directory $entry.File

    if ((Test-Path $target) -and -not $Force) {
        $skipped++
        continue
    }

    New-Item -ItemType Directory -Path $directory -Force | Out-Null

    $url = "$cdnBase/$($entry.Id).glb"
    try {
        Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing -TimeoutSec 60
        $downloaded++
        Write-Host ("  ok   {0}/{1}" -f $entry.Faction, $entry.File) -ForegroundColor Green
    }
    catch {
        $failed += "$($entry.Faction)/$($entry.File)"
        Write-Warning ("  fail {0}/{1}: {2}" -f $entry.Faction, $entry.File, $_.Exception.Message)
    }
}

Write-Host ""
Write-Host ("downloaded {0}, already present {1}, failed {2}" -f $downloaded, $skipped, $failed.Count)

if ($failed.Count -gt 0) {
    Write-Warning "The game still runs: any missing model falls back to procedural geometry."
    exit 1
}
