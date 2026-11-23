
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

    /// <summary>Edge smoothing samples for the 3D map (0, 2 or 4; -1 infers from tier).</summary>
    public int MsaaSamples = -1;

    /// <summary>A cap, because a strategy game does not need three hundred frames a second. Zero is no cap.</summary>
    public int MaxFps;

    public int EffectiveMsaaSamples => MsaaSamples >= 0
        ? MsaaSamples
        : SunShadows ? 4 : (Weather && RenderScale >= 0.99f) ? 2 : 0;

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
        SlowFrameMs = 34.0;
        FastFrameMs = 11.0;
        StepDownSeconds = 4.0;
        StepUpSeconds = 20.0;
        _loaded = true;
        foreach (var document in GameDataLoader.DataDocuments("common", "performance"))
            using (document)
                Merge(document.RootElement);
        if (Loaded.Count == 0)
            Loaded.Add(new QualityProfile { Id = "high", Order = 0, Label = "High" });
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        ResetCatalog();
    }

    public static void Merge(System.Text.Json.JsonElement root, List<string> errors = null)
    {
        EnsureLoaded();
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (root.TryGetProperty("software_markers", out var markers) &&
            markers.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var marker in markers.EnumerateArray())
                if (marker.ValueKind == System.Text.Json.JsonValueKind.String &&
                    marker.GetString() is { Length: > 0 } text && !SoftwareMarkers.Contains(text))
                    SoftwareMarkers.Add(text);
        if (root.TryGetProperty("slow_frame_ms", out var slow) && slow.TryGetDouble(out var slowMs))
            SlowFrameMs = Math.Clamp(slowMs, 12, 250);
        if (root.TryGetProperty("fast_frame_ms", out var fast) && fast.TryGetDouble(out var fastMs))
            FastFrameMs = Math.Clamp(fastMs, 2, 60);
        if (root.TryGetProperty("step_down_seconds", out var down) && down.TryGetDouble(out var downSeconds))
            StepDownSeconds = Math.Clamp(downSeconds, 1, 60);
        if (root.TryGetProperty("step_up_seconds", out var up) && up.TryGetDouble(out var upSeconds))
            StepUpSeconds = Math.Clamp(upSeconds, 5, 300);
        if (root.TryGetProperty("tiers", out var tiers) &&
            tiers.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var tier in tiers.EnumerateObject())
            {
                if (tier.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (GameDataLoader.IsRemoved(tier.Value, $"common/performance:{tier.Name}"))
                {
                    Loaded.RemoveAll(existing => string.Equals(existing.Id, tier.Name, StringComparison.OrdinalIgnoreCase));
                    continue;
                }
                var existingTier = Loaded.Find(p => string.Equals(p.Id, tier.Name, StringComparison.OrdinalIgnoreCase));
                Loaded.RemoveAll(existing => string.Equals(existing.Id, tier.Name, StringComparison.OrdinalIgnoreCase));
                var value = tier.Value;
                Loaded.Add(new QualityProfile
                {
                    Id = tier.Name,
                    Order = Int(value, "order", existingTier?.Order ?? Loaded.Count),
                    Label = Text(value, "label", existingTier?.Label ?? tier.Name),
                    Description = Text(value, "description", existingTier?.Description ?? ""),
                    RenderScale = (float)Math.Clamp(Number(value, "render_scale", existingTier?.RenderScale ?? 1.0), 0.25, 2.0),
                    MapSmoothing = Flag(value, "map_smoothing", existingTier?.MapSmoothing ?? true),
                    SunShadows = Flag(value, "sun_shadows", existingTier?.SunShadows ?? true),
                    CityLabels = Flag(value, "city_labels", existingTier?.CityLabels ?? true),
                    TradeTraffic = Flag(value, "trade_traffic", existingTier?.TradeTraffic ?? true),
                    Weather = Flag(value, "weather", existingTier?.Weather ?? true),
                    MsaaSamples = value.TryGetProperty("msaa", out _) ? Math.Clamp(Int(value, "msaa", -1), -1, 4) : (existingTier?.MsaaSamples ?? -1),
                    MaxFps = Math.Clamp(Int(value, "max_fps", existingTier?.MaxFps ?? 0), 0, 480)
                });
            }
            Loaded.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
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
        // OpenGL/ANGLE is an API choice, not a performance measurement. A working
        // full material must not lose city names, weather or resolution merely for
        // using that backend. Auto adapts to measured frames; a manual tier wins.
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

    /// <summary>
    /// The cheapest tier the automatic governor may step down to from this start: the last one
    /// (the processor tier) only when the machine started there, otherwise the one before it.
    /// </summary>
    public static int AutomaticFloor(QualityProfile start)
    {
        EnsureLoaded();
        if (Loaded.Count == 0 || start is null) return int.MaxValue;
        var last = Loaded[^1].Order;
        if (start.Order >= last) return last;
        return Loaded.Count >= 2 ? Loaded[^2].Order : last;
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
    private readonly Queue<double> _gpuWindow = new();
    private double[] _sortScratch = Array.Empty<double>();
    private double _windowSum;
    private readonly int _windowFrames;
    // The cheapest tier the governor may reach by itself: the processor tier only for a machine
    // that started there. A real graphics card that is merely busy never falls to half the
    // pixels and the flat raster, the picture people called "soap".
    private readonly int _floorOrder;
    private double _slowFor;
    private double _fastFor;
    private double _sinceChange;
    private int _consecutiveExtreme;
    // How long the frame must stay fast before stepping back up: doubled after every step down,
    // so a machine on the edge settles instead of bouncing between two pictures.
    private double _stepUpWait = -1;
    // Seconds of frames seen since the start or since the player changed the setting. The first
    // twenty are loading - shaders compiling, textures going up - and decide nothing.
    private double _warmedFor;
    private const double WarmUpSeconds = 20.0;

    public QualityProfile Profile { get; private set; }

    /// <summary>False when the player chose a tier by hand, in which case the governor never moves it.</summary>
    public bool Automatic { get; }

    // The layers the starting tier shows. The governor trades pixels and shadows for frames; it
    // never takes the clouds, the fog's mist, the ships or the city names off the map by itself.
    private QualityProfile _layers;

    public FrameRateGovernor(QualityProfile start, bool automatic, int windowFrames = 120)
    {
        Profile = start ?? throw new ArgumentNullException(nameof(start));
        _layers = start;
        Automatic = automatic;
        _windowFrames = Math.Clamp(windowFrames, 12, 600);
        _floorOrder = QualityProfiles.AutomaticFloor(start);
    }

    /// <summary>
    /// One frame. Returns the profile to apply when the level changed, null when it did not.
    ///
    /// A frame four times slower than the threshold is a hitch - a save, a treaty, a Windows
    /// update - and is ignored: one 900 ms frame must not convince the game it is running on a
    /// processor.
    /// </summary>
    /// <param name="gpuMilliseconds">
    /// What the graphics card spent drawing the map this frame, or below zero when unknown. A
    /// slow frame with an idle card is a busy processor - the simulation, a save, another program
    /// - and fewer pixels would not make it a millisecond faster; it only blurred the map.
    /// </param>
    public QualityProfile Sample(double frameMilliseconds, double gpuMilliseconds = -1)
    {
        if (!Automatic || frameMilliseconds <= 0 || double.IsNaN(frameMilliseconds)) return null;
        var extremeCap = QualityProfiles.SlowFrameMs * 4.0;
        var sampleMs = frameMilliseconds;
        if (frameMilliseconds > extremeCap)
        {
            // Ignore isolated 1-2 frame hitches (save, GC, OS stall), but count sustained <7.3 FPS
            // slideshows (clamped per frame) so a struggling machine always steps down cleanly.
            _consecutiveExtreme++;
            if (_consecutiveExtreme <= 2) return null;
            sampleMs = extremeCap;
        }
        else
        {
            _consecutiveExtreme = 0;
        }
        var frameSeconds = sampleMs / 1000.0;
        _warmedFor += frameSeconds;
        if (_warmedFor < WarmUpSeconds) return null;
        _sinceChange += frameSeconds;
        // Counted in frames rather than seconds: a game that is already slow would otherwise
        // take a minute to collect enough frames to admit it.
        _window.Enqueue(sampleMs);
        _windowSum += sampleMs;
        while (_window.Count > _windowFrames) _windowSum -= _window.Dequeue();
        if (gpuMilliseconds >= 0 && !double.IsNaN(gpuMilliseconds))
        {
            _gpuWindow.Enqueue(Math.Min(gpuMilliseconds, extremeCap));
            while (_gpuWindow.Count > _windowFrames) _gpuWindow.Dequeue();
        }
        if (_window.Count < _windowFrames) return null;

        if (_sortScratch.Length < _windowFrames) _sortScratch = new double[_windowFrames];
        var windowCount = _window.Count;
        _window.CopyTo(_sortScratch, 0);
        Array.Sort(_sortScratch, 0, windowCount);
        var median = _sortScratch[windowCount / 2];
        var gpuKnown = _gpuWindow.Count >= _windowFrames / 2;
        var gpuMedian = 0.0;
        if (gpuKnown)
        {
            var gpuCount = _gpuWindow.Count;
            _gpuWindow.CopyTo(_sortScratch, 0);
            Array.Sort(_sortScratch, 0, gpuCount);
            gpuMedian = _sortScratch[gpuCount / 2];
        }
        // Only a card that is really busy is the card's fault when GPU timing is reported.
        // When a driver cannot report GPU time (!gpuKnown), fall back to clearly sustained slow
        // frame medians (> 1.25x SlowFrameMs) so the machine can still step down instead of stalling.
        var cardBound = gpuKnown
            ? gpuMedian > QualityProfiles.SlowFrameMs * 0.75
            : median > QualityProfiles.SlowFrameMs * 1.25;
        var cardIdle = gpuKnown && gpuMedian < QualityProfiles.SlowFrameMs * 0.35;
        // Accumulate actual elapsed frame time (frameSeconds), NOT the entire windowSeconds per frame!
        if (median > QualityProfiles.SlowFrameMs && cardBound)
        {
            _slowFor += frameSeconds;
            _fastFor = 0;
        }
        else if (median < QualityProfiles.FastFrameMs || cardIdle && Profile.Order > 0)
        {
            _fastFor += frameSeconds;
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
            if (cheaper is not null && cheaper.Order <= _floorOrder)
            {
                _stepUpWait = Math.Min(300.0, Math.Max(QualityProfiles.StepUpSeconds, _stepUpWait < 0 ? QualityProfiles.StepUpSeconds : _stepUpWait * 2));
                return Adopt(cheaper);
            }
        }
        else if (_fastFor >= Math.Max(QualityProfiles.StepUpSeconds, _stepUpWait) &&
                 _sinceChange >= Math.Max(QualityProfiles.StepUpSeconds, _stepUpWait))
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
        if (profile is not null) _layers = profile;
        _window.Clear();
        _gpuWindow.Clear();
        _windowSum = 0;
        _slowFor = _fastFor = _sinceChange = 0;
        _consecutiveExtreme = 0;
        _warmedFor = 0;
    }

    private QualityProfile Adopt(QualityProfile profile)
    {
        // The step's pixels, shadows, MSAA and frame cap; the starting tier's layers.
        profile = profile.Clone();
        profile.CityLabels = _layers.CityLabels;
        profile.TradeTraffic = _layers.TradeTraffic;
        profile.Weather = _layers.Weather;
        Profile = profile;
        _window.Clear();
        _gpuWindow.Clear();
        _windowSum = 0;
        _sinceChange = 0;
        _consecutiveExtreme = 0;
        return profile;
    }
}
