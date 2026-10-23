
using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// How much of the picture this machine is allowed to pay for.
///
/// A strategy game is played on whatever the player already owns, and that is not always the
/// machine it was designed on: a laptop on its integrated chip, a card that fell off the bus
/// and left Windows drawing through its own software rasteriser, a driver that quietly moved
/// the game to OpenGL. The right answer to that is a cheaper picture at a playable frame rate,
/// not the authored picture at one and a half frames a second - and it has to happen by itself,
/// because a player whose machine is already crawling is not going to go looking for a
/// settings screen.
///
/// The ladder is data (`common/performance`): a mod may add a tier, change what one costs, or
/// name another adapter as "no graphics card here". `order` is the ladder itself - zero is the
/// best picture, and each step down must cost no more than the one above it.
/// </summary>
public sealed class QualityProfile
{
    public string Id = "high";
    public int Order;
    public string Label = "High";
    public string Description = "";

    /// <summary>Share of the window's pixels the map is drawn at.</summary>
    public float RenderScale = 1f;

    /// <summary>
    /// The smoothed raster, which costs a second texture fetch and a noise evaluation per pixel
    /// for a softer coastline. Off is the authored data as a raster, and is cheaper.
    /// </summary>
    public bool MapSmoothing = true;

    public bool SunShadows = true;
    public bool CityLabels = true;
    public bool TradeTraffic = true;
    public bool Weather = true;

    /// <summary>A cap, because a strategy game does not need three hundred frames a second. Zero is no cap.</summary>
    public int MaxFps;

    public QualityProfile Clone() => (QualityProfile)MemberwiseClone();
}

public static class QualityProfiles
{
    private static readonly List<QualityProfile> Loaded = new();
    private static readonly List<string> SoftwareMarkers = new();
    private static bool _loaded;

    public static double SlowFrameMs { get; private set; } = 34.0;
    public static double FastFrameMs { get; private set; } = 11.0;
    public static double StepDownSeconds { get; private set; } = 4.0;
    public static double StepUpSeconds { get; private set; } = 20.0;

    /// <summary>The ladder, best picture first.</summary>
    public static IReadOnlyList<QualityProfile> Ladder
    {
        get { EnsureLoaded(); return Loaded; }
    }

    public static void ResetCatalog()
    {
        Loaded.Clear();
        SoftwareMarkers.Clear();
        _loaded = false;
        EnsureLoaded();
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var document in GameDataLoader.DataDocuments("common", "performance"))
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (root.TryGetProperty("software_markers", out var markers) &&
                    markers.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var marker in markers.EnumerateArray())
                        if (marker.ValueKind == System.Text.Json.JsonValueKind.String &&
                            marker.GetString() is { Length: > 0 } text) SoftwareMarkers.Add(text);
                if (root.TryGetProperty("slow_frame_ms", out var slow) && slow.TryGetDouble(out var slowMs))
                    SlowFrameMs = Math.Clamp(slowMs, 12, 250);
                if (root.TryGetProperty("fast_frame_ms", out var fast) && fast.TryGetDouble(out var fastMs))
                    FastFrameMs = Math.Clamp(fastMs, 2, 60);
                if (root.TryGetProperty("step_down_seconds", out var down) && down.TryGetDouble(out var downSeconds))
                    StepDownSeconds = Math.Clamp(downSeconds, 1, 60);
                if (root.TryGetProperty("step_up_seconds", out var up) && up.TryGetDouble(out var upSeconds))
                    StepUpSeconds = Math.Clamp(upSeconds, 5, 300);
                if (!root.TryGetProperty("tiers", out var tiers) ||
                    tiers.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (var tier in tiers.EnumerateObject())
                {
                    if (tier.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var value = tier.Value;
                    Loaded.Add(new QualityProfile
                    {
                        Id = tier.Name,
                        Order = Int(value, "order", Loaded.Count),
                        Label = Text(value, "label", tier.Name),
                        Description = Text(value, "description", ""),
                        RenderScale = (float)Math.Clamp(Number(value, "render_scale", 1.0), 0.25, 2.0),
                        MapSmoothing = Flag(value, "map_smoothing", true),
                        SunShadows = Flag(value, "sun_shadows", true),
                        CityLabels = Flag(value, "city_labels", true),
                        TradeTraffic = Flag(value, "trade_traffic", true),
                        Weather = Flag(value, "weather", true),
                        MaxFps = Math.Clamp(Int(value, "max_fps", 0), 0, 480)
                    });
                }
            }
        Loaded.Sort((a, b) => a.Order.CompareTo(b.Order));
        if (Loaded.Count == 0)
            Loaded.Add(new QualityProfile { Id = "high", Order = 0, Label = "High" });
    }


    /// <summary>
    /// The tier a machine should start on.
    ///
    /// An explicit choice in the settings wins. "Auto" reads the adapter: a software rasteriser
    /// starts at the bottom, because the difference between eight frames a second and thirty is
    /// not a preference. Everything else starts at the top and lets the governor below find the
    /// level the machine can hold - a guess from a model name is worth less than four seconds of
    /// measured frames.
    /// </summary>
    public static QualityProfile For(string setting, string adapter, string renderingMethod)
    {
        EnsureLoaded();
        var chosen = (setting ?? "").Trim();
        if (chosen.Length > 0 && !chosen.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var named = Loaded.FirstOrDefault(tier => tier.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase));
            if (named is not null) return named.Clone();
        }
        var name = adapter ?? "";
        foreach (var marker in SoftwareMarkers)
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return Loaded[^1].Clone();
        if ((renderingMethod ?? "").Contains("gl_compatibility", StringComparison.OrdinalIgnoreCase))
            return Loaded[Math.Max(0, Loaded.Count - 2)].Clone();
        return Loaded[0].Clone();
    }

    /// <summary>The next cheaper tier, or null when this is already the last one.</summary>
    public static QualityProfile Next(QualityProfile current)
    {
        EnsureLoaded();
        if (current is null) return null;
        foreach (var tier in Loaded)
            if (tier.Order > current.Order) return tier.Clone();
        return null;
    }

    /// <summary>The next better tier, or null when this is already the best picture.</summary>
    public static QualityProfile Previous(QualityProfile current)
    {
        EnsureLoaded();
        if (current is null) return null;
        for (var index = Loaded.Count - 1; index >= 0; index--)
            if (Loaded[index].Order < current.Order) return Loaded[index].Clone();
        return null;
    }

    private static string Text(System.Text.Json.JsonElement value, string name, string fallback) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind == System.Text.Json.JsonValueKind.String ? property.GetString() ?? fallback : fallback;

    private static int Int(System.Text.Json.JsonElement value, string name, int fallback) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : fallback;

    private static double Number(System.Text.Json.JsonElement value, string name, double fallback) =>
        value.TryGetProperty(name, out var property) && property.TryGetDouble(out var number) ? number : fallback;

    private static bool Flag(System.Text.Json.JsonElement value, string name, bool fallback) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
            ? property.GetBoolean() : fallback;
}

/// <summary>
/// Watches the frame the game is actually getting and moves one step down the ladder when it
/// cannot hold thirty frames a second, and one step back up when it has held ninety for long
/// enough that the move is not a guess.
///
/// It samples rather than reacts: a single 300 ms frame is a save, a treaty or a Windows update,
/// not a graphics card that is too slow, and stepping down for it would leave the player on a
/// poorer picture for the rest of the session. The median of a window is used for the same
/// reason. Upward moves are deliberately much slower than downward ones - being wrong about
/// "this machine can do better" is what makes a game oscillate, and the player sees the
/// oscillation, not the frame rate.
/// </summary>
public sealed class FrameRateGovernor
{
    private readonly Queue<double> _window = new();
    private readonly int _windowFrames;
    private double _slowFor;
    private double _fastFor;
    private double _sinceChange;

    public QualityProfile Profile { get; private set; }

    /// <summary>False when the player chose a tier by hand, in which case the governor never moves it.</summary>
    public bool Automatic { get; }

    public FrameRateGovernor(QualityProfile start, bool automatic, int windowFrames = 120)
    {
        Profile = start ?? throw new ArgumentNullException(nameof(start));
        Automatic = automatic;
        _windowFrames = Math.Clamp(windowFrames, 12, 600);
    }

    /// <summary>
    /// One frame. Returns the profile to apply when the level changed, null when it did not.
    ///
    /// A frame four times slower than the threshold is a hitch - a save, a treaty, a Windows
    /// update - and is ignored: one 900 ms frame must not convince the game it is running on a
    /// processor.
    /// </summary>
    public QualityProfile Sample(double frameMilliseconds)
    {
        if (!Automatic || frameMilliseconds <= 0 || double.IsNaN(frameMilliseconds)) return null;
        if (frameMilliseconds > QualityProfiles.SlowFrameMs * 4) return null;
        _sinceChange += frameMilliseconds / 1000.0;
        // Counted in frames rather than seconds: a game that is already slow would otherwise
        // take a minute to collect enough frames to admit it.
        _window.Enqueue(frameMilliseconds);
        while (_window.Count > _windowFrames) _window.Dequeue();
        if (_window.Count < _windowFrames) return null;

        var ordered = _window.ToArray();
        Array.Sort(ordered);
        var median = ordered[ordered.Length / 2];
        var windowSeconds = ordered.Sum() / 1000.0;

        if (median > QualityProfiles.SlowFrameMs)
        {
            _slowFor += windowSeconds;
            _fastFor = 0;
        }
        else if (median < QualityProfiles.FastFrameMs)
        {
            _fastFor += windowSeconds;
            _slowFor = 0;
        }
        else
        {
            _slowFor = 0;
            _fastFor = 0;
        }

        if (_slowFor >= QualityProfiles.StepDownSeconds)
        {
            _slowFor = 0;
            var cheaper = QualityProfiles.Next(Profile);
            if (cheaper is not null) return Adopt(cheaper);
        }
        else if (_fastFor >= QualityProfiles.StepUpSeconds && _sinceChange >= QualityProfiles.StepUpSeconds)
        {
            _fastFor = 0;
            var better = QualityProfiles.Previous(Profile);
            if (better is not null) return Adopt(better);
        }
        return null;
    }

    /// <summary>A setting the player changed by hand: start again from there.</summary>
    public void Reset(QualityProfile profile)
    {
        Profile = profile;
        _window.Clear();
        _slowFor = _fastFor = _sinceChange = 0;
    }

    private QualityProfile Adopt(QualityProfile profile)
    {
        Profile = profile;
        _window.Clear();
        _sinceChange = 0;
        return profile;
    }
}
