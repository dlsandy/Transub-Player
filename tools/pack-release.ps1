# Build release artifacts: portable zip and/or Inno Setup installer.
# Shared stage = self-contained TransubPlayer + mpv (ASR via Whisper.net; models on demand).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1
#   powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -Target Portable
#   powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -Target Setup
#   powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -SkipNativePrep
#   powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -FrameworkDependent
param(
    [ValidateSet("All", "Portable", "Setup")]
    [string]$Target = "All",
    [switch]$SkipNativePrep,
    [switch]$FrameworkDependent,
    [switch]$SkipZip,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$csproj = Join-Path $root "src\TransubPlayer\TransubPlayer.csproj"
if (-not (Test-Path $csproj)) { throw "Project not found: $csproj" }

$version = "1.5.1"
Select-Xml -Path $csproj -XPath "//Version" -ErrorAction SilentlyContinue |
    ForEach-Object { if ($_.Node.InnerText) { $version = $_.Node.InnerText.Trim() } }

$stamp = Get-Date -Format "yyyyMMdd-HHmm"
$packName = "TransubPlayer-$version-win-x64"
$outRoot = Join-Path $root "artifacts\pack"
$publishDir = Join-Path $outRoot "_publish"
$stageDir = Join-Path $outRoot "_stage"
$portableStageDir = Join-Path $outRoot $packName
$zipPath = Join-Path $outRoot "$packName.zip"
$stableZip = Join-Path $outRoot "TransubPlayer-win-x64.zip"
$setupPath = Join-Path $outRoot "TransubPlayer-$version-win-x64-setup.exe"
$issPath = Join-Path $root "tools\installer\TransubPlayer.iss"

$mpvExe = Join-Path $root "native\mpv\mpv.exe"
$wantPortable = $Target -eq "All" -or $Target -eq "Portable"
$wantSetup = $Target -eq "All" -or $Target -eq "Setup"

function Assert-Dotnet {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw "未找到 dotnet。请先安装 .NET SDK（net10）。" }
}

function Ensure-Native {
    if ($SkipNativePrep) {
        Write-Host "跳过 native 准备（-SkipNativePrep）"
        if (-not (Test-Path $mpvExe)) {
            throw "mpv 不完整，缺少 native\mpv\mpv.exe（且已 SkipNativePrep）"
        }
        return
    }

    if (-not (Test-Path $mpvExe)) {
        Write-Host "未找到 mpv，正在拉取…"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "tools\fetch-mpv.ps1")
        if ($LASTEXITCODE -ne 0) { throw "fetch-mpv.ps1 failed" }
    }
    else {
        Write-Host "mpv: OK ($mpvExe)"
    }
}

function Publish-App {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    $selfContained = -not $FrameworkDependent
    Write-Host "dotnet publish ($Configuration / $Runtime / self-contained=$selfContained)…"
    $args = @(
        "publish", $csproj,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $(if ($selfContained) { "true" } else { "false" }),
        "-o", $publishDir,
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:PublishReadyToRun=false"
    )
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

    $exe = Join-Path $publishDir "TransubPlayer.exe"
    if (-not (Test-Path $exe)) { throw "Publish output missing TransubPlayer.exe" }
}

function Assemble-Stage {
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    Write-Host "复制发布产物到共享 stage…"
    & robocopy $publishDir $stageDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy publish failed: $LASTEXITCODE" }

    $stagedMpv = Join-Path $stageDir "mpv\mpv.exe"
    if (-not (Test-Path $stagedMpv)) {
        Write-Host "发布目录未带上 mpv，从 native 复制…"
        if (-not (Test-Path $mpvExe)) { throw "mpv.exe 不存在：$mpvExe" }
        & robocopy (Join-Path $root "native\mpv") (Join-Path $stageDir "mpv") /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy mpv failed: $LASTEXITCODE" }
    }

    Get-ChildItem $stageDir -File -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

    # Installer stage must NOT include portable marker (data goes to LocalAppData).
    $marker = Join-Path $stageDir "portable.txt"
    if (Test-Path $marker) { Remove-Item $marker -Force }

    Write-Host "清除打包目录 Internet 标记…"
    Get-ChildItem -LiteralPath $stageDir -Recurse -File -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue

    Sign-App -StageDir $stageDir
}

function Sign-App {
    param([string]$StageDir)
    $thumb = $env:TRANSUB_SIGN_THUMBPRINT
    if ([string]::IsNullOrWhiteSpace($thumb)) { return }

    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        Write-Host "WARN: TRANSUB_SIGN_THUMBPRINT set but signtool.exe not found — skip signing"
        return
    }

    $targets = @(
        (Join-Path $StageDir "TransubPlayer.exe"),
        (Join-Path $StageDir "mpv\mpv.exe")
    ) | Where-Object { Test-Path $_ }

    foreach ($exe in $targets) {
        Write-Host "Signing $exe …"
        & signtool.exe sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 $thumb $exe
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $exe" }
    }
}

function Sign-File {
    param([string]$Path)
    $thumb = $env:TRANSUB_SIGN_THUMBPRINT
    if ([string]::IsNullOrWhiteSpace($thumb)) { return }
    if (-not (Test-Path $Path)) { return }

    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        Write-Host "WARN: signtool.exe not found — skip signing $Path"
        return
    }

    Write-Host "Signing $Path …"
    & signtool.exe sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 $thumb $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $Path" }
}

function Find-Iscc {
    $candidates = @(
        ${env:INNO_SETUP_ISCC},
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 5\ISCC.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($c in $candidates) {
        if (Test-Path $c) { return (Get-Item $c).FullName }
    }

    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Make-Portable {
    if (-not $wantPortable) { return }

    if (Test-Path $portableStageDir) { Remove-Item $portableStageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $portableStageDir | Out-Null
    & robocopy $stageDir $portableStageDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy portable stage failed: $LASTEXITCODE" }

    # Marker: side-by-side data/ + in-app zip update.
    $markerText = @"
Transub Player portable layout.
Keep this file next to TransubPlayer.exe so settings and models stay in .\data\
"@
    Set-Content -Path (Join-Path $portableStageDir "portable.txt") -Value $markerText -Encoding UTF8

    if ($SkipZip) {
        Write-Host "跳过压缩（-SkipZip）；便携目录: $portableStageDir"
        return
    }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Write-Host "压缩便携包 $zipPath …"
    Push-Location $outRoot
    try {
        & tar.exe -a -cf $zipPath $packName
        if ($LASTEXITCODE -ne 0) { throw "tar zip failed: $LASTEXITCODE" }

        Copy-Item -LiteralPath $zipPath -Destination $stableZip -Force
        Write-Host "稳定别名: $stableZip"
    }
    finally {
        Pop-Location
    }
}

function Make-Setup {
    if (-not $wantSetup) { return }

    if (-not (Test-Path $issPath)) {
        throw "Inno script missing: $issPath"
    }

    $iscc = Find-Iscc
    if (-not $iscc) {
        $msg = "未找到 Inno Setup 6（ISCC.exe）。请安装 https://jrsoftware.org/isinfo.php 或设置环境变量 INNO_SETUP_ISCC。"
        if ($Target -eq "Setup") { throw $msg }
        Write-Host "WARN: $msg"
        Write-Host "WARN: 已跳过安装程序；便携包仍已生成（若选择了 Portable/All）。"
        return
    }

    Write-Host "编译安装程序（$iscc）…"
    $icon = Join-Path $root "src\TransubPlayer\Assets\app.ico"
    & $iscc `
        "/DMyAppVersion=$version" `
        "/DMyStageDir=$stageDir" `
        "/DMyOutDir=$outRoot" `
        "/DMySetupBaseName=TransubPlayer-$version-win-x64-setup" `
        "/DMyAppIcon=$icon" `
        $issPath
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed: $LASTEXITCODE" }

    if (-not (Test-Path $setupPath)) {
        # Inno may append .exe to OutputBaseFilename; accept either.
        $alt = Get-ChildItem $outRoot -Filter "TransubPlayer-$version-win-x64-setup*.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($alt) {
            if ($alt.FullName -ne $setupPath) {
                Copy-Item $alt.FullName $setupPath -Force
            }
        }
    }

    if (-not (Test-Path $setupPath)) {
        throw "Setup output missing: $setupPath"
    }

    Sign-File -Path $setupPath
}

function Show-Summary {
    Write-Host ""
    Write-Host "打包完成  (Target=$Target, version=$version, stamp=$stamp)"
    Write-Host "  共享 stage: $stageDir"

    if ($wantPortable) {
        if (Test-Path $portableStageDir) {
            $dirSize = (Get-ChildItem $portableStageDir -Recurse -File -ErrorAction SilentlyContinue |
                Measure-Object -Sum Length).Sum
            Write-Host ("  便携目录: {0} ({1:N1} MB)" -f $portableStageDir, ($dirSize / 1MB))
        }
        if (-not $SkipZip -and (Test-Path $zipPath)) {
            Write-Host ("  便携 zip: {0} ({1:N1} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
            if (Test-Path $stableZip) {
                Write-Host "  更新用别名: $stableZip"
            }
        }
    }

    if ($wantSetup -and (Test-Path $setupPath)) {
        Write-Host ("  安装程序: {0} ({1:N1} MB)" -f $setupPath, ((Get-Item $setupPath).Length / 1MB))
    }
    elseif ($wantSetup) {
        Write-Host "  安装程序: （未生成）"
    }

    Write-Host ""
    Write-Host "发布到 GitHub / GitCode Releases 时请上传："
    Write-Host "  - TransubPlayer-$version-win-x64-setup.exe   （安装程序，推荐）"
    Write-Host "  - TransubPlayer-$version-win-x64.zip         （便携包）"
    Write-Host "  - TransubPlayer-win-x64.zip                  （应用内更新别名）"
    Write-Host "tag 用 v$version。"
}

Assert-Dotnet
Ensure-Native
Publish-App
Assemble-Stage
Make-Portable
Make-Setup
Show-Summary
