using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleDream.Radio;

/// <summary>
/// TCI client. SimpleDream only listens: the one way out is <see cref="Send"/>, and it
/// refuses every command that isn't on the receive-only list below, so nothing here can
/// key or tune a transmitter.
/// </summary>
public sealed class TciClient : IDisposable
{
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "iq_start", "iq_stop", "iq_samplerate", "vfo", "mute" };

    public delegate void IqHandler(ReadOnlySpan<float> iq, int rate);

    private const int HeaderBytes = 64;
    private const int IqStream = 0;
    private const int Int16Format = 0, Int24Format = 1, Int32Format = 2, Float32Format = 3;

    private readonly string _host;
    private readonly int _port;
    private readonly int _rx;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    /// <summary>IQ waiting to go to the decoder. TCI packets are tiny (about 1600 a second
    /// at 384 kHz); handing them over 25 times a second saves most of the thread wake-ups.</summary>
    private float[] _floats = new float[1 << 16];
    private int _pending;
    private int _pendingRate;
    private const int BatchesPerSecond = 25;

    public event Action? Changed;
    public event IqHandler? IqReceived;

    public bool Connected { get; private set; }
    public string Device { get; private set; } = "";
    public string Error { get; private set; } = "";
    public long VfoHz { get; private set; }
    public long DdsHz { get; private set; }
    public bool RadioMuted { get; private set; }
    public DateTime LastIq { get; private set; }

    public TciClient(string host, int port, int rx)
    {
        (_host, _port, _rx) = (host, port, rx);
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://{_host}:{_port}/"), _stop.Token);
                _ws = ws;
                Error = "";
                await ReceiveAsync(ws);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is WebSocketException or IOException or InvalidOperationException)
            {
                Error = e.InnerException?.Message ?? e.Message;
            }
            _ws = null;
            Connected = false;
            Changed?.Invoke();
            try { await Task.Delay(2000, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[1 << 17];
        var used = 0;
        while (ws.State == WebSocketState.Open)
        {
            if (used == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            var r = await ws.ReceiveAsync(buffer.AsMemory(used), _stop.Token);
            if (r.MessageType == WebSocketMessageType.Close) return;
            used += r.Count;
            if (!r.EndOfMessage) continue;
            if (r.MessageType == WebSocketMessageType.Text)
                await OnTextAsync(Encoding.UTF8.GetString(buffer, 0, used));
            else
                OnBinary(buffer.AsSpan(0, used));
            used = 0;
        }
    }

    private async Task OnTextAsync(string text)
    {
        var changed = false;
        foreach (var command in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = command.IndexOf(':');
            var verb = (colon < 0 ? command : command[..colon]).ToLowerInvariant();
            var args = colon < 0 ? [] : command[(colon + 1)..].Split(',');
            switch (verb)
            {
                case "device" when args.Length > 0:
                    Device = args[0];
                    changed = true;
                    break;
                case "ready":
                    Connected = true;
                    changed = true;
                    await SendAsync($"iq_samplerate:48000");
                    await SendAsync($"iq_start:{_rx}");
                    break;
                case "vfo" when args.Length >= 3 && Int(args[0]) == _rx && Int(args[1]) == 0:
                    VfoHz = Long(args[2]);
                    changed = true;
                    break;
                case "dds" when args.Length >= 2 && Int(args[0]) == _rx:
                    DdsHz = Long(args[1]);
                    changed = true;
                    break;
                case "mute" when args.Length >= 1:
                    RadioMuted = args[0].Equals("true", StringComparison.OrdinalIgnoreCase);
                    changed = true;
                    break;
            }
        }
        if (changed) Changed?.Invoke();
    }

    private void OnBinary(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= HeaderBytes) return;
        var receiver = BinaryPrimitives.ReadInt32LittleEndian(frame);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
        var format = BinaryPrimitives.ReadInt32LittleEndian(frame[8..]);
        var type = BinaryPrimitives.ReadInt32LittleEndian(frame[24..]);
        if (type != IqStream || receiver != _rx) return;
        var data = frame[HeaderBytes..];
        var count = format switch
        {
            Int16Format => data.Length / 2,
            Int24Format => data.Length / 3,
            Int32Format or Float32Format => data.Length / 4,
            _ => 0,
        };
        count &= ~1;
        if (count == 0) return;
        if (rate != _pendingRate)
        {
            _pending = 0;
            _pendingRate = rate;
        }
        var batch = Math.Max(rate / BatchesPerSecond * 2, count);
        if (_floats.Length < batch + count) Array.Resize(ref _floats, (batch + count) * 2);
        var dest = _floats.AsSpan(_pending, count);
        switch (format)
        {
            case Float32Format:
                MemoryMarshal.Cast<byte, float>(data)[..count].CopyTo(dest);
                break;
            case Int16Format:
                for (var i = 0; i < count; i++) dest[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768f;
                break;
            case Int24Format:
                for (var i = 0; i < count; i++)
                    dest[i] = ((data[i * 3 + 2] << 24) | (data[i * 3 + 1] << 16) | (data[i * 3] << 8)) / 2147483648f;
                break;
            case Int32Format:
                for (var i = 0; i < count; i++) dest[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) / 2147483648f;
                break;
        }
        LastIq = DateTime.UtcNow;
        _pending += count;
        if (_pending < batch) return;
        IqReceived?.Invoke(_floats.AsSpan(0, _pending), rate);
        _pending = 0;
    }

    private async Task SendAsync(string command)
    {
        var verb = command.Split(':', 2)[0];
        if (!Allowed.Contains(verb)) throw new InvalidOperationException($"TCI command not allowed: {verb}");
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(command + ";"), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException) { }
        finally { _sendLock.Release(); }
    }

    public void SetFrequency(long hz) => _ = SendAsync(string.Create(CultureInfo.InvariantCulture, $"vfo:{_rx},0,{hz}"));

    public void SetRadioMuted(bool muted) => _ = SendAsync($"mute:{(muted ? "true" : "false")}");

    public void Dispose()
    {
        try { SendAsync($"iq_stop:{_rx}").Wait(500); }
        catch (AggregateException) { }
        _stop.Cancel();
        try { _loop.Wait(1000); }
        catch (AggregateException) { }
        _stop.Dispose();
        _sendLock.Dispose();
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
    private static long Long(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
