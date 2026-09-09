<#
.SYNOPSIS
    Prints the geometry summary of a glTF/GLB model so its facing can be inferred.

.DESCRIPTION
    MiVic renders every unit facing +X, so an imported model must be rotated to
    match. Rather than guessing, this dumps each mesh's bounds and its node
    transform: the long axis and the offset of the gun, wings or tracks identify
    which way the model actually faces.

.EXAMPLE
    pwsh ./tools/inspect-model.ps1 src/MiVic.Game/Content/Models/Soviet/aircraft.glb
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) {
    throw "Model not found: $Path"
}

$bytes = [System.IO.File]::ReadAllBytes($Path)
$json = $null
$offset = 12

while ($offset -lt $bytes.Length) {
    $chunkLength = [BitConverter]::ToUInt32($bytes, $offset)
    $chunkType = [System.Text.Encoding]::ASCII.GetString($bytes, $offset + 4, 4)

    if ($chunkType -eq 'JSON') {
        $json = [System.Text.Encoding]::UTF8.GetString($bytes, $offset + 8, $chunkLength)
        break
    }

    $offset += 8 + $chunkLength
}

if (-not $json) { throw "No JSON chunk in $Path" }

$doc = $json | ConvertFrom-Json

Write-Host "=== $([System.IO.Path]::GetFileName($Path)) ===" -ForegroundColor Cyan
Write-Host "meshes=$($doc.meshes.Count) nodes=$($doc.nodes.Count) materials=$($doc.materials.Count)"

Write-Host "`n-- node transforms (mesh nodes only) --" -ForegroundColor Yellow

for ($i = 0; $i -lt $doc.nodes.Count; $i++) {
    $node = $doc.nodes[$i]

    if ($null -eq $node.mesh) { continue }

    $t = if ($node.translation) { '(' + (($node.translation | ForEach-Object { [math]::Round($_, 2) }) -join ', ') + ')' } else { 'none' }
    $s = if ($node.scale) { '(' + (($node.scale | ForEach-Object { [math]::Round($_, 2) }) -join ', ') + ')' } else { 'none' }

    Write-Host ("  [{0,2}] {1,-22} mesh={2,-3} T={3,-28} S={4}" -f $i, $node.name, $node.mesh, $t, $s)
}

Write-Host "`n-- mesh-local POSITION bounds --" -ForegroundColor Yellow

foreach ($mesh in $doc.meshes) {
    foreach ($primitive in $mesh.primitives) {
        $accessor = $doc.accessors[$primitive.attributes.POSITION]
        $min = $accessor.min
        $max = $accessor.max

        $cx = [math]::Round(($min[0] + $max[0]) / 2, 3)
        $cy = [math]::Round(($min[1] + $max[1]) / 2, 3)
        $cz = [math]::Round(($min[2] + $max[2]) / 2, 3)
        $sx = [math]::Round($max[0] - $min[0], 3)
        $sy = [math]::Round($max[1] - $min[1], 3)
        $sz = [math]::Round($max[2] - $min[2], 3)

        Write-Host ("  {0,-22} centre=({1,8},{2,8},{3,8})  size=({4,7},{5,7},{6,7})" -f $mesh.name, $cx, $cy, $cz, $sx, $sy, $sz)
    }
}
