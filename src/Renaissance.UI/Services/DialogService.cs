using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Renaissance.UI.ViewModels;
using Renaissance.UI.Views;

namespace Renaissance.UI.Services;

public sealed record FileTypeFilter(string Name, params string[] Patterns);

public interface IDialogService
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickOpenFileAsync(string title, params FileTypeFilter[] filters);
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string defaultExtension, params FileTypeFilter[] filters);
    Task<string?> PromptAsync(string title, string message, string initial = "");
    Task<bool> ConfirmAsync(string title, string message);
    Task ShowMessageAsync(string title, string message);
    void ShowParams(ParamsViewModel vm);
}

public sealed class DialogService : IDialogService
{
    private readonly Window _owner;
    private ParamsWindow? _paramsWindow;

    public DialogService(Window owner) => _owner = owner;

    public async Task<string?> PickFolderAsync(string title)
    {
        var result = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private static List<FilePickerFileType> ToTypes(FileTypeFilter[] filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList();

    public async Task<string?> PickOpenFileAsync(string title, params FileTypeFilter[] filters)
    {
        var result = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToTypes(filters),
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string defaultExtension, params FileTypeFilter[] filters)
    {
        var result = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
            FileTypeChoices = ToTypes(filters),
        });
        return result?.TryGetLocalPath();
    }

    public Task<string?> PromptAsync(string title, string message, string initial = "") =>
        SimpleDialogs.PromptAsync(_owner, title, message, initial);

    public Task<bool> ConfirmAsync(string title, string message) => SimpleDialogs.ConfirmAsync(_owner, title, message);

    public Task ShowMessageAsync(string title, string message) => SimpleDialogs.MessageAsync(_owner, title, message);

    public void ShowParams(ParamsViewModel vm)
    {
        if (_paramsWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new ParamsWindow { DataContext = vm };
        window.Closed += (_, _) =>
        {
            vm.Dispose();
            _paramsWindow = null;
        };
        _paramsWindow = window;
        window.Show(_owner);
    }

    public void CloseParams() => _paramsWindow?.Close();
}

internal static class SimpleDialogs
{
    private static Window Create(string title, double width = 420) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initial)
    {
        var dialog = Create(title);
        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionEnd = initial.Length };
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => dialog.Close(box.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        dialog.Opened += (_, _) => box.Focus();
        var result = await dialog.ShowDialog<string?>(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var dialog = Create(title);
        var yes = new Button { Content = "Yes", IsDefault = true, Classes = { "primary" } };
        var no = new Button { Content = "No", IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } },
            },
        };
        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task MessageAsync(Window owner, string title, string message)
    {
        var dialog = Create(title, 480);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Classes = { "primary" } };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } },
            },
        };
        await dialog.ShowDialog(owner);
    }
}
