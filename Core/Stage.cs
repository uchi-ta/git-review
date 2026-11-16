using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GitReview.Core;

/// <summary>
/// Self-copying launch and update detection.
///
/// Running the EXE straight off the share keeps the file locked, so nobody else can
/// overwrite it. Instead the shared copy stages itself into
/// %LOCALAPPDATA%\GitReview\app\&lt;version&gt;\ and runs from there. The version folder is
/// derived from the origin file's timestamp and size, so overwriting the share changes
/// the version and the running app can notice and restart itself.
/// </summary>
internal static class Stage
{
    public const string ExeName = "GitReview.exe";
    public const string OriginArg = "--origin";
    public const string NoStageArg = "--no-stage";

    /// <summary>Set once a version fails to start, so it is not retried this session.</summary>
    public static string? FailedVersion;

    public static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitReview");

    public static string AppRoot => Path.Combine(LocalRoot, "app");

    /// <summary>
    /// What the Start menu shortcut points at. Pointing it at the share would raise
    /// "発行元を確認できません" on every launch, so it lives locally.
    /// </summary>
    public static string LocalEntry => Path.Combine(LocalRoot, ExeName);

    private static string OriginFile => Path.Combine(LocalRoot, "origin.txt");

    /// <summary>Timestamp of the running EXE, shown in the title so an update is visible.</summary>
    public static string BuildStamp
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                return path is null
                    ? ""
                    : File.GetLastWriteTime(path).ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "";
            }
        }
    }

    public static string? ReadOrigin()
    {
        try
        {
            if (!File.Exists(OriginFile)) return null;
            var s = File.ReadAllText(OriginFile).Trim();
            return Directory.Exists(s) ? s : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the origin EXE to the fixed local path and remembers where it came from.
    /// By the time this runs the entry EXE has already exited, so the copy can proceed.
    /// </summary>
    public static void EnsureLocalEntry(string originDir)
    {
        try
        {
            var originExe = Path.Combine(originDir, ExeName);
            if (!File.Exists(originExe)) return;
            if (string.Equals(Path.GetFullPath(originDir).TrimEnd('\\'), LocalRoot,
                    StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(LocalRoot);
            File.WriteAllText(OriginFile, originDir.TrimEnd('\\'));

            var src = new FileInfo(originExe);
            var dst = new FileInfo(LocalEntry);
            var hashFile = Path.Combine(LocalRoot, "entry.sha256");

            // Length and timestamp can match while the content is corrupt, so check the
            // recorded hash too.
            if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc == src.LastWriteTimeUtc
                && File.Exists(hashFile) && HashFile(LocalEntry) == File.ReadAllText(hashFile).Trim())
                return;

            // Just after the entry EXE launches us it may not have exited yet.
            for (var i = 0; i < 50 && IsInUse(LocalEntry); i++) Thread.Sleep(200);

            if (!WaitUntilStable(originExe, 60)) return;

            var hash = CopyVerified(originExe, LocalEntry, 3);
            if (hash is null)
            {
                Log($"local entry: could not copy a good {ExeName}");
                return;
            }

            File.SetLastWriteTimeUtc(LocalEntry, src.LastWriteTimeUtc);
            File.WriteAllText(hashFile, hash);
            Log($"local entry updated, sha256 {hash}");
        }
        catch (Exception ex)
        {
            Log("local entry: " + ex.Message);   // in use or similar; try again next launch
        }
    }

    /// <summary>
    /// Copies with verification. A slow or flaky share can produce a copy of the right
    /// length whose content is wrong, so hash while reading, read the result back, and
    /// compare. Writes go to a neighbouring .part file and only replace the target once
    /// the hash matches, so a half-written EXE is never visible. Returns the hash, or
    /// null after <paramref name="attempts"/> failures.
    /// </summary>
    public static string? CopyVerified(string src, string dst, int attempts)
    {
        var temp = dst + ".part";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                string expected;
                using (var sha = SHA256.Create())
                using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        sha.TransformBlock(buf, 0, n, null, 0);
                        output.Write(buf, 0, n);
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    expected = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                }

                var actual = HashFile(temp);
                if (actual == expected)
                {
                    // Only swap once the copy is known good; a failed swap leaves the original.
                    if (File.Exists(dst)) File.Replace(temp, dst, null, true);
                    else File.Move(temp, dst);
                    return expected;
                }

                Log($"copy verify failed ({attempt}/{attempts}): {Path.GetFileName(dst)} " +
                    $"expected {expected[..12]} got {actual[..12]}");
            }
            catch (Exception ex)
            {
                Log($"copy failed ({attempt}/{attempts}): {ex.Message}");
            }

            try { File.Delete(temp); } catch { /* never touch the original */ }
            Thread.Sleep(2000 * attempt);
        }
        return null;
    }

    /// <summary>
    /// Copies the origin build locally and starts it. True once the new version is up,
    /// in which case the caller should exit. <paramref name="repositoryPath"/> is handed
    /// to the new build so a restart keeps looking at the same repository.
    /// </summary>
    public static bool CopyAndRun(string originDir, string? repositoryPath = null)
    {
        try
        {
            var originExe = Path.Combine(originDir, ExeName);
            if (!File.Exists(originExe)) return false;

            var version = Version(originDir);
            if (version == FailedVersion) return false;

            var target = Path.Combine(AppRoot, version);
            var targetExe = Path.Combine(target, ExeName);
            var hashFile = Path.Combine(target, "sha256.txt");
            var startedFile = Path.Combine(target, "started");

            // Use what is there if the hash matches. For a version staged before hashes
            // were recorded, accept it if it has run before, or is running right now.
            var ready = false;
            try
            {
                if (File.Exists(targetExe))
                {
                    ready = File.Exists(hashFile)
                        ? HashFile(targetExe) == File.ReadAllText(hashFile).Trim()
                        : File.Exists(startedFile) || IsInUse(targetExe);
                }
            }
            catch { /* treat as not ready */ }

            if (!ready)
            {
                if (!WaitUntilStable(originExe, 60)) return false;
                if (Version(originDir) != version)
                {
                    Log("origin changed while waiting; retrying next round");
                    return false;
                }

                Directory.CreateDirectory(target);

                var hash = CopyVerified(originExe, targetExe, 3);
                if (hash is null)
                {
                    Log($"giving up: could not copy a good {ExeName} from {originDir}");
                    return false;
                }
                File.WriteAllText(hashFile, hash);
                Log($"staged {version} sha256 {hash}");
            }

            CleanOldVersions(version);
            try { File.Delete(startedFile); } catch { /* first run */ }

            var startInfo = new ProcessStartInfo(targetExe)
            {
                UseShellExecute = false,
                WorkingDirectory = target,
            };
            startInfo.ArgumentList.Add(OriginArg);
            startInfo.ArgumentList.Add(originDir.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(repositoryPath)) startInfo.ArgumentList.Add(repositoryPath);

            var child = Process.Start(startInfo);

            // Wait for it to come up. A corrupt EXE makes the .NET host stop with an
            // unrelated dialog ("install the .NET Desktop Runtime"), so detect that.
            var deadline = DateTime.Now.AddSeconds(45);
            while (DateTime.Now < deadline)
            {
                if (File.Exists(startedFile)) return true;
                if (child is { HasExited: true })
                {
                    Log($"new version {version} exited (code {child.ExitCode}) before it started");
                    break;
                }
                Thread.Sleep(200);
            }

            if (child is { HasExited: false })
            {
                Log($"new version {version} did not start within 45s");
                try { child.Kill(); } catch { /* already gone */ }
            }

            // Drop the version so the next round copies it again.
            try { Directory.Delete(target, true); } catch { /* in use */ }
            FailedVersion = version;
            return false;
        }
        catch (Exception ex)
        {
            Log("CopyAndRun failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Tells the parent (entry EXE or previous version) that this one is up.</summary>
    public static void MarkStarted(string baseDir)
    {
        try { File.WriteAllText(Path.Combine(baseDir, "started"), DateTime.Now.ToString("o")); }
        catch { /* the parent falls back to its timeout */ }
    }

    /// <summary>Version identity of the shared build: the EXE's timestamp and size.</summary>
    public static string Version(string originDir)
    {
        try
        {
            var fi = new FileInfo(Path.Combine(originDir, ExeName));
            if (!fi.Exists) return "0";

            return fi.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture)
                 + "-" + fi.Length.ToString("x", CultureInfo.InvariantCulture);
        }
        catch
        {
            return "0";   // share unreachable; callers treat this as "no update"
        }
    }

    public static bool IsUnderAppRoot(string dir) => IsUnder(dir, AppRoot);

    public static bool IsUnderLocalRoot(string dir) => IsUnder(dir, LocalRoot);

    /// <summary>Appends to %LOCALAPPDATA%\GitReview\launch.log so failures can be traced.</summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LocalRoot);
            File.AppendAllText(
                Path.Combine(LocalRoot, "launch.log"),
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  stage: " + message + "\r\n",
                new UTF8Encoding(false));
        }
        catch { /* logging must never break startup */ }
    }

    // ---------------------------------------------------------------- helpers

    private static void CleanOldVersions(string keep)
    {
        try
        {
            foreach (var d in new DirectoryInfo(AppRoot).GetDirectories())
            {
                if (string.Equals(d.Name, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { d.Delete(true); } catch { /* in use */ }
            }
        }
        catch { /* nothing staged yet */ }
    }

    private static bool IsUnder(string dir, string root)
    {
        try
        {
            var a = Path.GetFullPath(dir).TrimEnd('\\');
            var b = Path.GetFullPath(root).TrimEnd('\\');
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInUse(string path)
    {
        // A missing file is not in use. Without this check the open below throws
        // FileNotFoundException, which is an IOException, and callers wait for nothing.
        if (!File.Exists(path)) return false;

        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until the origin EXE stops changing, so we never pick it up mid-deploy.
    /// False if it is still moving after <paramref name="seconds"/>.
    /// </summary>
    private static bool WaitUntilStable(string path, int seconds)
    {
        long lastLength = -1;
        var lastWrite = DateTime.MinValue;
        var stable = 0;

        for (var i = 0; i < seconds; i++)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;

                if (fi.Length == lastLength && fi.LastWriteTimeUtc == lastWrite)
                {
                    if (++stable >= 2) return true;
                }
                else
                {
                    stable = 0;
                    lastLength = fi.Length;
                    lastWrite = fi.LastWriteTimeUtc;
                }
            }
            catch { /* transient share hiccup */ }

            Thread.Sleep(1000);
        }

        Log($"origin {Path.GetFileName(path)} kept changing; skipping this round");
        return false;
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
