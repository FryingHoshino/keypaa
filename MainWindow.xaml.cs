using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace keypaa;

public partial class MainWindow : Window
{
    const int NumKeys = 2;
    const int ProfileCount = 8;
    const double LeftWidth = 760;
    static readonly string[] KeyNames = { "Key 1 (y, A0)", "Key 2 (u, A1)" };
    static readonly double[] StdRates = { 125, 250, 500, 1000, 2000, 4000, 8000 };

    readonly SerialPort port = new() { BaudRate = 115200, NewLine = "\n", DtrEnable = true, ReadTimeout = 200 };
    Thread? reader;
    volatile bool readerRun;

    readonly ProgressBar[] bars;
    readonly TextBlock[] rawTxt, stateTxt, calTxt;
    readonly Button[] fullButtons;
    readonly Slider[][] sld;                 // [key][top, bottom, rtPress, rtRelease]
    readonly Control[] connectedOnly;
    readonly int[] idle = new int[2], full = new int[2];

    // fed by the serial reader thread, drained on the UI thread each frame
    readonly ConcurrentQueue<ScopeSample> scopeQ = new();
    readonly ConcurrentQueue<(int lane, int state, long dev)> devEvQ = new();
    readonly ConcurrentQueue<(int id, long dev, long ts)> syncQ = new();
    long lastDev;
    bool devInit;

    // profiles (mirror of what is on the keypad)
    readonly string[] profNames = new string[ProfileCount];
    readonly int[][] profVals = new int[ProfileCount][];   // [top0,bot0,rtp0,rtr0, top1,bot1,rtp1,rtr1]
    int activeProfile;

    // logger
    readonly EventStore store = new();
    readonly InputHook hook = new();
    readonly PollAnalyzer analyzer = new();
    readonly DispatcherTimer analysisTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    bool loggerOpen, analysing, scopeTab, scopeStreamOn, suppress;
    int analysedVersion = -1;
    double closedW, closedH, closedMinW;

    public MainWindow()
    {
        InitializeComponent();
        bars = new[] { Bar1, Bar2 };
        rawTxt = new[] { Raw1, Raw2 };
        stateTxt = new[] { State1, State2 };
        calTxt = new[] { Cal1, Cal2 };
        fullButtons = new[] { BtnFull1, BtnFull2 };
        sld = new[] { new[] { S1Top, S1Bot, S1RtP, S1RtR }, new[] { S2Top, S2Bot, S2RtP, S2RtR } };
        connectedOnly = new Control[] { BtnApply, BtnIdle, BtnRead, BtnSave, BtnDefaults, BtnFull1, BtnFull2,
                                        ProfileBox, ProfileNameBox, BtnRename, CopyBox, BtnCopy, BtnResetSlot };

        for (int i = 0; i < ProfileCount; i++)
        {
            profNames[i] = "Profile" + (i + 1);
            profVals[i] = new[] { 14, 10, 10, 10, 14, 10, 10, 10 };
            ProfileBox.Items.Add("");
            CopyBox.Items.Add("");
        }
        RefreshProfileList();
        LoadSliders();

        SetConnectedUi(false);
        RefreshPorts();
        InitLatency();

        TimelineCtl.Store = store;
        hook.Start();
        CompositionTarget.Rendering += OnFrame;
        analysisTimer.Tick += AnalysisTick;
        analysisTimer.Start();
    }

    // ---------- connection ----------

    void RefreshPorts()
    {
        var current = PortBox.SelectedItem as string;
        PortBox.Items.Clear();
        foreach (var p in SerialPort.GetPortNames().OrderBy(n => n)) PortBox.Items.Add(p);
        if (current != null && PortBox.Items.Contains(current)) PortBox.SelectedItem = current;
        else if (PortBox.Items.Count > 0) PortBox.SelectedIndex = 0;
    }

    void Connect()
    {
        if (PortBox.SelectedItem is not string name) { Log("No port selected."); return; }
        try
        {
            port.PortName = name;
            port.Open();
            devInit = false;
            readerRun = true;
            reader = new Thread(ReadLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "SerialRead" };
            reader.Start();
            SetConnectedUi(true);
            Log($"Connected to {name}");
            Send(MuteCheck.IsChecked == true ? "KEYS 0" : "KEYS 1");
            Send("GET");
            Send("STREAM 1");
            UpdateScopeStream();
        }
        catch (Exception ex) { Log("Connect failed: " + ex.Message); }
    }

    void Disconnect()
    {
        if (!port.IsOpen) return;
        if (rec.Active) StopRecording();
        try
        {
            port.WriteLine("STREAM 0");
            port.WriteLine("SCOPE 0");
            port.WriteLine("EVT 0");
            port.WriteLine("KEYS 1");   // give the keys back
            Thread.Sleep(50);
        }
        catch { /* ignore */ }
        readerRun = false;
        try { port.Close(); } catch { /* ignore */ }
        reader?.Join(300);
        scopeStreamOn = false;
        SetConnectedUi(false);
        Log("Disconnected");
    }

    void SetConnectedUi(bool connected)
    {
        BtnConnect.Content = connected ? "Disconnect" : "Connect";
        StatusText.Text = connected ? "Connected" : "Disconnected";
        PortBox.IsEnabled = !connected;
        BtnRefresh.IsEnabled = !connected;
        foreach (var c in connectedOnly) c.IsEnabled = connected;
    }

    void Send(string cmd)
    {
        if (!port.IsOpen) return;
        try { port.WriteLine(cmd); }
        catch (Exception ex) { Log("Send failed: " + ex.Message); }
    }

    // ---------- serial reader (own thread: timestamps are taken the moment bytes arrive) ----------

    void ReadLoop()
    {
        var buf = new byte[1024];
        var sb = new StringBuilder();
        while (readerRun)
        {
            int n;
            try { n = port.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch { break; }
            long ts = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                char c = (char)buf[i];
                if (c != '\n') { if (c != '\r') sb.Append(c); continue; }
                string line = sb.ToString();
                sb.Clear();
                if (line.Length > 0) Dispatch(line, ts);
            }
        }
    }

    long Unwrap(uint t)
    {
        if (!devInit) { devInit = true; lastDev = t; return t; }
        lastDev += unchecked((int)(t - (uint)lastDev));
        return lastDev;
    }

    static uint Hx(string s, int i, int n)
    {
        uint v = 0;
        for (int k = 0; k < n; k++) { char c = s[i + k]; v = (v << 4) | (uint)(c <= '9' ? c - '0' : (c & 0xDF) - 'A' + 10); }
        return v;
    }

    void Dispatch(string line, long ts)
    {
        switch (line[0])
        {
            case 'W' when line.Length == 22:
                scopeQ.Enqueue(new ScopeSample
                {
                    T = Unwrap(Hx(line, 1, 8)), V0 = (ushort)Hx(line, 9, 3), V1 = (ushort)Hx(line, 12, 3),
                    R0 = (ushort)Hx(line, 15, 3), R1 = (ushort)Hx(line, 18, 3), F = (byte)Hx(line, 21, 1)
                });
                break;
            case 'E' when line.Length == 11:
                devEvQ.Enqueue(((int)Hx(line, 1, 1), (int)Hx(line, 2, 1), Unwrap(Hx(line, 3, 8))));
                break;
            case 'S' when line.Length == 13:
                syncQ.Enqueue(((int)Hx(line, 1, 4), Unwrap(Hx(line, 5, 8)), ts));
                break;
            default:
                Dispatcher.BeginInvoke(new Action(() => HandleLine(line)));
                break;
        }
    }

    void OnFrame(object? sender, EventArgs e)
    {
        while (hook.Queue.TryDequeue(out var ev))
        {
            store.Add(ev);
            if (rec.Active && ev.Kind < 2) rec.AddHook(ev.Lane, ev.Kind, ev.Ticks);
        }
        while (scopeQ.TryDequeue(out var sm)) ScopeCtl.Add(sm);
        while (devEvQ.TryDequeue(out var d))
            if (rec.Active && d.lane < 7) rec.AddDevice(d.lane, d.state == 1 ? 0 : 1, d.dev);
        while (syncQ.TryDequeue(out var y)) OnSyncReply(y.id, y.dev, y.ts);

        if (loggerOpen)
        {
            if (scopeTab) { if (!ScopeCtl.IsFrozen) ScopeCtl.InvalidateVisual(); }
            else if (!TimelineCtl.IsFrozen) TimelineCtl.InvalidateVisual();
        }
    }

    void HandleLine(string line)
    {
        var p = line.Split(',');

        if (p[0] == "V" && p.Length == 1 + NumKeys * 2)          // V,val0,val1,pressed0,pressed1
        {
            for (int i = 0; i < NumKeys; i++)
            {
                if (int.TryParse(p[1 + i], out int v)) { bars[i].Value = Math.Clamp(v, 0, 1023); rawTxt[i].Text = v.ToString(); }
                bool down = p[1 + NumKeys + i] == "1";
                stateTxt[i].Text = down ? "PRESSED" : "-";
                stateTxt[i].Foreground = down ? Brushes.Green : SystemColors.ControlTextBrush;
            }
        }
        else if (p[0] == "C" && p.Length == 5)                    // C,idle0,full0,idle1,full1
        {
            for (int i = 0; i < NumKeys; i++)
            {
                int.TryParse(p[1 + i * 2], out idle[i]);
                int.TryParse(p[2 + i * 2], out full[i]);
                calTxt[i].Text = $"{idle[i]} / {full[i]}";
            }
            PushScopeConfig();
        }
        else if (p[0] == "P" && p.Length == 11)                   // P,n,name,top0,bot0,rtp0,rtr0,top1,bot1,rtp1,rtr1
        {
            if (!int.TryParse(p[1], out int n) || n < 0 || n >= ProfileCount) return;
            profNames[n] = p[2];
            for (int i = 0; i < 8; i++) int.TryParse(p[3 + i], out profVals[n][i]);
            RefreshProfileList();
            if (n == activeProfile) LoadSliders();
        }
        else if (p[0] == "A" && p.Length == 2)                    // A,activeProfile
        {
            if (int.TryParse(p[1], out int n) && n >= 0 && n < ProfileCount) activeProfile = n;
            RefreshProfileList();
            LoadSliders();
            Log($"Active profile: {activeProfile + 1} ({profNames[activeProfile]})");
        }
        else Log(line);
    }

    // ---------- calibration ----------

    void BtnIdle_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Release BOTH HE keys completely, then click OK.", "Calibrate IDLE",
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        Send("CALR");
    }

    void BtnFull1_Click(object sender, RoutedEventArgs e) => CalibrateFull(0);
    void BtnFull2_Click(object sender, RoutedEventArgs e) => CalibrateFull(1);

    void CalibrateFull(int key)
    {
        if (MessageBox.Show($"Press {KeyNames[key]} fully down and HOLD it, then click OK\n" +
                "(the reading is taken right after you click).", "Calibrate FULL",
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        Send($"CALP {key}");
    }

    // ---------- profiles ----------

    void RefreshProfileList()
    {
        suppress = true;
        for (int i = 0; i < ProfileCount; i++)
        {
            string t = $"{i + 1}: {profNames[i]}";
            ProfileBox.Items[i] = t;
            CopyBox.Items[i] = t;
        }
        ProfileBox.SelectedIndex = activeProfile;
        suppress = false;
    }

    void LoadSliders()
    {
        for (int k = 0; k < NumKeys; k++)
            for (int j = 0; j < 4; j++) sld[k][j].Value = profVals[activeProfile][k * 4 + j];
        ProfileNameBox.Text = profNames[activeProfile];
        PushScopeConfig();
    }

    void PushScopeConfig()
    {
        for (int k = 0; k < NumKeys; k++)
            ScopeCtl.Cfg[k] = new ScopeCfg
            {
                Idle = idle[k], Full = full[k], Top = (int)sld[k][0].Value, Bot = (int)sld[k][1].Value,
                RtP = (int)sld[k][2].Value, RtR = (int)sld[k][3].Value
            };
    }

    void ApplySliders()
    {
        for (int k = 0; k < NumKeys; k++)
        {
            for (int j = 0; j < 4; j++) profVals[activeProfile][k * 4 + j] = (int)sld[k][j].Value;
            Send($"DZ {k} {(int)sld[k][0].Value} {(int)sld[k][1].Value}");
            Send($"RT {k} {(int)sld[k][2].Value} {(int)sld[k][3].Value}");
        }
        PushScopeConfig();
    }

    void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppress || ProfileBox.SelectedIndex < 0) return;
        Send($"PROF {ProfileBox.SelectedIndex}");   // keypad switches instantly and replies with its settings
    }

    void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        ApplySliders();
        Log("Applied to the active profile (live). Click 'Save to EEPROM' to keep it.");
    }

    void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        ApplySliders();
        Send("SAVE");
        Log("Saved all profiles + calibration to EEPROM.");
    }

    void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        string name = new string(ProfileNameBox.Text.Trim().Select(c => c <= ' ' || c == ',' ? '_' : c).ToArray());
        if (name.Length == 0) return;
        if (name.Length > 7) name = name[..7];
        profNames[activeProfile] = name;
        Send($"PNAME {activeProfile} {name}");
        RefreshProfileList();
    }

    void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        int t = CopyBox.SelectedIndex;
        if (t < 0 || t == activeProfile) return;
        if (MessageBox.Show($"Overwrite slot {t + 1} ({profNames[t]}) with slot {activeProfile + 1}?", "Copy profile",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ApplySliders();
        Send($"PCOPY {activeProfile} {t}");
    }

    void BtnResetSlot_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show($"Reset slot {activeProfile + 1} to defaults?", "Reset profile",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Send($"PRESET {activeProfile}");
    }

    void BtnRead_Click(object sender, RoutedEventArgs e) => Send("GET");

    void BtnDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reset ALL profiles and calibration to the built-in defaults?\n(Not permanent until you Save.)",
                "Defaults", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Send("DEFAULTS");
    }

    // ---------- window / misc ----------

    void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshPorts();
    void BtnConnect_Click(object sender, RoutedEventArgs e) { if (port.IsOpen) Disconnect(); else Connect(); }
    void MuteCheck_Changed(object sender, RoutedEventArgs e) => Send(MuteCheck.IsChecked == true ? "KEYS 0" : "KEYS 1");

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        Disconnect();
        syncTimer.Stop();
        analysisTimer.Stop();
        CompositionTarget.Rendering -= OnFrame;
        hook.Dispose();
    }

    // The keypad types y/u/t/g/Esc/Left into whatever has focus; keep them from changing sliders or the port box.
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (k is Key.Left or Key.Y or Key.U or Key.T or Key.G or Key.Escape) e.Handled = true;
    }

    void Log(string msg)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    // ---------- logger panel (expands to the right, window re-centres) ----------

    void BtnLogger_Click(object sender, RoutedEventArgs e)
    {
        loggerOpen = !loggerOpen;
        WindowState = WindowState.Normal;
        var wa = SystemParameters.WorkArea;

        if (loggerOpen)
        {
            closedW = Width; closedH = Height; closedMinW = MinWidth;
            LeftCol.Width = new GridLength(LeftWidth);
            LoggerCol.Width = new GridLength(1, GridUnitType.Star);
            LoggerPanel.Visibility = Visibility.Visible;
            MinWidth = LeftWidth + 480;
            Width = Math.Min(wa.Width - 20, LeftWidth + 1000);
            Height = Math.Min(wa.Height - 20, Math.Max(Height, 780));
            BtnLogger.Content = "Logger ◀";
        }
        else
        {
            LoggerPanel.Visibility = Visibility.Collapsed;
            LoggerCol.Width = new GridLength(0);
            LeftCol.Width = new GridLength(1, GridUnitType.Star);
            MinWidth = closedMinW;
            Width = closedW; Height = closedH;
            BtnLogger.Content = "Logger ▶";
        }
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
        analysedVersion = -1;
        UpdateScopeStream();
    }

    void TabLog_Click(object sender, RoutedEventArgs e) => ShowView(false);
    void TabScope_Click(object sender, RoutedEventArgs e) => ShowView(true);

    void ShowView(bool scope)
    {
        scopeTab = scope;
        LogView.Visibility = scope ? Visibility.Collapsed : Visibility.Visible;
        ScopeGrid.Visibility = scope ? Visibility.Visible : Visibility.Collapsed;
        BtnTabLog.IsChecked = !scope;
        BtnTabScope.IsChecked = scope;
        suppress = true;
        FreezeBtn.IsChecked = scope ? ScopeCtl.IsFrozen : TimelineCtl.IsFrozen;
        suppress = false;
        UpdateScopeStream();
    }

    // the keypad only streams fast samples while the scope is actually visible
    void UpdateScopeStream()
    {
        bool want = port.IsOpen && loggerOpen && scopeTab && !ScopeCtl.IsFrozen;
        if (want == scopeStreamOn) return;
        scopeStreamOn = want;
        Send(want ? "SCOPE 1" : "SCOPE 0");
    }

    void Freeze_Changed(object sender, RoutedEventArgs e)
    {
        if (suppress) return;
        bool f = FreezeBtn.IsChecked == true;
        if (scopeTab) { ScopeCtl.SetFrozen(f); UpdateScopeStream(); }
        else TimelineCtl.SetFrozen(f);
    }

    void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (scopeTab) { ScopeCtl.Clear(); return; }
        store.Clear();
        SpectrumCtl.SetData(null, 0, "");
        EstimateText.Text = "Polling rate: waiting for input...";
        EstimateText.Foreground = Brushes.Goldenrod;
        analysedVersion = -1;
        UpdateStats();
        TimelineCtl.InvalidateVisual();
    }

    void UpdateStats()
    {
        long now = Stopwatch.GetTimestamp(), from = now - 5 * Stopwatch.Frequency;
        int recent = 0;
        for (int i = store.Times.Count - 1; i >= 0 && store.Times[i] >= from; i--) recent++;
        StatsText.Text = $"{store.Times.Count} events  |  {recent / 5.0:0.0}/s (last 5 s)  |  hook: {(hook.Active ? "on" : "FAILED")}";
    }

    async void AnalysisTick(object? sender, EventArgs e)
    {
        if (!loggerOpen || scopeTab || TimelineCtl.IsFrozen) return;
        UpdateStats();
        if (analysing || store.Version == analysedVersion) return;

        analysing = true;
        analysedVersion = store.Version;
        var snapshot = store.Times.ToArray();
        try
        {
            await Task.Run(() => analyzer.Analyze(snapshot, Stopwatch.Frequency));
            ShowResult();
        }
        catch (Exception ex) { Log("Analysis error: " + ex.Message); }
        finally { analysing = false; }
    }

    void ShowResult()
    {
        string label = "";
        int n = analyzer.Events;
        if (n < 40)
        {
            EstimateText.Text = $"Polling rate: collecting... ({n} events, need ~100+)";
            EstimateText.Foreground = Brushes.Goldenrod;
        }
        else if (analyzer.Snr < 60)
        {
            EstimateText.Text = $"Polling rate: unclear (SNR {analyzer.Snr:0}x, {n} events) - keep tapping";
            EstimateText.Foreground = Brushes.Goldenrod;
        }
        else
        {
            double f = analyzer.EstimateHz;
            double std = StdRates.OrderBy(r => Math.Abs(Math.Log(f / r))).First();
            label = Math.Abs(f / std - 1) < 0.04 ? $"{std:0} Hz" : $"{f:0} Hz";
            EstimateText.Text = $"Polling rate ~ {label}   (peak {f:0.0} Hz, SNR {analyzer.Snr:0}x, {n} events)";
            EstimateText.Foreground = (Brush)new BrushConverter().ConvertFromString("#5EEAD4")!;
        }
        SpectrumCtl.SetData(analyzer.Spectrum, label.Length > 0 ? analyzer.EstimateHz : 0, label);
    }
}
