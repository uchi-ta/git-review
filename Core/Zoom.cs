using System.Windows;

namespace GitReview.Core;

/// <summary>
/// Text size of the panes, adjusted with Ctrl+wheel the way VS Code zooms its editor.
/// The sizes go into application resources and the templates bind them dynamically,
/// so a change takes effect without rebuilding anything.
///
/// 差分は文字サイズで、左の一覧は LayoutTransform の倍率で拡大する。一覧は文字だけ大きくしても
/// アイコンや行の間隔が付いてこないので、行ごと同じ倍率で拡大している。
/// </summary>
public static class Zoom
{
    public const double Min = 0.6;
    public const double Max = 3.0;

    private const double BodySize = 12.5;
    private const double GutterSize = 11;
    private const double BannerSize = 11.5;
    private const double GutterWidth = 44;

    public static double Scale { get; private set; } = 1;

    /// <summary>Rounded to whole percent so the status text and the steps stay tidy.</summary>
    public static int Percent => (int)Math.Round(Scale * 100);

    public static void Apply(double scale)
    {
        Scale = Math.Clamp(scale, Min, Max);

        var resources = Application.Current.Resources;
        resources["DiffFontSize"] = BodySize * Scale;
        resources["DiffGutterFontSize"] = GutterSize * Scale;
        resources["DiffBannerFontSize"] = BannerSize * Scale;

        // The line-number column has to grow too, or the numbers clip when zoomed in.
        resources["DiffGutterWidth"] = new GridLength(Math.Round(GutterWidth * Scale));

        // 左のコミット履歴と変更ファイルの一覧はこの倍率で拡大する
        resources["ListZoom"] = Scale;
    }

    /// <summary>One wheel notch or one keypress: 10% of the current size, so it feels even.</summary>
    public static double Step(double scale, int direction)
        => Math.Clamp(scale * (direction > 0 ? 1.1 : 1 / 1.1), Min, Max);
}
