using System.Windows.Controls;
using TuneUp.ViewModels;

namespace TuneUp.Views;

public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is NetworkViewModel vm) vm.Activate(); };
        Unloaded += (_, _) => { if (DataContext is NetworkViewModel vm) vm.Deactivate(); };
    }
}
