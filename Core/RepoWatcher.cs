using System.IO;

namespace GitReview.Core;

/// <summary>
/// リポジトリの変化を見張る。別の窓で commit・checkout・merge・fetch をしたときに、
/// 「再読込」を押さなくても履歴が最新になる。
///
/// 見るのは .git の中の HEAD と refs（packed-refs を含む）だけにしている。
/// 作業ツリー全体を見るとビルド生成物などで通知が溢れるうえ、GitReview が扱うのは
/// コミット済みの履歴だけなので、参照が動いたときに読み直せば足りる。
/// </summary>
internal sealed class RepoWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Action _onChanged;

    public RepoWatcher(string repositoryRoot, Action onChanged)
    {
        _onChanged = onChanged;

        try
        {
            var gitDir = ResolveGitDir(repositoryRoot);
            if (gitDir is null) return;

            _watcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                // 既定の 8KB では、rebase のように一度に多くが動くと取りこぼす
                InternalBufferSize = 64 * 1024,
            };

            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += (_, _) => { /* 溢れても次の変化で拾えるので何もしない */ };

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // 監視できなくても「再読込」で読み直せるので、起動は続ける
            Stage.Log("repo watch: " + ex.Message);
            _watcher = null;
        }
    }

    public void Dispose()
    {
        if (_watcher is null) return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }

    /// <summary>
    /// .git の場所。worktree や submodule では .git がファイルで、中に本体の場所が書いてある。
    /// </summary>
    private static string? ResolveGitDir(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, ".git");
        if (Directory.Exists(path)) return path;
        if (!File.Exists(path)) return null;

        var line = File.ReadAllText(path).Trim();
        const string marker = "gitdir:";
        if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return null;

        var target = line[marker.Length..].Trim();
        if (!Path.IsPathRooted(target)) target = Path.GetFullPath(Path.Combine(repositoryRoot, target));
        return Directory.Exists(target) ? target : null;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsInteresting(e.FullPath)) return;
        if (e is RenamedEventArgs renamed && !IsInteresting(renamed.FullPath) && !IsInteresting(renamed.OldFullPath))
            return;

        _onChanged();
    }

    /// <summary>
    /// 履歴の見え方が変わるものだけを拾う。index や objects の書き込み、
    /// 作業中の .lock ファイル、reflog（logs\）は無視する。
    /// </summary>
    private static bool IsInteresting(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;

        if (name.Equals("HEAD", StringComparison.Ordinal) ||
            name.Equals("packed-refs", StringComparison.Ordinal)) return true;

        var path = fullPath.Replace('/', '\\');
        if (path.Contains("\\logs\\", StringComparison.OrdinalIgnoreCase)) return false;

        return path.Contains("\\refs\\", StringComparison.OrdinalIgnoreCase);
    }
}
