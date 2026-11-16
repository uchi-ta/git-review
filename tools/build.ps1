# Builds GitReview.
#   Publishes a single framework-dependent EXE (~260 KB) and drops it at the repo root.
#   Target machines need the .NET 10 Desktop Runtime (x64).

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$out  = Join-Path $root 'GitReview.exe'
$publishDir = Join-Path $root 'bin\publish'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK (dotnet) not found.' }

Write-Host "dotnet publish -> $publishDir"
& dotnet publish (Join-Path $root 'GitReview.csproj') -c Release -o $publishDir -p:DebugType=none --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

Copy-Item (Join-Path $publishDir 'GitReview.exe') $out -Force
Write-Host ("OK: {0}  ({1:N0} bytes)" -f $out, (Get-Item $out).Length)
