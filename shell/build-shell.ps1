# エクスプローラーの右クリックメニュー用ハンドラーをビルドし、署名済みパッケージを作る。
#
#   使うのは無償のツールだけ（Visual Studio は不要）。初回は自動で取得する。
#     ・llvm-mingw（C++ コンパイラ）          https://github.com/mstorsjo/llvm-mingw
#     ・Microsoft.Windows.SDK.BuildTools     NuGet の makeappx.exe / signtool.exe
#   取得先は shell\.toolchain（git 管理外）で、PC にはインストールしない。
#
#   署名には、このユーザーの証明書ストア（CurrentUser\My）にあるコード署名証明書を使う。
#   無ければ自己署名証明書を作る。秘密鍵はストアから出さない（.pfx は作らない）。
#   各 PC に配るのは公開鍵の out\GitReview-signing.cer だけ。
#
#   成果物（out\）:
#     GitReviewShell.dll       右クリックメニューのハンドラー
#     GitReviewShell.msix      署名済みの Sparse パッケージ
#     GitReview-signing.cer    各 PC で信頼させる公開鍵
#
#   使い方:
#     powershell -ExecutionPolicy Bypass -File shell\build-shell.ps1

[CmdletBinding()]
param(
    # 証明書の Subject。パッケージの Publisher と一致させる必要があり、
    # 配布後に変えると別アプリ扱いになるので、決めたら変えない
    [string]$Publisher = 'CN=GitReview Local Signing'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$toolchain = Join-Path $here '.toolchain'
$out = Join-Path $here 'out'

$llvmVersion = '20260908'
$sdkVersion = '10.0.28000.2705'

New-Item -ItemType Directory -Force -Path $toolchain, $out | Out-Null

# ---------------------------------------------------------------- ツールの取得

$llvm = Join-Path $toolchain "llvm-mingw-$llvmVersion-ucrt-x86_64"
if (-not (Test-Path $llvm)) {
    $zip = Join-Path $toolchain "llvm-mingw-$llvmVersion-ucrt-x86_64.zip"
    Write-Host "llvm-mingw を取得: $llvmVersion"
    Invoke-WebRequest -UseBasicParsing -OutFile $zip `
        "https://github.com/mstorsjo/llvm-mingw/releases/download/$llvmVersion/llvm-mingw-$llvmVersion-ucrt-x86_64.zip"
    Expand-Archive $zip -DestinationPath $toolchain -Force
}

$sdk = Join-Path $toolchain 'sdkbuildtools'
if (-not (Test-Path $sdk)) {
    $pkg = Join-Path $toolchain 'sdkbuildtools.zip'
    Write-Host "Windows SDK BuildTools を取得: $sdkVersion"
    Invoke-WebRequest -UseBasicParsing -OutFile $pkg `
        "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$sdkVersion/microsoft.windows.sdk.buildtools.$sdkVersion.nupkg"
    Expand-Archive $pkg -DestinationPath $sdk -Force
}

$clang = Join-Path $llvm 'bin\x86_64-w64-mingw32-clang++.exe'
$makeappx = Get-ChildItem $sdk -Recurse -Filter makeappx.exe | Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName
$signtool = Get-ChildItem $sdk -Recurse -Filter signtool.exe | Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName

# ---------------------------------------------------------------- ハンドラー DLL

Write-Host 'GitReviewShell.dll をビルド'
& $clang -std=c++17 -O2 -s -shared -static -fno-exceptions -fno-rtti -municode `
    -DUNICODE -D_UNICODE -DWIN32_LEAN_AND_MEAN `
    (Join-Path $here 'GitReviewShell.cpp') (Join-Path $here 'GitReviewShell.def') `
    -o (Join-Path $out 'GitReviewShell.dll') `
    -lole32 -lshell32 -lshlwapi -luuid
if ($LASTEXITCODE -ne 0) { throw "コンパイル失敗 (exit $LASTEXITCODE)" }

# ---------------------------------------------------------------- パッケージの中身

$staging = Join-Path $out 'package'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $staging 'Assets') | Out-Null

(Get-Content (Join-Path $here 'AppxManifest.xml') -Raw -Encoding UTF8).Replace('__PUBLISHER__', $Publisher) |
    Set-Content (Join-Path $staging 'AppxManifest.xml') -Encoding UTF8 -NoNewline

# アイコン画像はアプリのアイコンから作る
Add-Type -AssemblyName System.Drawing
$ico = Join-Path $root 'tools\GitReview.ico'
foreach ($pair in @(@('Square150x150Logo.png', 150), @('Square44x44Logo.png', 44), @('StoreLogo.png', 50))) {
    $icon = New-Object System.Drawing.Icon($ico, 256, 256)
    $source = $icon.ToBitmap()
    $bitmap = New-Object System.Drawing.Bitmap($pair[1], $pair[1])
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.DrawImage($source, 0, 0, $pair[1], $pair[1])
    $bitmap.Save((Join-Path $staging "Assets\$($pair[0])"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bitmap.Dispose(); $source.Dispose(); $icon.Dispose()
}

# ---------------------------------------------------------------- 署名用の証明書

$codeSigning = '1.3.6.1.5.5.7.3.3'
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and
                   ($_.EnhancedKeyUsageList.ObjectId -contains $codeSigning) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Host "自己署名証明書を作成: $Publisher（有効期限 10 年）"
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
        -KeyUsage DigitalSignature -FriendlyName 'GitReview package signing' `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @("2.5.29.37={text}$codeSigning", '2.5.29.19={text}')
}

$cer = Join-Path $out 'GitReview-signing.cer'
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null

# ---------------------------------------------------------------- パッケージ化と署名

$msix = Join-Path $out 'GitReviewShell.msix'
Write-Host 'GitReviewShell.msix を作成'
# /nv: マニフェストの Executable（GitReview.exe）はパッケージの外にあるので検証を省く
& $makeappx pack /d $staging /p $msix /nv /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx 失敗 (exit $LASTEXITCODE)" }

& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "signtool 失敗 (exit $LASTEXITCODE)" }

Write-Host ''
Write-Host ("OK  証明書 {0}（{1} まで）" -f $cert.Thumbprint, $cert.NotAfter.ToString('yyyy-MM-dd'))
Get-ChildItem $out -File | ForEach-Object { Write-Host ("    {0,-24} {1,10:N0} bytes" -f $_.Name, $_.Length) }
