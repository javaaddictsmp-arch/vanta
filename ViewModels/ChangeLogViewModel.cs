using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

public sealed class ChangeLogRow
{
    public ChangeLogRow(ChangeLogEntry e)
    {
        Entry = e;
        Time = e.TimestampUtc.ToLocalTime().ToString("g");
        Action = e.Action switch { "Apply" => "Applied", "Revert" => "Reverted", "RevertAll" => "Reverted all", _ => e.Action };
        CanUndo = e.Action == "Apply" && !e.Undone;
        Status = e.Action == "Apply" ? (e.Undone ? "Undone" : "Active") : "";
    }

    public ChangeLogEntry Entry { get; }
    public string Time { get; }
    public string Action { get; }
    public string TweakName => Entry.TweakName;
    public string Note => Entry.Note;
    public string Status { get; }
    public bool CanUndo { get; }

    /// <summary>Note and status joined, skipping whichever is empty.</summary>
    public string Detail => string.Join(" - ", new[] { Entry.Note, Status }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public partial class ChangeLogViewModel : ObservableObject
{
    public ChangeLogViewModel()
    {
        AppServices.ChangeLog.Changed += (_, _) => UiThread.Post(Reload);
        Reload();
    }

    public ObservableCollection<ChangeLogRow> Rows { get; } = new();

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _hasActiveChanges;
    [ObservableProperty] private bool _hasEntries;

    private void Reload()
    {
        Rows.Clear();
        foreach (var e in AppServices.ChangeLog.Snapshot().OrderByDescending(x => x.TimestampUtc))
            Rows.Add(new ChangeLogRow(e));

        HasEntries = Rows.Count > 0;
        HasActiveChanges = AppServices.ChangeLog.ActiveApplies().Count > 0;
    }

    [RelayCommand]
    private async Task UndoAsync(ChangeLogRow? row)
    {
        if (row == null || !row.CanUndo) return;

        var result = await AppServices.Engine.UndoEntryAsync(row.Entry.Id);
        StatusMessage = result.Success ? $"Undid: {row.TweakName}." : result.Message;
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        if (!HasActiveChanges)
        {
            StatusMessage = "There are no active changes to revert.";
            return;
        }

        var answer = MessageBox.Show(
            "Undo every change Vanta has made and put your previous values back?\n\n" +
            "Some changes need a restart or sign-out to fully take effect.",
            "Revert all changes", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var progress = new Progress<string>(m => StatusMessage = m);
        var result = await AppServices.Engine.RevertAllAsync(progress);
        StatusMessage = result.Message;
    }

    [RelayCommand]
    private void OpenBackups() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Backups}\"") { UseShellExecute = true });
}
