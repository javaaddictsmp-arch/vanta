using System.Windows.Controls;
using TuneUp.ViewModels;

namespace TuneUp.Views;

public partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is StartupViewModel vm) vm.Activate(); };
        Unloaded += (_, _) => { if (DataContext is StartupViewModel vm) vm.Deactivate(); };
    }
}
