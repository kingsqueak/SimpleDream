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
    /// <summary>DRM channels sit 5 kHz apart, so anything wider names the station next door.</summary>
    private const double MatchKhz = 1;
    private const string AllLanguages = "All languages";

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _clock;
    private List<BroadcastItem> _all = [];
    private double _tunedKhz;

    public ObservableCollection<BroadcastItem> Items { get; } = [];
    public ObservableCollection<string> Languages { get; } = [AllLanguages];

    [ObservableProperty] private string _filter = "Now";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _sourceStatus = "";
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private string _language = AllLanguages;
    private string _sort;
    private bool _ascending;
    private string _shown = "";

    public string TimeZoneName => TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName;
    public bool FilterNow => Filter == "Now";
    public bool FilterToday => Filter == "Today";
    public bool FilterAll => Filter == "All";

    public string KhzHeader => Header("kHz", "kHz");
    public string StatusHeader => Header("Status", "Status");
    public string TimeHeader => Header("Time", "Local time");
    public string StationHeader => Header("Station", "Station");
    public string LanguageHeader => Header("Language", "Language");

    private string Header(string column, string title) =>
        column == _sort ? $"{title} {(_ascending ? "▲" : "▼")}" : title;

    /// <summary>The nearest scheduled station on the tuned frequency, on the air now.</summary>
    public string? ScheduledHere { get; private set; }

    public BroadcastsViewModel(MainViewModel main)
    {
        _main = main;
        _sort = main.Settings.BroadcastSort;
        _ascending = main.Settings.BroadcastSortAscending;
        var saved = main.Settings.BroadcastLanguage;
        Reload();
        if (saved != "")
        {
            if (!Languages.Contains(saved)) Languages.Add(saved);
            Language = saved;
        }
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

    partial void OnLanguageChanged(string value)
    {
        // The list being rebuilt clears the selection for a moment.
        if (string.IsNullOrEmpty(value)) return;
        _main.Settings.BroadcastLanguage = value == AllLanguages ? "" : value;
        _main.Settings.Save();
        Apply();
    }

    /// <summary>Click a heading to sort by it; click again to reverse.</summary>
    [RelayCommand]
    private void Sort(string column)
    {
        _ascending = column != _sort || !_ascending;
        _sort = column;
        _main.Settings.BroadcastSort = _sort;
        _main.Settings.BroadcastSortAscending = _ascending;
        _main.Settings.Save();
        foreach (var name in new[] { nameof(KhzHeader), nameof(StatusHeader), nameof(TimeHeader), nameof(StationHeader), nameof(LanguageHeader) })
            OnPropertyChanged(name);
        Apply();
    }

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
        UpdateLanguages();
        Match();
        Apply();
    }

    /// <summary>Only touches the dropdown when the set of languages changes, so it keeps its selection.</summary>
    private void UpdateLanguages()
    {
        var chosen = Language;
        var found = _all.Select(b => b.Language.Trim()).Where(l => l != "").Distinct(StringComparer.OrdinalIgnoreCase);
        if (chosen != AllLanguages) found = found.Append(chosen).Distinct(StringComparer.OrdinalIgnoreCase);
        var list = found.Order(StringComparer.OrdinalIgnoreCase).Prepend(AllLanguages).ToList();
        if (list.SequenceEqual(Languages)) return;
        Languages.Clear();
        foreach (var l in list) Languages.Add(l);
        Language = chosen;
    }

    private void Match()
    {
        ScheduledHere = _all.Where(b => b.IsOn && Math.Abs(b.Khz - _tunedKhz) <= MatchKhz)
            .OrderBy(b => Math.Abs(b.Khz - _tunedKhz)).FirstOrDefault()?.Station;
    }

    /// <summary>The chosen order, ties by frequency. Blank text sorts last either way.</summary>
    private IEnumerable<BroadcastItem> Sorted(IEnumerable<BroadcastItem> rows)
    {
        IOrderedEnumerable<BroadcastItem> By<T>(Func<BroadcastItem, T> key) =>
            _ascending ? rows.OrderBy(key) : rows.OrderByDescending(key);
        IOrderedEnumerable<BroadcastItem> ByText(Func<BroadcastItem, string> key)
        {
            var blankLast = rows.OrderBy(b => key(b).Trim() == "");
            return _ascending
                ? blankLast.ThenBy(key, StringComparer.OrdinalIgnoreCase)
                : blankLast.ThenByDescending(key, StringComparer.OrdinalIgnoreCase);
        }
        return (_sort switch
        {
            "kHz" => By(b => b.Khz),
            "Time" => By(b => b.LocalStart),
            "Station" => ByText(b => b.Station),
            "Language" => ByText(b => b.Language),
            _ => By(b => b.StateRank),
        }).ThenBy(b => b.Khz).ThenBy(b => b.StartUtc);
    }

    private void Apply()
    {
        var q = Search.Trim();
        var found = _all.Where(b => Filter switch
            {
                "Now" => b.IsOn || b.IsSoon,
                "Today" => b.Today,
                _ => true,
            })
            .Where(b => q == "" || b.Station.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || b.Language.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || b.Target.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || b.KhzText.Contains(q))
            .Where(b => Language is AllLanguages or "" || b.Language.Trim().Equals(Language, StringComparison.OrdinalIgnoreCase));
        var rows = Sorted(found).ToList();
        // The list refreshes against the clock: leave it, and its scroll position, alone if nothing moved.
        var shown = string.Join("|", rows.Select(b => $"{b.Khz}{b.StartUtc}{b.State}{b.Station}"));
        if (shown != _shown)
        {
            _shown = shown;
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
        }
        Summary = $"{rows.Count(b => b.IsOn)} on now, {_all.Count} listed";
    }
}
