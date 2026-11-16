using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GitReview;

/// <summary>
/// 今いる変更箇所を薄い枠で囲む。Ctrl+D / Ctrl+U で飛んだ先や、ファイルを開いた直後に、
/// 「どこに来たのか」が目で分かるようにするためのもの。
///
/// 行は仮想化した一覧で描いているので、枠は一覧の上に敷いたレイヤー（Adorner）に描く。
/// 画面に出ている行の分だけを見て位置を決めるため、何万行あっても重くならない。
/// </summary>
internal sealed class DiffBlockMarker
{
    private readonly ListBox _list;
    private readonly BlockAdorner _adorner;

    private int _start = -1;
    private int _end = -1;

    public DiffBlockMarker(ListBox list)
    {
        _list = list;
        _adorner = new BlockAdorner(list, this);

        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => _adorner.Refresh()));
        list.Loaded += (_, _) => _adorner.Attach();
    }

    /// <summary><paramref name="start"/> から <paramref name="end"/> までの行を囲む。</summary>
    public void Show(int start, int end)
    {
        _start = start;
        _end = Math.Max(start, end);
        _adorner.Refresh();
    }

    /// <summary>枠の出し入れ。ヘルプなど、上に重なるものを出すあいだは隠す。</summary>
    public void SetVisible(bool visible)
        => _adorner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void Clear()
    {
        if (_start < 0) return;

        _start = _end = -1;
        _adorner.Refresh();
    }

    /// <summary>囲む範囲。画面に出ている行が無ければ空。</summary>
    private Rect Bounds()
    {
        if (_start < 0) return Rect.Empty;

        var bounds = Rect.Empty;
        for (var i = _start; i <= _end && i < _list.Items.Count; i++)
        {
            if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) continue;
            if (!row.IsVisible || row.ActualHeight <= 0) continue;

            try
            {
                var top = row.TransformToAncestor(_list).Transform(new Point(0, 0)).Y;
                bounds.Union(new Rect(0, top, Math.Max(1, _list.ActualWidth), row.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                // まだ画面に出ていない行。無視してよい
            }
        }
        return bounds;
    }

    private sealed class BlockAdorner : Adorner
    {
        private readonly DiffBlockMarker _owner;
        private bool _attached;

        public BlockAdorner(UIElement adorned, DiffBlockMarker owner) : base(adorned)
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

        protected override void OnRender(DrawingContext dc)
        {
            try
            {
                var bounds = _owner.Bounds();
                if (bounds.IsEmpty || bounds.Height <= 0) return;

                if (Application.Current.TryFindResource("Accent") is not SolidColorBrush accent) return;

                // 読む邪魔にならないよう、細く薄い枠だけ。塗りはほんの少し
                var pen = new Pen(new SolidColorBrush(accent.Color) { Opacity = 0.45 }, 1);
                pen.Freeze();

                var fill = new SolidColorBrush(accent.Color) { Opacity = 0.05 };
                fill.Freeze();

                dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
                dc.DrawRoundedRectangle(fill, pen,
                    new Rect(bounds.X + 0.5, bounds.Y + 0.5, Math.Max(1, bounds.Width - 1), bounds.Height), 3, 3);
                dc.Pop();
            }
            catch (Exception ex)
            {
                Core.Stage.Log("diff block marker: " + ex.Message);
            }
        }
    }
}
