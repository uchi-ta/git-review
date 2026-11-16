using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GitReview;

/// <summary>差分の中のどの行が変わったか。色に使う。</summary>
internal enum DiffMark { None, Added, Removed, Both }

/// <summary>縮小イメージの 1 行。文字は点の並びとして描くので、中身はそのまま渡す。</summary>
internal readonly record struct DiffOverviewLine(DiffMark Mark, string Text);

/// <summary>
/// 差分ペインの右端に出す、コードの縮小イメージつきのスクロールバー
/// （VS Code のミニマップと同じ役割）。
///
/// ファイル全体を縦に縮め、文字は読めない大きさの点の並びとして置く。追加・削除の行には
/// 色の帯を敷くので、どこが変わったかと、どんな形のコードかが一目で分かる。
/// 今見えている範囲は明るい枠で示し、クリックやドラッグでその位置へ飛べる。
/// 一覧の標準のスクロールバーはこれで置き換える（同じものが 2 本並ぶと狭いため）。
///
/// 絵は差分 1 つにつき一度だけ組み立てて使い回す。スクロールのたびに作り直すと、
/// 何千行もの点を毎回描くことになって重くなるため。
/// </summary>
internal sealed class DiffOverview : FrameworkElement
{
    private const double SideMargin = 3;
    private const double CharWidth = 1.15;      // 文字 1 つぶんの幅
    private const double MaxLineHeight = 3;     // 行の高さの上限（これ以上は間延びする）
    private const double MinThumbHeight = 18;

    /// <summary>描く行数の上限。これを超えたら間引く（点が重なって見た目は変わらない）。</summary>
    private const int MaxDrawnLines = 4000;

    private IReadOnlyList<DiffOverviewLine> _lines = [];
    private ScrollViewer? _scroller;
    private DrawingGroup? _picture;
    private Size _pictureSize;
    private bool _dragging;

    public DiffOverview()
    {
        Focusable = false;
        Cursor = Cursors.Arrow;
    }

    /// <summary>行ごとの中身。差分を出し直すたびに入れ替える。</summary>
    public void SetLines(IReadOnlyList<DiffOverviewLine> lines)
    {
        _lines = lines;
        _picture = null;
        InvalidateVisual();
    }

    /// <summary>位置を合わせる相手の一覧。表示を切り替えたときに差し替える。</summary>
    public void Follow(ScrollViewer? scroller)
    {
        if (ReferenceEquals(_scroller, scroller)) return;

        if (_scroller is not null) _scroller.ScrollChanged -= OnScrollChanged;
        _scroller = scroller;
        if (_scroller is not null) _scroller.ScrollChanged += OnScrollChanged;

        InvalidateVisual();
    }

    /// <summary>テーマを変えたときなど、色を取り直す。</summary>
    public void Refresh()
    {
        _picture = null;
        InvalidateVisual();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidateVisual();

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _picture = null;   // 大きさが変わったら縮め方も変わる
    }

    // ---------------------------------------------------------------- 描画

    protected override void OnRender(DrawingContext dc)
    {
        try
        {
            Draw(dc);
        }
        catch (Exception ex)
        {
            Core.Stage.Log("diff overview: " + ex.Message);
        }
    }

    private void Draw(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (Brush("GutterBg") is { } track) dc.DrawRectangle(track, null, new Rect(0, 0, width, height));

        if (_picture is null || _pictureSize != new Size(width, height))
        {
            _picture = BuildPicture(width, height);
            _pictureSize = new Size(width, height);
        }

        if (_picture is not null) dc.DrawDrawing(_picture);

        DrawThumb(dc, width, height);
    }

    /// <summary>縮小イメージを組み立てる。差分が変わるか、大きさが変わったときだけ呼ぶ。</summary>
    private DrawingGroup? BuildPicture(double width, double height)
    {
        if (_lines.Count == 0) return null;

        var added = Brush("DiffAddMark");
        var removed = Brush("DiffDelMark");
        var text = Brush("TextMuted") ?? Brush("Text");
        if (added is null || removed is null || text is null) return null;

        var addBand = Fade(added, 0.18);
        var delBand = Fade(removed, 0.18);
        var addGlyph = Fade(added, 0.95);
        var delGlyph = Fade(removed, 0.95);
        var plainGlyph = Fade(text, 0.55);

        var step = height / _lines.Count;
        var lineHeight = Math.Max(0.8, Math.Min(MaxLineHeight, step));
        var skip = Math.Max(1, (int)Math.Ceiling(_lines.Count / (double)MaxDrawnLines));
        var maxChars = (int)((width - SideMargin * 2) / CharWidth);

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            for (var i = 0; i < _lines.Count; i += skip)
            {
                var line = _lines[i];
                var top = i * step;

                // 変わった行には帯を敷く。行が細いときでも色が見えるように 1px は確保する
                var band = line.Mark switch
                {
                    DiffMark.Added or DiffMark.Both => addBand,
                    DiffMark.Removed => delBand,
                    _ => null,
                };
                if (band is not null)
                    dc.DrawRectangle(band, null, new Rect(0, top, width, Math.Max(1, lineHeight)));

                if (line.Text.Length == 0) continue;

                var glyph = line.Mark switch
                {
                    DiffMark.Added or DiffMark.Both => addGlyph,
                    DiffMark.Removed => delGlyph,
                    _ => plainGlyph,
                };

                DrawGlyphs(dc, line.Text, glyph, top, lineHeight, maxChars);
            }
        }

        group.Freeze();
        return group;
    }

    /// <summary>
    /// 1 行ぶんの文字を点の並びとして描く。空白でない文字が続くところを 1 本の棒にするので、
    /// 字そのものは読めないが、字下げや行の長さといった「コードの形」は残る。
    /// </summary>
    private static void DrawGlyphs(
        DrawingContext dc, string line, Brush brush, double top, double lineHeight, int maxChars)
    {
        var height = Math.Max(0.8, lineHeight - (lineHeight > 2 ? 1 : 0.2));
        var limit = Math.Min(line.Length, maxChars);

        var i = 0;
        while (i < limit)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < limit && !char.IsWhiteSpace(line[i])) i++;

            dc.DrawRectangle(brush, null,
                new Rect(SideMargin + start * CharWidth, top, Math.Max(1, (i - start) * CharWidth), height));
        }
    }

    private void DrawThumb(DrawingContext dc, double width, double height)
    {
        if (_scroller is not { ExtentHeight: > 0 } scroller) return;
        if (scroller.ViewportHeight >= scroller.ExtentHeight) return;   // 全部見えているなら要らない

        var top = scroller.VerticalOffset / scroller.ExtentHeight * height;
        var size = Math.Max(MinThumbHeight, scroller.ViewportHeight / scroller.ExtentHeight * height);
        top = Math.Min(top, Math.Max(0, height - size));

        var accent = Brush("PaneActive") ?? Brush("Accent") ?? Brush("Border");
        if (accent is null) return;

        // 下の縮小イメージが透けるくらいの濃さにする
        var rect = new Rect(0.5, top, Math.Max(1, width - 1), size);
        dc.DrawRoundedRectangle(Fade(accent, 0.22), new Pen(Fade(accent, 0.7), 1), rect, 3, 3);
    }

    private static Brush Fade(Brush brush, double opacity)
    {
        var copy = brush.Clone();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }

    private static Brush? Brush(string key) => Application.Current.TryFindResource(key) as Brush;

    // ---------------------------------------------------------------- 操作

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        ScrollTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;

        ScrollTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_scroller is null) return;

        _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>押した高さが画面の真ん中に来るように送る。</summary>
    private void ScrollTo(double y)
    {
        if (_scroller is not { ExtentHeight: > 0 } scroller || ActualHeight <= 0) return;

        var fraction = Math.Clamp(y / ActualHeight, 0, 1);
        scroller.ScrollToVerticalOffset(fraction * scroller.ExtentHeight - scroller.ViewportHeight / 2);
    }
}
