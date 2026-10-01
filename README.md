# Claude Quota

A tiny always-on-top widget for **Windows and macOS** that shows how much of your Claude usage is
left: the 5-hour session limit, the weekly limit, and extra-usage credits.

It sits as a translucent "soap bubble" pill docked to a screen edge. Hover it to see details.

## Features

- **Always on top.** Stays above other windows. It is hidden from the taskbar and Alt+Tab on
  Windows, and from the Dock on macOS.
- **Collapsed pill.** Shows session % · week % and four status squares:

  | Square | Meaning |
  |---|---|
  | Orange | Session (5h) left. Fills from the bottom. |
  | Purple | Week (7d) left |
  | Red | Extra-usage spend vs. monthly cap. Stays empty when no cap is set. |
  | Blue | Lit when the last update succeeded, dark on error |

- **Hover to expand.** Shows Session/Week rings with reset countdowns, extra-usage spend and the
  last update time.
- **Drag to any edge.** The widget snaps to the nearest edge of the screen you drop it on.
  - Top/bottom edges: a horizontal pill (280×36).
  - Left/right edges: a vertical strip (40×150) that expands into a tall panel along the edge.
- **Multi-monitor and mixed DPI.** It works on any screen and stays crisp at any scaling. The
  screen, edge and position are remembered. If that screen is gone, it moves to the primary one.
- **Auto refresh.** Updates every 60 seconds. Right-click for **Refresh now**,
  **Start with Windows** / **Start at login**, and **Exit**.
- **Click-through.** The window is only as big as the pill, so it never blocks clicks around it.

## Requirements

- **Claude Code installed and signed in** with a Claude subscription. The widget reads the OAuth
  token that Claude Code stores. It never writes the token or sends it anywhere except to
  Anthropic's usage endpoint.
  - Windows: `%USERPROFILE%\.claude\.credentials.json`
  - macOS: the login Keychain, item `Claude Code-credentials`. It falls back to
    `~/.claude/.credentials.json`.
- Windows 10/11 x64, or macOS on Apple Silicon (`arm64`) or Intel (`x64`).

## Install on Windows

1. Run `dist\ClaudeQuota-Setup.msi`. No admin rights are needed.
   - It installs per-user to `%LocalAppData%\Programs\Claude Quota`.
   - It adds a Start-menu shortcut, enables *Start with Windows*, and launches the widget.
2. The installer is unsigned. If SmartScreen warns, choose **More info → Run anyway**.

To uninstall, use **Settings → Apps → Installed apps → Claude Quota → Uninstall**. This closes the
widget and removes the files, the shortcut and the autostart entry.

## Install on macOS

Pick the archive for the Mac's chip. Use  → **About This Mac**: *Apple M…* means `arm64`, *Intel*
means `x64`.

- `dist/ClaudeQuota-macOS-arm64.tar.gz`
- `dist/ClaudeQuota-macOS-x64.tar.gz`

The app is not signed with an Apple developer certificate, so macOS blocks it until you clear the
download flag and sign it locally once. In Terminal:

```bash
cd ~/Downloads
tar -xzf ClaudeQuota-macOS-arm64.tar.gz          # or double-click the archive
xattr -cr "Claude Quota.app"                     # clear the "downloaded from the internet" flag
codesign --force --deep --sign - "Claude Quota.app"   # ad-hoc signature (required on Apple Silicon)
mv "Claude Quota.app" /Applications/
open "/Applications/Claude Quota.app"
```

On the first refresh, macOS asks whether `security` may read the **Claude Code-credentials**
Keychain item. Choose **Always Allow**. If you choose *Allow*, it asks again every minute.

To start it at login, right-click the pill and choose **Start at login**. This writes
`~/Library/LaunchAgents/com.claudequota.plist`. To uninstall, turn that off, quit the widget
(right-click → **Exit**), and delete the app.

## Usage

| Action | How |
|---|---|
| See details | Hover the pill |
| Move | Drag it and release near any edge of any screen |
| Refresh / autostart / quit | Right-click the pill (Control-click on a Mac) |

The position is saved as `screen|edge|fraction` in `dock.txt`:

- Windows: `%AppData%\ClaudeQuota\dock.txt`
- macOS: `~/.config/ClaudeQuota/dock.txt`

Delete that file to reset to the top-right corner of the primary screen.

## Build from source

The app is [Avalonia](https://avaloniaui.net/) on **.NET 10**. It needs the .NET 10 SDK. NuGet
restores Avalonia and WiX automatically. Both builds below run on Windows.

```powershell
# Run locally
dotnet run -c Release

# Windows installer -> dist\ClaudeQuota-Setup.msi
.\build-installer.ps1 -Version 2.0.2

# macOS apps -> dist\ClaudeQuota-macOS-arm64.tar.gz and -x64.tar.gz
.\build-mac.ps1 -Version 2.0.2
```

Bump `-Version` for every release. MSI upgrades only replace an installed copy when the version is
higher.

- `build-installer.ps1`:
  1. Publishes a self-contained, compressed single-file `ClaudeQuota.exe` to `publish\`, so the
     target PC needs no .NET runtime.
  2. Packages it with WiX (`installer\`) into `dist\ClaudeQuota-Setup.msi`.
- `build-mac.ps1`:
  1. Publishes self-contained single-file binaries for `osx-arm64` and `osx-x64`.
  2. Wraps each one in `Claude Quota.app` with `LSUIElement`, so it has no Dock icon.
  3. Writes a `.tar.gz`. A zip made on Windows would lose the executable bit.
- On a Mac you can also just run `dotnet run -c Release` in the repo.

`installer\make-icon.ps1` regenerates `app.ico` and `app.icns`. The Windows build runs it only if
the icon is missing.

## Project layout

| Path | Purpose |
|---|---|
| `MainWindow.axaml` | All UI: pill, bubble effect, headers, details, context menu |
| `MainWindow.axaml.cs` | Docking, multi-monitor, hover/expand, usage fetching, per-OS bits |
| `App.axaml(.cs)`, `Program.cs` | Avalonia entry point |
| `app.manifest` | Windows: per-monitor-v2 DPI awareness |
| `app.ico`, `app.icns` | App icons (generated by `installer\make-icon.ps1`) |
| `Quota.csproj` | Avalonia project, `net10.0` |
| `installer\Package.wxs` | WiX package: files, shortcut, autostart, close-on-upgrade, launch-after-install |
| `installer\ClaudeQuota.Installer.wixproj` | WiX SDK project |
| `build-installer.ps1` | Windows publish + installer build |
| `build-mac.ps1` | macOS publish + `.app` bundles |
| `backup-wpf\` | The previous Windows-only WPF version (v1), kept for reference. Not built. |

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Login expired - run Claude Code once." | Open Claude Code so it refreshes its token. The widget re-reads it every minute. |
| "Not signed in - run Claude Code once." | Claude Code's login wasn't found. Sign in to Claude Code. On a Mac, check that the Keychain prompt wasn't denied. |
| Shows `…` / "Loading…" for a long time | Usually a network problem. The status line in the expanded view shows the error. |
| macOS: "is damaged and can't be opened" or "cannot be opened" | Run the `xattr` and `codesign` commands from *Install on macOS*. |
| Widget is off-screen or on the wrong screen | Delete `dock.txt` (see *Usage*) and restart. |
| Text hard to read over busy backgrounds | Increase the tint in `MainWindow.axaml` (`Pill` → `Background="#50000000"`). |

## Note on the data source

Usage comes from `GET https://api.anthropic.com/api/oauth/usage`, the endpoint Claude Code itself
uses. It is undocumented and may change without notice.
