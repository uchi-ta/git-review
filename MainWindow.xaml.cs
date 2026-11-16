using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitReview.Core;
using Microsoft.Win32;

namespace GitReview;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _vm;

    /// <summary>
    /// Depth of the "ignore selection changes" scope. A counter rather than a flag because
    /// list rebuilds nest inside operations that already suppress.
    /// </summary>
    private int _suppressDepth;

    private bool SelectionMuted => _suppressDepth > 0;

    private ListBox _activePane;

    /// <summary>
    /// Endpoints of a keyboard range selection in the commit list. WPF keeps its own anchor,
    /// but only input updates it, so programmatic moves (G, [, ], J/K) would leave Shift+Arrow
    /// extending from a stale row. Tracking the pair here keeps the two in step.
    /// </summary>
    private int _commitAnchor = -1;
    private int _commitFocus = -1;

    /// <summary>C を押すたびに変わる前後行数。-1 はファイル全体（既定）。</summary>
    private static readonly int[] ContextSteps = [-1, 3, 8, 25];

    /// <summary>Poll interval for "has the shared build changed?".</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>リポジトリの変化を検知してから読み直すまでの待ち。書き込みが続く間はまとめる。</summary>
    private static readonly TimeSpan RepoWatchDelay = TimeSpan.FromMilliseconds(700);

    private DispatcherTimer? _updateTimer;
    private string? _myVersion;

    /// <summary>Set while handing over to a new build, so Closing does not save stale state.</summary>
    private bool _handingOver;

    /// <summary>File to reselect once the restored commit's file list arrives.</summary>
    private string? _pendingFilePath;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _vm = new MainViewModel(_settings);

        InitializeComponent();

        DataContext = _vm;
        _activePane = CommitList;

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        RestoreWindowPosition();
        if (_settings.Maximized) WindowState = WindowState.Maximized;

        ApplyLeftWidth();
        ApplyPaneHeights();

        _vm.ErrorOccurred += OnError;
        _vm.CommitsRefreshing += OnCommitsRefreshing;
        _vm.CommitsRefreshed += OnCommitsRefreshed;
        _vm.FilesRefreshing += OnFilesRefreshing;
        _vm.FilesRefreshed += OnFilesRefreshed;
        _vm.DiffShown += OnDiffShown;

        BuildHelp();
        SetWindowIcon();
        UpdateThemeButton();

        // 色を付けたまま文字を選べるようにする（一覧の行は文字単位では選べないため自前で持つ）
        _unifiedSelector = new DiffTextSelector(DiffList, ["Body"],
            (i, _) => i < _vm.DiffLines.Count ? _vm.DiffLines[i].DisplayText : null,
            i => BlockRange(_vm.DiffLines.Count, j => _vm.DiffLines[j].Kind == DiffLineKind.Hunk, i));

        _splitSelector = new DiffTextSelector(SplitList, ["LeftText", "RightText"],
            (i, cell) => i >= _vm.DiffRows.Count
                ? null
                : cell == "LeftText" ? _vm.DiffRows[i].Left.Text : _vm.DiffRows[i].Right.Text,
            i => BlockRange(_vm.DiffRows.Count, j => _vm.DiffRows[j].Kind == DiffRowKind.Hunk, i));

        _unifiedBlockMarker = new DiffBlockMarker(DiffList);
        _splitBlockMarker = new DiffBlockMarker(SplitList);

        // ここではまだ画面ができていないので、トグルの初期値を入れてから表示を決める
        TextModeButton.IsChecked = _settings.DiffTextMode;
        _uiReady = true;
        ApplySplitView();

        PreviewKeyDown += OnGlobalKeyDown;
        PreviewMouseWheel += OnGlobalMouseWheel;
        Loaded += OnLoaded;
        Closing += OnClosing;

        // The title bar can only be tinted once the window has an HWND.
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
    }

    /// <summary>The diff list currently on screen: unified or side-by-side.</summary>
    private ListBox ActiveDiffList => _vm.SplitView ? SplitList : DiffList;

    private DiffTextSelector? _unifiedSelector;
    private DiffTextSelector? _splitSelector;

    private DiffBlockMarker? _unifiedBlockMarker;
    private DiffBlockMarker? _splitBlockMarker;

    /// <summary>今いる変更箇所を囲む枠（表示中の一覧のもの）。</summary>
    private DiffBlockMarker? ActiveBlockMarker => _vm.SplitView ? _splitBlockMarker : _unifiedBlockMarker;

    private void ClearBlockMarkers()
    {
        _unifiedBlockMarker?.Clear();
        _splitBlockMarker?.Clear();
    }

    /// <summary>今表示している差分の文字選択。</summary>
    private DiffTextSelector? ActiveSelector => _vm.SplitView ? _splitSelector : _unifiedSelector;

    /// <summary>4 回クリックしたときに広げる範囲。<paramref name="isBoundary"/> は @@ の行。</summary>
    private static (int Start, int End) BlockRange(int count, Func<int, bool> isBoundary, int index)
    {
        if (index < 0 || index >= count) return (0, Math.Max(0, count - 1));

        var start = index;
        while (start > 0 && !isBoundary(start)) start--;

        var end = index;
        while (end + 1 < count && !isBoundary(end + 1)) end++;

        return (start, end);
    }

    private void ClearTextSelection()
    {
        _unifiedSelector?.Clear();
        _splitSelector?.Clear();
    }

    private bool IsDiffPane(ListBox list)
        => ReferenceEquals(list, DiffList) || ReferenceEquals(list, SplitList);

    private void OnSplitViewChanged(object sender, RoutedEventArgs e) => ApplySplitView();

    private void ToggleSplitView()
    {
        _vm.SplitView = !_vm.SplitView;
        ApplySplitView();
    }

    private void ApplySplitView()
    {
        ApplyDiffView();

        // Keep the focused pane pointing at something visible.
        if (IsDiffPane(_activePane)) _activePane = ActiveDiffList;
        UpdatePaneFrames();

        ScrollDiffToTop();
    }

    // ---------------------------------------------------------------- テキスト表示

    /// <summary>画面ができるまではトグルの変化に反応しない（起動時に文字へ移ってしまうため）。</summary>
    private bool _uiReady;

    private bool TextMode => TextModeButton.IsChecked == true;

    private void OnTextModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        ApplyDiffView();

        // 切り替えた直後に選べるよう、文字の表示にはキーボードごと移す
        if (TextMode) DiffTextBox.Focus();
        else FocusPane(ActiveDiffList);

        _settings.DiffTextMode = TextMode;
        _settings.ScheduleSave();
    }

    /// <summary>差分ペインに出すもの（色付きの一覧か、選んでコピーできる文字か）を決める。</summary>
    private void ApplyDiffView()
    {
        var split = _vm.SplitView;

        DiffList.Visibility = !TextMode && !split ? Visibility.Visible : Visibility.Collapsed;
        SplitList.Visibility = !TextMode && split ? Visibility.Visible : Visibility.Collapsed;
        DiffTextBox.Visibility = TextMode ? Visibility.Visible : Visibility.Collapsed;

        if (TextMode) FillDiffText();
        UpdateOverview();
    }

    /// <summary>右端のスクロールバーに、今の差分の変更位置を渡す。</summary>
    private void UpdateOverview()
    {
        DiffMap.Visibility = TextMode ? Visibility.Collapsed : Visibility.Visible;
        if (TextMode) return;

        DiffMap.Follow(DiffScroller());
        DiffMap.SetLines(BuildOverviewLines());
    }

    /// <summary>縮小イメージに渡す行。左右に並べているときは、新しい側の文字を使う。</summary>
    private IReadOnlyList<DiffOverviewLine> BuildOverviewLines()
    {
        if (_vm.SplitView)
        {
            return [.. _vm.DiffRows.Select(row =>
            {
                var mark = (row.Left.Kind, row.Right.Kind) switch
                {
                    (DiffCellKind.Removed, DiffCellKind.Added) => DiffMark.Both,
                    (_, DiffCellKind.Added) => DiffMark.Added,
                    (DiffCellKind.Removed, _) => DiffMark.Removed,
                    _ => DiffMark.None,
                };

                var text = row.Right.Text.Length > 0 ? row.Right.Text : row.Left.Text;
                return new DiffOverviewLine(mark, text);
            })];
        }

        return [.. _vm.DiffLines.Select(line => new DiffOverviewLine(
            line.Kind switch
            {
                DiffLineKind.Added => DiffMark.Added,
                DiffLineKind.Removed => DiffMark.Removed,
                _ => DiffMark.None,
            },
            line.Content))];
    }

    /// <summary>
    /// 表示中の差分を、そのままの unified diff として流し込む。行数が多くても TextBox は
    /// 一度に持てるが、極端に長い差分では重くなるので上限を設ける。
    /// </summary>
    private void FillDiffText()
    {
        const int MaxLines = 20000;

        var lines = _vm.DiffLines;
        var sb = new StringBuilder();

        foreach (var line in lines.Take(MaxLines)) sb.AppendLine(line.Text);
        if (lines.Count > MaxLines)
            sb.AppendLine($"…（ここから先の {lines.Count - MaxLines} 行は省略しました）");

        DiffTextBox.Text = sb.ToString();
        DiffTextBox.CaretIndex = 0;
        DiffTextBox.ScrollToHome();
    }

    // ---------------------------------------------------------------- text size

    /// <summary>Ctrl+wheel resizes the diff text, as Ctrl+wheel zooms the VS Code editor.</summary>
    /// <summary>ホイール 1 刻みで送る量の倍率。WPF の既定（3 行ぶん）では細かすぎるため。</summary>
    private const double WheelScrollFactor = 2;

    /// <summary>WPF が 1 行とみなす高さ。ピクセル単位でスクロールする一覧でも同じ値を使う。</summary>
    private const double WheelLineHeight = 16;

    private void OnGlobalMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            ScrollByWheel(e);
            return;
        }

        e.Handled = true;   // otherwise the list under the pointer scrolls as well
        SetZoom(Zoom.Step(Zoom.Scale, e.Delta));
    }

    private void SetZoom(double scale)
    {
        if (Math.Abs(scale - Zoom.Scale) < 0.001) return;

        // 倍率を変える前に読む。変えた後では、ドラッグされた幅を新しい倍率で割ってしまう
        CaptureLeftWidth();

        Zoom.Apply(scale);
        _settings.DiffFontScale = Zoom.Scale;
        _settings.ScheduleSave();
        ApplyLeftWidth();

        _vm.SetStatus($"文字サイズ {Zoom.Percent}%");
    }

    /// <summary>
    /// ホイールの送り幅を増やす。OS の「1 度に n 行」の設定はそのまま尊重し、その倍率だけ変える。
    /// 「1 画面ずつ」にしている場合は何もしない（その動きを壊さないため）。
    /// </summary>
    private void ScrollByWheel(MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;   // 横スクロールは既定のまま

        var lines = SystemParameters.WheelScrollLines;
        if (lines <= 0) return;

        if (ScrollerUnder(e.OriginalSource as DependencyObject) is not { } scroller) return;
        if (scroller.ScrollableHeight <= 0) return;

        e.Handled = true;
        scroller.ScrollToVerticalOffset(
            scroller.VerticalOffset - e.Delta / 120.0 * lines * WheelLineHeight * WheelScrollFactor);
    }

    /// <summary>マウスの下にある一覧のスクロール担当を探す。</summary>
    private static ScrollViewer? ScrollerUnder(DependencyObject? from)
    {
        for (var d = from; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollViewer found) return found;

        return null;
    }

    // ---------------------------------------------------------------- pane width

    /// <summary>差分が潰れないように、左ペインに渡せる最大の幅を決めるときの差分側の下限。</summary>
    private const double DiffMinWidth = 360;

    /// <summary>XAML の仕切り（GridSplitter）の幅。</summary>
    private const double SplitterWidth = 8;

    /// <summary>最後に自分で設定した左ペインの幅。</summary>
    private double _appliedLeftWidth;

    /// <summary>
    /// 左ペインの幅を拡大率に合わせる。覚えているのは 100% のときの幅なので、
    /// 文字が大きくなれば左ペインの取り分も同じ割合で増える。
    /// </summary>
    private void ApplyLeftWidth()
    {
        var width = _settings.LeftPaneWidth * Zoom.Scale;

        // 窓が狭いときは、差分側が読める幅を先に確保する
        var available = MainSplit.ActualWidth;
        if (available > 0) width = Math.Min(width, available - SplitterWidth - DiffMinWidth);

        width = Math.Max(LeftColumn.MinWidth, width);
        LeftColumn.Width = new GridLength(width);

        // 次にここへ来たとき、ユーザーが動かしたのかを見分けるために覚えておく
        _appliedLeftWidth = width;
    }

    /// <summary>
    /// 仕切りをドラッグされていたら、その幅を 100% 換算で覚え直す。
    /// ドラッグの完了イベントではまだ幅が反映されていないので、イベントには頼らず、
    /// 自分で決めた幅（<see cref="_appliedLeftWidth"/>）と違っていればユーザーが動かしたとみなす。
    /// </summary>
    private void CaptureLeftWidth()
    {
        var actual = LeftColumn.ActualWidth;
        if (actual <= 0 || Math.Abs(actual - _appliedLeftWidth) < 0.5) return;

        _settings.LeftPaneWidth = Math.Round(actual / Zoom.Scale);
    }

    /// <summary>
    /// コミット履歴と変更ファイルの取り分。こちらは割合で覚えるので、窓の高さが変わっても
    /// 同じ比率になる。仕切りのドラッグは星取りの値に入るので、読むだけでよい。
    /// </summary>
    private void ApplyPaneHeights()
    {
        var ratio = Math.Clamp(_settings.CommitPaneRatio, 0.15, 0.85);

        CommitRow.Height = new GridLength(ratio, GridUnitType.Star);
        FileRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void CapturePaneHeights()
    {
        var total = CommitPane.ActualHeight + FilePane.ActualHeight;
        if (total <= 0) return;

        _settings.CommitPaneRatio = Math.Round(CommitPane.ActualHeight / total, 4);
    }

    private void OnMainSplitSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;

        CaptureLeftWidth();
        ApplyLeftWidth();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) => CycleTheme();

    private void CycleTheme()
    {
        _settings.Theme = Theme.Next(_settings.Theme);
        _settings.ScheduleSave();
        Theme.Apply(_settings.Theme);
        UpdateThemeButton();

        // Word-highlight backgrounds are resolved when a row is built, so rebuild the rows
        // to pick up the new palette.
        DiffList.Items.Refresh();
        SplitList.Items.Refresh();
        DiffMap.Refresh();   // 縮小イメージも色を取り直す
        UpdatePaneFrames();
        _vm.SetStatus($"テーマ: {Theme.Describe(_settings.Theme)}");
    }

    // Geometric shapes rather than sun/moon glyphs: those are missing from some UI fonts.
    private void UpdateThemeButton()
        => ThemeButton.Content = _settings.Theme switch
        {
            AppTheme.Light => "○ ライト",
            AppTheme.Dark => "● ダーク",
            _ => "◐ OS",
        };

    // ---------------------------------------------------------------- lifecycle

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (App.IsStaged)
        {
            // Tell whoever launched us that this build is up, then keep the local entry
            // EXE and its shortcut current and start watching the share for updates.
            Stage.MarkStarted(App.BaseDir);
            StartStagingChores(App.OriginDir);
            StartUpdateWatch(Path.GetFileName(App.BaseDir));
        }

        if (App.StageFailed)
            _vm.SetStatus("新しい版を起動できなかったため、この版で動いています" +
                          "（%LOCALAPPDATA%\\GitReview\\launch.log 参照）");

        // A folder handed over by the Explorer menu may not be a repository at all, and
        // silently opening a different one would be baffling. Remember it and say so.
        string? rejected = null;

        foreach (var candidate in StartupCandidates())
        {
            // Quiet: these are guesses, and a miss just means trying the next one.
            if (await _vm.OpenRepositoryAsync(candidate, reportErrors: false))
            {
                await AfterRepositoryOpenedAsync();

                if (rejected is not null)
                    _vm.SetStatus($"{rejected} は git リポジトリではないため、前回のリポジトリを開きました");

                return;
            }

            if (rejected is null && candidate == App.RepositoryArg) rejected = candidate;
        }

        _vm.SetStatus(rejected is not null
            ? $"{rejected} は git リポジトリではありません。フォルダを開いてください (O)"
            : "git リポジトリが見つかりません。フォルダを開いてください (O)");

        FocusPane(CommitList);
    }

    private IEnumerable<string> StartupCandidates()
    {
        if (App.RepositoryArg is not null) yield return App.RepositoryArg;

        if (!string.IsNullOrWhiteSpace(_settings.LastRepository) &&
            Directory.Exists(_settings.LastRepository))
            yield return _settings.LastRepository;

        // When staged, the working directory is the app folder and never a repository.
        if (!App.IsStaged) yield return Environment.CurrentDirectory;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _repoWatchTimer?.Stop();
        _repoWatcher?.Dispose();

        // During a handover the new build owns the state file; writing our copy back
        // would undo anything it has recorded since.
        if (_handingOver) return;

        SaveSession();
    }

    /// <summary>
    /// 前回閉じた位置に出す。モニタを外した後などで画面の外になる場合は、既定の位置に任せる。
    /// 少しでも画面に掛かっていれば復元する（タイトルバーさえ見えれば動かせるため）。
    /// </summary>
    private void RestoreWindowPosition()
    {
        if (_settings.WindowLeft is not { } left || _settings.WindowTop is not { } top) return;

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        // タイトルバーが 120px 以上は画面に掛かっていること
        if (left > screenRight - 120 || left + Width < screenLeft + 120) return;
        if (top < screenTop - 4 || top > screenBottom - 60) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private void SaveSession()
    {
        // 仕切りの位置は、ドラッグのイベントではなくここで読む
        CaptureLeftWidth();
        CapturePaneHeights();

        _settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
        _settings.Save();

        // Run off the UI thread so the async save cannot deadlock on the dispatcher.
        Task.Run(() => _vm.FlushAsync()).GetAwaiter().GetResult();
    }

    // ---------------------------------------------------------------- staging

    /// <summary>
    /// Refreshes the local entry EXE, the Start menu shortcut and the Explorer menu entry.
    /// These wait on the share, so they run off the UI thread; the shortcut needs COM,
    /// hence an STA thread.
    /// </summary>
    private static void StartStagingChores(string originDir)
    {
        var thread = new Thread(() =>
        {
            Stage.EnsureLocalEntry(originDir);
            Taskbar.EnsureStartMenuShortcut(originDir);

            // 証明書が信頼されている PC では Windows 11 の上位メニューに出し、
            // 「その他のオプション」側の従来の登録は二重になるので外す。
            // 従来の登録はローカルの入口 EXE を指す（共有から直接起動すると毎回警告が出るため）。
            if (ShellPackage.Ensure(originDir))
                ShellMenu.Unregister();
            else
                ShellMenu.Register(Stage.LocalEntry, Taskbar.EnsureLocalIconFile());
        })
        {
            IsBackground = true,
            Name = "GitReview staging",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // ---------------------------------------------------------------- update watch

    /// <summary>
    /// Watches the share for a newer build. When one appears, the review state is flushed
    /// and the new build is started; this one exits once it is up.
    /// </summary>
    private void StartUpdateWatch(string myVersion)
    {
        _myVersion = myVersion;
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += (_, _) => CheckForUpdate();
        _updateTimer.Start();
    }

    private async void CheckForUpdate()
    {
        string latest;
        try { latest = Stage.Version(App.OriginDir); }
        catch { return; }

        if (latest == "0") return;                     // share unreachable
        if (latest == _myVersion) return;              // unchanged
        if (latest == Stage.FailedVersion) return;     // already failed to start this session

        _updateTimer!.Stop();

        // Hand over cleanly: stop accepting input, persist everything, then start the
        // new build. Once the handover flag is set, Closing leaves the state file alone.
        _handingOver = true;
        IsEnabled = false;
        _vm.SetStatus("新しい版に切り替えています…");

        SaveSession();

        // Copying off the share and waiting for the new build to come up can take a
        // while, so keep it off the UI thread. Hand over the open repository as well.
        var repository = _vm.RepositoryRoot ?? App.RepositoryArg;

        if (await Task.Run(() => Stage.CopyAndRun(App.OriginDir, repository)))
        {
            Application.Current.Shutdown();
            return;
        }

        _handingOver = false;
        IsEnabled = true;
        _updateTimer.Start();
        _vm.SetStatus("新しい版を起動できませんでした。この版で続けます" +
                      "（%LOCALAPPDATA%\\GitReview\\launch.log 参照）");
    }

    /// <summary>
    /// Uses the embedded icon rather than the EXE's, so a single-file build staged under
    /// %LOCALAPPDATA% still shows the right picture.
    /// </summary>
    private void SetWindowIcon()
    {
        try
        {
            using var stream = Taskbar.OpenIconStream();
            if (stream is null) return;

            var decoder = new IconBitmapDecoder(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0) Icon = decoder.Frames[0];
        }
        catch { /* the default icon is fine */ }
    }

    private void OnError(string message)
        => Dispatcher.Invoke(() =>
            MessageBox.Show(this, message, "GitReview", MessageBoxButton.OK, MessageBoxImage.Warning));

    // ---------------------------------------------------------------- repository

    private async void OnOpenRepository(object sender, RoutedEventArgs e) => await OpenRepositoryAsync();

    private async Task OpenRepositoryAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "git リポジトリのフォルダを選択",
            InitialDirectory = _vm.RepositoryRoot ?? _settings.LastRepository ?? Environment.CurrentDirectory,
        };

        if (dialog.ShowDialog(this) != true) return;

        if (await _vm.OpenRepositoryAsync(dialog.FolderName))
            await AfterRepositoryOpenedAsync();
    }

    /// <summary>
    /// Selects the commit to start on and waits for its files to load, so the caller can
    /// put its own message in the status bar afterwards without it being overwritten.
    /// </summary>
    private async Task AfterRepositoryOpenedAsync()
    {
        StartRepoWatch();

        if (CommitList.Items.Count == 0)
        {
            FocusPane(CommitList);
            return;
        }

        // 引数でコミットを指定されていれば、それを開く（1 回だけ）
        if (_pendingCommitArg is { Length: > 0 } wanted)
        {
            _pendingCommitArg = null;
            if (await TryOpenCommitAsync(wanted))
            {
                FocusPane(CommitList);
                return;
            }

            _vm.SetStatus($"{wanted} というコミットは見つかりませんでした");
        }

        if (CommitList.SelectedItems.Count == 0)
        {
            // Pick up where the reader left off, so an update restart is not disruptive.
            var last = _settings.LastCommitSha is null
                ? null
                : _vm.Commits.FirstOrDefault(c => c.Sha == _settings.LastCommitSha);

            _pendingFilePath = last is null ? null : _settings.LastFilePath;

            SelectSingleCommit(last ?? _vm.Commits[0], silent: true);
            await ReloadCurrentSelectionAsync();
        }

        if (_vm.TakeRevisionNote() is { } note) _vm.SetStatus(note);

        FocusPane(CommitList);
    }

    /// <summary>引数で渡されたコミット。開いたら null にする。</summary>
    private string? _pendingCommitArg = App.CommitArg;

    /// <summary>
    /// sha やブランチ名で指されたコミットを開く。履歴に載っていないときは、そのコミットを
    /// 起点にして履歴を読み直す（古いコミットを直接渡されても開けるように）。
    /// </summary>
    private async Task<bool> TryOpenCommitAsync(string commitish)
    {
        var sha = await _vm.ResolveCommitAsync(commitish);
        if (sha is null) return false;

        var commit = _vm.Commits.FirstOrDefault(c => c.Sha == sha);
        if (commit is null)
        {
            _vm.Revision = sha;
            await _vm.ReloadCommitsAsync();
            commit = _vm.Commits.FirstOrDefault(c => c.Sha == sha);
        }

        if (commit is null) return false;

        SelectSingleCommit(commit, silent: true);
        await ReloadCurrentSelectionAsync();
        return true;
    }

    // ---------------------------------------------------------------- 変更の検知

    private RepoWatcher? _repoWatcher;
    private DispatcherTimer? _repoWatchTimer;

    /// <summary>
    /// リポジトリの参照が動いたら履歴を読み直す。別の窓で commit や checkout をしても、
    /// 「再読込」を押さずに画面が最新になる。
    /// </summary>
    private void StartRepoWatch()
    {
        _repoWatcher?.Dispose();
        _repoWatcher = null;

        if (_vm.RepositoryRoot is null) return;

        // 通知は別スレッドから来るうえ、rebase などでは一度に何度も来るのでまとめる
        if (_repoWatchTimer is null)
        {
            _repoWatchTimer = new DispatcherTimer { Interval = RepoWatchDelay };
            _repoWatchTimer.Tick += async (_, _) => await OnRepoChangedAsync();
        }

        _repoWatcher = new RepoWatcher(_vm.RepositoryRoot, () =>
            Dispatcher.BeginInvoke(() =>
            {
                _repoWatchTimer.Stop();
                _repoWatchTimer.Start();
            }));
    }

    private async Task OnRepoChangedAsync()
    {
        _repoWatchTimer!.Stop();

        // 読み込み中や新しい版への切り替え中は触らない。次の変化、または待ち直しで拾う
        if (_handingOver || _vm.RepositoryRoot is null) return;
        if (_vm.IsBusy)
        {
            _repoWatchTimer.Start();
            return;
        }

        var before = CommitList.SelectedItems.Cast<CommitItem>().Select(c => c.Sha).ToList();

        _vm.RememberSelection(CommitList.SelectedItems.Cast<CommitItem>());
        await _vm.ReloadCommitsAsync();

        var after = CommitList.SelectedItems.Cast<CommitItem>().Select(c => c.Sha).ToList();

        // 読んでいたコミットがそのまま残っていれば、差分はいじらない（読んでいた位置が飛ぶため）
        if (!after.SequenceEqual(before))
        {
            if (after.Count == 0 && CommitList.Items.Count > 0) CommitList.SelectedIndex = 0;
            else await ReloadCurrentSelectionAsync();
        }

        _vm.SetStatus("リポジトリの変更を検知して履歴を更新しました");
    }

    // ---------------------------------------------------------------- ブランチ

    private async void OnOpenBranches(object sender, RoutedEventArgs e) => await OpenBranchesAsync();

    /// <summary>
    /// ブランチを探して、そのブランチの履歴に切り替える。checkout はしない
    /// （GitReview はリポジトリに書き込まない）。見るものを変えるだけ。
    /// </summary>
    private async Task OpenBranchesAsync()
    {
        if (_vm.RepositoryRoot is null) return;

        _vm.BranchFilter = "";
        await _vm.LoadBranchesAsync();

        BranchPopup.IsOpen = true;
        if (BranchList.Items.Count > 0) BranchList.SelectedIndex = 0;

        // 開いた直後はまだ中身ができていないので、組み上がってからキーボードを渡す
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            BranchSearchBox.Focus();
            Keyboard.Focus(BranchSearchBox);
            BranchSearchBox.SelectAll();
        });
    }

    private void OnBranchSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                MoveBranch(+1);
                return;
            case Key.Up:
                e.Handled = true;
                MoveBranch(-1);
                return;
            case Key.Enter:
                e.Handled = true;
                _ = ApplyBranchAsync(SelectedBranch);
                return;
        }
    }

    /// <summary>選んでいるブランチ。絞り込み直後で選択が外れていれば先頭。</summary>
    private BranchItem? SelectedBranch
        => BranchList.SelectedItem as BranchItem ?? BranchList.Items.OfType<BranchItem>().FirstOrDefault();

    /// <summary>
    /// 絞り込むと一覧を作り直すので選択が外れる。すぐ Enter で決められるよう、先頭を選び直す。
    /// 並べ替えが終わってからでないと選べないので、ひと呼吸おく。
    /// </summary>
    private void OnBranchSearchChanged(object sender, TextChangedEventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (BranchList.SelectedIndex < 0 && BranchList.Items.Count > 0) BranchList.SelectedIndex = 0;
        });

    private void MoveBranch(int direction)
    {
        if (BranchList.Items.Count == 0) return;

        var index = Math.Clamp(BranchList.SelectedIndex + direction, 0, BranchList.Items.Count - 1);
        BranchList.SelectedIndex = index;
        BranchList.ScrollIntoView(BranchList.Items[index]);
    }

    private void OnBranchClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (BranchList.ContainerFromElement(source) is not ListBoxItem item) return;

        e.Handled = true;
        _ = ApplyBranchAsync(item.DataContext as BranchItem);
    }

    private async Task ApplyBranchAsync(BranchItem? branch)
    {
        if (branch is null) return;

        BranchPopup.IsOpen = false;

        _vm.Revision = branch.Name;
        await ReloadAsync();

        _vm.SetStatus($"{branch.Name} の履歴を表示しています" +
                      $"{(branch.IsRemote ? "（リモート追跡ブランチ）" : "")}");
        FocusPane(CommitList);
    }

    private async void OnReload(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        if (_vm.RepositoryRoot is null) return;

        _vm.RememberSelection(CommitList.SelectedItems.Cast<CommitItem>());
        await _vm.ReloadCommitsAsync();

        if (CommitList.SelectedItems.Count == 0 && CommitList.Items.Count > 0)
            CommitList.SelectedIndex = 0;
        else
            await ReloadCurrentSelectionAsync();

        if (_vm.TakeRevisionNote() is { } note) _vm.SetStatus(note);
    }

    private async void OnLoadMore(object sender, RoutedEventArgs e) => await LoadMoreAsync();

    private async Task LoadMoreAsync()
    {
        if (_vm.RepositoryRoot is null) return;
        _vm.MaxCount += 200;
        await ReloadAsync();
    }

    private async void OnRevBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ReloadAsync();
        FocusPane(CommitList);
    }

    private void OnFilterBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        FocusPane(CommitList);
    }

    // ---------------------------------------------------------------- selection

    private void MuteSelection() => _suppressDepth++;

    /// <summary>Runs <paramref name="action"/> with selection events muted.</summary>
    private void WithoutSelectionEvents(Action action)
    {
        MuteSelection();
        try
        {
            action();
        }
        finally
        {
            _suppressDepth--;
        }
    }

    /// <summary>
    /// Rebuilding a list destroys the focused ListBoxItem, and WPF then restarts arrow
    /// navigation from the first row. These flags let the focus be put back afterwards.
    /// </summary>
    private bool _commitListHadFocus;
    private bool _fileListHadFocus;

    private void OnCommitsRefreshing()
    {
        _commitListHadFocus = CommitList.IsKeyboardFocusWithin;
        MuteSelection();
    }

    private void OnFilesRefreshing()
    {
        _fileListHadFocus = FileList.IsKeyboardFocusWithin;
        MuteSelection();
    }

    /// <summary>Restores the commit selection the rebuild dropped, then unmutes.</summary>
    private void OnCommitsRefreshed()
    {
        try
        {
            CommitList.SelectedItems.Clear();
            foreach (var c in _vm.ResolvePendingSelection())
                CommitList.SelectedItems.Add(c);

            if (CommitList.SelectedItem is CommitItem first)
                CommitList.ScrollIntoView(first);
        }
        finally
        {
            _suppressDepth--;
        }

        if (_commitListHadFocus) FocusSelectedItem(CommitList);
    }

    private void OnFilesRefreshed()
    {
        try
        {
            var node = _vm.SelectedFile is null ? null : _vm.FindNode(_vm.SelectedFile);

            FileList.SelectedItem = node is not null && _vm.Files.Contains(node) ? node : null;
            if (FileList.SelectedItem is FileNode row) FileList.ScrollIntoView(row);
        }
        finally
        {
            _suppressDepth--;
        }

        if (_fileListHadFocus) FocusSelectedItem(FileList);
    }

    /// <summary>Puts the keyboard back on the selected row so arrow keys continue from it.</summary>
    private static void FocusSelectedItem(ListBox list)
    {
        if (list.SelectedIndex < 0) return;

        list.UpdateLayout();   // realise the container if virtualisation dropped it
        (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem)?.Focus();
    }

    private async void OnCommitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectionMuted) return;

        // A plain click or single-item selection re-anchors the keyboard range.
        if (CommitList.SelectedItems.Count == 1)
            _commitAnchor = _commitFocus = CommitList.SelectedIndex;

        await ReloadCurrentSelectionAsync();
    }

    /// <summary>Shift+Up/Down in the commit list: grow or shrink the reviewed range.</summary>
    private async Task ExtendCommitSelectionAsync(int delta)
    {
        if (_vm.Commits.Count == 0) return;

        var last = _vm.Commits.Count - 1;
        if (_commitAnchor < 0) _commitAnchor = Math.Max(CommitList.SelectedIndex, 0);
        _commitAnchor = Math.Clamp(_commitAnchor, 0, last);

        var from = _commitFocus < 0 ? _commitAnchor : Math.Clamp(_commitFocus, 0, last);
        _commitFocus = Math.Clamp(from + delta, 0, last);

        var lo = Math.Min(_commitAnchor, _commitFocus);
        var hi = Math.Max(_commitAnchor, _commitFocus);

        WithoutSelectionEvents(() =>
        {
            CommitList.SelectedItems.Clear();
            for (var i = lo; i <= hi; i++) CommitList.SelectedItems.Add(_vm.Commits[i]);
            CommitList.ScrollIntoView(_vm.Commits[_commitFocus]);
        });

        await ReloadCurrentSelectionAsync();
    }

    private async Task ReloadCurrentSelectionAsync()
    {
        var selection = CommitList.SelectedItems.Cast<CommitItem>().ToList();
        _settings.LastCommitSha = selection.Count == 1 ? selection[0].Sha : null;
        _settings.ScheduleSave();

        // False means a newer selection took over while this one was loading; touching the
        // file list now would fight with it.
        if (await _vm.SetSelectionAsync(selection)) SelectDefaultFile();
    }

    private FileNode? SelectedNode => FileList.SelectedItem as FileNode;

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectionMuted) return;

        // Landing on a folder row leaves the diff pane on the file it was showing.
        // The row is already selected and on screen, so nothing about the list needs
        // touching: rebuilding it here would drop the focused row and make every arrow
        // key feel sticky.
        if (SelectedNode?.File is { } file) SelectFile(file, ensureVisible: false);
    }

    /// <summary>Double-click checks a file off, or opens and closes a folder.</summary>
    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The check box handles its own clicks; acting again would undo them.
        if (e.OriginalSource is DependencyObject source && IsWithinCheckBox(source)) return;

        var node = SelectedNode;
        if (node is null) return;

        if (node.IsFolder) ToggleFolder(node);
        else ToggleCurrentFile();

        e.Handled = true;
    }

    private static bool IsWithinCheckBox(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is CheckBox) return true;

        return false;
    }

    private void ToggleFolder(FileNode folder)
    {
        WithoutSelectionEvents(() =>
        {
            _vm.ToggleFolder(folder);
            FileList.SelectedItem = folder;
        });

        FocusSelectedItem(FileList);
    }

    private void SetAllFoldersExpanded(bool expanded)
    {
        var keep = SelectedNode;

        WithoutSelectionEvents(() =>
        {
            _vm.SetAllFoldersExpanded(expanded);
            if (keep is not null && _vm.Files.Contains(keep)) FileList.SelectedItem = keep;
        });

        FocusSelectedItem(FileList);
        _vm.SetStatus(expanded ? "フォルダをすべて開きました" : "フォルダをすべて閉じました");
    }

    /// <summary>Left closes an open folder, otherwise jumps up to the enclosing one.</summary>
    private void CollapseOrGoToParent()
    {
        if (SelectedNode is { IsFolder: true, IsExpanded: true } open)
        {
            ToggleFolder(open);
            return;
        }

        var index = FileList.SelectedIndex;
        if (index < 0) return;

        var depth = _vm.Files[index].Depth;

        for (var i = index - 1; i >= 0; i--)
        {
            if (!_vm.Files[i].IsFolder || _vm.Files[i].Depth >= depth) continue;

            FileList.SelectedIndex = i;
            FileList.ScrollIntoView(_vm.Files[i]);
            FocusSelectedItem(FileList);
            return;
        }
    }

    /// <summary>
    /// After a commit loads, land on the file being restored from the last session, and
    /// otherwise on the first one that still needs review.
    /// </summary>
    private void SelectDefaultFile()
    {
        FileItem? target = null;

        if (_pendingFilePath is not null)
        {
            target = _vm.AllFiles.FirstOrDefault(f => f.Path == _pendingFilePath);
            _pendingFilePath = null;
        }

        target ??= _vm.FindNextUnreviewed(null) ?? _vm.AllFiles.FirstOrDefault();
        SelectFile(target);
    }

    /// <summary>
    /// The single entry point for changing the current file. Synchronous on purpose: the
    /// diff is fetched in the background, so holding an arrow key moves through the list
    /// at full speed instead of waiting on git for every row.
    /// </summary>
    /// <summary>
    /// Pass <paramref name="ensureVisible"/> false when the caller is reacting to the list's
    /// own selection: the row is already there, and rebuilding would only cost time.
    /// </summary>
    private void SelectFile(FileItem? file, bool ensureVisible = true)
    {
        _settings.LastFilePath = file?.Path;
        _settings.ScheduleSave();
        _vm.SelectFile(file);

        if (ensureVisible) RefilterFiles(file);

        // Scrolling is left to DiffShown: it belongs with the content, and touching the
        // scroll viewer here would put layout work back on the key press.
    }

    /// <summary>
    /// Points the list at <paramref name="select"/>, rebuilding it only when the filter could
    /// actually change what is visible. Rebuilding on every move would drop the focused row
    /// and send the next arrow key back to the top of the list.
    /// </summary>
    private void RefilterFiles(FileItem? select)
    {
        WithoutSelectionEvents(() =>
        {
            var node = select is null ? null : _vm.FindNode(select);

            // Rebuild only when the row is not already on screen, either because a folder
            // above it is closed or because the filter is hiding it.
            if (node is not null && !node.IsVisible)
            {
                _vm.ExpandTo(node);
                _vm.ApplyFileFilter();
            }
            else if (select is null && _vm.UnreviewedFilesOnly)
            {
                _vm.ApplyFileFilter();
            }

            FileList.SelectedItem = node is { IsVisible: true } ? node : null;
            if (node is { IsVisible: true }) FileList.ScrollIntoView(node);
        });
    }

    // ---------------------------------------------------------------- review flow

    /// <summary>Enter: mark the current file reviewed, then jump to the next thing to review.</summary>
    /// <summary>
    /// Viewed ボタン。未レビューのファイルならレビュー済みにして次の未レビューへ進む（Enter と同じ）。
    /// 既にレビュー済みなら、GitHub と同じくチェックを外すだけでその場に留まる。
    /// </summary>
    private void OnViewedClick(object sender, RoutedEventArgs e) => ToggleViewed();

    /// <summary>
    /// Viewed の切り替え。付けたときは次の未レビューのファイルへ進み、
    /// 外したときはその場に留まる（外すのは読み直したいときなので）。
    /// </summary>
    private void ToggleViewed()
    {
        var file = _vm.SelectedFile;
        if (file is null) return;

        if (file.IsReviewed)
        {
            // 一覧の選択がフォルダ行にあっても、差分に出ているファイルを対象にする
            _vm.ToggleReviewed(file);
            RefilterFiles(file);
            return;
        }

        Advance();
    }

    /// <summary>
    /// Viewed / Enter: 今のファイルをレビュー済みにして、次の未レビューのファイルへ進む。
    /// 最後まで終わってもコミットは移らない（次に何を読むかは読み手が決める）。
    /// </summary>
    private void Advance()
    {
        if (_vm.SelectedFile is null && _vm.AllFiles.Count > 0)
        {
            SelectDefaultFile();
            return;
        }

        var next = _vm.MarkAndFindNext(_vm.SelectedFile);

        if (next is not null)
        {
            SelectFile(next);
            FocusPane(FileList);
            return;
        }

        // このコミットは全部終わった。次のコミットへは勝手に移らない
        // （どのコミットを次に読むかは読み手が決めることなので）
        _vm.ApplyCommitFilter();
        _vm.SetStatus("このコミットはすべてレビュー済みです");
        ShowDiffHint("このコミットはすべてレビュー済みです", atBottom: true, icon: "✓");
        FocusPane(FileList);
    }

    private void UnmarkAndGoBack()
    {
        var current = _vm.SelectedFile;
        if (current is not null) current.IsReviewed = false;

        var prev = _vm.FindPreviousUnreviewed(current);
        if (prev is not null && !ReferenceEquals(prev, current)) SelectFile(prev);
        else SelectFile(current);
    }

    /// <summary>N and P move between files, stepping over the folder rows.</summary>
    private void MoveFile(int delta)
    {
        if (_vm.Files.Count == 0) return;

        var index = FileList.SelectedIndex;

        for (var i = index + delta; i >= 0 && i < _vm.Files.Count; i += delta)
        {
            if (_vm.Files[i].IsFolder) continue;

            FileList.SelectedIndex = i;
            FileList.ScrollIntoView(_vm.Files[i]);
            return;
        }
    }

    /// <summary>Arrow keys walk every row, folders included, as in a file explorer.</summary>
    private void MoveFileRow(int delta)
    {
        if (_vm.Files.Count == 0) return;

        var index = FileList.SelectedIndex;
        var next = Math.Clamp((index < 0 ? 0 : index) + delta, 0, _vm.Files.Count - 1);
        if (next == index) return;

        FileList.SelectedIndex = next;
        FileList.ScrollIntoView(_vm.Files[next]);
    }

    private void MoveCommit(int delta)
    {
        if (_vm.Commits.Count == 0) return;

        var index = CommitList.SelectedIndex;
        var next = Math.Clamp((index < 0 ? 0 : index) + delta, 0, _vm.Commits.Count - 1);
        SelectSingleCommit(_vm.Commits[next]);
    }

    /// <summary>
    /// Selects one commit. With <paramref name="silent"/> the selection change does not load the
    /// diff — callers that follow up with their own load use it to avoid loading twice.
    /// </summary>
    private void SelectSingleCommit(CommitItem commit, bool silent = false)
    {
        _commitAnchor = _commitFocus = _vm.Commits.IndexOf(commit);

        if (silent)
        {
            WithoutSelectionEvents(Apply);
            return;
        }
        Apply();

        void Apply()
        {
            CommitList.SelectedItems.Clear();
            CommitList.SelectedItems.Add(commit);
            CommitList.ScrollIntoView(commit);
        }
    }

    // ---------------------------------------------------------------- key handling

    private void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (HelpOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key is Key.Escape or Key.OemQuestion or Key.F1 or Key.Enter or Key.Space)
            {
                HelpOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }

        // Let text input behave normally; only a few chords reach through.
        if (e.OriginalSource is TextBox)
        {
            if (e.Key == Key.Escape)
            {
                FocusPane(CommitList);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.F)
            {
                FilterBox.Focus();
                FilterBox.SelectAll();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.E)
            {
                // 文字の表示や絞り込み欄にいるときでも、Viewed は切り替えられるようにする
                ToggleViewed();
                e.Handled = true;
            }
            return;
        }

        if (alt) return;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.O:
                    e.Handled = true;
                    _ = OpenRepositoryAsync();
                    return;
                case Key.F:
                    e.Handled = true;
                    FilterBox.Focus();
                    FilterBox.SelectAll();
                    return;
                case Key.C when shift:
                    e.Handled = true;
                    CopyCurrentPath();
                    return;
                case Key.C:
                    e.Handled = true;
                    CopySelectedDiff();
                    return;
                case Key.D:
                    e.Handled = true;
                    JumpToChange(+1);
                    return;
                case Key.U:
                    e.Handled = true;
                    JumpToChange(-1);
                    return;

                case Key.OemPlus:
                case Key.Add:
                    e.Handled = true;
                    SetZoom(Zoom.Step(Zoom.Scale, +1));
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    e.Handled = true;
                    SetZoom(Zoom.Step(Zoom.Scale, -1));
                    return;
                case Key.D0:
                case Key.NumPad0:
                    e.Handled = true;
                    SetZoom(1);
                    return;

                // Viewed のオン・オフ。どのペインにいても効く（Space と違って入力欄でも使える）。
                // 付けたときは Viewed ボタンと同じく次のファイルへ進む
                case Key.E:
                    e.Handled = true;
                    ToggleViewed();
                    return;
            }
            return;
        }

        if (shift && ReferenceEquals(_activePane, CommitList) &&
            e.Key is Key.Down or Key.Up or Key.J or Key.K)
        {
            e.Handled = true;
            _ = ExtendCommitSelectionAsync(e.Key is Key.Down or Key.J ? +1 : -1);
            return;
        }

        switch (e.Key)
        {
            case Key.F1:
            case Key.OemQuestion:
                e.Handled = true;
                ToggleHelp();
                return;

            case Key.F5:
            case Key.R:
                e.Handled = true;
                _ = ReloadAsync();
                return;

            case Key.O:
                e.Handled = true;
                _ = OpenRepositoryAsync();
                return;

            case Key.L:
                e.Handled = true;
                _ = LoadMoreAsync();
                return;

            case Key.Tab:
                e.Handled = true;
                CyclePane(shift ? -1 : +1);
                return;

            case Key.D1:
                e.Handled = true;
                FocusPane(CommitList);
                return;
            case Key.D2:
                e.Handled = true;
                FocusPane(FileList);
                return;
            case Key.D3:
                e.Handled = true;
                FocusPane(ActiveDiffList);
                return;

            case Key.Enter:
                e.Handled = true;
                // On a folder row Enter opens and closes it, as in a file explorer.
                if (SelectedNode is { IsFolder: true } folder) ToggleFolder(folder);
                else if (shift) UnmarkAndGoBack();
                else Advance();
                return;

            case Key.Left when ReferenceEquals(_activePane, FileList):
                e.Handled = true;
                CollapseOrGoToParent();
                return;
            case Key.Right when ReferenceEquals(_activePane, FileList):
                e.Handled = true;
                if (SelectedNode is { IsFolder: true, IsExpanded: false } closed) ToggleFolder(closed);
                else MoveFileRow(+1);
                return;

            case Key.Space:
            case Key.X:
                e.Handled = true;
                ToggleCurrentFile();
                return;

            case Key.N:
                e.Handled = true;
                MoveFile(+1);
                return;
            case Key.P:
                e.Handled = true;
                MoveFile(-1);
                return;

            case Key.E:
                e.Handled = true;
                SetAllFoldersExpanded(!shift);
                return;

            case Key.J:
                e.Handled = true;
                MoveActivePane(+1);
                return;
            case Key.K:
                e.Handled = true;
                MoveActivePane(-1);
                return;

            case Key.G:
                e.Handled = true;
                JumpActivePane(toEnd: shift);
                return;

            case Key.OemOpenBrackets:
                e.Handled = true;
                MoveCommit(-1);
                return;
            case Key.OemCloseBrackets:
                e.Handled = true;
                MoveCommit(+1);
                return;

            case Key.A:
                e.Handled = true;
                _vm.MarkAll(!shift);
                return;

            case Key.U:
                e.Handled = true;
                if (shift) _vm.UnreviewedCommitsOnly = !_vm.UnreviewedCommitsOnly;
                else _vm.UnreviewedFilesOnly = !_vm.UnreviewedFilesOnly;
                return;

            case Key.W:
                e.Handled = true;
                _vm.IgnoreWhitespace = !_vm.IgnoreWhitespace;
                return;

            case Key.C:
                e.Handled = true;
                CycleContext();
                return;

            case Key.T:
                e.Handled = true;
                CycleTheme();
                return;

            case Key.S:
                e.Handled = true;
                ToggleSplitView();
                return;

            case Key.V:
                e.Handled = true;
                TextModeButton.IsChecked = !TextMode;
                return;

            case Key.B:
                e.Handled = true;
                _ = OpenBranchesAsync();
                return;

            case Key.Escape:
                e.Handled = true;
                ClearTextSelection();
                FocusPane(CommitList);
                return;
        }
    }

    /// <summary>
    /// Space/X: flip the checkmark without touching the diff pane, so the reader keeps
    /// their scroll position.
    /// </summary>
    private void ToggleCurrentFile()
    {
        // On a folder row, check off everything underneath in one go.
        if (SelectedNode is { IsFolder: true } folder)
        {
            var files = folder.Files().ToList();
            var reviewed = files.All(f => f.IsReviewed);

            foreach (var f in files) f.IsReviewed = !reviewed;

            WithoutSelectionEvents(() =>
            {
                _vm.ApplyFileFilter();
                if (_vm.Files.Contains(folder)) FileList.SelectedItem = folder;
            });
            return;
        }

        var file = SelectedNode?.File ?? _vm.SelectedFile;
        if (file is null) return;

        _vm.ToggleReviewed(file);
        RefilterFiles(file);
    }

    private void CycleContext()
    {
        var index = Array.IndexOf(ContextSteps, _vm.ContextLines);
        _vm.ContextLines = ContextSteps[(index + 1) % ContextSteps.Length];
    }

    // ---------------------------------------------------------------- panes

    private void OnPaneFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not ListBox list) return;

        _activePane = list;
        UpdatePaneFrames();
    }

    /// <summary>
    /// Frames the pane holding the keyboard.
    /// </summary>
    private void UpdatePaneFrames()
    {
        var active = TryFindResource("PaneActive") as Brush ?? Brushes.Transparent;

        CommitPane.BorderBrush = ReferenceEquals(_activePane, CommitList) ? active : Brushes.Transparent;
        FilePane.BorderBrush = ReferenceEquals(_activePane, FileList) ? active : Brushes.Transparent;
        DiffPane.BorderBrush = IsDiffPane(_activePane) ? active : Brushes.Transparent;

        UpdateKeyHints();
    }

    /// <summary>
    /// 画面の下に、今いるペインでよく使うキーを出す。キーを忘れても目に入る場所に置き、
    /// 全部を見たいときは ? で一覧を開く。
    /// </summary>
    private void UpdateKeyHints()
    {
        const string help = "  ·  ? キー操作";

        KeyHints.Text = true switch
        {
            _ when ReferenceEquals(_activePane, CommitList) =>
                "↑↓ 選択  ·  Shift+↑↓ 範囲  ·  [ ] 前後  ·  B ブランチ  ·  Tab ペイン移動" + help,
            _ when ReferenceEquals(_activePane, FileList) =>
                "Enter 完了して次へ  ·  Space 完了切替  ·  N P 次前  ·  ←→ 開閉  ·  Tab ペイン移動" + help,
            _ when IsDiffPane(_activePane) =>
                "Ctrl+D Ctrl+U 次前の変更  ·  C 省略切替  ·  S 左右に並べる  ·  V テキスト" + help,
            _ => "Enter 完了して次へ  ·  Space 完了切替  ·  Tab ペイン移動" + help,
        };
    }

    private void CyclePane(int direction)
    {
        ListBox[] panes = [CommitList, FileList, ActiveDiffList];
        var index = Array.IndexOf(panes, _activePane);
        if (index < 0) index = 0;

        var next = ((index + direction) % panes.Length + panes.Length) % panes.Length;
        FocusPane(panes[next]);
    }

    private void FocusPane(ListBox list)
    {
        _activePane = list;
        UpdatePaneFrames();
        list.Focus();

        if (list.SelectedIndex < 0 && list.Items.Count > 0 && !IsDiffPane(list))
            list.SelectedIndex = 0;

        if (list.SelectedIndex >= 0)
        {
            list.ScrollIntoView(list.SelectedItem);
            list.UpdateLayout();
            (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem)?.Focus();
        }
    }

    private void MoveActivePane(int delta)
    {
        if (IsDiffPane(_activePane))
        {
            ScrollDiff(delta);
            return;
        }

        if (ReferenceEquals(_activePane, CommitList))
        {
            MoveCommit(delta);
            FocusPane(CommitList);
            return;
        }

        MoveFile(delta);
    }

    private void JumpActivePane(bool toEnd)
    {
        if (IsDiffPane(_activePane))
        {
            var sv = DiffScroller();
            if (toEnd) sv?.ScrollToEnd(); else sv?.ScrollToHome();
            return;
        }

        var list = _activePane;
        if (list.Items.Count == 0) return;

        var index = toEnd ? list.Items.Count - 1 : 0;

        if (ReferenceEquals(list, CommitList)) SelectSingleCommit(_vm.Commits[index]);
        else list.SelectedIndex = index;

        list.ScrollIntoView(list.Items[index]);
    }

    /// <summary>差分を数行ぶん送る（ページ送りは一覧自身の PageUp / PageDown に任せる）。</summary>
    private void ScrollDiff(int direction)
    {
        var sv = DiffScroller();
        if (sv is null) return;

        for (var i = 0; i < 3; i++)
        {
            if (direction > 0) sv.LineDown(); else sv.LineUp();
        }
    }

    private void ScrollDiffToTop() => DiffScroller()?.ScrollToTop();

    // ---------------------------------------------------------------- 変更箇所へ送る

    /// <summary>行の数（1 列か左右かで見ているものが違う）。</summary>
    private int DiffItemCount => _vm.SplitView ? _vm.DiffRows.Count : _vm.DiffLines.Count;

    private bool IsChangeAt(int index)
    {
        if (index < 0 || index >= DiffItemCount) return false;

        if (_vm.SplitView)
        {
            var row = _vm.DiffRows[index];
            return row.Left.Kind == DiffCellKind.Removed || row.Right.Kind == DiffCellKind.Added;
        }
        return _vm.DiffLines[index].Kind is DiffLineKind.Added or DiffLineKind.Removed;
    }

    /// <summary>変更の塊の先頭か（前の行が変更でない変更行）。</summary>
    private bool IsChangeStart(int index) => IsChangeAt(index) && !IsChangeAt(index - 1);

    /// <summary><paramref name="from"/> から <paramref name="direction"/> 方向にある変更の塊の先頭。無ければ -1。</summary>
    private int FindChangeStart(int from, int direction)
    {
        var count = DiffItemCount;

        for (var i = from + direction; i >= 0 && i < count; i += direction)
            if (IsChangeStart(i)) return i;

        return -1;
    }

    /// <summary>
    /// ファイルを開いたときの位置。最初の変更が画面の中央に来るようにして、
    /// スクロールせずにレビューを始められるようにする。
    /// </summary>
    private void ScrollDiffToFirstChange()
    {
        var index = FindChangeStart(-1, +1);
        if (index < 0)
        {
            ScrollDiffToTop();
            return;
        }

        CenterDiffItem(index);
    }

    /// <summary>Ctrl+D / Ctrl+U: 次 / 前の変更箇所へ送る。</summary>
    private void JumpToChange(int direction)
    {
        if (TextMode) return;

        var index = FindChangeStart(CenterDiffIndex(), direction);
        if (index < 0)
        {
            ShowDiffHint(
                direction > 0 ? "これより後に変更はありません" : "これより前に変更はありません",
                atBottom: direction > 0);
            return;
        }

        CenterDiffItem(index);
    }

    /// <summary>
    /// 行き止まりの合図。差分ペインの端を短く光らせ、小さな文字を添えて消す。
    /// 窓や音は出さない（読んでいる流れを切らないため）。
    /// </summary>
    private void ShowDiffHint(string text, bool atBottom, string? icon = null)
    {
        DiffHintText.Text = text;
        DiffHintIcon.Text = icon ?? (atBottom ? "↓" : "↑");

        var side = atBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        DiffHintEdge.VerticalAlignment = side;
        DiffHintPill.VerticalAlignment = side;
        DiffHintPill.Margin = atBottom ? new Thickness(0, 0, 0, 18) : new Thickness(0, 18, 0, 0);

        // 見落とさないよう、少し長めに出す（それでも 2 秒ほどで消える）
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        DiffHint.BeginAnimation(OpacityProperty, fade);

        // 進もうとした向きから、すっと滑り込ませる
        DiffHintPill.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = atBottom ? 10 : -10,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    /// <summary>
    /// 指定の行を画面の中央に寄せ、その変更の塊を枠で囲む。
    ///
    /// 送るのは一度だけにする。ScrollIntoView で端まで飛ばしてから中央へ寄せ直すと、
    /// 画面が行って戻るように見えてちらつくため。画面に出ていない行は、行の高さの平均から
    /// 行き先を見積もって送り、見積もりが外れていたときだけレイアウト後に直す。
    /// </summary>
    private void CenterDiffItem(int index, int attempt = 0)
    {
        var list = ActiveDiffList;
        if (index < 0 || index >= list.Items.Count) return;
        if (DiffScroller() is not { } scroller) return;

        MarkChangeBlock(index);

        if (CenteredOffset(list, scroller, index) is { } exact)
        {
            scroller.ScrollToVerticalOffset(exact);
            return;
        }

        var rowHeight = scroller.ExtentHeight / Math.Max(1, list.Items.Count);

        // 差分を出した直後は一覧の寸法がまだ決まっていない。測れるようになってから送る
        if (rowHeight <= 0)
        {
            if (attempt >= 3) return;

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CenterDiffItem(index, attempt + 1));
            return;
        }

        scroller.ScrollToVerticalOffset(
            Math.Max(0, index * rowHeight - (scroller.ViewportHeight - rowHeight) / 2));

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (CenteredOffset(list, scroller, index) is not { } refined) return;

            // わずかなずれで送り直すと、それ自体がちらつきになる
            if (Math.Abs(refined - scroller.VerticalOffset) < rowHeight * 2) return;

            scroller.ScrollToVerticalOffset(refined);
        });
    }

    /// <summary>その行を中央に置くためのスクロール位置。行がまだ画面に無ければ null。</summary>
    private static double? CenteredOffset(ListBox list, ScrollViewer scroller, int index)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement { IsVisible: true } row)
            return null;

        if (TopInScroller(row, scroller) is not { } top) return null;

        return Math.Max(0, scroller.VerticalOffset + top - (scroller.ViewportHeight - row.ActualHeight) / 2);
    }

    /// <summary><paramref name="start"/> から続く変更行を、今いる場所として囲む。</summary>
    private void MarkChangeBlock(int start)
    {
        ClearBlockMarkers();
        if (!IsChangeAt(start)) return;

        var end = start;
        while (IsChangeAt(end + 1)) end++;

        ActiveBlockMarker?.Show(start, end);
    }

    /// <summary>
    /// 今、画面の中央あたりに出ている行。Ctrl+D / Ctrl+U の起点に使う。
    /// 全行を見ると差分が大きいときに重いので、スクロール位置からの見積もりの周りだけ探す。
    /// </summary>
    private int CenterDiffIndex()
    {
        var list = ActiveDiffList;
        var count = DiffItemCount;
        if (DiffScroller() is not { ExtentHeight: > 0 } scroller || count == 0) return -1;

        var estimate = (int)(scroller.VerticalOffset / scroller.ExtentHeight * count);
        estimate = Math.Clamp(estimate, 0, count - 1);

        var middle = scroller.ViewportHeight / 2;
        var from = Math.Max(0, estimate - 400);
        var to = Math.Min(count - 1, estimate + 400);

        for (var i = from; i <= to; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) continue;
            if (TopInScroller(row, scroller) is not { } top) continue;

            if (top + row.ActualHeight >= middle) return i;
        }
        return estimate;
    }

    private static double? TopInScroller(FrameworkElement row, ScrollViewer scroller)
    {
        try
        {
            return row.TransformToAncestor(scroller).Transform(new Point(0, 0)).Y;
        }
        catch (InvalidOperationException)
        {
            return null;   // まだ画面に出ていない
        }
    }

    /// <summary>差分が差し替わったとき。文字の表示なら入れ直す。</summary>
    private void OnDiffShown()
    {
        ClearTextSelection();
        ClearBlockMarkers();
        UpdateOverview();

        if (TextMode) FillDiffText();
        else ScrollDiffToFirstChange();

        AnimateDiffIn();
    }

    /// <summary>
    /// 差分が入れ替わったときに、軽く浮かび上がらせる。切り替わりが唐突に見えないようにするだけで、
    /// 読み始められるまでの時間は変えない（表示はすでに出来上がっている）。
    /// </summary>
    private void AnimateDiffIn()
    {
        // 行がとても多い差分では、重ねて描く負担のほうが目立つので動かさない
        if (DiffItemCount > 20000) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        DiffContent.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.35,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(170),
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        });

        DiffContent.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 10,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(230),
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        });
    }

    private ScrollViewer? _diffScroller;
    private ScrollViewer? _splitScroller;

    /// <summary>
    /// The active diff pane's scroll viewer, found once. Walking the visual tree on every
    /// arrow key is wasted work on the path that has to feel immediate.
    /// </summary>
    private ScrollViewer? DiffScroller()
    {
        if (_vm.SplitView) return _splitScroller ??= FindScrollViewer(SplitList);
        return _diffScroller ??= FindScrollViewer(DiffList);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    // ---------------------------------------------------------------- clipboard

    private void CopySelectedDiff()
    {
        // マウスで文字を選んでいれば、そちらを優先する
        if (ActiveSelector is { HasSelection: true } selector)
        {
            TrySetClipboard(selector.SelectedText);
            return;
        }

        var selected = ActiveDiffList.SelectedItems;
        if (selected.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var item in selected)
        {
            // The side-by-side rows carry the unified text they were built from.
            sb.AppendLine(item switch
            {
                DiffLine line => line.Text,
                DiffRow row => row.CopyText,
                _ => "",
            });
        }

        TrySetClipboard(sb.ToString());
    }

    private void CopyCurrentPath()
    {
        if (_vm.SelectedFile is null) return;
        TrySetClipboard(_vm.SelectedFile.Path);
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // The clipboard is occasionally locked by another process; nothing to recover.
        }
    }

    // ---------------------------------------------------------------- help

    private void OnToggleHelp(object sender, RoutedEventArgs e) => ToggleHelp();

    private void ToggleHelp()
    {
        var show = HelpOverlay.Visibility != Visibility.Visible;
        HelpOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // 枠や選択はレイヤーに描いているので、そのままだとヘルプの上に重なってしまう
        _unifiedBlockMarker?.SetVisible(!show);
        _splitBlockMarker?.SetVisible(!show);
        _unifiedSelector?.SetVisible(!show);
        _splitSelector?.SetVisible(!show);
    }

    private void BuildHelp()
    {
        (string Key, string Text)[] left =
        [
            ("O / Ctrl+O", "リポジトリのフォルダを開く"),
            ("R / F5", "再読込"),
            ("L", "コミットをさらに読む"),
            ("Ctrl+F", "絞り込み入力へ"),
            ("Tab / Shift+Tab", "ペインを移動"),
            ("1 / 2 / 3", "コミット / ファイル / 差分 へ"),
            ("J / K", "選択を下 / 上へ（差分ではスクロール）"),
            ("↑ / ↓ / PageUp / PageDown", "通常のリスト操作"),
            ("G / Shift+G", "先頭 / 末尾へ"),
            ("[ / ]", "前 / 次のコミット"),
            ("Shift+↑ / Shift+↓", "コミットを範囲選択（まとめて差分表示）"),
        ];

        (string Key, string Text)[] right =
        [
            ("Enter", "完了にして次の未レビューのファイルへ（Viewed ボタンと同じ）"),
            ("Shift+Enter", "未完了に戻して前へ"),
            ("Space / X", "レビュー完了を切り替え（その場に留まる）"),
            ("Ctrl+E", "レビュー完了を切り替え（付けたら次のファイルへ）"),
            ("N / P", "次 / 前のファイル（フォルダを飛ばす）"),
            ("← / →", "フォルダを閉じる / 開く"),
            ("E / Shift+E", "フォルダをすべて開く / 閉じる"),
            ("ダブルクリック", "完了を切り替え（フォルダは開閉）"),
            ("A / Shift+A", "全ファイルを完了 / 未完了に"),
            ("U", "未レビューのファイルのみ表示"),
            ("Shift+U", "未レビューのコミットのみ表示"),
            ("W", "空白の変更を無視"),
            ("B", "ブランチを探して表示を切り替え"),
            ("S", "左右に並べる / 1 列（split / unified）"),
            ("ドラッグ", "差分の文字を選ぶ（Ctrl+C でコピー、Esc で解除）"),
            ("ダブルクリック", "語を選ぶ。続けて押すと 行 → @@ の塊 → 全体 と広がる"),
            ("V", "差分を色なしの文字として出す表示に切り替え"),
            ("C", "変更のない行の省略を切り替え（全体 / 3 / 8 / 25 行）"),
            ("T", "テーマ切り替え（OS / ライト / ダーク）"),
            ("Ctrl+ホイール", "差分の文字サイズを拡大 / 縮小"),
            ("Ctrl++ / Ctrl+- / Ctrl+0", "同上 / 等倍に戻す"),
            ("Ctrl+D / Ctrl+U", "次 / 前の変更箇所へ送る（中央に寄せる）"),
            ("Ctrl+C", "選択した差分行をコピー"),
            ("Ctrl+Shift+C", "ファイルパスをコピー"),
            ("? / F1", "このヘルプ"),
        ];

        Fill(HelpLeft, left);
        Fill(HelpRight, right);

        static void Fill(StackPanel panel, (string Key, string Text)[] rows)
        {
            foreach (var (key, text) in rows)
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // A keycap-style chip, so the shortcut reads apart from its description.
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(0, 0, 12, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock
                    {
                        Text = key,
                        FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                        FontSize = 11,
                    },
                };
                chip.SetResourceReference(BackgroundProperty, "AccentQuiet");
                ((TextBlock)chip.Child).SetResourceReference(ForegroundProperty, "Accent");

                // 説明は折り返す。窓が狭いときに右端で切れて読めなくなるのを防ぐ
                var body = new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                Grid.SetColumn(chip, 0);
                Grid.SetColumn(body, 1);
                grid.Children.Add(chip);
                grid.Children.Add(body);
                panel.Children.Add(grid);
            }
        }
    }
}
