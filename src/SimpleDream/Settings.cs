using System.Text.Json;

namespace SimpleDream;

/// <summary>Small JSON settings file in the app data directory.</summary>
public sealed class Settings
{
    public string TciHost { get; set; } = "127.0.0.1";
    public int TciPort { get; set; } = 50001;
    public int Receiver { get; set; }
    public bool AutoConnect { get; set; } = true;
    public int StepHz { get; set; } = 5000;
    public double Volume { get; set; } = 80;
    public string OutputDevice { get; set; } = "";
    /// <summary>Auto, Off or On: whether the radio's IQ arrives with its spectrum mirrored.</summary>
    public string IqMirror { get; set; } = "Auto";
    public bool SmoothSbr { get; set; }
    public string Theme { get; set; } = "System";
    public string RecordDir { get; set; } = "";
    public double DataPanelWidth { get; set; } = 320;
    /// <summary>Broadcast list: column to sort by, direction, and language shown ("" for all).</summary>
    public string BroadcastSort { get; set; } = "Status";
    public bool BroadcastSortAscending { get; set; } = true;
    public string BroadcastLanguage { get; set; } = "";

    public static string Dir { get; } = MakeDir();
    public static string ScheduleDir { get; } = Directory.CreateDirectory(Path.Combine(Dir, "schedule")).FullName;
    private static string FilePath => Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string MakeDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "SimpleDream");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options)); }
        catch (IOException) { }
    }
}
