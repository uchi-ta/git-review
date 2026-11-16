using System.Windows.Media;


namespace GitReview.Core;

public enum ChangeKind { Added, Modified, Deleted, Renamed, Copied, TypeChanged, Unmerged, Unknown }

public enum DiffLineKind { Context, Added, Removed, Hunk, Meta, Note }

/// <summary>ブランチ一覧の 1 行。表示を切り替えるためだけに使う。</summary>
public sealed class BranchItem
{
    public required string Name { get; init; }
    public required string ShortSha { get; init; }
    public required string DateText { get; init; }
    public bool IsRemote { get; init; }

    /// <summary>今チェックアウトしているブランチ。</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>One commit row in the history list. Review progress is mutable and bound to the UI.</summary>
public sealed class CommitItem : ObservableObject
{
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Author { get; init; }
    public required DateTimeOffset Date { get; init; }
    public required string[] Parents { get; init; }
    public required string Subject { get; init; }

    public bool IsMerge => Parents.Length > 1;
    public bool IsRoot => Parents.Length == 0;

    public string DateText => Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private int _reviewedFiles;
    public int ReviewedFiles
    {
        get => _reviewedFiles;
        set { if (Set(ref _reviewedFiles, value)) OnProgressChanged(); }
    }

    private int _totalFiles = -1;
    /// <summary>-1 means "never opened, file count unknown".</summary>
    public int TotalFiles
    {
        get => _totalFiles;
        set { if (Set(ref _totalFiles, value)) OnProgressChanged(); }
    }

    private void OnProgressChanged()
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(IsFullyReviewed));
    }

    public bool IsFullyReviewed => TotalFiles > 0 && ReviewedFiles >= TotalFiles;

    public string ProgressText => TotalFiles < 0
        ? "–"
        : IsFullyReviewed ? $"✓ {ReviewedFiles}/{TotalFiles}" : $"{ReviewedFiles}/{TotalFiles}";
}

/// <summary>One changed file inside the selected diff target.</summary>
public sealed class FileItem : ObservableObject
{
    /// <summary>Path on the "new" side. For deletions this is the removed path.</summary>
    public required string Path { get; init; }

    /// <summary>Source path for renames/copies, otherwise null.</summary>
    public string? OldPath { get; init; }

    public required ChangeKind Kind { get; init; }
    public int Added { get; set; }
    public int Deleted { get; set; }
    public bool IsBinary { get; set; }

    private bool _isReviewed;
    public bool IsReviewed
    {
        get => _isReviewed;
        set => Set(ref _isReviewed, value);
    }

    public string StatusLetter => Kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Modified => "M",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Copied => "C",
        ChangeKind.TypeChanged => "T",
        ChangeKind.Unmerged => "U",
        _ => "?",
    };

    public string DisplayPath => OldPath is null ? Path : $"{OldPath} → {Path}";

    public string StatText => IsBinary ? "bin" : $"+{Added} −{Deleted}";
}

/// <summary>A base..head pair to diff, derived from the commit selection.</summary>
public sealed class DiffTarget
{
    public required string BaseSha { get; init; }
    public required string HeadSha { get; init; }
    public required int CommitCount { get; init; }
    public required string Title { get; init; }

    public bool IsSingleCommit => CommitCount == 1;

    /// <summary>Stable identity used as the review-state key. Single commits key by their own sha.</summary>
    public string Key => IsSingleCommit ? HeadSha : $"{Short(BaseSha)}..{Short(HeadSha)}";

    private static string Short(string sha) => sha.Length <= 12 ? sha : sha[..12];
}

public sealed class DiffOptions
{
    /// <summary>Context lines; a negative value means "whole file".</summary>
    public int Context { get; init; } = 3;
    public bool IgnoreWhitespace { get; init; }

    public int EffectiveContext => Context < 0 ? 100000 : Context;
}

/// <summary>A run of text within a line, flagged when word diffing found it changed.</summary>
public sealed class DiffSegment
{
    public required string Text { get; init; }
    public required bool Changed { get; init; }
}

/// <summary>One rendered line of a unified diff.</summary>
public sealed class DiffLine
{
    public required DiffLineKind Kind { get; init; }
    public required string Text { get; init; }
    public required string DisplayText { get; init; }

    /// <summary>Tab-expanded text without the leading +/-/space marker.</summary>
    public required string Content { get; init; }

    public int? OldNo { get; init; }
    public int? NewNo { get; init; }

    /// <summary>Runs covering <see cref="DisplayText"/>. Always at least one segment.</summary>
    public IReadOnlyList<DiffSegment> Segments { get; set; } = [];

    public string OldNoText => OldNo?.ToString() ?? "";
    public string NewNoText => NewNo?.ToString() ?? "";
}

/// <summary>
/// A row of the changed-file tree: either a folder that can be opened and closed, or one
/// changed file. The tree is flattened into a plain list for display so the pane keeps its
/// virtualisation and its arrow-key navigation.
/// </summary>
public sealed class FileNode : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>Level of nesting, assigned once the tree has been compacted.</summary>
    public int Depth { get; set; }

    /// <summary>Null on a folder.</summary>
    public FileItem? File { get; init; }

    /// <summary>Enclosing folder, so opening the way to a row costs only its depth.</summary>
    public FileNode? Parent { get; set; }

    /// <summary>Whether the row is currently in the flattened list on screen.</summary>
    public bool IsVisible { get; set; }

    public List<FileNode> Children { get; } = [];

    public bool IsFolder => File is null;

    /// <summary>Indent for the row, one step per level of nesting.</summary>
    public double Indent => Depth * 14;

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) OnPropertyChanged(nameof(ChevronGlyph)); }
    }

    public string ChevronGlyph => IsExpanded ? "M3.5,6 L8,10.5 L12.5,6" : "M6,3.5 L10.5,8 L6,12.5";

    public string Glyph => IsFolder ? FileIcon.Folder : FileIcon.For(File!.Path).Glyph;

    public Brush GlyphBrush => IsFolder ? FileIcon.FolderBrush : FileIcon.For(File!.Path).Brush;

    // ---- folder progress, refreshed when the files under it are checked off ----

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    private bool _isFullyReviewed;
    public bool IsFullyReviewed
    {
        get => _isFullyReviewed;
        set => Set(ref _isFullyReviewed, value);
    }

    /// <summary>Every file at or below this node, in display order.</summary>
    public IEnumerable<FileItem> Files()
    {
        if (File is not null)
        {
            yield return File;
            yield break;
        }

        foreach (var child in Children)
            foreach (var file in child.Files())
                yield return file;
    }

    public void RefreshProgress()
    {
        if (IsFolder)
        {
            foreach (var child in Children) child.RefreshProgress();

            var total = 0;
            var done = 0;
            foreach (var file in Files())
            {
                total++;
                if (file.IsReviewed) done++;
            }

            ProgressText = $"{done}/{total}";
            IsFullyReviewed = total > 0 && done == total;
        }
        else
        {
            IsFullyReviewed = File!.IsReviewed;
        }
    }
}

public enum DiffCellKind { Empty, Context, Added, Removed }

/// <summary>One side of a side-by-side row.</summary>
public sealed class DiffCell
{
    public static readonly DiffCell Empty = new()
    {
        Kind = DiffCellKind.Empty,
        Text = "",
        Segments = [],
    };

    public required DiffCellKind Kind { get; init; }
    public int? LineNo { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<DiffSegment> Segments { get; init; }

    public string LineNoText => LineNo?.ToString() ?? "";
}

public enum DiffRowKind { Pair, Hunk, Meta, Note }

/// <summary>One row of the side-by-side view: an old/new pair, or a full-width banner.</summary>
public sealed class DiffRow
{
    public required DiffRowKind Kind { get; init; }
    public DiffCell Left { get; init; } = DiffCell.Empty;
    public DiffCell Right { get; init; } = DiffCell.Empty;
    public string Banner { get; init; } = "";

    /// <summary>What Ctrl+C puts on the clipboard for this row.</summary>
    public required string CopyText { get; init; }
}

/// <summary>Both renderings of one file's diff, built from a single parse.</summary>
public sealed class ParsedDiff
{
    public required List<DiffLine> Lines { get; init; }
    public required List<DiffRow> Rows { get; init; }
}
