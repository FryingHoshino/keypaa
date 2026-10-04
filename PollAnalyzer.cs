namespace keypaa;

/// <summary>
/// Estimates the USB polling rate from event timestamps.
/// Reports reach the PC on a fixed poll grid, so events are (almost) multiples of 1/rate apart.
/// We histogram the time differences between nearby events (autocorrelation), high-pass and taper it,
/// then FFT it: a comb with period 1/rate becomes peaks at rate, 2*rate, 3*rate...
/// Only short differences (125 ms) are used, so clock drift between PC and USB can't smear the result.
/// The estimate is the lowest strong peak (the fundamental).
/// </summary>
public sealed class PollAnalyzer
{
    public const int Fs = 32768;                      // histogram resolution: 30.5 us bins
    public const int Lags = 4096;                     // look at differences up to 125 ms
    public const int FftN = 65536;                    // 0.5 Hz per spectrum bin
    public const double HzPerBin = (double)Fs / FftN;

    public float[] Spectrum { get; private set; } = new float[FftN / 2 + 1];   // amplitude, 0..16384 Hz
    public double EstimateHz { get; private set; }
    public double Snr { get; private set; }           // >= ~60 is a confident detection
    public int Events { get; private set; }

    readonly double[] re = new double[FftN];
    readonly double[] im = new double[FftN];
    readonly double[] hist = new double[Lags + 1];
    readonly double[] cs = new double[Lags + 2];

    public void Analyze(long[] ticks, double ticksPerSec)
    {
        int n = ticks.Length;
        Events = n;
        Array.Clear(hist);
        double maxLag = (double)Lags / Fs;

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dt = (ticks[j] - ticks[i]) / ticksPerSec;
                if (dt >= maxLag) break;
                int lag = (int)Math.Round(dt * Fs);
                if (lag >= 1 && lag <= Lags) hist[lag]++;
            }

        // running sums for the moving average (removes slow key-timing structure)
        cs[0] = 0; cs[1] = 0;
        for (int k = 1; k <= Lags; k++) cs[k + 1] = cs[k] + hist[k];

        Array.Clear(re);
        Array.Clear(im);
        const int Half = 524;   // ~16 ms each side
        for (int k = 1; k <= Lags; k++)
        {
            int lo = Math.Max(1, k - Half), hi = Math.Min(Lags, k + Half);
            double ma = (cs[hi + 1] - cs[lo]) / (hi - lo + 1);
            double v = (hist[k] - ma) * 0.5 * (1 + Math.Cos(Math.PI * k / (Lags + 1)));   // Hann taper
            re[k] = v;
            re[FftN - k] = v;      // autocorrelation is symmetric -> real spectrum
        }
        Fft(re, im);

        int bins = FftN / 2 + 1;
        var power = new double[bins];
        var amp = new float[bins];
        for (int b = 0; b < bins; b++)
        {
            double p = Math.Max(0, re[b]);
            power[b] = p;
            amp[b] = (float)Math.Sqrt(p);
        }
        Spectrum = amp;
        Estimate(power);
    }

    void Estimate(double[] p)
    {
        EstimateHz = 0;
        Snr = 0;
        int lo = (int)(100 / HzPerBin), hi = (int)(16000 / HzPerBin);

        var sm = new double[p.Length];
        for (int i = 2; i < p.Length - 2; i++)
            sm[i] = (p[i - 2] + p[i - 1] + p[i] + p[i + 1] + p[i + 2]) / 5;

        double smax = 0;
        for (int i = lo; i < hi; i++) if (sm[i] > smax) smax = sm[i];
        if (smax <= 0) return;

        // noise floor = mean of the lowest 80% of the spectrum
        var seg = new double[hi - lo];
        Array.Copy(p, lo, seg, 0, seg.Length);
        Array.Sort(seg);
        int keep = (int)(seg.Length * 0.8);
        double noise = 0;
        for (int i = 0; i < keep; i++) noise += seg[i];
        noise = noise / keep + 1e-9;

        for (int i = lo + 2; i < hi - 2; i++)
        {
            if (sm[i] >= sm[i - 1] && sm[i] >= sm[i + 1] && sm[i] >= 0.4 * smax)
            {
                double y0 = sm[i - 1], y1 = sm[i], y2 = sm[i + 1], den = y0 - 2 * y1 + y2;
                double d = den != 0 ? 0.5 * (y0 - y2) / den : 0;
                EstimateHz = (i + d) * HzPerBin;
                Snr = y1 / noise;
                return;
            }
        }
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci;
                    double xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = t;
                }
            }
        }
    }
}
