using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using GitReview.Core;

namespace GitReview;

/// <summary>
/// Holds every piece of state the window binds to, and owns the git + persistence calls.
/// The window itself only translates input into the methods below.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    /// <summary>Captured here because the view model is built on the UI thread.</summary>
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private GitClient? _git;
    private ReviewStore? _store;

    private readonly List<CommitItem> _allCommits = [];
    private readonly List<FileItem> _allFiles = [];

    private CancellationTokenSource? _filesCts;
    private CancellationTokenSource? _diffCts;
    private bool _suppressFileEvents;
    private int _savePending;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _revision = settings.Revision;
        _maxCount = settings.MaxCount;
        _ignoreWhitespace = settings.IgnoreWhitespace;
        _contextLines = settings.ContextLines;
        _unreviewedFilesOnly = settings.UnreviewedFilesOnly;
        _unreviewedCommitsOnly = settings.UnreviewedCommitsOnly;
        _splitView = settings.SplitView;
    }

    // ---------------------------------------------------------------- bindings

    public ObservableCollection<CommitItem> Commits { get; } = [];

    /// <summary>The changed-file tree, flattened to the rows currently on screen.</summary>
    public ObservableCollection<FileNode> Files { get; } = [];

    private readonly List<FileNode> _fileRoots = [];
    private readonly Dictionary<FileItem, FileNode> _nodesByFile = [];
    // Replaced wholesale rather than filled item by item: a large file's diff runs to six
    // figures of lines, and one CollectionChanged event each would block the UI for a
    // minute. The lists are immutable once published, so plain lists are enough.
    private IReadOnlyList<DiffLine> _diffLines = [];
    public IReadOnlyList<DiffLine> DiffLines
    {
        get => _diffLines;
        private set => Set(ref _diffLines, value);
    }

    private IReadOnlyList<DiffRow> _diffRows = [];
    public IReadOnlyList<DiffRow> DiffRows
    {
        get => _diffRows;
        private set => Set(ref _diffRows, value);
    }

    private string? _repositoryRoot;
    public string? RepositoryRoot
    {
        get => _repositoryRoot;
        private set { if (Set(ref _repositoryRoot, value)) OnRepositoryChanged(); }
    }

    private string _branch = "";
    public string Branch
    {
        get => _branch;
        private set { if (Set(ref _branch, value)) OnRepositoryChanged(); }
    }

    private void OnRepositoryChanged()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(HeadingText));
        OnPropertyChanged(nameof(HeadingSubText));
    }

    /// <summary>Repository folder name, shown as the window's own heading.</summary>
    public string HeadingText => RepositoryRoot is null
        ? "GitReview"
        : Path.GetFileName(RepositoryRoot.TrimEnd('\\', '/'));

    public string HeadingSubText => RepositoryRoot is null
        ? "リポジトリ未選択"
        : $"{(Branch.Length > 0 ? Branch + "  ·  " : "")}{RepositoryRoot}";

    public string TitleText
    {
        get
        {
            var where = RepositoryRoot is null
                ? "リポジトリ未選択"
                : $"{RepositoryRoot}{(Branch.Length > 0 ? $"  [{Branch}]" : "")}";

            // The build stamp makes it obvious whether a shared update has landed here.
            var build = Stage.BuildStamp;
            return build.Length > 0
                ? $"GitReview — {where}  ·  build {build}"
                : $"GitReview — {where}";
        }
    }

    private string _revision = "HEAD";
    public string Revision
    {
        get => _revision;
        set { if (Set(ref _revision, value)) { _settings.Revision = value; _settings.ScheduleSave(); } }
    }

    // ---------------------------------------------------------------- ブランチ

    /// <summary>絞り込み後のブランチ。一覧を出すときに読み直す。</summary>
    public ObservableCollection<BranchItem> Branches { get; } = [];

    private readonly List<BranchItem> _allBranches = [];

    private string _branchFilter = "";
    public string BranchFilter
    {
        get => _branchFilter;
        set { if (Set(ref _branchFilter, value)) ApplyBranchFilter(); }
    }

    /// <summary>sha やブランチ名を完全な sha にする。指すものが無ければ null。</summary>
    public Task<string?> ResolveCommitAsync(string rev)
        => _git is null ? Task.FromResult<string?>(null) : _git.ResolveCommitAsync(rev);

    public async Task LoadBranchesAsync()
    {
        if (_git is null) return;

        try
        {
            var branches = await _git.GetBranchesAsync();

            _allBranches.Clear();
            _allBranches.AddRange(branches);
            ApplyBranchFilter();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    private void ApplyBranchFilter()
    {
        var needle = BranchFilter.Trim();

        Branches.Clear();
        foreach (var b in _allBranches)
        {
            if (needle.Length > 0 && !b.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            Branches.Add(b);
        }
    }

    private int _maxCount = 200;
    public int MaxCount
    {
        get => _maxCount;
        set { if (Set(ref _maxCount, value)) { _settings.MaxCount = value; _settings.ScheduleSave(); } }
    }

    private string _commitFilter = "";
    public string CommitFilter
    {
        get => _commitFilter;
        set { if (Set(ref _commitFilter, value)) ApplyCommitFilter(); }
    }

    private bool _unreviewedCommitsOnly;
    public bool UnreviewedCommitsOnly
    {
        get => _unreviewedCommitsOnly;
        set
        {
            if (!Set(ref _unreviewedCommitsOnly, value)) return;
            _settings.UnreviewedCommitsOnly = value;
            _settings.ScheduleSave();
            ApplyCommitFilter();
        }
    }

    private bool _unreviewedFilesOnly;
    public bool UnreviewedFilesOnly
    {
        get => _unreviewedFilesOnly;
        set
        {
            if (!Set(ref _unreviewedFilesOnly, value)) return;
            _settings.UnreviewedFilesOnly = value;
            _settings.ScheduleSave();
            ApplyFileFilter();
        }
    }

    private bool _ignoreWhitespace;
    public bool IgnoreWhitespace
    {
        get => _ignoreWhitespace;
        set
        {
            if (!Set(ref _ignoreWhitespace, value)) return;
            _settings.IgnoreWhitespace = value;
            _settings.ScheduleSave();
            OnPropertyChanged(nameof(DiffOptionText));
            ReloadDiff();
        }
    }

    private int _contextLines = -1;
    /// <summary>Unified-diff context; -1 renders the whole file.</summary>
    public int ContextLines
    {
        get => _contextLines;
        set
        {
            if (!Set(ref _contextLines, value)) return;

            _settings.ContextLines = value;

            // 省略表示に戻したときに同じ行数で開けるよう、最後に使った行数を覚えておく
            if (value >= 0) _settings.AbbrevContextLines = value;

            _settings.ScheduleSave();
            OnPropertyChanged(nameof(DiffOptionText));
            OnPropertyChanged(nameof(AbbreviateContext));
            ReloadDiff();
        }
    }

    /// <summary>
    /// 変更のない行を省略するか。既定は省略せず、ファイル全体を出す
    /// （前後だけ見せられても、その周りを読みたいことが多いため）。
    /// </summary>
    public bool AbbreviateContext
    {
        get => ContextLines >= 0;
        set => ContextLines = value ? Math.Max(0, _settings.AbbrevContextLines) : -1;
    }

    private bool _splitView;
    /// <summary>True shows the old and new sides next to each other instead of one column.</summary>
    public bool SplitView
    {
        get => _splitView;
        set
        {
            if (!Set(ref _splitView, value)) return;
            _settings.SplitView = value;
            _settings.ScheduleSave();
            OnPropertyChanged(nameof(DiffOptionText));
        }
    }

    public string DiffOptionText
        => $"{(SplitView ? "split" : "unified")} / context " +
           $"{(ContextLines < 0 ? "全体" : ContextLines.ToString())}" +
           $"{(IgnoreWhitespace ? " / 空白無視" : "")}";

    public DiffTarget? Target { get; private set; }

    private FileItem? _selectedFile;
    public FileItem? SelectedFile
    {
        get => _selectedFile;
        private set
        {
            if (!Set(ref _selectedFile, value)) return;
            OnPropertyChanged(nameof(DiffHeader));
            OnPropertyChanged(nameof(DiffSubHeader));
        }
    }

    public string DiffHeader => SelectedFile?.DisplayPath ?? "(ファイル未選択)";

    public string DiffSubHeader
    {
        get
        {
            if (SelectedFile is null) return Target?.Title ?? "";
            var f = SelectedFile;
            return $"{f.StatusLetter}  {f.StatText}   {Target?.Title}";
        }
    }

    private string _status = "フォルダを開いてください (O)";
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Lets the window report staging and update progress in the status bar.</summary>
    public void SetStatus(string message) => Status = message;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string ProgressText
    {
        get
        {
            if (_allFiles.Count == 0) return "";
            var done = _allFiles.Count(f => f.IsReviewed);
            return $"レビュー済み {done}/{_allFiles.Count}";
        }
    }

    public double ProgressRatio => _allFiles.Count == 0
        ? 0
        : (double)_allFiles.Count(f => f.IsReviewed) / _allFiles.Count;

    public IReadOnlyList<FileItem> AllFiles => _allFiles;
    public IReadOnlyList<CommitItem> AllCommits => _allCommits;

    /// <summary>Raised when something goes wrong in a background load.</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Raised around a rebuild of the visible commit list. Rebuilding clears the collection,
    /// which the ListBox reports as an empty selection, so the window mutes selection handling
    /// between these two events and restores the selection itself.
    /// </summary>
    public event Action? CommitsRefreshing;
    public event Action? CommitsRefreshed;

    /// <summary>Same pairing for the file list.</summary>
    public event Action? FilesRefreshing;
    public event Action? FilesRefreshed;

    // ---------------------------------------------------------------- repository

    /// <summary>
    /// Opens a repository. Startup tries several candidates in turn and most are expected
    /// to miss, so it passes <paramref name="reportErrors"/> false to stay quiet.
    /// </summary>
    public async Task<bool> OpenRepositoryAsync(string path, bool reportErrors = true)
    {
        try
        {
            IsBusy = true;
            Status = "リポジトリを開いています…";

            var git = await GitClient.OpenAsync(path);
            _git = git;
            _store = ReviewStore.Load(git.Root);

            RepositoryRoot = git.Root;
            Branch = await git.GetCurrentBranchAsync();
            _settings.LastRepository = git.Root;
            _settings.ScheduleSave();

            await ReloadCommitsAsync();
            return true;
        }
        catch (Exception ex)
        {
            if (reportErrors)
            {
                ErrorOccurred?.Invoke(ex.Message);
                Status = "リポジトリを開けませんでした";
            }
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ReloadCommitsAsync()
    {
        if (_git is null || _store is null) return;

        var keepShas = Commits.Count > 0 ? SelectedShasSnapshot() : [];

        try
        {
            IsBusy = true;
            Status = "コミット履歴を読み込み中…";

            // checkout でブランチが変わっていることがあるので、ここでも読み直す
            Branch = await _git.GetCurrentBranchAsync();

            var (commits, note) = await ReadLogAsync();

            _allCommits.Clear();
            _allCommits.AddRange(commits);

            // Seed progress from whatever we already recorded for these commits.
            foreach (var c in _allCommits)
            {
                var state = _store.Find(c.Sha);
                c.TotalFiles = state?.Total ?? -1;
                c.ReviewedFiles = state?.ReviewedCount ?? 0;
            }

            ApplyCommitFilter();
            RestoreSelection(keepShas);

            _revisionNote = note;
            Status = note
                ?? (commits.Count == 0
                    ? "コミットがありません"
                    : $"{commits.Count} 件のコミットを読み込みました");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            Status = "コミット履歴の読み込みに失敗しました";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 指定された rev で履歴を読む。rev は設定に残るので、別のリポジトリを開いたときに
    /// そこには無いブランチ名が残っていることがある。その場合はエラーの窓を出さず、
    /// HEAD に戻して読み直し、何が起きたかをステータスで知らせる。
    /// </summary>
    private async Task<(List<CommitItem> Commits, string? Note)> ReadLogAsync()
    {
        try
        {
            return (await _git!.GetLogAsync(Revision, MaxCount), null);
        }
        catch (GitException ex) when (IsUnknownRevision(ex.Message) && Revision != DefaultRevision)
        {
            var missing = Revision;
            Revision = DefaultRevision;

            return (await _git!.GetLogAsync(Revision, MaxCount),
                $"{missing} はこのリポジトリにないため、{DefaultRevision} を表示しています");
        }
    }

    private const string DefaultRevision = "HEAD";

    private string? _revisionNote;

    /// <summary>
    /// rev を HEAD に戻したときの知らせ。読み取ると消える。
    /// 読み込み直後のステータスは後続の処理で上書きされるので、落ち着いてから出し直すために使う。
    /// </summary>
    public string? TakeRevisionNote()
    {
        var note = _revisionNote;
        _revisionNote = null;
        return note;
    }

    private static bool IsUnknownRevision(string message)
        => message.Contains("bad revision", StringComparison.OrdinalIgnoreCase)
           || message.Contains("unknown revision", StringComparison.OrdinalIgnoreCase)
           || message.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase);

    private List<string> _pendingSelection = [];
    private List<string> SelectedShasSnapshot() => _pendingSelection;

    /// <summary>Shas the window should re-select after a reload; set by the window.</summary>
    public void RememberSelection(IEnumerable<CommitItem> selection)
        => _pendingSelection = selection.Select(c => c.Sha).ToList();

    private void RestoreSelection(List<string> shas) => _pendingSelection = shas;

    /// <summary>Commits to re-select after a reload, resolved against the current list.</summary>
    public List<CommitItem> ResolvePendingSelection()
        => _pendingSelection
            .Select(sha => Commits.FirstOrDefault(c => c.Sha == sha))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

    public void ApplyCommitFilter()
    {
        CommitsRefreshing?.Invoke();
        try
        {
            var needle = CommitFilter.Trim();
            Commits.Clear();

            foreach (var c in _allCommits)
            {
                if (UnreviewedCommitsOnly && c.IsFullyReviewed) continue;

                if (needle.Length > 0 &&
                    !(c.Subject.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                      c.Author.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                      c.Sha.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Commits.Add(c);
            }
        }
        finally
        {
            CommitsRefreshed?.Invoke();
        }
    }

    // ---------------------------------------------------------------- diff target

    /// <summary>Builds a diff target from the selected commits and loads its file list.</summary>
    /// <summary>
    /// Points at the commits to diff and loads their file list. False when a newer selection
    /// superseded this one, so the caller knows not to act on a stale result.
    /// </summary>
    public async Task<bool> SetSelectionAsync(IReadOnlyList<CommitItem> selection)
    {
        RememberSelection(selection);

        if (_git is null || selection.Count == 0)
        {
            Target = null;
            ClearFiles();
            return false;
        }

        var indices = selection
            .Select(c => _allCommits.IndexOf(c))
            .Where(i => i >= 0)
            .Order()
            .ToList();

        if (indices.Count == 0)
        {
            Target = null;
            ClearFiles();
            return false;
        }

        // The log is newest-first, so the smallest index is the newest commit.
        var newest = _allCommits[indices[0]];
        var oldest = _allCommits[indices[^1]];

        var baseSha = oldest.Parents.Length > 0 ? oldest.Parents[0] : GitClient.EmptyTree;

        string title;
        if (indices.Count == 1)
        {
            title = $"{newest.ShortSha}  {newest.Subject}";
            if (newest.IsMerge) title += "   (第1親との差分)";
        }
        else
        {
            var contiguous = indices[^1] - indices[0] + 1 == indices.Count;
            title = $"{indices.Count} commits  {oldest.ShortSha}..{newest.ShortSha}";
            if (!contiguous) title += "   (選択が連続していないため範囲全体の差分)";
        }

        Target = new DiffTarget
        {
            BaseSha = baseSha,
            HeadSha = newest.Sha,
            CommitCount = indices.Count,
            Title = title,
        };

        return await LoadFilesAsync();
    }

    /// <summary>
    /// Same quiet period as the diff: arrowing down the commit list should not run git for
    /// every commit it passes through.
    /// </summary>
    private static readonly TimeSpan FileListLoadDelay = TimeSpan.FromMilliseconds(90);

    /// <summary>
    /// アメンドすると sha が変わって別のコミット扱いになり、レビューマークが全部消えてしまう。
    /// 同じ親から作られた前の版の記録が残っていれば、中身が変わっていないファイルのマークだけを
    /// 引き継ぐ。親が同じなので、中身が同じファイルは差分も同じになる。
    /// 変わったファイルは未レビューのまま残す（読み直してほしいのはそこだから）。
    /// </summary>
    private async Task<int> CarryOverReviewAsync(List<FileItem> files, CancellationToken ct)
    {
        if (_git is null || _store is null || Target is null) return 0;
        if (!Target.IsSingleCommit) return 0;

        // 一度でも開いたコミットには触らない（引き継ぎは初回だけ）
        if (_store.Find(Target.Key) is not null) return 0;

        if (_store.FindPredecessor(Target.BaseSha, Target.Key) is not { } previous) return 0;

        var changed = await _git.GetChangedPathsAsync(previous.Key, Target.HeadSha, ct);
        if (changed is null) return 0;

        var reviewed = previous.Reviewed.ToHashSet(StringComparer.Ordinal);
        var carried = 0;

        foreach (var f in files)
        {
            if (changed.Contains(f.Path) || !reviewed.Contains(f.Path)) continue;

            _store.SetReviewed(Target.Key, f.Path, true);
            carried++;
        }

        return carried;
    }

    private async Task<bool> LoadFilesAsync()
    {
        if (_git is null || _store is null || Target is null) return false;

        _filesCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filesCts = cts;

        try
        {
            IsBusy = true;
            Status = "変更ファイルを取得中…";

            await Task.Delay(FileListLoadDelay, cts.Token);

            var files = await _git.GetChangedFilesAsync(Target, cts.Token);
            if (!ReferenceEquals(_filesCts, cts)) return false;

            var carried = await CarryOverReviewAsync(files, cts.Token);
            if (!ReferenceEquals(_filesCts, cts)) return false;

            var key = Target.Key;
            _suppressFileEvents = true;
            try
            {
                DetachFileHandlers();
                _allFiles.Clear();

                foreach (var f in files)
                {
                    f.IsReviewed = _store.IsReviewed(key, f.Path);
                    f.PropertyChanged += OnFilePropertyChanged;
                    _allFiles.Add(f);
                }
            }
            finally
            {
                _suppressFileEvents = false;
            }

            _store.SetTotal(key, _allFiles.Count);
            _store.SetBase(key, Target.BaseSha);
            SyncCommitProgress();
            ScheduleSave();

            BuildFileTree();
            ApplyFileFilter();
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ProgressRatio));
            OnPropertyChanged(nameof(DiffSubHeader));

            Status = carried == 0
                ? $"{_allFiles.Count} ファイルの変更"
                : $"{_allFiles.Count} ファイルの変更  ·  変更のない {carried} ファイルは前の版のレビュー済みを引き継ぎました";
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;   // superseded by a newer selection
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            Status = "変更ファイルの取得に失敗しました";
            return false;
        }
        finally
        {
            if (ReferenceEquals(_filesCts, cts)) IsBusy = false;
        }
    }

    private void ClearFiles()
    {
        DetachFileHandlers();
        _allFiles.Clear();
        _fileRoots.Clear();
        _nodesByFile.Clear();
        Files.Clear();
        DiffLines = [];
        DiffRows = [];
        SelectedFile = null;
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressRatio));
    }

    private void DetachFileHandlers()
    {
        foreach (var f in _allFiles) f.PropertyChanged -= OnFilePropertyChanged;
    }

    /// <summary>
    /// Rebuilds the folder tree from the current file list. Folders that hold a single
    /// folder are merged into one row, the way VS Code compacts them, so a deep source
    /// layout does not become a staircase.
    /// </summary>
    private void BuildFileTree()
    {
        _fileRoots.Clear();
        _nodesByFile.Clear();

        // Insert in git's order, so files keep their ordering within each folder.
        var root = new FileNode { Name = "" };
        foreach (var file in _allFiles) Insert(root, file);

        Compact(root);
        SetDepth(root.Children, 0);

        _fileRoots.AddRange(root.Children);

        foreach (var file in _allFiles)
        {
            var node = Find(_fileRoots, file);
            if (node is not null) _nodesByFile[file] = node;
        }

        RefreshFolderProgress();

        static void Insert(FileNode root, FileItem file)
        {
            var segments = file.Path.Split('/');
            var parent = root;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                var name = segments[i];
                var folder = parent.Children.FirstOrDefault(c => c.IsFolder && c.Name == name);

                if (folder is null)
                {
                    folder = new FileNode { Name = name };
                    parent.Children.Add(folder);
                }
                parent = folder;
            }

            parent.Children.Add(new FileNode { Name = segments[^1], File = file });
        }

        // A folder holding nothing but one folder becomes a single "a/b" row.
        static void Compact(FileNode node)
        {
            for (var i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                if (!child.IsFolder) continue;

                while (child.Children.Count == 1 && child.Children[0].IsFolder)
                {
                    var only = child.Children[0];
                    var merged = new FileNode { Name = $"{child.Name}/{only.Name}" };
                    merged.Children.AddRange(only.Children);
                    node.Children[i] = child = merged;
                }

                Compact(child);
            }
        }

        static void SetDepth(List<FileNode> nodes, int depth, FileNode? parent = null)
        {
            foreach (var node in nodes)
            {
                node.Depth = depth;
                node.Parent = parent;
                SetDepth(node.Children, depth + 1, node);
            }
        }

        static FileNode? Find(List<FileNode> nodes, FileItem file)
        {
            foreach (var node in nodes)
            {
                if (ReferenceEquals(node.File, file)) return node;

                var found = Find(node.Children, file);
                if (found is not null) return found;
            }
            return null;
        }
    }

    public void ApplyFileFilter()
    {
        FilesRefreshing?.Invoke();
        try
        {
            var keep = SelectedFile;

            foreach (var node in Files) node.IsVisible = false;
            Files.Clear();

            foreach (var node in _fileRoots) AddVisible(node, keep);
        }
        finally
        {
            FilesRefreshed?.Invoke();
        }
    }

    private void AddVisible(FileNode node, FileItem? keep)
    {
        if (node.IsFolder)
        {
            // A folder whose files are all filtered away has nothing to show.
            if (!node.Files().Any(f => IsVisible(f, keep))) return;

            node.IsVisible = true;
            Files.Add(node);
            if (!node.IsExpanded) return;

            foreach (var child in node.Children) AddVisible(child, keep);
            return;
        }

        if (!IsVisible(node.File!, keep)) return;

        node.IsVisible = true;
        Files.Add(node);
    }

    // Keep the current file visible even when reviewed, so the diff pane does not empty
    // out from under the reader.
    private bool IsVisible(FileItem file, FileItem? keep)
        => !UnreviewedFilesOnly || !file.IsReviewed || ReferenceEquals(file, keep);

    public FileNode? FindNode(FileItem file)
        => _nodesByFile.GetValueOrDefault(file);

    /// <summary>Opens every folder above <paramref name="node"/> so its row can be shown.</summary>
    public void ExpandTo(FileNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
    }

    /// <summary>Opens or closes a folder row and redraws the list.</summary>
    public void ToggleFolder(FileNode folder)
    {
        if (!folder.IsFolder) return;

        folder.IsExpanded = !folder.IsExpanded;
        ApplyFileFilter();
    }

    public void SetAllFoldersExpanded(bool expanded)
    {
        foreach (var node in _fileRoots) Walk(node);
        ApplyFileFilter();

        void Walk(FileNode node)
        {
            if (!node.IsFolder) return;
            node.IsExpanded = expanded;
            foreach (var child in node.Children) Walk(child);
        }
    }

    private void RefreshFolderProgress()
    {
        foreach (var node in _fileRoots) node.RefreshProgress();
    }

    // ---------------------------------------------------------------- diff body

    /// <summary>
    /// Quiet period before the diff is fetched. Holding the arrow key down the file list
    /// would otherwise spawn a git process per row, and every one of them would land out
    /// of order.
    /// </summary>
    private static readonly TimeSpan DiffLoadDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Selects a file and schedules its diff. Returns at once so the list keeps up with
    /// the keyboard; the diff arrives on its own.
    /// </summary>
    public void SelectFile(FileItem? file)
    {
        SelectedFile = file;
        ScheduleDiffLoad();
    }

    /// <summary>Re-fetches the current diff, after the context or whitespace option changed.</summary>
    public void ReloadDiff() => ScheduleDiffLoad();

    private DiffOptions CurrentDiffOptions
        => new() { Context = ContextLines, IgnoreWhitespace = IgnoreWhitespace };

    // Shared empties, so clearing an already-empty pane raises nothing and rebinds nothing.
    private static readonly IReadOnlyList<DiffLine> NoLines = [];
    private static readonly IReadOnlyList<DiffRow> NoRows = [];

    private DispatcherOperation? _diffRender;

    /// <summary>
    /// Raised when the diff pane has been filled in, so the window can put it back to the
    /// top. Deferred along with the rendering itself.
    /// </summary>
    public event Action? DiffShown;

    private void ScheduleDiffLoad()
    {
        _diffCts?.Cancel();

        // Everything past this point is kept out of the selection path. Moving through the
        // file list must not depend on how large the diff being left behind is.
        DiffLines = NoLines;
        DiffRows = NoRows;

        if (_git is null || Target is null || SelectedFile is null)
        {
            _diffRender?.Abort();
            return;
        }

        var options = CurrentDiffOptions;

        // Already read once: still not rendered here. Handing a six-figure line list to the
        // pane is real layout work, and it would land on the key press that caused it.
        if (_git.TryGetCachedDiff(Target, SelectedFile, options, out var cached))
        {
            ShowDiff(SelectedFile, cached);
            return;
        }

        var cts = new CancellationTokenSource();
        _diffCts = cts;
        _ = LoadDiffAsync(_git, Target, SelectedFile, options, cts);
    }

    /// <summary>
    /// Fills the diff pane at background priority. Key presses are input priority, which
    /// the dispatcher serves first, so holding an arrow key keeps the file list moving and
    /// the diff is drawn once the reader stops.
    /// </summary>
    private void ShowDiff(FileItem owner, ParsedDiff parsed)
    {
        _diffRender?.Abort();

        _diffRender = _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!ReferenceEquals(SelectedFile, owner)) return;   // the reader moved on

            DiffLines = parsed.Lines;
            DiffRows = parsed.Rows;

            DiffShown?.Invoke();
            Prefetch();
        });
    }

    private async Task LoadDiffAsync(
        GitClient git, DiffTarget target, FileItem file, DiffOptions options, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(DiffLoadDelay, cts.Token);

            // Reading and parsing are both off the UI thread: a whole-file diff is seconds
            // of work.
            var parsed = await Task.Run(() => git.GetFileDiffAsync(target, file, options, cts.Token), cts.Token);
            if (!ReferenceEquals(_diffCts, cts)) return;

            ShowDiff(file, parsed);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    /// <summary>Waited out before prefetching, so holding an arrow key starts no git at all.</summary>
    private static readonly TimeSpan PrefetchDelay = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? _prefetchCts;

    /// <summary>
    /// Reads the neighbouring files' diffs into the cache while the reader is busy with
    /// this one, so moving on with Enter or an arrow key shows them instantly.
    /// </summary>
    private void Prefetch()
    {
        _prefetchCts?.Cancel();

        if (_git is null || Target is null || SelectedFile is null) return;

        var index = _allFiles.IndexOf(SelectedFile);
        if (index < 0) return;

        var git = _git;
        var target = Target;
        var options = CurrentDiffOptions;

        var ahead = new[] { index + 1, index - 1 }
            .Where(i => i >= 0 && i < _allFiles.Count)
            .Select(i => _allFiles[i])
            .Where(f => !git.TryGetCachedDiff(target, f, options, out _))
            .ToArray();

        if (ahead.Length == 0) return;

        var cts = new CancellationTokenSource();
        _prefetchCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PrefetchDelay, cts.Token);

                foreach (var file in ahead)
                    await git.GetFileDiffAsync(target, file, options, cts.Token);
            }
            catch
            {
                // The reader moved on, or git is unhappy. Either way there is nothing to do.
            }
        });
    }

    // ---------------------------------------------------------------- review state

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressFileEvents || e.PropertyName != nameof(FileItem.IsReviewed)) return;
        if (sender is not FileItem file || _store is null || Target is null) return;

        _store.SetReviewed(Target.Key, file.Path, file.IsReviewed);
        SyncCommitProgress();
        RefreshFolderProgress();
        ScheduleSave();

        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressRatio));
    }

    public void ToggleReviewed(FileItem? file)
    {
        if (file is null) return;
        file.IsReviewed = !file.IsReviewed;
    }

    public void MarkAll(bool reviewed)
    {
        if (_store is null || Target is null) return;

        foreach (var f in _allFiles) f.IsReviewed = reviewed;

        ApplyFileFilter();
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressRatio));
    }

    /// <summary>Marks the current file reviewed, then returns the next file to move to.</summary>
    public FileItem? MarkAndFindNext(FileItem? current)
    {
        if (current is not null && !current.IsReviewed)
            current.IsReviewed = true;

        return FindNextUnreviewed(current);
    }

    public FileItem? FindNextUnreviewed(FileItem? from)
    {
        if (_allFiles.Count == 0) return null;

        var start = from is null ? -1 : _allFiles.IndexOf(from);

        for (var i = start + 1; i < _allFiles.Count; i++)
            if (!_allFiles[i].IsReviewed) return _allFiles[i];

        // Wrap around so a file skipped earlier is not lost.
        for (var i = 0; i <= start && i < _allFiles.Count; i++)
            if (!_allFiles[i].IsReviewed) return _allFiles[i];

        return null;
    }

    public FileItem? FindPreviousUnreviewed(FileItem? from)
    {
        if (_allFiles.Count == 0) return null;

        var start = from is null ? _allFiles.Count : _allFiles.IndexOf(from);

        for (var i = start - 1; i >= 0; i--)
            if (!_allFiles[i].IsReviewed) return _allFiles[i];

        for (var i = _allFiles.Count - 1; i >= start && i >= 0; i--)
            if (!_allFiles[i].IsReviewed) return _allFiles[i];

        return null;
    }

    private void SyncCommitProgress()
    {
        if (Target is null) return;

        // Progress is displayed per commit, so only single-commit targets update a row.
        if (!Target.IsSingleCommit) return;

        var commit = _allCommits.FirstOrDefault(c => c.Sha == Target.HeadSha);
        if (commit is null) return;

        commit.TotalFiles = _allFiles.Count;
        commit.ReviewedFiles = _allFiles.Count(f => f.IsReviewed);

        if (UnreviewedCommitsOnly && commit.IsFullyReviewed)
        {
            // Leave the row in place while it is the active selection; the list is refiltered
            // the next time the user moves.
        }
    }

    // ---------------------------------------------------------------- persistence

    private void ScheduleSave()
    {
        if (_store is null) return;

        // Interlocked, not a plain flag: the reset below runs on a thread pool thread.
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400);
                Interlocked.Exchange(ref _savePending, 0);
                await _store.SaveAsync();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _savePending, 0);
                ErrorOccurred?.Invoke($"レビュー状態の保存に失敗しました。\n\n{ex.Message}");
            }
        });
    }

    public async Task FlushAsync()
    {
        if (_store is null) return;
        try
        {
            await _store.SaveAsync();
        }
        catch
        {
            // Closing anyway.
        }
    }
}
