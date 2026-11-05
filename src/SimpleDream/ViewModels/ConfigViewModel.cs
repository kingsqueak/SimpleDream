using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleDream.Radio;

namespace SimpleDream.ViewModels;

/// <summary>The Configuration window: deeper settings kept off the main window.</summary>
public sealed partial class ConfigViewModel : ObservableObject
{
    private const string SystemDefault = "System default";
    public MainViewModel Main { get; }
    public string[] OutputDevices { get; }
    public string[] MirrorChoices { get; } = ["Auto", "Off", "On"];
    public string[] ThemeChoices { get; } = ["System", "Dark", "Light"];

    [ObservableProperty] private string _host;
    [ObservableProperty] private decimal _port;
    [ObservableProperty] private int _receiver;
    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private string _outputDevice;
    [ObservableProperty] private string _recordDir;
    [ObservableProperty] private string _mirror;
    [ObservableProperty] private bool _smoothSbr;
    [ObservableProperty] private string _theme;

    public ConfigViewModel(MainViewModel main)
    {
        Main = main;
        var s = main.Settings;
        (_host, _port, _receiver, _autoConnect) = (s.TciHost, s.TciPort, s.Receiver, s.AutoConnect);
        (_recordDir, _mirror, _smoothSbr, _theme) = (s.RecordDir, s.IqMirror, s.SmoothSbr, s.Theme);
        var devices = JsonSerializer.Deserialize<string[]>(Native.OutputDevices()) ?? [];
        OutputDevices = [SystemDefault, .. devices];
        _outputDevice = s.OutputDevice == "" ? SystemDefault : s.OutputDevice;
    }

    [RelayCommand]
    private void Connect()
    {
        var s = Main.Settings;
        (s.TciHost, s.TciPort, s.Receiver) = (Host.Trim(), (int)Port, Receiver);
        s.Save();
        Main.Connect();
    }

    [RelayCommand] private void Restart() => Main.RestartDecoder();

    private void Save(Action<Settings> change)
    {
        change(Main.Settings);
        Main.SettingsChanged();
    }

    partial void OnAutoConnectChanged(bool value) => Save(s => s.AutoConnect = value);
    partial void OnRecordDirChanged(string value) => Save(s => s.RecordDir = value.Trim());
    partial void OnMirrorChanged(string value) => Save(s => s.IqMirror = value);
    partial void OnSmoothSbrChanged(bool value) => Save(s => s.SmoothSbr = value);
    partial void OnOutputDeviceChanged(string value) => Main.SelectOutputDevice(value == SystemDefault ? "" : value);

    partial void OnThemeChanged(string value)
    {
        Save(s => s.Theme = value);
        App.ApplyTheme(value);
    }
}
