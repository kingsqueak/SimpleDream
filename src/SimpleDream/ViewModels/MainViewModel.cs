using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleDream.Radio;

namespace SimpleDream.ViewModels;

/// <summary>The app: connects TCI to the decoder and turns its status into what the window shows.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Lock is given up as a mirror problem after this long off-centre.</summary>
    private static readonly TimeSpan MirrorHunt = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoAudioAfter = TimeSpan.FromSeconds(3);

    public Settings Settings { get; } = Settings.Load();
    public BroadcastsViewModel Broadcasts { get; }
    public ObservableCollection<ServiceItem> Services { get; } = [];
    public ObservableCollection<JournalineEntry> JournalineEntries { get; } = [];

    private readonly nint _rx;
    private readonly DispatcherTimer _timer;
    private TciClient? _tci;
    private bool _disposed;
    private bool _mirror;
    private bool? _mirrorForced;
    private DateTime _huntFrom = DateTime.UtcNow;
    private long _lastVfo;
    private long _lastAudioOk = -1;
    private DateTime _lastAudioAt;
    private double _shiftHz;
    private bool _radioMutedBefore;
    private bool _weMutedRadio;
    private string _serviceKey = "";
    private string _journalineKey = "";
    private long _tuneTarget;
    private readonly List<string> _slides = [];
    private int _slideIndex = -1;

    public event Action<float[], float[][], double, double?, double?>? SpectrumUpdated;

    [ObservableProperty] private string _connectionLabel = "Not connected";
    [ObservableProperty] private string _connectionDetail = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _freqHead = "-.---.";
    [ObservableProperty] private string _freqTail = "---";
    [ObservableProperty] private double _stepHz;
    [ObservableProperty] private string _stepLabel = "";

    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isWeak;
    [ObservableProperty] private double _snr;
    [ObservableProperty] private string _snrText = "—";
    [ObservableProperty] private double _needsSnr = double.NaN;
    [ObservableProperty] private string _qualityText = "";

    [ObservableProperty] private string _station = "";
    [ObservableProperty] private string _stationDetail = "";
    [ObservableProperty] private string _technical = "";
    [ObservableProperty] private string _textMessage = "";
    [ObservableProperty] private bool _hasTextMessage;
    [ObservableProperty] private bool _hasManyServices;

    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _radioMuted;
    [ObservableProperty] private bool _isRecording;

    [ObservableProperty] private bool _hasSlides;
    [ObservableProperty] private string? _slidePath;
    [ObservableProperty] private string _slideCount = "";
    [ObservableProperty] private bool _hasJournaline;
    [ObservableProperty] private string _journalineTitle = "";
    [ObservableProperty] private string _journalineText = "";
    [ObservableProperty] private bool _journalineCanBack;
    [ObservableProperty] private bool _showJournaline;
    [ObservableProperty] private bool _hasData;

    // Config > Details
    [ObservableProperty] private string _detailsText = "";
    [ObservableProperty] private string _logText = "";

    public MainViewModel()
    {
        var config = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["play"] = Environment.GetEnvironmentVariable("SIMPLEDREAM_SILENT") == null,
            ["output_device"] = Settings.OutputDevice,
            ["volume"] = Gain(Settings.Volume),
            ["data_dir"] = Settings.Dir,
        });
        _rx = Native.Create(config);
        _volume = Settings.Volume;
        Broadcasts = new BroadcastsViewModel(this);
        SetStep(Settings.StepHz);
        ApplyMirrorSetting();
        Command(new { cmd = "smooth_sbr", on = Settings.SmoothSbr });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();

        if (Environment.GetEnvironmentVariable("SIMPLEDREAM_REPLAY") is { } replay) StartReplay(replay);
        else if (Settings.AutoConnect) Connect();
    }

    private static double Gain(double percent) => Math.Pow(Math.Clamp(percent, 0, 100) / 100, 2);

    private void Command(object o) => Native.Command(_rx, JsonSerializer.Serialize(o));

    /// <summary>(Re)connect with the current settings.</summary>
    [RelayCommand]
    public void Connect()
    {
        Disconnect();
        var tci = new TciClient(Settings.TciHost, Settings.TciPort, Settings.Receiver);
        tci.IqReceived += (iq, rate) => Native.PushIq(_rx, iq, rate, _shiftHz, _mirror);
        tci.Changed += () =>
        {
            _shiftHz = tci.VfoHz - tci.DdsHz;
            Dispatcher.UIThread.Post(OnTciChanged);
        };
        _tci = tci;
        ConnectionLabel = $"{Settings.TciHost}:{Settings.TciPort}";
        ConnectionDetail = "Connecting…";
    }

    public void Disconnect()
    {
        if (_tci is not { } tci) return;
        if (_weMutedRadio) tci.SetRadioMuted(_radioMutedBefore);
        _weMutedRadio = false;
        _tci = null;
        tci.Dispose();
        IsConnected = false;
    }

    /// <summary>Whether a radio's IQ is mirrored: Station Engine's is.</summary>
    private void ApplyMirrorSetting()
    {
        _mirrorForced = Settings.IqMirror switch { "On" => true, "Off" => false, _ => null };
        _mirror = _mirrorForced ?? _mirror;
        _huntFrom = DateTime.UtcNow;
    }

    public void SettingsChanged()
    {
        Settings.Save();
        ApplyMirrorSetting();
        Command(new { cmd = "smooth_sbr", on = Settings.SmoothSbr });
    }

    public void SelectOutputDevice(string name)
    {
        Settings.OutputDevice = name;
        Settings.Save();
        Command(new { cmd = "output_device", name });
    }

    public void RestartDecoder() => Command(new { cmd = "restart" });

    private void OnTciChanged()
    {
        if (_tci is not { } tci || _disposed) return;
        IsConnected = tci.Connected;
        ConnectionLabel = tci.Device != "" ? $"{tci.Device} {Settings.TciHost}" : $"{Settings.TciHost}:{Settings.TciPort}";
        ConnectionDetail = tci.Connected ? "Connected over TCI" : tci.Error != "" ? tci.Error : "Connecting…";
        if (_mirrorForced == null && tci.Connected && tci.Device.Contains("Zeus", StringComparison.OrdinalIgnoreCase)) _mirror = true;
        RadioMuted = tci.RadioMuted;
        if (tci.VfoHz != 0 && tci.VfoHz != _lastVfo) OnFrequency(tci.VfoHz);
    }

    private void OnFrequency(long hz)
    {
        var moved = Math.Abs(hz - _lastVfo) > 200;
        _lastVfo = hz;
        _tuneTarget = hz;
        ShowFrequency(hz);
        if (!moved) return;
        Command(new { cmd = "new_station" });
        ClearStation();
        Broadcasts.FrequencyChanged(hz / 1000.0);
    }

    private void ShowFrequency(long hz)
    {
        var s = hz.ToString("000000000");
        var mhz = s[..3].TrimStart('0');
        if (mhz == "") mhz = "0";
        FreqHead = $"{mhz}.{s[3..6]}.";
        FreqTail = s[6..];
    }

    private void ClearStation()
    {
        Station = "";
        StationDetail = "";
        Technical = "";
        TextMessage = "";
        HasTextMessage = false;
        Services.Clear();
        HasManyServices = false;
        _serviceKey = "";
        _huntFrom = DateTime.UtcNow;
        _lastAudioOk = -1;
        _lastAudioAt = default;
        _slides.Clear();
        _slideIndex = -1;
        SlidePath = null;
        HasSlides = false;
        HasJournaline = false;
        ShowJournaline = false;
        HasData = false;
        _journalineKey = "";
        JournalineEntries.Clear();
    }

    public void Tune(long hz)
    {
        if (hz <= 0 || _tci is not { Connected: true } tci) return;
        _tuneTarget = hz;
        tci.SetFrequency(hz);
    }

    public void TuneKhz(double khz) => Tune((long)Math.Round(khz * 1000));

    /// <summary>Mouse wheel on the frequency: moves by the step, on the step's grid.</summary>
    public void TuneSteps(int steps)
    {
        var from = _tuneTarget != 0 ? _tuneTarget : _lastVfo;
        if (from == 0) return;
        var step = (long)StepHz;
        Tune((from / step + steps) * step + (steps < 0 && from % step != 0 ? step : 0));
    }

    private void SetStep(int hz)
    {
        StepHz = hz;
        StepLabel = hz >= 1000 ? $"{hz / 1000} kHz" : $"{hz} Hz";
    }

    [RelayCommand]
    private void PickStep(string hz)
    {
        SetStep(int.Parse(hz));
        Settings.StepHz = (int)StepHz;
        Settings.Save();
    }

    partial void OnVolumeChanged(double value)
    {
        Settings.Volume = value;
        Settings.Save();
        if (!IsMuted) Command(new { cmd = "volume", gain = Gain(value) });
    }

    partial void OnIsMutedChanged(bool value) => Command(new { cmd = "volume", gain = value ? 0 : Gain(Volume) });

    [RelayCommand] private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>Mute the radio's own audio. It's put back on exit if we changed it.</summary>
    [RelayCommand]
    private void ToggleRadioMute()
    {
        if (_tci is not { } tci) return;
        if (!_weMutedRadio)
        {
            _radioMutedBefore = tci.RadioMuted;
            _weMutedRadio = true;
        }
        tci.SetRadioMuted(!RadioMuted);
    }

    [RelayCommand]
    private void ToggleRecord()
    {
        if (IsRecording)
        {
            Command(new { cmd = "record_stop" });
            return;
        }
        var dir = Settings.RecordDir != "" ? Settings.RecordDir : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        Directory.CreateDirectory(dir);
        var name = $"SimpleDream {DateTime.Now:yyyy-MM-dd HHmmss} {_lastVfo / 1000} kHz.wav";
        Command(new { cmd = "record_start", path = Path.Combine(dir, name) });
    }

    [RelayCommand] private void JournalineBack() => Command(new { cmd = "jl_back" });
    [RelayCommand] private void JournalineHome() => Command(new { cmd = "jl_home" });
    [RelayCommand] private void ShowPictures() => ShowJournaline = false;
    [RelayCommand] private void ShowNews() => ShowJournaline = true;

    [RelayCommand]
    private void SlideBack() => StepSlide(-1);
    [RelayCommand]
    private void SlideForward() => StepSlide(1);

    private void StepSlide(int by)
    {
        if (_slides.Count == 0) return;
        _slideIndex = Math.Clamp(_slideIndex + by, 0, _slides.Count - 1);
        SlidePath = _slides[_slideIndex];
        SlideCount = $"{_slideIndex + 1} of {_slides.Count}";
    }

    private void Poll()
    {
        if (_disposed) return;
        var events = Native.Events(_rx);
        if (events.Length > 2) OnEvents(events);
        var status = Native.Status(_rx);
        if (status.Length > 0) OnStatus(status);
    }

    private void OnEvents(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            switch (e.GetProperty("type").GetString())
            {
                case "slide":
                    var atEnd = _slideIndex == _slides.Count - 1;
                    _slides.Add(e.GetProperty("path").GetString()!);
                    if (atEnd) _slideIndex = _slides.Count - 1;
                    SlidePath = _slides[_slideIndex];
                    SlideCount = $"{_slideIndex + 1} of {_slides.Count}";
                    HasSlides = true;
                    break;
            }
        }
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static long Int(JsonElement e, string key) => (long)(Num(e, key) ?? 0);

    private void OnStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var state = Str(r, "state") ?? "Acquisition";
        var snr = Num(r, "snr_db");
        var needs = Num(r, "needs_snr_db");
        var audioOk = Int(r, "audio_ok");
        var mscOk = Int(r, "msc_ok");
        var mscBad = Int(r, "msc_bad");
        if (audioOk != _lastAudioOk)
        {
            if (_lastAudioOk >= 0) _lastAudioAt = DateTime.UtcNow;
            _lastAudioOk = audioOk;
        }
        var flowing = DateTime.UtcNow - _lastAudioAt < NoAudioAfter;
        var haveIq = _tci is { } t ? DateTime.UtcNow - t.LastIq < TimeSpan.FromSeconds(2) : _replaying;
        var locked = state == "Locked";

        Snr = snr ?? 0;
        SnrText = snr is { } s ? $"{s:0.0} dB" : "—";
        NeedsSnr = needs ?? double.NaN;
        var total = mscOk + mscBad;
        QualityText = total > 0 ? $"{100.0 * mscOk / total:0}% good" : "";

        IsPlaying = flowing;
        IsWeak = locked && !flowing && needs is { } n && (snr ?? 0) < n;
        (StatusText, StatusDetail) = true switch
        {
            _ when !haveIq && !IsConnected && !_replaying => ("Not connected", ConnectionDetail),
            _ when !haveIq => ("Waiting for IQ", "The radio isn't sending IQ"),
            _ when flowing => ("Playing", ""),
            _ when IsWeak => ("Too weak", $"This station needs about {needs:0} dB"),
            _ when locked => ("Locked", "Waiting for audio"),
            _ when state == "Tracking" => ("Syncing", ""),
            _ => ("No DRM signal", "Searching"),
        };

        // The radio gave no lock off-centre: its IQ may be mirrored the other way.
        if (_mirrorForced == null && state != "Acquisition") _huntFrom = DateTime.UtcNow;
        if (_mirrorForced == null && Math.Abs(_shiftHz) > 100 && DateTime.UtcNow - _huntFrom > MirrorHunt)
        {
            _mirror = !_mirror;
            _huntFrom = DateTime.UtcNow;
        }

        UpdateServices(r);
        UpdateJournaline(r);
        HasData = HasSlides || HasJournaline;
        if (!HasSlides && HasJournaline) ShowJournaline = true;
        var recording = Str(r, "recording");
        IsRecording = recording != null;

        var text = Str(r, "text")?.Trim() ?? "";
        TextMessage = text;
        HasTextMessage = text != "";

        UpdateSpectrum(r);
        UpdateDetails(r, state);
    }

    private void UpdateServices(JsonElement r)
    {
        var selected = (int)(Num(r, "selected") ?? -1);
        var services = r.GetProperty("services");
        var key = services.GetRawText();
        JsonElement? current = null;
        foreach (var s in services.EnumerateArray())
            if ((int)Int(s, "id") == selected) current = s;
        if (key != _serviceKey)
        {
            _serviceKey = key;
            Services.Clear();
            foreach (var s in services.EnumerateArray())
            {
                if (s.GetProperty("audio").GetBoolean())
                    Services.Add(new ServiceItem((int)Int(s, "id"), Str(s, "label") is { Length: > 0 } l ? l : $"Service {Int(s, "id") + 1}",
                        id => Command(new { cmd = "service", id })));
            }
            HasManyServices = Services.Count > 1;
        }
        foreach (var item in Services) item.IsSelected = item.Id == selected;
        if (current is not { } c)
        {
            Station = Broadcasts.ScheduledHere ?? "";
            StationDetail = Station != "" ? "Scheduled here" : "Nothing scheduled on this frequency. Open Broadcasts to find a station.";
            Technical = "";
            return;
        }
        Station = Str(c, "label") is { Length: > 0 } label ? label : Broadcasts.ScheduledHere ?? "DRM station";
        // Stations often send a transmitter label, so the schedule's name helps.
        var scheduled = Broadcasts.ScheduledHere is { } name && name != Station ? name : null;
        StationDetail = string.Join(" · ", new[] { scheduled, Str(c, "language"), Str(c, "programme"), Str(c, "country") }.Where(x => !string.IsNullOrEmpty(x)));
        var bits = new List<string?> { Str(c, "codec") };
        if (Num(c, "bitrate") is { } rate) bits.Add($"{rate / 1000:0.0} kbit/s");
        if (Str(r, "mode") is { } m) bits.Add($"Mode {m}");
        if (Num(r, "occupancy_khz") is { } kHz) bits.Add($"{kHz:0.#} kHz");
        bits.Add(Str(r, "msc_mode"));
        if (Str(c, "warning") is { } w) bits.Add(w);
        else if (c.GetProperty("decodable").GetBoolean() == false) bits.Add("this audio can't be decoded");
        Technical = string.Join(" · ", bits.Where(x => !string.IsNullOrEmpty(x)));
    }

    private void UpdateJournaline(JsonElement r)
    {
        if (!r.TryGetProperty("journaline", out var j) || j.ValueKind != JsonValueKind.Object)
        {
            HasJournaline = false;
            return;
        }
        HasJournaline = true;
        var key = j.GetRawText();
        if (key == _journalineKey) return;
        _journalineKey = key;
        JournalineTitle = Str(j, "title") ?? "";
        JournalineText = Str(j, "text") ?? "";
        JournalineCanBack = j.TryGetProperty("can_back", out var b) && b.GetBoolean();
        JournalineEntries.Clear();
        var i = 0;
        foreach (var e in j.GetProperty("entries").EnumerateArray())
        {
            JournalineEntries.Add(new JournalineEntry(i, Str(e, "text") ?? "", e.GetProperty("available").GetBoolean(),
                index => Command(new { cmd = "jl_follow", index })));
            i++;
        }
    }

    private void UpdateSpectrum(JsonElement r)
    {
        if (SpectrumUpdated is null) return;
        var sp = r.GetProperty("spectrum");
        var db = sp.GetProperty("db");
        if (db.GetArrayLength() == 0) return;
        var line = new float[db.GetArrayLength()];
        var i = 0;
        foreach (var v in db.EnumerateArray()) line[i++] = v.GetSingle();
        var wf = r.GetProperty("waterfall");
        var rows = new float[wf.GetArrayLength()][];
        i = 0;
        foreach (var row in wf.EnumerateArray())
        {
            var a = new float[row.GetArrayLength()];
            var k = 0;
            foreach (var v in row.EnumerateArray()) a[k++] = v.GetSingle();
            rows[i++] = a;
        }
        double? low = null, high = null;
        if (sp.TryGetProperty("band_hz", out var band) && band.ValueKind == JsonValueKind.Array)
        {
            low = band[0].GetDouble();
            high = band[1].GetDouble();
        }
        SpectrumUpdated(line, rows, sp.GetProperty("span_hz").GetDouble(), low, high);
    }

    private void UpdateDetails(JsonElement r, string state)
    {
        DetailsText = string.Join("\n",
            $"State             {state}, {(Str(r, "mode") is { } m ? "mode " + m : "no mode yet")}",
            $"SNR               {Num(r, "snr_db"):0.0} dB   MER {Num(r, "mer_db"):0.0} dB   WMER {Num(r, "wmer_db"):0.0} dB",
            $"Channel           Doppler {Num(r, "doppler_hz"):0.00} Hz   delay {Num(r, "delay_ms"):0.00} ms",
            $"Offsets           DC {Num(r, "dc_hz"):0.0} Hz   clock {Num(r, "sro_hz"):0.00} Hz   mirrored IQ {(_mirror ? "yes" : "no")}",
            $"Modulation        data {Str(r, "msc_mode")}   control {Str(r, "sdc_mode")}   {Str(r, "interleaving")} interleave",
            $"FAC               {Int(r, "fac_ok")} good, {Int(r, "fac_bad")} bad",
            $"SDC               {Int(r, "sdc_ok")} good, {Int(r, "sdc_bad")} bad",
            $"Programme data    {Int(r, "msc_ok")} good, {Int(r, "msc_bad")} bad",
            $"Audio             {Str(r, "codec")}: {Int(r, "audio_ok")} good, {Int(r, "audio_bad")} concealed",
            $"Sound card        buffer {Num(r, "buffer_ms"):0} ms   drift {Num(r, "drift_ppm"):0.0} ppm",
            $"Input             {Num(r, "level_dbfs"):0.0} dBFS   shift {_shiftHz:0} Hz   dropped packets {Int(r, "dropped")}",
            $"Broadcast time    {Str(r, "time_utc")}");
        var log = r.GetProperty("log");
        LogText = string.Join("\n", log.EnumerateArray().Select(x => x.GetString()));
    }

    private bool _replaying;

    /// <summary>Development aid: SIMPLEDREAM_REPLAY=file,rate,shift,mirror plays a raw f32 capture in real time.</summary>
    private void StartReplay(string spec)
    {
        var a = spec.Split(',');
        var rate = int.Parse(a[1]);
        _shiftHz = double.Parse(a[2]);
        _mirror = a[3] == "1";
        _mirrorForced = _mirror;
        _replaying = true;
        ConnectionLabel = "Replay";
        ConnectionDetail = Path.GetFileName(a[0]);
        if (a.Length > 4) { _lastVfo = long.Parse(a[4]); ShowFrequency(_lastVfo); Broadcasts.FrequencyChanged(_lastVfo / 1000.0); }
        _ = Task.Run(async () =>
        {
            var bytes = await File.ReadAllBytesAsync(a[0]);
            const int pairs = 2400;
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            var start = DateTime.UtcNow;
            for (var i = 0; i + pairs * 2 <= floats.Length && !_disposed; i += pairs * 2)
            {
                Native.PushIq(_rx, floats.AsSpan(i, pairs * 2), rate, _shiftHz, _mirror);
                var due = TimeSpan.FromSeconds((i / 2 + pairs) / (double)rate) - (DateTime.UtcNow - start);
                if (due > TimeSpan.Zero) await Task.Delay(due);
            }
            _replaying = false;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        Disconnect();
        Settings.Save();
        Native.Destroy(_rx);
    }
}
