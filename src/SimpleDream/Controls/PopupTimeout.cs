using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SimpleDream.Controls;

/// <summary>
/// Popups and menus close by themselves after 8 s with no activity in them.
/// Activity in any popup counts for all, so a menu opened from a popup keeps
/// its parent open.
/// </summary>
public static class PopupTimeout
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly HashSet<Popup> Open = [];
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private static DateTime _lastActivity;

    private static void Touch() => _lastActivity = DateTime.UtcNow;

    public static void Enable()
    {
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, e) =>
        {
            if (e.NewValue is true && popup.Child is not ToolTip)
            {
                Open.Add(popup);
                _lastActivity = DateTime.UtcNow;
                Timer.Start();
            }
            else if (e.NewValue is false && Open.Remove(popup) && Open.Count == 0)
                Timer.Stop();
        });
        Timer.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lastActivity < Timeout) return;
            foreach (var popup in Open.ToArray()) popup.Close();
        };
        const RoutingStrategies Route = RoutingStrategies.Tunnel;
        InputElement.PointerMovedEvent.AddClassHandler<PopupRoot>((_, _) => Touch(), Route, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<PopupRoot>((_, _) => Touch(), Route, handledEventsToo: true);
        InputElement.PointerWheelChangedEvent.AddClassHandler<PopupRoot>((_, _) => Touch(), Route, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<PopupRoot>((_, _) => Touch(), Route, handledEventsToo: true);
    }
}
