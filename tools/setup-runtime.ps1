#Requires -Version 5.1
<#
    GitReview の動作環境を確認する。必要なら .NET 10 Desktop Runtime を入れる。

        powershell -ExecutionPolicy Bypass -File <共有>\git-review\setup-runtime.ps1
        powershell -ExecutionPolicy Bypass -File <共有>\git-review\setup-runtime.ps1 -Install

    GitReview は framework-dependent（約 0.7 MB）で配っているため、各マシンに
    .NET 10 Desktop Runtime (x64) が要る。無いと
    「You must install .NET Desktop Runtime to run this application」が出る。

    -Install : ランタイムが無ければ winget で入れる（無ければ配布ページを開く）
    -Reset   : ローカルへ写した EXE が壊れている場合の復旧。
               %LOCALAPPDATA%\GitReview の配布物だけを消して入れ直させる。
               レビュー記録 (state\) と設定 (settings.json) は消さない。
    -RemoveExplorerMenu : エクスプローラーの右クリックメニューの登録を消す。
               （登録は GitReview の起動時に自動で行われる）
    -TrustCertificate : 右クリックメニューを Windows 11 の上位メニューに出すため、
               配布元の GitReview-signing.cer を LocalMachine\TrustedPeople に入れる。
               管理者の PowerShell で PC ごとに 1 回だけ実行する。
               以後は GitReview の起動時に自動で上位メニューへ切り替わる。
    -UntrustCertificate : 上の証明書を LocalMachine\TrustedPeople から外す（管理者）。

    このファイルは日本語を含むため UTF-8 BOM 付きで保存すること。
#>

[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Reset,
    [switch]$RemoveExplorerMenu,
    [switch]$TrustCertificate,
    [switch]$UntrustCertificate
)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

$origin    = Split-Path -Parent $MyInvocation.MyCommand.Path
$originExe = Join-Path $origin 'GitReview.exe'
$localRoot = Join-Path $env:LOCALAPPDATA 'GitReview'

function Info($m) { Write-Host $m }
function Ok($m)   { Write-Host $m -ForegroundColor Green }
function Warn($m) { Write-Host $m -ForegroundColor Yellow }

Info '=== GitReview 動作環境の確認 ==='
Info ''

# --- 配布元 -----------------------------------------------------------------
if (Test-Path $originExe) {
    $fi = Get-Item $originExe
    Ok ("配布元: {0}  ({1:N0} bytes, {2})" -f $originExe, $fi.Length, $fi.LastWriteTime)
} else {
    Warn "配布元に GitReview.exe がありません: $origin"
}

# --- git --------------------------------------------------------------------
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) { Ok ("git: {0}" -f (& git --version)) }
else      { Warn 'git が PATH にありません。GitReview は git CLI を呼び出します。' }

# --- .NET 10 Desktop Runtime ------------------------------------------------
$hasRuntime = $false
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    $runtimes = & dotnet --list-runtimes 2>$null
    $hasRuntime = [bool]($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' })
}

if ($hasRuntime) {
    Ok '.NET 10 Desktop Runtime: あり'
} else {
    Warn '.NET 10 Desktop Runtime (x64): なし'
    if ($Install) {
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            Info 'winget で導入します…'
            & winget install --id Microsoft.DotNet.DesktopRuntime.10 -e --source winget --scope machine --accept-source-agreements --accept-package-agreements
        } else {
            Warn 'winget がありません。配布ページを開きます。'
            Start-Process 'https://dotnet.microsoft.com/download/dotnet/10.0'
        }
    } else {
        Info '  導入するには -Install を付けて実行するか、次を実行してください:'
        Info '  winget install --id Microsoft.DotNet.DesktopRuntime.10 -e --source winget --scope machine'
    }
}

# --- ローカルの配布物 -------------------------------------------------------
Info ''
Info "ローカル: $localRoot"
if (Test-Path $localRoot) {
    foreach ($n in @('GitReview.exe', 'origin.txt', 'entry.sha256')) {
        $p = Join-Path $localRoot $n
        if (Test-Path $p) { Info ("  {0,-16} {1:N0} bytes" -f $n, (Get-Item $p).Length) }
    }
    $appRoot = Join-Path $localRoot 'app'
    if (Test-Path $appRoot) {
        foreach ($d in Get-ChildItem $appRoot -Directory) { Info ("  app\{0}" -f $d.Name) }
    }
    $state = Join-Path $localRoot 'state'
    if (Test-Path $state) {
        Info ("  state\           レビュー記録 {0} 件" -f (Get-ChildItem $state -Filter *.json).Count)
    }
} else {
    Info '  まだありません（初回は共有の GitReview.exe を実行してください）'
}

# --- エクスプローラーの右クリックメニュー -----------------------------------
$menuKeys = @(
    'HKCU:\Software\Classes\Directory\shell\GitReview',
    'HKCU:\Software\Classes\Directory\Background\shell\GitReview'
)

$packageName = 'GitReview.ContextMenu'
$originCer   = Join-Path $origin 'GitReview-signing.cer'

function Get-TrustedCert {
    if (-not (Test-Path $originCer)) { return $null }
    $c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($originCer)
    Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $c.Thumbprint
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($TrustCertificate -or $UntrustCertificate) {
    Info ''
    if (-not (Test-Path $originCer)) {
        Warn "配布元に GitReview-signing.cer がありません: $origin"
    } elseif (-not (Test-Admin)) {
        Warn '管理者の PowerShell で実行してください（LocalMachine の証明書ストアを変更します）。'
    } elseif ($TrustCertificate) {
        if (Get-TrustedCert) {
            Ok '証明書: すでに信頼済みです'
        } else {
            Import-Certificate -FilePath $originCer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
            if (Get-TrustedCert) { Ok '証明書を LocalMachine\TrustedPeople に入れました。GitReview を起動し直すと上位メニューに切り替わります。' }
            else { Warn '証明書を入れられませんでした。' }
        }
    } else {
        $t = Get-TrustedCert
        if ($t) { $t | Remove-Item; Ok '証明書を LocalMachine\TrustedPeople から外しました。' }
        else { Info '証明書: もともと信頼されていません' }
    }
}

if ($RemoveExplorerMenu) {
    Info ''
    foreach ($k in $menuKeys) {
        if (Test-Path $k) { Remove-Item $k -Recurse -Force; Ok "削除: $k" }
    }
    Get-AppxPackage $packageName -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-AppxPackage $_.PackageFullName
        Ok "削除: パッケージ $($_.PackageFullName)"
    }
    Ok '右クリックメニューの登録を消しました（次回起動時に再登録されます）。'
} else {
    Info ''
    $package = Get-AppxPackage $packageName -ErrorAction SilentlyContinue
    $registered = ($menuKeys | Where-Object { Test-Path $_ }).Count
    if ($package) {
        Ok ("右クリックメニュー: 上位メニューに登録済み（{0}）" -f $package.Version)
    }
    if ($registered -eq $menuKeys.Count) {
        $cmd = (Get-ItemProperty "$($menuKeys[0])\command" -Name '(default)' -ErrorAction SilentlyContinue).'(default)'
        Ok "右クリックメニュー: 「その他のオプション」に登録済み"
        if ($cmd) { Info "  $cmd" }
        Info '  Windows 11 では「その他のオプションを表示」の中に出ます（Shift+右クリックで直接）'
    } elseif (-not $package) {
        Info '右クリックメニュー: 未登録（GitReview を一度起動すると登録されます）'
    }

    if (Test-Path $originCer) {
        if (Get-TrustedCert) { Ok '上位メニュー用の証明書: 信頼済み' }
        else {
            Info '上位メニュー用の証明書: 未設定（「その他のオプション」の中に出ます）'
            Info '  上位メニューに出すには、管理者の PowerShell で -TrustCertificate を付けて 1 回実行'
        }
    }
}

# --- 復旧 -------------------------------------------------------------------
if ($Reset) {
    Info ''
    Info '配布物を削除します（レビュー記録と設定は残します）…'
    Get-Process GitReview -ErrorAction SilentlyContinue | ForEach-Object {
        Info ("  終了: pid {0}" -f $_.Id)
        $_ | Stop-Process -Force
    }
    Start-Sleep -Seconds 1

    foreach ($n in @('app', 'GitReview.exe', 'entry.sha256', 'origin.txt', 'launch.log')) {
        $p = Join-Path $localRoot $n
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
    # The menu points at the entry EXE we just deleted; the next launch registers it again.
    foreach ($k in $menuKeys) {
        if (Test-Path $k) { Remove-Item $k -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Get-ChildItem $localRoot -Filter 'GitReview-*.ico' -ErrorAction SilentlyContinue | Remove-Item -Force
    Ok '削除しました。共有の GitReview.exe を実行し直してください。'
}

Info ''
Info '起動: 初回だけ共有の GitReview.exe を実行 → 以降はスタートメニューの GitReview から'
