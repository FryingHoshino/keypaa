using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace keypaa;

public struct ScopeSample { public long T; public ushort V0, V1, R0, R1; public byte F; }   // T = device micros (unwrapped)
public sealed class ScopeCfg { public int Idle = 600, Full = 900, Top = 14, Bot = 10, RtP = 10, RtR = 10; }

/// <summary>Live oscilloscope of both HE sensors with idle/full/deadzone lines, the rapid-trigger threshold and press/release markers.</summary>
public sealed class ScopeView : FrameworkElement
{
    public readonly List<ScopeSample> Samples = new();
    public readonly ScopeCfg[] Cfg = { new(), new() };
    public double WindowSeconds { get; set; } = 2;
    public bool IsFrozen { get; private set; }
    long frozenT;

    static readonly SolidColorBrush Bg = Pal.Hex("#23272B"), Dim = Pal.Hex("#9CA3AF"), Green = Pal.Hex("#4ADE80"), Red = Pal.Hex("#F87171");
    static readonly SolidColorBrush[] KeyBrush = { Pal.Hex("#5EEAD4"), Pal.Hex("#C4B5FD") };
    static readonly Pen GridPen = Pal.MakePen(Pal.Hex("#3A4046"), 1), RefPen = Pal.MakePen(Pal.Hex("#6B7280"), 1, true);
    static readonly Pen TrigPen = Pal.MakePen(Pal.Hex("#FBBF24"), 1.2, true);
    static readonly Pen[] LinePen = { Pal.MakePen(KeyBrush[0], 1.4), Pal.MakePen(KeyBrush[1], 1.4) };

    public void Add(ScopeSample s) { Samples.Add(s); if (Samples.Count > 120000) Samples.RemoveRange(0, 40000); }
    public void Clear() { Samples.Clear(); InvalidateVisual(); }
    public void SetFrozen(bool f) { IsFrozen = f; if (f && Samples.Count > 0) frozenT = Samples[^1].T; InvalidateVisual(); }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        WindowSeconds = Math.Clamp(WindowSeconds * (e.Delta > 0 ? 0.8 : 1.25), 0.05, 30);
        InvalidateVisual();
        e.Handled = true;
    }

    int Lower(long t)
    {
        int lo = 0, hi = Samples.Count;
        while (lo < hi) { int m = (lo + hi) / 2; if (Samples[m].T < t) lo = m + 1; else hi = m; }
        return lo;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (w < 100 || h < 140) return;
        const double L = 10, R = 10, axis = 18;
        double pw = w - L - R, ph = (h - axis - 12) / 2;
        double span = WindowSeconds * 1e6;
        long endT = IsFrozen ? frozenT : (Samples.Count > 0 ? Samples[^1].T : 0);
        long startT = endT - (long)span;
        int lo = Lower(startT), hi = Samples.Count;

        double[] steps = { 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5 };
        double step = steps.First(s => WindowSeconds / s <= 10);
        for (int k = 0; k * step <= WindowSeconds; k++)
        {
            double x = L + pw - k * step / WindowSeconds * pw;
            dc.DrawLine(GridPen, new Point(x, 4), new Point(x, h - axis));
            dc.DrawText(Pal.Txt(this, k == 0 ? "now" : $"-{k * step:0.###}s", 10, Dim), new Point(x - 12, h - axis + 2));
        }
        dc.DrawText(Pal.Txt(this, $"{WindowSeconds:0.##}s", 10, Dim), new Point(L, h - axis + 2));

        for (int key = 0; key < 2; key++)
            DrawPlot(dc, key, new Rect(L, 4 + key * (ph + 4), pw, ph), startT, span, lo, hi);
    }

    void DrawPlot(DrawingContext dc, int key, Rect r, long startT, double span, int lo, int hi)
    {
        var c = Cfg[key];
        bool known = c.Full > c.Idle + 20;
        double ymin = known ? Math.Max(0, c.Idle - 25) : 0, ymax = known ? Math.Min(1023, c.Full + 25) : 1023;
        double Y(double v) => r.Bottom - (v - ymin) / (ymax - ymin) * r.Height;
        double X(long t) => r.Left + (t - startT) / span * r.Width;

        dc.PushClip(new RectangleGeometry(r));
        foreach (var (level, lbl) in new (int, string)[] { (c.Idle, "idle"), (c.Full, "full"), (c.Idle + c.Top, "top DZ"), (c.Full - c.Bot, "bottom DZ") })
        {
            double y = Y(level);
            dc.DrawLine(RefPen, new Point(r.Left, y), new Point(r.Right, y));
            dc.DrawText(Pal.Txt(this, $"{lbl} {level}", 9, Dim), new Point(r.Left + 3, y - 12));
        }

        Func<ScopeSample, double> val = s => key == 0 ? s.V0 : s.V1;
        Func<ScopeSample, double> trig = s =>
        {
            int rf = key == 0 ? s.R0 : s.R1;
            return ((s.F >> key) & 1) == 1 ? rf - c.RtR : rf + c.RtP;   // level the next movement must cross
        };
        var tg = Line(r, lo, hi, startT, span, trig, Y);
        if (tg != null) dc.DrawGeometry(null, TrigPen, tg);
        var vg = Line(r, lo, hi, startT, span, val, Y);
        if (vg != null) dc.DrawGeometry(null, LinePen[key], vg);

        // press (green, below) / release (red, above) markers
        var up = new StreamGeometry(); var dn = new StreamGeometry();
        int marks = 0;
        using (var cu = up.Open()) using (var cd = dn.Open())
        {
            for (int i = Math.Max(lo, 1); i < hi && marks < 400; i++)
            {
                bool a = ((Samples[i - 1].F >> key) & 1) == 1, b = ((Samples[i].F >> key) & 1) == 1;
                if (a == b) continue;
                marks++;
                double x = X(Samples[i].T), y = Y(val(Samples[i]));
                if (b) { cu.BeginFigure(new Point(x, y + 3), true, true); cu.LineTo(new Point(x - 5, y + 12), true, false); cu.LineTo(new Point(x + 5, y + 12), true, false); }
                else { cd.BeginFigure(new Point(x, y - 3), true, true); cd.LineTo(new Point(x - 5, y - 12), true, false); cd.LineTo(new Point(x + 5, y - 12), true, false); }
            }
        }
        up.Freeze(); dn.Freeze();
        dc.DrawGeometry(Green, null, up);
        dc.DrawGeometry(Red, null, dn);
        dc.Pop();

        dc.DrawRectangle(null, GridPen, r);
        dc.DrawText(Pal.Txt(this, key == 0 ? "Key 1 (y)" : "Key 2 (u)", 12, KeyBrush[key]), new Point(r.Right - 80, r.Top + 3));
    }

    /// <summary>Polyline with per-pixel min/max so a 2 kHz stream stays fast and still shows every spike.</summary>
    Geometry? Line(Rect r, int lo, int hi, long startT, double span, Func<ScopeSample, double> sel, Func<double, double> Y)
    {
        int cols = Math.Max(1, (int)r.Width);
        var fv = new double[cols]; var mn = new double[cols]; var mx = new double[cols]; var used = new bool[cols];
        for (int i = lo; i < hi; i++)
        {
            int col = (int)((Samples[i].T - startT) / span * cols);
            if (col < 0 || col >= cols) continue;
            double y = Y(sel(Samples[i]));
            if (!used[col]) { used[col] = true; fv[col] = mn[col] = mx[col] = y; }
            else { if (y < mn[col]) mn[col] = y; if (y > mx[col]) mx[col] = y; }
        }
        var pts = new List<Point>();
        for (int col = 0; col < cols; col++)
        {
            if (!used[col]) continue;
            double x = r.Left + col;
            pts.Add(new Point(x, fv[col]));
            if (mn[col] != fv[col]) pts.Add(new Point(x, mn[col]));
            if (mx[col] != fv[col] && mx[col] != mn[col]) pts.Add(new Point(x, mx[col]));
        }
        if (pts.Count < 2) return null;
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(pts[0], false, false); c.PolyLineTo(pts.Skip(1).ToList(), true, false); }
        g.Freeze();
        return g;
    }
}

/// <summary>Overlaid latency distributions (area-normalised) for the ticked CSV sessions.</summary>
public sealed class HistogramView : FrameworkElement
{
    public static readonly SolidColorBrush[] Palette =
        { Pal.Hex("#5EEAD4"), Pal.Hex("#FBBF24"), Pal.Hex("#C4B5FD"), Pal.Hex("#F87171"), Pal.Hex("#60A5FA"), Pal.Hex("#A3E635") };
    public List<(string Name, Brush Color, double[] Data)> Series { get; set; } = new();

    static readonly SolidColorBrush Bg = Pal.Hex("#23272B"), Dim = Pal.Hex("#9CA3AF");
    static readonly Pen GridPen = Pal.MakePen(Pal.Hex("#3A4046"), 1);

    static double NiceStep(double raw)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw))), m = raw / p;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        const double L = 12, R = 12, T = 8, B = 22;
        double pw = w - L - R, ph = h - T - B;
        if (pw < 60 || ph < 40) return;
        if (Series.Count == 0)
        {
            dc.DrawText(Pal.Txt(this, "Tick one or more sessions on the left to compare latency distributions", 12, Dim), new Point(L + 6, T + 6));
            return;
        }

        var sorted = Series.Select(s => s.Data.OrderBy(v => v).ToArray()).ToList();
        double lo = sorted.Min(d => LatStats.Pct(d, 0.5)), hi = sorted.Max(d => LatStats.Pct(d, 99.5));
        if (hi - lo < 0.2) hi = lo + 0.2;
        const int bins = 60;
        double bw = (hi - lo) / bins, ymax = 0;
        var dens = new double[Series.Count][];
        for (int s = 0; s < Series.Count; s++)
        {
            dens[s] = new double[bins];
            foreach (var v in Series[s].Data) { int b = (int)((v - lo) / bw); if (b >= 0 && b < bins) dens[s][b]++; }
            for (int b = 0; b < bins; b++) dens[s][b] /= Series[s].Data.Length * bw;
            ymax = Math.Max(ymax, dens[s].Max());
        }
        ymax *= 1.1;

        double step = NiceStep((hi - lo) / 8);
        for (double f = Math.Ceiling(lo / step) * step; f <= hi; f += step)
        {
            double x = L + (f - lo) / (hi - lo) * pw;
            dc.DrawLine(GridPen, new Point(x, T), new Point(x, T + ph));
            dc.DrawText(Pal.Txt(this, f.ToString("0.##") + " ms", 10, Dim), new Point(x - 16, T + ph + 3));
        }

        for (int s = 0; s < Series.Count; s++)
        {
            var pts = new List<Point>();
            for (int b = 0; b < bins; b++) pts.Add(new Point(L + (b + 0.5) / bins * pw, T + ph - dens[s][b] / ymax * ph));
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(pts[0].X, T + ph), true, true);
                c.PolyLineTo(pts, true, false);
                c.LineTo(new Point(pts[^1].X, T + ph), true, false);
            }
            g.Freeze();
            var col = ((SolidColorBrush)Series[s].Color).Color;
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(55, col.R, col.G, col.B)), Pal.MakePen(Series[s].Color, 1.6), g);
            dc.DrawRectangle(Series[s].Color, null, new Rect(w - 190, T + 4 + s * 15, 9, 9));
            string name = Series[s].Name.Length > 24 ? Series[s].Name[..24] : Series[s].Name;
            dc.DrawText(Pal.Txt(this, name, 10, Dim), new Point(w - 176, T + 1 + s * 15));
        }
    }
}
