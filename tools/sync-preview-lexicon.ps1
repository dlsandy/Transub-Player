# Sync Transub training lexicon into Player preview assets (ja-soft / av_soft).
# Requires sibling Transub repo or TRANSUB_HOME.
param(
    [string]$TransubRoot = $env:TRANSUB_HOME
)

$ErrorActionPreference = 'Stop'
$playerRoot = Split-Path -Parent $PSScriptRoot
if (-not $TransubRoot) {
    $guess = Join-Path (Split-Path -Parent $playerRoot) 'Transub'
    if (Test-Path $guess) { $TransubRoot = $guess }
}
if (-not $TransubRoot -or -not (Test-Path $TransubRoot)) {
    throw "Set TRANSUB_HOME or place Transub beside SubPlayer."
}

$assets = Join-Path $playerRoot 'src\TransubPlayer\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# D01 = shared ja-asr-domain-fixes + opaque adult ASR (TDP merge).
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { throw 'node required to decode tdp-bundled.tpack' }
$tdpPack = Join-Path $TransubRoot 'shared\tdp\tdp-bundled.tpack'
$domainOut = Join-Path $assets 'ja-asr-domain-fixes.json'
if (Test-Path $tdpPack) {
    $js = @"
const fs=require('fs');
const tdpPack=require('$($TransubRoot.Replace('\','/'))/src/js/tdp-pack-core');
const buf=fs.readFileSync('$($tdpPack.Replace('\','/'))');
const parsed=tdpPack.parsePack(buf);
const pairs=tdpPack.decodeD01Payload(tdpPack.getSection(parsed,'D01'));
fs.writeFileSync('$($domainOut.Replace('\','/'))', JSON.stringify(pairs, null, 2)+'\n');
console.log('D01 pairs', pairs.length);
"@
    node -e $js
} else {
    Copy-Item -Force (Join-Path $TransubRoot 'shared\ja-asr-domain-fixes.json') $domainOut
    Write-Host 'tdp-bundled missing — copied shared ja-asr-domain-fixes only'
}

Copy-Item -Force (Join-Path $TransubRoot 'shared\mt-trained-remaps.json') (Join-Path $assets 'mt-trained-remaps.json')
Copy-Item -Force (Join-Path $TransubRoot 'shared\av-domain-glossary.json') (Join-Path $assets 'av-domain-glossary.json')
Copy-Item -Force (Join-Path $TransubRoot 'shared\av-actor-glossary.json') (Join-Path $assets 'av-actor-glossary.json')
Write-Host "Synced preview lexicon -> $assets"
