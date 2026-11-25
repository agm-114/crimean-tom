using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Quickload;

// The Unity player can spend several seconds in its splash before it delivers an OnGUI event.
public static class SplashProgress
{
    private static readonly ManualResetEvent dismissed = new(false);
    private static int started, hidden, checkedSnapshots, sceneReady;
    private const int SnapshotCount = 4;
    private static long shownAtTicks;

    internal static void Show(bool cacheEnabled)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || Interlocked.Exchange(ref started, 1) != 0) return;
        var thread = new Thread(() => DrawUntilHandoff(cacheEnabled)) { IsBackground = true, Name = "Quickload splash" };
        thread.Start();
    }

    public static void SnapshotChecked() => Interlocked.Increment(ref checkedSnapshots);

    public static void SceneReady() => Volatile.Write(ref sceneReady, 1);

    public static TimeSpan? Hide()
    {
        if (Interlocked.Exchange(ref hidden, 1) != 0) return null;
        dismissed.Set();
        long ticks = Volatile.Read(ref shownAtTicks);
        return ticks > 0 ? TimeSpan.FromTicks(ticks) : null;
    }

    private static void DrawUntilHandoff(bool cacheEnabled)
    {
        nint window = 0, previousProc = 0;
        WindowProc? paint = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            var clock = Stopwatch.StartNew();
            nint game = 0;
            while ((game = GameWindow(process.Id)) == 0)
                if (dismissed.WaitOne(40) || clock.Elapsed.TotalSeconds > 60) return;

            if (dismissed.WaitOne(0)) return;
            const uint popup = 0x80000000, noActivate = 0x08000000, toolWindow = 0x80, layered = 0x80000, transparent = 0x20;
            window = CreateWindowExW(noActivate | toolWindow | layered | transparent, "STATIC", "", popup,
                0, 0, 0, 0, game, 0, 0, 0);
            if (window == 0) return;
            paint = (hwnd, message, wParam, lParam) =>
            {
                if (message == 0x14) return 1; // WM_ERASEBKGND: Paint fills the entire panel.
                if (message != 0x0F) return CallWindowProcW(previousProc, hwnd, message, wParam, lParam);
                nint dc = GetDC(hwnd);
                try
                {
                    if (dc != 0 && GetClientRect(hwnd, out var bounds)) Paint(dc, bounds, cacheEnabled, clock.Elapsed);
                }
                finally { ReleaseDC(hwnd, dc); ValidateRect(hwnd, 0); }
                return 0;
            };
            previousProc = SetWindowLongPtrW(window, -4, Marshal.GetFunctionPointerForDelegate(paint));
            if (previousProc == 0) return;
            SetLayeredWindowAttributes(window, 0, 240, 2);
            long lastPaint = -250;
            (int X, int Y, int Width, int Height) last = default;
            while (!dismissed.WaitOne(0) && clock.Elapsed.TotalMinutes < 2 && GetClientRect(game, out var client))
            {
                while (PeekMessageW(out var message, 0, 0, 0, 1)) DispatchMessageW(ref message);
                if (clock.ElapsedMilliseconds - lastPaint >= 200)
                {
                    InvalidateRect(window, 0, false);
                    lastPaint = clock.ElapsedMilliseconds;
                }
                Point origin = default;
                if (client.Right > 0 && client.Bottom > 0 && ClientToScreen(game, ref origin))
                {
                    int width = Math.Min(client.Right, Math.Clamp(client.Right / 3, 320, 560));
                    int height = Math.Clamp(client.Bottom / 13, 72, 92);
                    var bounds = (origin.X + (client.Right - width) / 2, origin.Y + client.Bottom * 85 / 100 - height, width, height);
                    if (bounds != last)
                    {
                        if (SetWindowPos(window, 0, bounds.Item1, bounds.Item2, width, height, 0x50) && last == default)
                            Volatile.Write(ref shownAtTicks, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).Ticks);
                        last = bounds;
                    }
                }
                if (dismissed.WaitOne(50)) break;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            // The in-game progress panel works independently of the Windows splash.
        }
        finally
        {
            if (window != 0) DestroyWindow(window);
            GC.KeepAlive(paint);
        }
    }

    private static void Paint(nint dc, Rect bounds, bool cacheEnabled, TimeSpan elapsed)
    {
        int width = bounds.Right, height = bounds.Bottom, pad = height / 5;
        if (width <= 2 * pad || height < 4) return;
        int count = Math.Min(SnapshotCount, Volatile.Read(ref checkedSnapshots));
        bool scene = Volatile.Read(ref sceneReady) != 0;
        Fill(0, 0, width, height, 0x282623);
        SetBkMode(dc, 1); // transparent text background
        Text("Quickload", pad, pad / 2, width - pad, height / 2, height / 4, 600, 0xF3F2EF);
        var previous = PreviousLaunch.Current;
        Text(previous?.Estimate ?? "Learning ETA", pad, pad / 2, width - pad, height / 2, height / 6, 400, 0xBBB8B3, right: true);
        int left = pad, right = width - pad, y = height / 2, bar = Math.Max(4, height / 16);
        Fill(left, y, right, y + bar, 0x474340);
        if (previous != null)
        {
            // Reserve a few pixels for brief stages. The remaining width follows the last launch.
            int gap = 1, minimum = Math.Min(3, Math.Max(0, (right - left) / previous.Count - gap));
            int flexible = Math.Max(0, right - left - previous.Count * minimum - (previous.Count - 1) * gap);
            double cumulative = 0;
            int start = left;
            for (int i = 0; i < previous.Count; i++)
            {
                var stage = previous.ReadStage(i);
                cumulative += stage.Seconds;
                int end = left + (i + 1) * minimum + i * gap + (int)Math.Round(flexible * cumulative / previous.TotalSeconds);
                if (stage.Fraction > 0) Fill(start, y, start + (int)Math.Round((end - start) * stage.Fraction), y + bar, 0x4952BD);
                if (i + 1 < previous.Count) Fill(end, y, end + gap, y + bar, 0x282623);
                start = end + gap;
            }
        }
        else
        {
            int span = (right - left) / 5;
            int x = left + (int)((right - left - span) * (1 - Math.Cos(elapsed.TotalSeconds * 2)) / 2);
            Fill(x, y, x + span, y + bar, 0x4952BD);
        }
        string phase = scene ? (count == SnapshotCount || !cacheEnabled ? "Opening the loading screen" : "Preparing game data")
            : (cacheEnabled && count < 2 ? "Reading startup snapshots" : "Waiting for the first scene");
        Text(phase, pad, y + bar + 2, right, height - pad / 3, height / 6, 400, 0xC4C1BC);
        Text($"Elapsed {PreviousLaunch.FormatTime(PreviousLaunch.Elapsed)}", pad, y + bar + 2, right, height - pad / 3,
            height / 6, 400, 0xBBB8B3, right: true);

        void Fill(int x1, int y1, int x2, int y2, uint color)
        {
            Rect area = new() { Left = x1, Top = y1, Right = x2, Bottom = y2 };
            SetDCBrushColor(dc, color);
            FillRect(dc, ref area, GetStockObject(18)); // DC_BRUSH
        }

        void Text(string value, int x1, int y1, int x2, int y2, int size, int weight, uint color, bool right = false)
        {
            nint font = CreateFontW(-size, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            nint previous = SelectObject(dc, font);
            Rect area = new() { Left = x1, Top = y1, Right = x2, Bottom = y2 };
            SetTextColor(dc, color);
            DrawTextW(dc, value, value.Length, ref area, 0x8024u | (right ? 2u : 0)); // single line, centered vertically, ellipsis
            SelectObject(dc, previous);
            DeleteObject(font);
        }
    }

    private static nint GameWindow(int processId)
    {
        nint window = 0;
        while ((window = FindWindowExW(0, window, "UnityWndClass", null)) != 0)
        {
            GetWindowThreadProcessId(window, out int owner);
            if (owner == processId) return window;
        }
        return 0;
    }

    private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Position;
        public uint Private;
    }

    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string type, string? name);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string type, string text, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32")] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32")] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32")] private static extern nint CallWindowProcW(nint previous, nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32")] private static extern nint GetDC(nint window);
    [DllImport("user32")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32")] private static extern bool InvalidateRect(nint window, nint area, bool erase);
    [DllImport("user32")] private static extern bool ValidateRect(nint window, nint area);
    [DllImport("user32")] private static extern int FillRect(nint dc, ref Rect area, nint brush);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int DrawTextW(nint dc, string text, int length, ref Rect area, uint flags);
    [DllImport("gdi32")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32")] private static extern uint SetDCBrushColor(nint dc, uint color);
    [DllImport("gdi32")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int height, int width, int angle, int orientation,
        int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32")] private static extern nint GetStockObject(int index);
}
