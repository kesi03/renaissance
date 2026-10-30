using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Renaissance.UI.Controls;

/// <summary>
/// Rotary dial inspired by modern plugin UIs. Value is normalised 0..1.
/// Drag vertically to adjust (Shift = fine), mouse wheel to nudge, double-click to reset.
/// </summary>
public sealed class DialControl : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<DialControl, double>(nameof(Value), 0.0, defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => Math.Clamp(double.IsNaN(v) ? 0 : v, 0, 1));

    public static readonly StyledProperty<double> DefaultValueProperty =
        AvaloniaProperty.Register<DialControl, double>(nameof(DefaultValue), 0.0);

    public static readonly StyledProperty<IBrush> AccentProperty =
        AvaloniaProperty.Register<DialControl, IBrush>(nameof(Accent), new SolidColorBrush(Color.Parse("#4FD1C5")));

    public static readonly StyledProperty<bool> IsModifiedProperty =
        AvaloniaProperty.Register<DialControl, bool>(nameof(IsModified));

    private const double StartAngle = 135;
    private const double SweepAngle = 270;

    private bool _dragging;
    private Point _dragStart;
    private double _valueAtDragStart;

    static DialControl()
    {
        AffectsRender<DialControl>(ValueProperty, AccentProperty, IsModifiedProperty, DefaultValueProperty);
        FocusableProperty.OverrideDefaultValue<DialControl>(true);
        CursorProperty.OverrideDefaultValue<DialControl>(new Cursor(StandardCursorType.SizeNorthSouth));
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DefaultValue { get => GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public IBrush Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public bool IsModified { get => GetValue(IsModifiedProperty); set => SetValue(IsModifiedProperty, value); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStart = point.Position;
        _valueAtDragStart = Value;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var delta = (_dragStart.Y - e.GetPosition(this).Y) / (fine ? 1200.0 : 160.0);
        Value = _valueAtDragStart + delta;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.002 : 0.02;
        Value += Math.Sign(e.Delta.Y) * step;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.002 : 0.02;
        switch (e.Key)
        {
            case Key.Up or Key.Right: Value += step; e.Handled = true; break;
            case Key.Down or Key.Left: Value -= step; e.Handled = true; break;
            case Key.Home: Value = 0; e.Handled = true; break;
            case Key.End: Value = 1; e.Handled = true; break;
        }
    }

    private static Point OnCircle(Point c, double r, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static StreamGeometry Arc(Point c, double r, double from, double sweep)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(OnCircle(c, r, from), false);
        ctx.ArcTo(OnCircle(c, r, from + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
        ctx.EndFigure(false);
        return g;
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 12) return;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var stroke = Math.Max(3, size * 0.07);
        var trackRadius = size / 2 - stroke;
        var value = Value;

        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#2E333D")), stroke, lineCap: PenLineCap.Round),
            Arc(center, trackRadius, StartAngle, SweepAngle - 0.01));

        if (value > 0.002)
        {
            context.DrawGeometry(null, new Pen(Accent, stroke, lineCap: PenLineCap.Round),
                Arc(center, trackRadius, StartAngle, Math.Max(0.5, SweepAngle * value - 0.01)));
        }

        var knobRadius = trackRadius - stroke - 3;
        var knobBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.4, 0.3, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.4, 0.3, RelativeUnit.Relative),
            RadiusX = RelativeScalar.Parse("80%"),
            RadiusY = RelativeScalar.Parse("80%"),
            GradientStops = { new GradientStop(Color.Parse("#4A505C"), 0), new GradientStop(Color.Parse("#1E2128"), 1) },
        };
        context.DrawEllipse(knobBrush, new Pen(new SolidColorBrush(Color.Parse("#0E1014")), 1), center, knobRadius, knobRadius);

        var angle = StartAngle + SweepAngle * value;
        context.DrawLine(new Pen(IsModified ? Accent : Brushes.White, Math.Max(2, size * 0.045), lineCap: PenLineCap.Round),
            OnCircle(center, knobRadius * 0.35, angle), OnCircle(center, knobRadius * 0.88, angle));

        if (IsFocused)
        {
            context.DrawEllipse(null, new Pen(Accent, 1, dashStyle: DashStyle.Dot), center, size / 2 - 1, size / 2 - 1);
        }
    }
}
