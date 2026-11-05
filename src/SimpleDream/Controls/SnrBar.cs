using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SimpleDream.Controls;

/// <summary>Signal quality bar, 0 to 30 dB, with a mark at what the station's modulation needs.</summary>
public sealed class SnrBar : Control
{
    private const double MaxDb = 30;
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<SnrBar, double>(nameof(Value));
    public static readonly StyledProperty<double> NeedsProperty = AvaloniaProperty.Register<SnrBar, double>(nameof(Needs), double.NaN);
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Needs { get => GetValue(NeedsProperty); set => SetValue(NeedsProperty, value); }

    private static readonly IBrush Low = new SolidColorBrush(Color.Parse("#F5A524"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush Plain = new SolidColorBrush(Color.Parse("#3B82F6"));

    static SnrBar() => AffectsRender<SnrBar>(ValueProperty, NeedsProperty);
    public SnrBar() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    private IBrush Themed(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.DrawRectangle(Themed("MeterTrack"), null, new Rect(0, 0, w, h), 3, 3);
        var fill = Math.Clamp(Value / MaxDb, 0, 1) * w;
        var brush = double.IsNaN(Needs) ? Plain : Value >= Needs ? Good : Low;
        if (fill > 0) ctx.DrawRectangle(brush, null, new Rect(0, 0, fill, h), 3, 3);
        if (!double.IsNaN(Needs))
            ctx.FillRectangle(Themed("MeterPeak"), new Rect(Math.Clamp(Needs / MaxDb, 0, 1) * w - 1, 0, 2, h));
    }
}
