using System.IO;
using System.Windows;
using System.Windows.Threading;
using GitReview.Core;

namespace GitReview;

public partial class App : Application
{
    /// <summary>Folder the running build was distributed from (the share, normally).</summary>
    public static string OriginDir { get; private set; } = "";

    /// <summary>Folder the running EXE sits in.</summary>
    public static string BaseDir { get; private set; } = "";

    /// <summary>True when running from a staged copy under %LOCALAPPDATA%\GitReview\app.</summary>
    public static bool IsStaged { get; private set; }

    /// <summary>True when staging was attempted and failed, so this build carries on.</summary>
    public static bool StageFailed { get; private set; }

    /// <summary>Repository path given on the command line, if any.</summary>
    public static string? RepositoryArg { get; private set; }

    /// <summary>コマンドラインで指定されたコミット（sha やブランチ名）。起動時にこれを開く。</summary>
    public static string? CommitArg { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;
        Taskbar.SetAppId();   // must happen before any window exists

        // Seed the palette and the text size before the first window is built, so nothing
        // flashes or re-lays-out on the way up.
        var settings = AppSettings.Load();
        Theme.Apply(settings.Theme);
        Zoom.Apply(settings.DiffFontScale);

        var args = e.Args;
        BaseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        // Where this build came from: the argument, then the remembered origin (when
        // launched from the local entry EXE), then our own folder.
        OriginDir = GetArgValue(args, Stage.OriginArg)
                    ?? (Stage.IsUnderLocalRoot(BaseDir) ? Stage.ReadOrigin() : null)
                    ?? BaseDir;

        IsStaged = Stage.IsUnderAppRoot(BaseDir);
        RepositoryArg = GetRepositoryArg(args);
        CommitArg = GetCommitArg(args);

        // Don't keep the share's EXE locked: copy it locally and run that instead. The
        // repository argument goes along so the staged build opens what was asked for.
        if (!IsStaged && !args.Contains(Stage.NoStageArg, StringComparer.OrdinalIgnoreCase))
        {
            if (Stage.CopyAndRun(OriginDir, RepositoryArg))
            {
                Shutdown();
                return;
            }
            StageFailed = true;   // carry on in place and say so in the window
        }

        new MainWindow().Show();
    }

    /// <summary>First argument that is not an option and names an existing directory.</summary>
    private static string? GetRepositoryArg(string[] args) => PositionalArgs(args).FirstOrDefault(Directory.Exists);

    /// <summary>
    /// フォルダではない方の引数。コミットの sha やブランチ名として扱い、起動時にそれを開く。
    /// 例: GitReview.exe C:\path\to\repo 1a2b3c4
    /// </summary>
    private static string? GetCommitArg(string[] args)
        => PositionalArgs(args).FirstOrDefault(a => !Directory.Exists(a));

    private static IEnumerable<string> PositionalArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], Stage.OriginArg, StringComparison.OrdinalIgnoreCase))
            {
                i++;   // skip its value
                continue;
            }
            if (args[i].StartsWith('-')) continue;

            yield return args[i];
        }
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];

        return null;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"予期しないエラーが発生しました。\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "GitReview", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
