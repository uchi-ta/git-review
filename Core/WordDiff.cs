using System.Text;

namespace GitReview.Core;

/// <summary>
/// Word-level diff between a removed line and the added line paired with it, so the reader
/// sees which part of the line actually changed rather than the whole row being coloured.
///
/// One instance is reused for a whole diff: the LCS table is the expensive part and it is
/// allocated once rather than per line.
/// </summary>
internal sealed class WordDiffer
{
    /// <summary>Longer lines skip word diffing; the table is O(n*m) and the payoff drops.</summary>
    private const int MaxTokens = 160;

    /// <summary>
    /// Below this share of shared characters the two lines are treated as unrelated, and
    /// highlighting every word would be noise rather than information.
    /// </summary>
    private const double MinSimilarity = 0.25;

    private readonly int[,] _lcs = new int[MaxTokens + 1, MaxTokens + 1];

    public bool TryCompute(
        string oldText,
        string newText,
        out IReadOnlyList<DiffSegment> oldSegments,
        out IReadOnlyList<DiffSegment> newSegments)
    {
        oldSegments = [];
        newSegments = [];

        if (oldText.Length == 0 || newText.Length == 0) return false;
        if (oldText == newText) return false;

        var a = Tokenize(oldText);
        var b = Tokenize(newText);
        if (a.Count > MaxTokens || b.Count > MaxTokens) return false;

        BuildTable(a, b);

        var oldChanged = new bool[a.Count];
        var newChanged = new bool[b.Count];
        var common = 0;

        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                common += a[x].Length;
                x++;
                y++;
            }
            else if (_lcs[x + 1, y] >= _lcs[x, y + 1])
            {
                oldChanged[x++] = true;
            }
            else
            {
                newChanged[y++] = true;
            }
        }
        while (x < a.Count) oldChanged[x++] = true;
        while (y < b.Count) newChanged[y++] = true;

        var similarity = (double)common / Math.Max(oldText.Length, newText.Length);
        if (similarity < MinSimilarity) return false;

        oldSegments = Merge(a, oldChanged);
        newSegments = Merge(b, newChanged);
        return true;
    }

    private void BuildTable(List<string> a, List<string> b)
    {
        for (var i = a.Count; i >= 0; i--)
        {
            for (var j = b.Count; j >= 0; j--)
            {
                if (i == a.Count || j == b.Count)
                {
                    _lcs[i, j] = 0;
                }
                else
                {
                    _lcs[i, j] = a[i] == b[j]
                        ? _lcs[i + 1, j + 1] + 1
                        : Math.Max(_lcs[i + 1, j], _lcs[i, j + 1]);
                }
            }
        }
    }

    /// <summary>
    /// Splits into identifier-like runs, whitespace runs, and single punctuation characters —
    /// the granularity that makes a code change read clearly.
    /// </summary>
    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var i = 0;

        while (i < s.Length)
        {
            var start = i;
            var c = s[i];

            if (char.IsLetterOrDigit(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            }
            else if (c is ' ' or '\t')
            {
                while (i < s.Length && s[i] is ' ' or '\t') i++;
            }
            else
            {
                i++;
            }

            tokens.Add(s[start..i]);
        }
        return tokens;
    }

    private static List<DiffSegment> Merge(List<string> tokens, bool[] changed)
    {
        var segments = new List<DiffSegment>();
        var text = new StringBuilder();
        bool? current = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (current is null)
            {
                current = changed[i];
            }
            else if (changed[i] != current)
            {
                segments.Add(new DiffSegment { Text = text.ToString(), Changed = current.Value });
                text.Clear();
                current = changed[i];
            }
            text.Append(tokens[i]);
        }

        if (text.Length > 0)
            segments.Add(new DiffSegment { Text = text.ToString(), Changed = current ?? false });

        return segments;
    }
}
