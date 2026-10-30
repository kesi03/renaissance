using Avalonia.Controls;
using Renaissance.UI.ViewModels;

namespace Renaissance.UI.Views;

public partial class PluginView : UserControl
{
    public PluginView()
    {
        InitializeComponent();

        EditorHost.HandleReady += handle =>
        {
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            if (DataContext is PluginViewModel vm) _ = vm.AttachEditorAsync(handle, scale);
        };

        PatchList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not PatchItem item) return;
            PatchList.SelectedItem = null;
            PatchButton.Flyout?.Hide();
            if (DataContext is PluginViewModel vm) vm.SelectPatchCommand.Execute(item);
        };
    }
}
