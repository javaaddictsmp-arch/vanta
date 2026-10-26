using System.Runtime.InteropServices;
using System.Text;

namespace TuneUp.Services;

/// <summary>P/Invoke used by the FPS Boost Center (Phase 2).</summary>
internal static partial class NativeMethods
{
    // ---- Process access ---------------------------------------------------------------------
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_SUSPEND_RESUME = 0x0800;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>Works with PROCESS_QUERY_LIMITED_INFORMATION, so it also reads most protected/anti-cheat games.</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    // ntdll: suspend / resume a whole process (what Resource Monitor's "Suspend process" uses).
    [DllImport("ntdll.dll")]
    internal static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    internal static extern int NtResumeProcess(IntPtr processHandle);

    // ---- Timer resolution (units of 100 ns: 5000 = 0.5 ms, 156250 = 15.625 ms) --------------
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);

    [DllImport("ntdll.dll")]
    internal static extern int NtSetTimerResolution(uint desiredResolution, [MarshalAs(UnmanagedType.U1)] bool setResolution, out uint currentResolution);

    // ---- Standby list purge -------------------------------------------------------------------
    // NtSetSystemInformation(SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4).
    // Needs SeProfileSingleProcessPrivilege (admins have it, but it must be enabled first).
    internal const int SystemMemoryListInformation = 80;
    internal const int MemoryPurgeStandbyList = 4;

    [DllImport("ntdll.dll")]
    internal static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    internal const uint TOKEN_QUERY = 0x8;
    internal const uint SE_PRIVILEGE_ENABLED = 0x2;

    internal static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                   && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ---- Firmware type (Phase 3): 1 = legacy BIOS, 2 = UEFI ----------------------------------
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFirmwareType(out int firmwareType);

    // ---- CPU topology -----------------------------------------------------------------------
    internal const int RelationProcessorCore = 0;
    internal const int RelationCache = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    // ---- Foreground window / keyboard hook ---------------------------------------------------
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    internal const int WH_KEYBOARD_LL = 13;
    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    // ---- Accessibility shortcut prompts (Sticky / Filter / Toggle Keys) ----------------------
    internal const uint SPI_GETFILTERKEYS = 0x0032;
    internal const uint SPI_SETFILTERKEYS = 0x0033;
    internal const uint SPI_GETTOGGLEKEYS = 0x0034;
    internal const uint SPI_SETTOGGLEKEYS = 0x0035;
    internal const uint SPI_GETSTICKYKEYS = 0x003A;
    internal const uint SPI_SETSTICKYKEYS = 0x003B;

    /// <summary>Bit 0x1 = feature on, 0x4 = keyboard shortcut active (5x Shift etc.), 0x8 = confirm prompt.</summary>
    internal const uint HOTKEYACTIVE = 0x4;
    internal const uint FEATURE_ON = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FLAGSONLY { public uint cbSize; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILTERKEYS
    {
        public uint cbSize; public uint dwFlags;
        public uint iWaitMSec; public uint iDelayMSec; public uint iRepeatMSec; public uint iBounceMSec;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint param, ref FLAGSONLY data, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint action, uint param, ref FILTERKEYS data, uint winIni);

    // ---- Overlay window styles ---------------------------------------------------------------
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TRANSPARENT = 0x20;
    internal const int WS_EX_TOOLWINDOW = 0x80;
    internal const int WS_EX_LAYERED = 0x80000;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    // ---- Kernel driver address lookup (for DPC/ISR attribution) ------------------------------
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDeviceDrivers([Out] IntPtr[]? imageBases, uint cb, out uint needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    internal static extern uint GetDeviceDriverBaseName(IntPtr imageBase, StringBuilder name, uint size);
}
