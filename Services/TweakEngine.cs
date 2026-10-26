using Microsoft.Win32;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// The single pipeline every change goes through:
///   restore point (once per session) -> .reg export -> snapshot old values -> write -> log.
/// If any write fails, everything already written for that tweak is rolled back.
/// Undo / Revert use the snapshots in the change log, so they restore the exact previous state.
/// </summary>
public sealed class TweakEngine
{
    private readonly ChangeLogService _log;
    private readonly BackupService _backup;
    private readonly RestorePointService _restore;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _restorePointAttempted;

    public TweakEngine(ChangeLogService log, BackupService backup, RestorePointService restore, SettingsService settings)
    {
        _log = log;
        _backup = backup;
        _restore = restore;
        _settings = settings;
    }

    /// <summary>Last restore-point message, so the UI can show it.</summary>
    public string? LastRestorePointMessage { get; private set; }

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public static bool IsApplicable(TweakDefinition def) => def.AppliesTo switch
    {
        WindowsTarget.Win10 => !IsWindows11,
        WindowsTarget.Win11 => IsWindows11,
        _ => true
    };

    // ============================================================================================
    // Resolved operations (registry + service ops share the same machinery)
    // ============================================================================================
    private sealed record ResolvedOp(
        string Hive, string Path, string Name, RegistryValueKind Kind,
        string ApplyValue, string? DefaultValue, string? Service);

    private static List<ResolvedOp> Expand(TweakDefinition def)
    {
        var list = new List<ResolvedOp>();
        foreach (var op in def.Operations)
        {
            if (op.Kind.Equals("service", StringComparison.OrdinalIgnoreCase))
            {
                var servicePath = $@"SYSTEM\CurrentControlSet\Services\{op.Name}";
                if (!RegistryService.KeyExists("HKLM", servicePath)) continue; // service not on this PC

                list.Add(new ResolvedOp(
                    "HKLM", servicePath, "Start", RegistryValueKind.DWord,
                    ServiceStartValue(op.ApplyValue),
                    op.DefaultValue == null ? null : ServiceStartValue(op.DefaultValue),
                    op.Name));
            }
            else
            {
                var kind = Enum.Parse<RegistryValueKind>(op.ValueType, ignoreCase: true);
                list.Add(new ResolvedOp(op.Hive, op.Path, op.Name, kind, op.ApplyValue, op.DefaultValue, null));
            }
        }
        return list;
    }

    private static string ServiceStartValue(string startType) => startType.ToLowerInvariant() switch
    {
        "automatic" or "auto" => "2",
        "manual" or "demand" => "3",
        "disabled" => "4",
        _ => throw new ArgumentException($"Unknown service start type '{startType}'")
    };

    // ============================================================================================
    // Detect current state
    // ============================================================================================
    public Task<bool> IsAppliedAsync(TweakDefinition def) => Task.Run(() => IsApplied(def));

    public bool IsApplied(TweakDefinition def)
    {
        try
        {
            var ops = Expand(def);
            if (ops.Count == 0) return false;

            foreach (var op in ops)
            {
                var current = RegistryService.Read(op.Hive, op.Path, op.Name);
                if (!RegistryService.Matches(current, op.Kind, op.ApplyValue)) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not detect state of tweak {def.Id}", ex);
            return false;
        }
    }

    // ============================================================================================
    // Apply
    // ============================================================================================
    public async Task<EngineResult> ApplyAsync(TweakDefinition def, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ApplyCoreAsync(def);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies several tweaks in sequence (used by Quick Optimize).</summary>
    public async Task<(int Applied, int Skipped, int Failed, bool Reboot, bool SignOut)> ApplyManyAsync(
        IEnumerable<TweakDefinition> defs, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int applied = 0, skipped = 0, failed = 0;
        bool reboot = false, signOut = false;

        await _gate.WaitAsync(ct);
        try
        {
            foreach (var def in defs)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Applying: {def.Name}");

                if (IsApplied(def)) { skipped++; continue; }

                var result = await ApplyCoreAsync(def);
                if (result.Success)
                {
                    applied++;
                    reboot |= result.RebootRequired;
                    signOut |= result.SignOutRequired;
                }
                else failed++;
            }
        }
        finally
        {
            _gate.Release();
        }

        return (applied, skipped, failed, reboot, signOut);
    }

    private async Task<EngineResult> ApplyCoreAsync(TweakDefinition def)
    {
        try
        {
            if (!IsApplicable(def))
                return EngineResult.Fail("This tweak does not apply to your version of Windows.");

            var ops = Expand(def);
            if (ops.Count == 0)
                return EngineResult.Fail("Nothing to change on this PC (the related component is not installed).");

            if (IsApplied(def))
                return EngineResult.Ok("Already applied.");

            await EnsureRestorePointAsync();

            var backupFolder = await _backup.ExportKeysAsync(
                def.Id, ops.Select(o => (o.Hive, o.Path)));

            // Snapshot first, so a failed write can be rolled back exactly.
            var snapshots = ops.Select(Snapshot).ToList();
            var written = new List<OpSnapshot>();

            try
            {
                for (var i = 0; i < ops.Count; i++)
                {
                    RegistryService.Write(ops[i].Hive, ops[i].Path, ops[i].Name, ops[i].Kind, ops[i].ApplyValue);
                    written.Add(snapshots[i]);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"Applying {def.Id} failed, rolling back", ex);
                Restore(written);
                return EngineResult.Fail($"Could not apply this tweak: {ex.Message}. Nothing was left changed.");
            }

            // Services: stop them now so Disabled takes effect before the next reboot.
            foreach (var op in ops.Where(o => o.Service != null && o.ApplyValue == "4"))
                await ProcessRunner.RunAsync("sc.exe", $"stop \"{op.Service}\"", 15_000);

            _log.Add(new ChangeLogEntry
            {
                Action = "Apply",
                TweakId = def.Id,
                TweakName = def.Name,
                BackupFolder = backupFolder,
                Snapshots = snapshots,
                Note = def.RebootRequired ? "Restart required" : ""
            });

            AppLog.Info($"Applied tweak {def.Id}");
            return EngineResult.Ok("Applied.", def.RebootRequired, def.SignOutRequired);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unexpected error applying {def.Id}", ex);
            return EngineResult.Fail($"Unexpected error: {ex.Message}");
        }
    }

    // ============================================================================================
    // Revert / Undo
    // ============================================================================================
    /// <summary>
    /// Puts a tweak back. Uses the recorded original values when Vanta applied it; otherwise
    /// falls back to the Windows defaults written in the catalog.
    /// </summary>
    public async Task<EngineResult> RevertAsync(TweakDefinition def, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var ops = Expand(def);
            var recorded = _log.LatestActiveApply(def.Id);

            // Snapshot the current state so the revert itself is logged.
            var before = ops.Select(Snapshot).ToList();

            if (recorded != null)
            {
                Restore(recorded.Snapshots);
                _log.MarkUndone(recorded.Id);
            }
            else
            {
                RestoreToDefaults(ops);
            }

            _log.Add(new ChangeLogEntry
            {
                Action = "Revert",
                TweakId = def.Id,
                TweakName = def.Name,
                Snapshots = before,
                Undone = true, // informational entry, not undoable
                Note = recorded != null ? "Restored your previous values" : "Restored Windows defaults"
            });

            AppLog.Info($"Reverted tweak {def.Id}");
            return EngineResult.Ok("Reverted.", def.RebootRequired, def.SignOutRequired);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Revert of {def.Id} failed", ex);
            return EngineResult.Fail($"Could not revert: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Undo one specific Apply entry from the change log.</summary>
    public async Task<EngineResult> UndoEntryAsync(Guid entryId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var entry = _log.Snapshot().FirstOrDefault(e => e.Id == entryId);
            if (entry == null || entry.Action != "Apply" || entry.Undone)
                return EngineResult.Fail("That change can no longer be undone.");

            Restore(entry.Snapshots);
            _log.MarkUndone(entry.Id);
            _log.Add(new ChangeLogEntry
            {
                Action = "Revert",
                TweakId = entry.TweakId,
                TweakName = entry.TweakName,
                Undone = true,
                Note = "Undone from change log"
            });
            return EngineResult.Ok("Undone.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Undo failed", ex);
            return EngineResult.Fail($"Could not undo: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Undoes every active change, newest first.</summary>
    public async Task<EngineResult> RevertAllAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var active = _log.ActiveApplies();
            if (active.Count == 0) return EngineResult.Ok("There was nothing to revert.");

            var count = 0;
            foreach (var entry in active) // already newest first
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Reverting: {entry.TweakName}");
                Restore(entry.Snapshots);
                _log.MarkUndone(entry.Id);
                count++;
            }

            _log.Add(new ChangeLogEntry
            {
                Action = "RevertAll",
                TweakName = "All changes",
                Undone = true,
                Note = $"{count} change(s) reverted"
            });

            AppLog.Info($"Reverted all changes ({count})");
            return EngineResult.Ok($"Reverted {count} change(s). Some may need a restart or sign-out to fully take effect.", true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Revert all failed", ex);
            return EngineResult.Fail($"Revert all stopped: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================
    private async Task EnsureRestorePointAsync()
    {
        if (_restorePointAttempted || !_settings.Current.CreateRestorePoint) return;
        _restorePointAttempted = true;

        var outcome = await _restore.CreateAsync("Vanta - before changes");
        LastRestorePointMessage = outcome.Message;
    }

    private static OpSnapshot Snapshot(ResolvedOp op)
    {
        var current = RegistryService.Read(op.Hive, op.Path, op.Name);
        return new OpSnapshot
        {
            Hive = op.Hive,
            Path = op.Path,
            Name = op.Name,
            Existed = current.Exists,
            PreviousKind = current.Exists ? current.Kind.ToString() : null,
            PreviousValue = current.Value,
            ServiceName = op.Service
        };
    }

    /// <summary>Writes back (or deletes) each snapshot, in reverse order.</summary>
    private static void Restore(IEnumerable<OpSnapshot> snapshots)
    {
        foreach (var s in snapshots.Reverse())
        {
            try
            {
                if (s.Existed && s.PreviousKind != null && s.PreviousValue != null)
                {
                    var kind = Enum.Parse<RegistryValueKind>(s.PreviousKind, true);
                    RegistryService.Write(s.Hive, s.Path, s.Name, kind, s.PreviousValue);
                }
                else
                {
                    RegistryService.DeleteValue(s.Hive, s.Path, s.Name);
                }

                RestartServiceIfNeeded(s);
            }
            catch (Exception ex)
            {
                AppLog.Error($"Restoring {s.Hive}\\{s.Path}\\{s.Name} failed", ex);
                throw;
            }
        }
    }

    private static void RestoreToDefaults(IEnumerable<ResolvedOp> ops)
    {
        foreach (var op in ops.Reverse())
        {
            if (op.DefaultValue == null)
                RegistryService.DeleteValue(op.Hive, op.Path, op.Name);
            else
                RegistryService.Write(op.Hive, op.Path, op.Name, op.Kind, op.DefaultValue);

            if (op.Service != null && op.DefaultValue != null && op.DefaultValue != "4")
                _ = ProcessRunner.RunAsync("sc.exe", $"start \"{op.Service}\"", 15_000);
        }
    }

    /// <summary>After restoring a service's Start value, start it again if it is no longer disabled.</summary>
    private static void RestartServiceIfNeeded(OpSnapshot s)
    {
        if (s.ServiceName == null || s.PreviousValue == null) return;
        if (s.PreviousValue != "4")
            _ = ProcessRunner.RunAsync("sc.exe", $"start \"{s.ServiceName}\"", 15_000);
    }
}
