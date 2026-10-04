using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace keypaa;

public sealed class CsvItem
{
    public string Path { get; set; } = "";
    public string Display { get; set; } = "";
    public bool Selected { get; set; }
}

public partial class MainWindow
{
    readonly LatencyRecorder rec = new();
    readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Dictionary<int, long> syncPending = new();
    readonly List<CsvItem> csvItems = new();
    int syncId;
    double? prevW, prevH;

    void InitLatency()
    {
        syncTimer.Tick += (_, _) => { SendSync(); UpdateLatStatus(); };
        Directory.CreateDirectory(LatCsv.Folder);
    }

    // ---------- recording ----------

    void SendSync()
    {
        if (!port.IsOpen) return;
        int id = syncId++ & 0xFFFF;
        long t0 = Stopwatch.GetTimestamp();
        syncPending[id] = t0;
        try { port.WriteLine("SYNC " + id); } catch { /* ignore */ }
        if (syncPending.Count > 50) foreach (var k in syncPending.Keys.Take(25).ToList()) syncPending.Remove(k);
    }

    void OnSyncReply(int id, long devUs, long ts)
    {
        if (syncPending.Remove(id, out var t0)) rec.Clock.Add(devUs, t0, ts);
    }

    void UpdateLatStatus()
    {
        if (!rec.Active) return;
        LatStatus.Text = $"Recording...  clock sync: {rec.Clock.Count} samples, best RTT {rec.Clock.BestRttMs:0.00} ms  |  " +
                         $"paired events: {rec.Pairs.Count}  |  last {rec.LastMs:0.00} ms";
    }

    void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        if (rec.Active) StopRecording(); else StartRecording();
    }

    void StartRecording()
    {
        if (!port.IsOpen) { MessageBox.Show("Connect to the keypad first.", "Latency"); return; }
        if (MuteCheck.IsChecked == true) MuteCheck.IsChecked = false;   // muted keys never reach the PC
        syncPending.Clear();
        rec.Start();
        Send("EVT 1");
        syncTimer.Start();
        BtnRecord.Content = "■ Stop and save";
        BtnLatency.Content = "Latency ● REC";
        LatStatus.Text = "Recording... tap your keys (all of them if you like). First pairs appear after ~2 s of clock sync.";
    }

    void StopRecording()
    {
        syncTimer.Stop();
        Send("EVT 0");
        var pairs = rec.Finish();
        BtnRecord.Content = "● Record";
        BtnLatency.Content = "Latency ▼";
        if (pairs.Count < 5)
        {
            LatStatus.Text = $"Only {pairs.Count} paired events - nothing saved. Tap the keys for 20-30 s while recording (keys must not be muted).";
            return;
        }
        string path = LatCsv.Save(pairs, profNames[activeProfile], rec.Clock);
        LatStatus.Text = $"Saved {Path.GetFileName(path)}  ({pairs.Count} samples, mean {pairs.Average(p => p.Ms):0.00} ms)";
        RefreshCsvList(path);
    }

    // ---------- overlay ----------

    void BtnLatency_Click(object sender, RoutedEventArgs e)
    {
        if (LatOverlay.Visibility == Visibility.Visible) CloseLatency(); else OpenLatency();
    }

    void BtnLatClose_Click(object sender, RoutedEventArgs e) => CloseLatency();

    void OpenLatency()
    {
        var wa = SystemParameters.WorkArea;
        if (Width < 1000 || Height < 680)
        {
            prevW = Width; prevH = Height;
            Width = Math.Max(Width, Math.Min(1000, wa.Width - 20));
            Height = Math.Max(Height, Math.Min(700, wa.Height - 20));
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
        RefreshCsvList();
        LatOverlay.Visibility = Visibility.Visible;
        LatOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    void CloseLatency()
    {
        LatOverlay.Visibility = Visibility.Collapsed;
        if (prevW is double w)
        {
            var wa = SystemParameters.WorkArea;
            Width = w; Height = prevH!.Value;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
            prevW = null;
        }
    }

    // ---------- CSV list + comparison ----------

    void RefreshCsvList(string? select = null)
    {
        var keep = csvItems.Where(c => c.Selected).Select(c => c.Path).ToHashSet();
        if (select != null) keep.Add(select);
        string folder = LatCsv.Folder;
        var extra = csvItems.Select(c => c.Path).Where(p => !p.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
        var files = Directory.GetFiles(folder, "*.csv").Concat(extra).Distinct().OrderByDescending(File.GetLastWriteTime).ToList();

        csvItems.Clear();
        foreach (var f in files)
            csvItems.Add(new CsvItem { Path = f, Display = System.IO.Path.GetFileNameWithoutExtension(f), Selected = keep.Contains(f) });
        CsvList.ItemsSource = null;
        CsvList.ItemsSource = csvItems;
        Compare();
    }

    void Compare()
    {
        var series = new List<(string Name, Brush Color, double[] Data)>();
        var rows = new List<LatRow>();
        double? baseMean = null;
        int ci = 0;
        foreach (var it in csvItems.Where(c => c.Selected))
        {
            try
            {
                var (prof, ms) = LatCsv.Load(it.Path);
                if (ms.Length == 0) continue;
                rows.Add(LatStats.Row(it.Display, prof, ms, baseMean));
                baseMean ??= ms.Average();
                series.Add((it.Display, HistogramView.Palette[ci++ % HistogramView.Palette.Length], ms));
            }
            catch (Exception ex) { Log("CSV load failed: " + ex.Message); }
        }
        HistCtl.Series = series;
        HistCtl.InvalidateVisual();
        StatsGrid.ItemsSource = rows;
    }

    void CsvCheck_Click(object sender, RoutedEventArgs e) => Compare();
    void BtnCsvRefresh_Click(object sender, RoutedEventArgs e) => RefreshCsvList();
    void BtnCsvFolder_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", LatCsv.Folder);

    void BtnCsvBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CSV files|*.csv", Multiselect = true, InitialDirectory = LatCsv.Folder };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
            if (!csvItems.Any(c => c.Path == f))
                csvItems.Add(new CsvItem { Path = f, Display = System.IO.Path.GetFileNameWithoutExtension(f), Selected = true });
            else csvItems.First(c => c.Path == f).Selected = true;
        CsvList.ItemsSource = null;
        CsvList.ItemsSource = csvItems;
        Compare();
    }

    void BtnCsvDelete_Click(object sender, RoutedEventArgs e)
    {
        var del = csvItems.Where(c => c.Selected && c.Path.StartsWith(LatCsv.Folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (del.Count == 0) return;
        if (MessageBox.Show($"Delete {del.Count} ticked session file(s) from disk?", "Delete",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        foreach (var d in del) { try { File.Delete(d.Path); } catch { /* ignore */ } }
        RefreshCsvList();
    }
}
