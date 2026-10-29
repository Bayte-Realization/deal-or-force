#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// One folder for everything the player owns: saves, settings, logs, mods, screenshots.
///
/// Where things were before. Saves moved to `Documents/Deal or Force/save games` a while ago
/// because a save nobody can find is a save nobody can send to support. Everything else stayed
/// where Godot put it: `%APPDATA%/Godot/app_userdata/Deal or Force` on Windows - settings, the
/// engine log, diagnostic channels, installed mods, DLC entitlements, the renderer choice -
/// which is five levels inside a hidden folder, and only half the story, because the saves were
/// somewhere else entirely. A player asking "where is my game?" had two answers and neither one
/// was a folder they would ever open.
///
/// So: one root, named after the publisher and then the game, under Documents, with folders
/// inside it that say what they hold. The engine's own `user://` is pointed at the same root by
/// a directory junction on Windows (see `Tools/game-folders.ps1`), so there is no second half
/// left behind: `user://mods` is `mods/`, `user://logs/godot.log` is `logs/godot.log`.
///
/// This class is Godot-free on purpose. It is the layout and the migration, nothing else, so
/// the bench can create the same folders, a test can prove the migration is idempotent, and a
/// headless run has no reason to reach for an engine API to learn where a file goes.
/// </summary>
public sealed class GameFolders
{
    /// <summary>The publisher folder, so the game is not the only thing in Documents with a name like this.</summary>
    public const string PublisherFolder = "Bayte Realization";

    /// <summary>The game folder, which is also what the launcher and the support answers call it.</summary>
    public const string GameFolder = "Deal or Force";

    /// <summary>What the root holds, in the order the folder should read: what the player made, then what the game made.</summary>
    public const string SavesFolder = "saves";
    public const string SettingsFolder = "settings";
    public const string LogsFolder = "logs";
    public const string ScreenshotsFolder = "screenshots";
    public const string ModsFolder = "mods";
    public const string DlcFolder = "dlc";

    /// <summary>Bug reports the player hands over: zips, one per report.</summary>
    public const string ReportsFolder = "reports";

    private readonly List<string> _moved = new();
    private readonly List<string> _skipped = new();

    private GameFolders(string root) => Root = root;

    /// <summary>The root itself: `…/Documents/Bayte Realization/Deal or Force`.</summary>
    public string Root { get; }

    public string Saves => Path.Combine(Root, SavesFolder);
    public string Settings => Path.Combine(Root, SettingsFolder);
    public string Logs => Path.Combine(Root, LogsFolder);
    public string Screenshots => Path.Combine(Root, ScreenshotsFolder);
    public string Mods => Path.Combine(Root, ModsFolder);
    public string Dlc => Path.Combine(Root, DlcFolder);
    public string Reports => Path.Combine(Root, ReportsFolder);

    /// <summary>The one settings file: `settings/settings.json`, not a second copy in the root.</summary>
    public string SettingsFile => Path.Combine(Settings, "settings.json");

    /// <summary>Files copied out of an older location by the last <see cref="Prepare"/>.</summary>
    public IReadOnlyList<string> Moved => _moved;

    /// <summary>Files that could not be copied, with the reason, so a locked file is reported instead of lost.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>
    /// The layout for a documents folder, or null when the system will not name one - it is
    /// allowed not to, on a redirected or locked-down profile.
    /// </summary>
    public static GameFolders? For(string? documentsFolder)
    {
        if (string.IsNullOrWhiteSpace(documentsFolder)) return null;
        try
        {
            // Godot hands back forward slashes and Path.Combine adds backslashes, so without
            // GetFullPath the same folder would be written two ways and shown to the player as
            // `C:/Users/User/Documents\Bayte Realization\Deal or Force`, which reads like a bug.
            return new GameFolders(Path.GetFullPath(Path.Combine(documentsFolder, PublisherFolder, GameFolder)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Create the folders, write the note that explains them, and pull in everything an older
    /// install left in one of the two old places. Safe to call on every start: a folder that
    /// exists is not touched, a file that exists is never overwritten, and a source that is the
    /// destination - which is what `user://` looks like once the junction is in place - is
    /// skipped instead of copied onto itself.
    /// </summary>
    public bool Prepare(string? legacyUserRoot = null, string? legacyDocumentsRoot = null)
    {
        _moved.Clear();
        _skipped.Clear();
        var ok = Ensure(Root) & Ensure(Saves) & Ensure(Settings) & Ensure(Logs) & Ensure(Screenshots)
                 & Ensure(Mods) & Ensure(Dlc) & Ensure(Reports);
        if (!ok) return false;

        // Before the junction exists, the engine's folder and this one are two different
        // places, and the older of them holds the settings and the logs the player already has.
        if (legacyUserRoot is not null && !Same(legacyUserRoot, Root))
        {
            MigrateFile(Path.Combine(legacyUserRoot, "settings.json"), SettingsFile);
            MigrateFolder(Path.Combine(legacyUserRoot, "save_games"), Saves);
            MigrateFolder(Path.Combine(legacyUserRoot, LogsFolder), Logs);
            MigrateFolder(Path.Combine(legacyUserRoot, ModsFolder), Mods);
            MigrateFolder(Path.Combine(legacyUserRoot, DlcFolder), Dlc);
        }

        // And the folder saves lived in before the publisher folder existed. Its "save games"
        // goes where saves go now; anything else in it came from the same era and is kept
        // beside the root rather than thrown away.
        if (legacyDocumentsRoot is not null && !Same(legacyDocumentsRoot, Root))
        {
            MigrateFolder(Path.Combine(legacyDocumentsRoot, "save games"), Saves);
            MigrateFolder(legacyDocumentsRoot, Root, shallow: true);
            TryRemoveIfEmpty(legacyDocumentsRoot);
        }

        WriteReadme();
        return true;
    }

    /// <summary>
    /// Whether the engine's `user://` already is this root. The junction is what makes the answer
    /// yes, and it is also the difference between one folder and two.
    /// </summary>
    public bool EngineSharesRoot(string userRoot) => Same(userRoot, Root);

    private bool Ensure(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception exception)
        {
            _skipped.Add(folder + " — " + exception.Message);
            return false;
        }
    }

    private static bool Same(string left, string right)
    {
        try
        {
            return string.Equals(Real(left), Real(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A path with every link resolved. `user://` is a junction into this folder now, so the two
    /// spellings name one place; comparing the spellings would say they are different and copy
    /// every log file onto itself on each start - which is what seven hundred lines of "already
    /// present" in the log were.
    /// </summary>
    private static string Real(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) return full;
        var target = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
        return target is null
            ? full
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
    }

    /// <summary>Copy every file of a folder one level deep, never over an existing file.</summary>
    private void MigrateFolder(string from, string to, bool shallow = false)
    {
        if (!Directory.Exists(from) || Same(from, to)) return;
        Ensure(to);
        try
        {
            foreach (var file in Directory.EnumerateFiles(from))
                MigrateFile(file, Path.Combine(to, Path.GetFileName(file)));
            if (shallow) return;
            foreach (var folder in Directory.EnumerateDirectories(from))
            {
                var target = Path.Combine(to, Path.GetFileName(folder));
                Ensure(target);
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(folder, file);
                    var destination = Path.Combine(target, relative);
                    Ensure(Path.GetDirectoryName(destination) ?? target);
                    MigrateFile(file, destination);
                }
                // A source folder emptied by the move goes away, so "one folder" is true on disk
                // and not only by convention.
                foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                             .OrderByDescending(path => path.Length))
                    TryRemoveIfEmpty(directory);
                TryRemoveIfEmpty(folder);
            }
            TryRemoveIfEmpty(from);
        }
        catch (Exception exception)
        {
            _skipped.Add(from + " — " + exception.Message);
        }
    }

    private void MigrateFile(string from, string to)
    {
        if (Same(from, to)) return;
        try
        {
            if (!File.Exists(from)) return;
            // Never overwrite: a settings file already in the new folder is the more recent
            // decision, and a save with the same name is the one the player expects to see.
            if (File.Exists(to))
            {
                _skipped.Add(to + " — already present");
                return;
            }
            Ensure(Path.GetDirectoryName(to) ?? Root);
            File.Copy(from, to);
            _moved.Add(to);
            // A move, not a copy: the whole point of this exercise is one folder, and a second
            // copy of every save left behind is the mess it was meant to end. A file the engine
            // still has open simply stays where it is, and the folder tool finishes it.
            try { File.Delete(from); }
            catch (Exception) { /* locked by a running engine; the copy is already safe */ }
        }
        catch (Exception exception)
        {
            _skipped.Add(from + " — " + exception.Message);
        }
    }

    private void TryRemoveIfEmpty(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            if (entries.MoveNext()) return;
            Directory.Delete(folder);
        }
        catch (Exception)
        {
            // A folder somebody else still has open is not worth a support ticket; it simply
            // stays until the next start.
        }
    }

    /// <summary>
    /// The note in the root, in both languages the game ships. A folder that explains itself is
    /// the difference between a support answer and a support question.
    /// </summary>
    private void WriteReadme()
    {
        const string text =
            "Deal or Force — файлы игры / game files\n" +
            "=======================================\n\n" +
            "Всё, что игра пишет на диск, лежит здесь.\n" +
            "Everything the game writes to disk is in this folder.\n\n" +
            "  saves/       — сохранения кампаний, по файлу на сохранение\n" +
            "                 campaign saves, one file each\n" +
            "  settings/    — settings.json: язык, графика, где стоят панели\n" +
            "                 settings.json: language, graphics, panel placement\n" +
            "  logs/        — godot.log и каналы диагностики\n" +
            "                 godot.log and the diagnostic channels\n" +
            "  screenshots/ — снимки экрана из игры\n" +
            "                 screenshots taken in game\n" +
            "  mods/        — установленные моды, по папке на мод\n" +
            "                 installed mods, one folder each\n" +
            "  dlc/         — дополнения и права на них\n" +
            "                 purchased content and its entitlements\n\n" +
            "Папку можно целиком скопировать на другую машину: кампании, настройки и моды\n" +
            "переедут вместе с ней.\n" +
            "The folder can be copied to another machine as a whole: campaigns, settings and mods\n" +
            "travel with it.\n\n" +
            "Не удаляйте папку во время игры. / Do not delete this folder while the game is running.\n";
        try
        {
            var path = Path.Combine(Root, "README.txt");
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            File.WriteAllText(path, text);
        }
        catch (Exception exception)
        {
            _skipped.Add("README.txt — " + exception.Message);
        }
    }
}
