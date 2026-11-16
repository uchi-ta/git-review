using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace GitReview.Core;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Supplies the colour palette as application resources. Everything in XAML binds to
/// these with DynamicResource, so swapping the palette re-themes the live window.
/// </summary>
public static class Theme
{
    // The greys are warm-neutral and the accent is purple.
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Bg"] = "#1B1B1F",
        ["Surface"] = "#232329",
        ["SurfaceAlt"] = "#26262C",
        ["SurfaceSunken"] = "#1F1F24",
        ["Border"] = "#33333B",
        ["BorderStrong"] = "#3E3E48",

        ["Text"] = "#EDEDF2",
        ["TextMuted"] = "#9A9AA5",
        ["TextFaint"] = "#6E6E78",

        ["Accent"] = "#C0A6E8",
        ["AccentHover"] = "#D3C2F2",
        ["AccentSoft"] = "#50406B",
        ["AccentQuiet"] = "#312B3A",
        ["OnAccent"] = "#1B1B1F",
        ["PaneActive"] = "#6E5C8C",
        ["Heading"] = "#9B8FB0",

        ["Selection"] = "#312B3A",
        ["SelectionFocused"] = "#50406B",
        ["Hover"] = "#2A2A31",
        ["Success"] = "#6FBF7F",
        ["SuccessSoft"] = "#27362B",

        ["DiffAddBg"] = "#16281B",
        ["DiffAddMark"] = "#6FBF7F",
        ["DiffAddText"] = "#A5DCB0",
        ["DiffAddWord"] = "#2C5535",
        ["DiffDelBg"] = "#2B1A1C",
        ["DiffDelMark"] = "#DF5B52",
        ["DiffDelText"] = "#E9A39D",
        ["DiffDelWord"] = "#5E2B2C",
        ["DiffHunkBg"] = "#2A2536",
        ["DiffHunkText"] = "#B59BD4",
        ["NoteBg"] = "#2E2A1E",
        ["NoteText"] = "#E0B458",

        ["DiffEmptyBg"] = "#191920",
        ["GutterBg"] = "#1F1F24",
        ["GutterText"] = "#64646E",

        ["ScrollThumb"] = "#3E3E48",
        ["ScrollThumbHover"] = "#50505C",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Bg"] = "#F4F3F7",
        ["Surface"] = "#FFFFFF",
        ["SurfaceAlt"] = "#EEEDF3",
        ["SurfaceSunken"] = "#FAFAFC",
        ["Border"] = "#E2E0E8",
        ["BorderStrong"] = "#CFCCD8",

        ["Text"] = "#1C1B22",
        ["TextMuted"] = "#6B6874",
        ["TextFaint"] = "#97939F",

        ["Accent"] = "#6D4FBF",
        ["AccentHover"] = "#5B3FA8",
        ["AccentSoft"] = "#E7DFF8",
        ["AccentQuiet"] = "#F0EBFA",
        ["OnAccent"] = "#FFFFFF",
        ["PaneActive"] = "#B9A8E0",
        ["Heading"] = "#6D4FBF",

        ["Selection"] = "#F0EBFA",
        ["SelectionFocused"] = "#E1D5F6",
        ["Hover"] = "#EFEDF3",
        ["Success"] = "#2F8F45",
        ["SuccessSoft"] = "#E4F5E8",

        ["DiffAddBg"] = "#EAF8EE",
        ["DiffAddMark"] = "#3AA05A",
        ["DiffAddText"] = "#14502A",
        ["DiffAddWord"] = "#B9E9C6",
        ["DiffDelBg"] = "#FDEFEF",
        ["DiffDelMark"] = "#D9534F",
        ["DiffDelText"] = "#6E1F1C",
        ["DiffDelWord"] = "#F6C6C4",
        ["DiffHunkBg"] = "#F0ECF9",
        ["DiffHunkText"] = "#5B3FA8",
        ["NoteBg"] = "#FDF6E3",
        ["NoteText"] = "#8A6A10",

        ["DiffEmptyBg"] = "#EFEEF3",
        ["GutterBg"] = "#FAFAFC",
        ["GutterText"] = "#A3A0AB",

        ["ScrollThumb"] = "#CBC8D4",
        ["ScrollThumbHover"] = "#B2AEBF",
    };

    public static bool IsDark { get; private set; }

    /// <summary>Installs the palette for <paramref name="mode"/> into application resources.</summary>
    public static void Apply(AppTheme mode)
    {
        IsDark = mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemPrefersDark(),
        };

        var palette = IsDark ? Dark : Light;
        var resources = Application.Current.Resources;

        foreach (var (key, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;

            resources[key + "Color"] = color;

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        foreach (var window in Application.Current.Windows.OfType<Window>())
            ApplyTitleBar(window);
    }

    /// <summary>Cycles Dark → Light → System. Dark is the resting state.</summary>
    public static AppTheme Next(AppTheme mode) => mode switch
    {
        AppTheme.Dark => AppTheme.Light,
        AppTheme.Light => AppTheme.System,
        _ => AppTheme.Dark,
    };

    public static string Describe(AppTheme mode) => mode switch
    {
        AppTheme.Light => "ライト",
        AppTheme.Dark => "ダーク",
        _ => "OS に合わせる",
    };

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- title bar

    private const int DwmwaUseImmersiveDarkMode = 20;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Tints the non-client area so the title bar matches the window.</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;   // called again from SourceInitialized

            var dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            // The frame keeps its old colour until something asks it to redraw.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch { /* older Windows simply keeps the light title bar */ }
    }
}
