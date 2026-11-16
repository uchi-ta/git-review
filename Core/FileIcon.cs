using System.IO;
using System.Windows.Media;

namespace GitReview.Core;

/// <summary>
/// A glyph and a colour per file type for the changed-file list. The shapes are stroked
/// outlines in one consistent style, and the colours are mid-tone
/// so they read on both the light and the dark palette.
/// </summary>
internal static class FileIcon
{
    // Shapes, drawn in a 16x16 box.
    private const string Chevrons = "M5.4,4.6 L2,8 L5.4,11.4 M10.6,4.6 L14,8 L10.6,11.4";
    private const string Markup = "M5.2,4.6 L1.8,8 L5.2,11.4 M10.8,4.6 L14.2,8 L10.8,11.4 M9.4,3.2 L6.6,12.8";
    private const string Braces = "M6.2,2.8 C4.6,2.8 5.1,7 3.4,8 C5.1,9 4.6,13.2 6.2,13.2 "
                                + "M9.8,2.8 C11.4,2.8 10.9,7 12.6,8 C10.9,9 11.4,13.2 9.8,13.2";
    private const string Terminal = "M2.6,3.8 L6.6,8 L2.6,12.2 M8.4,12.4 L13.4,12.4";
    private const string Droplet = "M8,1.8 C8,1.8 3.6,7 3.6,10 A4.4,4.4 0 0 0 12.4,10 C12.4,7 8,1.8 8,1.8 Z";
    private const string Picture = "M2.2,3.6 L13.8,3.6 L13.8,12.4 L2.2,12.4 Z "
                                 + "M2.2,10.2 L6,6.4 L9.4,9.8 L11.2,8 L13.8,10.6";
    private const string Cube = "M2,6.4 L8,3.4 L14,6.4 L8,9.4 Z M2,6.4 L2,11 L8,14 L14,11 L14,6.4 M8,9.4 L8,14";
    private const string Page = "M4,1.8 L10,1.8 L12.4,4.2 L12.4,14.2 L4,14.2 Z M10,1.8 L10,4.2 L12.4,4.2 "
                              + "M6,7.4 L10.4,7.4 M6,10 L10.4,10";
    private const string Document = "M4,1.8 L10,1.8 L12.4,4.2 L12.4,14.2 L4,14.2 Z M10,1.8 L10,4.2 L12.4,4.2";

    public const string Folder = "M1.8,3.6 L6.2,3.6 L7.8,5.6 L14.2,5.6 L14.2,12.6 L1.8,12.6 Z";

    private static readonly Dictionary<string, (string Glyph, string Color)> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // compiled languages
            ["cs"] = (Chevrons, "#7FA7D9"),
            ["vb"] = (Chevrons, "#7FA7D9"),
            ["fs"] = (Chevrons, "#7FA7D9"),
            ["java"] = (Chevrons, "#7FA7D9"),
            ["kt"] = (Chevrons, "#7FA7D9"),
            ["go"] = (Chevrons, "#5BC0DE"),
            ["rs"] = (Chevrons, "#D9885F"),
            ["c"] = (Chevrons, "#8FA3B8"),
            ["h"] = (Chevrons, "#8FA3B8"),
            ["cpp"] = (Chevrons, "#8FA3B8"),
            ["hpp"] = (Chevrons, "#8FA3B8"),

            // scripting languages
            ["py"] = (Chevrons, "#6FBF7F"),
            ["rb"] = (Chevrons, "#DF5B52"),
            ["php"] = (Chevrons, "#B596F5"),
            ["lua"] = (Chevrons, "#7AA2F7"),

            ["js"] = (Chevrons, "#E8B54A"),
            ["mjs"] = (Chevrons, "#E8B54A"),
            ["cjs"] = (Chevrons, "#E8B54A"),
            ["ts"] = (Chevrons, "#4E9BD5"),
            ["jsx"] = (Markup, "#E8B54A"),
            ["tsx"] = (Markup, "#4E9BD5"),

            // markup
            ["html"] = (Markup, "#D9885F"),
            ["htm"] = (Markup, "#D9885F"),
            ["cshtml"] = (Markup, "#D9885F"),
            ["razor"] = (Markup, "#D9885F"),
            ["vue"] = (Markup, "#6FBF7F"),
            ["svelte"] = (Markup, "#DF5B52"),
            ["xml"] = (Markup, "#9AA4B2"),
            ["xaml"] = (Markup, "#B596F5"),
            ["svg"] = (Picture, "#E8B54A"),

            // styles
            ["css"] = (Droplet, "#7AA2F7"),
            ["scss"] = (Droplet, "#D96BA0"),
            ["sass"] = (Droplet, "#D96BA0"),
            ["less"] = (Droplet, "#5B7FBF"),

            // data and configuration
            ["json"] = (Braces, "#E8B54A"),
            ["yaml"] = (Braces, "#4EC9B0"),
            ["yml"] = (Braces, "#4EC9B0"),
            ["toml"] = (Braces, "#4EC9B0"),
            ["ini"] = (Braces, "#9AA4B2"),
            ["config"] = (Braces, "#9AA4B2"),
            ["csproj"] = (Braces, "#B596F5"),
            ["sln"] = (Braces, "#B596F5"),
            ["props"] = (Braces, "#B596F5"),
            ["targets"] = (Braces, "#B596F5"),
            ["sql"] = (Braces, "#5BC0DE"),
            ["csv"] = (Braces, "#6FBF7F"),

            // shells
            ["ps1"] = (Terminal, "#7AA2F7"),
            ["psm1"] = (Terminal, "#7AA2F7"),
            ["psd1"] = (Terminal, "#7AA2F7"),
            ["sh"] = (Terminal, "#6FBF7F"),
            ["bash"] = (Terminal, "#6FBF7F"),
            ["bat"] = (Terminal, "#9AA4B2"),
            ["cmd"] = (Terminal, "#9AA4B2"),

            // documents
            ["md"] = (Page, "#8FA3B8"),
            ["markdown"] = (Page, "#8FA3B8"),
            ["txt"] = (Page, "#9AA4B2"),
            ["rst"] = (Page, "#9AA4B2"),
            ["pdf"] = (Page, "#DF5B52"),

            // images
            ["png"] = (Picture, "#D9A08A"),
            ["jpg"] = (Picture, "#D9A08A"),
            ["jpeg"] = (Picture, "#D9A08A"),
            ["gif"] = (Picture, "#D9A08A"),
            ["bmp"] = (Picture, "#D9A08A"),
            ["webp"] = (Picture, "#D9A08A"),
            ["ico"] = (Picture, "#E8B54A"),

            // binaries and archives
            ["dll"] = (Cube, "#B8A7CE"),
            ["exe"] = (Cube, "#B8A7CE"),
            ["so"] = (Cube, "#B8A7CE"),
            ["zip"] = (Cube, "#C0A6E8"),
            ["7z"] = (Cube, "#C0A6E8"),
            ["gz"] = (Cube, "#C0A6E8"),
            ["bin"] = (Cube, "#B8A7CE"),
        };

    private static readonly Dictionary<string, Brush> Brushes = [];

    public static (string Glyph, Brush Brush) For(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.');

        var (glyph, color) = ByExtension.TryGetValue(extension, out var known)
            ? known
            : (Document, "#9AA4B2");

        return (glyph, BrushFor(color));
    }

    public static Brush FolderBrush => BrushFor("#B8A7CE");

    private static Brush BrushFor(string hex)
    {
        if (Brushes.TryGetValue(hex, out var brush)) return brush;

        brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        Brushes[hex] = brush;
        return brush;
    }
}
