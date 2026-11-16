using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitReview.Core;

/// <summary>Window/session preferences, kept next to the review state.</summary>
public sealed class AppSettings
{
    public string? LastRepository { get; set; }

    /// <summary>
    /// Where the reader left off. Restored on the next launch so an update restart does
    /// not throw away their place in the review.
    /// </summary>
    public string? LastCommitSha { get; set; }
    public string? LastFilePath { get; set; }

    /// <summary>Dark by default.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public string Revision { get; set; } = "HEAD";
    public int MaxCount { get; set; } = 200;
    public bool IgnoreWhitespace { get; set; }
    /// <summary>差分の前後行数。-1 はファイル全体（既定）。</summary>
    public int ContextLines { get; set; } = -1;

    /// <summary>「省略表示」に切り替えたときの前後行数。</summary>
    public int AbbrevContextLines { get; set; } = 8;
    public bool UnreviewedFilesOnly { get; set; }
    public bool UnreviewedCommitsOnly { get; set; }
    public bool SplitView { get; set; }

    /// <summary>差分を、文字を選んでコピーできる表示にしているか。</summary>
    public bool DiffTextMode { get; set; }

    /// <summary>Diff text size, as set with Ctrl+wheel.</summary>
    public double DiffFontScale { get; set; } = 1;

    /// <summary>左ペインの幅。拡大率 100% のときの値で持ち、実際の幅は倍率を掛けて決める。</summary>
    public double LeftPaneWidth { get; set; } = 430;

    /// <summary>左ペインのうちコミット履歴が占める割合。残りが変更ファイル。</summary>
    public double CommitPaneRatio { get; set; } = 0.465;

    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 860;

    /// <summary>ウィンドウの位置。まだ一度も閉じていなければ null で、その場合は既定の位置に出す。</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool Maximized { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Keep the theme readable in the file rather than storing it as a number.
        Converters = { new JsonStringEnumConverter() },
    };

    private static string Path0 => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitReview", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path0))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path0, Encoding.UTF8), Options)
                       ?? new AppSettings();
        }
        catch
        {
            // Preferences are not worth failing startup over.
        }
        return new AppSettings();
    }

    private int _savePending;

    /// <summary>
    /// Writes the file shortly after the last change. Without this the theme, the filters
    /// and the reading position would only survive a clean exit, not a kill or a crash.
    /// </summary>
    public void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            Interlocked.Exchange(ref _savePending, 0);
            Save();
        });
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path0)!);
            File.WriteAllText(Path0, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        }
        catch
        {
            // Ignore: losing preferences is harmless.
        }
    }
}
