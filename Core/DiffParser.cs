using System.Text;
using System.Text.RegularExpressions;

namespace GitReview.Core;

/// <summary>
/// Turns raw `git diff` output into renderable lines with per-side line numbers, then pairs
/// removals with additions to build the side-by-side rows and the word-level highlighting.
/// </summary>
public static partial class DiffParser
{
    private const int TabSize = 4;

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    public static ParsedDiff Parse(string raw)
    {
        var lines = ParseLines(raw);
        var rows = BuildRows(lines);
        return new ParsedDiff { Lines = lines, Rows = rows };
    }

    // ---------------------------------------------------------------- unified

    private static List<DiffLine> ParseLines(string raw)
    {
        var result = new List<DiffLine>();
        if (string.IsNullOrEmpty(raw))
        {
            result.Add(Note("(差分がありません)"));
            return result;
        }

        var oldNo = 0;
        var newNo = 0;
        var sawHunk = false;
        var headerNotes = new List<string>();

        foreach (var line in raw.Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                var m = HunkHeader().Match(text);
                var skipped = 0;
                if (m.Success)
                {
                    var nextNew = int.Parse(m.Groups[3].Value);

                    // ここまでに出した行と、次の塊の先頭との差が、省略した行数になる
                    skipped = Math.Max(0, nextNew - Math.Max(newNo, 1));

                    oldNo = int.Parse(m.Groups[1].Value);
                    newNo = nextNew;
                }
                sawHunk = true;
                result.Add(Make(DiffLineKind.Hunk, text, null, null, SkipLabel(skipped)));
                continue;
            }

            if (!sawHunk)
            {
                // The file list already shows status and paths, so drop the redundant header
                // lines and keep only what the rest of the UI cannot tell the reader.
                if (IsInformativeHeader(text)) headerNotes.Add(text);
                continue;
            }

            if (text.Length == 0)
            {
                // A trailing empty line from the final split; treat as unchanged blank context.
                result.Add(Make(DiffLineKind.Context, "", oldNo++, newNo++));
                continue;
            }

            switch (text[0])
            {
                case '+':
                    result.Add(Make(DiffLineKind.Added, text, null, newNo++));
                    break;
                case '-':
                    result.Add(Make(DiffLineKind.Removed, text, oldNo++, null));
                    break;
                case ' ':
                    result.Add(Make(DiffLineKind.Context, text, oldNo++, newNo++));
                    break;
                default:
                    result.Add(Make(DiffLineKind.Meta, text, null, null));
                    break;
            }
        }

        // Drop a single trailing blank context line produced by the diff's final newline.
        if (result.Count > 0)
        {
            var last = result[^1];
            if (last.Kind == DiffLineKind.Context && last.Text.Length == 0)
                result.RemoveAt(result.Count - 1);
        }

        if (!sawHunk)
        {
            // No hunks at all: a pure rename, a mode change, or a binary file.
            foreach (var note in headerNotes) result.Add(Note(note));

            if (result.Count == 0)
                result.Add(Note("(本文の変更はありません)"));
        }

        return result;
    }

    // ---------------------------------------------------------------- side by side

    /// <summary>
    /// Walks the unified lines, pairing each block of removals with the additions that follow
    /// it. Paired lines also get word-level segments, which the unified view uses too.
    /// </summary>
    private static List<DiffRow> BuildRows(List<DiffLine> lines)
    {
        var differ = new WordDiffer();
        var rows = new List<DiffRow>(lines.Count);
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];

            if (line.Kind is DiffLineKind.Added or DiffLineKind.Removed)
            {
                var removed = new List<DiffLine>();
                var added = new List<DiffLine>();

                while (i < lines.Count && lines[i].Kind is DiffLineKind.Added or DiffLineKind.Removed)
                {
                    if (lines[i].Kind == DiffLineKind.Removed) removed.Add(lines[i]);
                    else added.Add(lines[i]);
                    i++;
                }

                EmitChangeBlock(rows, removed, added, differ);
                continue;
            }

            if (line.Kind == DiffLineKind.Context)
            {
                rows.Add(new DiffRow
                {
                    Kind = DiffRowKind.Pair,
                    Left = Cell(DiffCellKind.Context, line.OldNo, line),
                    Right = Cell(DiffCellKind.Context, line.NewNo, line),
                    CopyText = line.Text,
                });
                i++;
                continue;
            }

            rows.Add(new DiffRow
            {
                Kind = line.Kind switch
                {
                    DiffLineKind.Hunk => DiffRowKind.Hunk,
                    DiffLineKind.Note => DiffRowKind.Note,
                    _ => DiffRowKind.Meta,
                },
                Banner = line.DisplayText,
                CopyText = line.Text,
            });
            i++;
        }

        return rows;
    }

    private static void EmitChangeBlock(
        List<DiffRow> rows, List<DiffLine> removed, List<DiffLine> added, WordDiffer differ)
    {
        var count = Math.Max(removed.Count, added.Count);

        for (var k = 0; k < count; k++)
        {
            var left = k < removed.Count ? removed[k] : null;
            var right = k < added.Count ? added[k] : null;

            IReadOnlyList<DiffSegment>? leftWords = null;
            IReadOnlyList<DiffSegment>? rightWords = null;

            if (left is not null && right is not null &&
                differ.TryCompute(left.Content, right.Content, out var lw, out var rw))
            {
                leftWords = lw;
                rightWords = rw;

                // The unified view shows the same highlighting, behind the marker column.
                left.Segments = WithMarker(left, lw);
                right.Segments = WithMarker(right, rw);
            }

            rows.Add(new DiffRow
            {
                Kind = DiffRowKind.Pair,
                Left = left is null
                    ? DiffCell.Empty
                    : Cell(DiffCellKind.Removed, left.OldNo, left, leftWords),
                Right = right is null
                    ? DiffCell.Empty
                    : Cell(DiffCellKind.Added, right.NewNo, right, rightWords),
                CopyText = CopyTextFor(left, right),
            });
        }
    }

    private static string CopyTextFor(DiffLine? left, DiffLine? right)
    {
        if (left is not null && right is not null) return left.Text + "\n" + right.Text;
        return left?.Text ?? right?.Text ?? "";
    }

    private static DiffCell Cell(
        DiffCellKind kind, int? lineNo, DiffLine line, IReadOnlyList<DiffSegment>? words = null)
        => new()
        {
            Kind = kind,
            LineNo = lineNo,
            Text = line.Content,
            Segments = words ?? [new DiffSegment { Text = line.Content, Changed = false }],
        };

    /// <summary>Re-adds the +/- marker as an unchanged run so the unified view lines up.</summary>
    private static IReadOnlyList<DiffSegment> WithMarker(DiffLine line, IReadOnlyList<DiffSegment> content)
    {
        var markerLength = line.DisplayText.Length - line.Content.Length;
        if (markerLength <= 0) return content;

        var result = new List<DiffSegment>(content.Count + 1)
        {
            new() { Text = line.DisplayText[..markerLength], Changed = false },
        };
        result.AddRange(content);
        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsInformativeHeader(string text)
        => text.StartsWith("Binary files", StringComparison.Ordinal)
        || text.StartsWith("GIT binary patch", StringComparison.Ordinal)
        || text.StartsWith("similarity index", StringComparison.Ordinal)
        || text.StartsWith("dissimilarity index", StringComparison.Ordinal)
        || text.StartsWith("rename from", StringComparison.Ordinal)
        || text.StartsWith("rename to", StringComparison.Ordinal)
        || text.StartsWith("copy from", StringComparison.Ordinal)
        || text.StartsWith("copy to", StringComparison.Ordinal)
        || text.StartsWith("old mode", StringComparison.Ordinal)
        || text.StartsWith("new mode", StringComparison.Ordinal)
        || text.StartsWith("new file mode", StringComparison.Ordinal)
        || text.StartsWith("deleted file mode", StringComparison.Ordinal);

    private static DiffLine Note(string text) => Make(DiffLineKind.Note, text, null, null);

    /// <summary>
    /// 省略した部分に出す文言。`@@ -267,16 +267,32 @@` をそのまま出しても読み手には
    /// 情報が少ないので、何行飛ばしたのかだけを静かに示す。行番号は溝に出るので書かない。
    /// </summary>
    private static string SkipLabel(int skipped)
        => skipped > 0 ? $"⋯　{skipped:N0} 行省略" : "⋯";

    private static DiffLine Make(DiffLineKind kind, string text, int? oldNo, int? newNo, string? displayText = null)
    {
        string marker, content;

        if (kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context)
        {
            marker = text.Length > 0 ? text[..1] : "";
            content = ExpandTabs(text.Length > 0 ? text[1..] : "");
        }
        else
        {
            marker = "";
            content = ExpandTabs(text);
        }

        var display = displayText ?? marker + content;

        return new DiffLine
        {
            Kind = kind,
            Text = text,
            DisplayText = display,
            Content = content,
            OldNo = oldNo,
            NewNo = newNo,
            Segments = [new DiffSegment { Text = display, Changed = false }],
        };
    }

    /// <summary>WPF's TextBlock does not honour tab stops, so expand them here.</summary>
    private static string ExpandTabs(string s)
    {
        if (s.IndexOf('\t') < 0) return s;

        var sb = new StringBuilder(s.Length + 16);
        var col = 0;

        foreach (var c in s)
        {
            if (c == '\t')
            {
                var pad = TabSize - (col % TabSize);
                sb.Append(' ', pad);
                col += pad;
            }
            else
            {
                sb.Append(c);
                col++;
            }
        }
        return sb.ToString();
    }
}
