<#
.SYNOPSIS
    Rebuilds the UI symbol font: the subset of Noto Sans Math carrying the symbols Noto Sans
    has no glyph for.

.DESCRIPTION
    The ImGui atlas is built from Noto Sans, which covers Latin, Greek and General Punctuation
    and nothing besides. The interface draws symbols from three blocks outside that — √ for a
    completed objective, ▶ for the running production job, ⌀ and arrows and a minus sign in the
    model viewer and the economy panel. A codepoint with no glyph is not an error and not a log
    line: ImGui draws its fallback glyph and the reader sees a box, which is how two of these
    sat in the interface unnoticed until somebody looked at a screenshot.

    So a second font is merged into the same atlas entries, and it is asked for nothing but the
    codepoints the catalogue lists. It is a subset rather than the whole 967 kB Noto Sans Math
    because six glyphs are wanted out of it, and a build input nobody can read is a build input
    nobody checks.

    The list is not repeated here. It is read out of Ui/UiSymbols.cs, from the entries marked
    SymbolPath.AtlasSymbol: add a symbol to that catalogue, run this, and the self-test's symbol
    check says whether the atlas and the font still agree.

.PARAMETER Source
    A Noto Sans Math TTF to subset. Downloaded from the URL below when not given.

.EXAMPLE
    pwsh tools/build-ui-symbol-font.ps1
#>
[CmdletBinding()]
param(
    [string] $Source
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogue = Join-Path $repoRoot 'src/MiVic.Game/Ui/UiSymbols.cs'
$target = Join-Path $repoRoot 'src/MiVic.Game/Content/Fonts/NotoSansMath-UiSymbols.ttf'

# Noto Sans Math, SIL OFL 1.1, from the upstream build repository. The hash is pinned because
# the subset is only as good as what it was cut from, and a font that changed underfoot would
# otherwise be discovered by a screenshot.
$sourceUrl = 'https://raw.githubusercontent.com/notofonts/notofonts.github.io/main/fonts/NotoSansMath/hinted/ttf/NotoSansMath-Regular.ttf'
$sourceSha256 = 'D51AFD5739C7BA6C44FCAB35A88160E25DFB69A2D4AD0BD99533F8D894AF1F96'

# The symbols, out of the catalogue rather than out of this script.
$text = Get-Content $catalogue -Raw
$found = [regex]::Matches($text, "new\('(.)',\s*""[^""]*"",\s*SymbolPath\.AtlasSymbol")
$codepoints = $found | ForEach-Object { [int][char]$_.Groups[1].Value } | Sort-Object -Unique

# A pattern that stopped matching would build an empty font and every symbol would draw as a box
# again, which is the defect this script exists to prevent — so it fails instead.
if ($codepoints.Count -eq 0) {
    throw "No SymbolPath.AtlasSymbol entries found in $catalogue. This script reads the catalogue by pattern; if the entries were rewritten, that pattern has to be updated too."
}

$unicodeList = ($codepoints | ForEach-Object { 'U+{0:X4}' -f $_ }) -join ','
Write-Output "symbols wanted : $unicodeList"

if (-not $Source) {
    $Source = Join-Path ([System.IO.Path]::GetTempPath()) 'NotoSansMath-Regular.ttf'
    Write-Output "downloading    : $sourceUrl"
    Invoke-WebRequest -Uri $sourceUrl -OutFile $Source -UseBasicParsing
}

$hash = (Get-FileHash $Source -Algorithm SHA256).Hash

if ($hash -ne $sourceSha256) {
    throw "$Source is not the Noto Sans Math this script was written against: sha256 $hash, expected $sourceSha256."
}

python -c 'import fontTools' 2>$null

if ($LASTEXITCODE -ne 0) {
    throw 'fontTools is required: python -m pip install fonttools'
}

python -m fontTools.subset $Source --unicodes=$unicodeList --recommended-glyphs --name-IDs='*' --layout-features='' --output-file=$target

if ($LASTEXITCODE -ne 0) {
    throw "fontTools failed with exit code $LASTEXITCODE"
}

# Verified by reading the font back, not by trusting an exit code: the one thing that must be
# true of the file is that it carries what the atlas is going to ask it for.
$verify = @"
import sys
from fontTools.ttLib import TTFont

font = TTFont(r'$target')
have = set(font.getBestCmap())
want = [$($codepoints -join ', ')]
missing = [hex(code) for code in want if code not in have]

print('glyphs in subset:', len(font.getGlyphOrder()))

if missing:
    print('MISSING:', ' '.join(missing))
    sys.exit(1)
"@

$verify | python -

if ($LASTEXITCODE -ne 0) {
    throw 'The subset does not carry every codepoint it was asked for.'
}

Write-Output "wrote          : $target ($((Get-Item $target).Length) bytes)"
