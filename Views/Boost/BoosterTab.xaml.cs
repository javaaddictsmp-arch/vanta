using System.Windows.Controls;
using TuneUp.ViewModels;

namespace TuneUp.Views.Boost;

public partial class BoosterTab : UserControl
{
    public BoosterTab() => InitializeComponent();

    /// <summary>Fill the running-process list only when it is opened (it is a snapshot).</summary>
    private void OnProcessDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is BoosterTabViewModel vm) vm.RefreshProcessListCommand.Execute(null);
    }
}
