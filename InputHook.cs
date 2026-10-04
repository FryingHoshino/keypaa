using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace keypaa;

// Kind: 0 = key down, 1 = key up, 2 = wheel notch (Value = +1 CW / -1 CCW)
public readonly record struct RawEvent(byte Lane, byte Kind, int Value, long Ticks);

public sealed class Seg { public long Start; public long End; }   // End == 0 while still held

/// <summary>All logged input, touched on the UI thread only.</summary>
public sealed class EventStore
{
    // Lane order follows the .ino: KEY1 y, KEY2 u, KEY3 ESC, KEY4 g, KEY5 t, RE switch (Left), RE rotate (Alt+scroll)
    public static readonly string[] LaneNames = { "Y", "U", "ESC", "G", "T", "ENC SW", "ENC ROT" };
    public const int LaneCount = 7;

    public readonly List<Seg>[] Segs = new List<Seg>[LaneCount];
    public readonly List<(long t, int dir)> Wheels = new();
    public readonly List<long> Times = new();        // every event, for polling-rate analysis
    public int Version;
    readonly Seg?[] open = new Seg?[LaneCount];

    public EventStore() { for (int i = 0; i < LaneCount; i++) Segs[i] = new List<Seg>(); }

    public void Add(in RawEvent e)
    {
        switch (e.Kind)
        {
            case 0:
                var s = new Seg { Start = e.Ticks };
                open[e.Lane] = s;
                Segs[e.Lane].Add(s);
                Times.Add(e.Ticks);
                break;
            case 1:
                if (open[e.Lane] is { } o) { o.End = e.Ticks; open[e.Lane] = null; }
                Times.Add(e.Ticks);
                break;
            default:
                Wheels.Add((e.Ticks, e.Value));
                Times.Add(e.Ticks);
                break;
        }
        Version++;
        if (Times.Count > 60000) Times.RemoveRange(0, 20000);
        if (Segs[e.Lane].Count > 20000) Segs[e.Lane].RemoveRange(0, 5000);
        if (Wheels.Count > 20000) Wheels.RemoveRange(0, 5000);
    }

    public void Clear()
    {
        foreach (var l in Segs) l.Clear();
        Array.Clear(open);
        Wheels.Clear();
        Times.Clear();
        Version++;
    }
}

/// <summary>
/// Low-level keyboard + mouse hook on its own high-priority thread, so timestamps
/// are taken as soon as Windows delivers the event (not when the UI thread gets to it).
/// </summary>
public sealed class InputHook : IDisposable
{
    public readonly ConcurrentQueue<RawEvent> Queue = new();
    public bool Active => hKb != IntPtr.Zero;

    readonly bool[] held = new bool[EventStore.LaneCount];
    long altUp;
    Thread? thread;
    uint threadId;
    IntPtr hKb, hMs;
    Native.HookProc? kbProc, msProc;   // keep delegates alive

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "InputHook" };
        thread.Start();
    }

    void Run()
    {
        threadId = Native.GetCurrentThreadId();
        kbProc = KbProc;
        msProc = MsProc;
        var mod = Native.GetModuleHandle(null);
        hKb = Native.SetWindowsHookEx(13, kbProc, mod, 0);   // WH_KEYBOARD_LL
        hMs = Native.SetWindowsHookEx(14, msProc, mod, 0);   // WH_MOUSE_LL

        while (Native.GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref m);
            Native.DispatchMessage(ref m);
        }
        if (hKb != IntPtr.Zero) Native.UnhookWindowsHookEx(hKb);
        if (hMs != IntPtr.Zero) Native.UnhookWindowsHookEx(hMs);
    }

    static int LaneOf(uint vk) => vk switch
    {
        0x59 => 0,   // Y
        0x55 => 1,   // U
        0x1B => 2,   // ESC
        0x47 => 3,   // G
        0x54 => 4,   // T
        0x25 => 5,   // Left arrow (encoder switch)
        0xA4 => 6,   // Left Alt (encoder rotation)
        _ => -1
    };

    IntPtr KbProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            long t = Stopwatch.GetTimestamp();
            int msg = (int)wParam;
            int flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & 0x10) == 0)   // ignore injected (software) events
            {
                int lane = LaneOf((uint)Marshal.ReadInt32(lParam));
                if (lane >= 0)
                {
                    if ((msg == 0x100 || msg == 0x104) && !held[lane])        // ignore auto-repeat
                    {
                        held[lane] = true;
                        Queue.Enqueue(new RawEvent((byte)lane, 0, 0, t));
                    }
                    else if ((msg == 0x101 || msg == 0x105) && held[lane])
                    {
                        held[lane] = false;
                        if (lane == 6) altUp = t;
                        Queue.Enqueue(new RawEvent((byte)lane, 1, 0, t));
                    }
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    IntPtr MsProc(int code, IntPtr wParam, IntPtr lParam)
    {
        // Only wheel notches that belong to the encoder (Alt held, or released <50 ms ago)
        if (code >= 0 && (int)wParam == 0x020A)
        {
            long t = Stopwatch.GetTimestamp();
            if (held[6] || (t - altUp) < Stopwatch.Frequency / 20)
            {
                int flags = Marshal.ReadInt32(lParam, 12);
                if ((flags & 1) == 0)
                {
                    short delta = (short)(Marshal.ReadInt32(lParam, 8) >> 16);
                    Queue.Enqueue(new RawEvent(6, 2, delta > 0 ? 1 : -1, t));
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (threadId != 0) Native.PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);   // WM_QUIT
        thread?.Join(500);
    }

    static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
            public uint time; public int ptX; public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    }
}
