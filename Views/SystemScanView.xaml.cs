using System.Windows.Controls;
using TuneUp.ViewModels;

namespace TuneUp.Views;

public partial class SystemScanView : UserControl
{
    public SystemScanView()
    {
        InitializeComponent();
        // First visit runs the scan automatically.
        Loaded += async (_, _) =>
        {
            if (DataContext is SystemScanViewModel vm) await vm.EnsureScannedAsync();
        };
    }
}
