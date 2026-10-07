using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace keypaa;

/// <summary>
/// Filter lab (inside the Latency Lab overlay): adjustable signal filtering on the keypad plus what it costs.
///   Less filtering  -> shorter scan loop / lower firmware delay, but more sensor noise.
/// The keypad reports its scan timing ("T," lines) and can measure its own noise at rest ("NOISE", "N," line);
/// from those we show the delay saved, the noise added, and whether your RT / deadzone settings still hold.
/// </summary>
public partial class MainWindow
{
    static readonly int[] AdcDivs = { 16, 32, 64, 128 };
    const int DefOvs = 8, DefAdc = 32, DefDb = 7, DefRe = 30;     // = the old hardcoded values
    const int NoiseMs = 1000;

    sealed record NoiseKey(double Mean, double Sd, int Min, int Max);
    sealed record NoiseResult(int N, int Ms, int Ovs, int Adc, NoiseKey[] K);

    int fOvs = DefOvs, fAdc = DefAdc, fDb = DefDb, fRe = DefRe;   // what the keypad is running right now
    double loopAvgUs, loopMaxUs, readUs;                          // scan timing reported by the keypad (readUs = both HE keys)
    bool haveFilt, haveTiming, noisePending;
    readonly Stopwatch noiseClock = new();
    NoiseResult? noise;

    bool NoiseBusy => noisePending && noiseClock.ElapsedMilliseconds < 4000;
    string FilterTag => $"{fOvs}x ADC/{fAdc} db{fDb} re{fRe}";
    double ScanMs => haveTiming ? loopAvgUs / 1000 : double.NaN;
    double NoiseSdForCurrentFilter =>
        noise != null && noise.Ovs == fOvs && noise.Adc == fAdc ? noise.K.Max(k => k.Sd) : double.NaN;

    void InitFilter()
    {
        FAdc.ItemsSource = new[] { "÷16 (fastest, beyond ADC spec)", "÷32 (default)", "÷64", "÷128 (Arduino default, slowest)" };
        FAdc.SelectedIndex = 1;
        FOvsS.ValueChanged += (_, _) => UpdateFilterInfo();
        FDbS.ValueChanged += (_, _) => UpdateFilterInfo();
        FReS.ValueChanged += (_, _) => UpdateFilterInfo();
        FAdc.SelectionChanged += (_, _) => UpdateFilterInfo();
        SetFilterButtons(false);
    }

    void SetFilterButtons(bool on)
    {
        BtnFiltApply.IsEnabled = BtnNoise.IsEnabled = BtnFiltDefault.IsEnabled = BtnFiltSave.IsEnabled = on;
        if (!on) { haveFilt = false; haveTiming = false; noisePending = false; }
        UpdateFilterInfo();
    }

    (int ovs, int adc, int db, int re) Pending() =>
        ((int)FOvsS.Value, AdcDivs[Math.Max(0, FAdc.SelectedIndex)], (int)FDbS.Value, (int)FReS.Value);

    bool FiltDirty()
    {
        var p = Pending();
        return p.ovs != fOvs || p.adc != fAdc || p.db != fDb || p.re != fRe;
    }

    void LoadFilterControls()
    {
        FOvsS.Value = fOvs; FDbS.Value = fDb; FReS.Value = fRe;
        int i = Array.IndexOf(AdcDivs, fAdc);
        FAdc.SelectedIndex = i >= 0 ? i : 1;
    }

    /// <summary>Firmware delay model: wait for the next scan (avg half a loop) + centre of the averaging window (half of one key's window).</summary>
    static (double avg, double worst) Delay(double loopUs, double readBothKeysUs) =>
        (loopUs / 2 + readBothKeysUs / 4, loopUs + readBothKeysUs / 4);

    // ---------- lines from the keypad ----------

    bool HandleFilterLine(string[] p)
    {
        switch (p[0])
        {
            case "F" when p.Length == 5:   // F,oversampling,adcDivider,releaseDebounceMs,encoderConfirmMs
                if (int.TryParse(p[1], out int o) && int.TryParse(p[2], out int a) &&
                    int.TryParse(p[3], out int d) && int.TryParse(p[4], out int r))
                {
                    bool dirty = FiltDirty();            // unapplied edits survive a refresh
                    fOvs = o; fAdc = a; fDb = d; fRe = r;
                    haveFilt = true;
                    if (!dirty) LoadFilterControls();
                    UpdateFilterInfo();
                }
                return true;

            case "T" when p.Length == 4:   // T,avgLoopUs,maxLoopUs,avgReadUs
                if (double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double la) &&
                    double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lm) &&
                    double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double rd))
                {
                    loopAvgUs = la; loopMaxUs = lm; readUs = rd;
                    haveTiming = la > 0;
                    if (LatOverlay.Visibility == Visibility.Visible) UpdateFilterInfo();
                }
                return true;

            case "N" when p.Length == 12:  // N,n,ref0,sumD0,sumD2_0,min0,max0,ref1,sumD1,sumD2_1,min1,max1
            {
                noisePending = false;
                var v = new long[11];
                for (int i = 0; i < 11; i++)
                    if (!long.TryParse(p[1 + i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return true;
                long n = v[0];
                if (n < 2) { Log("Noise test returned no samples."); return true; }
                var keys = new NoiseKey[2];
                for (int k = 0; k < 2; k++)
                {
                    int b = 1 + k * 5;                   // ref, sum of deviations, sum of squared deviations, min, max
                    double md = (double)v[b + 1] / n;
                    double variance = (double)v[b + 2] / n - md * md;
                    keys[k] = new NoiseKey(v[b] + md, Math.Sqrt(Math.Max(0, variance)), (int)v[b + 3], (int)v[b + 4]);
                }
                noise = new NoiseResult((int)n, NoiseMs, fOvs, fAdc, keys);
                Log($"Noise test done: key 1 sd {keys[0].Sd:0.00}, key 2 sd {keys[1].Sd:0.00} counts ({fOvs}x, ADC /{fAdc}).");
                UpdateFilterInfo();
                return true;
            }
        }
        return false;
    }

    // ---------- readout ----------

    (string text, bool bad) Verdict(int k, NoiseKey nk)
    {
        var pv = profVals[activeProfile];
        int topDz = pv[k * 4], rtP = pv[k * 4 + 2], rtR = pv[k * 4 + 3];
        int minRt = Math.Min(rtP, rtR);
        double nz = Math.Max(nk.Max - nk.Min, 6 * nk.Sd);        // realistic worst-case excursion of the reading
        string chatter = nz < 0.5 * minRt ? "ok" : nz < minRt ? "marginal" : "RISK";
        double rest = nk.Max - idle[k];                           // highest reading above the calibrated idle
        double need = topDz + rtP;                                // excursion needed to fake a press at rest
        string falsePress = rest < 0.6 * need ? "ok" : rest < need ? "marginal" : "RISK";
        string text = $"noise ~{nz:0.0} counts | RT {rtP}/{rtR}: chatter {chatter} (RT >= {Math.Ceiling(nz * 1.5):0} advised) | " +
                      $"false press at rest {falsePress} (peak +{rest:0} of {need:0})";
        return (text, chatter != "ok" || falsePress != "ok");
    }

    void UpdateFilterInfo()
    {
        var (ovs, adc, db, re) = Pending();
        FOvsV.Text = ovs + "×";
        FDbV.Text = db + " ms";
        FReV.Text = re + " ms";

        var sb = new StringBuilder();
        bool warn = false;

        if (!connected) sb.AppendLine("Not connected.");
        else if (!haveFilt) sb.AppendLine("Waiting for the keypad's filter settings (needs the updated .ino)...");
        else sb.AppendLine($"On keypad: {fOvs}x oversampling, ADC /{fAdc}, ESC/G/T release debounce {fDb} ms, encoder confirm {fRe} ms" +
                           (FiltDirty() ? "   [edits not applied yet]" : ""));

        if (haveTiming && readUs > 0)
        {
            var (a, w) = Delay(loopAvgUs, readUs);
            sb.AppendLine($"Scan loop: avg {loopAvgUs / 1000:0.00} ms ({1e6 / loopAvgUs:0} Hz), worst {loopMaxUs / 1000:0.0} ms; HE reads are {readUs / 1000:0.00} ms of it");
            sb.AppendLine($"Firmware delay (est.): avg {a:0.00} ms, worst {w:0.00} ms  (+ 0-1 ms USB poll)");
            if (haveFilt && (ovs != fOvs || adc != fAdc))
            {
                double conv = readUs / (2.0 * fOvs) * adc / fAdc;      // us per ADC sample at the new clock
                double rp = 2 * ovs * conv, lp = loopAvgUs - readUs + rp;
                var (pa, pw) = Delay(lp, rp);
                sb.AppendLine($"If applied ({ovs}x, /{adc}): loop {lp / 1000:0.00} ms, delay avg {pa:0.00} ms ({pa - a:+0.00;-0.00;0.00}), worst {pw:0.00} ms ({pw - w:+0.00;-0.00;0.00})");
                if (noise != null)
                {
                    double f = Math.Sqrt((double)noise.Ovs / ovs);
                    sb.AppendLine($"  predicted noise vs last test: x{f:0.00} (sd ~{noise.K.Max(k => k.Sd) * f:0.00} counts)" +
                                  (adc != noise.Adc ? "; the ADC clock's own effect is not modelled - apply and measure" : ""));
                }
                else sb.AppendLine("  run a noise test to see what lower filtering would cost");
            }
        }
        else if (connected && haveFilt) sb.AppendLine("Scan timing: waiting for the keypad...");

        if (noise != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Noise test ({noise.Ovs}x, ADC /{noise.Adc}, {noise.N} readings, keys at rest):");
            for (int k = 0; k < 2; k++)
            {
                var nk = noise.K[k];
                var (text, bad) = Verdict(k, nk);
                warn |= bad;
                sb.AppendLine($"  Key {k + 1}: mean {nk.Mean:0.0}  sd {nk.Sd:0.00}  peak-to-peak {nk.Max - nk.Min}   {text}");
            }
            if (haveFilt && (noise.Ovs != fOvs || noise.Adc != fAdc)) sb.AppendLine("  (measured with other settings than the keypad runs now)");
        }
        else if (connected) sb.AppendLine("\nNo noise test yet: keep both keys released and press 'Noise test'.");

        FiltInfo.Text = sb.ToString();
        FiltInfo.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Warn" : "TextBright");
    }

    // ---------- buttons ----------

    bool ApplyFilter()
    {
        if (!connected) return false;
        if (rec.Active) { Log("Stop the latency recording before changing the filter."); return false; }
        if (NoiseBusy) { Log("Wait for the noise test to finish."); return false; }
        var (ovs, adc, db, re) = Pending();
        Send($"FILT {ovs} {adc} {db} {re}");
        Log($"Filter applied live: {ovs}x, ADC /{adc}, release debounce {db} ms, encoder confirm {re} ms (Save to EEPROM keeps it).");
        return true;
    }

    void BtnFiltApply_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    void BtnFiltDefault_Click(object sender, RoutedEventArgs e)
    {
        FOvsS.Value = DefOvs; FDbS.Value = DefDb; FReS.Value = DefRe;
        FAdc.SelectedIndex = Array.IndexOf(AdcDivs, DefAdc);
        ApplyFilter();
    }

    void BtnFiltSave_Click(object sender, RoutedEventArgs e)
    {
        if (FiltDirty() && !ApplyFilter()) return;
        BtnSave_Click(sender, e);   // saves profiles, calibration and the filter settings together
    }

    void BtnNoise_Click(object sender, RoutedEventArgs e)
    {
        if (!connected || NoiseBusy) return;
        if (!BeginCalMute("the noise test")) return;   // keys are muted for the test, then restored
        if (MessageBox.Show("Release both HE keys and don't touch the keypad or the desk.\nThe test takes about 1 second.",
                "Noise test", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
        {
            noisePending = true;
            noiseClock.Restart();
            Send($"NOISE {NoiseMs}");
            Log("Noise test running...");
        }
        EndCalMuteWhenReleased();
    }
}
