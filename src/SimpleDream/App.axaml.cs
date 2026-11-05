using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using SimpleDream.Controls;
using SimpleDream.Platform;
using SimpleDream.ViewModels;
using SimpleDream.Views;

namespace SimpleDream;

public partial class App : Application
{
    private PosixSignalRegistration[] _signals = [];

    public static string Version { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Name = $"SimpleDream {Version}";
    }

    private void OnAbout(object? sender, EventArgs e) =>
        ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow)?.ShowAbout();

    public static void ApplyTheme(string theme)
    {
        if (Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                "Dark" => ThemeVariant.Dark,
                "Light" => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MacActivity.Begin("Live radio decoding");
            SliderWheel.Enable();
            PopupTimeout.Enable();
            var vm = new MainViewModel();
            ApplyTheme(vm.Settings.Theme);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.Exit += (_, _) => vm.Dispose();
            _signals =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => vm.Dispose()),
                PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => vm.Dispose()),
            ];
        }
        base.OnFrameworkInitializationCompleted();
    }
}
