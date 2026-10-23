using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DealOrForce.Launcher;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        try
        {
            var profile = Option("--profile") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", "Deal or Force");
            // Before anything can be said to the player: the language the game was last set to.
            L.Choose(profile);
            if (Option("--language") is { } language) L.Use(language);
            var root = Option("--root") ?? LauncherCatalog.FindRoot(AppContext.BaseDirectory);
            // --snapshot <png>: draw the window off screen into a picture and quit, for a look at
            // the layout without a desktop (and for the press kit). It saves nothing.
            var snapshot = Option("--snapshot");
            // One settings editor per user session prevents two launchers overwriting each other.
            using var mutex = new Mutex(snapshot is null, snapshot is null ? "Local\\DealOrForceLauncher" : null, out var first);
            if (!first) { MessageBox.Show(L.T("The launcher is already open. Switch to its window."), "Deal or Force"); return 0; }
            var app = new Application();
            var window = new MainWindow(new LauncherCatalog(root, profile), Option("--godot"));
            if (snapshot is not null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -30000; window.Top = 0; window.ShowActivated = false; window.ShowInTaskbar = false;
                window.ContentRendered += (_, _) => { Save(window, snapshot); app.Shutdown(); };
            }
            return app.Run(window);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, L.T("Deal or Force — cannot start"), MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
    }

    private static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var picture = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var area = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            context.DrawRectangle(window.Background, null, area);
            context.DrawRectangle(new VisualBrush(content), null, area);
        }
        picture.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
