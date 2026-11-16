# Builds and publishes GitReview to the shared folder.
#
#   The EXE is written to a neighbouring .part file first and then renamed, so other
#   machines never see a half-copied EXE. Running instances notice the new timestamp
#   within about 5 seconds and restart themselves into it.
#
#   Usage:
#     powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -Share '\\server\share\tools'
#     powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -Share '\\server\share\tools' -SkipBuild

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Share,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$exe  = Join-Path $root 'GitReview.exe'

if (-not $SkipBuild) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $here 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
}
if (-not (Test-Path $exe)) { throw "not built yet: $exe" }

$target = Join-Path $Share 'git-review'
if (-not (Test-Path $Share)) { throw "share not reachable: $Share" }
if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

$dest = Join-Path $target 'GitReview.exe'
$part = "$dest.part"

Write-Host "copy -> $part"
Copy-Item $exe $part -Force

# Verify the copy before it becomes visible under the real name.
$srcHash  = (Get-FileHash $exe  -Algorithm SHA256).Hash
$partHash = (Get-FileHash $part -Algorithm SHA256).Hash
if ($srcHash -ne $partHash) {
    Remove-Item $part -Force -ErrorAction SilentlyContinue
    throw "copy verification failed (sha256 mismatch)"
}

Move-Item $part $dest -Force
Write-Host ("OK: {0}  ({1:N0} bytes, sha256 {2})" -f $dest, (Get-Item $dest).Length, $srcHash.Substring(0, 12).ToLower())

# The runtime helper travels with the EXE, but is not part of the version identity,
# so copying it does not trigger a restart on its own.
$setup = Join-Path $here 'setup-runtime.ps1'
if (Test-Path $setup) {
    Copy-Item $setup (Join-Path $target 'setup-runtime.ps1') -Force
    Write-Host "copied setup-runtime.ps1"
}

# Windows 11 の上位メニュー用（shell\build-shell.ps1 の成果物）。これも版の判定には使わないので、
# 置き換えても再起動は起きない。各マシンは次回の起動時に取り込む。
# 置く順番は DLL → 証明書 → msix。msix が見えた時点で残りがそろっているようにする。
$shellOut = Join-Path $root 'shell\out'
foreach ($name in @('GitReviewShell.dll', 'GitReview-signing.cer', 'GitReviewShell.msix')) {
    $src = Join-Path $shellOut $name
    if (-not (Test-Path $src)) { Write-Host "skip $name (run shell\build-shell.ps1 first)"; continue }

    $dst = Join-Path $target $name
    if ((Test-Path $dst) -and (Get-FileHash $dst).Hash -eq (Get-FileHash $src).Hash) { continue }

    Copy-Item $src "$dst.part" -Force
    if ((Get-FileHash "$dst.part").Hash -ne (Get-FileHash $src).Hash) {
        Remove-Item "$dst.part" -Force -ErrorAction SilentlyContinue
        throw "copy verification failed: $name"
    }
    Move-Item "$dst.part" $dst -Force
    Write-Host "copied $name"
}

Write-Host "Running instances pick this up within about 5 seconds."
