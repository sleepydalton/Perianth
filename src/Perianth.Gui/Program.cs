using Avalonia;

namespace Perianth.Gui;

internal static class Program
{
    /// <summary>
    /// Starts the window.
    /// </summary>
    /// <remarks>
    /// The graphical front end is a peer of the command-line tool, not a wrapper
    /// around it: it calls the core and the pipeline directly, so a refusal
    /// arrives as the typed object that carries its kind and its reason rather
    /// than as a line of text to parse back apart.
    /// </remarks>
    [System.STAThread]
    public static int Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Also called by the designer, which needs it to be public.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
