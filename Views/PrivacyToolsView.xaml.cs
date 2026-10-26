using System.Windows.Controls;
using TuneUp.ViewModels;

namespace TuneUp.Views;

public partial class PrivacyToolsView : UserControl
{
    public PrivacyToolsView()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is PrivacyToolsViewModel vm) await vm.ActivateAsync(); };
    }
}
