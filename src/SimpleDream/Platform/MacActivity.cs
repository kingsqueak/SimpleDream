using System.Runtime.InteropServices;

namespace SimpleDream.Platform;

/// <summary>
/// Tells macOS this app is doing latency-critical, user-initiated work, so
/// App Nap and timer coalescing leave it alone when its window is hidden or
/// on another Space. No-op elsewhere.
/// </summary>
public static class MacActivity
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const ulong NSActivityUserInitiated = 0x00FFFFFFUL | (1UL << 20);
    private const ulong NSActivityLatencyCritical = 0xFF00000000UL;
    private static IntPtr _token;

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, string arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendActivity(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);

    public static void Begin(string reason)
    {
        if (!OperatingSystem.IsMacOS() || _token != IntPtr.Zero) return;
        try
        {
            var info = Send(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
            var text = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), reason);
            _token = SendActivity(info, sel_registerName("beginActivityWithOptions:reason:"),
                NSActivityUserInitiated | NSActivityLatencyCritical, text);
            Send(_token, sel_registerName("retain"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
