# Download a Windows mpv build into native/mpv (used by Transub Player).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "native\mpv"
New-Item -ItemType Directory -Force -Path (Join-Path $root "native") | Out-Null

function Get-7zr {
    $seven = Join-Path $root "native\7zr.exe"
    if (Test-Path $seven) { return $seven }
    Write-Host "Downloading 7zr.exe..."
    Invoke-WebRequest -Uri "https://www.7-zip.org/a/7zr.exe" -OutFile $seven -UseBasicParsing
    return $seven
}

Write-Host "Looking up latest mpv win64 build..."
$headers = @{ "User-Agent" = "TransubPlayer" }
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/zhongfly/mpv-winbuild/releases/latest" -Headers $headers
$asset = $rel.assets |
    Where-Object { $_.name -match '^mpv-x86_64-v3-.*\.7z$' -or $_.name -match '^mpv-x86_64-.*\.7z$' } |
    Where-Object { $_.name -notmatch 'dev|debug|lx' } |
    Select-Object -First 1
if (-not $asset) {
    throw "No mpv-x86_64 *.7z asset found on zhongfly/mpv-winbuild latest release."
}

$archive = Join-Path $root "native\$($asset.name)"
Write-Host "Downloading $($asset.name)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing

$seven = Get-7zr
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
& $seven x "-o$dest" $archive | Out-Null

$exe = Get-ChildItem -Path $dest -Filter mpv.exe -Recurse | Select-Object -First 1
if (-not $exe) { throw "Archive extracted but mpv.exe was not found." }
if ($exe.DirectoryName -ne $dest) {
    Get-ChildItem $exe.DirectoryName | ForEach-Object {
        Move-Item $_.FullName (Join-Path $dest $_.Name) -Force
    }
}

Remove-Item $archive -Force -ErrorAction SilentlyContinue
Write-Host "mpv ready: $(Join-Path $dest 'mpv.exe')"
