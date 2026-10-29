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
            // The one game folder under Documents is the profile: with the folder junction in
            // place this is also the engine's user:// folder, so the launcher, the game and a
            // support answer all name the same place.
            var profile = Option("--profile") ?? LauncherCatalog.ProfileInDocuments(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            // Before anything can be said to the player: the language the game was last set to.
            L.Choose(profile);
            if (Option("--language") is { } language) L.Use(language);
            var root = Option("--root") ?? LauncherCatalog.FindRoot(AppContext.BaseDirectory);
            // --snapshot <png>: draw the window off screen into a picture and quit, for a look at
            // the layout without a desktop (and for the press kit). Content selection is
            // not saved; use --profile for isolated startup logs and package discovery.
            var snapshot = Option("--snapshot");
            // One settings editor per user session prevents two launchers overwriting each other.
            using var mutex = new Mutex(snapshot is null, snapshot is null ? "Local\\DealOrForceLauncher" : null, out var first);
            if (!first) { MessageBox.Show(L.T("The launcher is already open. Switch to its window."), "Deal or Force"); return 0; }
            var app = new Application();
            var window = new MainWindow(new LauncherCatalog(root, profile), Option("--godot"));
            if (snapshot is not null)
            {
                if (Option("--snapshot-size") is { } requestedSize)
                {
                    var dimensions = requestedSize.Split('x', 'X');
                    if (dimensions.Length != 2 || !int.TryParse(dimensions[0], out var width) ||
                        !int.TryParse(dimensions[1], out var height) || width < window.MinWidth || height < window.MinHeight)
                        throw new ArgumentException(L.T("Snapshot size must be at least 980x660."));
                    window.Width = width; window.Height = height;
                }
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
            // An automatic VisualBrush viewbox crops empty margins and can make a
            // snapshot look clipped even when the real window has room for its controls.
            context.DrawRectangle(new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area, Stretch = Stretch.Fill
            }, null, area);
        }
        picture.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
