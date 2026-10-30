using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Renaissance.Core.Models;

namespace Renaissance.UI.ViewModels;

public sealed partial class PluginRowViewModel : ObservableObject
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#4CD97B"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5B942"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F25F5C"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#7C8493"));

    [ObservableProperty] private PluginInfo _info;
    [ObservableProperty] private Bitmap? _icon;

    public PluginRowViewModel(PluginInfo info)
    {
        _info = info;
        LoadIcon();
    }

    public void Update(PluginInfo info)
    {
        Info = info;
        LoadIcon();
        OnPropertyChanged(string.Empty);
    }

    public string Name => (Info.Favourite ? "★ " : "") + Info.Name;
    public string Version => Info.Version;
    public string Vendor => Info.Vendor;
    public string Type => Info.Type;
    public string Bits => Info.BitsLabel;
    public string Host => Info.HostType.ToString();
    public string Status => Info.Status.ToString();
    public string Details => Info.Details;
    public string Tags => Info.Tags;
    public bool IsDisabled => Info.Status == PluginStatus.Disabled;
    public bool IsFailed => Info.Status is PluginStatus.Crashed or PluginStatus.Warning or PluginStatus.Missing;

    public IBrush StatusBrush => Info.Status switch
    {
        PluginStatus.Ok => Green,
        PluginStatus.Warning => Amber,
        PluginStatus.Crashed => Red,
        PluginStatus.Missing => Grey,
        _ => Grey,
    };

    private void LoadIcon()
    {
        Icon = null;
        var path = Info.IconPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp")) return;
        try { Icon = new Bitmap(path); } catch { Icon = null; }
    }
}
