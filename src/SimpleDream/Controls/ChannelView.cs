using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SimpleDream.ViewModels;

namespace SimpleDream.Controls;

/// <summary>The channel the decoder sees: spectrum over waterfall, with the DRM block marked.</summary>
public sealed class ChannelView : Control
{
    private const int WfRows = 300;
    private const double SpectrumShare = 0.36;
    private static readonly uint[] Palette = BuildPalette();
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IPen Trace = new Pen(new SolidColorBrush(Color.Parse("#7FB2FF")), 1);
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    private static readonly IBrush Band = new SolidColorBrush(Color.FromArgb(46, 59, 130, 246));
    private static readonly IBrush Axis = new SolidColorBrush(Color.Parse("#8A93A3"));

    private WriteableBitmap? _wf;
    private int _wfWidth;
    private int _top;
    private float[] _line = [];
    private double _span = 48000, _floor = -120, _ceiling = -60;
    private double? _low, _high;
    private MainViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.SpectrumUpdated -= OnSpectrum;
        _vm = DataContext as MainViewModel;
        if (_vm != null) _vm.SpectrumUpdated += OnSpectrum;
    }

    private void OnSpectrum(float[] line, float[][] rows, double span, double? low, double? high)
    {
        (_line, _span, _low, _high) = (line, span, low, high);
        var sorted = (float[])line.Clone();
        Array.Sort(sorted);
        var floor = sorted[sorted.Length / 4] - 6.0;
        var ceiling = Math.Max(sorted[^1] + 6.0, floor + 30);
        _floor += (floor - _floor) * 0.2;
        _ceiling += (ceiling - _ceiling) * 0.2;
        foreach (var r in rows) AddRow(r);
        InvalidateVisual();
    }

    private unsafe void AddRow(float[] row)
    {
        if (_wf == null || _wfWidth != row.Length)
        {
            _wfWidth = row.Length;
            _top = 0;
            _wf?.Dispose();
            _wf = new WriteableBitmap(new PixelSize(_wfWidth, WfRows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var clear = _wf.Lock();
            new Span<uint>((void*)clear.Address, clear.RowBytes / 4 * WfRows).Fill(Palette[0]);
        }
        _top = (_top + WfRows - 1) % WfRows;
        using var fb = _wf.Lock();
        var dst = new Span<uint>((byte*)fb.Address + _top * fb.RowBytes, _wfWidth);
        var scale = 255 / Math.Max(_ceiling - _floor, 1);
        for (var i = 0; i < row.Length; i++)
            dst[i] = Palette[Math.Clamp((int)((row[i] - _floor) * scale), 0, 255)];
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(Bg, new Rect(0, 0, w, h));
        var specH = h * SpectrumShare;
        var X = (double hz) => (hz + _span / 2) / _span * w;

        if (_wf != null)
        {
            var below = new Rect(0, specH, w, h - specH);
            // The ring buffer draws in two parts: newest at the top.
            var first = WfRows - _top;
            var firstH = below.Height * first / WfRows;
            ctx.DrawImage(_wf, new Rect(0, _top, _wfWidth, first), new Rect(0, specH, w, firstH));
            if (_top > 0) ctx.DrawImage(_wf, new Rect(0, 0, _wfWidth, _top), new Rect(0, specH + firstH, w, below.Height - firstH));
        }

        if (_low is { } lo && _high is { } hi)
            ctx.FillRectangle(Band, new Rect(X(lo), 0, X(hi) - X(lo), specH));

        for (var k = -20; k <= 20; k += 5)
        {
            var x = X(k * 1000);
            ctx.DrawLine(Grid, new Point(x, 0), new Point(x, specH));
            if (k % 10 != 0) continue;
            var text = new FormattedText(k == 0 ? "0" : $"{k:+0;-0} kHz", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, Axis);
            ctx.DrawText(text, new Point(x + 4, 2));
        }

        if (_line.Length > 1)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (var i = 0; i < _line.Length; i++)
                {
                    var y = specH * (1 - Math.Clamp((_line[i] - _floor) / Math.Max(_ceiling - _floor, 1), 0, 1));
                    var p = new Point(w * i / (_line.Length - 1), y);
                    if (i == 0) g.BeginFigure(p, false); else g.LineTo(p);
                }
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, Trace, geo);
        }
    }

    // Same ramp as SimpleHPSDR's waterfall.
    private static uint[] BuildPalette()
    {
        (double at, byte r, byte g, byte b)[] stops =
        [
            (0.00, 0x0B, 0x0D, 0x11), (0.20, 0x0A, 0x1E, 0x5A), (0.40, 0x10, 0x6B, 0xD8),
            (0.60, 0x2E, 0xD3, 0xC8), (0.75, 0xF5, 0xD0, 0x3B), (0.90, 0xF2, 0x5F, 0x2C), (1.00, 0xFF, 0xFF, 0xFF),
        ];
        var lut = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var t = i / 255.0;
            var k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1].at) k++;
            var (a, c) = (stops[k], stops[k + 1]);
            var f = (t - a.at) / (c.at - a.at);
            byte L(byte x, byte y) => (byte)(x + (y - x) * f);
            lut[i] = 0xFF000000u | (uint)(L(a.r, c.r) << 16) | (uint)(L(a.g, c.g) << 8) | L(a.b, c.b);
        }
        return lut;
    }
}
