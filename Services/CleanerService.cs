using System.Management;
using System.Security.Cryptography;
using EnumerationOptions = System.IO.EnumerationOptions;

namespace TuneUp.Services;

public enum CleanKind { Files, RecycleBin, WindowsOld }

/// <summary>One thing the cleaner can empty. Sizes are always measured before anything is deleted.</summary>
public sealed class CleanCategory
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Warning { get; init; } = "";
    public bool DefaultOn { get; init; }
    public CleanKind Kind { get; init; } = CleanKind.Files;
    /// <summary>Folders whose contents (never the folder itself) are removed. Wildcards: "*" = any one folder.</summary>
    public string[] Roots { get; init; } = Array.Empty<string>();
    /// <summary>Only delete matching file names (e.g. "thumbcache_*.db"). Null = all files.</summary>
    public string? Pattern { get; init; }
    /// <summary>Skip files modified more recently than this (protects in-use installer temp files).</summary>
    public int MinAgeHours { get; init; }

    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
}

public sealed record BigFile(string Path, long Size, DateTime Modified);
public sealed record DuplicateGroup(long Size, List<string> Files);
public sealed record FolderSize(string Path, long Size);
public sealed record GameInstall(string Name, string Launcher, string Folder, long Size, string Drive, string DriveKind);

/// <summary>
/// Junk cleaner and disk tools. Rules: sizes are shown first, only the listed folders are ever
/// touched, files in use are skipped (never forced), and the honest truth is stated up front:
/// cleaning frees space, it does not make games faster unless the drive is nearly full.
/// </summary>
public sealed class CleanerService
{
    private static string L(string p) => Environment.ExpandEnvironmentVariables(p);
    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string Sys = Path.GetPathRoot(Win) ?? "C:\\";

    public static List<CleanCategory> Categories() => new()
    {
        new() { Id = "temp", Name = "Temporary files", DefaultOn = true, MinAgeHours = 24,
                Description = "Leftovers from installers and apps in your Temp folders. Files from the last 24 hours are kept in case something is still using them.",
                Roots = new[] { L("%TEMP%"), Path.Combine(Win, "Temp") } },
        new() { Id = "wu", Name = "Windows Update download cache", DefaultOn = true, MinAgeHours = 24,
                Description = "Update files Windows already installed. Windows downloads them again if it ever needs them.",
                Roots = new[] { Path.Combine(Win, "SoftwareDistribution", "Download") } },
        new() { Id = "do", Name = "Delivery Optimization cache", DefaultOn = true,
                Description = "Update pieces Windows keeps to share with other PCs. Safe to remove.",
                Roots = new[] { Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") } },
        new() { Id = "dumps", Name = "Crash dumps and error reports", DefaultOn = true,
                Description = "Memory dumps written when an app or Windows crashed, and queued error reports. Only useful to developers.",
                Roots = new[] { Path.Combine(Win, "Minidump"), L(@"%LOCALAPPDATA%\CrashDumps"), L(@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive"), L(@"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue") } },
        new() { Id = "thumbs", Name = "Thumbnail cache", DefaultOn = false, Pattern = "thumbcache_*.db",
                Description = "Picture previews in File Explorer. They are rebuilt automatically (folders may show blank previews briefly). Files Explorer is using are skipped.",
                Roots = new[] { L(@"%LOCALAPPDATA%\Microsoft\Windows\Explorer") } },
        new() { Id = "browsers", Name = "Browser caches (Chrome, Edge, Brave, Firefox, Opera GX)", DefaultOn = false,
                Description = "Saved copies of web pages and images. Sites load a little slower the first time afterwards. Passwords, cookies, history and bookmarks are NOT touched. Close your browsers first.",
                Roots = new[]
                {
                    L(@"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache"), L(@"%LOCALAPPDATA%\Google\Chrome\User Data\*\Code Cache"), L(@"%LOCALAPPDATA%\Google\Chrome\User Data\*\GPUCache"),
                    L(@"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Cache"), L(@"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\Code Cache"), L(@"%LOCALAPPDATA%\Microsoft\Edge\User Data\*\GPUCache"),
                    L(@"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\*\Cache"), L(@"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\*\Code Cache"),
                    L(@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2"),
                    L(@"%LOCALAPPDATA%\Opera Software\Opera GX Stable\Cache"), L(@"%APPDATA%\Opera Software\Opera GX Stable\Code Cache")
                } },
        new() { Id = "shaders", Name = "Graphics shader caches (NVIDIA, AMD, DirectX)", DefaultOn = false,
                Description = "Pre-compiled shaders that make games load and run smoothly.",
                Warning = "After clearing, games stutter for a while the first time you play them again while shaders rebuild. Only clear after a driver problem or to free space.",
                Roots = new[] { L(@"%LOCALAPPDATA%\NVIDIA\DXCache"), L(@"%LOCALAPPDATA%\NVIDIA\GLCache"), L(@"%LOCALAPPDATA%\NVIDIA Corporation\NV_Cache"),
                                L(@"%LOCALAPPDATA%\AMD\DxCache"), L(@"%LOCALAPPDATA%\AMD\DxcCache"), L(@"%LOCALAPPDATA%\AMD\VkCache"), L(@"%LOCALAPPDATA%\D3DSCache") } },
        new() { Id = "recycle", Name = "Recycle Bin", DefaultOn = false, Kind = CleanKind.RecycleBin,
                Description = "Files you deleted earlier.",
                Warning = "Emptying the Recycle Bin deletes those files permanently. Check it for anything you still want first." },
        new() { Id = "winold", Name = "Previous Windows installation (Windows.old)", DefaultOn = false, Kind = CleanKind.WindowsOld,
                Description = "Kept for 10 days after a big Windows update so you can go back. Removing it is done by Windows' own Disk Cleanup, which Vanta opens for you.",
                Warning = "After removing it you can no longer roll back to the previous Windows version.",
                Roots = new[] { Path.Combine(Sys, "Windows.old") } },
    };

    // ---- Measure -----------------------------------------------------------------------------------
    public Task MeasureAsync(IEnumerable<CleanCategory> cats, CancellationToken ct) => Task.Run(() =>
    {
        foreach (var c in cats)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (c.Kind == CleanKind.RecycleBin)
                {
                    var info = new NativeMethods.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
                    NativeMethods.SHQueryRecycleBin(null, ref info);
                    c.SizeBytes = info.i64Size;
                    c.FileCount = (int)info.i64NumItems;
                    continue;
                }
                long size = 0; var count = 0;
                foreach (var f in Files(c, ct)) { size += f.Length; count++; }
                c.SizeBytes = size;
                c.FileCount = count;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLog.Warn($"Measuring {c.Id}: {ex.Message}"); }
        }
    }, ct);

    private static IEnumerable<FileInfo> Files(CleanCategory c, CancellationToken ct)
    {
        var cutoff = DateTime.Now.AddHours(-c.MinAgeHours);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        foreach (var root in ExpandRoots(c.Roots))
        {
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(root).EnumerateFiles(c.Pattern ?? "*", opts); }
            catch { continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (c.MinAgeHours > 0 && f.LastWriteTime > cutoff) continue;
                yield return f;
            }
        }
    }

    /// <summary>Expands "*" segments (browser profiles) and drops folders that don't exist.</summary>
    private static IEnumerable<string> ExpandRoots(IEnumerable<string> roots)
    {
        foreach (var r in roots)
        {
            var star = r.IndexOf("\\*\\", StringComparison.Ordinal);
            if (star < 0) { if (Directory.Exists(r)) yield return r; continue; }
            var parent = r[..star];
            var rest = r[(star + 3)..];
            if (!Directory.Exists(parent)) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(parent); } catch { continue; }
            foreach (var s in subs)
            {
                var full = Path.Combine(s, rest);
                if (Directory.Exists(full)) yield return full;
            }
        }
    }

    // ---- Clean ---------------------------------------------------------------------------------------
    public Task<(long Freed, int Deleted, int Skipped)> CleanAsync(IEnumerable<CleanCategory> cats, IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        long freed = 0; int deleted = 0, skipped = 0;
        foreach (var c in cats)
        {
            progress?.Report($"Cleaning: {c.Name}");
            switch (c.Kind)
            {
                case CleanKind.RecycleBin:
                    var before = c.SizeBytes;
                    if (NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null,
                            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND) == 0)
                    { freed += before; deleted += c.FileCount; }
                    continue;
                case CleanKind.WindowsOld:
                    // Windows.old is owned by TrustedInstaller; Windows' own tool removes it properly.
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cleanmgr.exe", $"/d {Sys[0]}") { UseShellExecute = true }); }
                    catch (Exception ex) { AppLog.Warn("cleanmgr: " + ex.Message); }
                    continue;
            }

            foreach (var f in Files(c, ct).ToList())
            {
                try
                {
                    var len = f.Length;
                    f.Attributes = FileAttributes.Normal;
                    f.Delete();
                    freed += len;
                    deleted++;
                }
                catch { skipped++; } // in use or protected: leave it
            }

            // Remove now-empty sub folders, never the category root itself.
            foreach (var root in ExpandRoots(c.Roots))
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { /* in use */ }
                }
                catch { /* unreadable */ }
            }
        }
        AppLog.Info($"Cleaner freed {freed / 1048576.0:0} MB ({deleted} files, {skipped} skipped)");
        return (freed, deleted, skipped);
    }, ct);

    // ---- Big files / duplicates / breakdown ------------------------------------------------------------
    public static Task<List<BigFile>> FindBigFilesAsync(string root, long minBytes, int top, IProgress<int>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var heap = new PriorityQueue<BigFile, long>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var seen = 0;
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (++seen % 5000 == 0) progress?.Report(seen);
            long len;
            try { len = f.Length; } catch { continue; }
            if (len < minBytes) continue;
            heap.Enqueue(new BigFile(f.FullName, len, f.LastWriteTime), len);
            if (heap.Count > top) heap.Dequeue();
        }
        var list = new List<BigFile>();
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }, ct);

    public static Task<List<DuplicateGroup>> FindDuplicatesAsync(string root, long minBytes, IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        progress?.Report("Listing files...");
        var bySize = new Dictionary<long, List<string>>();
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            long len;
            try { len = f.Length; } catch { continue; }
            if (len < minBytes) continue;
            if (!bySize.TryGetValue(len, out var l)) bySize[len] = l = new List<string>();
            l.Add(f.FullName);
        }

        // Same size -> compare a 64 KB head hash -> confirm with a full hash. Fast and exact.
        var groups = new List<DuplicateGroup>();
        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        var i = 0;
        foreach (var (size, files) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Comparing files ({++i}/{candidates.Count})...");
            foreach (var headGroup in files.GroupBy(f => Hash(f, 65536)).Where(g => g.Key != null && g.Count() > 1))
                foreach (var full in headGroup.GroupBy(f => Hash(f, long.MaxValue)).Where(g => g.Key != null && g.Count() > 1))
                    groups.Add(new DuplicateGroup(size, full.ToList()));
        }
        return groups.OrderByDescending(g => g.Size * (g.Files.Count - 1)).ToList();
    }, ct);

    private static string? Hash(string path, long maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var sha = SHA256.Create();
            if (maxBytes == long.MaxValue) return Convert.ToHexString(sha.ComputeHash(fs));
            var buf = new byte[maxBytes];
            var n = fs.Read(buf, 0, buf.Length);
            return Convert.ToHexString(sha.ComputeHash(buf, 0, n));
        }
        catch { return null; }
    }

    public static Task<List<FolderSize>> BreakdownAsync(string root, CancellationToken ct) => Task.Run(() =>
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var result = new List<FolderSize>();
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root).ToList(); } catch { return result; }
        foreach (var d in dirs)
        {
            ct.ThrowIfCancellationRequested();
            long size = 0;
            try { foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", opts)) { try { size += f.Length; } catch { } } } catch { }
            result.Add(new FolderSize(d, size));
        }
        long loose = 0;
        try { foreach (var f in new DirectoryInfo(root).EnumerateFiles()) { try { loose += f.Length; } catch { } } } catch { }
        if (loose > 0) result.Add(new FolderSize(Path.Combine(root, "(files in this folder)"), loose));
        return result.OrderByDescending(r => r.Size).ToList();
    }, ct);

    public static void SendToRecycleBin(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    // ---- Drives & games ------------------------------------------------------------------------------
    /// <summary>Drive letter -> "NVMe SSD" / "SSD" / "HDD", via Windows' storage provider.</summary>
    public static Dictionary<char, string> DriveKinds()
    {
        var map = new Dictionary<char, string>();
        try
        {
            var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
            var kinds = new Dictionary<string, string>();
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk")))
                foreach (ManagementObject d in s.Get())
                {
                    var bus = Convert.ToInt32(d["BusType"] ?? 0);
                    var media = Convert.ToInt32(d["MediaType"] ?? 0);
                    kinds[d["DeviceId"]?.ToString() ?? ""] = bus == 17 ? "NVMe SSD" : media == 4 ? "SSD" : media == 3 ? "HDD" : "Drive";
                }
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition")))
                foreach (ManagementObject p in s.Get())
                {
                    var letter = Convert.ToChar(Convert.ToUInt16(p["DriveLetter"] ?? 0));
                    if (letter == '\0') continue;
                    if (kinds.TryGetValue(p["DiskNumber"]?.ToString() ?? "", out var k)) map[char.ToUpperInvariant(letter)] = k;
                }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Drive type lookup failed: " + ex.Message);
        }
        return map;
    }

    public static Task<List<GameInstall>> GameSizesAsync(IEnumerable<TuneUp.Models.GameEntry> games, CancellationToken ct) => Task.Run(() =>
    {
        var kinds = DriveKinds();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var list = new List<GameInstall>();
        foreach (var g in games.Where(g => g.InstallDir != null && Directory.Exists(g.InstallDir)))
        {
            ct.ThrowIfCancellationRequested();
            long size = 0;
            try { foreach (var f in new DirectoryInfo(g.InstallDir!).EnumerateFiles("*", opts)) { try { size += f.Length; } catch { } } } catch { }
            var drive = char.ToUpperInvariant(g.InstallDir![0]);
            list.Add(new GameInstall(g.Name, g.Launcher, g.InstallDir, size, $"{drive}:", kinds.TryGetValue(drive, out var k) ? k : "Unknown"));
        }
        return list.OrderByDescending(x => x.Size).ToList();
    }, ct);

    /// <summary>fsutil: DisableDeleteNotify = 0 means TRIM is on.</summary>
    public static async Task<string> TrimStatusAsync()
    {
        var r = await ProcessRunner.RunAsync("fsutil.exe", "behavior query DisableDeleteNotify", 15_000);
        if (r.StdOut.Contains("NTFS DisableDeleteNotify = 0")) return "TRIM is ON for SSDs (good).";
        if (r.StdOut.Contains("NTFS DisableDeleteNotify = 1")) return "TRIM is OFF. SSDs slowly lose speed without it; run 'fsutil behavior set DisableDeleteNotify 0' as admin to turn it back on.";
        return "Could not read the TRIM setting (needs admin).";
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" :
        bytes >= 1L << 10 ? $"{bytes / 1024.0:0} KB" : $"{bytes} B";
}
