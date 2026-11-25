using System;
using System.IO;
using DealOrForce.Platform;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Where the game keeps its files, asked once.
///
/// The layout itself lives in <see cref="GameFolders"/>, which knows nothing about Godot so the
/// bench and the tests can use the same rule. This class is the only place that asks the engine
/// two questions - what is the documents folder, and what does `user://` resolve to - and the
/// answer is cached, because a path is not something to recompute four hundred times a session.
///
/// Everything the game writes goes through here now. A screen that wants the save folder asks
/// for it instead of building `Documents/Deal or Force/save games` out of three strings, which
/// is how the game ended up with a save folder in one place and its settings in another.
/// </summary>
public static class GamePaths
{
    private static GameFolders _folders;
    private static bool _resolved;
    private static bool _prepared;

    private static bool IsMobilePlatform =>
        OS.HasFeature("mobile") || OS.HasFeature("android") || OS.HasFeature("ios") ||
        OS.GetName() is "Android" or "iOS";

    /// <summary>Opt-in only for the built-in test runner, never ordinary launches.</summary>
    private static string VerificationDocuments
    {
        get
        {
            if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--verify-build") < 0) return null;
            var path = OS.GetEnvironment("DOF_VERIFICATION_DOCUMENTS");
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
        }
    }

    /// <summary>The engine's own folder, which is where the game lived before this file existed.</summary>
    public static string EngineRoot => ProjectSettings.GlobalizePath("user://");

    private static string ResolveSandboxRoot()
    {
        var root = IsMobilePlatform ? GodotPlatformBridge.ResolveWritableDataRoot() : EngineRoot;
        if (string.IsNullOrWhiteSpace(root)) root = EngineRoot;
        if (string.IsNullOrWhiteSpace(root)) root = OS.GetUserDataDir();
        if (string.IsNullOrWhiteSpace(root)) return ".";
        try { return Path.GetFullPath(root); }
        catch { return root; }
    }

    private static string FallbackRoot => IsMobilePlatform ? ResolveSandboxRoot() : EngineRoot;

    /// <summary>
    /// The one root the player is told about: `Documents/Bayte Realization/Deal or Force`. When
    /// the system will not name a documents folder (or on mobile Scoped Storage) the sandboxed
    /// writable root stands in, because a game that cannot save is worse dressed than a game
    /// saved in a folder nobody finds.
    /// </summary>
    public static GameFolders Folders
    {
        get
        {
            if (_resolved) return _folders;
            _resolved = true;
            var verificationDocuments = VerificationDocuments;
            if (verificationDocuments is null && IsMobilePlatform)
            {
                // Android/iOS always stay inside app-private writable sandbox storage (user://).
                _folders = null;
                return null;
            }
            var documents = verificationDocuments ?? OS.GetSystemDir(OS.SystemDir.Documents);
            _folders = GameFolders.For(documents);
            return _folders;
        }
    }

    public static string Root => Folders?.Root ?? FallbackRoot;

    /// <summary>Whether the one root is a real folder under Documents rather than the engine fallback.</summary>
    public static bool Located => Folders is not null;

    public static string Saves => Folders?.Saves ?? Path.Combine(FallbackRoot, "save_games");
    public static string Settings => Folders?.Settings ?? FallbackRoot;
    public static string SettingsFile => Folders?.SettingsFile ?? Path.Combine(FallbackRoot, "settings.json");
    public static string Logs => Folders?.Logs ?? Path.Combine(FallbackRoot, "logs");
    public static string Screenshots => Folders?.Screenshots ?? Path.Combine(FallbackRoot, "screenshots");
    public static string Mods => Folders?.Mods ?? Path.Combine(FallbackRoot, "mods");
    public static string Dlc => Folders?.Dlc ?? Path.Combine(FallbackRoot, "dlc");
    public static string Reports => Folders?.Reports ?? Path.Combine(FallbackRoot, "reports");

    /// <summary>
    /// Create the folders and take in whatever an older install left in the two old places.
    /// Called from the menu and again from the campaign, because either one can be the first
    /// thing to run: the menu when a player starts there, the campaign when a save is opened
    /// straight from the launcher.
    /// </summary>
    public static void Prepare()
    {
        if (_prepared) return;
        _prepared = true;
        var folders = Folders;
        if (folders is null)
        {
            if (IsMobilePlatform)
            {
                var root = ResolveSandboxRoot();
                foreach (var dir in new[] { root, Saves, Settings, Logs, Screenshots, Mods, Dlc, Reports, Path.Combine(root, "cache") })
                {
                    try { Directory.CreateDirectory(dir); }
                    catch (Exception ex) { GD.PushWarning($"[folders] mkdir failed '{dir}': {ex.Message}"); }
                }
                GD.Print($"[folders] root={root} mobile_sandbox=true logs={Logs}");
                return;
            }
            GD.PushWarning("No documents folder; the game will keep its files in " + EngineRoot);
            return;
        }
        var documents = OS.GetSystemDir(OS.SystemDir.Documents);
        var legacyDocuments = string.IsNullOrWhiteSpace(documents)
            ? null
            : Path.Combine(documents, GameFolders.GameFolder);
        // Explicit verification processes own a disposable profile. Never migrate a
        // player's settings, DLC, mods or saves into (or out of) that test profile.
        if (VerificationDocuments is not null) folders.Prepare();
        else folders.Prepare(EngineRoot, legacyDocuments);

        // Said out loud once, in the log: which folder the player owns, whether the engine's
        // folder is the same one, and how many files came across from the old places. Without
        // this line a support conversation starts by asking the player to hunt for a folder.
        GD.Print($"[folders] root={folders.Root} engine_shares_root={folders.EngineSharesRoot(EngineRoot)} " +
                 $"moved={folders.Moved.Count} skipped={folders.Skipped.Count}");
        foreach (var line in folders.Skipped) GD.Print("[folders] kept in place: " + line);
    }

    /// <summary>A sentence for a tooltip or a settings row: where the game keeps what it keeps.</summary>
    public static string Describe() => Root;
}
