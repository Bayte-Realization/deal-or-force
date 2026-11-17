using System;
using System.Collections.Generic;
using System.Globalization;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether a rising's formations the road does not reach are fed by their own people instead of being cut off.</summary>
    public bool PartisanSupplyEnabled { get; set; } = true;

    /// <summary>
    /// How far the people of a region stand behind a rising's fighters, 0 to 1 (Partisans.Support),
    /// before either rule below applies. A region of the rising's homeland is never under
    /// <see cref="PartisanHomelandSupport"/>, which clears this; ground outside it counts for
    /// <see cref="PartisanAbroadSupportShare"/> of what its people or its anger would give, so
    /// there the rising's own people have to be seven in ten.
    /// </summary>
    public float PartisanSupportFloor { get; set; } = 0.35f;
    public float PartisanHomelandSupport { get; set; } = 0.6f;
    public float PartisanAbroadSupportShare { get; set; } = 0.5f;

    /// <summary>
    /// People of a region, at full support, whose bread and cartridges keep one division in
    /// the field. The smallest faction of 2024 (ninety thousand people) feeds the two or three
    /// formations it has; one with millions feeds dozens. It binds only on a side that has
    /// crowded more men onto a scrap of ground than live there.
    /// </summary>
    public float PartisanPeoplePerDivision { get; set; } = 30000f;

    /// <summary>
    /// The supply a formation living off the land settles at when the land can feed everybody:
    /// under the 0.75 of a garrison on its stores (a garrison has a depot, a partisan has
    /// gardens), over the 0.5 below which men start to leave. And the share of it they get in
    /// a province the enemy controls, where there is a garrison in the town and a curfew.
    /// </summary>
    public float PartisanForageSupply { get; set; } = 0.65f;
    public float PartisanOccupiedGroundShare { get; set; } = 0.5f;

    /// <summary>Whether a rising's formation beaten with nowhere to retreat scatters among its people instead of surrendering.</summary>
    public bool GuerrillaGoToGroundEnabled { get; set; } = true;

    /// <summary>
    /// The share of such a formation that gets away to fight again. The rest is what losing a
    /// battle with no road out costs: dead and prisoners, as in any surrender. At six in ten a
    /// detachment comes back about twice before there is too little of it left to field.
    /// </summary>
    public float GuerrillaGoToGroundShare { get; set; } = 0.6f;

    /// <summary>
    /// The chance each week that the hidden fighters of a region field a detachment, the least
    /// of a militia formation's establishment they will field, and the organisation it takes
    /// the field with - under a fresh militia's 0.45, because it mustered in a hurry.
    /// </summary>
    public float GuerrillaReformWeeklyChance { get; set; } = 0.25f;
    public float GuerrillaReformMinShare { get; set; } = 0.5f;
    public float GuerrillaReformOrganisation { get; set; } = 0.35f;

    /// <summary>
    /// The share of a region's hidden fighters who go back to their lives each week. Twenty-seven
    /// thousand are under one detachment in sixty weeks, nine thousand in twenty-three: the time
    /// an occupier has to pay for in garrisons, or in a peace.
    /// </summary>
    public float GuerrillaBankDecayWeekly { get; set; } = 0.03f;
}

public sealed partial class Army
{
    /// <summary>
    /// No road reaches this formation, and it is not cut off: the people around it are feeding
    /// it (Partisans.LiveOffTheLand). Set and cleared by the supply trace. Saved; false in a
    /// save from before 16 November 2026, which the next trace puts right.
    /// </summary>
    public bool LivingOffLand;
}

public sealed partial class Nation
{
    /// <summary>
    /// Fighters of this side who hid their rifles and went home after a battle lost with no way
    /// out, by the region (map index) they are in. They are nobody's prisoners; they come back
    /// as detachments while the war lasts and drift away while it does (Partisans). Saved;
    /// empty in a save from before 16 November 2026.
    /// </summary>
    public readonly Dictionary<int, float> FightersGoneToGround = new();
}

/// <summary>
/// Fighters among their own people.
///
/// A rising was held to the rules of a state's army in the two places where it is least like
/// one. Its supply grew from a single seat, the way a country's grows from its capital, so a
/// valley of its own people that an enemy province cut off from that seat was a pocket and
/// starved - four in five of the rising formations that were ever cut off were cut off on
/// ground their own side held. And a formation of it that lost a battle with no province to
/// fall back into surrendered whole, like a division in a cauldron, though a rising holds one
/// to six provinces and so has no province behind it almost by definition: the guerrilla
/// cover that saves half its men in the fight (GuerrillaWar) was cancelled by losing all of
/// them at the end of it. Between them these two were most of why a player saw partisans
/// "die right where they rose" (SimBench --pockets, 16 November 2026).
///
/// Both answers read one number, <see cref="Support"/>: how far the people of the region a
/// formation stands in are this side's own. Where it is high enough, the land feeds the
/// formation the road does not reach (<see cref="LiveOffTheLand"/>) - so many divisions per
/// head of population and no more, food and cartridges but no guns - and a formation beaten
/// with nowhere to go scatters into the villages instead of walking into captivity
/// (<see cref="TryGoToGround"/>), to come back as a detachment where no enemy formation is
/// standing (<see cref="TickStages"/>). Neither is immunity. They fight by the same formulas
/// as everybody, each lost battle costs four men in ten and everything heavy, the land feeds
/// only so many, and the hidden drift home week by week.
///
/// Only sides born of a rising (WarAims.IsRebel): a breakaway, a movement in arms, a faction,
/// a front of a civil war. Not by size of country and not by who is playing it. A state's own
/// militia under occupation would be the same rule and is not here yet.
/// </summary>
public static class Partisans
{
    /// <summary>The reason a formation that scattered is taken off the map with: not a loss, and the bench reads it.</summary>
    public const string WentToGround = "went to ground";

    /// <summary>What these rules did in this process: a bench counter, not campaign state.</summary>
    public static readonly Dictionary<string, int> Tally = new(StringComparer.Ordinal);

    private static void Count(string key, int by = 1) => Tally[key] = Tally.GetValueOrDefault(key) + by;

    /// <summary>
    /// How far the people around this province are the side's own, 0 to 1; nothing for a side
    /// that is not a rising. In its homeland never less than a floor; elsewhere a share of what
    /// kinship or the region's anger gives (StrandedGarrisons.Kin), or of the following its
    /// movement has built there. A read.
    ///
    /// "Homeland" is the regions it was born owning, and also any province it already governed
    /// on the morning its war began (<see cref="HeldWhenTheWarBegan"/>). The second is not a
    /// nicety: the factions of 2024 hold much of their ground as occupied, not owned - Hayat
    /// Tahrir al-Sham's Aleppo, the Rapid Support Forces' Khartoum, the M23's Kivu towns - and
    /// read by the homeland list alone those were foreign country, where kinship counts for
    /// half. With the faction's people the same as the government's, kinship is the region's
    /// anger, and half of that is under the floor: nine HTS formations were cut off and taken
    /// whole in Aleppo in the seventh week of a two-year run (SimBench --pockets, seed 20260830).
    /// </summary>
    public static float Support(Campaign c, Nation nation, int province)
    {
        var map = c?.ProvinceMap;
        if (map is null || !WarAims.IsRebel(nation) || province <= 0 || province > map.ProvinceCount ||
            map.IsWater(province)) return 0f;
        var region = map.ProvinceToState[province];
        var parent = c.NationById(nation.RevoltParentId >= 0 ? nation.RevoltParentId : nation.SecededFromId);
        var kin = StrandedGarrisons.Kin(c, parent, nation, province);
        if (nation.Movement is { } movement && movement.Support.TryGetValue(region, out var following))
            kin = MathF.Max(kin, following);
        var d = c.Defines;
        var home = c.Homelands.TryGetValue(nation.Id, out var homeland) && homeland.Contains(region) ||
                   HeldWhenTheWarBegan(c, nation, province);
        return M.Clamp01(home ? MathF.Max(d.PartisanHomelandSupport, kin) : kin * d.PartisanAbroadSupportShare);
    }

    /// <summary>
    /// Whether the side governed this province when a war it is fighting began: it controlled it
    /// then (the war's record of first holders says so, for ground that has changed hands since),
    /// or it controls it now and nobody has taken it in the war. Ground it conquered in the war
    /// is not this, and nor is anything at all for a side at peace. A read.
    /// </summary>
    public static bool HeldWhenTheWarBegan(Campaign c, Nation nation, int province)
    {
        var controllers = c?.ProvinceController;
        if (controllers is null || nation is null || province <= 0 || province >= controllers.Length) return false;
        var own = (ushort)nation.MapIndex;
        var wars = Warfare.LiveWars(c);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (war.Ended || !war.Involves(nation.Id)) continue;
            if (war.OriginalController.TryGetValue(province, out var first) ? first == own : controllers[province] == own)
                return true;
        }
        return false;
    }

    /// <summary>Divisions the people of a region will keep in the field at this support.</summary>
    public static float Feeds(Campaign c, int region, float support)
    {
        var people = c?.StateByMapIndex(region)?.Population ?? 0L;
        return people <= 0 ? 0f : people * M.Clamp01(support) / MathF.Max(1f, c.Defines.PartisanPeoplePerDivision);
    }

    /// <summary>Whether the supply trace should ask the people before marking this side's formations cut off.</summary>
    internal static bool MayForage(Campaign c, Nation nation) =>
        c?.Defines.PartisanSupplyEnabled == true && WarAims.IsRebel(nation);

    /// <summary>
    /// The supply trace found no road to this formation. If the people where it stands are the
    /// side's own it is not cut off: they feed it, as well as their number allows against every
    /// division of the side standing unreached in the region (<paramref name="standing"/>), and
    /// half as well on ground an enemy controls. Never worse than a starved pocket eats - a
    /// formation among friends is not hungrier than one among enemies. False, and nothing
    /// touched, when they are not its people or there is nobody living there.
    /// </summary>
    internal static bool LiveOffTheLand(Campaign c, Nation nation, Army army, float standing, bool amongFriends)
    {
        var d = c.Defines;
        if (!d.PartisanSupplyEnabled || army.EmbarkedFleetId >= 0 || c.ProvinceMap is not { } map ||
            army.ProvinceId <= 0 || army.ProvinceId > map.ProvinceCount) return false;
        var support = Support(c, nation, army.ProvinceId);
        if (support <= 0f || support < d.PartisanSupportFloor) return false;
        var region = map.ProvinceToState[army.ProvinceId];
        var fed = Feeds(c, region, support);
        if (fed <= 0f) return false;

        var share = M.Clamp01(fed / MathF.Max(0.01f, MathF.Max(standing, army.Divisions)));
        var target = d.PartisanForageSupply * share * (amongFriends ? 1f : M.Clamp01(d.PartisanOccupiedGroundShare));
        army.CutOff = false;
        army.DaysCutOff = 0;
        army.LivingOffLand = true;
        army.SupplyDistance = 0;
        army.SupplySourceStateMapIndex = region;
        army.SupplyCongestion = share;
        // The same step toward its target a formation on a road takes at each trace.
        army.SupplyLevel = M.Lerp(army.SupplyLevel,
            M.Clamp01(MathF.Max(d.PocketHungerSupplyFloor, target)), 0.34f);
        return true;
    }

    /// <summary>What a player reads about a formation living off the land: who feeds it, how many, and what it goes without. A read.</summary>
    public static string ForageLine(Campaign c, Army army)
    {
        if (c?.ProvinceMap is null || army is null || !army.LivingOffLand) return "";
        var nation = c.NationById(army.NationId);
        var region = c.StateByMapIndex(army.SupplySourceStateMapIndex);
        if (nation is null || region is null) return "";
        var fed = Feeds(c, region.MapIndex, Support(c, nation, army.ProvinceId));
        var standing = 0f;
        foreach (var other in c.Armies)
            if (other.NationId == army.NationId && other.LivingOffLand &&
                other.SupplySourceStateMapIndex == army.SupplySourceStateMapIndex) standing += other.Divisions;
        var name = string.IsNullOrEmpty(region.DisplayName) ? region.Name : region.DisplayName;
        return Loc.Format("Living off the land: the people of {0} feed {1:0.#} division(s); {2:0.#} stand here.", name, fed, standing) +
               " " + Loc.Get("No road reaches it, and it is not cut off. Food and cartridges, no replacement equipment.");
    }

    // ------------------------------------------------------------------ going to ground

    /// <summary>
    /// Men a formation actually has: its establishment at its strength, and never more than
    /// its roll says. The roll alone would not do - a militia's roll is topped up from its
    /// village day by day while its strength is not (MilitiaCore), and a detachment that came
    /// back thin, was beaten again and hid again would have hidden more men than it ever had.
    /// </summary>
    private static float Men(Army army)
    {
        var establishment = army.MaxPersonnel > 0 ? army.MaxPersonnel : army.Divisions * MilitaryBootstrap.DivisionSize;
        var men = MathF.Max(0f, establishment * M.Clamp01(army.Strength));
        return army.Personnel > 0 ? MathF.Min(men, army.Personnel) : men;
    }

    /// <summary>
    /// A formation has lost a battle and has no province to fall back into. Called before it
    /// surrenders, at each place Warfare decides that. A rising's formation among its own
    /// people does not surrender: most of its men hide their rifles and go home, to be counted
    /// in <see cref="Nation.FightersGoneToGround"/> for the region; the rest are the dead and
    /// the prisoners of a lost battle, on the war's books like any others; the formation and
    /// everything heavy it had are gone. True when that happened, and the caller then skips
    /// the surrender.
    /// </summary>
    public static bool TryGoToGround(Campaign c, StrategicWar war, Army army, int province)
    {
        var d = c?.Defines;
        var map = c?.ProvinceMap;
        if (d is null || map is null || army is null || !d.GuerrillaGoToGroundEnabled) return false;
        var nation = c.NationById(army.NationId);
        if (nation is null || province <= 0 || province > map.ProvinceCount) return false;
        if (Support(c, nation, province) < MathF.Max(0.0001f, d.PartisanSupportFloor)) return false;
        var men = Men(army);
        if (men < 1f) return false;

        var region = map.ProvinceToState[province];
        var hidden = men * M.Clamp01(d.GuerrillaGoToGroundShare);
        nation.FightersGoneToGround[region] = nation.FightersGoneToGround.GetValueOrDefault(region) + hidden;
        var lost = men - hidden;
        var killed = lost * M.Clamp01(d.SurrenderKilledShare);
        WarLosses.Record(war ?? WarLosses.WarOf(c, army), nation.Id, killed, 0f, lost - killed);

        Count("went_to_ground");
        DiagnosticLogs.Write("simulation",
            $"WENT_TO_GROUND army={army.Id} nation={nation.Tag} province={province} region={region} hidden={DiagnosticLogs.N(hidden)} lost={DiagnosticLogs.N(lost)}");
        // Both sides are told, each in its own chronicle: to one it is men saved, to the other
        // a victory with fewer prisoners than there were defenders.
        var place = c.StateByMapIndex(region)?.Name ?? "";
        var count = hidden.ToString("N0", CultureInfo.InvariantCulture);
        var enemy = war is null ? -1 : war.EnemyLeaderOf(nation.Id);
        c.Chronicle.Record(c, "fighters_went_to_ground", nation.Id, enemy, subject: count, place: place, isPublic: false);
        if (enemy >= 0)
            c.Chronicle.Record(c, "enemy_went_to_ground", enemy, nation.Id, subject: count, place: place, isPublic: false);
        Attrition.Destroy(c, army, WentToGround, lost: false);
        return true;
    }

    /// <summary>Everybody of this side in hiding, across all its regions.</summary>
    public static float Hidden(Nation nation)
    {
        if (nation is null || nation.FightersGoneToGround.Count == 0) return 0f;
        var men = 0f;
        foreach (var here in nation.FightersGoneToGround.Values) men += here;
        return men;
    }

    /// <summary>The fewest hidden fighters that take the field as a detachment.</summary>
    private static float Muster(Defines d) =>
        MathF.Max(1f, MilitiaCore.Weights.Formation * M.Clamp01(d.GuerrillaReformMinShare));

    /// <summary>
    /// Detachments this side could still field from hiding. A rising with nobody on the map
    /// and three thousand rifles under the floorboards has not been beaten yet, and the rules
    /// that decide a rising is finished count these beside its formations (Factions.FightsOn).
    /// </summary>
    public static int DetachmentsInHiding(Campaign c, Nation nation) =>
        c is null || nation is null ? 0 : (int)(Hidden(nation) / Muster(c.Defines));

    [ThreadStatic] private static List<int> _regions;
    [ThreadStatic] private static HashSet<int> _garrisoned;

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>
    /// Weekly: the hidden drift home, and where there are still enough of them in a region, and
    /// the side still believes in its war, some weeks they take the field again - on ground the
    /// side holds in that region, or failing that in a province of it where no enemy formation
    /// is standing, which they take. So an occupier keeps a rising down by standing in every
    /// province of its homeland, for as long as the hidden last, or it makes peace. A side at
    /// war with nobody has nobody in hiding: the war ended and they are simply home.
    ///
    /// Countries in list order and regions in number order, and the roll is a hash of the seed,
    /// the side, the region and the week: the same campaign fields the same detachments.
    /// </summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c?.ProvinceMap is null || c.ElapsedDays <= 0 || c.ElapsedDays % 7 != 2) yield break;
        var d = c.Defines;
        var regions = _regions ??= new List<int>();
        for (var index = 0; index < c.Nations.Count; index++)
        {
            var nation = c.Nations[index];
            var bank = nation.FightersGoneToGround;
            if (bank.Count == 0) continue;
            if (nation.Population <= 0 || !LogisticsSystem.Besieged(c, nation.Id)) { bank.Clear(); continue; }
            yield return 0;

            regions.Clear();
            foreach (var region in bank.Keys) regions.Add(region);
            regions.Sort();
            var keep = 1f - M.Clamp01(d.GuerrillaBankDecayWeekly);
            foreach (var region in regions)
            {
                var men = bank[region] * keep;
                if (men < 1f) { bank.Remove(region); continue; }
                bank[region] = men;
                if (!d.GuerrillaGoToGroundEnabled || men < Muster(d) ||
                    nation.WarSupport <= d.FactionFightOnSupport) continue;
                if (Escalation.Roll(c.Seed, nation.Id, region, c.ElapsedDays / 7, 0x6A7E) >= d.GuerrillaReformWeeklyChance)
                    continue;
                var fielded = Reform(c, nation, region, MathF.Min(men, MilitiaCore.Weights.Formation));
                if (fielded <= 0f) continue;
                if (men - fielded < 1f) bank.Remove(region);
                else bank[region] = men - fielded;
            }
        }
    }

    /// <summary>
    /// Field one detachment of a region's hidden fighters, and say how many men it took; nothing
    /// when every province of the region has an enemy formation standing in it.
    /// </summary>
    private static float Reform(Campaign c, Nation nation, int region, float men)
    {
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController;
        var state = c.StateByMapIndex(region);
        if (state is null || controllers is null) return 0f;

        // Ground the side still holds in the region first: the place its militia would muster.
        var (province, _) = MilitiaCore.Seat(map, controllers, (ushort)nation.MapIndex, (ushort)region);
        StrategicWar war = null;
        ushort holder = 0;
        if (province <= 0)
        {
            province = UnwatchedProvince(c, nation, region, out war, out holder);
            if (province <= 0) return 0f;
        }
        var army = MilitiaCore.FoundAt(c, nation, state, province, (int)men, c.Defines.GuerrillaReformOrganisation);
        if (army is null) return 0f;
        if (war is not null)
        {
            // They take the province they came out in: nobody was standing in it to say no.
            war.OriginalController.TryAdd(province, holder);
            c.SetProvinceController(province, (ushort)nation.MapIndex);
            war.FrontDirty = true;
        }
        Count("came_back");
        if (war is not null) Count("came_back_on_enemy_ground");
        DiagnosticLogs.Write("simulation",
            $"CAME_BACK nation={nation.Tag} region={region} province={province} men={army.Personnel} took_ground={(war is not null ? 1 : 0)}");
        var count = army.Personnel.ToString("N0", CultureInfo.InvariantCulture);
        var enemyId = war?.EnemyLeaderOf(nation.Id) ?? EnemyOf(c, nation);
        c.Chronicle.Record(c, "fighters_came_back", nation.Id, enemyId, subject: count, place: state.Name, isPublic: false);
        if (enemyId >= 0)
            c.Chronicle.Record(c, "enemy_came_back", enemyId, nation.Id, subject: count, place: state.Name, isPublic: false);
        c.MarkStrategicChange();
        return army.Personnel;
    }

    private static int EnemyOf(Campaign c, Nation nation)
    {
        var wars = Warfare.LiveWars(c);
        for (var index = 0; index < wars.Count; index++)
            if (!wars[index].Ended && wars[index].Involves(nation.Id)) return wars[index].EnemyLeaderOf(nation.Id);
        return -1;
    }

    /// <summary>
    /// A province of the region held by somebody this side is at war with, with no formation of
    /// any of its enemies standing in it: the roughest such ground, then the lowest number.
    /// </summary>
    private static int UnwatchedProvince(Campaign c, Nation nation, int region, out StrategicWar war, out ushort holder)
    {
        war = null;
        holder = 0;
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController;
        var wars = Warfare.LiveWars(c);
        var garrisoned = _garrisoned ??= new HashSet<int>();
        garrisoned.Clear();
        foreach (var army in c.Armies)
        {
            var at = army.ProvinceId;
            if (army.NationId == nation.Id || army.EmbarkedFleetId >= 0 || army.Divisions <= 0f ||
                at <= 0 || at > map.ProvinceCount || map.ProvinceToState[at] != region) continue;
            if (CampaignDiplomacy.AtWar(c, nation.Id, army.NationId)) garrisoned.Add(at);
        }

        var best = 0;
        var roughest = float.MinValue;
        for (var province = 1; province <= map.ProvinceCount && province < controllers.Length; province++)
        {
            if (map.ProvinceToState[province] != region || map.IsWater(province) ||
                c.DestroyedProvinces.Contains(province) || garrisoned.Contains(province)) continue;
            var flag = controllers[province];
            if (flag == 0 || flag == nation.MapIndex) continue;
            var owner = c.NationByMapIndex(flag);
            if (owner is null) continue;
            StrategicWar against = null;
            for (var index = 0; index < wars.Count; index++)
                if (!wars[index].Ended && wars[index].Opposes(nation.Id, owner.Id)) { against = wars[index]; break; }
            if (against is null) continue;
            var rough = map.TerrainAt(province).Defense;
            if (rough <= roughest) continue;
            roughest = rough;
            best = province;
            war = against;
            holder = flag;
        }
        return best;
    }
}
