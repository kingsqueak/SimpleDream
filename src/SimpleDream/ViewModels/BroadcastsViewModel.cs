using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleDream.Radio;

namespace SimpleDream.ViewModels;

/// <summary>The broadcast list: downloaded, cached, and refreshed against the clock.</summary>
public sealed partial class BroadcastsViewModel : ObservableObject
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);
    private const double MatchKhz = 5;

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private List<BroadcastItem> _all = [];
    private double _tunedKhz;

    public ObservableCollection<BroadcastItem> Items { get; } = [];

    [ObservableProperty] private string _filter = "Now";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _sourceStatus = "";
    [ObservableProperty] private bool _isUpdating;

    public string TimeZoneName => TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName;
    public bool FilterNow => Filter == "Now";
    public bool FilterToday => Filter == "Today";
    public bool FilterAll => Filter == "All";

    /// <summary>The nearest scheduled station on the tuned frequency, on the air now.</summary>
    public string? ScheduledHere { get; private set; }

    public BroadcastsViewModel(MainViewModel main)
    {
        _main = main;
        Reload();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => Reload();
        _clock.Start();
        _ = UpdateIfOldAsync();
    }

    private async Task UpdateIfOldAsync()
    {
        var sources = Sources();
        var stale = sources.Any(s =>
        {
            var path = Path.Combine(Settings.ScheduleDir, s.File);
            return !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > MaxAge;
        });
        if (stale) await UpdateAsync();
    }

    private sealed record Src(string Title, string Url, string File);

    private static List<Src> Sources()
    {
        using var doc = JsonDocument.Parse(Native.ScheduleSources(Settings.ScheduleDir));
        return doc.RootElement.EnumerateArray()
            .Select(e => new Src(e.GetProperty("title").GetString()!, e.GetProperty("url").GetString()!, e.GetProperty("file").GetString()!))
            .ToList();
    }

    /// <summary>Fetch every source. One failing keeps its cached copy.</summary>
    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        var failed = new List<string>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SimpleDream/{App.Version}");
        foreach (var s in Sources())
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(s.Url);
                var dest = Path.Combine(Settings.ScheduleDir, s.File);
                await File.WriteAllBytesAsync(dest + ".tmp", bytes);
                File.Move(dest + ".tmp", dest, true);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                failed.Add(s.Title);
            }
        }
        IsUpdating = false;
        Reload();
        if (failed.Count > 0) SourceStatus += $"  Couldn't update {string.Join(", ", failed)}.";
    }

    [RelayCommand]
    private void SetFilter(string which) => Filter = which;

    partial void OnFilterChanged(string value)
    {
        OnPropertyChanged(nameof(FilterNow));
        OnPropertyChanged(nameof(FilterToday));
        OnPropertyChanged(nameof(FilterAll));
        Apply();
    }

    partial void OnSearchChanged(string value) => Apply();

    public void FrequencyChanged(double khz)
    {
        _tunedKhz = khz;
        Match();
    }

    private void Reload()
    {
        using var doc = JsonDocument.Parse(Native.Schedule(Settings.ScheduleDir));
        var root = doc.RootElement;
        var items = new List<BroadcastItem>();
        foreach (var e in root.GetProperty("entries").EnumerateArray())
        {
            string S(string k) => e.GetProperty(k).ValueKind == JsonValueKind.String ? e.GetProperty(k).GetString()! : "";
            items.Add(new BroadcastItem(e.GetProperty("khz").GetDouble(), e.GetProperty("start").GetInt32(), e.GetProperty("stop").GetInt32(),
                S("days"), S("station"), S("language"), S("target"), S("country"), S("site"),
                e.GetProperty("kw").ValueKind == JsonValueKind.Number ? e.GetProperty("kw").GetDouble() : null,
                S("state"), S("source"), e.GetProperty("today").GetBoolean(), _main.TuneKhz));
        }
        _all = items;
        var sources = root.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        SourceStatus = sources.Count == 0 ? "No schedule downloaded yet." : $"From {string.Join(" and ", sources)}. Schedules can be out of date.";
        Match();
        Apply();
    }

    private void Match()
    {
        ScheduledHere = _all.Where(b => b.IsOn && Math.Abs(b.Khz - _tunedKhz) <= MatchKhz)
            .OrderBy(b => Math.Abs(b.Khz - _tunedKhz)).FirstOrDefault()?.Station;
    }

    private void Apply()
    {
        var q = Search.Trim();
        var rows = _all.Where(b => Filter switch
            {
                "Now" => b.IsOn || b.IsSoon,
                "Today" => b.Today,
                _ => true,
            })
            .Where(b => q == "" || b.Station.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || b.Language.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || b.Target.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || b.KhzText.Contains(q))
            .OrderByDescending(b => b.IsOn).ThenByDescending(b => b.IsSoon).ThenBy(b => b.Khz)
            .ToList();
        Items.Clear();
        foreach (var r in rows) Items.Add(r);
        Summary = $"{rows.Count(b => b.IsOn)} on now, {_all.Count} listed";
    }
}
