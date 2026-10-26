using System.Windows.Controls;
using TuneUp.ViewModels;

namespace TuneUp.Views;

public partial class CleaningView : UserControl
{
    public CleaningView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is CleaningViewModel vm) await vm.ActivateAsync(); };
        Unloaded += (_, _) => { if (DataContext is CleaningViewModel vm) vm.Deactivate(); };
    }

    /// <summary>Fill the running-app picker only when it opens (it's a snapshot).</summary>
    private void OnAllowDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is CleaningViewModel vm) vm.RefreshRunningNamesCommand.Execute(null);
    }
}
