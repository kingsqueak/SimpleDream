using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using SimpleDream.Controls;
using SimpleDream.ViewModels;

namespace SimpleDream.Views;

public partial class MainWindow : Window
{
    private BroadcastsWindow? _broadcasts;
    private ConfigWindow? _config;
    private Bitmap? _slide;
    private double _wheel;

    public MainWindow()
    {
        InitializeComponent();
        // On macOS the menu lives in the system bar, so this would only leave a gap.
        MenuBar.IsVisible = !OperatingSystem.IsMacOS();
        Opened += (_, _) =>
        {
            // Development aid for screenshots.
            switch (Environment.GetEnvironmentVariable("SIMPLEDREAM_SHOW"))
            {
                case "broadcasts": OnBroadcasts(null, new RoutedEventArgs()); break;
                case "config": OnConfig(null, new RoutedEventArgs()); break;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.PropertyChanged += OnViewModelChanged;
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SlidePath)) return;
        var old = _slide;
        _slide = null;
        try
        {
            if (Vm.SlidePath is { } path) _slide = new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { }
        Slide.Source = _slide;
        old?.Dispose();
    }

    private void OnConfig(object? sender, RoutedEventArgs e)
    {
        if (_config is { IsVisible: true })
        {
            _config.Activate();
            return;
        }
        _config = new ConfigWindow { DataContext = new ConfigViewModel(Vm) };
        _config.Show(this);
    }

    private void OnBroadcasts(object? sender, RoutedEventArgs e)
    {
        if (_broadcasts is { IsVisible: true })
        {
            _broadcasts.Activate();
            return;
        }
        _broadcasts = new BroadcastsWindow { DataContext = Vm.Broadcasts };
        _broadcasts.Show(this);
    }

    public void ShowAbout() => new AboutWindow().ShowDialog(this);

    private void OnFrequencyWheel(object? sender, PointerWheelEventArgs e)
    {
        // One wheel click is 0.2 on macOS and 1.0 elsewhere; trackpads send fractions.
        _wheel += SliderWheel.Amount(e) / SliderWheel.ClickDelta;
        e.Handled = true;
        var steps = (int)Math.Truncate(_wheel + Math.CopySign(1e-6, _wheel));
        if (steps == 0) return;
        _wheel -= steps;
        Vm.TuneSteps(steps);
    }

    private void OnFrequencyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FrequencyEntry.IsVisible) return;
        FrequencyText.IsVisible = false;
        FrequencyEntry.IsVisible = true;
        FrequencyEntry.Text = "";
        FrequencyEntry.PlaceholderText = "kHz";
        FrequencyEntry.Focus();
    }

    private void EndEntry()
    {
        FrequencyEntry.IsVisible = false;
        FrequencyText.IsVisible = true;
    }

    private void OnFrequencyKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) EndEntry();
        if (e.Key != Key.Enter) return;
        // 6110 is kHz, 6.11 is MHz.
        if (double.TryParse(FrequencyEntry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
            Vm.TuneKhz(v < 30 ? v * 1000 : v);
        EndEntry();
    }

    private void OnFrequencyLostFocus(object? sender, RoutedEventArgs e) => EndEntry();
}
