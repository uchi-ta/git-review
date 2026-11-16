using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitReview.Core;

public sealed class ReviewRecord
{
    public bool Reviewed { get; set; }
    public DateTimeOffset? At { get; set; }
}

/// <summary>Review state for one diff target (a commit, or a base..head range).</summary>
public sealed class TargetState
{
    /// <summary>Total changed files the last time this target was opened; -1 if never opened.</summary>
    public int Total { get; set; } = -1;

    /// <summary>
    /// 差分の起点（単一コミットなら第1親）。アメンドで sha が変わったときに、
    /// 同じ親を持つ前の版を探すために使う。古い記録には無いので null がある。
    /// </summary>
    public string? Base { get; set; }

    public Dictionary<string, ReviewRecord> Files { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public int ReviewedCount => Files.Count(kv => kv.Value.Reviewed);
}

public sealed class ReviewData
{
    public int Version { get; set; } = 1;
    public string RepositoryRoot { get; set; } = "";
    public Dictionary<string, TargetState> Targets { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Persists per-file review checkmarks to %LOCALAPPDATA%\GitReview\state\*.json.
/// Keys are commit shas, so recorded state never goes stale: a commit's content is immutable.
/// </summary>
public sealed class ReviewStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly ReviewData _data;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private ReviewStore(string path, ReviewData data)
    {
        _path = path;
        _data = data;
    }

    public static ReviewStore Load(string repositoryRoot)
    {
        var path = ResolvePath(repositoryRoot);
        ReviewData? data = null;

        if (File.Exists(path))
        {
            try
            {
                data = JsonSerializer.Deserialize<ReviewData>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            }
            catch (Exception)
            {
                // A corrupt or half-written state file must not stop the app. Keep the bad file
                // aside so nothing is silently lost, then start from empty state.
                TryBackup(path);
            }
        }

        data ??= new ReviewData();
        data.RepositoryRoot = repositoryRoot;
        return new ReviewStore(path, data);
    }

    public TargetState? Find(string targetKey)
    {
        lock (_data) return _data.Targets.GetValueOrDefault(targetKey);
    }

    public bool IsReviewed(string targetKey, string path)
    {
        lock (_data)
        {
            return _data.Targets.TryGetValue(targetKey, out var state)
                && state.Files.TryGetValue(path, out var record)
                && record.Reviewed;
        }
    }

    public void SetReviewed(string targetKey, string path, bool reviewed)
    {
        lock (_data)
        {
            var state = GetOrCreate(targetKey);

            if (reviewed) state.Files[path] = new ReviewRecord { Reviewed = true, At = DateTimeOffset.Now };
            else state.Files.Remove(path);
        }
    }

    public void SetTotal(string targetKey, int total)
    {
        lock (_data) GetOrCreate(targetKey).Total = total;
    }

    public void SetBase(string targetKey, string baseSha)
    {
        lock (_data) GetOrCreate(targetKey).Base = baseSha;
    }

    /// <summary>
    /// アメンド前の記録を探す。同じ親から作られた別のコミットで、レビュー済みが1件でもあるもの。
    /// 複数あれば、最後にレビューした時刻が新しいものを選ぶ。
    /// </summary>
    public (string Key, IReadOnlyList<string> Reviewed)? FindPredecessor(string baseSha, string exceptKey)
    {
        lock (_data)
        {
            var best = _data.Targets
                .Where(kv => kv.Key != exceptKey && kv.Value.Base == baseSha && kv.Value.ReviewedCount > 0
                             && !kv.Key.Contains("..", StringComparison.Ordinal))   // 範囲の記録は対象外
                .OrderByDescending(kv => kv.Value.Files.Values.Max(r => r.At ?? DateTimeOffset.MinValue))
                .Select(kv => (kv.Key, kv.Value))
                .FirstOrDefault();

            if (best.Key is null) return null;

            var reviewed = best.Value.Files
                .Where(f => f.Value.Reviewed)
                .Select(f => f.Key)
                .ToList();

            return (best.Key, reviewed);
        }
    }

    /// <summary>Caller must hold the lock on <see cref="_data"/>.</summary>
    private TargetState GetOrCreate(string targetKey)
    {
        if (!_data.Targets.TryGetValue(targetKey, out var state))
        {
            state = new TargetState();
            _data.Targets[targetKey] = state;
        }
        return state;
    }

    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);

            // Serialising walks the dictionaries, and the UI thread adds to them whenever a
            // file is checked off. Without the lock that races and the save is lost.
            string json;
            lock (_data) json = JsonSerializer.Serialize(_data, JsonOptions);

            var temp = _path + ".tmp";

            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string ResolvePath(string repositoryRoot)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitReview", "state");

        var name = Sanitize(Path.GetFileName(repositoryRoot.TrimEnd('\\', '/')));
        if (name.Length == 0) name = "repo";

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repositoryRoot.ToLowerInvariant())))[..12];

        return Path.Combine(baseDir, $"{name}-{hash}.json");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    private static void TryBackup(string path)
    {
        try
        {
            File.Move(path, $"{path}.broken-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
        catch
        {
            // Nothing useful to do; Load() falls back to empty state either way.
        }
    }
}
