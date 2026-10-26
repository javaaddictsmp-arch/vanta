using System.Text.Json;
using TuneUp.Models;

namespace TuneUp.Services;

/// <summary>
/// Persistent record of everything Vanta changed. This is what makes Undo and "Revert all"
/// exact: each Apply entry stores the previous value of every registry item it touched.
/// </summary>
public sealed class ChangeLogService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private List<ChangeLogEntry> _entries = new();

    public event EventHandler? Changed;

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(AppPaths.ChangeLogFile))
                {
                    _entries = JsonSerializer.Deserialize<List<ChangeLogEntry>>(
                                   File.ReadAllText(AppPaths.ChangeLogFile)) ?? new();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not read change log; starting a new one", ex);
                // Keep the unreadable file around instead of overwriting it.
                try
                {
                    File.Move(AppPaths.ChangeLogFile,
                        AppPaths.ChangeLogFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}");
                }
                catch { /* ignore */ }
                _entries = new();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public List<ChangeLogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public void Add(ChangeLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkUndone(Guid id)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e != null) e.Undone = true;
            SaveLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Most recent Apply entry for this tweak that has not been undone.</summary>
    public ChangeLogEntry? LatestActiveApply(string tweakId)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Action == "Apply" && !e.Undone && e.TweakId == tweakId)
                           .OrderByDescending(e => e.TimestampUtc)
                           .FirstOrDefault();
        }
    }

    public List<ChangeLogEntry> ActiveApplies()
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Action == "Apply" && !e.Undone)
                           .OrderByDescending(e => e.TimestampUtc)
                           .ToList();
        }
    }

    private void SaveLocked()
    {
        try
        {
            File.WriteAllText(AppPaths.ChangeLogFile, JsonSerializer.Serialize(_entries, Json));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save change log", ex);
        }
    }
}
