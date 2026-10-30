using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renaissance.Core.Models;
using Renaissance.Core.Session;
using Serilog;

namespace Renaissance.UI.ViewModels;

public sealed record ParamPage(string Name, int Count)
{
    public string Label => $"{Name}  ({Count})";
}

public sealed partial class ParamsViewModel : ObservableObject, IDisposable
{
    public const string AllPages = "All";

    private readonly PluginSession _session;
    private readonly DispatcherTimer _flushTimer;
    private readonly HashSet<ParameterItemViewModel> _dirty = [];
    private readonly HashSet<int> _displayRefresh = [];
    private List<ParameterItemViewModel> _items = [];
    private bool _flushing;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _modifiedOnly;
    [ObservableProperty] private bool _automatableOnly;
    [ObservableProperty] private ParamPage? _selectedPage;

    public ParamsViewModel(PluginSession session)
    {
        _session = session;
        Title = $"{session.Info.Name} — Parameters";
        BuildItems();

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _flushTimer.Tick += async (_, _) => await FlushAsync();
        _flushTimer.Start();

        session.ParametersRefreshed += OnParametersRefreshed;
        session.ParameterAutomated += OnParameterAutomated;
    }

    public string Title { get; }
    public ObservableCollection<ParamPage> Pages { get; } = [];
    public ObservableCollection<ParameterItemViewModel> Visible { get; } = [];
    public string CountText => $"{Visible.Count} of {_items.Count} parameters";

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnModifiedOnlyChanged(bool value) => ApplyFilter();
    partial void OnAutomatableOnlyChanged(bool value) => ApplyFilter();
    partial void OnSelectedPageChanged(ParamPage? value) => ApplyFilter();

    private void BuildItems()
    {
        var defaults = _session.DefaultValues;
        _items = _session.Parameters.Select(p => new ParameterItemViewModel(
            p.Index, p.Name, ParameterGrouper.GroupFor(p.Name), p.Automatable, p.Value,
            p.Index < defaults.Length ? defaults[p.Index] : p.Value, p.DisplayWithLabel, OnUserChanged)).ToList();

        Pages.Clear();
        Pages.Add(new ParamPage(AllPages, _items.Count));
        foreach (var group in ParameterGrouper.Order)
        {
            var count = _items.Count(i => i.Group == group);
            if (count > 0) Pages.Add(new ParamPage(group, count));
        }
        SelectedPage = Pages[0];
    }

    private void ApplyFilter()
    {
        IEnumerable<ParameterItemViewModel> query = _items;
        if (SelectedPage is { Name: not AllPages } page) query = query.Where(i => i.Group == page.Name);
        if (ModifiedOnly) query = query.Where(i => i.IsModified);
        if (AutomatableOnly) query = query.Where(i => i.Automatable);
        var search = SearchText.Trim();
        if (search.Length > 0) query = query.Where(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        Visible.Clear();
        foreach (var item in query) Visible.Add(item);
        OnPropertyChanged(nameof(CountText));
    }

    // Coalesces rapid dial movements: only the latest value per parameter is sent.
    private void OnUserChanged(ParameterItemViewModel item) => _dirty.Add(item);

    private async Task FlushAsync()
    {
        if (_flushing || (_dirty.Count == 0 && _displayRefresh.Count == 0)) return;
        _flushing = true;
        try
        {
            var batch = _dirty.ToList();
            _dirty.Clear();
            foreach (var item in batch)
            {
                await _session.SetParameterAsync(item.Index, (float)item.Value);
                var display = await _session.GetParameterDisplayAsync(item.Index);
                item.SetFromHost(item.Value, display);
            }

            var refresh = _displayRefresh.ToList();
            _displayRefresh.Clear();
            foreach (var index in refresh)
            {
                var item = _items.ElementAtOrDefault(index);
                if (item is null) continue;
                var display = await _session.GetParameterDisplayAsync(index);
                item.SetFromHost(_session.Parameters[index].Value, display);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Parameter flush failed");
        }
        finally { _flushing = false; }
    }

    private void OnParameterAutomated(int index, float value) => Dispatcher.UIThread.Post(() =>
    {
        var item = _items.ElementAtOrDefault(index);
        if (item is null) return;
        item.SetFromHost(value, null);
        _displayRefresh.Add(index);
    });

    private void OnParametersRefreshed() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var p in _session.Parameters)
            _items.ElementAtOrDefault(p.Index)?.SetFromHost(p.Value, p.DisplayWithLabel);
        if (ModifiedOnly) ApplyFilter();
    });

    [RelayCommand]
    private void ResetPage()
    {
        foreach (var item in Visible.ToList()) item.ResetCommand.Execute(null);
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var item in _items) item.ResetCommand.Execute(null);
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        _session.ParametersRefreshed -= OnParametersRefreshed;
        _session.ParameterAutomated -= OnParameterAutomated;
    }
}
