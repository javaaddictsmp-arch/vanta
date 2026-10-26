using System.Text.RegularExpressions;

namespace TuneUp.Services;

public sealed record PowerPlan(string Guid, string Name, bool Active);

/// <summary>Settings Vanta writes into its own "Vanta Performance" plan.</summary>
public sealed class PerformancePlanSettings
{
    /// <summary>Minimum processor state, % (PROCTHROTTLEMIN). 100 keeps clocks pinned; 5 lets them idle.</summary>
    public int MinProcessorState { get; set; } = 5;
    public int MaxProcessorState { get; set; } = 100;
    /// <summary>0 Disabled, 1 Enabled, 2 Aggressive, 3 Efficient Enabled, 4 Efficient Aggressive (PERFBOOSTMODE).</summary>
    public int BoostMode { get; set; } = 2;
    /// <summary>Energy Performance Preference 0 (max performance) - 100 (max saving) (PERFEPP).</summary>
    public int Epp { get; set; } = 0;
    /// <summary>Core parking: minimum unparked cores %, 100 = parking off (CPMINCORES).</summary>
    public int MinUnparkedCores { get; set; } = 100;
    public bool UsbSelectiveSuspendOff { get; set; } = true;
    public bool PcieLinkPowerManagementOff { get; set; } = true;
    /// <summary>Disables CPU idle states (IDLEDISABLE). Advanced: more heat and power, rarely helps.</summary>
    public bool DisableIdleStates { get; set; }
}

/// <summary>
/// Everything goes through powercfg.exe, so every change is visible in Control Panel and can be
/// undone there too. Vanta only ever edits its own plan, never your existing plans.
/// </summary>
public sealed class PowerPlanService
{
    /// <summary>Fixed GUID for Vanta's plan so it can always be found (and removed).</summary>
    public const string TuneUpPlanGuid = "7a1d3c9e-5b2f-4e8a-9c61-2f0d7b3a4e10";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    // Setting GUIDs that have no short powercfg alias.
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string PcieAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    private static readonly Regex GuidRegex = new(
        @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s+\((.+?)\)(\s*\*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<List<PowerPlan>> ListAsync()
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", "/list", 15_000);
        return GuidRegex.Matches(r.StdOut)
            .Select(m => new PowerPlan(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim(), m.Groups[3].Success))
            .ToList();
    }

    public async Task<string?> GetActiveAsync()
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", 15_000);
        var m = GuidRegex.Match(r.StdOut);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public async Task<bool> SetActiveAsync(string guid)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/setactive {guid}", 15_000);
        if (!r.Ok) AppLog.Warn($"powercfg /setactive {guid} failed: {r.StdOut} {r.StdErr}");
        return r.Ok;
    }

    public async Task<bool> PlanExistsAsync(string guid) =>
        (await ListAsync()).Any(p => p.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates (or refreshes) "Vanta Performance", a copy of High Performance plus your settings.</summary>
    public async Task<bool> EnsureTuneUpPlanAsync(PerformancePlanSettings s)
    {
        if (!await PlanExistsAsync(TuneUpPlanGuid))
        {
            var dup = await ProcessRunner.RunAsync("powercfg.exe", $"/duplicatescheme {HighPerformanceGuid} {TuneUpPlanGuid}", 15_000);
            if (!dup.Ok)
            {
                // Some OEM images hide High Performance; fall back to Balanced as the base.
                dup = await ProcessRunner.RunAsync("powercfg.exe", $"/duplicatescheme {BalancedGuid} {TuneUpPlanGuid}", 15_000);
                if (!dup.Ok)
                {
                    AppLog.Error($"Could not create Vanta power plan: {dup.StdOut} {dup.StdErr}");
                    return false;
                }
            }
            AppLog.Info("Created Vanta Performance power plan");
        }

        // Always (re)name it, so a plan created before the rename to Vanta gets the new name too.
        await ProcessRunner.RunAsync("powercfg.exe",
            $"/changename {TuneUpPlanGuid} \"Vanta Performance\" \"Created by Vanta for gaming. Safe to delete.\"", 15_000);

        await ApplySettingsAsync(s);
        return true;
    }

    public async Task ApplySettingsAsync(PerformancePlanSettings s)
    {
        var g = TuneUpPlanGuid;
        async Task Set(string sub, string setting, int value)
        {
            await ProcessRunner.RunAsync("powercfg.exe", $"/setacvalueindex {g} {sub} {setting} {value}", 15_000);
            await ProcessRunner.RunAsync("powercfg.exe", $"/setdcvalueindex {g} {sub} {setting} {value}", 15_000);
        }

        await Set("SUB_PROCESSOR", "PROCTHROTTLEMIN", Math.Clamp(s.MinProcessorState, 0, 100));
        await Set("SUB_PROCESSOR", "PROCTHROTTLEMAX", Math.Clamp(s.MaxProcessorState, 1, 100));
        await Set("SUB_PROCESSOR", "PERFBOOSTMODE", Math.Clamp(s.BoostMode, 0, 4));
        await Set("SUB_PROCESSOR", "PERFEPP", Math.Clamp(s.Epp, 0, 100));
        await Set("SUB_PROCESSOR", "CPMINCORES", Math.Clamp(s.MinUnparkedCores, 0, 100));
        await Set("SUB_PROCESSOR", "IDLEDISABLE", s.DisableIdleStates ? 1 : 0);
        await Set(SubUsb, UsbSelectiveSuspend, s.UsbSelectiveSuspendOff ? 0 : 1);
        await Set(SubPcie, PcieAspm, s.PcieLinkPowerManagementOff ? 0 : 2);

        // Re-activating applies the new values if the plan is in use right now.
        if (await GetActiveAsync() == g) await SetActiveAsync(g);
    }

    /// <summary>Unhides the core parking settings in Control Panel's advanced power options.</summary>
    public Task UnhideCoreParkingAsync() =>
        ProcessRunner.RunAsync("powercfg.exe", "-attributes SUB_PROCESSOR CPMINCORES -ATTRIB_HIDE", 15_000);

    /// <summary>Adds Microsoft's hidden "Ultimate Performance" plan to the list (does not activate it).</summary>
    public async Task<string?> AddUltimatePerformanceAsync()
    {
        var plans = await ListAsync();
        var existing = plans.FirstOrDefault(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing.Guid;

        var r = await ProcessRunner.RunAsync("powercfg.exe", $"-duplicatescheme {UltimateGuid}", 15_000);
        var m = GuidRegex.Match(r.StdOut);
        return m.Success ? m.Groups[1].Value : null;
    }

    public async Task<bool> RemoveTuneUpPlanAsync()
    {
        if (!await PlanExistsAsync(TuneUpPlanGuid)) return true;
        if (await GetActiveAsync() == TuneUpPlanGuid) await SetActiveAsync(BalancedGuid);
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/delete {TuneUpPlanGuid}", 15_000);
        return r.Ok;
    }
}
