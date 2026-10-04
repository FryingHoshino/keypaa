using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace keypaa;

/// <summary>Maps device micros() to PC Stopwatch ticks using ping/reply samples (NTP style, lowest-RTT samples, local linear fit).</summary>
public sealed class ClockSync
{
    struct S { public long X, Y, Rtt; }
    readonly List<S> s = new();
    public int Count => s.Count;
    public double BestRttMs { get; private set; } = double.NaN;

    public void Add(long devUs, long t0, long t1)
    {
        s.Add(new S { X = devUs, Y = (t0 + t1) / 2, Rtt = t1 - t0 });
        double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        if (double.IsNaN(BestRttMs) || ms < BestRttMs) BestRttMs = ms;
        if (s.Count > 20000) s.RemoveRange(0, 5000);
    }

    int Lower(long x)
    {
        int lo = 0, hi = s.Count;
        while (lo < hi) { int m = (lo + hi) / 2; if (s[m].X < x) lo = m + 1; else hi = m; }
        return lo;
    }

    public long? Map(long devUs)
    {
        int a = Lower(devUs - 4_000_000), n = Lower(devUs + 4_000_000) - a;
        if (n < 8) return null;
        var sel = Enumerable.Range(a, n).OrderBy(i => s[i].Rtt).Take(Math.Max(6, n / 4)).Select(i => s[i]).ToArray();
        if (sel.Max(v => v.X) - sel.Min(v => v.X) < 2_000_000) return null;   // need a spread to fit drift
        long y0 = sel[0].Y;
        double mx = sel.Average(v => (double)(v.X - devUs)), my = sel.Average(v => (double)(v.Y - y0));
        double sxx = sel.Sum(v => Math.Pow(v.X - devUs - mx, 2));
        double sxy = sel.Sum(v => (v.X - devUs - mx) * (v.Y - y0 - my));
        double slope = sxx > 0 ? sxy / sxx : Stopwatch.Frequency / 1e6;
        return y0 + (long)(my - slope * mx);
    }
}

public record struct LatPair(byte Lane, byte Edge, long DevUs, long HostTicks, double Ms);   // Edge: 0 down, 1 up

/// <summary>Pairs each device key edge with the matching HID event seen by the PC hook.</summary>
public sealed class LatencyRecorder
{
    public ClockSync Clock { get; private set; } = new();
    public readonly List<LatPair> Pairs = new();
    public bool Active;
    readonly Queue<long>[] dev = new Queue<long>[16], hook = new Queue<long>[16];

    public LatencyRecorder() { for (int i = 0; i < 16; i++) { dev[i] = new(); hook[i] = new(); } }
    public double LastMs => Pairs.Count > 0 ? Pairs[^1].Ms : double.NaN;

    public void Start()
    {
        Pairs.Clear(); Clock = new ClockSync();
        foreach (var q in dev) q.Clear();
        foreach (var q in hook) q.Clear();
        Active = true;
    }

    public void AddDevice(int lane, int edge, long devUs) { Enq(dev[lane * 2 + edge], devUs); Pair(lane * 2 + edge); }
    public void AddHook(int lane, int edge, long ticks) { Enq(hook[lane * 2 + edge], ticks); Pair(lane * 2 + edge); }
    static void Enq(Queue<long> q, long v) { q.Enqueue(v); if (q.Count > 500) q.Dequeue(); }

    void Pair(int i)
    {
        var d = dev[i]; var h = hook[i];
        while (d.Count > 0 && h.Count > 0)
        {
            long? m = Clock.Map(d.Peek());
            if (m == null) return;                                    // clock not synced yet
            double ms = (h.Peek() - m.Value) * 1000.0 / Stopwatch.Frequency;
            if (ms > 60) { d.Dequeue(); continue; }                   // device event the PC never saw
            if (ms < -3) { h.Dequeue(); continue; }                   // PC event with no device event
            Pairs.Add(new LatPair((byte)(i / 2), (byte)(i % 2), d.Dequeue(), h.Dequeue(), ms));
        }
    }

    /// <summary>Recompute every latency with the full sync history (more accurate than the live numbers).</summary>
    public List<LatPair> Finish()
    {
        Active = false;
        var res = new List<LatPair>();
        foreach (var p in Pairs)
        {
            var m = Clock.Map(p.DevUs);
            if (m == null) continue;
            double ms = (p.HostTicks - m.Value) * 1000.0 / Stopwatch.Frequency;
            if (ms > -3 && ms < 60) res.Add(p with { Ms = ms });
        }
        return res;
    }
}

public static class LatCsv
{
    public static string Folder
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "keypaa", "latency");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string Save(List<LatPair> pairs, string profile, ClockSync clock)
    {
        string safe = new string(profile.Where(char.IsLetterOrDigit).ToArray());
        string path = Path.Combine(Folder, $"latency_{DateTime.Now:yyyyMMdd_HHmmss}_{safe}.csv");
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# keypaa latency session");
        sb.AppendLine($"# date={DateTime.Now:s}");
        sb.AppendLine($"# profile={profile}");
        sb.AppendLine($"# sync_samples={clock.Count}");
        sb.AppendLine($"# best_rtt_ms={clock.BestRttMs.ToString("0.000", inv)}");
        sb.AppendLine("time_s,key,edge,latency_ms");
        long t0 = pairs[0].HostTicks;
        foreach (var p in pairs)
            sb.AppendLine(string.Create(inv, $"{(p.HostTicks - t0) / (double)Stopwatch.Frequency:0.000},{EventStore.LaneNames[p.Lane]},{(p.Edge == 0 ? "down" : "up")},{p.Ms:0.000}"));
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>Reads any CSV whose last column is latency in ms; '#' lines are metadata.</summary>
    public static (string profile, double[] ms) Load(string path)
    {
        string profile = "";
        var list = new List<double>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#')) { if (line.StartsWith("# profile=")) profile = line[10..]; continue; }
            var f = line.Split(',');
            if (f.Length >= 2 && double.TryParse(f[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) list.Add(v);
        }
        return (profile, list.ToArray());
    }
}

public sealed class LatRow
{
    public string File { get; set; } = "";
    public string Profile { get; set; } = "";
    public int N { get; set; }
    public string Mean { get; set; } = "";
    public string Median { get; set; } = "";
    public string P95 { get; set; } = "";
    public string P99 { get; set; } = "";
    public string Min { get; set; } = "";
    public string Max { get; set; } = "";
    public string Jitter { get; set; } = "";     // standard deviation
    public string VsFirst { get; set; } = "";    // mean difference to the first ticked file
}

public static class LatStats
{
    public static double Pct(double[] sorted, double p)
    {
        double idx = p / 100 * (sorted.Length - 1);
        int i = (int)idx;
        double f = idx - i;
        return i + 1 < sorted.Length ? sorted[i] * (1 - f) + sorted[i + 1] * f : sorted[i];
    }

    public static LatRow Row(string name, string profile, double[] ms, double? baseMean)
    {
        var s = ms.OrderBy(v => v).ToArray();
        double mean = s.Average(), sd = Math.Sqrt(s.Sum(v => (v - mean) * (v - mean)) / s.Length);
        string F(double v) => v.ToString("0.00") + " ms";
        return new LatRow
        {
            File = name, Profile = profile, N = s.Length, Mean = F(mean), Median = F(Pct(s, 50)),
            P95 = F(Pct(s, 95)), P99 = F(Pct(s, 99)), Min = F(s[0]), Max = F(s[^1]), Jitter = F(sd),
            VsFirst = baseMean == null ? "baseline" : $"{mean - baseMean.Value:+0.00;-0.00;0.00} ms"
        };
    }
}
