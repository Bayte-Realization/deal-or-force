using System.IO;
using System.Windows;

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
            var root = Option("--root") ?? LauncherCatalog.FindRoot(AppContext.BaseDirectory);
            var profile = Option("--profile") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", "Deal or Force");
            // One settings editor per user session prevents two launchers overwriting each other.
            using var mutex = new Mutex(true, "Local\\DealOrForceLauncher", out var first);
            if (!first) { MessageBox.Show("Лаунчер уже открыт. Переключитесь на его окно.", "Deal or Force"); return 0; }
            var app = new Application();
            var window = new MainWindow(new LauncherCatalog(root, profile), Option("--godot"));
            return app.Run(window);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Deal or Force — запуск невозможен", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
    }
}
