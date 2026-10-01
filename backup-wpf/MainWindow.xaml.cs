using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Quota;

public partial class MainWindow : Window
{
    // Connect over IPv4 first: on this network the IPv6 attempt stalls ~21 s before falling back.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = (await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct))
                .OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1).ToArray();
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct); return new System.Net.Sockets.NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }
    });
    static readonly string CredPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const double SquareSize = 11;
    const double WinW = 400, WinH = 360;   // fixed transparent canvas (DIPs) the pill animates inside
    // Horizontal on the top/bottom edge, vertical along the left/right edge (both collapsed and expanded)
    bool Vertical => _edge is Edge.Left or Edge.Right;
    double CollapsedW => Vertical ? 40 : 280;
    double CollapsedH => Vertical ? 150 : 36;
    double ExpandedW => Vertical ? 150 : 360;
    double ExpandedH => Vertical ? 300 : 140;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => { MakeToolWindow(); LoadDock(); };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => Dock(_monitor, _edge, _frac));
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) StartupItem.IsChecked = k?.GetValue("ClaudeQuota") != null;
        SessionTrack.Data = WeekTrack.Data = Arc(359.99);
        Inner.SizeChanged += (_, _) => UpdateClip();
        FilmSpin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever });
        RimSpin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(360, 0, TimeSpan.FromSeconds(9)) { RepeatBehavior = RepeatBehavior.Forever });
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        Loaded += async (_, _) => await RefreshAsync();
        // Keep on top even over other topmost windows
        var keep = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        keep.Tick += (_, _) => { Topmost = false; Topmost = true; };
        keep.Start();
    }

    // ---- Docking: drag anywhere, snaps to the nearest edge of the monitor it was dropped on ----
    // All placement maths is in physical pixels (the app is per-monitor DPI aware), so monitors with
    // different sizes, positions and scaling all work.
    enum Edge { Top, Bottom, Left, Right }
    static readonly string DockPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeQuota", "dock.txt");
    Edge _edge = Edge.Top;
    string _monitor = "";      // device name, e.g. \\.\DISPLAY2
    double _frac = 1;          // position along the edge: 0 = left/top end, 1 = right/bottom end
    bool _dragging;

    IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    void Pill_Down(object s, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        _dragging = true;
        try { DragMove(); }   // blocks until the mouse is released
        finally { _dragging = false; }
        Snap();
    }

    // Moving onto a monitor with different scaling makes WPF rescale/reposition the window; re-dock afterwards.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!_dragging) Dispatcher.BeginInvoke(() => Dock(_monitor, _edge, _frac));
    }

    void Snap()
    {
        // Where the collapsed pill is now, in physical pixels
        GetWindowRect(Hwnd, out var wr);
        double s = GetDpiForWindow(Hwnd) / 96.0;
        double cw = CollapsedW * s, ch = CollapsedH * s;
        double ox = Pill.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => wr.Right - wr.Left - cw,
            _ => (wr.Right - wr.Left - cw) / 2,
        };
        double oy = Pill.VerticalAlignment == VerticalAlignment.Bottom ? wr.Bottom - wr.Top - ch : 0;
        var pr = new RECT { Left = (int)(wr.Left + ox), Top = (int)(wr.Top + oy), Right = (int)(wr.Left + ox + cw), Bottom = (int)(wr.Top + oy + ch) };

        // Monitor it was dropped on; pull the pill inside its work area and pick the closest edge
        var mon = GetMonitor(MonitorFromRect(ref pr, 2 /* MONITOR_DEFAULTTONEAREST */));
        var wa = mon.Work;
        double x = Math.Clamp(pr.Left, wa.Left, wa.Right - cw), y = Math.Clamp(pr.Top, wa.Top, wa.Bottom - ch);
        var d = new[] { (Edge.Top, y - wa.Top), (Edge.Bottom, wa.Bottom - (y + ch)), (Edge.Left, x - wa.Left), (Edge.Right, wa.Right - (x + cw)) };
        var edge = d.MinBy(t => t.Item2).Item1;
        double frac = edge is Edge.Top or Edge.Bottom
            ? (x - wa.Left) / Math.Max(1, wa.Right - wa.Left - cw)
            : (y - wa.Top) / Math.Max(1, wa.Bottom - wa.Top - ch);
        Dock(mon.Name, edge, frac);
        try { Directory.CreateDirectory(Path.GetDirectoryName(DockPath)!); File.WriteAllText(DockPath, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_monitor}|{_edge}|{_frac}")); } catch { }
    }

    void LoadDock()
    {
        try
        {
            var p = File.ReadAllText(DockPath).Split('|');
            Dock(p[0], Enum.Parse<Edge>(p[1]), double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { Dock("", Edge.Top, 1); }   // default: top-right corner of the primary monitor
    }

    // Place the pill flush against an edge of a monitor (falls back to the primary one if it is gone).
    // It expands away from the edge and toward the screen centre, so the transparent window is aligned
    // accordingly and the pill never grows off-screen.
    void Dock(string monitor, Edge edge, double frac)
    {
        if (Hwnd == IntPtr.Zero) return;
        var mons = GetMonitors();
        var mon = mons.FirstOrDefault(m => m.Name == monitor);
        if (mon.Name == null) mon = mons.FirstOrDefault(m => m.Primary);
        if (mon.Name == null) return;
        _monitor = mon.Name; _edge = edge; _frac = Math.Clamp(frac, 0, 1);

        var wa = mon.Work;
        double s = mon.Scale;
        // Windows rescales the window when it crosses to a monitor with other scaling; restore the canvas size.
        Width = WinW; Height = WinH;
        double cw = CollapsedW * s, ch = CollapsedH * s, w = WinW * s, h = WinH * s;
        double x, y;
        if (edge is Edge.Top or Edge.Bottom)
        {
            double along = wa.Left + _frac * (wa.Right - wa.Left - cw);
            Pill.VerticalAlignment = edge == Edge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            y = edge == Edge.Top ? wa.Top : wa.Bottom - h;
            bool leftHalf = along + cw / 2 < (wa.Left + wa.Right) / 2.0;
            Pill.HorizontalAlignment = leftHalf ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            x = leftHalf ? along : along + cw - w;
        }
        else
        {
            double along = wa.Top + _frac * (wa.Bottom - wa.Top - ch);
            Pill.HorizontalAlignment = edge == Edge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            x = edge == Edge.Left ? wa.Left : wa.Right - w;
            bool topHalf = along + ch / 2 < (wa.Top + wa.Bottom) / 2.0;
            Pill.VerticalAlignment = topHalf ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            y = topHalf ? along : along + ch - h;
        }
        SetWindowPos(Hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h), 0x0004 | 0x0010);   // NOZORDER | NOACTIVATE

        // Square corners and no rim on the side touching the edge
        const double r = 16, t = 1.5;
        (Pill.CornerRadius, Pill.BorderThickness) = edge switch
        {
            Edge.Top => (new CornerRadius(0, 0, r, r), new Thickness(t, 0, t, t)),
            Edge.Bottom => (new CornerRadius(r, r, 0, 0), new Thickness(t, t, t, 0)),
            Edge.Left => (new CornerRadius(0, r, r, 0), new Thickness(0, t, t, t)),
            _ => (new CornerRadius(r, 0, 0, r), new Thickness(t, t, 0, t)),
        };

        // Keep the header row next to the edge when docked at the bottom (details open upward)
        bool bottom = edge == Edge.Bottom;
        Row0.Height = bottom ? new GridLength(1, GridUnitType.Star) : new GridLength(36);
        Row1.Height = bottom ? new GridLength(36) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(Header, bottom ? 1 : 0);
        Grid.SetRow(Details, bottom ? 0 : 1);
        LayoutDetails();
        UpdateClip();
        Expand(Pill.IsMouseOver);   // morph to the new collapsed shape (horizontal <-> vertical)
    }

    // Details: Session | Week | Extra side by side, or stacked top-to-bottom along a side edge.
    void LayoutDetails()
    {
        bool v = Vertical;
        Details.ColumnDefinitions.Clear();
        Details.RowDefinitions.Clear();
        if (v)
            for (int i = 0; i < 3; i++) Details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        else
            foreach (var w in new[] { 1, 1, 1.3 }) Details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });

        var panels = new[] { DSession, DWeek, DExtra };
        for (int i = 0; i < 3; i++)
        {
            Grid.SetRow(panels[i], v ? i : 0);
            Grid.SetColumn(panels[i], v ? 0 : i);
            panels[i].Margin = new Thickness(0, v && i > 0 ? 14 : 0, 0, 0);
        }
        Details.Margin = v ? new Thickness(10, 8, 10, 12) : new Thickness(16, 4, 16, 12);
        DExtra.HorizontalAlignment = v ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        DExtra.Margin = v ? new Thickness(0, 16, 0, 0) : new Thickness(8, 0, 0, 0);
        foreach (var t in DExtra.Children.OfType<TextBlock>()) t.TextAlignment = v ? TextAlignment.Center : TextAlignment.Left;
    }

    // Clip contents to the pill's rounded corners; the rect overhangs the docked edge so those corners stay square.
    void UpdateClip()
    {
        double w = Inner.ActualWidth, h = Inner.ActualHeight, o = 16;
        InnerClip.Rect = _edge switch
        {
            Edge.Top => new Rect(0, -o, w, h + o),
            Edge.Bottom => new Rect(0, 0, w, h + o),
            Edge.Left => new Rect(-o, 0, w + o, h),
            _ => new Rect(0, 0, w + o, h),
        };
    }

    // ---- Monitors (physical pixels) ----
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    record struct Monitor(string Name, RECT Work, double Scale, bool Primary);
    delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX info);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, int flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static Monitor GetMonitor(IntPtr hMon)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMon, ref info);
        GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _);
        return new Monitor(info.szDevice, info.rcWork, dpi / 96.0, (info.dwFlags & 1) != 0);
    }

    static List<Monitor> GetMonitors()
    {
        var list = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { list.Add(GetMonitor(h)); return true; }, IntPtr.Zero);
        return list;
    }

    // Tool window: hidden from Alt+Tab and shown on every virtual desktop.
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    void MakeToolWindow()
    {
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80);   // GWL_EXSTYLE |= WS_EX_TOOLWINDOW
    }

    async void Refresh_Click(object s, RoutedEventArgs e) => await RefreshAsync();
    void Exit_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();

    void Startup_Click(object s, RoutedEventArgs e)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
        if (StartupItem.IsChecked) k.SetValue("ClaudeQuota", $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue("ClaudeQuota", false);
    }

    // ---- Expand / collapse (200 ms ease-out) ----
    void Pill_MouseEnter(object s, MouseEventArgs e) => Expand(true);
    void Pill_MouseLeave(object s, MouseEventArgs e) => Expand(false);

    void Expand(bool open)
    {
        Animate(Pill, WidthProperty, open ? ExpandedW : CollapsedW);
        Animate(Pill, HeightProperty, open ? ExpandedH : CollapsedH);
        Animate(Details, OpacityProperty, open ? 1 : 0);
        // On the side edges the collapsed strip uses the vertical header; expanded always shows the full layout
        Animate(Header, OpacityProperty, open || !Vertical ? 1 : 0);
        Animate(VHeader, OpacityProperty, !open && Vertical ? 1 : 0);
    }

    static void Animate(UIElement el, DependencyProperty p, double to) =>
        el.BeginAnimation(p, new DoubleAnimation(to, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    // ---- Data ----
    async Task RefreshAsync()
    {
        try
        {
            using var cred = JsonDocument.Parse(File.ReadAllText(CredPath));
            var token = cred.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString();
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Add("Authorization", "Bearer " + token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.Add("User-Agent", "claude-code/2.0");
            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                SetStatus(false, resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Login expired - run Claude Code once." : $"HTTP {(int)resp.StatusCode}");
                return;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var session = ShowLimit(root, "five_hour", SessionArc, SessionPct, SessionReset, FillSession);
            var week = ShowLimit(root, "seven_day", WeekArc, WeekPct, WeekReset, FillWeek);
            Summary.Text = $"{Pct(session)}  ·  {Pct(week)}";
            VSession.Text = Pct(session); VWeek.Text = Pct(week);
            ShowExtra(root);
            SetStatus(true, $"Updated {DateTime.Now:HH:mm}");
        }
        catch (Exception ex) { SetStatus(false, ex.Message); }
    }

    static string Pct(double? v) => v is double d ? $"{d:0}%" : "–";

    void SetStatus(bool ok, string text)
    {
        FillStatus.Height = ok ? SquareSize : 0;
        SqStatus.ToolTip = StatusText.Text = text;
    }

    // Returns percent left, and fills the ring, labels and the matching square.
    static double? ShowLimit(JsonElement root, string key, System.Windows.Shapes.Path arc, TextBlock pct, TextBlock reset, Border fill)
    {
        if (!root.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object)
        {
            arc.Data = Geometry.Empty; pct.Text = "–"; reset.Text = ""; fill.Height = 0;
            return null;
        }
        double left = Math.Clamp(100 - e.GetProperty("utilization").GetDouble(), 0, 100);
        arc.Data = left < 0.5 ? Geometry.Empty : Arc(Math.Min(left, 99.9) * 3.6);
        pct.Text = $"{left:0}%";
        fill.Height = SquareSize * left / 100;
        reset.Text = "";
        if (e.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String)
        {
            var d = DateTimeOffset.Parse(r.GetString()!) - DateTimeOffset.Now;
            reset.Text = d.TotalHours >= 24 ? $"{(int)d.TotalDays}d {d.Hours}h left" : $"{Math.Max(0, (int)d.TotalHours)}h {Math.Max(0, d.Minutes)}m left";
        }
        return left;
    }

    // Extra usage (credits spent beyond the plan). The red square fills with spend vs. the monthly limit.
    void ShowExtra(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var x) || x.ValueKind != JsonValueKind.Object
            || !x.TryGetProperty("is_enabled", out var en) || !en.GetBoolean())
        {
            ExtraText.Text = "Off"; FillExtra.Height = 0; SqExtra.ToolTip = "Extra usage: off";
            return;
        }
        int dp = x.TryGetProperty("decimal_places", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 2;
        double scale = Math.Pow(10, dp);
        double used = x.TryGetProperty("used_credits", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() / scale : 0;
        double? limit = x.TryGetProperty("monthly_limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() / scale : null;
        var cur = x.TryGetProperty("currency", out var c) && c.GetString() == "USD" ? "$" : "";
        ExtraText.Text = limit is double lim ? $"{cur}{used:0.00} / {cur}{lim:0.00}" : $"{cur}{used:0.00} used";
        FillExtra.Height = limit is double m && m > 0 ? SquareSize * Math.Clamp(used / m, 0, 1) : 0;
        SqExtra.ToolTip = "Extra usage: " + ExtraText.Text;
    }

    // Arc starting at 12 o'clock, clockwise, in a 48x48 box with a 5px stroke
    static Geometry Arc(double degrees)
    {
        const double c = 24, r = 21.5;
        double a = degrees * Math.PI / 180;
        var fig = new PathFigure { StartPoint = new Point(c, c - r) };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, degrees > 180, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }
}
