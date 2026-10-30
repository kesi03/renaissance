using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Renaissance.UI.ViewModels;

public sealed partial class ParameterItemViewModel : ObservableObject
{
    private readonly Action<ParameterItemViewModel> _changed;
    private bool _suppress;

    [ObservableProperty] private double _value;
    [ObservableProperty] private string _display = "";

    public ParameterItemViewModel(int index, string name, string group, bool automatable, double value, double defaultValue,
        string display, Action<ParameterItemViewModel> changed)
    {
        Index = index;
        Name = string.IsNullOrWhiteSpace(name) ? $"Param {index + 1}" : name;
        Group = group;
        Automatable = automatable;
        DefaultValue = defaultValue;
        _value = value;
        _display = display;
        _changed = changed;
    }

    public int Index { get; }
    public string Name { get; }
    public string Group { get; }
    public bool Automatable { get; }
    public double DefaultValue { get; }
    public bool IsModified => Math.Abs(Value - DefaultValue) > 0.0005;
    public string Tooltip => $"{Name}: {Display}\nDrag to adjust · Shift = fine · Wheel · Double-click resets";

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(IsModified));
        if (!_suppress) _changed(this);
    }

    partial void OnDisplayChanged(string value) => OnPropertyChanged(nameof(Tooltip));

    public void SetFromHost(double value, string? display)
    {
        _suppress = true;
        try
        {
            Value = value;
            if (display is not null) Display = display;
        }
        finally { _suppress = false; }
    }

    [RelayCommand] private void Reset() => Value = DefaultValue;
    [RelayCommand] private void SetMin() => Value = 0;
    [RelayCommand] private void SetCenter() => Value = 0.5;
    [RelayCommand] private void SetMax() => Value = 1;
}
