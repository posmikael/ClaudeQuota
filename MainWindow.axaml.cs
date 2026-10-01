using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
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
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string CredPath = Path.Combine(Home, ".claude", ".credentials.json");
    const double SquareSize = 11;
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
        Screens.Changed += (_, _) => Dispatcher.UIThread.Post(() => Dock(_monitor, _edge, _frac));
        // Moving onto a monitor with different scaling makes the OS rescale/reposition the window; re-dock afterwards.
        ScalingChanged += (_, _) => { if (!_pressed) Dispatcher.UIThread.Post(() => Dock(_monitor, _edge, _frac)); };

        // Hover: poll the cursor. Enter/leave events misfire when the window grows under the pointer.
        var hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        hover.Tick += (_, _) => { if (!_pressed && IsVisible && PointerOverPill() != _open) Expand(!_open); };
        hover.Start();
        Pill.PointerPressed += Pill_Pressed;
        Pill.PointerMoved += Pill_Moved;
        Pill.PointerReleased += Pill_Released;

        SessionTrack.Data = WeekTrack.Data = Arc(359.99);
        RequestAnimationFrame(Shimmer);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        // Keep on top even over other topmost windows
        var keep = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        keep.Tick += (_, _) => { Topmost = false; Topmost = true; };
        if (OperatingSystem.IsWindows()) keep.Start();
    }

    // Slowly rotate the film (one turn per 14 s) and, the other way, the rim (per 9 s): the soap-bubble shimmer
    void Shimmer(TimeSpan t)
    {
        double s = t.TotalSeconds;
        if (Film.Fill is Brush film) film.Transform = new RotateTransform(s * 360 / 14 % 360);
        if (Pill.BorderBrush is Brush rim) rim.Transform = new RotateTransform(360 - s * 360 / 9 % 360);
        RequestAnimationFrame(Shimmer);
    }

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

    async Task RefreshAsync()
    {
        if (_refreshing) return;   // e.g. a Keychain prompt is still waiting for an answer
        _refreshing = true;
        try
        {
            using var cred = JsonDocument.Parse(await Task.Run(ReadCredentials));
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
        finally { _refreshing = false; }
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

    void SetStatus(bool ok, string text)
    {
        FillStatus.Height = ok ? SquareSize : 0;
        StatusText.Text = text;
        ToolTip.SetTip(SqStatus, text);
    }

    // Returns percent left, and fills the ring, labels and the matching square.
    static double? ShowLimit(JsonElement root, string key, Avalonia.Controls.Shapes.Path arc, TextBlock pct, TextBlock reset, Border fill)
    {
        if (!root.TryGetProperty(key, out var e) || e.ValueKind != JsonValueKind.Object)
        {
            arc.Data = null; pct.Text = "–"; reset.Text = ""; fill.Height = 0;
            return null;
        }
        double left = Math.Clamp(100 - e.GetProperty("utilization").GetDouble(), 0, 100);
        arc.Data = left < 0.5 ? null : Arc(Math.Min(left, 99.9) * 3.6);
        pct.Text = $"{left:0}%";
        fill.Height = SquareSize * left / 100;
        reset.Text = "";
        if (e.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String)
        {
            var d = DateTimeOffset.Parse(r.GetString()!, CultureInfo.InvariantCulture) - DateTimeOffset.Now;
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
            ExtraText.Text = "Off"; FillExtra.Height = 0; ToolTip.SetTip(SqExtra, "Extra usage: off");
            return;
        }
        int dp = x.TryGetProperty("decimal_places", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 2;
        double scale = Math.Pow(10, dp);
        double used = x.TryGetProperty("used_credits", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() / scale : 0;
        double? limit = x.TryGetProperty("monthly_limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() / scale : null;
        var cur = x.TryGetProperty("currency", out var c) && c.GetString() == "USD" ? "$" : "";
        ExtraText.Text = limit is double lim ? $"{cur}{used:0.00} / {cur}{lim:0.00}" : $"{cur}{used:0.00} used";
        FillExtra.Height = limit is double m && m > 0 ? SquareSize * Math.Clamp(used / m, 0, 1) : 0;
        ToolTip.SetTip(SqExtra, "Extra usage: " + ExtraText.Text);
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
