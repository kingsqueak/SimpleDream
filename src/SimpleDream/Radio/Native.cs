using System.Runtime.InteropServices;
using System.Text;

namespace SimpleDream.Radio;

/// <summary>The Rust bridge to DecDRM. Strings are UTF-8 JSON.</summary>
internal static partial class Native
{
    private const string Lib = "simpledream_native";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sd_new(string config);
    [LibraryImport(Lib)]
    private static partial void sd_free(nint h);
    [LibraryImport(Lib)]
    private static unsafe partial void sd_push_iq(nint h, float* iq, int pairs, int rate, double shiftHz, int mirror);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void sd_command(nint h, string cmd);
    [LibraryImport(Lib)]
    private static partial nint sd_status(nint h);
    [LibraryImport(Lib)]
    private static partial nint sd_events(nint h);
    [LibraryImport(Lib)]
    private static partial nint sd_output_devices();
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sd_schedule_sources(string dir);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sd_schedule(string dir);
    [LibraryImport(Lib)]
    private static partial void sd_string_free(nint p);

    private static string Take(nint p)
    {
        if (p == 0) return "";
        var s = Marshal.PtrToStringUTF8(p) ?? "";
        sd_string_free(p);
        return s;
    }

    public static nint Create(string config) => sd_new(config);
    public static void Destroy(nint h) => sd_free(h);
    public static void Command(nint h, string json) => sd_command(h, json);
    public static string Status(nint h) => Take(sd_status(h));
    public static string Events(nint h) => Take(sd_events(h));
    public static string OutputDevices() => Take(sd_output_devices());
    public static string ScheduleSources(string dir) => Take(sd_schedule_sources(dir));
    public static string Schedule(string dir) => Take(sd_schedule(dir));

    public static unsafe void PushIq(nint h, ReadOnlySpan<float> iq, int rate, double shiftHz, bool mirror)
    {
        fixed (float* p = iq)
            sd_push_iq(h, p, iq.Length / 2, rate, shiftHz, mirror ? 1 : 0);
    }
}
