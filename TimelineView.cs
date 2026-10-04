using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace keypaa;

internal static class Pal
{
    static readonly Typeface Face = new("Segoe UI");

    public static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public static Pen MakePen(Brush b, double thickness, bool dashed = false)
    {
        var p = new Pen(b, thickness);
        if (dashed) p.DashStyle = DashStyles.Dash;
        p.Freeze();
        return p;
    }

    public static FormattedText Txt(Visual v, string s, double size, Brush b) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b,
            VisualTreeHelper.GetDpi(v).PixelsPerDip);
}

/// <summary>Live scrolling piano-roll of every input, newest at the right edge (like an audio visualiser).</summary>
public sealed class TimelineView : FrameworkElement
{
    public EventStore? Store { get; set; }
    public double WindowSeconds { get; set; } = 6;
    public bool IsFrozen { get; private set; }
    long frozenNow;
    readonly Dictionary<string, FormattedText> cache = new();

    static readonly SolidColorBrush Bg = Pal.Hex("#23272B"), Stripe = Pal.Hex("#2A2F34"), Dim = Pal.Hex("#9CA3AF");
    static readonly Pen GridPen = Pal.MakePen(Pal.Hex("#3A4046"), 1);
    static readonly Pen CwPen = Pal.MakePen(Pal.Hex("#FDE047"), 1.5);
    static readonly Pen CcwPen = Pal.MakePen(Pal.Hex("#FB923C"), 1.5);
    static readonly SolidColorBrush[] LaneBrush =
    {
        Pal.Hex("#5EEAD4"), Pal.Hex("#C4B5FD"), Pal.Hex("#FCA5A5"), Pal.Hex("#FDE68A"),
        Pal.Hex("#93C5FD"), Pal.Hex("#F9A8D4"), Pal.Hex("#D1D5DB")
    };

    public void SetFrozen(bool frozen)
    {
        IsFrozen = frozen;
        if (frozen) frozenNow = Stopwatch.GetTimestamp();
        InvalidateVisual();
    }

    FormattedText Cached(string text, Brush b, double size = 11)
    {
        string key = text + "|" + size + "|" + b.GetHashCode();
        if (!cache.TryGetValue(key, out var ft)) cache[key] = ft = Pal.Txt(this, text, size, b);
        return ft;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        WindowSeconds = Math.Clamp(WindowSeconds * (e.Delta > 0 ? 0.8 : 1.25), 0.5, 60);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (Store == null) return;

        const double labelW = 62, axisH = 18;
        double plotW = w - labelW - 6, plotH = h - axisH;
        if (plotW < 40 || plotH < 40) return;

        int lanes = EventStore.LaneCount;
        double laneH = plotH / lanes;
        long freq = Stopwatch.Frequency;
        long now = IsFrozen ? frozenNow : Stopwatch.GetTimestamp();
        double span = WindowSeconds;
        long start = now - (long)(span * freq);
        double X(long t) => labelW + (t - start) / (span * freq) * plotW;

        // lane stripes + labels
        for (int i = 0; i < lanes; i++)
        {
            if (i % 2 == 0) dc.DrawRectangle(Stripe, null, new Rect(labelW, i * laneH, plotW, laneH));
            var lbl = Cached(EventStore.LaneNames[i], LaneBrush[i], 12);
            dc.DrawText(lbl, new Point(6, i * laneH + (laneH - lbl.Height) / 2));
        }

        // static grid measured back from "now"
        double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 20 };
        double step = steps.First(s => span / s <= 12);
        for (int k = 0; k * step <= span; k++)
        {
            double x = labelW + plotW - k * step / span * plotW;
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, plotH));
            string text = k == 0 ? "now" : "-" + (k * step).ToString("0.##") + "s";
            dc.DrawText(Cached(text, Dim, 10), new Point(x - 12, plotH + 3));
        }
        dc.DrawText(Cached($"{span:0.#}s", Dim, 10), new Point(6, plotH + 3));

        // key-held bars
        for (int lane = 0; lane < lanes; lane++)
        {
            var list = Store.Segs[lane];
            double y = lane * laneH;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var s = list[i];
                long end = s.End == 0 ? now : s.End;
                if (end < start) break;
                if (s.Start > now) continue;
                double x1 = Math.Max(labelW, X(s.Start));
                double x2 = Math.Min(labelW + plotW, X(Math.Min(end, now)));
                dc.DrawRectangle(LaneBrush[lane], null, new Rect(x1, y + 3, Math.Max(2, x2 - x1), laneH - 6));
            }
        }

        // encoder wheel notches (yellow = CW, orange = CCW)
        var wl = Store.Wheels;
        double wy = (lanes - 1) * laneH;
        for (int i = wl.Count - 1; i >= 0; i--)
        {
            var (t, dir) = wl[i];
            if (t < start) break;
            if (t > now) continue;
            double x = X(t);
            dc.DrawLine(dir > 0 ? CwPen : CcwPen, new Point(x, wy + 2), new Point(x, wy + laneH - 2));
        }
    }
}
