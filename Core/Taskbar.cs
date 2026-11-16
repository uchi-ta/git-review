using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GitReview.Core;

/// <summary>
/// Taskbar integration. The shared EXE is copied locally before it runs, so without help
/// the pinned icon and the running window would be treated as two different apps. Giving
/// both the same AppUserModelID merges them into one button.
/// </summary>
internal static class Taskbar
{
    public const string AppId = "GitReview";
    public const string LinkName = "GitReview.lnk";

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>Must run before any window is created.</summary>
    public static void SetAppId()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { /* not fatal */ }
    }

    public static Stream? OpenIconStream()
    {
        try { return Assembly.GetEntryAssembly()?.GetManifestResourceStream("GitReview.ico"); }
        catch { return null; }
    }

    /// <summary>
    /// Writes the icon next to the local entry EXE so the shortcut never points at the
    /// share. The size goes in the file name: Windows caches icons per path, so changing
    /// the icon has to change the path too.
    /// </summary>
    public static string? EnsureLocalIconFile()
    {
        try
        {
            var dir = Stage.LocalRoot;
            Directory.CreateDirectory(dir);

            using var src = OpenIconStream();
            if (src is null) return null;

            var path = Path.Combine(dir, $"GitReview-{src.Length}.ico");
            if (!File.Exists(path))
            {
                using (var dst = File.Create(path)) src.CopyTo(dst);

                foreach (var old in Directory.GetFiles(dir, "GitReview*.ico"))
                {
                    if (string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(old); } catch { /* in use by Explorer */ }
                }
            }
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static string StartMenuLinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), LinkName);

    /// <summary>
    /// Puts a shortcut in the Start menu. Pinning that to the taskbar gives one button
    /// shared with the running window.
    /// </summary>
    public static void EnsureStartMenuShortcut(string originDir)
    {
        try
        {
            // Pointing at the share warns "発行元を確認できません" on every launch.
            var target = Stage.LocalEntry;
            if (!File.Exists(target)) target = Path.Combine(originDir, Stage.ExeName);
            if (!File.Exists(target)) return;

            var lnk = StartMenuLinkPath;
            var icon = EnsureLocalIconFile();

            if (File.Exists(lnk))
            {
                ReadShortcut(lnk, out var currentTarget, out var currentIcon);
                if (string.Equals(currentTarget, target, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(currentIcon, icon ?? target, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            CreateShortcut(lnk, target, icon);
        }
        catch { /* a missing shortcut is not worth failing startup over */ }
    }

    // ---------------------------------------------------------------- IShellLink

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint props);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid id, int pid)
    {
        public Guid FormatId = id;
        public int PropertyId = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort ValueType;
        private readonly ushort _r1, _r2, _r3;
        public IntPtr Value;
        private readonly IntPtr _padding;

        public static PropVariant FromString(string s) => new()
        {
            ValueType = 31,   // VT_LPWSTR
            Value = Marshal.StringToCoTaskMemUni(s),
        };

        public void Clear()
        {
            if (Value == IntPtr.Zero) return;
            Marshal.FreeCoTaskMem(Value);
            Value = IntPtr.Zero;
        }
    }

    // PKEY_AppUserModel_ID
    private static PropertyKey AppIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    private static void ReadShortcut(string lnkPath, out string? target, out string? icon)
    {
        target = null;
        icon = null;
        try
        {
            var link = (IShellLinkW)new ShellLinkObject();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnkPath, 0);

            var path = new StringBuilder(1024);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            target = path.ToString();

            var iconPath = new StringBuilder(1024);
            link.GetIconLocation(iconPath, iconPath.Capacity, out _);
            icon = iconPath.ToString();
        }
        catch { /* treat an unreadable shortcut as absent */ }
    }

    private static void CreateShortcut(string lnkPath, string target, string? icon)
    {
        var link = (IShellLinkW)new ShellLinkObject();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        link.SetDescription("ローカル git のコードレビュー");
        link.SetIconLocation(icon ?? target, 0);

        var store = (IPropertyStore)link;
        var value = PropVariant.FromString(AppId);
        try
        {
            store.SetValue(ref AppIdKey, ref value);
            store.Commit();
        }
        finally
        {
            value.Clear();
        }

        ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnkPath, true);
    }
}
