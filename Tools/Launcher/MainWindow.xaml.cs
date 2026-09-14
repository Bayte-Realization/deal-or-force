using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        EngineButton.Visibility = catalog.IsProject ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentLabel.Text = catalog.IsProject ? "ПРОЕКТ  /  GODOT .NET" : "WINDOWS  /  ЛОКАЛЬНАЯ УСТАНОВКА";
        _catalog.Discover();
        LogDiscovery();
        _dlc = !_catalog.Packages.Any(p => !p.IsDlc) && _catalog.Packages.Any(p => p.IsDlc);
        RefreshView();
        Closing += (_, e) =>
        {
            if (!_dirty) return;
            var result = MessageBox.Show(this, "Сохранить изменения состава игры?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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
        EmptyMessage.Text = Search.Text.Length > 0 ? "Ничего не найдено. Попробуйте другое название или ID." : _dlc
            ? "Дополнения не найдены.\n\nДобавьте папку дополнения с dlc.json в dlc и нажмите «Обновить»."
            : "Моды пока не установлены.\n\nДобавьте папку мода с manifest.json в mods и нажмите «Обновить».\n\n_templates — примеры для авторов, не активные моды.";
        ModsTab.Background = Brush(!_dlc ? "#665632" : "#213340");
        DlcTab.Background = Brush(_dlc ? "#665632" : "#213340");
        Summary.Text = $"К ЗАПУСКУ\n{_catalog.Packages.Count(p => p.IsDlc && p.Active)} дополнений  ·  {_catalog.Packages.Count(p => !p.IsDlc && p.Active)} модов";
        Earlier.IsEnabled = Later.IsEnabled = !_dlc && PackageList.SelectedItem != null;
        if (_catalog.SettingsError.Length > 0) Status.Text = "Ошибка настроек. Откройте папку профиля; резервная копия — .bak.";
        else if (_catalog.Warnings.Count > 0) Status.Text = $"Предупреждения обнаружения: {_catalog.Warnings.Count}. Подробности — в журнале.";
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (Details == null) return;
        if (PackageList.SelectedItem is not Package p) { Details.Text = "Выберите элемент для просмотра сведений.\n\nНастройки хранятся отдельно от файлов модов и DLC."; return; }
        var position = _catalog.EffectiveOrder(p.IsDlc).FindIndex(id => id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
        Details.Text = $"{p.Name}  /  {p.Id}\n" + (p.Description.Length > 0 ? p.Description + "\n" : "") +
            (p.Active ? $"Порядок в категории: {position + 1}. " : "") +
            (p.Dependencies.Length > 0 ? "Зависимости: " + string.Join(", ", p.Dependencies) + ".\n" : "Без зависимостей.\n") +
            (!p.Owned ? $"Дополнение установлено, но entitlement «{p.Entitlement}» не получен. Лаунчер не изменяет права доступа.\n" : "") +
            (p.HasCode ? "Содержит исполняемый код — включайте только моды из доверенных источников.\n" : "") +
            (!p.IsDlc && p.SupportedVersion != "*" ? $"Заявленная версия игры: {p.SupportedVersion} (совместимость не проверена автоматически).\n" : "") + p.Path;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
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
        Status.Text = "Есть изменения. Сохраните их или нажмите «Играть».";
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
        Status.Text = "Порядок изменён. Зависимости всегда загружаются перед зависимым модом.";
    }
    private bool Save()
    {
        try { _catalog.Save(); _dirty = false; Status.Text = "Сохранено. Изменения вступят в силу при следующем запуске."; Log("Content selection saved."); return true; }
        catch (Exception ex) { Report(ex); return false; }
    }
    private void SaveClick(object sender, RoutedEventArgs e) => Save();
    private void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (_dirty && !Save()) return;
        _catalog.Discover(); LogDiscovery();
        Status.Text = "Список обновлён. Настройки применяются при следующем запуске.";
        RefreshView();
    }
    private void OpenPackageFolder(object sender, RoutedEventArgs e) => OpenFolder((PackageList.SelectedItem as Package)?.Path ?? Path.Combine(_catalog.Root, _dlc ? "dlc" : "mods"));
    private void OpenProfile(object sender, RoutedEventArgs e) => OpenFolder(_catalog.Profile);
    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Report(ex); }
    }
    private void ChooseEngine(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите Godot .NET (mono), не обычную версию Godot", Filter = "Godot .NET (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _godot = dialog.FileName;
        try { Directory.CreateDirectory(_catalog.Profile); File.WriteAllText(_enginePath, _godot); Status.Text = "Godot выбран: " + Path.GetFileName(_godot); }
        catch (Exception ex) { Report(ex); }
    }
    private async void PlayClick(object sender, RoutedEventArgs e)
    {
        if (_game is { HasExited: false }) return;
        _catalog.Resolve();
        if (_catalog.Packages.Any(p => p.Enabled && p.Owned && !p.Active))
        {
            MessageBox.Show(this, "Некоторые включённые элементы имеют недоступные зависимости или цикл. Включите необходимые зависимости либо выключите проблемные элементы.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_catalog.IsProject && (string.IsNullOrWhiteSpace(_godot) || !File.Exists(_godot)))
        { ChooseEngine(sender, e); if (string.IsNullOrWhiteSpace(_godot)) return; }
        if (!Save()) return;
        try
        {
            var info = _catalog.LaunchInfo(_godot);
            _game = Process.Start(info) ?? throw new InvalidOperationException("Не удалось создать процесс игры.");
            Log($"Started {info.FileName}; PID {_game.Id}; profile {_catalog.SelectionPath}");
            Play.IsEnabled = false; Play.Content = "ИГРА ЗАПУЩЕНА";
            Status.Text = "Игра запускается. Дождитесь главного меню; повторный запуск заблокирован.";
            await _game.WaitForExitAsync();
            var code = _game.ExitCode;
            _game.Dispose(); _game = null;
            Play.IsEnabled = true; Play.Content = "ИГРАТЬ  ▶";
            Status.Text = code == 0 ? "Игра закрыта. Можно изменить состав следующей кампании." : $"Игра завершилась с кодом {code}. Проверьте логи игры.";
            Log("Game exited: " + code);
        }
        catch (Exception ex) { Play.IsEnabled = true; Play.Content = "ИГРАТЬ  ▶"; Report(ex); }
    }
    private void Report(Exception ex)
    {
        Log(ex.ToString()); Status.Text = ex.Message;
        MessageBox.Show(this, ex.Message, "Deal or Force — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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
