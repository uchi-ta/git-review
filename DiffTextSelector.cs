using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GitReview;

/// <summary>
/// 差分ペインの文字をマウスで選べるようにする。
///
/// 差分は仮想化した一覧（ListBox）で描いていて、行は TextBlock なので、そのままでは
/// 文字単位の選択ができない。選択そのものは自前で持ち、見た目は一覧の上に敷いた
/// レイヤー（Adorner）へ四角を描いて出す。描き直すのは画面に出ている行だけなので、
/// 何万行の差分でも上下の動きは重くならない。
///
/// 左右に並べた表示では、左右をまたぐ選択はしない（またぐと、どちらの側の文字を
/// コピーしたのか決められないため）。
/// </summary>
internal sealed class DiffTextSelector
{
    /// <summary>これ以上動かしたら「ドラッグ」とみなす。ただの行クリックと区別するため。</summary>
    private const double DragThreshold = 3;

    private readonly ListBox _list;
    private readonly IReadOnlyList<string> _cellNames;

    /// <summary>行番号と欄の名前から、その欄に出している文字列を取る。</summary>
    private readonly Func<int, string, string?> _cellText;

    /// <summary>4 回クリックしたときに広げる範囲（同じ @@ の塊）。</summary>
    private readonly Func<int, (int Start, int End)>? _blockRange;

    private readonly SelectionAdorner _adorner;

    private Point _pressPoint;
    private Spot? _press;
    private Spot? _anchor;
    private Spot? _focus;
    private string? _cell;
    private bool _dragging;

    private readonly record struct Spot(int Index, string Cell, int Offset);

    public DiffTextSelector(
        ListBox list,
        IReadOnlyList<string> cellNames,
        Func<int, string, string?> cellText,
        Func<int, (int Start, int End)>? blockRange = null)
    {
        _list = list;
        _cellNames = cellNames;
        _cellText = cellText;
        _blockRange = blockRange;
        _adorner = new SelectionAdorner(list, this);

        list.PreviewMouseLeftButtonDown += OnMouseDown;
        list.PreviewMouseMove += OnMouseMove;
        list.PreviewMouseLeftButtonUp += OnMouseUp;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => _adorner.Refresh()));

        // 一覧ができあがってからでないとレイヤーが取れない
        list.Loaded += (_, _) => _adorner.Attach();
    }

    public bool HasSelection => SelectedText.Length > 0;

    /// <summary>選んだ範囲の文字列。何も選んでいなければ空。</summary>
    public string SelectedText
    {
        get
        {
            if (Ordered() is not var (start, end) || _cell is null) return "";

            var sb = new System.Text.StringBuilder();
            for (var i = start.Index; i <= end.Index; i++)
            {
                var text = _cellText(i, _cell) ?? "";
                var from = i == start.Index ? Math.Clamp(start.Offset, 0, text.Length) : 0;
                var to = i == end.Index ? Math.Clamp(end.Offset, 0, text.Length) : text.Length;

                if (i > start.Index) sb.Append("\r\n");
                if (to > from) sb.Append(text, from, to - from);
            }
            return sb.ToString();
        }
    }

    /// <summary>選択の見た目の出し入れ。ヘルプなど、上に重なるものを出すあいだは隠す。</summary>
    public void SetVisible(bool visible) => _adorner.SetVisible(visible);

    public void Clear()
    {
        if (_anchor is null && _focus is null) return;

        _anchor = _focus = null;
        _cell = null;
        _adorner.Refresh();
    }

    // ---------------------------------------------------------------- マウス

    /// <summary>
    /// 選択はマウスを動かすたびに走る。ここで例外を出すとエラーの窓が出てレビューが止まるので、
    /// 何かあっても選択を捨てて記録するだけにする。
    /// </summary>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Core.Stage.Log("diff selection: " + ex.Message);

            _dragging = false;
            if (_list.IsMouseCaptured) _list.ReleaseMouseCapture();
            Clear();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e) => Guard(() =>
    {
        Clear();

        _pressPoint = e.GetPosition(_list);
        _press = HitSpot(_pressPoint, null);

        // VS Code と同じく、2 回で語、3 回で行、4 回で @@ の塊、5 回で差分全体と広げる
        if (e.ClickCount < 2 || _press is not { } spot) return;

        SelectAround(spot, e.ClickCount);
        _list.Focus();
        e.Handled = true;
    });

    /// <summary>クリックの回数に応じて、押した場所のまわりを選ぶ。</summary>
    private void SelectAround(Spot spot, int clicks)
    {
        _cell = spot.Cell;
        var text = _cellText(spot.Index, spot.Cell) ?? "";

        if (clicks == 2)
        {
            var (from, to) = WordAt(text, spot.Offset);
            _anchor = spot with { Offset = from };
            _focus = spot with { Offset = to };
        }
        else if (clicks == 3)
        {
            _anchor = spot with { Offset = 0 };
            _focus = spot with { Offset = text.Length };
        }
        else
        {
            var (start, end) = clicks == 4 && _blockRange is not null
                ? _blockRange(spot.Index)
                : (0, Math.Max(0, _list.Items.Count - 1));

            _anchor = new Spot(start, spot.Cell, 0);
            _focus = new Spot(end, spot.Cell, (_cellText(end, spot.Cell) ?? "").Length);
        }

        _list.SelectedItems.Clear();
        _adorner.Refresh();
    }

    /// <summary>
    /// 語の範囲。区切りは差分の語ハイライトと同じ規則で、識別子（英数字と <c>_</c>）の並び、
    /// 空白の並び、それ以外は 1 文字。語の直後で押したときは、その語を選ぶ。
    /// </summary>
    private static (int From, int To) WordAt(string text, int offset)
    {
        if (text.Length == 0) return (0, 0);

        var i = Math.Clamp(offset, 0, text.Length - 1);
        if (i > 0 && !IsWordChar(text[i]) && IsWordChar(text[i - 1])) i--;

        if (IsWordChar(text[i])) return Run(text, i, IsWordChar);
        if (text[i] is ' ' or '\t') return Run(text, i, c => c is ' ' or '\t');

        return (i, i + 1);
    }

    private static (int From, int To) Run(string text, int i, Func<char, bool> belongs)
    {
        var from = i;
        while (from > 0 && belongs(text[from - 1])) from--;

        var to = i;
        while (to < text.Length && belongs(text[to])) to++;

        return (from, to);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void OnMouseMove(object sender, MouseEventArgs e) => Guard(() => MoveTo(sender, e));

    private void MoveTo(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var point = e.GetPosition(_list);

        if (!_dragging)
        {
            if (_press is null) return;
            if (Math.Abs(point.X - _pressPoint.X) < DragThreshold &&
                Math.Abs(point.Y - _pressPoint.Y) < DragThreshold) return;

            _dragging = true;
            _anchor = _press;
            _cell = _press.Value.Cell;
            _list.CaptureMouse();

            // 行の選択（青い帯）と重なると何を選んでいるのか分からなくなる
            _list.SelectedItems.Clear();
        }

        AutoScroll(point);

        if (HitSpot(point, _cell) is { } spot) _focus = spot;
        _adorner.Refresh();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => Guard(() =>
    {
        _press = null;
        if (!_dragging) return;

        _dragging = false;
        _list.ReleaseMouseCapture();
        e.Handled = true;
    });

    /// <summary>ペインの外まで引っ張ったら、その方向へ送る。</summary>
    private void AutoScroll(Point point)
    {
        var scroller = FindScroller();
        if (scroller is null) return;

        if (point.Y < 0) scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 3);
        else if (point.Y > _list.ActualHeight) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 3);
    }

    // ---------------------------------------------------------------- 位置の割り出し

    /// <summary>一覧の中の座標を、行番号・欄・その欄の何文字目か、に変換する。</summary>
    private Spot? HitSpot(Point point, string? restrictCell)
    {
        var item = ItemAt(point);
        if (item is null) return null;

        var index = _list.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0) return null;

        var cell = restrictCell is null ? PickCell(item, point) : FindCell(item, restrictCell);
        if (cell is null) return null;

        var local = _list.TranslatePoint(point, cell.Value.Text);
        var pointer = cell.Value.Text.GetPositionFromPoint(local, snapToText: true);
        if (pointer is null) return null;

        return new Spot(index, cell.Value.Name, new TextRange(cell.Value.Text.ContentStart, pointer).Text.Length);
    }

    /// <summary>
    /// 座標の上にある行。行と行の間や余白では、縦方向がいちばん近い行を選ぶ
    /// （ドラッグを画面の外へ引っ張ったときに途切れないようにするため）。
    /// </summary>
    private ListBoxItem? ItemAt(Point point)
    {
        if (_list.InputHitTest(point) is DependencyObject hit && Ancestor<ListBoxItem>(hit) is { } item) return item;

        ListBoxItem? best = null;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < _list.Items.Count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem candidate) continue;
            if (!candidate.IsVisible) continue;

            var bounds = Bounds(candidate);
            var distance = point.Y < bounds.Top ? bounds.Top - point.Y
                : point.Y > bounds.Bottom ? point.Y - bounds.Bottom
                : 0;

            if (distance >= bestDistance) continue;

            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    private (string Name, TextBlock Text)? PickCell(ListBoxItem item, Point point)
    {
        (string Name, TextBlock Text)? fallback = null;

        foreach (var name in _cellNames)
        {
            if (FindCell(item, name) is not { } cell) continue;

            var bounds = Bounds(cell.Text);
            if (point.X >= bounds.Left && point.X <= bounds.Right) return cell;

            // 欄の外（余白や行番号のあたり）なら、いちばん近い欄に寄せる
            if (fallback is null || Math.Abs(point.X - bounds.Left) < Math.Abs(point.X - Bounds(fallback.Value.Text).Left))
                fallback = cell;
        }
        return fallback;
    }

    private static (string Name, TextBlock Text)? FindCell(ListBoxItem item, string name)
        => Descendant<TextBlock>(item, name) is { } text ? (name, text) : null;

    private Rect Bounds(FrameworkElement element)
    {
        try
        {
            return element.TransformToAncestor(_list)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;   // まだ画面に出ていない
        }
    }

    private (Spot Start, Spot End)? Ordered()
    {
        if (_anchor is not { } a || _focus is not { } b) return null;

        var forward = a.Index < b.Index || (a.Index == b.Index && a.Offset <= b.Offset);
        var (start, end) = forward ? (a, b) : (b, a);
        return start.Index == end.Index && start.Offset == end.Offset ? null : (start, end);
    }

    // ---------------------------------------------------------------- 見た目

    /// <summary>選んでいる範囲を四角で返す。画面に出ている行の分だけ。</summary>
    private IEnumerable<Rect> SelectionRects()
    {
        if (Ordered() is not var (start, end) || _cell is null) yield break;

        for (var i = start.Index; i <= end.Index; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
            if (Descendant<TextBlock>(item, _cell) is not { IsVisible: true } text) continue;

            var full = _cellText(i, _cell) ?? "";
            var from = i == start.Index ? Math.Clamp(start.Offset, 0, full.Length) : 0;
            var to = i == end.Index ? Math.Clamp(end.Offset, 0, full.Length) : full.Length;
            if (to <= from) continue;

            foreach (var rect in RectsIn(text, from, to)) yield return rect;
        }
    }

    /// <summary>1 つの欄の中の from..to を四角にする。折り返している行は行ごとに分ける。</summary>
    private IEnumerable<Rect> RectsIn(TextBlock text, int from, int to)
    {
        Rect head, tail;
        try
        {
            head = PointerAt(text, from).GetCharacterRect(LogicalDirection.Forward);
            tail = PointerAt(text, to).GetCharacterRect(LogicalDirection.Backward);
        }
        catch (Exception)
        {
            yield break;   // 測れないときは描かない
        }

        // 空の行や、まだ整列していない行では測れない（Rect.Empty が返る）
        if (head.IsEmpty || tail.IsEmpty) yield break;

        var transform = TryTransform(text);
        if (transform is null) yield break;

        var width = Math.Max(0, text.ActualWidth);

        if (Math.Abs(head.Top - tail.Top) < 0.5)
        {
            yield return transform.TransformBounds(Box(head.Left, head.Top, tail.Left - head.Left, head.Height));
            yield break;
        }

        yield return transform.TransformBounds(Box(head.Left, head.Top, width - head.Left, head.Height));

        var middle = tail.Top - (head.Top + head.Height);
        if (middle > 0.5) yield return transform.TransformBounds(Box(0, head.Top + head.Height, width, middle));

        yield return transform.TransformBounds(Box(0, tail.Top, tail.Left, tail.Height));
    }

    /// <summary>負や無限の大きさを Rect に渡すと落ちるので、必ず正の値にしてから作る。</summary>
    private static Rect Box(double x, double y, double width, double height)
    {
        static double Sane(double v) => double.IsFinite(v) ? Math.Max(1, v) : 1;

        return new Rect(double.IsFinite(x) ? x : 0, double.IsFinite(y) ? y : 0, Sane(width), Sane(height));
    }

    private GeneralTransform? TryTransform(Visual from)
    {
        try { return from.TransformToAncestor(_list); }
        catch (InvalidOperationException) { return null; }
    }

    // ---------------------------------------------------------------- 小道具

    /// <summary>文字数（見えている文字の数）から TextPointer へ。間に挟まる Run の境目は数えない。</summary>
    private static TextPointer PointerAt(TextBlock text, int offset)
    {
        var p = text.ContentStart;

        while (p is not null)
        {
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = p.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= run) return p.GetPositionAtOffset(offset) ?? text.ContentEnd;

                offset -= run;
                p = p.GetPositionAtOffset(run);
                continue;
            }
            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }
        return text.ContentEnd;
    }

    /// <summary>
    /// 親をたどる。ヒットテストは Run のような Visual ではない要素も返すので、
    /// その場合は論理ツリー側をたどってから視覚ツリーに戻る。
    /// </summary>
    private static T? Ancestor<T>(DependencyObject? from) where T : DependencyObject
    {
        var d = from;
        while (d is not null)
        {
            if (d is T found) return found;

            d = d switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(d),
                FrameworkContentElement element => element.Parent,
                ContentElement content => ContentOperations.GetParent(content),
                _ => LogicalTreeHelper.GetParent(d),
            };
        }
        return null;
    }

    private static T? Descendant<T>(DependencyObject from, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(from);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(from, i);
            if (child is T found && found.Name == name) return found;

            if (Descendant<T>(child, name) is { } deeper) return deeper;
        }
        return null;
    }

    private ScrollViewer? FindScroller() => DescendantOfType<ScrollViewer>(_list);

    private static T? DescendantOfType<T>(DependencyObject from) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(from);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(from, i);
            if (child is T found) return found;

            if (DescendantOfType<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>一覧の上に敷いて、選択範囲の四角だけを描くレイヤー。</summary>
    private sealed class SelectionAdorner : Adorner
    {
        private readonly DiffTextSelector _owner;
        private bool _attached;

        public SelectionAdorner(UIElement adorned, DiffTextSelector owner) : base(adorned)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public void Attach()
        {
            if (_attached) return;

            var layer = AdornerLayer.GetAdornerLayer(AdornedElement);
            if (layer is null) return;

            layer.Add(this);
            _attached = true;
        }

        public void Refresh()
        {
            Attach();
            InvalidateVisual();
        }

        public void SetVisible(bool visible)
            => Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        protected override void OnRender(DrawingContext dc)
        {
            // 描画の最中に落ちるとエラーの窓が出てレビューが止まる。何かあっても描かないだけにする
            try
            {
                // 下の文字が透けるように、アクセント色を薄くして敷く
                if (Application.Current.TryFindResource("Accent") is not SolidColorBrush accent) return;

                var brush = new SolidColorBrush(accent.Color) { Opacity = 0.35 };
                brush.Freeze();

                dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
                foreach (var rect in _owner.SelectionRects()) dc.DrawRectangle(brush, null, rect);
                dc.Pop();
            }
            catch (Exception ex)
            {
                Core.Stage.Log("diff selection draw: " + ex.Message);
            }
        }
    }
}
