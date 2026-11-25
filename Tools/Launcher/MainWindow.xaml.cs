using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DealOrForce.Simulation;
using Microsoft.Win32;

namespace DealOrForce.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherCatalog _catalog;
    private readonly BuildManifest? _build;
    private readonly StartOptions _options;
    private string _windowMode;
    private bool _dlc;
    private bool _dirty;
    private bool _loading = true;
    private string? _conflictKey;
    private List<ModConflict> _conflicts = new();
    private Process? _game;
    private string? _godot;
    private readonly string _enginePath;

    public MainWindow(LauncherCatalog catalog, string? godot)
    {
        _catalog = catalog;
        _enginePath = Path.Combine(catalog.Profile, "launcher_engine.txt");
        _godot = godot ?? (File.Exists(_enginePath) ? File.ReadAllText(_enginePath).Trim() : Environment.GetEnvironmentVariable("GODOT_NET"));
        _build = catalog.IsProject ? null : BuildManifest.Load(catalog.Root);
        _options = StartOptions.Load(catalog.Profile);
        var mode = GameSettings.Read(catalog.Profile, "window_mode");
        _windowMode = Array.IndexOf(StartOptions.WindowModes, mode) >= 0 ? mode! : StartOptions.WindowModes[0];
        InitializeComponent();
        Backdrop.Source = Picture("menu_world.png", 1920);
        L.TranslateTree(this);
        _catalog.Discover();
        LogDiscovery();
        foreach (var choice in RendererChoices.Children.OfType<RadioButton>()) choice.IsChecked = (string)choice.Tag == _options.Renderer;
        foreach (var choice in WindowChoices.Children.OfType<RadioButton>()) choice.IsChecked = (string)choice.Tag == _windowMode;
        SafeMap.IsChecked = _options.SafeMap;
        _loading = false;
        WriteCodeCaptions();
        NewsTab.IsChecked = true;
        RefreshView();
        Closing += (_, e) =>
        {
            if (!_dirty) return;
            var result = MessageBox.Show(this, L.T("Save the changes to the game's content?"), Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) e.Cancel = true;
            if (result == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }

    /// <summary>Open a tab by name: news, mods, dlc, options, help. For --tab and snapshots.</summary>
    public void ShowTab(string name)
    {
        var tab = name.ToLowerInvariant() switch
        {
            "mods" => ModsTab, "dlc" or "expansions" => DlcTab, "options" => OptionsTab, "help" => HelpTab, _ => NewsTab,
        };
        tab.IsChecked = true;
    }

    /// <summary>Everything the code writes rather than the XAML, in the current language.</summary>
    private void WriteCodeCaptions()
    {
        LanguageButton.Content = L.LanguageLabel;
        VersionLabel.Text = GameBuildInfo.VersionLabel;
        BuildLabel.Text = _build is null
            ? (_catalog.IsProject ? L.T("Running from the project") : L.T("No build information beside the game"))
            : L.F("Build {0} · {1}", _build.BuildId, _build.Engine);
        EnvironmentLabel.Text = _catalog.IsProject ? L.T("PROJECT  /  GODOT .NET") : L.T("WINDOWS  /  LOCAL INSTALLATION");
        Play.Content = L.T(_game is { HasExited: false } ? "Game running" : "Play");
        EngineButton.Visibility = _catalog.IsProject ? Visibility.Visible : Visibility.Collapsed;
        ProfileNote.Text = L.F("Everything the game writes lives in one folder: {0}", _catalog.Profile);
        CheckButton.IsEnabled = _build is { Sha256.Count: > 0 };
        if (CheckResults.Items.Count == 0)
            CheckSummary.Text = _build is { Sha256.Count: > 0 }
                ? L.F("{0} files listed in {1}.", _build.Sha256.Count, BuildManifest.FileName)
                : _catalog.IsProject ? L.T("This is the project, not a delivered build: there is nothing to compare with.")
                : L.F("{0} is missing, so there is nothing to compare with.", BuildManifest.FileName);
        ShowNotes();
        UpdateStartLine();
    }

    private void ShowNotes()
    {
        NewsReleaseChip.Text = L.F("Release {0}", GameBuildInfo.Version);
        NewsLanguageNote.Text = L.T("Release notes · English");
        var path = Changelog.Find(_catalog.Root, L.IsRussian, GameBuildInfo.Version);
        string? text = null;
        try { if (path is not null) text = File.ReadAllText(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        NotesMissing.Visibility = text is null ? Visibility.Visible : Visibility.Collapsed;
        Notes.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null)
            Notes.Document = LauncherNotes.Render(text, Res("TextAccent"), Res("TextBody"), Res("TextMuted"), Res("StateInfo"));
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private void TabChecked(object sender, RoutedEventArgs e)
    {
        if (NewsView is null) return;
        NewsView.Visibility = NewsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ContentView.Visibility = ModsTab.IsChecked == true || DlcTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OptionsView.Visibility = OptionsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HelpView.Visibility = HelpTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (sender == ModsTab || sender == DlcTab) { _dlc = sender == DlcTab; RefreshView(); }
    }

    private void RefreshView(string? selected = null)
    {
        if (PackageList == null || _loading) return;
        selected ??= (PackageList.SelectedItem as Package)?.Id;
        var visible = _catalog.Packages.Where(p => p.IsDlc == _dlc &&
            (p.Name.Contains(Search.Text, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(Search.Text, StringComparison.OrdinalIgnoreCase))).ToList();
        PackageList.ItemsSource = visible;
        PackageList.SelectedItem = visible.FirstOrDefault(p => p.Id == selected) ?? visible.FirstOrDefault();
        EmptyMessage.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = Search.Text.Length > 0 ? L.T("Nothing found. Try another name or ID.") : _dlc
            ? L.T("No expansions found.\n\nAdd an expansion folder with a dlc.json to dlc, then press \"Refresh\".")
            : L.T("No mods installed yet.\n\nAdd a mod folder with a manifest.json to mods, then press \"Refresh\".\n\n_templates holds examples for authors; they are not active mods.");
        DlcCount.Text = _catalog.Packages.Count(p => p.IsDlc && p.Active).ToString();
        ModCount.Text = _catalog.Packages.Count(p => !p.IsDlc && p.Active).ToString();
        var problems = _catalog.Packages.Count(p => p.Kind == "problem");
        Summary.Text = problems > 0 ? L.F("{0} enabled items cannot load: see the list.", problems) : L.T("Ready to launch.");
        Earlier.IsEnabled = Later.IsEnabled = !_dlc && PackageList.SelectedItem != null;
        Earlier.Visibility = Later.Visibility = _dlc ? Visibility.Collapsed : Visibility.Visible;
        if (_catalog.SettingsError.Length > 0) Status.Text = L.T("Settings error. Open the profile folder; the backup is the .bak file.");
        else if (_catalog.Warnings.Count > 0) Status.Text = L.F("Discovery warnings: {0}. Details are in the log.", _catalog.Warnings.Count);
        ShowConflicts();
        UpdateDetails();
        UpdateStartLine();
    }

    /// <summary>Two enabled mods editing the same data file: say which, and which one wins.</summary>
    private void ShowConflicts()
    {
        // Walking every enabled mod's files is the slow part: done again only when the load order changes.
        var active = _catalog.EffectiveOrder(false)
            .Select(id => _catalog.Packages.First(p => !p.IsDlc && p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
        var key = string.Join("\n", active.Select(p => p.Id + "|" + p.Path));
        if (key != _conflictKey)
        {
            _conflictKey = key;
            _conflicts = ModConflicts.Find(active);
            foreach (var conflict in _conflicts) Log($"CONFLICT {conflict.File}: {string.Join(" < ", conflict.Mods)}");
        }
        var conflicts = _conflicts;
        ConflictBox.Visibility = !_dlc && conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (conflicts.Count == 0) return;
        string Name(string id) => _catalog.Packages.FirstOrDefault(p => !p.IsDlc && p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;
        var lines = conflicts.Take(3).Select(c => L.F("{0}: {1} — {2} wins where they disagree.",
            c.File, string.Join(", ", c.Mods.Select(Name)), Name(c.Mods[^1])));
        ConflictText.Text = L.F("Enabled mods change the same files ({0}):", conflicts.Count) + "\n" + string.Join("\n", lines) +
            (conflicts.Count > 3 ? "\n" + L.F("…and {0} more. The full list is in the launcher's log.", conflicts.Count - 3) : "");
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

    /// <summary>One line under Play: how the next start will go.</summary>
    private void UpdateStartLine()
    {
        if (StartLine is null) return;
        var renderer = RendererChoices.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == _options.Renderer);
        var rendererName = renderer?.Content is StackPanel { Children.Count: > 0 } panel && panel.Children[0] is TextBlock t ? t.Text : _options.Renderer;
        var window = WindowChoices.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == _windowMode)?.Content as string ?? _windowMode;
        var mods = _catalog.Packages.Count(p => !p.IsDlc && p.Active);
        var parts = new List<string> { rendererName, window };
        if (_options.SafeMap) parts.Add(L.T("safe map"));
        parts.Add(L.F("mods: {0}", mods));
        StartLine.Text = string.Join("  ·  ", parts);
    }

    /// <summary>
    /// The other language, now - for the launcher, and through the settings file they both read,
    /// for the game it is about to start.
    /// </summary>
    private void LanguageClick(object sender, RoutedEventArgs e)
    {
        var next = L.Languages[(Array.IndexOf(L.Languages, L.Language) + 1) % L.Languages.Length];
        L.Use(next);
        var remembered = L.Remember(_catalog.Profile, next);
        L.TranslateTree(this);
        WriteCodeCaptions();
        RefreshView();
        Status.Text = remembered
            ? L.T("Language changed. The game will start in it too.")
            : L.T("The launcher changed language, but the game's settings file could not be written, so the game keeps its own.");
        Log($"Language: {next}; game settings {(remembered ? "updated" : "not written")}.");
    }

    private void RendererChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _options.Renderer = (string)((RadioButton)sender).Tag;
        SaveOptions(L.T("Renderer chosen for the next start."));
    }

    private void SafeMapClick(object sender, RoutedEventArgs e)
    {
        _options.SafeMap = SafeMap.IsChecked == true;
        SaveOptions(_options.SafeMap ? L.T("The next start uses the safe map.") : L.T("The next start uses the full map."));
    }

    private void WindowChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _windowMode = (string)((RadioButton)sender).Tag;
        Status.Text = GameSettings.Write(_catalog.Profile, "window_mode", _windowMode)
            ? L.T("Window mode saved in the game's settings.")
            : L.T("The game's settings file could not be written; the game keeps its own window mode.");
        UpdateStartLine();
    }

    private void SaveOptions(string done)
    {
        try { _options.Save(_catalog.Profile); Status.Text = done; Log($"Start options: renderer {_options.Renderer}, safe map {_options.SafeMap}."); }
        catch (Exception ex) { Report(ex); }
        UpdateStartLine();
    }

    private async void CheckFilesClick(object sender, RoutedEventArgs e)
    {
        if (_build is null) return;
        CheckButton.IsEnabled = false;
        CheckResults.ItemsSource = null;
        var progress = new Progress<string>(file => CheckSummary.Text = L.F("Checking {0}…", file));
        try
        {
            var root = _catalog.Root;
            var results = await Task.Run(() => _build.Verify(root, progress));
            CheckResults.ItemsSource = results.Select(r => new
            {
                r.File, r.Matches,
                Verdict = r.Matches ? L.T("intact") : r.Missing ? L.T("missing") : L.T("differs"),
            }).ToList();
            var bad = results.Count(r => !r.Matches);
            CheckSummary.Text = bad == 0
                ? L.F("All {0} files are as shipped.", results.Count)
                : L.F("{0} of {1} files differ from the build. Download the game again, or replace those files.", bad, results.Count);
            Log($"File check: {results.Count - bad} intact, {bad} not: " + string.Join(", ", results.Where(r => !r.Matches).Select(r => r.File)));
        }
        catch (Exception ex) { Report(ex); }
        finally { CheckButton.IsEnabled = true; }
    }

    /// <summary>
    /// The game's own report (Scripts/Simulation/SupportReport.cs), written from outside it -
    /// which is when it is needed most: a game that does not reach its menu cannot press its
    /// own "Report a problem".
    /// </summary>
    private async void ReportClick(object sender, RoutedEventArgs e)
    {
        ReportButton.IsEnabled = false;
        Status.Text = L.T("Writing the report…");
        var facts = Facts();
        try
        {
            var zip = await Task.Run(() => SupportReport.Create(facts));
            if (zip.Length == 0) { Status.Text = L.F("The report could not be written: {0}", SupportReport.LastFailure); return; }
            Log("Report written: " + zip);
            Status.Text = L.F("Report written: {0}", Path.GetFileName(zip));
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", zip }, UseShellExecute = false });
        }
        catch (Exception ex) { Report(ex); }
        finally { ReportButton.IsEnabled = true; }
    }

    private SupportReport.Facts Facts()
    {
        var profile = _catalog.Profile;
        var dpi = VisualTreeHelper.GetDpi(this);
        var facts = new SupportReport.Facts
        {
            GameVersion = GameBuildInfo.VersionLabel + (_build is null ? "" : " (" + _build.BuildId + ")"),
            MapDataVersion = _build?.MapDataVersion ?? "",
            Engine = _build?.Engine ?? (_catalog.IsProject ? "Godot .NET (project)" : ""),
            Renderer = _options.Renderer + " (launcher start option)" + (_options.SafeMap ? ", --safe-map" : ""),
            Gpu = GraphicsCards(),
            Cpu = $"{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")} · {Environment.ProcessorCount} threads",
            System = RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture,
            Language = L.Language,
            Screen = $"{SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX:0}x{SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY:0} at {dpi.PixelsPerInchX:0} dpi",
            World = "none: written by the launcher, outside the game",
            SettingsFile = GameSettings.FileIn(profile),
            LogsFolder = Path.Combine(profile, GameFolders.LogsFolder),
            SavesFolder = Path.Combine(profile, GameFolders.SavesFolder),
            ReportsFolder = Path.Combine(profile, GameFolders.ReportsFolder),
        };
        facts.Notes.Add("window mode " + _windowMode);
        facts.Notes.Add("installation " + _catalog.Root);
        var active = _catalog.Packages.Where(p => p.Active).Select(p => (p.IsDlc ? "dlc " : "mod ") + p.Id + " " + p.Version).ToList();
        facts.Notes.Add(active.Count == 0 ? "no mods or expansions enabled" : "enabled: " + string.Join(", ", active));
        return facts;
    }

    /// <summary>The display adapters Windows has drivers for, by the names their drivers give.</summary>
    private static string GraphicsCards()
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is null) return "";
            var names = new List<string>();
            foreach (var key in adapters.GetSubKeyNames().Where(k => k.All(char.IsDigit)))
            {
                using var adapter = adapters.OpenSubKey(key);
                if (adapter?.GetValue("DriverDesc") is string name && !names.Contains(name))
                    names.Add(name + (adapter.GetValue("DriverVersion") is string version ? " (driver " + version + ")" : ""));
            }
            return string.Join("; ", names);
        }
        catch (Exception) { return ""; }
    }

    private static BitmapImage? Picture(string name, int width)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Art/{name}");
            image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch (Exception) { return null; }
    }

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
    private void OpenReports(object sender, RoutedEventArgs e) => OpenFolder(Path.Combine(_catalog.Profile, GameFolders.ReportsFolder));
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
            var info = _catalog.LaunchInfo(_godot, _options);
            _game = Process.Start(info) ?? throw new InvalidOperationException(L.T("Could not start the game process."));
            Log($"Started {info.FileName} {string.Join(' ', info.ArgumentList)}; PID {_game.Id}; profile {_catalog.SelectionPath}");
            Play.IsEnabled = false; Play.Content = L.T("Game running");
            Status.Text = L.T("The game is starting. Wait for the main menu; a second launch is blocked.");
            await _game.WaitForExitAsync();
            var code = _game.ExitCode;
            _game.Dispose(); _game = null;
            Play.IsEnabled = true; Play.Content = L.T("Play");
            Status.Text = code == 0 ? L.T("The game has closed. You can change the content for the next campaign.")
                : L.F("The game exited with code {0}. If it keeps happening, write a report on the Help tab.", code);
            Log("Game exited: " + code);
        }
        catch (Exception ex) { Play.IsEnabled = true; Play.Content = L.T("Play"); Report(ex); }
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
