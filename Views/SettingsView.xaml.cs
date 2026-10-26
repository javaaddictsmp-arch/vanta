using System.Windows.Controls;
using System.Windows.Input;
using TuneUp.Services;
using TuneUp.ViewModels;

namespace TuneUp.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is SettingsViewModel vm) await vm.ActivateAsync(); };
    }

    /// <summary>Records the pressed combination (needs at least one of Ctrl/Alt/Shift/Win).</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var combo = HotkeyParser.FromKeyPress(key, Keyboard.Modifiers);
        if (combo != null && DataContext is SettingsViewModel vm) vm.SetHotkey(combo);
    }
}
