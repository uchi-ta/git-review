using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.Win32;

namespace GitReview.Core;

/// <summary>
/// Windows 11 の新しい右クリックメニュー（上位に出る方）への登録。
///
/// 上位メニューに出せるのはパッケージ化された IExplorerCommand だけなので、
/// shell\ でビルドした Sparse パッケージ（GitReviewShell.msix）とハンドラー
/// （GitReviewShell.dll）を配布元から受け取り、%LOCALAPPDATA%\GitReview を外部の場所として登録する。
///
/// パッケージは自己署名なので、署名の公開鍵（GitReview-signing.cer）が
/// LocalMachine\TrustedPeople に入っている PC でしか登録できない（入れるのは管理者で 1 回、
/// setup-runtime.ps1 -TrustCertificate）。入っていない PC では何もせず false を返し、
/// 呼び出し側が従来の「その他のオプション」への登録を続ける。
/// </summary>
internal static class ShellPackage
{
    public const string DllName = "GitReviewShell.dll";
    public const string MsixName = "GitReviewShell.msix";
    public const string CerName = "GitReview-signing.cer";

    private const string PackageName = "GitReview.ContextMenu";

    // 登録済みパッケージの一覧。ユーザー単位で、キー名がパッケージのフルネーム
    // （名前_版_アーキテクチャ__発行元 ID）、PackageRootFolder が外部の場所になる。
    private const string RepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>
    /// 上位メニューを使える状態にする。使える状態なら true（従来の登録は外してよい）。
    /// </summary>
    public static bool Ensure(string originDir)
    {
        try
        {
            var localDll = Path.Combine(Stage.LocalRoot, DllName);
            var originDll = Path.Combine(originDir, DllName);
            var originMsix = Path.Combine(originDir, MsixName);
            var originCer = Path.Combine(originDir, CerName);

            // 配布元に一式が無い（まだ配っていない）なら、以前の登録が生きていればそれを使う
            if (!File.Exists(originDll) || !File.Exists(originMsix) || !File.Exists(originCer))
                return RegisteredVersion() is not null && File.Exists(localDll);

            if (!IsTrusted(originCer))
            {
                Stage.Log("shell package: 証明書が信頼されていないため従来のメニューを使う");
                return false;
            }

            // ハンドラーはエクスプローラー側（dllhost）が読み込み中だと置き換えられない。
            // その場合は今ある版のまま使い、次回の起動で置き換える
            if (!File.Exists(localDll) || HashFile(localDll) != HashFile(originDll))
            {
                if (Stage.CopyVerified(originDll, localDll, 1) is null && !File.Exists(localDll))
                    return false;
                Stage.Log("shell package: ハンドラーを更新");
            }

            var wanted = ReadPackageVersion(originMsix);
            var registered = RegisteredVersion();
            if (wanted is not null && registered is not null && registered >= wanted)
                return true;

            // 共有上のファイルを直接登録させず、ローカルに写してから渡す
            var localMsix = Path.Combine(Stage.LocalRoot, MsixName);
            if (Stage.CopyVerified(originMsix, localMsix, 1) is null) return false;

            Register(localMsix);

            registered = RegisteredVersion();
            Stage.Log($"shell package: 登録 {registered?.ToString() ?? "失敗"}");
            return registered is not null;
        }
        catch (Exception ex)
        {
            Stage.Log("shell package: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// このユーザーに登録済みで、外部の場所が %LOCALAPPDATA%\GitReview のパッケージの版。無ければ null。
    /// </summary>
    private static Version? RegisteredVersion()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(RepositoryKey);
        if (packages is null) return null;

        Version? best = null;
        foreach (var fullName in packages.GetSubKeyNames())
        {
            var parts = fullName.Split('_');
            if (parts.Length < 2 || parts[0] != PackageName) continue;
            if (!Version.TryParse(parts[1], out var version)) continue;

            using var key = packages.OpenSubKey(fullName);
            var root = key?.GetValue("PackageRootFolder") as string;
            if (root is null || !string.Equals(root.TrimEnd('\\'), Stage.LocalRoot.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase)) continue;

            if (best is null || version > best) best = version;
        }
        return best;
    }

    private static bool IsTrusted(string cerPath)
    {
        using var cert = X509CertificateLoader.LoadCertificateFromFile(cerPath);
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
    }

    private static Version? ReadPackageVersion(string msixPath)
    {
        using var zip = ZipFile.OpenRead(msixPath);
        var entry = zip.GetEntry("AppxManifest.xml");
        if (entry is null) return null;

        using var stream = entry.Open();
        var identity = XDocument.Load(stream).Root?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
        return Version.TryParse(identity?.Attribute("Version")?.Value, out var v) ? v : null;
    }

    /// <summary>
    /// Add-AppxPackage で登録する。PowerShell 7 では Appx モジュールがそのまま動かないので、
    /// Windows PowerShell 5.1 を明示して呼ぶ。
    /// </summary>
    private static void Register(string msixPath)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var script = $"Add-AppxPackage -Path {Quote(msixPath)} -ExternalLocation {Quote(Stage.LocalRoot)}";

        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p is null) return;

        var stderr = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            Stage.Log("shell package: Add-AppxPackage が応答しない");
            return;
        }

        var error = stderr.Result.Trim();
        if (p.ExitCode != 0 || error.Length > 0)
            Stage.Log($"shell package: Add-AppxPackage exit {p.ExitCode} {error}");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
