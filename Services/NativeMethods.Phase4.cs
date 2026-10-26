using System.Runtime.InteropServices;

namespace TuneUp.Services;

/// <summary>P/Invoke used by Phase 4 (cleaning, RAM, processes, hotkey).</summary>
internal static partial class NativeMethods
{
    // ---- Recycle Bin ---------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    internal const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    // ---- Working sets ----------------------------------------------------------------------------
    internal const uint PROCESS_SET_QUOTA = 0x0100;

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(IntPtr process);

    // ---- I/O priority (ProcessIoPriority = 33; 0 very low, 1 low, 2 normal) ------------------------
    internal const int ProcessIoPriority = 33;

    [DllImport("ntdll.dll")]
    internal static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref int info, int length, out int returnLength);

    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;

    // ---- Memory lists (RAM cleaner) -----------------------------------------------------------------
    // SYSTEM_MEMORY_LIST_COMMAND for NtSetSystemInformation(SystemMemoryListInformation = 80, ...):
    // 2 = trim every working set on the system, including Windows' compressed-memory store
    // (MemCompression), which no other API can reach. What Mem Reduct / ExitLag-style cleaners use.
    internal const int MemoryEmptyWorkingSets = 2;
    internal const int MemoryFlushModifiedList = 3;
    internal const int MemoryPurgeLowPriorityStandbyList = 5;

    /// <summary>With (SIZE_T)-1 for both sizes: trims the system file cache's working set. Needs SeIncreaseQuotaPrivilege.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetSystemFileCacheSize(UIntPtr minimumFileCacheSize, UIntPtr maximumFileCacheSize, uint flags);
    // Windows 10+: merge identical physical pages (what Mem Reduct calls "combine memory lists").
    internal const int SystemCombinePhysicalMemoryInformation = 130;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_COMBINE_INFORMATION_EX
    {
        public IntPtr Handle;
        public UIntPtr PagesCombined;
        public uint Flags;
    }

    [DllImport("ntdll.dll")]
    internal static extern int NtSetSystemInformation(int infoClass, ref MEMORY_COMBINE_INFORMATION_EX info, int length);

    /// <summary>
    /// SystemMemoryListInformation (80) returns SYSTEM_MEMORY_LIST_INFORMATION: 22 ULONG_PTR page counts
    /// (Zero, Free, Modified, ModifiedNoWrite, Bad, PageCountByPriority[8] = standby, Repurposed[8], ModifiedPageFile).
    /// </summary>
    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    // ---- Global hotkey -----------------------------------------------------------------------------
    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
