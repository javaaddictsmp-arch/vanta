using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>Reads/writes boost-state.json. Written after every step, so a crash mid-boost is recoverable.</summary>
public static class BoostStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static void Save(BoostState state)
    {
        lock (Gate)
        {
            try
            {
                var tmp = AppPaths.BoostStateFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
                File.Move(tmp, AppPaths.BoostStateFile, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not save boost state", ex);
            }
        }
    }

    public static BoostState? Load()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(AppPaths.BoostStateFile)
                    ? JsonSerializer.Deserialize<BoostState>(File.ReadAllText(AppPaths.BoostStateFile))
                    : null;
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not read boost state", ex);
                return null;
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { File.Delete(AppPaths.BoostStateFile); } catch { /* ignore */ }
        }
    }
}

/// <summary>
/// Puts back everything a boost changed. Used on normal boost end, by the watchdog process when
/// Vanta was killed, and on the next Vanta start if both were killed (e.g. power loss).
/// Each step is independent: one failure never stops the others.
/// </summary>
public static class BoostRestorer
{
    public static async Task RestoreAsync(BoostState state, string reason)
    {
        AppLog.Info($"Restoring boost for {state.GameName} ({reason})");

        foreach (var p in state.Suspended)
            Step($"resume {p.Name}", () =>
            {
                if (ProcessUtil.IsSameProcess(p)) ProcessUtil.Resume(p.Pid);
            });

        foreach (var p in state.PriorityChanges)
            Step($"priority {p.Name}", () =>
            {
                if (!ProcessUtil.IsSameProcess(p)) return;
                var prio = ProcessUtil.ParsePriority(p.OriginalPriority);
                if (prio == null) return;
                using var proc = Process.GetProcessById(p.Pid);
                ProcessUtil.TrySetPriority(proc, prio.Value);
            });

        foreach (var p in state.Closed)
            Step($"reopen {p.Name}", () =>
            {
                if (p.Path == null || !File.Exists(p.Path)) return;
                if (Process.GetProcessesByName(p.Name).Length > 0) return; // already back
                Process.Start(new ProcessStartInfo(p.Path) { UseShellExecute = true });
            });

        foreach (var svc in state.StoppedServices)
            Step($"start service {svc}", () =>
            {
                using var sc = new ServiceController(svc);
                if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                    sc.Start();
            });

        if (state.OriginalPowerPlan != null)
            await StepAsync("power plan", () => new PowerPlanService().SetActiveAsync(state.OriginalPowerPlan));

        Step("accessibility shortcuts", () => KeyboardGuard.RestoreAccessibility(state));

        if (state.OriginalToastEnabled != null)
            Step("notifications", () =>
            {
                const string path = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
                if (state.OriginalToastEnabled == -1) RegistryService.DeleteValue("HKCU", path, "ToastEnabled");
                else RegistryService.Write("HKCU", path, "ToastEnabled", Microsoft.Win32.RegistryValueKind.DWord,
                    state.OriginalToastEnabled.Value.ToString());
            });

        state.Active = false;
        BoostStateStore.Clear();
        AppLog.Info("Boost restore finished");
    }

    /// <summary>Called at app start: restores a boost left behind by a crash or power loss.</summary>
    public static async Task<string?> RestoreStaleAsync()
    {
        var state = BoostStateStore.Load();
        if (state is not { Active: true }) return null;

        // Another live Vanta owns it (e.g. a second window was opened): leave it alone.
        if (state.TuneUpPid != Environment.ProcessId && IsVanta(state.TuneUpPid)) return null;

        await RestoreAsync(state, "left over from a previous session");
        return $"Vanta restored settings from an unfinished boost of {state.GameName}.";
    }

    private static bool IsVanta(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("Vanta", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void Step(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { AppLog.Warn($"Restore step '{what}' failed: {ex.Message}"); }
    }

    private static async Task StepAsync(string what, Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) { AppLog.Warn($"Restore step '{what}' failed: {ex.Message}"); }
    }
}

/// <summary>
/// Blocks the Windows key while the game window is in front (a global low-level keyboard hook:
/// no injection into the game) and turns off the Sticky/Filter/Toggle Keys shortcut prompts.
/// The hook dies with Vanta; the accessibility flags are restored from the boost state.
/// </summary>
public sealed class KeyboardGuard : IDisposable
{
    private IntPtr _hook;
    private NativeMethods.LowLevelKeyboardProc? _proc; // keep a reference so the GC can't collect it
    private Func<int, bool>? _isGamePid;

    /// <summary>Must be called on a thread with a message loop (the UI thread).</summary>
    public void BlockWindowsKey(Func<int, bool> isGamePid)
    {
        if (_hook != IntPtr.Zero) return;
        _isGamePid = isGamePid;
        _proc = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) AppLog.Warn($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
    }

    public void Unblock()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            if (vk is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN)
            {
                NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var pid);
                if (_isGamePid?.Invoke(pid) == true) return (IntPtr)1; // swallow
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Turns off the "press Shift 5 times" style prompts unless the feature itself is in use.</summary>
    public static void DisableAccessibilityShortcuts(BoostState state)
    {
        var sticky = new NativeMethods.FLAGSONLY { cbSize = 8 };
        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETSTICKYKEYS, 8, ref sticky, 0) &&
            (sticky.dwFlags & NativeMethods.FEATURE_ON) == 0)
        {
            state.StickyKeysFlags = sticky.dwFlags;
            sticky.dwFlags &= ~NativeMethods.HOTKEYACTIVE;
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETSTICKYKEYS, 8, ref sticky, 0);
        }

        var toggle = new NativeMethods.FLAGSONLY { cbSize = 8 };
        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETTOGGLEKEYS, 8, ref toggle, 0) &&
            (toggle.dwFlags & NativeMethods.FEATURE_ON) == 0)
        {
            state.ToggleKeysFlags = toggle.dwFlags;
            toggle.dwFlags &= ~NativeMethods.HOTKEYACTIVE;
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETTOGGLEKEYS, 8, ref toggle, 0);
        }

        var size = (uint)Marshal.SizeOf<NativeMethods.FILTERKEYS>();
        var filter = new NativeMethods.FILTERKEYS { cbSize = size };
        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETFILTERKEYS, size, ref filter, 0) &&
            (filter.dwFlags & NativeMethods.FEATURE_ON) == 0)
        {
            state.FilterKeysFlags = filter.dwFlags;
            filter.dwFlags &= ~NativeMethods.HOTKEYACTIVE;
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETFILTERKEYS, size, ref filter, 0);
        }
    }

    public static void RestoreAccessibility(BoostState state)
    {
        if (state.StickyKeysFlags is { } s)
        {
            var v = new NativeMethods.FLAGSONLY { cbSize = 8, dwFlags = s };
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETSTICKYKEYS, 8, ref v, 0);
        }
        if (state.ToggleKeysFlags is { } t)
        {
            var v = new NativeMethods.FLAGSONLY { cbSize = 8, dwFlags = t };
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETTOGGLEKEYS, 8, ref v, 0);
        }
        if (state.FilterKeysFlags is { } f)
        {
            var size = (uint)Marshal.SizeOf<NativeMethods.FILTERKEYS>();
            var v = new NativeMethods.FILTERKEYS { cbSize = size };
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETFILTERKEYS, size, ref v, 0);
            v.dwFlags = f;
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETFILTERKEYS, size, ref v, 0);
        }
    }

    public void Dispose() => Unblock();
}
