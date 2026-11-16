using System.IO;
using Microsoft.Win32;

namespace GitReview.Core;

/// <summary>
/// Registers "GitReview で開く" on folders in Explorer.
///
/// Everything lives under HKCU, so no elevation is needed and removing it affects only the
/// current user. Windows 11 lists entries registered this way under
/// 「その他のオプションを表示」 (Shift+F10 shows that menu directly) — the new top-level menu
/// only accepts MSIX-packaged IExplorerCommand handlers, which a single EXE on a share
/// cannot provide.
/// </summary>
internal static class ShellMenu
{
    private const string EntryName = "GitReview";
    private const string Label = "GitReview で開く";

    /// <summary>
    /// Right-clicking a folder, and right-clicking the empty area inside one. Both hand the
    /// folder over as %V; %1 is not filled in for the Background case.
    /// </summary>
    private static readonly string[] Parents =
    [
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Directory\Background\shell",
    ];

    public static void Register(string exePath, string? iconPath)
    {
        if (!File.Exists(exePath)) return;

        var command = $"\"{exePath}\" \"%V\"";
        var icon = iconPath is not null && File.Exists(iconPath) ? iconPath : exePath;

        foreach (var parent in Parents)
        {
            try
            {
                if (IsCurrent(parent, command, icon)) continue;

                using var key = Registry.CurrentUser.CreateSubKey($@"{parent}\{EntryName}");
                if (key is null) continue;

                key.SetValue(null, Label);
                key.SetValue("Icon", icon);

                using var commandKey = key.CreateSubKey("command");
                commandKey?.SetValue(null, command);

                Stage.Log($"explorer menu registered under {parent}");
            }
            catch (Exception ex)
            {
                Stage.Log($"explorer menu: {ex.Message}");
            }
        }
    }

    public static void Unregister()
    {
        foreach (var parent in Parents)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey($@"{parent}\{EntryName}"))
                    if (key is null) continue;

                Registry.CurrentUser.DeleteSubKeyTree($@"{parent}\{EntryName}", throwOnMissingSubKey: false);
                Stage.Log($"explorer menu removed from {parent}");
            }
            catch (Exception ex)
            {
                Stage.Log($"explorer menu remove: {ex.Message}");
            }
        }
    }

    /// <summary>True when the entry already says exactly what we would write.</summary>
    private static bool IsCurrent(string parent, string command, string icon)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{parent}\{EntryName}");
            if (key is null) return false;

            if (key.GetValue(null) as string != Label) return false;
            if (key.GetValue("Icon") as string != icon) return false;

            using var commandKey = key.OpenSubKey("command");
            return commandKey?.GetValue(null) as string == command;
        }
        catch
        {
            return false;
        }
    }
}
