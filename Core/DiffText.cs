using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GitReview.Core;

/// <summary>
/// Renders <see cref="DiffSegment"/> runs into a TextBlock, tinting the changed ones.
///
/// A plain Text binding cannot express per-run backgrounds, and a converter cannot either,
/// so the inlines are built here. <see cref="HighlightProperty"/> names the brush resource
/// to use, which differs between the removed and added sides.
/// </summary>
public static class DiffText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<DiffSegment>), typeof(DiffText),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.RegisterAttached(
        "Highlight", typeof(string), typeof(DiffText),
        new PropertyMetadata(null, OnChanged));

    public static void SetSegments(DependencyObject target, IReadOnlyList<DiffSegment>? value)
        => target.SetValue(SegmentsProperty, value);

    public static IReadOnlyList<DiffSegment>? GetSegments(DependencyObject target)
        => (IReadOnlyList<DiffSegment>?)target.GetValue(SegmentsProperty);

    public static void SetHighlight(DependencyObject target, string? value)
        => target.SetValue(HighlightProperty, value);

    public static string? GetHighlight(DependencyObject target)
        => (string?)target.GetValue(HighlightProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;

        var segments = GetSegments(block);

        if (segments is null || segments.Count == 0)
        {
            block.Text = "";
            return;
        }

        // The common case by far: one unchanged run. Setting Text avoids building inlines
        // for every row of a long diff.
        if (segments.Count == 1 && !segments[0].Changed)
        {
            block.Text = segments[0].Text;
            return;
        }

        var key = GetHighlight(block);
        var brush = key is null ? null : block.TryFindResource(key) as Brush;

        block.Inlines.Clear();
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.Changed && brush is not null) run.Background = brush;
            block.Inlines.Add(run);
        }
    }
}
