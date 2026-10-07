using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace keypaa;

/// <summary>Frequency spectrum of event timing. Wheel = zoom, drag = pan, double-click = reset.</summary>
public sealed class SpectrumView : FrameworkElement
{
    const double DataMax = PollAnalyzer.Fs / 2.0;   // 16384 Hz
    const double DefaultMax = 8500;
    const double L = 10, R = 10, T = 22, B = 22;

    float[]? data;
    double estHz;
    string estLabel = "";
    double viewMin = 0, viewMax = DefaultMax;
    Point? mouse;
    bool dragging;
    double dragX, dragMin;

    static SolidColorBrush Bg => Theme.ChartBg;
    static SolidColorBrush Dim => Theme.TextDim;
    static SolidColorBrush Teal => Theme.Good;
    static SolidColorBrush White => Theme.TextBright;
    static SolidColorBrush Fill => Theme.SpectrumFill;
    static Pen GridPen => Pal.MakePen(Theme.ChartGrid, 1);
    static Pen LinePen => Pal.MakePen(Theme.SpectrumLine, 1);
    static Pen EstPen => Pal.MakePen(Teal, 1.5, dashed: true);
    static Pen CursorPen => Pal.MakePen(Theme.Cursor, 1);

    public void SetData(float[]? d, double estimateHz, string label)
    {
        data = d; estHz = estimateHz; estLabel = label;
        InvalidateVisual();
    }

    void SetView(double min, double span)
    {
        span = Math.Clamp(span, 20, DataMax);
        min = Math.Clamp(min, 0, DataMax - span);
        viewMin = min; viewMax = min + span;
        InvalidateVisual();
    }

    static double NiceStep(double raw)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double m = raw / p;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double pw = ActualWidth - L - R;
        if (pw < 50) return;
        double span = viewMax - viewMin;
        double ratio = Math.Clamp((e.GetPosition(this).X - L) / pw, 0, 1);
        double f = viewMin + ratio * span;
        double ns = Math.Clamp(span * (e.Delta > 0 ? 0.8 : 1.25), 20, DataMax);
        SetView(f - ratio * ns, ns);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { SetView(0, DefaultMax); return; }
        dragging = true;
        dragX = e.GetPosition(this).X;
        dragMin = viewMin;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        mouse = p;
        if (dragging)
        {
            double span = viewMax - viewMin;
            SetView(dragMin - (p.X - dragX) / (ActualWidth - L - R) * span, span);
        }
        else InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { dragging = false; ReleaseMouseCapture(); }
    protected override void OnMouseLeave(MouseEventArgs e) { mouse = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        double pw = w - L - R, ph = h - T - B;
        if (pw < 50 || ph < 40) return;
        double span = viewMax - viewMin, baseY = T + ph;

        dc.DrawText(Pal.Txt(this, "Polling-rate spectrum (Hz)  -  wheel: zoom, drag: pan, double-click: reset", 11, Dim),
            new Point(L, 3));

        // x grid
        double step = NiceStep(span / 8);
        for (double f = Math.Ceiling(viewMin / step) * step; f <= viewMax; f += step)
        {
            double x = L + (f - viewMin) / span * pw;
            dc.DrawLine(GridPen, new Point(x, T), new Point(x, baseY));
            dc.DrawText(Pal.Txt(this, f.ToString("0.##"), 10, Dim), new Point(x - 12, baseY + 3));
        }

        // spectrum (auto-scaled to what is visible)
        var vals = new double[(int)pw + 1];
        double vmax = 0;
        if (data != null)
        {
            double binsPerPx = span / pw / PollAnalyzer.HzPerBin;
            for (int px = 0; px < vals.Length; px++)
            {
                double fa = viewMin + px / pw * span, fb = viewMin + (px + 1) / pw * span, v;
                if (binsPerPx <= 1)
                {
                    double pos = fa / PollAnalyzer.HzPerBin;
                    int i0 = Math.Clamp((int)pos, 0, data.Length - 2);
                    double fr = Math.Clamp(pos - i0, 0, 1);
                    v = data[i0] * (1 - fr) + data[i0 + 1] * fr;
                }
                else
                {
                    int ia = Math.Clamp((int)(fa / PollAnalyzer.HzPerBin), 0, data.Length - 1);
                    int ib = Math.Clamp((int)(fb / PollAnalyzer.HzPerBin), ia, data.Length - 1);
                    v = 0;
                    for (int i = ia; i <= ib; i++) if (data[i] > v) v = data[i];
                }
                vals[px] = v;
                if (v > vmax) vmax = v;
            }
            if (vmax > 0)
            {
                double ymax = vmax * 1.08;
                var pts = new List<Point>(vals.Length);
                for (int px = 0; px < vals.Length && px <= pw; px++)
                    pts.Add(new Point(L + px, baseY - vals[px] / ymax * ph));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(L, baseY), true, true);
                    c.PolyLineTo(pts, true, false);
                    c.LineTo(new Point(L + pts.Count - 1, baseY), true, false);
                }
                g.Freeze();
                dc.DrawGeometry(Fill, LinePen, g);
            }
        }

        // detected polling rate
        if (estHz > 0 && estHz >= viewMin && estHz <= viewMax)
        {
            double x = L + (estHz - viewMin) / span * pw;
            dc.DrawLine(EstPen, new Point(x, T), new Point(x, baseY));
            dc.DrawText(Pal.Txt(this, "~ " + estLabel, 12, Teal), new Point(Math.Min(x + 4, w - 90), T + 2));
        }

        // hover readout
        if (mouse is Point m && m.X >= L && m.X <= L + pw)
        {
            double f = viewMin + (m.X - L) / pw * span;
            dc.DrawLine(CursorPen, new Point(m.X, T), new Point(m.X, baseY));
            dc.DrawText(Pal.Txt(this, $"{f:0.0} Hz", 11, White), new Point(Math.Min(m.X + 6, w - 70), baseY - 18));
        }
    }
}
