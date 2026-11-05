using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimpleDream.ViewModels;

/// <summary>One service of the station (button in the service row).</summary>
public sealed partial class ServiceItem(int id, string label, Action<int> select) : ObservableObject
{
    public int Id { get; } = id;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isSelected;
    [RelayCommand] private void Select() => select(Id);
}

/// <summary>One line of a Journaline menu.</summary>
public sealed partial class JournalineEntry(int index, string text, bool available, Action<int> follow)
{
    public string Text { get; } = text;
    public bool Available { get; } = available;
    [RelayCommand] private void Follow() => follow(index);
}

/// <summary>One row of the broadcast list.</summary>
public sealed partial class BroadcastItem(double khz, int startUtc, int stopUtc, string days, string station, string language,
    string target, string country, string site, double? kw, string state, string source, bool today, Action<double> tune)
{
    public double Khz { get; } = khz;
    public int StartUtc { get; } = startUtc;
    public int StopUtc { get; } = stopUtc;
    public string Days { get; } = days;
    public string Station { get; } = station;
    public string Language { get; } = language;
    public string Target { get; } = target;
    public string Country { get; } = country;
    public string Site { get; } = site;
    public string State { get; } = state;
    public string Source { get; } = source;
    public bool Today { get; } = today;

    /// <summary>Sort position of the status: on the air first, then starting soon.</summary>
    public int StateRank => State switch { "on" => 0, "ending" => 1, "soon" => 2, _ => 3 };
    /// <summary>Start in the viewer's time zone, minutes after local midnight.</summary>
    public int LocalStart => (int)DateTime.UtcNow.Date.AddMinutes(StartUtc).ToLocalTime().TimeOfDay.TotalMinutes;
    public bool IsOn => State is "on" or "ending";
    public bool IsSoon => State == "soon";
    public string KhzText => Khz == Math.Floor(Khz) ? Khz.ToString("0") : Khz.ToString("0.###");
    public string UtcText => $"{Hhmm(StartUtc)}-{Hhmm(StopUtc)}";
    public string LocalText => StopUtc % 1440 == StartUtc % 1440 ? "All day" : $"{Local(StartUtc)}-{Local(StopUtc)}";
    public string StateText => State switch { "on" => "On now", "ending" => "Ending", "soon" => "Soon", _ => "" };
    public string Detail => string.Join(" · ", new[] { Site, kw is { } p ? $"{p:0.#} kW" : "", Days, $"{UtcText} UTC", Source }.Where(s => s != ""));

    [RelayCommand] private void Tune() => tune(Khz);

    private static string Hhmm(int minutes) => $"{minutes % 1440 / 60:00}{minutes % 60:00}";

    /// <summary>A UTC minute of the day in the viewer's time zone, today.</summary>
    private static string Local(int minutes)
    {
        var utc = DateTime.UtcNow.Date.AddMinutes(minutes);
        return utc.ToLocalTime().ToString("HH:mm");
    }
}
