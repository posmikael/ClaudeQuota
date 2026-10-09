# CLAUDE.md: notes for working on this repo

User-facing docs are in `README.md`. This file covers how the code works, what was learned the hard
way, and how to verify changes.

## What it is

An Avalonia 12 (.NET 10, `net10.0`) always-on-top widget for **Windows and macOS** that shows Claude
usage limits. It is one window with all logic in `MainWindow.axaml(.cs)`. Keep it that way unless it
grows a lot. Prefer **simple, not over-engineered** solutions.

v1 was Windows-only WPF. Its sources were removed from the repo; they are in the git history before the cleanup commit.

Personal, machine-specific notes (if any) are in `CLAUDE.local.md`, which is not committed.

## Build / run / ship

```powershell
taskkill /IM ClaudeQuota.exe /F          # a running copy locks the output exe
dotnet build -c Release                  # quick compile check
.\build-installer.ps1 -Version x.y.z     # Windows: self-contained single-file publish + MSI -> dist\ClaudeQuota-Setup.msi
.\build-mac.ps1 -Version x.y.z           # macOS: osx-arm64 + osx-x64 .app bundles -> dist\ClaudeQuota-macOS-*.tar.gz
```

- **Windows.**
  - The installed copy lives in `%LocalAppData%\Programs\Claude Quota\` and autostarts via
    `HKCU\...\Run`. When testing a dev build, kill the installed instance first, or you'll be
    looking at the wrong one.
  - Reinstall test: `msiexec /i dist\ClaudeQuota-Setup.msi /qn /l*v %TEMP%\cq.log`, then uninstall
    with `/x`.
  - Bump the version for every MSI. `MajorUpgrade` only upgrades when the version is higher.
  - Add an entry to `CHANGELOG.md` (user-facing wording) for every new version.
- **macOS.** `build-mac.ps1` cross-builds from Windows.
  - Each archive contains `Claude Quota.app/Contents/{Info.plist, MacOS/ClaudeQuota, Resources/app.icns}`.
    `Info.plist` sets `LSUIElement` (no Dock icon).
  - The binary is single-file with native dylibs self-extracted, so there is exactly one Mach-O to
    sign.
  - It is a `.tar.gz` written with `System.Formats.Tar`, because a zip from Windows loses the
    executable bit.
  - The binary is unsigned (the SDK only signs when building on a Mac). Users must run `xattr -cr`
    plus `codesign --force --deep -s -` once (see README). Apple Silicon refuses unsigned arm64 code.
- **The macOS path has never been run on a real Mac.** Everything below marked (mac) is written from
  API knowledge. Verify on a Mac before trusting it.

## Architecture (MainWindow.axaml.cs)

- **Window = pill envelope.** Avalonia's transparent window on Windows is a DirectComposition
  window (`WS_EX_NOREDIRECTIONBITMAP`). Unlike WPF's layered window, its transparent pixels are
  **not** click-through.
  - So the window is sized to the collapsed pill, or to the expanded size while hovered.
  - `Place(open)` sizes and positions it, anchored at the corner the pill grows from.
  - The pill animates `Width`/`Height` inside (XAML `DoubleTransition`, 200 ms cubic ease-out).
  - On collapse, the window shrinks 220 ms later, after the animation.
  - **On Windows the window is never resized on hover.** It always has the expanded size, and
    `SetWindowRgn` trims it (drawing and clicks) to the collapsed pill or the full window. Resizing
    plus moving a DirectComposition window shows one stale frame with the old content at the new
    origin, so the pill blinked sideways on every hover. A region change has no such frame.
  - On Windows, `Place` also calls `SetWindowPos` with position and size together, so the move and
    resize (on docking) can't show as two steps.
- **Units.**
  - Screen geometry (`Screen.WorkingArea`) and window `Position` are in "desktop units": physical px
    on Windows, points on macOS (mac).
  - Sizes are DIPs. `Units(screen) = screen.Scaling * DesktopScaling / RenderScaling` converts
    between them on both OSes.
- **Docking.**
  - State is `(_monitor key, _edge, _frac 0..1 along edge)`, persisted as `dock.txt` in
    `SpecialFolder.ApplicationData/ClaudeQuota` (`%AppData%` / `~/.config`).
  - The key is `DisplayName@X,Y`. On Windows `DisplayName` is empty, so the key is effectively the
    screen origin, e.g. `@1920,-886|Top|0.42`.
  - `Snap()` runs on drag release. It takes the collapsed pill rect, finds the screen
    (`ScreenFromBounds`), clamps into its work area, picks the nearest edge, then calls `Dock()`.
  - `Dock()` sets the pill's alignment so it expands away from the edge and toward the screen
    centre, sets corner radii and border, swaps rows for the bottom edge, then calls `Place()`.
- **Drag.** Manual drag: capture the pointer, then `Position = start + (pointerOnScreen - pressPoint)`.
  This is one code path that ends in a known `PointerReleased` on both OSes. `BeginMoveDrag` hands
  the drag to the OS, and how the drag ends differs by platform.
- **Hover.** A 50 ms `DispatcherTimer` polls the global cursor and checks it against the pill's
  screen rect (`PointerOverPill`).
  - Windows uses `GetCursorPos`. macOS uses `[NSEvent mouseLocation]` via `objc_msgSend` (mac),
    flipped against the screen at the origin.
  - **Don't switch back to `PointerEntered`/`PointerExited`.** Growing the window under the
    cursor makes Avalonia fire a spurious exit, which loops into expand/collapse flicker.
- **Orientation.** `Vertical` is true for the Left/Right edges.
  - `CollapsedW/H` and `ExpandedW/H` switch between horizontal (280×36 → 360×140) and vertical
    (40×150 → 150×300).
  - `LayoutDetails()` rearranges the same three detail panels (`DSession`, `DWeek`, `DExtra`) from
    columns to rows.
  - The vertical collapsed header `VHeader` mirrors the square fills via `{Binding #FillSession.Height}`.
- **Clip.** The `Pill` border has `ClipToBounds`, which clips to its own (per-edge) corner radii.
- **Shimmer.** `Shimmer()` runs on `RequestAnimationFrame` and sets a new `RotateTransform` on the
  film and rim brushes.
  - Avalonia `Animation` can't target a brush's transform: it throws `InvalidCastException` to
    `Visual`.
- **Data.** `RefreshAsync()` runs every minute (and on a click without a drag). It gets the token via
  `ReadCredentials()` on a worker thread.
  - macOS: `security find-generic-password -s "Claude Code-credentials" -w` (mac), falling back to
    the file.
  - Windows: `~/.claude/.credentials.json`.
  - It sends `GET /api/oauth/usage` with `anthropic-beta: oauth-2025-04-20`.
  - Fields used: `five_hour`, `seven_day` (`utilization` = % used, `resets_at`), and `extra_usage`
    (`used_credits` in minor units with `decimal_places`, `monthly_limit` may be null).
  - `_refreshing` stops overlapping refreshes while a Keychain prompt is open.
  - The endpoint rate-limits: HTTP 429 with `Retry-After` (128 s was seen) and no ratelimit
    headers. On 429 `_retryAt` blocks all requests, timer and clicks, until then. Polling every
    60 s plus Claude Code's own calls with the same token was enough to hit it. v2.0.4 moved to
    5 min for that reason; v2.0.5 went back to 1 min at the owner's request, relying on the backoff.
- **Autostart.** Windows uses the HKCU Run value `ClaudeQuota`. macOS uses
  `~/Library/LaunchAgents/com.claudequota.plist` with `RunAtLoad` (mac).
- **Windows-only bits.**
  - `WS_EX_TOOLWINDOW` via `Win32Properties.AddWindowStylesCallback` hides it from Alt+Tab.
  - A 5 s timer toggles `Topmost` to stay above other topmost windows.
  - Corner rounding is off.

## Gotchas learned (do not regress these)

- **IPv6 can stall.** On some networks the first .NET connection stalls about 21 s trying IPv6. The
  `HttpClient` uses a `SocketsHttpHandler.ConnectCallback` that tries IPv4 first. Keep it.
- **Locales with a decimal comma.** Always use `CultureInfo.InvariantCulture` when writing or
  parsing persisted numbers. A bug came from `"973,6,20"`.
- **Mixed-DPI multi-monitor.** Tested with a 125% primary screen and a 100% second screen placed
  above-right of it (negative Y).
  - Moving to a monitor with different DPI makes Windows apply the suggested rect.
    `ScalingChanged` re-docks unless a drag is in progress (`_pressed`).
- **Windows minimum window height.** Avalonia's default `WM_WINDOWPOSCHANGING` handling forced
  47 px (at 125%) on the 45 px pill, which pushed a bottom-docked pill into the taskbar. A
  `Win32Properties.AddWndProcHookCallback` hook marks `WM_WINDOWPOSCHANGING` handled for resizes.
  `WM_GETMINMAXINFO` was *not* the cause.
- **WiX v5 (not v6).** v6 requires accepting the OSMF EULA. Per-user MSI notes:
  - Install to `LocalAppDataFolder\Programs`.
  - Use HKCU registry key paths and `RemoveFolder`.
  - A component with a file plus a registry key path needs an explicit `Guid`.
  - ICE91 is suppressed.
- **Git.** The repo is `https://github.com/posmikael/ClaudeQuota` (branch `main`). Commit before a
  large redesign, so it is easy to go back.
- **Avalonia 12 renames.** `SystemDecorations` is now `WindowDecorations`.

## Verifying UI changes (how this was tested)

- Build, launch `bin\Release\net10.0\ClaudeQuota.exe` (or `publish\`), then take screenshots from
  PowerShell and view them with the Read tool.
  - Call `SetProcessDpiAwarenessContext(-4)` first, so coordinates are physical px.
  - Use `System.Drawing.Graphics.CopyFromScreen`.
- `Process.MainWindowHandle` is 0 for this tool window. Find the HWND with `EnumWindows` filtered by
  PID, then read `GetWindowRect`.
- Hover and drag tests use `SetCursorPos` plus `mouse_event` (down=2, up=4, right down/up=8/16).
- Synthetic mouse moves take over the real cursor, and a real cursor can trigger hover during a
  test. Keep such tests short and re-check the state.
- PowerShell tool: one `Add-Type` per process. Put multi-step Win32 probes in a script file in the
  scratchpad.

## Conventions

- Docs and code comments are in English.
- Design direction: Apple-like, a translucent "soap bubble" look, a notch-style pill docked to
  screen edges.
