using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace keypaa;

/// <summary>
/// All colours of the app. Pick a preset from the Theme popup, or choose "Custom" and use "Edit .txt"
/// (theme.txt in %AppData%\keypaa). Saving the file updates the app the moment you click back on it.
/// Colours can be #RRGGBB, #AARRGGBB or a colour name (HotPink, DimGray, ...). Anything you delete or
/// mistype in theme.txt falls back to the Dark preset.
/// </summary>
public static class Theme
{
    public static readonly string[] PresetNames = { "Crystal", "Dark", "Bright", "Custom" };
    public static string Current { get; private set; } = "Dark";
    public static string FontName { get; private set; } = "Segoe UI";
    public static event Action? Changed;

    static readonly (string Name, string About)[] Keys =
    {
        ("WindowBg", "main window background"),
        ("Text", "main window text"),
        ("Panel", "logger / scope side panel background"),
        ("OverlayDim", "veil behind the latency window"),
        ("OverlayPanel", "latency window and theme popup background"),
        ("TextBright", "strong text on panels, charts and buttons"),
        ("TextDim", "secondary text and chart labels"),
        ("Warn", "status text (collecting / unclear)"),
        ("Good", "status text (ok) and polling-rate marker"),
        ("KeyDown", "'PRESSED' text in live sensors"),
        ("ChartBg", "chart background"),
        ("ChartStripe", "alternate stripe / table rows"),
        ("ChartGrid", "grid lines, borders"),
        ("Lane0", "input log lane: Y"),
        ("Lane1", "input log lane: U"),
        ("Lane2", "input log lane: ESC"),
        ("Lane3", "input log lane: G"),
        ("Lane4", "input log lane: T"),
        ("Lane5", "input log lane: encoder switch"),
        ("Lane6", "input log lane: encoder rotation"),
        ("WheelCw", "encoder tick clockwise"),
        ("WheelCcw", "encoder tick counter-clockwise"),
        ("SpectrumFill", "spectrum fill"),
        ("SpectrumLine", "spectrum outline"),
        ("Cursor", "hover cursor line"),
        ("ScopeKey1", "scope trace key 1"),
        ("ScopeKey2", "scope trace key 2"),
        ("ScopePress", "scope press marker"),
        ("ScopeRelease", "scope release marker"),
        ("ScopeRef", "scope idle/full/deadzone lines"),
        ("ScopeTrigger", "scope rapid-trigger line"),
        ("Hist1", "latency curve 1"),
        ("Hist2", "latency curve 2"),
        ("Hist3", "latency curve 3"),
        ("Hist4", "latency curve 4"),
        ("Hist5", "latency curve 5"),
        ("Hist6", "latency curve 6"),
        ("ButtonBg", "buttons"),
        ("ButtonHover", "buttons on hover"),
        ("Accent", "pressed / checked buttons, progress bars"),
        ("InputBg", "text boxes")
    };

    // values are in the same order as Keys
    static readonly Dictionary<string, string[]> Presets = new()
    {
        ["Crystal"] = new[] { "#EAF5F5", "#112D32", "#D5EFEE", "#B00C312F", "#DFF1F0", "#0A2024", "#32595D", "#4F650B", "#136C5E", "#136C4F", "#F4FBFA", "#E7F3F3", "#B4CFCE", "#178217", "#15793F", "#188674", "#1D83A5", "#2256BF", "#3D2FDA", "#7523C7", "#167E4A", "#6B35B6", "#6634B2B2", "#177982", "#80112D32", "#117870", "#5937BE", "#157545", "#8B35B6", "#6C9391", "#71850F", "#178270", "#2256BF", "#7F26D9", "#1A9338", "#2A1FAD", "#BA23C7", "#F4FAFA", "#FFFFFF", "#69D3CB", "#FBFDFD" },
        ["Dark"]    = new[] { "#1E373E", "#E6EEF0", "#14252A", "#CC0A1417", "#192E33", "#F2F7F8", "#9FB6BC", "#E4E87D", "#78E2C8", "#63E3B9", "#0E1C20", "#142429", "#304950", "#80E5A2", "#AAEEDD", "#80D4E5", "#AAC6EE", "#8080E5", "#C6AAEE", "#D480E5", "#74E7C0", "#CE9DE7", "#6647D1C6", "#68DFD5", "#80FFFFFF", "#78E2D1", "#BEA5E9", "#68DFA3", "#D3A0E3", "#698D96", "#E7E774", "#80E5CC", "#99BBEA", "#C6AAEE", "#80E5A2", "#6F6FE2", "#DA91E8", "#233C43", "#2F5560", "#217368", "#112227" },
        ["Bright"]  = new[] { "#F5EEEA", "#322211", "#F2BDA5", "#B031180C", "#F1E5DF", "#24180A", "#5D4632", "#6B4906", "#2C511A", "#2E6722", "#FBF6F4", "#F3EBE7", "#CFBDB4", "#BF227D", "#D92653", "#BF2F22", "#C76823", "#93751A", "#798217", "#5B8B18", "#567B24", "#B42D65", "#66DF4620", "#B33319", "#80322211", "#B92740", "#B8720F", "#3C7321", "#B42D65", "#9D7C6C", "#A07A08", "#BF223C", "#AD721F", "#528B18", "#D02597", "#AD421F", "#7E7E16", "#FAF6F4", "#FFFFFF", "#D38969", "#FDFCFB" },
    };

    // Brushes are immutable (frozen). Changing theme builds a fresh set and swaps it into the application
    // resources: XAML ({DynamicResource Name}) restyles itself, drawing code reads the new brushes next frame.
    static Dictionary<string, SolidColorBrush> cur = new();
    public static SolidColorBrush Get(string name) => cur[name];
    public static SolidColorBrush Lane(int i) => cur["Lane" + i];
    public static SolidColorBrush WindowBg => cur["WindowBg"];
    public static SolidColorBrush Text => cur["Text"];
    public static SolidColorBrush Panel => cur["Panel"];
    public static SolidColorBrush OverlayDim => cur["OverlayDim"];
    public static SolidColorBrush OverlayPanel => cur["OverlayPanel"];
    public static SolidColorBrush TextBright => cur["TextBright"];
    public static SolidColorBrush TextDim => cur["TextDim"];
    public static SolidColorBrush Warn => cur["Warn"];
    public static SolidColorBrush Good => cur["Good"];
    public static SolidColorBrush KeyDown => cur["KeyDown"];
    public static SolidColorBrush ChartBg => cur["ChartBg"];
    public static SolidColorBrush ChartStripe => cur["ChartStripe"];
    public static SolidColorBrush ChartGrid => cur["ChartGrid"];
    public static SolidColorBrush Lane0 => cur["Lane0"];
    public static SolidColorBrush Lane1 => cur["Lane1"];
    public static SolidColorBrush Lane2 => cur["Lane2"];
    public static SolidColorBrush Lane3 => cur["Lane3"];
    public static SolidColorBrush Lane4 => cur["Lane4"];
    public static SolidColorBrush Lane5 => cur["Lane5"];
    public static SolidColorBrush Lane6 => cur["Lane6"];
    public static SolidColorBrush WheelCw => cur["WheelCw"];
    public static SolidColorBrush WheelCcw => cur["WheelCcw"];
    public static SolidColorBrush SpectrumFill => cur["SpectrumFill"];
    public static SolidColorBrush SpectrumLine => cur["SpectrumLine"];
    public static SolidColorBrush Cursor => cur["Cursor"];
    public static SolidColorBrush ScopeKey1 => cur["ScopeKey1"];
    public static SolidColorBrush ScopeKey2 => cur["ScopeKey2"];
    public static SolidColorBrush ScopePress => cur["ScopePress"];
    public static SolidColorBrush ScopeRelease => cur["ScopeRelease"];
    public static SolidColorBrush ScopeRef => cur["ScopeRef"];
    public static SolidColorBrush ScopeTrigger => cur["ScopeTrigger"];
    public static SolidColorBrush Hist1 => cur["Hist1"];
    public static SolidColorBrush Hist2 => cur["Hist2"];
    public static SolidColorBrush Hist3 => cur["Hist3"];
    public static SolidColorBrush Hist4 => cur["Hist4"];
    public static SolidColorBrush Hist5 => cur["Hist5"];
    public static SolidColorBrush Hist6 => cur["Hist6"];
    public static SolidColorBrush ButtonBg => cur["ButtonBg"];
    public static SolidColorBrush ButtonHover => cur["ButtonHover"];
    public static SolidColorBrush Accent => cur["Accent"];
    public static SolidColorBrush InputBg => cur["InputBg"];

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "keypaa");
    public static string FilePath => Path.Combine(Folder, "theme.txt");
    static string PresetFile => Path.Combine(Folder, "theme.preset");

    static Theme()
    {
        string p = "Dark";
        try { if (File.Exists(PresetFile)) p = File.ReadAllText(PresetFile).Trim(); } catch { /* ignore */ }
        Apply(PresetNames.Contains(p) ? p : "Dark", save: false);
    }

    /// <summary>Makes every colour available to XAML as {DynamicResource Name}.</summary>
    public static void Register(ResourceDictionary r)
    {
        foreach (var k in Keys) r[k.Name] = Get(k.Name);
    }

    static Dictionary<string, string> Dict(string preset)
    {
        var v = Presets[preset];
        return Keys.Select((k, i) => (k.Name, v[i])).ToDictionary(x => x.Name, x => x.Item2);
    }

    static Color Parse(string s, string fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(s)!; }
        catch { return (Color)ColorConverter.ConvertFromString(fallback)!; }
    }

    public static void Apply(string name, bool save = true)
    {
        var dark = Dict("Dark");
        var d = name == "Custom" ? new Dictionary<string, string>(dark) : Dict(name);
        string font = "Segoe UI";

        if (name == "Custom")
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllLines(FilePath, new[]
                    {
                        "// keypaa custom theme. Save this file, then click back on the app: it updates instantly.",
                        "// Colours: #RRGGBB, #AARRGGBB or a colour name (HotPink, DimGray...). Delete a line to use the Dark value.",
                        "",
                        "Font          = Segoe UI"
                    }.Concat(Keys.Select(k => $"{k.Name,-13} = {dark[k.Name],-9}  // {k.About}")));
                }
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    int c = raw.IndexOf("//", StringComparison.Ordinal);
                    var t = (c >= 0 ? raw[..c] : raw).Split('=', 2);
                    if (t.Length != 2) continue;
                    string key = t[0].Trim(), val = t[1].Trim();
                    if (key == "Font" && val.Length > 0) font = val;
                    else if (d.ContainsKey(key)) d[key] = val;
                }
            }
            catch { /* unreadable file: keep the Dark values */ }
        }

        var nb = new Dictionary<string, SolidColorBrush>();
        foreach (var k in Keys)
        {
            var b = new SolidColorBrush(Parse(d[k.Name], dark[k.Name]));
            b.Freeze();
            nb[k.Name] = b;
        }
        cur = nb;
        if (Application.Current != null)
            foreach (var k in Keys) Application.Current.Resources[k.Name] = nb[k.Name];
        FontName = font;
        Current = name;
        if (save)
        {
            try { Directory.CreateDirectory(Folder); File.WriteAllText(PresetFile, name); } catch { /* ignore */ }
        }
        Changed?.Invoke();
    }
}
