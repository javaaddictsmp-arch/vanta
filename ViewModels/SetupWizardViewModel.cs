using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneUp.Models;
using TuneUp.Services;

namespace TuneUp.ViewModels;

/// <summary>
/// First-run setup: 1) Simple or Full, 2) quick health check with "apply safe fixes", 3) background
/// options. Shown once; Settings has "Run setup again". Every step can be skipped.
/// </summary>
public partial class SetupWizardViewModel : ObservableObject
{
    private readonly DashboardViewModel _dashboard;

    public SetupWizardViewModel(DashboardViewModel dashboard)
    {
        _dashboard = dashboard;
        _dashboard.HealthItems.CollectionChanged += (_, _) => RefreshProblems();
        _dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DashboardViewModel.HealthScore) or nameof(DashboardViewModel.HealthLabel)
                or nameof(DashboardViewModel.IsBusy) or nameof(DashboardViewModel.StatusMessage))
                OnPropertyChanged(nameof(Dashboard));
        };
        var s = AppServices.Settings.Current;
        _simple = s.SimpleMode;
        _autoBoost = s.BoosterEnabled;
        _startWithWindows = s.StartWithWindows;
        _sessionReports = s.SessionReports;
    }

    public DashboardViewModel Dashboard => _dashboard;
    public ObservableCollection<HealthItem> Problems { get; } = new();
    public bool IsAdmin => MainViewModel.IsAdmin;
    public string HotkeyTip => $"Tip: press {AppServices.Settings.Current.Hotkey} inside a game to turn the boost on or off (useful when auto-boost is off). Change the keys in Settings.";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep0), nameof(IsStep1), nameof(IsStep2), nameof(NextText), nameof(CanGoBack))]
    private int _step;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFull))]
    private bool _simple;
    public bool IsFull { get => !Simple; set => Simple = !value; }

    [ObservableProperty] private bool _autoBoost;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _sessionReports;
    [ObservableProperty] private bool _checkForUpdates = AppServices.Settings.Current.CheckForUpdates;

    public bool IsStep0 => Step == 0;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool CanGoBack => Step > 0;
    public string NextText => Step == 2 ? "Finish" : "Next";

    public void Open()
    {
        Step = 0;
        Simple = UiMode.Instance.IsSimple;
        RefreshProblems();
        IsOpen = true;
    }

    private void RefreshProblems()
    {
        Problems.Clear();
        foreach (var p in _dashboard.HealthItems.Where(i => i.Severity is Severity.Problem or Severity.Warning).Take(4)) Problems.Add(p);
        OnPropertyChanged(nameof(NoProblems));
    }

    public bool NoProblems => Problems.Count == 0 && !_dashboard.IsBusy;

    partial void OnSimpleChanged(bool value) => UiMode.Instance.IsSimple = value; // preview the choice right away

    [RelayCommand]
    private void Back() { if (Step > 0) Step--; }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (Step < 2) { Step++; return; }
        await FinishAsync();
    }

    [RelayCommand]
    private async Task ApplySafeFixesAsync() => await _dashboard.QuickOptimizeCommand.ExecuteAsync(null);

    [RelayCommand]
    private void Skip() => Close();

    private async Task FinishAsync()
    {
        var s = AppServices.Settings.Current;
        s.BoosterEnabled = AutoBoost;
        s.SessionReports = SessionReports;
        s.CheckForUpdates = CheckForUpdates;
        if (StartWithWindows != s.StartWithWindows)
        {
            var ok = await SystemIntegrationService.SetStartWithWindowsAsync(StartWithWindows);
            s.StartWithWindows = StartWithWindows && ok;
        }
        Close();
    }

    private void Close()
    {
        AppServices.Settings.Current.FirstRunDone = true;
        AppServices.Settings.Save();
        IsOpen = false;
        Closed?.Invoke();
    }

    /// <summary>Lets the main window refresh pages whose toggles the wizard changed.</summary>
    public event Action? Closed;
}
