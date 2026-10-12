using Avalonia;

namespace Quota;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // One copy only: a second one (e.g. from the Start menu while autostart already runs it) would double
        // the polling with the same token and hit the rate limit.
        using var single = new Mutex(true, "ClaudeQuota", out bool first);
        if (!first) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer
    static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = false });   // a widget, not a Dock app
}
