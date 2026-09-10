<#
.SYNOPSIS
    Regenerates every MiVic model.

.DESCRIPTION
    One entry point for all the generators, so regenerating the art is one command
    and no family of models can quietly go stale. Each generator owns its own
    output files and they never overlap.

.EXAMPLE
    pwsh tools/blender/build_all.ps1
#>
[CmdletBinding()]
param(
    [string] $Blender = "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe",
    [string] $Out = "src/MiVic.Game/Content/Models/Generated"
)

$ErrorActionPreference = 'Stop'

$generators = @("build_vehicles.py", "build_figures.py", "build_buildings.py", "build_props.py", "build_bridge.py")
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

Push-Location $root

try {
    foreach ($generator in $generators) {
        $path = Join-Path "tools/blender" $generator

        if (-not (Test-Path $path)) {
            Write-Warning "$generator not found, skipping."
            continue
        }

        Write-Output "--- $generator"
        & $Blender --background --python $path -- --out $Out 2>&1 |
            Select-String -Pattern "^(wrote |done: |Error|Traceback)" |
            ForEach-Object { $_.Line }

        if ($LASTEXITCODE -ne 0) {
            throw "$generator failed with exit code $LASTEXITCODE"
        }
    }
}
finally {
    Pop-Location
}
