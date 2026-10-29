using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DealOrForce.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherCatalog _catalog;
    private bool _dlc;
    private bool _dirty;
    private Process? _game;
    private string? _godot;
    private readonly string _enginePath;
    public MainWindow(LauncherCatalog catalog, string? godot)
    {
        _catalog = catalog;
        _enginePath = Path.Combine(catalog.Profile, "launcher_engine.txt");
        _godot = godot ?? (File.Exists(_enginePath) ? File.ReadAllText(_enginePath).Trim() : Environment.GetEnvironmentVariable("GODOT_NET"));
        InitializeComponent();
        KeyArt.Source = KeyArtOfTheDay();
        VersionChip.Text = DealOrForce.Simulation.GameBuildInfo.VersionLabel;
        ProfileButton.ToolTip = catalog.Profile;
        L.TranslateTree(this);
        LanguageButton.Content = L.LanguageLabel;
        EngineButton.Visibility = catalog.IsProject ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentLabel.Text = catalog.IsProject ? L.T("PROJECT  /  GODOT .NET") : L.T("WINDOWS  /  LOCAL INSTALLATION");
        _catalog.Discover();
        LogDiscovery();
        _dlc = !_catalog.Packages.Any(p => !p.IsDlc) && _catalog.Packages.Any(p => p.IsDlc);
        RefreshView();
        Closing += (_, e) =>
        {
            if (!_dirty) return;
            var result = MessageBox.Show(this, L.T("Save the changes to the game's content?"), Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) e.Cancel = true;
            if (result == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }

    private void RefreshView(string? selected = null)
    {
        if (PackageList == null) return;
        selected ??= (PackageList.SelectedItem as Package)?.Id;
        var visible = _catalog.Packages.Where(p => p.IsDlc == _dlc &&
            (p.Name.Contains(Search.Text, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(Search.Text, StringComparison.OrdinalIgnoreCase))).ToList();
        PackageList.ItemsSource = visible;
        PackageList.SelectedItem = visible.FirstOrDefault(p => p.Id == selected) ?? visible.FirstOrDefault();
        EmptyMessage.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = Search.Text.Length > 0 ? L.T("Nothing found. Try another name or ID.") : _dlc
            ? L.T("No expansions found.\n\nAdd an expansion folder with a dlc.json to dlc, then press \"Refresh\".")
            : L.T("No mods installed yet.\n\nAdd a mod folder with a manifest.json to mods, then press \"Refresh\".\n\n_templates holds examples for authors; they are not active mods.");
        ModsTab.Background = Brush(!_dlc ? "#5E5032" : "#1C2E3A");
        DlcTab.Background = Brush(_dlc ? "#5E5032" : "#1C2E3A");
        ModsTab.BorderBrush = Brush(!_dlc ? "#C9AE6C" : "#4A5A62");
        DlcTab.BorderBrush = Brush(_dlc ? "#C9AE6C" : "#4A5A62");
        DlcCount.Text = _catalog.Packages.Count(p => p.IsDlc && p.Active).ToString();
        ModCount.Text = _catalog.Packages.Count(p => !p.IsDlc && p.Active).ToString();
        var problems = _catalog.Packages.Count(p => p.Kind == "problem");
        Summary.Text = problems > 0 ? L.F("{0} enabled items cannot load: see the list.", problems) : L.T("Ready to launch.");
        Earlier.IsEnabled = Later.IsEnabled = !_dlc && PackageList.SelectedItem != null;
        if (_catalog.SettingsError.Length > 0) Status.Text = L.T("Settings error. Open the profile folder; the backup is the .bak file.");
        else if (_catalog.Warnings.Count > 0) Status.Text = L.F("Discovery warnings: {0}. Details are in the log.", _catalog.Warnings.Count);
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (Details == null) return;
        if (PackageList.SelectedItem is not Package p) { Details.Text = L.T("Choose an item to see its details.\n\nSettings are kept apart from the mod and DLC files."); return; }
        var position = _catalog.EffectiveOrder(p.IsDlc).FindIndex(id => id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
        Details.Text = $"{p.Name}  /  {p.Id}\n" + (p.Description.Length > 0 ? p.Description + "\n" : "") +
            (p.Active ? L.F("Position in its category: {0}. ", position + 1) : "") +
            (p.Dependencies.Length > 0 ? L.F("Dependencies: {0}.\n", string.Join(", ", p.Dependencies)) : L.T("No dependencies.\n")) +
            (!p.Owned ? L.F("The expansion is installed, but the entitlement \"{0}\" is not owned. The launcher does not change entitlements.\n", p.Entitlement) : "") +
            (p.HasCode ? L.T("Contains executable code — only enable mods from sources you trust.\n") : "") +
            (!p.IsDlc && p.SupportedVersion != "*" ? L.F("Declared game version: {0} (compatibility is not checked automatically).\n", p.SupportedVersion) : "") + p.Path;
    }

    /// <summary>
    /// The other language, now - for the launcher, and through the settings file they both read,
    /// for the game it is about to start. The launcher followed the game's language and had no
    /// way to change it, so a player who opened it in the wrong one had to start the game,
    /// find the option there, quit and come back.
    /// </summary>
    private void LanguageClick(object sender, RoutedEventArgs e)
    {
        var next = L.Languages[(Array.IndexOf(L.Languages, L.Language) + 1) % L.Languages.Length];
        L.Use(next);
        var remembered = L.Remember(_catalog.Profile, next);
        L.TranslateTree(this);
        LanguageButton.Content = L.LanguageLabel;
        // The captions the code writes rather than the XAML: written again in the new language.
        EnvironmentLabel.Text = _catalog.IsProject ? L.T("PROJECT  /  GODOT .NET") : L.T("WINDOWS  /  LOCAL INSTALLATION");
        Play.Content = L.T(_game is { HasExited: false } ? "GAME RUNNING" : "PLAY  ▶");
        RefreshView();
        Status.Text = remembered
            ? L.T("Language changed. The game will start in it too.")
            : L.T("The launcher changed language, but the game's settings file could not be written, so the game keeps its own.");
        Log($"Language: {next}; game settings {(remembered ? "updated" : "not written")}.");
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    /// <summary>
    /// One of the game's loading-screen illustrations, a different one each day, so the launcher
    /// opens on the same world the game does. Embedded, because an exported build packs its own
    /// copies where a launcher cannot read them.
    /// </summary>
    private static BitmapImage? KeyArtOfTheDay()
    {
        string[] art = { "loading_war_room", "loading_negotiation_hall", "loading_port_dusk", "loading_winter_front" };
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Art/{art[DateTime.Now.DayOfYear % art.Length]}.jpg");
            image.DecodePixelWidth = 1100;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch (Exception) { return null; }
    }
    private void ModsClick(object sender, RoutedEventArgs e) { _dlc = false; RefreshView(); }
    private void DlcClick(object sender, RoutedEventArgs e) { _dlc = true; RefreshView(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) => RefreshView();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();
    private void ToggleClick(object sender, RoutedEventArgs e)
    {
        var check = (CheckBox)sender;
        var p = (Package)check.Tag;
        if (!p.Owned) return;
        p.Enabled = check.IsChecked == true;
        _dirty = true; _catalog.Resolve();
        Status.Text = L.T("There are unsaved changes. Save them, or press \"Play\".");
        RefreshView(p.Id);
    }
    private void EarlierClick(object sender, RoutedEventArgs e) => Move(-1);
    private void LaterClick(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta)
    {
        if (PackageList.SelectedItem is not Package p || p.IsDlc) return;
        var i = _catalog.Packages.IndexOf(p);
        var next = i + delta;
        if (next < 0 || next >= _catalog.Packages.Count || _catalog.Packages[next].IsDlc) return;
        (_catalog.Packages[i], _catalog.Packages[next]) = (_catalog.Packages[next], _catalog.Packages[i]);
        _dirty = true; _catalog.Resolve(); RefreshView(p.Id);
        Status.Text = L.T("Order changed. Dependencies always load before the mod that needs them.");
    }
    private bool Save()
    {
        try { _catalog.Save(); _dirty = false; Status.Text = L.T("Saved. The changes take effect at the next launch."); Log("Content selection saved."); return true; }
        catch (Exception ex) { Report(ex); return false; }
    }
    private void SaveClick(object sender, RoutedEventArgs e) => Save();
    private void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (_dirty && !Save()) return;
        _catalog.Discover(); LogDiscovery();
        Status.Text = L.T("List refreshed. Settings take effect at the next launch.");
        RefreshView();
    }
    private void OpenPackageFolder(object sender, RoutedEventArgs e) => OpenFolder((PackageList.SelectedItem as Package)?.Path ?? Path.Combine(_catalog.Profile, _dlc ? "dlc" : "mods"));
    private void OpenProfile(object sender, RoutedEventArgs e) => OpenFolder(_catalog.Profile);
    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Report(ex); }
    }
    private void ChooseEngine(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L.T("Choose Godot .NET (mono), not the standard build of Godot"), Filter = "Godot .NET (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _godot = dialog.FileName;
        try { Directory.CreateDirectory(_catalog.Profile); File.WriteAllText(_enginePath, _godot); Status.Text = L.F("Godot chosen: {0}", Path.GetFileName(_godot)); }
        catch (Exception ex) { Report(ex); }
    }
    private async void PlayClick(object sender, RoutedEventArgs e)
    {
        if (_game is { HasExited: false }) return;
        _catalog.Resolve();
        if (_catalog.Packages.Any(p => p.Enabled && p.Owned && !p.Active))
        {
            MessageBox.Show(this, L.T("Some enabled items have missing dependencies or a cycle. Enable the dependencies they need, or turn the problem items off."), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_catalog.IsProject && (string.IsNullOrWhiteSpace(_godot) || !File.Exists(_godot)))
        { ChooseEngine(sender, e); if (string.IsNullOrWhiteSpace(_godot)) return; }
        if (!Save()) return;
        try
        {
            var info = _catalog.LaunchInfo(_godot);
            _game = Process.Start(info) ?? throw new InvalidOperationException(L.T("Could not start the game process."));
            Log($"Started {info.FileName}; PID {_game.Id}; profile {_catalog.SelectionPath}");
            Play.IsEnabled = false; Play.Content = L.T("GAME RUNNING");
            Status.Text = L.T("The game is starting. Wait for the main menu; a second launch is blocked.");
            await _game.WaitForExitAsync();
            var code = _game.ExitCode;
            _game.Dispose(); _game = null;
            Play.IsEnabled = true; Play.Content = L.T("PLAY  ▶");
            Status.Text = code == 0 ? L.T("The game has closed. You can change the content for the next campaign.") : L.F("The game exited with code {0}. Check the game's logs.", code);
            Log("Game exited: " + code);
        }
        catch (Exception ex) { Play.IsEnabled = true; Play.Content = L.T("PLAY  ▶"); Report(ex); }
    }
    private void Report(Exception ex)
    {
        Log(ex.ToString()); Status.Text = ex.Message;
        MessageBox.Show(this, ex.Message, L.T("Deal or Force — error"), MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private void LogDiscovery()
    {
        Log($"Discovered {_catalog.Packages.Count} packages in {_catalog.Root}.");
        foreach (var note in _catalog.DiscoveryNotes) Log(note);
        foreach (var warning in _catalog.Warnings) Log("WARNING " + warning);
    }
    private void Log(string message)
    {
        try
        {
            var folder = Path.Combine(_catalog.Profile, "logs"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "launcher.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
