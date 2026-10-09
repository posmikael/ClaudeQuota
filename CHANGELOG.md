# Changelog

What changed in each version of Claude Quota. The newest version is at the top.

## 2.0.7 (2026-10-09)

- **Fixes a half-drawn pill after unplugging a monitor.** The widget now settles twice after a
  display change and redraws itself.

## 2.0.6 (2026-10-09)

- **Smoother when screens change.** Plugging or unplugging a monitor, or sharing your screen in
  Teams, no longer makes the pill jump or flicker. It now re-docks once, after the change settles.
- **Stays on top without flicker.** The keep-on-top check no longer toggles the window.

## 2.0.5 (2026-10-02)

- **Refreshes every minute** again. If the server answers "Too Many Requests", the widget still
  waits as long as it asks before trying again.

## 2.0.4 (2026-10-01)

- **Rate limits are handled.** Asking for usage too often gets "Too Many Requests" (HTTP 429)
  from the server. The widget now waits as long as the server asks (about 2 minutes) before trying
  again, and shows `Rate limited · updated HH:mm` instead of `HTTP 429`.
- **Refreshes every 5 minutes** instead of every minute, so it hits that limit less often. Click
  the pill to refresh right away.

## 2.0.3 (2026-10-01)

- **Click to refresh.** A click on the pill (without dragging it) updates the numbers now.
- **Clearer network errors.** A lost connection showed a raw error like
  `… (api.anthropic.com:443)`. It now shows `No connection · updated HH:mm`, so you can see how old
  the numbers are. The full error is in the tooltip.

## 2.0.2 (2026-10-01)

- Detail text is easier to read over light windows: secondary text is lighter, and the pill is
  darker while expanded.

## 2.0.1 (2026-10-01)

- Windows: fixed the pill blinking sideways when you hover over it.

## 2.0.0 (2026-10-01)

- **macOS support.** Rebuilt with Avalonia so the same app runs on Windows and macOS. On macOS it
  reads the Claude Code login from the Keychain and can start at login.
- Drag the pill to any edge of any screen and it snaps there. The position is remembered.

## 1.x

- The first, Windows-only version (WPF).
