using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Renaissance.UI.ViewModels;

namespace Renaissance.UI.Views;

public partial class ScanView : UserControl
{
    public ScanView()
    {
        InitializeComponent();
        PluginGrid.DoubleTapped += OnGridDoubleTapped;
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ScanViewModel vm && vm.SelectedPlugin is not null && e.Source is Avalonia.Visual v
            && v.FindAncestorOfType<DataGridRow>() is not null)
            vm.OpenPluginCommand.Execute(vm.SelectedPlugin);
    }
}
