# CLAUDE.md: notes for working on this repo

User-facing docs are in `README.md`. This file covers how the code works, what was learned the hard
way, and how to verify changes.

## What it is

A WPF (.NET 10, `net10.0-windows`) always-on-top widget showing Claude usage limits. It is one
window with all logic in `MainWindow.xaml(.cs)`. Keep it that way unless it grows a lot. The user
asked for **simple, not over-engineered**.

## Build / run / ship

```powershell
taskkill /IM ClaudeQuota.exe /F          # a running copy locks the output exe
dotnet build -c Release                  # quick compile check
.\build-installer.ps1 -Version x.y.z     # self-contained publish + MSI -> dist\ClaudeQuota-Setup.msi
```

- The installed copy lives in `%LocalAppData%\Programs\Claude Quota\` and autostarts via
  `HKCU\...\Run`. When testing a dev build, kill the installed instance first, or you'll be looking at
  the wrong one.
- Reinstall test: `msiexec /i dist\ClaudeQuota-Setup.msi /qn /l*v %TEMP%\cq.log`, then uninstall with
  `/x`. Both were verified to work, including closing the running app and launching after install.
  The self-contained exe takes a few seconds to start the first time.
- Bump the version for every MSI. `MajorUpgrade` only upgrades when the version is higher.

## Architecture (MainWindow.xaml.cs)

- **Fixed transparent canvas.** The window is a fixed `WinW×WinH` (400×360 DIP)
  `AllowsTransparency` window. Only the `Pill` border is painted, so fully transparent pixels are
  click-through. Expanding animates the pill's `Width`/`Height` (200 ms cubic ease-out) inside the
  window. The window itself never resizes during animation.
- **Docking.**
  - State is `(_monitor device name, _edge, _frac 0..1 along edge)`, persisted to
    `%AppData%\ClaudeQuota\dock.txt` as `\\.\DISPLAY2|Left|0.42`.
  - `Snap()` runs after `DragMove()`. It takes the collapsed pill rect in physical px, calls
    `MonitorFromRect`, clamps into that monitor's work area, picks the nearest edge, then calls
    `Dock()`.
  - `Dock()` sets the pill's alignment so it expands away from the edge and toward the screen
    centre, sets corner radii and border, then calls `SetWindowPos` in **physical pixels**.
- **Orientation.** `Vertical` is true for the Left/Right edges.
  - `CollapsedW/H` and `ExpandedW/H` switch between horizontal (280×36 → 360×140) and vertical
    (40×150 → 150×300).
  - `LayoutDetails()` rearranges the same three detail panels (`DSession`, `DWeek`, `DExtra`) from
    columns to rows. Nothing is duplicated.
  - The vertical collapsed header `VHeader` mirrors the square fills via bindings to `FillSession`
    and the other fills.
- **Bottom edge.** The header row and details rows swap so the header stays next to the edge.
- **Clip.** `UpdateClip()` extends the clip rect past the docked edge, so only the outer corners are
  rounded.
- **Data.** `RefreshAsync()` runs every 60 s and reads the `claudeAiOauth.accessToken` field
  from `~/.claude/.credentials.json`. It sends `GET /api/oauth/usage` with
  `anthropic-beta: oauth-2025-04-20`.
  - Fields used: `five_hour`, `seven_day` (`utilization` = % used, `resets_at`), and `extra_usage`
    (`used_credits` in minor units with `decimal_places`, `monthly_limit` may be null).
  - Other fields in the response (`seven_day_opus`, etc.) are null on Pro and ignored.
- **Tool window.** `WS_EX_TOOLWINDOW` hides it from Alt+Tab. A 5 s timer toggles `Topmost` to stay
  above other topmost windows.

## Gotchas learned (do not regress these)

- **The network stalls on IPv6.** On the user's network the first .NET connection stalls about 21 s
  trying IPv6. The `HttpClient` uses a `SocketsHttpHandler.ConnectCallback` that tries IPv4 first.
  Keep it.
- **The locale is Norwegian.** It uses a decimal comma. Always use `CultureInfo.InvariantCulture`
  when writing or parsing persisted numbers. A bug came from `"973,6,20"`.
- **Mixed-DPI multi-monitor.** The user has a primary 1920×1080 at 125% and a second 5120×1440 at
  100%, placed at physical (1920,-886).
  - The app is **PerMonitorV2** (`app.manifest`). Do all placement maths in physical px via Win32
    (`GetMonitorInfo`, `GetDpiForMonitor`, `GetWindowRect`, `SetWindowPos`), not WPF
    `Left`/`Top`/`SystemParameters`.
  - Moving to a monitor with different DPI makes WPF apply the suggested rect, which also changes
    `Width`/`Height`. `Dock()` resets `Width/Height = WinW/WinH`. `OnDpiChanged` re-docks unless
    `_dragging` is set.
- **`Mouse.GetPosition` is stale after `DragMove()`.** It returns (0,0). Don't use cursor
  coordinates to pick the edge. Use the pill rect.
- **Don't animate the window's position or size through WPF `Left` and `Width` together.** They
  apply as separate, deferred moves and fight each other, which makes the window drift or fly away.
  This is why the window has a fixed size and only the inner pill animates.
- **WiX v5 (not v6).** v6 requires accepting the OSMF EULA. Per-user MSI notes:
  - Install to `LocalAppDataFolder\Programs`. `ProgramFiles6432Folder` would need admin.
  - Use HKCU registry key paths and `RemoveFolder`.
  - A component with a file plus a registry key path needs an explicit `Guid`.
  - ICE91 is suppressed because it is expected for per-user installs.
- **No git repo.** There is no history to revert to. Before a large redesign, consider asking the user
  or keeping a copy, because the user has asked to "return to how it was" before.

## Verifying UI changes (how this was tested)

- Build, launch `dist`/`publish` exe, then take screenshots from PowerShell and view them with the
  Read tool.
  - Call `SetProcessDpiAwarenessContext(-4)` (per-monitor v2) in the PowerShell process first, so
    screen coordinates are physical px on every monitor.
  - Use `System.Drawing.Graphics.CopyFromScreen`.
- Hover and drag tests can use `Cursor.Position` plus `mouse_event` (move=1, down=2, up=4). Glide in
  small steps, because WPF needs real `WM_MOUSEMOVE` events for `MouseEnter`/`MouseLeave`.
- **The user is often at the machine.** Synthetic mouse moves and drags hijack their cursor and
  can fight their own actions. Keep such tests short. Prefer screenshots and reading `dock.txt` or the
  window rect via `GetWindowRect`. Stop if state changes unexpectedly, because the user may be
  interacting.
- Bash heredocs with `'EOF'` plus Python strings containing `\\` or `'` have broken several times.
  For non-trivial file edits, write a Python script to the scratchpad and run it, or use the Edit
  tool.

## User preferences

- Communicates in Russian (sometimes English). Docs and code comments are in English.
- Wants simple, practical solutions. Design direction so far: Apple-like, a translucent "soap
  bubble" look, a notch-style pill docked to screen edges.
