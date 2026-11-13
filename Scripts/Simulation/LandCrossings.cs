using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How far, in kilometres, a crossing's authored shore may lie from the nearest ground the
    /// map draws. The raster's cells are seven kilometres across and its coasts are coarser
    /// than that, so a point on the real quay is often a cell or two out to sea.
    /// </summary>
    public float CrossingResolveToleranceKm { get; set; } = 45f;

    /// <summary>
    /// The longest water a crossing may span between its two authored shores. A guard against
    /// a typo joining Calais to Kent, not a rule about which straits qualify.
    /// </summary>
    public float CrossingMaxSpanKm { get; set; } = 75f;

    /// <summary>March pace kept over a bridge or through a tunnel: a defile, one column wide.</summary>
    public float CrossingFixedLinkMarch { get; set; } = 0.80f;

    /// <summary>March pace kept on a causeway: a road with the sea on both sides of it.</summary>
    public float CrossingCausewayMarch { get; set; } = 0.85f;

    /// <summary>March pace kept on a ferry: a division loads, sails and unloads by battalions.</summary>
    public float CrossingFerryMarch { get; set; } = 0.30f;

    /// <summary>Attacker power kept when the assault goes over a bridge, a tunnel or a causeway.</summary>
    public float CrossingFixedLinkAttack { get; set; } = 0.60f;

    /// <summary>Attacker power kept when the assault lands from ferries on a defended shore.</summary>
    public float CrossingFerryAttack { get; set; } = 0.45f;

    /// <summary>How near, in degrees, a fleet at sea has to lie to a crossing to command its water.</summary>
    public float CrossingControlRangeDegrees { get; set; } = 0.9f;

    /// <summary>Enemy presence in the strait below which nobody has closed anything: a patrol boat is not a blockade.</summary>
    public float CrossingBlockingPresence { get; set; } = 6f;

    /// <summary>How many times the friendly presence in the strait the enemy's must be to command it.</summary>
    public float CrossingDominanceRatio { get; set; } = 1.5f;
}

/// <summary>How a strait is crossed, which decides what it costs and whether a navy can shut it.</summary>
public enum LandCrossingKind
{
    /// <summary>A road or rail bridge. Open whoever holds the water under it.</summary>
    Bridge,

    /// <summary>A tunnel under the strait. Open whoever holds the water over it.</summary>
    Tunnel,

    /// <summary>An embankment carrying a road: the cheapest crossing there is.</summary>
    Causeway,

    /// <summary>Boats. Slow, a poor way to arrive at a defended shore, and shut by whoever commands the strait.</summary>
    Ferry
}

/// <summary>
/// One short crossing of salt water as it is written in map_data/crossings: two shores,
/// each a point on the globe, and what joins them.
///
/// The shores are coordinates and never province numbers. Province numbers are an output
/// of the map compiler and move whenever a border is repaired; Tarifa does not.
/// </summary>
public sealed class LandCrossing
{
    public string Id = "";
    /// <summary>The English name, which is also its localisation key.</summary>
    public string Name = "";
    public LandCrossingKind Kind = LandCrossingKind.Ferry;
    /// <summary>False takes the crossing off the map: how a mod removes one it did not write.</summary>
    public bool Open = true;
    /// <summary>
    /// The last file to name a kind named one that does not exist. Such an entry is left out
    /// rather than guessed at: a "viaduct" quietly read as a ferry would be shut by navies
    /// its author never meant it to answer to.
    /// </summary>
    public bool KindUnknown;
    public (float Latitude, float Longitude)? From;
    public (float Latitude, float Longitude)? To;
    /// <summary>The water that commands it, when that is not simply halfway between the shores.</summary>
    public (float Latitude, float Longitude)? Sea;
    /// <summary>March pace kept on this crossing, or negative for its kind's (Defines).</summary>
    public float March = -1f;
    /// <summary>Attacker power kept across this crossing, or negative for its kind's (Defines).</summary>
    public float Attack = -1f;
}

/// <summary>
/// A crossing found on a particular map: the two provinces it joins. Made once when the map
/// is read and never changed afterwards, so every clone of the map shares the same objects.
/// </summary>
public sealed class ResolvedCrossing
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public LandCrossingKind Kind { get; init; }
    public int From { get; init; }
    public int To { get; init; }
    public float March { get; init; } = -1f;
    public float Attack { get; init; } = -1f;
    public float SeaLatitude { get; init; }
    public float SeaLongitude { get; init; }
    /// <summary>Kilometres between the two authored shores.</summary>
    public float SpanKm { get; init; }
    /// <summary>
    /// The two shores as the data file gives them: where the bridge or the ferry actually
    /// lands. The map draws the link between these, not between the centres of the provinces
    /// it joins - a centre can lie a hundred kilometres inland, and a line from one to the
    /// other is mostly a line across dry land.
    /// </summary>
    public float FromShoreLatitude { get; init; }
    public float FromShoreLongitude { get; init; }
    public float ToShoreLatitude { get; init; }
    public float ToShoreLongitude { get; init; }

    /// <summary>Whether a navy that commands the strait stops the crossing, rather than merely threatening it.</summary>
    public bool ClosesUnderBlockade => Kind == LandCrossingKind.Ferry;
}

/// <summary>What the map layer needs to draw one crossing, and nothing it could change.</summary>
public readonly struct CrossingLink
{
    public string Id { get; init; }
    public string Name { get; init; }
    public LandCrossingKind Kind { get; init; }
    public int FromProvince { get; init; }
    public int ToProvince { get; init; }
    public float FromLatitude { get; init; }
    public float FromLongitude { get; init; }
    public float ToLatitude { get; init; }
    public float ToLongitude { get; init; }
    /// <summary>An enemy fleet commands the strait: a ferry does not run, a bridge is under its guns.</summary>
    public bool Contested { get; init; }
    /// <summary>The viewer's formations cannot use it today.</summary>
    public bool Closed { get; init; }
}

public static class LandCrossingCatalog
{
    private static readonly Dictionary<string, LandCrossing> ById = new(StringComparer.OrdinalIgnoreCase);
    private static List<LandCrossing> _ordered = new();
    private static bool _loaded;

    /// <summary>Every authored crossing, in id order, including the ones a file switched off.</summary>
    public static IReadOnlyList<LandCrossing> All
    {
        get
        {
            if (!_loaded) ResetCatalog();
            return _ordered;
        }
    }

    public static LandCrossing Of(string id)
    {
        if (!_loaded) ResetCatalog();
        return id is not null && ById.TryGetValue(id, out var crossing) ? crossing : null;
    }

    /// <summary>Read map_data/crossings again, in file-name order: later files win, key by key.</summary>
    public static void ResetCatalog(List<string> errors = null)
    {
        ById.Clear();
        foreach (var document in GameDataLoader.DataDocuments("map_data", "crossings"))
            using (document)
                MergeInto(document.RootElement, errors);
        _loaded = true;
        Settle();
    }

    /// <summary>Forget every crossing, as if the folder were empty. For a map that has none, and for the tests.</summary>
    public static void Clear()
    {
        ById.Clear();
        _loaded = true;
        Settle();
    }

    /// <summary>Lay one more document over what is loaded, the way the next file in the folder would be.</summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (!_loaded) ResetCatalog();
        MergeInto(root, errors);
        Settle();
    }

    private static void MergeInto(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"crossings/{entry.Name}: a crossing is an object with \"from\", \"to\" and \"kind\".");
                continue;
            }
            if (!ById.TryGetValue(entry.Name, out var crossing))
                ById[entry.Name] = crossing = new LandCrossing { Id = entry.Name, Name = entry.Name };
            var value = entry.Value;
            if (value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                crossing.Name = name.GetString() ?? crossing.Name;
            if (value.TryGetProperty("kind", out var kind))
            {
                crossing.KindUnknown = !(kind.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<LandCrossingKind>(kind.GetString(), true, out var parsed) && Enum.IsDefined(parsed));
                if (!crossing.KindUnknown) crossing.Kind = Enum.Parse<LandCrossingKind>(kind.GetString(), true);
                else errors?.Add($"crossings/{entry.Name}: \"kind\" must be bridge, tunnel, causeway or ferry.");
            }
            if (value.TryGetProperty("open", out var open) && open.ValueKind is JsonValueKind.True or JsonValueKind.False)
                crossing.Open = open.GetBoolean();
            if (value.TryGetProperty("from", out var from))
            {
                if (TryPoint(from, out var point)) crossing.From = point;
                else errors?.Add($"crossings/{entry.Name}: \"from\" must be [latitude, longitude].");
            }
            if (value.TryGetProperty("to", out var to))
            {
                if (TryPoint(to, out var point)) crossing.To = point;
                else errors?.Add($"crossings/{entry.Name}: \"to\" must be [latitude, longitude].");
            }
            if (value.TryGetProperty("sea", out var sea))
            {
                if (TryPoint(sea, out var point)) crossing.Sea = point;
                else errors?.Add($"crossings/{entry.Name}: \"sea\" must be [latitude, longitude].");
            }
            if (value.TryGetProperty("march", out var march))
            {
                if (march.TryGetSingle(out var pace) && pace is > 0f and <= 1f) crossing.March = pace;
                else errors?.Add($"crossings/{entry.Name}: \"march\" is the share of march pace kept, above 0 and at most 1.");
            }
            if (value.TryGetProperty("attack", out var attack))
            {
                if (attack.TryGetSingle(out var kept) && kept is > 0f and <= 1f) crossing.Attack = kept;
                else errors?.Add($"crossings/{entry.Name}: \"attack\" is the share of attacking power kept, above 0 and at most 1.");
            }
        }
    }

    private static bool TryPoint(JsonElement element, out (float Latitude, float Longitude) point)
    {
        point = default;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2) return false;
        if (!element[0].TryGetSingle(out var latitude) || !element[1].TryGetSingle(out var longitude)) return false;
        if (latitude is < -90f or > 90f || longitude is < -180f or > 180f) return false;
        point = (latitude, longitude);
        return true;
    }

    private static void Settle()
    {
        _ordered = new List<LandCrossing>(ById.Values);
        _ordered.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
    }
}

/// <summary>
/// Armies walk across narrow water.
///
/// The map has no sea provinces and its land graph is cut wherever the raster shows a cell
/// of water, so Tarifa and Tangier, Kerch and Taman, Copenhagen and Malmö were as far apart
/// as Lisbon and New York: a division could reach the other shore only by being put on a
/// fleet and landed like an invasion. Every staff in history has treated those crossings as
/// a road with a bad stretch in it. So each one is an edge of the province graph
/// (<see cref="ProvinceTopology.Neighbors"/>), and everything that walks that graph - a
/// march, a supply trace, a front, the planner looking for somebody to attack - crosses it
/// without knowing the word.
///
/// What a crossing costs is here: a slower step (<see cref="MarchMultiplier"/>), a worse
/// assault (<see cref="AssaultMultiplier"/>), and a ferry that stops running while an enemy
/// fleet commands the strait (<see cref="ClosedTo"/>). What it does not do is make two
/// shores one territory: borders, enclaves and peace settlements read
/// <see cref="ProvinceTopology.LandNeighbors"/> and never see it.
///
/// Nothing here is saved. A crossing is map data, and whether one is shut today is worked
/// out from where the fleets are, which the save already holds.
/// </summary>
public static class LandCrossings
{
    private const float EarthRadiusKm = 6371f;

    /// <summary>What the last <see cref="Attach"/> could not place, one line an entry. Empty when the data is sound.</summary>
    public static IReadOnlyList<string> LastErrors { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Find every authored crossing on this map and make it an edge of the map's graph.
    /// Called once, where the map is read (MapRasterCatalog), before any campaign takes it.
    /// An entry that cannot be placed is reported by name and left out; the rest still attach,
    /// because one bad coordinate in a mod must not take the Bosphorus away from everybody.
    /// </summary>
    public static int Attach(ProvinceTopology map, List<string> errors = null)
    {
        if (map is null) return 0;
        var problems = errors ?? new List<string>();
        var before = problems.Count;
        LandCrossingCatalog.ResetCatalog(problems);
        // The tolerances are tunables like any other, and a map is read before there is a
        // campaign to own a Defines - so the shipped and modded values are read here once.
        var defines = new Defines();
        GameDataLoader.ApplyDefines(defines);
        var resolved = Resolve(map, LandCrossingCatalog.All, defines, problems);
        map.AttachCrossings(resolved);
        var found = new List<string>();
        for (var index = before; index < problems.Count; index++)
        {
            found.Add(problems[index]);
            Debug.WriteLine($"[dof] {problems[index]}");
            DiagnosticLogs.Write("errors", $"CROSSING_REJECTED {problems[index]}");
        }
        LastErrors = found;
        return resolved.Count;
    }

    /// <summary>
    /// Place a set of authored crossings on a map without attaching them. Each shore becomes
    /// the land province nearest its point, within the tolerance; an entry is refused, and
    /// says why, when a shore has no ground near it, when both shores come out as one
    /// province, when the two provinces already share a border (the map draws no water
    /// there, so there is nothing to cross), or when the span is longer than any strait.
    /// </summary>
    public static List<ResolvedCrossing> Resolve(ProvinceTopology map, IReadOnlyList<LandCrossing> authored,
        Defines defines, List<string> errors = null)
    {
        var resolved = new List<ResolvedCrossing>();
        if (map is null || authored is null) return resolved;
        defines ??= new Defines();
        var tolerance = MathF.Max(1f, defines.CrossingResolveToleranceKm);
        var taken = new HashSet<long>();
        foreach (var crossing in authored)
        {
            // An unknown kind was reported when the file was read; here it is only left out.
            if (crossing is null || !crossing.Open || crossing.KindUnknown) continue;
            if (crossing.From is not { } from || crossing.To is not { } to)
            {
                errors?.Add($"crossings/{crossing.Id}: needs both \"from\" and \"to\".");
                continue;
            }
            var span = DistanceKm(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
            if (span > defines.CrossingMaxSpanKm)
            {
                errors?.Add($"crossings/{crossing.Id}: its shores are {span:0} km apart, more than CrossingMaxSpanKm " +
                            $"({defines.CrossingMaxSpanKm:0}). Check the coordinates.");
                continue;
            }
            var first = NearestLandProvince(map, from.Latitude, from.Longitude, tolerance);
            var second = NearestLandProvince(map, to.Latitude, to.Longitude, tolerance);
            if (first <= 0 || second <= 0)
            {
                var lost = first <= 0 ? ("from", from) : ("to", to);
                errors?.Add($"crossings/{crossing.Id}: no land within {tolerance:0} km of \"{lost.Item1}\" " +
                            $"[{lost.Item2.Latitude:0.##}, {lost.Item2.Longitude:0.##}].");
                continue;
            }
            if (first == second)
            {
                errors?.Add($"crossings/{crossing.Id}: both shores are province {first}. " +
                            "Move the points further apart, each well onto its own side.");
                continue;
            }
            if (Array.IndexOf(map.LandNeighbors[first], second) >= 0)
            {
                errors?.Add($"crossings/{crossing.Id}: provinces {first} and {second} already share a border - " +
                            "this map draws no water between them, so there is nothing to cross.");
                continue;
            }
            var key = first < second ? ((long)first << 32) | (uint)second : ((long)second << 32) | (uint)first;
            if (!taken.Add(key))
            {
                errors?.Add($"crossings/{crossing.Id}: provinces {first} and {second} are already joined by another crossing.");
                continue;
            }
            var sea = crossing.Sea ?? Midpoint(from, to);
            resolved.Add(new ResolvedCrossing
            {
                Id = crossing.Id,
                Name = crossing.Name,
                Kind = crossing.Kind,
                From = first,
                To = second,
                March = crossing.March,
                Attack = crossing.Attack,
                SeaLatitude = sea.Latitude,
                SeaLongitude = sea.Longitude,
                SpanKm = span,
                FromShoreLatitude = from.Latitude,
                FromShoreLongitude = from.Longitude,
                ToShoreLatitude = to.Latitude,
                ToShoreLongitude = to.Longitude
            });
        }
        return resolved;
    }

    /// <summary>
    /// The land province with ground nearest a point, or 0 when none lies within the reach.
    /// Measured to the raster's cells rather than to province centres: the province a quay
    /// stands in is the one whose shore is nearest, and its centre may be forty kilometres
    /// inland. Ties go to the lower province id, so the same data always joins the same two.
    /// </summary>
    public static int NearestLandProvince(ProvinceTopology map, float latitude, float longitude, float reachKm)
    {
        if (map is null || map.Width <= 0 || map.Height <= 0) return 0;
        var degreesPerRow = 180f / map.Height;
        var degreesPerColumn = 360f / map.Width;
        var row = Math.Clamp((int)((90f - latitude) / degreesPerRow), 0, map.Height - 1);
        var column = (int)MathF.Floor((longitude + 180f) / degreesPerColumn);
        var kmPerRow = EarthRadiusKm * MathF.PI / 180f * degreesPerRow;
        var shrink = MathF.Max(0.05f, MathF.Cos(latitude * MathF.PI / 180f));
        var rows = (int)MathF.Ceiling(reachKm / kmPerRow) + 1;
        var columns = Math.Min(map.Width / 2, (int)MathF.Ceiling(reachKm / (kmPerRow * shrink)) + 1);
        var best = 0;
        var bestDistance = float.MaxValue;
        for (var y = Math.Max(0, row - rows); y <= Math.Min(map.Height - 1, row + rows); y++)
        {
            var cellLatitude = 90f - (y + 0.5f) * degreesPerRow;
            for (var offset = -columns; offset <= columns; offset++)
            {
                var x = ((column + offset) % map.Width + map.Width) % map.Width;
                int province = map.CellToProvince[y * map.Width + x];
                if (province <= 0 || map.IsWater(province)) continue;
                var distance = DistanceKm(latitude, longitude, cellLatitude, -180f + (x + 0.5f) * degreesPerColumn);
                if (distance > reachKm || distance > bestDistance) continue;
                if (distance == bestDistance && province >= best) continue;
                bestDistance = distance;
                best = province;
            }
        }
        return best;
    }

    /// <summary>Great-circle distance in kilometres.</summary>
    public static float DistanceKm(float latitudeA, float longitudeA, float latitudeB, float longitudeB)
    {
        const float radians = MathF.PI / 180f;
        var deltaLatitude = (latitudeB - latitudeA) * radians;
        var deltaLongitude = (longitudeB - longitudeA) * radians;
        var half = MathF.Sin(deltaLatitude * 0.5f);
        var halfLongitude = MathF.Sin(deltaLongitude * 0.5f);
        var a = half * half + MathF.Cos(latitudeA * radians) * MathF.Cos(latitudeB * radians) * halfLongitude * halfLongitude;
        return 2f * EarthRadiusKm * MathF.Asin(MathF.Min(1f, MathF.Sqrt(a)));
    }

    private static (float Latitude, float Longitude) Midpoint((float Latitude, float Longitude) a,
        (float Latitude, float Longitude) b)
    {
        var delta = b.Longitude - a.Longitude;
        while (delta > 180f) delta -= 360f;
        while (delta < -180f) delta += 360f;
        var longitude = a.Longitude + delta * 0.5f;
        while (longitude > 180f) longitude -= 360f;
        while (longitude < -180f) longitude += 360f;
        return ((a.Latitude + b.Latitude) * 0.5f, longitude);
    }

    // --- what a crossing costs -----------------------------------------------------------

    /// <summary>March pace kept on one crossing: its own figure, or its kind's.</summary>
    public static float MarchMultiplier(Defines defines, ResolvedCrossing crossing)
    {
        if (crossing is null) return 1f;
        var kept = crossing.March > 0f ? crossing.March : crossing.Kind switch
        {
            LandCrossingKind.Ferry => defines.CrossingFerryMarch,
            LandCrossingKind.Causeway => defines.CrossingCausewayMarch,
            _ => defines.CrossingFixedLinkMarch
        };
        return Math.Clamp(kept, 0.05f, 1f);
    }

    /// <summary>
    /// March pace kept on the step from one province to the next: one, unless the step is a
    /// strait crossing. The same factor slows a march through friendly ground and an advance
    /// into ground nobody is standing on.
    /// </summary>
    public static float MarchMultiplier(Campaign campaign, int from, int to)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || !map.IsCrossingEnd(from)) return 1f;
        return MarchMultiplier(campaign.Defines, map.CrossingBetween(from, to));
    }

    /// <summary>Attacking power kept by a formation that assaults over one crossing.</summary>
    public static float AttackMultiplier(Defines defines, ResolvedCrossing crossing)
    {
        if (crossing is null) return 1f;
        var kept = crossing.Attack > 0f ? crossing.Attack : crossing.Kind == LandCrossingKind.Ferry
            ? defines.CrossingFerryAttack
            : defines.CrossingFixedLinkAttack;
        return Math.Clamp(kept, 0.05f, 1f);
    }

    /// <summary>
    /// Attacking power kept by a whole assault on a province, some of which may be coming
    /// over a strait and some by land. Weighted by what each formation brings, so a division
    /// attacking Istanbul's Asian shore over the bridges fights at the bridge's price while
    /// the corps beside it that came round by land fights at its own - and the battle is
    /// charged for the mix. Read from where the attackers stand today; nothing is kept.
    /// </summary>
    public static float AssaultMultiplier(Campaign campaign, int battleProvince, IReadOnlyList<Army> attackers)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || attackers is null || !map.IsCrossingEnd(battleProvince)) return 1f;
        var total = 0f;
        var weighted = 0f;
        for (var index = 0; index < attackers.Count; index++)
        {
            var army = attackers[index];
            var weight = MathF.Max(0.0001f, army.Power);
            total += weight;
            weighted += weight * AttackMultiplier(campaign.Defines, map.CrossingBetween(army.ProvinceId, battleProvince));
        }
        return total <= 0f ? 1f : weighted / total;
    }

    /// <summary>
    /// How much an assault over this step should be avoided when there is another way in:
    /// zero by land, and the share of power the crossing costs otherwise. The front planner
    /// adds it to a sector's score the way it adds a river.
    /// </summary>
    public static float AssaultAversion(Campaign campaign, int from, int to)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || !map.IsCrossingEnd(from)) return 0f;
        return 1f - AttackMultiplier(campaign.Defines, map.CrossingBetween(from, to));
    }

    // --- who commands the strait ---------------------------------------------------------

    /// <summary>
    /// Whether a fleet at war with this country commands the water of a crossing: enough
    /// hostile hulls at sea within reach of it, and more of them by the dominance ratio than
    /// this country and its friends can show there. Fleets lying in port command nothing.
    ///
    /// Read from the fleets as they are this step and never remembered: a figure cached for
    /// the day would be one thing on a continuous run and another on a save loaded at noon.
    /// </summary>
    public static bool Contested(Campaign campaign, ResolvedCrossing crossing, int nationId)
    {
        if (campaign is null || crossing is null || campaign.Fleets.Count == 0 || !campaign.FeatureEnabled("navy"))
            return false;
        var defines = campaign.Defines;
        var hostile = 0f;
        var friendly = 0f;
        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.Hulls <= 0 || fleet.InPort) continue;
            if (NavalOperations.Distance(fleet.Latitude, fleet.Longitude, crossing.SeaLatitude, crossing.SeaLongitude) >
                defines.CrossingControlRangeDegrees) continue;
            if (NavalOperations.Friendly(campaign, nationId, fleet.NationId)) friendly += fleet.Presence;
            else if (CampaignDiplomacy.AtWar(campaign, nationId, fleet.NationId)) hostile += fleet.Presence;
        }
        return hostile >= defines.CrossingBlockingPresence && hostile > friendly * defines.CrossingDominanceRatio;
    }

    /// <summary>
    /// Whether this country's formations cannot use a crossing today. Only a ferry closes:
    /// boats do not sail under an enemy squadron's guns, while a bridge or a tunnel stays
    /// passable and is merely <see cref="Contested"/>.
    /// </summary>
    public static bool ClosedTo(Campaign campaign, ResolvedCrossing crossing, int nationId) =>
        crossing is not null && crossing.ClosesUnderBlockade && Contested(campaign, crossing, nationId);

    /// <summary>Whether the step from one province to the next is a ferry this country cannot run today.</summary>
    public static bool StepClosed(Campaign campaign, int nationId, int from, int to)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || !map.IsCrossingEnd(from)) return false;
        var crossing = map.CrossingBetween(from, to);
        return crossing is not null && ClosedTo(campaign, crossing, nationId);
    }

    /// <summary>The closed ferry a marching formation is waiting at, or null when nothing of the kind holds it.</summary>
    public static ResolvedCrossing WaitingAt(Campaign campaign, Army army)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || army is null || army.Route.Count == 0 || !map.IsCrossingEnd(army.ProvinceId)) return null;
        var crossing = map.CrossingBetween(army.ProvinceId, army.Route[0]);
        return crossing is not null && ClosedTo(campaign, crossing, army.NationId) ? crossing : null;
    }

    // --- for the map ---------------------------------------------------------------------

    /// <summary>
    /// Every crossing on the campaign's map as the viewer sees it today, for a layer that
    /// draws them. Fills and returns <paramref name="into"/>; nothing in the campaign changes.
    /// </summary>
    public static List<CrossingLink> Links(Campaign campaign, int viewerNationId, List<CrossingLink> into)
    {
        into ??= new List<CrossingLink>();
        into.Clear();
        var map = campaign?.ProvinceMap;
        if (map is null) return into;
        foreach (var crossing in map.Crossings)
        {
            var from = map.CenterOf(crossing.From);
            var to = map.CenterOf(crossing.To);
            var contested = viewerNationId >= 0 && Contested(campaign, crossing, viewerNationId);
            var authored = crossing.FromShoreLatitude != 0f || crossing.FromShoreLongitude != 0f ||
                           crossing.ToShoreLatitude != 0f || crossing.ToShoreLongitude != 0f;
            into.Add(new CrossingLink
            {
                Id = crossing.Id,
                Name = crossing.Name,
                Kind = crossing.Kind,
                FromProvince = crossing.From,
                ToProvince = crossing.To,
                // Shore to shore where the data says where the shores are (it always does for
                // a crossing read from map_data); centre to centre only for one built in code.
                FromLatitude = authored ? crossing.FromShoreLatitude : from.Latitude,
                FromLongitude = authored ? crossing.FromShoreLongitude : from.Longitude,
                ToLatitude = authored ? crossing.ToShoreLatitude : to.Latitude,
                ToLongitude = authored ? crossing.ToShoreLongitude : to.Longitude,
                Contested = contested,
                Closed = contested && crossing.ClosesUnderBlockade
            });
        }
        return into;
    }

    /// <summary>The word for a kind of crossing, in the player's language.</summary>
    public static string KindName(LandCrossingKind kind) => Loc.Get(kind switch
    {
        LandCrossingKind.Bridge => "Bridge",
        LandCrossingKind.Tunnel => "Tunnel",
        LandCrossingKind.Causeway => "Causeway",
        _ => "Ferry"
    });

    /// <summary>
    /// The sentence a player reads on a crossing: its name, what it is, what it costs to
    /// march and to attack over, and whether it is open to them today.
    /// </summary>
    public static string Describe(Campaign campaign, ResolvedCrossing crossing, int viewerNationId)
    {
        if (campaign is null || crossing is null) return "";
        var march = MarchMultiplier(campaign.Defines, crossing);
        var attack = AttackMultiplier(campaign.Defines, crossing);
        var text = Loc.Format("{0} ({1}): armies cross at {2:P0} of march pace and attack over it at {3:P0} strength.",
            Loc.Get(crossing.Name), KindName(crossing.Kind), march, attack);
        if (viewerNationId < 0 || !Contested(campaign, crossing, viewerNationId)) return text;
        return text + " " + Loc.Get(crossing.ClosesUnderBlockade
            ? "Closed: an enemy fleet commands the strait, and no ferry sails."
            : "Under the guns of an enemy fleet, but still open.");
    }

    /// <summary>The same sentence for the crossing a map link stands for.</summary>
    public static string Describe(Campaign campaign, CrossingLink link, int viewerNationId) =>
        Describe(campaign, campaign?.ProvinceMap?.CrossingBetween(link.FromProvince, link.ToProvince), viewerNationId);

    /// <summary>The one-line legend of the map layer.</summary>
    public static string Legend() =>
        Loc.Get("Strait crossings: dashed links armies can march over. Red: a ferry closed by an enemy fleet.");

    /// <summary>What a division held at a shut ferry says for itself.</summary>
    public static string WaitingSentence(ResolvedCrossing crossing) => crossing is null ? "" :
        Loc.Format("Waiting at {0}: an enemy fleet commands the strait, and no ferry sails.", Loc.Get(crossing.Name));
}
