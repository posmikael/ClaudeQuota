using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace Quota;

public partial class MainWindow : Window
{
    // Connect over IPv4 first: on some networks the IPv6 attempt stalls ~21 s before falling back.
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
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string CredPath = Path.Combine(Home, ".claude", ".credentials.json");
    const double SquareSize = 11;
    // Horizontal on the top/bottom edge, vertical along the left/right edge (both collapsed and expanded)
    bool Vertical => _edge is Edge.Left or Edge.Right;
    double CollapsedW => Vertical ? 40 : 280;
    double CollapsedH => Vertical ? 150 : 36;
    double ExpandedW => Vertical ? 150 : 360;
    double ExpandedH => Vertical ? 360 : 166;
    readonly DispatcherTimer _redock = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };   // on HTTP 429 RefreshAsync waits out Retry-After

    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            Win32Properties.SetWindowCornerPreference(this, Win32Properties.WindowCornerPreference.DoNotRound);
            // Tool window: hidden from Alt+Tab
            Win32Properties.AddWindowStylesCallback(this, (style, exStyle) => (style, exStyle | 0x80 /* WS_EX_TOOLWINDOW */));
            // Take sizes as given. The default WM_WINDOWPOSCHANGING handling enforces a minimum height (47 px at
            // 125%) that is taller than the 36 DIP pill, pushing it 2 px past the screen edge.
            Win32Properties.AddWndProcHookCallback(this, (IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                const int SWP_NOSIZE = 0x1;
                if (msg == 0x0046 /* WM_WINDOWPOSCHANGING */ && (Marshal.ReadInt32(lParam, 32 /* WINDOWPOS.flags */) & SWP_NOSIZE) == 0)
                    handled = true;
                return IntPtr.Zero;
            });
        }
        StartupItem.Header = OperatingSystem.IsWindows() ? "Start with Windows" : "Start at login";
        StartupItem.IsVisible = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        StartupItem.IsChecked = StartupEnabled();

        Opened += async (_, _) => { LoadDock(); await RefreshAsync(); };
        // Display changes (plug/unplug, Teams screen share) fire in bursts: re-dock once, after things settle.
        // A second pass shortly after catches DPI/size changes the OS applies late and forces a fresh frame;
        // after a monitor was unplugged the pill was once left drawn at half height.
        _redock.Tick += (_, _) =>
        {
            _redock.Stop();
            if (_pressed) return;
            Dock(_monitor, _edge, _frac);
            DispatcherTimer.RunOnce(() => { if (!_pressed) { Dock(_monitor, _edge, _frac); Pill.InvalidateMeasure(); InvalidateVisual(); } }, TimeSpan.FromMilliseconds(600));
        };
        Screens.Changed += (_, _) => { _redock.Stop(); _redock.Start(); };
        // Moving onto a monitor with different scaling makes the OS rescale/reposition the window; re-dock afterwards.
        ScalingChanged += (_, _) => { if (!_pressed) { _redock.Stop(); _redock.Start(); } };

        // Hover: poll the cursor. Enter/leave events misfire when the window grows under the pointer.
        // The same 50 ms tick drives the shimmer: 20 fps is smooth for such a slow turn and far cheaper than every frame.
        var hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var clock = Stopwatch.StartNew();
        hover.Tick += (_, _) =>
        {
            if (_hidden || !IsVisible) return;
            Shimmer(clock.Elapsed.TotalSeconds);
            if (!_pressed && PointerOverPill() != _open) Expand(!_open);
        };
        hover.Start();
        Pill.PointerPressed += Pill_Pressed;
        Pill.PointerMoved += Pill_Moved;
        Pill.PointerReleased += Pill_Released;

        SessionTrack.Data = WeekTrack.Data = Arc(359.99);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        // Countdowns, pace and staleness move with the clock, not only when a refresh succeeds
        var render = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        render.Tick += (_, _) => Render();
        render.Start();
        // Every second: step aside for a fullscreen app on the pill's screen. Every 5 s: keep on top even over
        // other topmost windows. Re-raise within the topmost band in one call; toggling Topmost made it flicker.
        var keep = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int ticks = 0;
        keep.Tick += (_, _) =>
        {
            if (_pressed || TryGetPlatformHandle()?.Handle is not IntPtr h) return;
            bool hide = FullscreenOverPill(h);
            if (hide != _hidden)
            {
                _hidden = hide;
                if (hide) Expand(false);
                ShowWindow(h, hide ? 0 : 4);   // SW_HIDE, SW_SHOWNOACTIVATE
            }
            if (!_hidden && ++ticks % 5 == 0)
                SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE
        };
        if (OperatingSystem.IsWindows()) keep.Start();
    }

    // Slowly rotate the film (one turn per 14 s) and, the other way, the rim (per 9 s): the soap-bubble shimmer
    void Shimmer(double s)
    {
        if (Film.Fill is Brush film) film.Transform = new RotateTransform(s * 360 / 14 % 360);
        if (Pill.BorderBrush is Brush rim) rim.Transform = new RotateTransform(360 - s * 360 / 9 % 360);
    }

    // ---- Fullscreen (Windows): hide while a fullscreen app (video, game, slide show) covers the pill's screen ----
    bool _hidden;

    bool FullscreenOverPill(IntPtr self)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self || !GetWindowRect(fg, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var center = new POINT { X = _pill.Center.X, Y = _pill.Center.Y };
        if (!GetMonitorInfo(MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi)) return false;
        var m = mi.rcMonitor;
        if (r.Left > m.Left || r.Top > m.Top || r.Right < m.Right || r.Bottom < m.Bottom) return false;
        // A maximized window can cover the screen too (auto-hide taskbar), but it keeps its title bar
        const int WS_CAPTION = 0x00C00000;
        if ((GetWindowLong(fg, -16 /* GWL_STYLE */) & WS_CAPTION) == WS_CAPTION) return false;
        // The desktop itself is a screen-sized window without a caption
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        return cls.ToString() is not ("Progman" or "WorkerW");
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    // ---- Docking: drag anywhere, snaps to the nearest edge of the screen it was dropped on ----
    // Screen geometry and window Position are in "desktop units": physical pixels on Windows, points on macOS.
    // Sizes are in DIPs, so convert with Units().
    enum Edge { Top, Bottom, Left, Right }
    static readonly string DockPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeQuota", "dock.txt");
    Edge _edge = Edge.Top;
    string _monitor = "";      // screen key, see Key()
    double _frac = 1;          // position along the edge: 0 = left/top end, 1 = right/bottom end
    PixelRect _pill;           // the collapsed pill on screen; the window grows from it when expanded
    double _unit = 1;          // desktop units per DIP on the docked screen
    bool _open;
    bool _pressed, _moved;
    PixelPoint _pressPointer, _pressWindow;

    double Units(Screen s) => s.Scaling * DesktopScaling / RenderScaling;
    static string Key(Screen s) => $"{s.DisplayName}@{s.Bounds.X},{s.Bounds.Y}";
    PixelPoint PointerOnScreen(PointerEventArgs e) => this.PointToScreen(e.GetPosition(this));

    void Pill_Pressed(object? s, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Pill).Properties.IsLeftButtonPressed) return;
        _pressed = true; _moved = false;
        _pressPointer = PointerOnScreen(e);
        _pressWindow = Position;
        e.Pointer.Capture(Pill);
    }

    void Pill_Moved(object? s, PointerEventArgs e)
    {
        if (!_pressed) return;
        var p = PointerOnScreen(e);
        int dx = p.X - _pressPointer.X, dy = p.Y - _pressPointer.Y;
        if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 3) return;
        _moved = true;
        Position = new PixelPoint(_pressWindow.X + dx, _pressWindow.Y + dy);
    }

    void Pill_Released(object? s, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        if (_moved) Snap();
        else _ = RefreshAsync(true);   // a click without a drag refreshes
    }

    bool PointerOverPill()
    {
        if (CursorOnScreen() is not PixelPoint c) return Pill.IsPointerOver;
        var b = Pill.Bounds;
        return new PixelRect(this.PointToScreen(b.TopLeft), this.PointToScreen(b.BottomRight)).Contains(c);
    }

    // Global cursor position in desktop units (see Units()), or null where there is no API for it.
    PixelPoint? CursorOnScreen()
    {
        if (OperatingSystem.IsWindows())
            return GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;
        // The menu-bar screen is the one at the origin
        if (OperatingSystem.IsMacOS() && (Screens.All.FirstOrDefault(s => s.Bounds.Position == default) ?? Screens.Primary) is { } main)
        {
            // [NSEvent mouseLocation]: points, origin at the bottom-left of the menu-bar screen
            var m = objc_msgSend_point(objc_getClass("NSEvent"), sel_registerName("mouseLocation"));
            double k = DesktopScaling;
            return new PixelPoint((int)(m.X * k), (int)(main.Bounds.Bottom - m.Y * k));
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CGPoint { public double X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("/usr/lib/libobjc.A.dylib")] static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern CGPoint objc_msgSend_point(IntPtr receiver, IntPtr selector);

    void Snap()
    {
        // Where the collapsed pill is now
        double u = DesktopScaling;
        int ww = (int)Math.Round(Width * u), wh = (int)Math.Round(Height * u);
        int cw = (int)Math.Round(CollapsedW * u), ch = (int)Math.Round(CollapsedH * u);
        int px = Position.X + (Pill.HorizontalAlignment == HorizontalAlignment.Right ? ww - cw : 0);
        int py = Position.Y + (Pill.VerticalAlignment == VerticalAlignment.Bottom ? wh - ch : 0);
        var pr = new PixelRect(px, py, cw, ch);

        // Screen it was dropped on; pull the pill inside its work area and pick the closest edge
        var mon = Screens.ScreenFromBounds(pr) ?? Screens.ScreenFromPoint(pr.Center) ?? Screens.Primary;
        if (mon == null) return;
        var wa = mon.WorkingArea;
        u = Units(mon);
        cw = (int)Math.Round(CollapsedW * u); ch = (int)Math.Round(CollapsedH * u);
        double x = Math.Clamp(px, wa.X, wa.Right - cw), y = Math.Clamp(py, wa.Y, wa.Bottom - ch);
        var d = new[] { (Edge.Top, y - wa.Y), (Edge.Bottom, wa.Bottom - (y + ch)), (Edge.Left, x - wa.X), (Edge.Right, wa.Right - (x + cw)) };
        var edge = d.MinBy(t => t.Item2).Item1;
        // The pill's length along the edge depends on the edge's orientation
        bool vert = edge is Edge.Left or Edge.Right;
        double len = Math.Round((vert ? 150 : 280) * u);
        double frac = vert
            ? (y - wa.Y) / Math.Max(1, wa.Height - len)
            : (x - wa.X) / Math.Max(1, wa.Width - len);
        Dock(Key(mon), edge, frac);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DockPath)!);
            File.WriteAllText(DockPath, string.Create(CultureInfo.InvariantCulture, $"{_monitor}|{_edge}|{_frac}"));
        }
        catch { }
    }

    void LoadDock()
    {
        try
        {
            var p = File.ReadAllText(DockPath).Split('|');
            Dock(p[0], Enum.Parse<Edge>(p[1]), double.Parse(p[2], CultureInfo.InvariantCulture));
        }
        catch { Dock("", Edge.Top, 1); }   // default: top-right corner of the primary screen
    }

    // Place the pill flush against an edge of a screen (falls back to the primary one if it is gone).
    // It expands away from the edge and toward the screen centre, so the window is anchored on that side
    // and the pill never grows off-screen.
    void Dock(string monitor, Edge edge, double frac)
    {
        var all = Screens.All;
        var mon = all.FirstOrDefault(m => Key(m) == monitor)
            ?? all.FirstOrDefault(m => !string.IsNullOrEmpty(m.DisplayName) && monitor.StartsWith(m.DisplayName + "@"))   // same screen, moved
            ?? Screens.Primary ?? all.FirstOrDefault();
        if (mon == null) return;
        _monitor = Key(mon); _edge = edge; _frac = Math.Clamp(frac, 0, 1);

        var wa = mon.WorkingArea;
        _unit = Units(mon);
        int cw = (int)Math.Round(CollapsedW * _unit), ch = (int)Math.Round(CollapsedH * _unit);
        int x, y;
        if (edge is Edge.Top or Edge.Bottom)
        {
            x = wa.X + (int)Math.Round(_frac * (wa.Width - cw));
            y = edge == Edge.Top ? wa.Y : wa.Bottom - ch;
            Pill.VerticalAlignment = edge == Edge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            Pill.HorizontalAlignment = x + cw / 2 < wa.Center.X ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        else
        {
            x = edge == Edge.Left ? wa.X : wa.Right - cw;
            y = wa.Y + (int)Math.Round(_frac * (wa.Height - ch));
            Pill.HorizontalAlignment = edge == Edge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Pill.VerticalAlignment = y + ch / 2 < wa.Center.Y ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        }
        _pill = new PixelRect(x, y, cw, ch);

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
        Inner.RowDefinitions[0].Height = bottom ? new GridLength(1, GridUnitType.Star) : new GridLength(36);
        Inner.RowDefinitions[1].Height = bottom ? new GridLength(36) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(Header, bottom ? 1 : 0);
        Grid.SetRow(Details, bottom ? 0 : 1);
        LayoutDetails();
        Place(_open);
        Expand(_open);   // morph to the new shape (horizontal <-> vertical)
    }

    // Size and place the window around the pill: collapsed, or the expanded size while hovered.
    // On Windows the window always has the expanded size and a window region trims it (and its clicks) to the
    // collapsed pill: resizing a DirectComposition window shows one stale frame of the old content at the new
    // origin, which made the pill blink sideways on every hover.
    void Place(bool open)
    {
        bool win = OperatingSystem.IsWindows();
        double w = open || win ? ExpandedW : CollapsedW, h = open || win ? ExpandedH : CollapsedH;
        int pw = (int)Math.Round(w * _unit), ph = (int)Math.Round(h * _unit);
        int x = Pill.HorizontalAlignment == HorizontalAlignment.Right ? _pill.Right - pw : _pill.X;
        int y = Pill.VerticalAlignment == VerticalAlignment.Bottom ? _pill.Bottom - ph : _pill.Y;
        // On Windows move and resize in one call; separate moves would flash the pill at a wrong spot.
        if (win && TryGetPlatformHandle()?.Handle is IntPtr hwnd)
        {
            SetWindowPos(hwnd, IntPtr.Zero, x, y, pw, ph, 0x0004 | 0x0010);   // NOZORDER | NOACTIVATE
            int rx = open ? 0 : _pill.X - x, ry = open ? 0 : _pill.Y - y;
            int rw = open ? pw : _pill.Width, rh = open ? ph : _pill.Height;
            SetWindowRgn(hwnd, CreateRectRgn(rx, ry, rx + rw, ry + rh), true);   // the window owns the region now
        }
        Width = w; Height = h;
        Position = new PixelPoint(x, y);
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

    // Details: Session | Week | Extra side by side, or stacked top-to-bottom along a side edge.
    void LayoutDetails()
    {
        bool v = Vertical;
        Details.ColumnDefinitions.Clear();
        Details.RowDefinitions.Clear();
        if (v)
            for (int i = 0; i < 3; i++) Details.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        else
            foreach (var w in new[] { 1, 1, 1.3 }) Details.ColumnDefinitions.Add(new ColumnDefinition(w, GridUnitType.Star));

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

    // ---- Expand / collapse (200 ms ease-out, see the transitions in the XAML) ----
    void Expand(bool open)
    {
        _open = open;
        if (open) Place(true);   // grow the window first so the pill has room
        Pill.Width = open ? ExpandedW : CollapsedW;
        Pill.Height = open ? ExpandedH : CollapsedH;
        // Darker tint while expanded so the small detail text stays readable over light windows
        Pill.Background = new SolidColorBrush(open ? Color.FromArgb(0xA0, 0, 0, 0) : Color.FromArgb(0x50, 0, 0, 0));
        Details.Opacity = open ? 1 : 0;
        // On the side edges the collapsed strip uses the vertical header; expanded always shows the full layout
        Header.Opacity = open || !Vertical ? 1 : 0;
        VHeader.Opacity = !open && Vertical ? 1 : 0;
        // Shrink the window (its region on Windows) once the pill has collapsed, so the space around it doesn't catch clicks
        if (!open) DispatcherTimer.RunOnce(() => { if (!_open) Place(false); }, TimeSpan.FromMilliseconds(220));
    }

    // ---- Context menu ----
    async void Refresh_Click(object? s, RoutedEventArgs e) => await RefreshAsync();
    void Exit_Click(object? s, RoutedEventArgs e) => Close();

    void Startup_Click(object? s, RoutedEventArgs e)
    {
        bool on = !StartupEnabled();
        try { SetStartup(on); } catch { }
        StartupItem.IsChecked = StartupEnabled();
    }

    // Autostart: HKCU Run key on Windows, a LaunchAgent on macOS
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string LaunchAgent = Path.Combine(Home, "Library", "LaunchAgents", "com.claudequota.plist");

    static bool StartupEnabled()
    {
        if (OperatingSystem.IsWindows())
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue("ClaudeQuota") != null;
        return OperatingSystem.IsMacOS() && File.Exists(LaunchAgent);
    }

    static void SetStartup(bool on)
    {
        if (OperatingSystem.IsWindows())
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
            if (on) k.SetValue("ClaudeQuota", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("ClaudeQuota", false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (!on) { File.Delete(LaunchAgent); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgent)!);
            File.WriteAllText(LaunchAgent, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>com.claudequota</string>
                  <key>ProgramArguments</key><array><string>{SecurityElement.Escape(Environment.ProcessPath)}</string></array>
                  <key>RunAtLoad</key><true/>
                </dict>
                </plist>
                """);
        }
    }

    // ---- Data ----
    bool _refreshing;
    DateTime? _updated;   // time of the last successful refresh
    DateTime _retryAt;    // after HTTP 429, no requests before this

    async Task RefreshAsync(bool manual = false)
    {
        if (_refreshing) return;   // e.g. a Keychain prompt is still waiting for an answer
        if (DateTime.Now < _retryAt)
        {
            if (manual) StatusText.Text = $"Rate limited, wait until {_retryAt:HH:mm}";
            await ShowCostsAsync();   // local data, no request needed
            return;
        }
        _refreshing = true;
        if (manual) StatusText.Text = "Updating…";
        try
        {
            using var cred = JsonDocument.Parse(await Task.Run(ReadCredentials));
            var token = cred.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString();
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Add("Authorization", "Bearer " + token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.Add("User-Agent", "claude-code/2.0");
            var resp = await Http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Retry-After is in seconds (about 2 min seen); without it, wait 5 min
                _retryAt = DateTime.Now + (resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                Fail("Rate limited", "HTTP 429");
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                Fail(resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Login expired - run Claude Code once." : $"HTTP {(int)resp.StatusCode}", $"HTTP {(int)resp.StatusCode}");
                return;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            _session = ReadLimit(root, "five_hour");
            _week = ReadLimit(root, "seven_day");
            ShowExtra(root);
            _updated = DateTime.Now;
            SetStatus(true, $"Updated {_updated:HH:mm}", "Click to refresh");
        }
        // Network errors read like "...(api.anthropic.com:443)"; show something short and keep the full text in the tooltip.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Fail("No connection", ex.Message); }
        catch (Exception ex) { Fail(ex.Message, ex.Message); }
        finally { _refreshing = false; }
        Render();   // after a failure too: the data on screen may have gone stale
        await ShowCostsAsync();
    }

    // Claude Code's login: in the Keychain on macOS, in ~/.claude/.credentials.json elsewhere.
    static string ReadCredentials()
    {
        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("/usr/bin/security", ["find-generic-password", "-s", "Claude Code-credentials", "-w"])
                { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0 && json.Trim().Length > 0) return json;
        }
        if (File.Exists(CredPath)) return File.ReadAllText(CredPath);
        throw new Exception("Not signed in - run Claude Code once.");
    }

    static string Pct(double? v) => v is double d ? $"{d:0}%" : "–";

    void SetStatus(bool ok, string text, string tip)
    {
        FillStatus.Height = ok ? SquareSize : 0;
        StatusText.Text = text;
        ToolTip.SetTip(SqStatus, text);
        ToolTip.SetTip(StatusText, tip);
    }

    // The data on screen stays from the last good refresh, so say how old it is.
    void Fail(string text, string detail) =>
        SetStatus(false, _updated is DateTime t ? $"{text} · updated {t:HH:mm}" : text,
            detail + Environment.NewLine + (DateTime.Now < _retryAt ? $"Next try after {_retryAt:HH:mm}" : "Click to retry"));

    // One usage window: percent used and when it resets (so it started at reset minus its length)
    record Limit(double Used, DateTimeOffset? Resets);
    Limit? _session, _week;
    static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5), WeekWindow = TimeSpan.FromDays(7);
    static readonly IBrush Low = new SolidColorBrush(Color.Parse("#FFD60A")), Critical = new SolidColorBrush(Color.Parse("#FF453A"));
    static IBrush? Warn(double? left) => left switch { <= 10 => Critical, <= 25 => Low, _ => null };

    static Limit? ReadLimit(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object) return null;
        DateTimeOffset? resets = e.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(r.GetString()!, CultureInfo.InvariantCulture) : null;
        return new Limit(e.GetProperty("utilization").GetDouble(), resets);
    }

    // Draws the last good data against the current time: countdowns, pace, low-quota colours, staleness.
    void Render()
    {
        if (_updated == null) return;   // "…" until the first answer
        var session = ShowLimit(_session, SessionWindow, SessionArc, SessionPace, SessionPct, SessionReset, SessionOut, FillSession);
        var week = ShowLimit(_week, WeekWindow, WeekArc, WeekPace, WeekPct, WeekReset, WeekOut, FillWeek);
        Summary.Inlines = [Colored(Pct(session), Warn(session)), new Run("  ·  "), Colored(Pct(week), Warn(week))];
        VSession.Text = Pct(session); VWeek.Text = Pct(week);
        VSession.Foreground = Warn(session) ?? Summary.Foreground;
        VWeek.Foreground = Warn(week) ?? Summary.Foreground;
        // Old numbers fade, so they aren't taken for current ones (offline, rate limited, logged out)
        double o = DateTime.Now - _updated > TimeSpan.FromMinutes(10) ? 0.45 : 1;
        Summary.Opacity = VSession.Opacity = VWeek.Opacity = DSession.Opacity = DWeek.Opacity = o;
    }

    static Run Colored(string text, IBrush? brush)
    {
        var run = new Run(text);
        if (brush != null) run.Foreground = brush;
        return run;
    }

    // Fills the ring, pace mark, labels and the matching square; returns percent left.
    static double? ShowLimit(Limit? l, TimeSpan window, Avalonia.Controls.Shapes.Path arc, Avalonia.Controls.Shapes.Path pace,
        TextBlock pct, TextBlock reset, TextBlock runsOut, Border fill)
    {
        pace.Data = null; reset.Text = ""; runsOut.IsVisible = false;
        if (l == null)
        {
            arc.Data = null; pct.Text = "–"; fill.Height = 0;
            return null;
        }
        double left = Math.Clamp(100 - l.Used, 0, 100);
        arc.Data = left < 0.5 ? null : Arc(Math.Min(left, 99.9) * 3.6);
        pct.Text = $"{left:0}%";
        pct.Foreground = Warn(left) ?? Brushes.White;
        fill.Height = SquareSize * left / 100;
        if (l.Resets is not DateTimeOffset r) return left;

        var now = DateTimeOffset.Now;
        var d = r - now;
        if (d <= TimeSpan.Zero) { reset.Text = "Resetting…"; return left; }
        reset.Text = d.TotalHours >= 24 ? $"{(int)d.TotalDays}d {d.Hours}h left" : $"{(int)d.TotalHours}h {d.Minutes}m left";

        // Even pace: what would be left now if the quota were spread evenly over the window
        var elapsed = window - d;
        pace.Data = Tick((1 - Math.Clamp(elapsed / window, 0, 1)) * 360);
        // At the average rate so far, when it runs out. Shown only if that is before the reset;
        // skipped very early in the window, where the rate means little.
        if (l.Used is >= 1 and < 100 && elapsed > window * 0.05)
        {
            var at = now + elapsed * ((100 - l.Used) / l.Used);
            if (at < r)
            {
                var t = at.LocalDateTime;
                runsOut.Text = "Runs out ~" + t.ToString(t.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture);
                runsOut.IsVisible = true;
            }
        }
        return left;
    }

    // Short mark across the ring at an angle from 12 o'clock, clockwise (same 48x48 box as Arc)
    static Geometry Tick(double degrees)
    {
        const double c = 24;
        double a = degrees * Math.PI / 180, x = Math.Sin(a), y = -Math.Cos(a);
        return new LineGeometry(new Point(c + 17.5 * x, c + 17.5 * y), new Point(c + 25.5 * x, c + 25.5 * y));
    }

    // Extra usage (credits spent beyond the plan). The red square fills with spend vs. the monthly limit.
    void ShowExtra(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var x) || x.ValueKind != JsonValueKind.Object
            || !x.TryGetProperty("is_enabled", out var en) || !en.GetBoolean())
        {
            ExtraText.Text = "Off"; FillExtra.Height = 0; ToolTip.SetTip(SqExtra, "Extra usage: off");
            return;
        }
        int dp = x.TryGetProperty("decimal_places", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 2;
        double scale = Math.Pow(10, dp);
        double used = x.TryGetProperty("used_credits", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() / scale : 0;
        double? limit = x.TryGetProperty("monthly_limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() / scale : null;
        var cur = x.TryGetProperty("currency", out var c) && c.GetString() == "USD" ? "$" : "";
        ExtraText.Text = limit is double lim
            ? string.Create(CultureInfo.InvariantCulture, $"{cur}{used:0.00} / {cur}{lim:0.00}")
            : string.Create(CultureInfo.InvariantCulture, $"{cur}{used:0.00} used");
        FillExtra.Height = limit is double m && m > 0 ? SquareSize * Math.Clamp(used / m, 0, 1) : 0;
        ToolTip.SetTip(SqExtra, "Extra usage: " + ExtraText.Text);
    }

    // ---- Cost: what the session and week would cost at API list prices ----
    // The usage endpoint only gives percentages, so add up the token usage Claude Code logs in its transcripts
    // (~/.claude/projects/**/*.jsonl). Only Claude Code on this computer counts, not claude.ai or other machines.
    static readonly string ProjectsDir = Path.Combine(Home, ".claude", "projects");
    record Usage(string Key, DateTimeOffset At, double Usd);
    readonly Dictionary<string, (DateTime Written, long Length, List<Usage> Items)> _transcripts = [];
    bool _costing;

    // USD per million tokens: input, output, cache read. Cache writes are 1.25x input (5 min) or 2x (1 h).
    // Longer ids first, since the match is by prefix. Unknown models are left out.
    static readonly (string Model, double In, double Out, double Read)[] Prices =
    [
        ("claude-fable-5-1", 10, 50, 0.25), ("claude-mythos-5-1", 10, 50, 0.25),
        ("claude-fable-5", 10, 50, 1), ("claude-mythos-5", 10, 50, 1),
        ("claude-opus-5-5", 4, 20, 0.20), ("claude-opus-5", 5, 25, 0.5),
        ("claude-opus-4-8", 5, 25, 0.5), ("claude-opus-4-7", 5, 25, 0.5), ("claude-opus-4-6", 5, 25, 0.5),
        ("claude-sonnet-5-5", 2, 10, 0.20), ("claude-sonnet-5", 2, 10, 0.20), ("claude-sonnet-4-6", 3, 15, 0.3),
        ("claude-haiku-5-5", 0.10, 0.50, 0.01), ("claude-haiku-4-5", 1, 5, 0.1),
    ];

    async Task ShowCostsAsync()
    {
        if (_costing) return;
        _costing = true;
        try
        {
            var now = DateTimeOffset.Now;
            var sessionStart = _session?.Resets is DateTimeOffset sr ? sr - SessionWindow : now - SessionWindow;
            var weekStart = _week?.Resets is DateTimeOffset wr ? wr - WeekWindow : now - WeekWindow;
            var (session, week) = await Task.Run(() => SumCosts(sessionStart, weekStart));
            SessionCost.Text = Usd(session);
            WeekCost.Text = Usd(week);
        }
        catch { }
        finally { _costing = false; }
    }

    static string Usd(double v) => v >= 100 ? $"${v:0}" : string.Create(CultureInfo.InvariantCulture, $"${v:0.00}");

    // Runs on a worker thread. A transcript is parsed again only when it has changed.
    (double Session, double Week) SumCosts(DateTimeOffset sessionStart, DateTimeOffset weekStart)
    {
        double session = 0, week = 0;
        if (!Directory.Exists(ProjectsDir)) return (0, 0);
        var seen = new HashSet<string>();   // a resumed conversation repeats messages in a new file
        foreach (var file in Directory.EnumerateFiles(ProjectsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < weekStart.UtcDateTime) continue;
            if (!_transcripts.TryGetValue(file, out var t) || t.Written != info.LastWriteTimeUtc || t.Length != info.Length)
                _transcripts[file] = t = (info.LastWriteTimeUtc, info.Length, ReadTranscript(file));
            foreach (var u in t.Items)
            {
                if (u.At < weekStart || !seen.Add(u.Key)) continue;
                week += u.Usd;
                if (u.At >= sessionStart) session += u.Usd;
            }
        }
        return (session, week);
    }

    static List<Usage> ReadTranscript(string file)
    {
        var items = new List<Usage>();
        try
        {
            // Claude Code may be writing to it: share access, and skip a half-written last line
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"usage\"") || !line.Contains("\"assistant\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (ReadUsage(doc.RootElement) is { } u) items.Add(u);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return items;
    }

    // One API response. Streaming writes it as several lines with the same message id and usage.
    static Usage? ReadUsage(JsonElement e)
    {
        if (!e.TryGetProperty("type", out var type) || type.GetString() != "assistant"
            || !e.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
            || !m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String
            || !m.TryGetProperty("model", out var model) || model.GetString() is not { } id) return null;
        int i = Array.FindIndex(Prices, p => id.StartsWith(p.Model, StringComparison.Ordinal));
        if (i < 0) return null;
        var p = Prices[i];

        static double N(JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        double input = N(u, "input_tokens"), output = N(u, "output_tokens"), read = N(u, "cache_read_input_tokens");
        double write = N(u, "cache_creation_input_tokens"), write1h = 0;
        if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
        {
            write1h = Math.Min(N(cc, "ephemeral_1h_input_tokens"), write);
            write -= write1h;
        }
        double usd = (input * p.In + write * p.In * 1.25 + write1h * p.In * 2 + read * p.Read + output * p.Out) / 1e6;
        // Haiku 5.5 costs 5x for prompts over 100K tokens; fast mode costs 2x
        if (p.Model == "claude-haiku-5-5" && input + write + write1h + read > 100_000) usd *= 5;
        if (u.TryGetProperty("speed", out var sp) && sp.ValueKind == JsonValueKind.String && sp.GetString() == "fast") usd *= 2;

        string key = (m.TryGetProperty("id", out var mid) ? mid.GetString() : null) + ":"
            + (e.TryGetProperty("requestId", out var rid) ? rid.GetString() : null);
        return new Usage(key, DateTimeOffset.Parse(ts.GetString()!, CultureInfo.InvariantCulture), usd);
    }

    // Arc starting at 12 o'clock, clockwise, in a 48x48 box with a 5px stroke
    static Geometry Arc(double degrees)
    {
        const double c = 24, r = 21.5;
        double a = degrees * Math.PI / 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c, c - r), false);
            ctx.ArcTo(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, degrees > 180, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        return g;
    }
}
