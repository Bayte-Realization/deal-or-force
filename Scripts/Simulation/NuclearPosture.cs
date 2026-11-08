using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>
    /// This country's own nuclear alert, 5 (routine) down to 1 (launch on warning). Only a
    /// deterrent holder sets it (NuclearPosture); the world's DEFCON is never calmer than the most
    /// alarmed holder. Changed on <see cref="NuclearPostureDay"/>; last test on <see cref="LastNuclearTestDay"/>.
    /// </summary>
    public int NuclearPosture = 5;
    public int NuclearPostureDay = -100000;
    public int LastNuclearTestDay = -100000;
    /// <summary>The last red-telephone call this country made or took.</summary>
    public int LastHotlineDay = -100000;
    /// <summary>The last day this country used a nuclear weapon (NuclearPosture.Strike).</summary>
    public int LastNuclearStrikeDay = -100000;
    /// <summary>The last day a nuclear weapon struck this country's forces, and whose (-1 for nobody's).</summary>
    public int NuclearStruckDay = -100000;
    public int NuclearStruckBy = -1;
    /// <summary>The last day this country used a nuclear weapon first - not in answer to one.</summary>
    public int NuclearFirstUseDay = -100000;
}

public sealed partial class Defines
{
    /// <summary>Days a posture must stand before it can be changed again, and the political credit raising it costs.</summary>
    public int NuclearPostureCooldownDays { get; set; } = 30;
    public float NuclearPostureRaiseCost { get; set; } = 5f;
    /// <summary>
    /// Nuclear coercion: the war support an enemy at war with a holder loses each week for each
    /// step of that holder's posture below 3 - the threat in the background of every report.
    /// </summary>
    public float NuclearCoercionWarSupport { get; set; } = 0.004f;
    /// <summary>A nuclear test: political credit, the years before the next, and the tension it adds with every rival.</summary>
    public float NuclearTestCost { get; set; } = 15f;
    public int NuclearTestCooldownDays { get; set; } = 365;
    public float NuclearTestTension { get; set; } = 15f;
    /// <summary>The red telephone: tension it removes between two holders, and the days before the next call.</summary>
    public float HotlineTension { get; set; } = 20f;
    public int HotlineCooldownDays { get; set; } = 90;
    /// <summary>
    /// The weekly chance, while the world stands at DEFCON 2 or 1, of an incident - a false
    /// warning, a lost bomber, a submarine nobody can reach (Petrov, 1983; Arkhipov, 1962).
    /// </summary>
    public float NuclearIncidentChance { get; set; } = 0.03f;
    /// <summary>Whether governments the computer runs set their own posture, test, and call each other.</summary>
    public bool AiNuclearPosture { get; set; } = false;
    /// <summary>The weekly chance a computer-run holder in a standoff tests a weapon, when it may.</summary>
    public float AiNuclearTestChance { get; set; } = 0.02f;
    /// <summary>The weekly chance two alarmed computer-run holders pick up the red telephone.</summary>
    public float AiHotlineChance { get; set; } = 0.25f;

    /// <summary>
    /// The last resort: the political credit a nuclear strike costs, the years before another,
    /// the share of the men in the struck province it kills, the ruin it leaves in the region,
    /// the war support it takes from the country whose forces it struck, the tension it adds
    /// with everybody not allied, and how long the world stays at DEFCON 2 or worse afterwards.
    /// </summary>
    public float NuclearStrikeCost { get; set; } = 25f;
    public int NuclearStrikeCooldownDays { get; set; } = 365;
    public float NuclearStrikeLoss { get; set; } = 0.6f;
    public float NuclearStrikeRuin { get; set; } = 0.5f;
    public float NuclearStrikeWarSupport { get; set; } = 0.3f;
    public float NuclearStrikeTension { get; set; } = 30f;
    public int NuclearShadowDays { get; set; } = 365;
    /// <summary>
    /// The weekly chance a computer-run holder at DEFCON 1, facing defeat on its own soil, gives
    /// the order. Zero keeps the weapons a threat only.
    /// </summary>
    public float AiNuclearStrikeChance { get; set; } = 0f;

    /// <summary>
    /// The nuclear umbrella: what attacking a country costs the attacker for each step an allied
    /// holder's own alert stands below 4. An alert raised is a guarantee made visible.
    /// </summary>
    public float AiNuclearUmbrellaPrice { get; set; } = 0f;

    /// <summary>
    /// The second strike: the days a holder whose forces were struck may answer wherever the
    /// striker's forces stand; the notoriety a first use earns (an answer earns none); and the
    /// weekly chance a computer-run holder gives the answer. A deterrent is as good as the
    /// certainty of the answer.
    /// </summary>
    public int NuclearRetaliationDays { get; set; } = 60;
    public float NuclearFirstUseNotoriety { get; set; } = 0f;
    public float AiNuclearRetaliationChance { get; set; } = 0f;
}

public sealed partial class Defines
{
    /// <summary>Whether a weapon may fall on the enemy's own forces and ground, not only on an invader at home.</summary>
    public bool NuclearCounterforce { get; set; } = true;
    /// <summary>A strike on a country's cities (countervalue): what it costs to order, the share of
    /// the struck region's people it kills, the stability and war support its country loses, and the
    /// notoriety it brings on top of first use. 0 casualties turns the order off.</summary>
    public float NuclearDevastateCost { get; set; } = 40f;
    public float NuclearCityCasualties { get; set; } = 0.15f;
    public float NuclearCityStability { get; set; } = 0.12f;
    public float NuclearCityWarSupport { get; set; } = 0.10f;
    public float NuclearDevastationNotoriety { get; set; } = 30f;
}

/// <summary>
/// Nuclear posture: what a deterrent holder does with its weapons short of using them.
///
/// The world's DEFCON was a reading - the arithmetic of who was at war with whom - and a
/// player could only watch it. A holder now has a posture of its own and four acts, the same for
/// every government:
///
/// - raise the alert (a step down the ladder): its forces recover faster, and every country at
///   war with it feels the threat in its war support - but the alert is paid for in upkeep,
///   and the world is never calmer than the most alarmed holder;
/// - stand down (a step back up): cheaper, calmer, and the world notices;
/// - test a weapon: credibility bought with every rival's tension and most of the world's
///   opinion, once a year at most;
/// - pick up the red telephone to another holder: both step back from the brink.
///
/// And at DEFCON 2 and 1 the machinery itself is the danger: a false warning or a lost
/// aircraft takes the world a step closer, and nobody chose it.
/// </summary>
public static class NuclearPosture
{
    public static bool IsHolder(Campaign c, Nation n) => n is not null && Deterrence.TierOf(c, n) >= 1;

    public static bool Active(Campaign c) => DefconSystem.Enabled(c) && c.FeatureEnabled("nuclear_posture");

    /// <summary>The most alarmed holder's posture, 5 when none has raised its alert.</summary>
    public static int Lowest(Campaign c, out Nation holder)
    {
        holder = null;
        var lowest = 5;
        if (!Active(c)) return lowest;
        foreach (var n in c.Nations)
            if (n.NuclearPosture < lowest && n.Population > 0 && IsHolder(c, n)) { lowest = n.NuclearPosture; holder = n; }
        return Math.Clamp(lowest, 1, 5);
    }

    /// <summary>The alert this country's forces stand at: its own posture, or the world's if that is higher.</summary>
    public static int Effective(Campaign c, Nation n) =>
        IsHolder(c, n) ? Math.Min(DefconSystem.CurrentLevel(c), Math.Clamp(n.NuclearPosture, 1, 5)) : DefconSystem.CurrentLevel(c);

    private static bool AtWarWith(Campaign c, int a, int b) => c.Wars.Any(w => !w.Ended && w.Opposes(a, b));

    /// <summary>Player-visible country targets follow the same non-civil wars as automatic
    /// targeting. Choosing a country never creates a war or bypasses readiness and cooldown.</summary>
    public static IReadOnlyList<Nation> Targets(Campaign c, Nation n) => c is null || n is null ? Array.Empty<Nation>() :
        c.Wars.Where(w => !w.Ended && !w.CivilConflict && w.Involves(n.Id)).SelectMany(w => w.EnemiesOf(n.Id)).Distinct()
            .Select(c.NationById).Where(t => t is { Population: > 0 }).OrderBy(t => t.Id).ToArray();

    public static string Refusal(Campaign c, Nation n, string action, Nation target = null)
    {
        if (!Active(c)) return Loc.Get("The nuclear alert is switched off in this campaign.");
        if (!IsHolder(c, n)) return Loc.Get("Your country holds no nuclear deterrent.");
        var d = c.Defines;
        switch (action)
        {
            case "raise":
                if (n.NuclearPosture <= 1) return Loc.Get("The forces already stand at the highest alert.");
                if (c.ElapsedDays - n.NuclearPostureDay < d.NuclearPostureCooldownDays)
                    return Loc.Format("The posture was changed {0} days ago; the next change after {1}.",
                        c.ElapsedDays - n.NuclearPostureDay, d.NuclearPostureCooldownDays);
                if (n.PoliticalPoints < d.NuclearPostureRaiseCost)
                    return Loc.Format("Raising the alert costs {0:0} political credit; there is {1:0.#}.", d.NuclearPostureRaiseCost, n.PoliticalPoints);
                return "";
            case "stand_down":
                if (n.NuclearPosture >= 5) return Loc.Get("The forces are already at routine readiness.");
                if (c.ElapsedDays - n.NuclearPostureDay < d.NuclearPostureCooldownDays)
                    return Loc.Format("The posture was changed {0} days ago; the next change after {1}.",
                        c.ElapsedDays - n.NuclearPostureDay, d.NuclearPostureCooldownDays);
                return "";
            case "test":
                if (c.ElapsedDays - n.LastNuclearTestDay < d.NuclearTestCooldownDays)
                    return Loc.Format("The last test was {0} days ago; the next after {1}.",
                        c.ElapsedDays - n.LastNuclearTestDay, d.NuclearTestCooldownDays);
                if (n.PoliticalPoints < d.NuclearTestCost)
                    return Loc.Format("A test costs {0:0} political credit; there is {1:0.#}.", d.NuclearTestCost, n.PoliticalPoints);
                return "";
            case "devastate":
                if (target is not null && !Targets(c, n).Any(t => t.Id == target.Id)) return Loc.Get("The selected country is not an opponent in an active interstate war.");
                if (!c.FeatureEnabled("nuclear_strikes")) return Loc.Get("No government in this world can order a nuclear weapon used.");
                if (d.NuclearCityCasualties <= 0f) return Loc.Get("No government in this world can order a strike on cities.");
                if (n.NuclearPosture > 1) return Loc.Get("Only forces at DEFCON 1 can be ordered to use a weapon.");
                if (c.ElapsedDays - n.LastNuclearStrikeDay < d.NuclearStrikeCooldownDays)
                    return Loc.Format("A weapon was used {0} days ago; the next order after {1}.",
                        c.ElapsedDays - n.LastNuclearStrikeDay, d.NuclearStrikeCooldownDays);
                if (n.PoliticalPoints < d.NuclearDevastateCost)
                    return Loc.Format("The order costs {0:0} political credit; there is {1:0.#}.", d.NuclearDevastateCost, n.PoliticalPoints);
                if (CityTarget(c, n, out _, target) <= 0) return Loc.Get("No eligible city target exists in the selected country.");
                return "";
            case "strike":
                if (target is not null && !Targets(c, n).Any(t => t.Id == target.Id)) return Loc.Get("The selected country is not an opponent in an active interstate war.");
                if (!c.FeatureEnabled("nuclear_strikes")) return Loc.Get("No government in this world can order a nuclear weapon used.");
                if (n.NuclearPosture > 1) return Loc.Get("Only forces at DEFCON 1 can be ordered to use a weapon.");
                if (c.ElapsedDays - n.LastNuclearStrikeDay < d.NuclearStrikeCooldownDays)
                    return Loc.Format("A weapon was used {0} days ago; the next order after {1}.",
                        c.ElapsedDays - n.LastNuclearStrikeDay, d.NuclearStrikeCooldownDays);
                if (n.PoliticalPoints < d.NuclearStrikeCost)
                    return Loc.Format("The order costs {0:0} political credit; there is {1:0.#}.", d.NuclearStrikeCost, n.PoliticalPoints);
                if (StrikeTarget(c, n, out _, target) <= 0)
                    return Loc.Get("No enemy force is within reach of a weapon.");
                return "";
            case "hotline":
                if (target is null || target.Id == n.Id || !IsHolder(c, target)) return Loc.Get("The red telephone connects two nuclear powers.");
                if (n.NuclearPosture >= 4 && target.NuclearPosture >= 4) return Loc.Get("Neither side is on alert: there is nothing to talk down.");
                if (c.ElapsedDays - n.LastHotlineDay < d.HotlineCooldownDays || c.ElapsedDays - target.LastHotlineDay < d.HotlineCooldownDays)
                    return Loc.Format("The red telephone was used in the last {0} days.", d.HotlineCooldownDays);
                return "";
        }
        return Loc.Get("Unknown order.");
    }

    public static bool Apply(Campaign c, Nation n, string action, Nation target = null)
    {
        if (Refusal(c, n, action, target).Length > 0) return false;
        var d = c.Defines;
        switch (action)
        {
            case "raise":
                n.PoliticalPoints -= d.NuclearPostureRaiseCost;
                n.NuclearPosture--;
                n.NuclearPostureDay = c.ElapsedDays;
                // DEFCON 4 is a watch, not news: the chronicle hears of an alert from 3 on.
                if (n.NuclearPosture <= 3) c.Chronicle.Record(c, "nuclear_alert_raised", n.Id, -1, n.NuclearPosture.ToString());
                // A government that puts its weapons on alert is remembered for it by everybody
                // who is not on its side - as much as it concerns them (OpinionWitnesses): its
                // rivals and neighbours in full, the far side of the world hardly at all.
                if (n.NuclearPosture <= 2)
                    foreach (var other in c.Nations)
                        if (other.Id != n.Id && other.Population > 0 && !Allied(c, n.Id, other.Id))
                            OpinionWitnesses.Witness(c, other, n, null, "nuclear_threats");
                break;
            case "stand_down":
                n.NuclearPosture++;
                n.NuclearPostureDay = c.ElapsedDays;
                if (n.NuclearPosture <= 4) c.Chronicle.Record(c, "nuclear_alert_lowered", n.Id, -1, n.NuclearPosture.ToString());
                break;
            case "test":
                n.PoliticalPoints -= d.NuclearTestCost;
                n.LastNuclearTestDay = c.ElapsedDays;
                c.Chronicle.Record(c, "nuclear_test", n.Id);
                // Everybody not on its side notes a test, by as much as it concerns them. It used
                // to be the same minus twenty, twice over, for every government on earth - and
                // it created a relation with every one of them to say so.
                foreach (var other in c.Nations)
                {
                    if (other.Id == n.Id || other.Population <= 0) continue;
                    if (!Allied(c, n.Id, other.Id)) OpinionWitnesses.Witness(c, other, n, null, "nuclear_test");
                    var relation = CampaignDiplomacy.Peek(c, n.Id, other.Id);
                    if (relation is null) continue;
                    if (relation.Stance == DiplomaticStance.Rival || relation.TensionStage >= (int)TensionStage.Crisis)
                        relation.Tension = MathF.Min(100f, relation.Tension + d.NuclearTestTension);
                }
                break;
            case "strike":
                Strike(c, n, cities: false, target);
                break;
            case "devastate":
                Strike(c, n, cities: true, target);
                break;
            case "hotline":
                n.LastHotlineDay = target.LastHotlineDay = c.ElapsedDays;
                n.NuclearPosture = Math.Min(5, n.NuclearPosture + 1);
                target.NuclearPosture = Math.Min(5, target.NuclearPosture + 1);
                n.NuclearPostureDay = target.NuclearPostureDay = c.ElapsedDays;
                var line = CampaignDiplomacy.Get(c, n.Id, target.Id);
                if (line is not null) line.Tension = MathF.Max(0f, line.Tension - d.HotlineTension);
                if (CampaignDiplomacy.Get(c, target.Id, n.Id) is { } back) OpinionSystem.Remember(c, back, "red_telephone");
                if (CampaignDiplomacy.Get(c, n.Id, target.Id) is { } forth) OpinionSystem.Remember(c, forth, "red_telephone");
                c.Chronicle.Record(c, "red_telephone", n.Id, target.Id);
                break;
        }
        c.MarkStrategicChange();
        return true;
    }

    /// <summary>
    /// Where a strike would fall, in a war that is not a civil one. First an answer: the striker's
    /// strongest force wherever it stands. Then the homeland: the enemy's strongest force on this
    /// country's own soil. Then counterforce: the enemy's strongest concentration anywhere - its
    /// own ground or the front. The user asked why a weapon could only be used on one's own land;
    /// no strategic doctrine aims it there first, and a country losing a war abroad has nothing
    /// of its own to defend with it. When the AI orders it at all is still Existential (defeat or
    /// the capital lost); NuclearCounterforce false keeps the old homeland-only rule.
    /// </summary>
    public static int StrikeTarget(Campaign c, Nation n, out HashSet<int> enemies, Nation target = null)
    {
        enemies = new HashSet<int>();
        var topology = c.ProvinceMap;
        if (topology is null || n is null) return 0;
        foreach (var war in c.Wars)
            if (!war.Ended && !war.CivilConflict && war.Involves(n.Id))
                foreach (var id in war.EnemiesOf(n.Id)) enemies.Add(id);
        if (target is not null)
        {
            if (!enemies.Contains(target.Id)) { enemies.Clear(); return 0; }
            enemies.IntersectWith(new[] { target.Id });
        }
        if (enemies.Count == 0) return 0;
        // An answer to a strike falls on the striker's strongest force wherever it stands; failing
        // that, and always otherwise, the weapon defends our own soil.
        var avenged = Avenging(c, n);
        if (target is not null && avenged?.Id != target.Id) avenged = null;
        var weight = new Dictionary<int, float>();
        var passes = c.Defines.NuclearCounterforce ? 3 : 2;
        for (var pass = avenged is null ? 1 : 0; pass < passes; pass++)
        {
            weight.Clear();
            foreach (var army in c.Armies)
            {
                if (!enemies.Contains(army.NationId) || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 ||
                    army.ProvinceId > topology.ProvinceCount || army.Divisions < 0.1f) continue;
                if (pass == 0 ? army.NationId != avenged.Id : pass == 1 && topology.ProvinceToNation[army.ProvinceId] != n.MapIndex) continue;
                weight[army.ProvinceId] = weight.GetValueOrDefault(army.ProvinceId) + army.Personnel * army.Strength;
            }
            var best = 0;
            var bestWeight = 0f;
            foreach (var (province, value) in weight)
                if (value > bestWeight || value == bestWeight && province < best) { best = province; bestWeight = value; }
            if (best > 0) return best;
        }
        return 0;
    }

    /// <summary>
    /// The holder whose strike this country may still answer: one that struck its forces in the
    /// last NuclearRetaliationDays and that it is still at war with. Null otherwise.
    /// </summary>
    public static Nation Avenging(Campaign c, Nation n)
    {
        if (n is null || n.NuclearStruckBy < 0 || c.ElapsedDays - n.NuclearStruckDay >= c.Defines.NuclearRetaliationDays) return null;
        var striker = c.NationById(n.NuclearStruckBy);
        return striker is { Population: > 0 } && striker.Id != n.Id && AtWarWith(c, n.Id, striker.Id) ? striker : null;
    }

    /// <summary>
    /// The weapon used. The enemy's forces in the province lose most of their men and all their
    /// order - and so does anybody of ours standing beside them; the region is left in ruins; the
    /// country whose army was struck loses the stomach for the war. Then the price: every
    /// government on earth remembers it, allies included; everybody not allied is the more tense
    /// for it; and the world goes to DEFCON 1 and stays at 2 or worse for a year.
    /// </summary>
    /// <summary>
    /// Where a strike on a country's cities would fall: the seat of the country it is answering, or
    /// else of its most populous enemy in a war that is not a civil one - the capital's province.
    /// </summary>
    public static int CityTarget(Campaign c, Nation n, out Nation victim, Nation target = null)
    {
        victim = null;
        if (c?.ProvinceMap is null || n is null) return 0;
        if (target is not null)
        {
            if (target.Movement is not null || !Targets(c, n).Any(t => t.Id == target.Id)) return 0;
            victim = target;
        }
        else victim = Avenging(c, n);
        if (victim is null && target is null)
            foreach (var war in c.Wars)
            {
                if (war.Ended || war.CivilConflict || !war.Involves(n.Id)) continue;
                foreach (var id in war.EnemiesOf(n.Id))
                    if (c.NationById(id) is { Population: > 0, Movement: null } enemy && (victim is null || enemy.Population > victim.Population))
                        victim = enemy;
            }
        if (victim is null) return 0;
        var map = c.ProvinceMap;
        if (victim.CapitalProvinceId > 0 && victim.CapitalProvinceId <= map.ProvinceCount &&
            map.ProvinceToNation[victim.CapitalProvinceId] == victim.MapIndex) return victim.CapitalProvinceId;
        var largest = c.StatesOf(victim).OrderByDescending(r => r.Population).FirstOrDefault();
        if (largest is null) return 0;
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (map.ProvinceToState[p] == largest.MapIndex && map.ProvinceToNation[p] == victim.MapIndex && !map.IsWater(p)) return p;
        return 0;
    }

    private static void Strike(Campaign c, Nation n, bool cities, Nation target = null)
    {
        var d = c.Defines;
        var answer = Avenging(c, n);
        if (target is not null && answer?.Id != target.Id) answer = null;
        var enemies = new HashSet<int>();
        Nation victim = null;
        var province = cities ? CityTarget(c, n, out victim, target) : StrikeTarget(c, n, out enemies, target);
        if (province <= 0) return;
        if (cities) enemies.Add(victim.Id);
        n.PoliticalPoints -= cities ? d.NuclearDevastateCost : d.NuclearStrikeCost;
        n.LastNuclearStrikeDay = c.ElapsedDays;
        // The first to use one is the outlaw; the one that answers is understood, if not forgiven.
        // A city struck is a crime of its own, first or not.
        if (answer is null)
        {
            n.NuclearFirstUseDay = c.ElapsedDays;
            n.Notoriety += d.NuclearFirstUseNotoriety;
        }
        else n.NuclearStruckBy = -1;
        if (cities) n.Notoriety += d.NuclearDevastationNotoriety;
        var struck = new HashSet<int>();
        foreach (var army in c.Armies)
        {
            if (army.ProvinceId != province || army.EmbarkedFleetId >= 0) continue;
            var men = (int)MathF.Floor(army.Personnel * M.Clamp01(d.NuclearStrikeLoss));
            var complement = army.Divisions * MilitaryBootstrap.DivisionSize;
            army.Personnel -= men;
            if (complement > 1f) army.Strength = M.Clamp01(army.Strength - men / complement);
            army.Organisation = 0f;
            if (enemies.Contains(army.NationId)) struck.Add(army.NationId);
        }
        var map = c.ProvinceMap.ProvinceToState[province];
        var region = c.States.Find(state => state.MapIndex == map);
        if (region is not null)
        {
            region.WarDamage = M.Clamp01(region.WarDamage + d.NuclearStrikeRuin * (cities ? 2f : 1f));
            region.LastFoughtDay = c.ElapsedDays;
            if (cities)
            {
                // The city itself: its people, and the country that loses them.
                region.Population = System.Math.Max(0, region.Population - (long)(region.Population * M.Clamp01(d.NuclearCityCasualties)));
                victim.Stability = M.Clamp01(victim.Stability - d.NuclearCityStability);
                victim.WarSupport = M.Clamp01(victim.WarSupport - d.NuclearCityWarSupport);
                struck.Add(victim.Id);
            }
        }
        foreach (var id in struck.OrderBy(id => id))
        {
            if (c.NationById(id) is not { } enemy) continue;
            enemy.WarSupport = M.Clamp01(enemy.WarSupport - d.NuclearStrikeWarSupport);
            enemy.NuclearStruckDay = c.ElapsedDays;
            enemy.NuclearStruckBy = n.Id;
            // A holder whose forces are struck goes to launch on warning at once: nobody waits a
            // month of committee meetings after that.
            if (IsHolder(c, enemy) && enemy.NuclearPosture > 1)
            {
                enemy.NuclearPosture = 1;
                enemy.NuclearPostureDay = c.ElapsedDays;
            }
        }
        foreach (var other in c.Nations)
        {
            if (other.Id == n.Id || other.Population <= 0) continue;
            // The countries struck remember it in full. Everybody else is a witness, and a
            // nuclear weapon used is everybody's business: never less than half, whatever the
            // ties and the distance (OpinionWitnesses).
            var atWarWith = enemies.Contains(other.Id);
            if (atWarWith && CampaignDiplomacy.Get(c, other.Id, n.Id) is { } seen)
                OpinionSystem.Remember(c, seen, answer is null ? "nuclear_strike" : "nuclear_retaliation");
            else if (!atWarWith)
                OpinionWitnesses.Witness(c, other, n, null, answer is null ? "nuclear_strike" : "nuclear_retaliation", floor: 0.5f);
            if (!Allied(c, n.Id, other.Id) && CampaignDiplomacy.Get(c, n.Id, other.Id) is { } relation)
                relation.Tension = MathF.Min(100f, relation.Tension + d.NuclearStrikeTension);
        }
        c.DefconLevel = 1;
        c.DefconDaysInStage = 0;
        c.DefconPeakLevel = 1;
        NuclearDetonations.Announce(province, c.ElapsedDays, answer is not null);
        c.Chronicle.Record(c, cities ? "nuclear_devastation" : answer is null ? "nuclear_strike" : "nuclear_retaliation", n.Id,
            cities ? victim.Id : struck.Count > 0 ? struck.Min() : -1, "", region?.DisplayName ?? "");
    }

    /// <summary>The most recent nuclear strike still casting its shadow over the world, or null.</summary>
    public static Nation Shadow(Campaign c)
    {
        Nation latest = null;
        foreach (var n in c.Nations)
            if (c.ElapsedDays - n.LastNuclearStrikeDay < c.Defines.NuclearShadowDays &&
                (latest is null || n.LastNuclearStrikeDay > latest.LastNuclearStrikeDay)) latest = n;
        return latest;
    }

    /// <summary>
    /// Defeat on its own soil: a war (not a civil one) this country is losing past -60, or one in
    /// which the enemy holds its capital - and an enemy force standing on its ground to strike.
    /// </summary>
    internal static bool Existential(Campaign c, Nation n)
    {
        var controllers = c.ProvinceController;
        var capitalLost = controllers is not null && n.CapitalProvinceId > 0 && n.CapitalProvinceId < controllers.Length &&
                          controllers[n.CapitalProvinceId] != n.MapIndex;
        foreach (var war in c.Wars)
        {
            if (war.Ended || war.CivilConflict || !war.Involves(n.Id)) continue;
            if (capitalLost || (war.IsAttacking(n.Id) ? war.WarScore : -war.WarScore) < -60f)
                return StrikeTarget(c, n, out _) > 0;
        }
        return false;
    }

    /// <summary>
    /// The umbrella over <paramref name="target"/>: the most alarmed holder allied to it (or that it
    /// answers to), neither the attacker nor on the attacker's side, prices a war against it by
    /// how far its alert stands below 4. A holder at routine readiness protects nobody this way -
    /// raising the alert is how a guarantee becomes believable.
    /// </summary>
    public static float UmbrellaPrice(Campaign c, Nation attacker, Nation target, out Nation patron)
    {
        patron = null;
        if (!Active(c) || attacker is null || target is null || c.Defines.AiNuclearUmbrellaPrice <= 0f) return 0f;
        var steps = 0;
        foreach (var holder in c.Nations)
        {
            if (holder.NuclearPosture >= 4 || holder.Id == attacker.Id || holder.Id == target.Id || holder.Population <= 0) continue;
            if (!Allied(c, holder.Id, target.Id) || Allied(c, holder.Id, attacker.Id) || !IsHolder(c, holder)) continue;
            if (4 - holder.NuclearPosture > steps) { steps = 4 - holder.NuclearPosture; patron = holder; }
        }
        return steps * c.Defines.AiNuclearUmbrellaPrice;
    }

    private static bool Allied(Campaign c, int a, int b) =>
        CampaignDiplomacy.Peek(c, a, b) is { Stance: DiplomaticStance.Allied } ||
        (c.NationById(a)?.AnswersTo(b) ?? false) || (c.NationById(b)?.AnswersTo(a) ?? false);

    /// <summary>
    /// The week: coercion felt by every enemy of an alarmed holder, the computer's governments'
    /// own decisions, and at the brink the incidents nobody chose. Runs on the DEFCON review day.
    /// </summary>
    public static void Week(Campaign c)
    {
        if (!Active(c)) return;
        var d = c.Defines;
        foreach (var holder in c.Nations)
        {
            if (holder.NuclearPosture >= 3 || holder.Population <= 0 || !IsHolder(c, holder)) continue;
            var pressure = d.NuclearCoercionWarSupport * (3 - holder.NuclearPosture);
            foreach (var war in c.Wars)
            {
                if (war.Ended || !war.Involves(holder.Id)) continue;
                foreach (var enemyId in war.EnemiesOf(holder.Id))
                    if (c.NationById(enemyId) is { } enemy && !IsHolder(c, enemy))
                        enemy.WarSupport = M.Clamp01(enemy.WarSupport - pressure);
            }
        }
        if (d.AiNuclearPosture) AiWeek(c);
        // A false warning in a tense season (DEFCON 3) takes the world to the brink's antechamber,
        // never past it: 1983 was a satellite mistaking sunlight for missiles, and the man on
        // duty did not pass it on. It used to fire at DEFCON 2 as well and take the world to 1 -
        // a level reached by accident inside the first few months of most campaigns.
        if (DefconSystem.CurrentLevel(c) == 3 && MovementRules.Roll(c.Seed, 0x4E55, c.ElapsedDays, 0x1983) < d.NuclearIncidentChance)
        {
            // Two holders the incident is between: the most alarmed pair.
            var pair = c.Nations.Where(n => n.Population > 0 && IsHolder(c, n)).OrderBy(n => n.NuclearPosture).ThenBy(n => n.Id).Take(2).ToList();
            c.DefconLevel = Math.Max(2, c.DefconLevel - 1);
            c.DefconDaysInStage = 0;
            c.DefconPeakLevel = Math.Min(c.DefconPeakLevel, c.DefconLevel);
            c.Chronicle.Record(c, "nuclear_incident", pair.Count > 0 ? pair[0].Id : c.Player?.Id ?? 0,
                pair.Count > 1 ? pair[1].Id : -1, c.DefconLevel.ToString());
            c.MarkStrategicChange();
        }
    }

    /// <summary>
    /// What a computer-run holder does, by the same rules and prices as the player: raise the
    /// alert while a war against another holder or a lost war on its own ground lasts, or while a
    /// standoff with another holder is at crisis; stand down two months after the danger passes;
    /// sometimes test a weapon in a standoff; and talk on the red telephone when both are alarmed.
    /// </summary>
    private static void AiWeek(Campaign c)
    {
        var d = c.Defines;
        var holders = c.Nations.Where(n => n.Population > 0 && IsHolder(c, n)).OrderBy(n => n.Id).ToList();
        foreach (var n in holders)
        {
            if (n.IsHuman) continue;
            var wanted = 5;
            foreach (var war in c.Wars)
            {
                if (war.Ended || !war.Involves(n.Id) || war.CivilConflict) continue;
                // Losing badly, not merely behind: the score swings past -25 and back every month.
                var losing = (war.IsAttacking(n.Id) ? war.WarScore : -war.WarScore) < -40f;
                var againstHolder = war.EnemiesOf(n.Id).Any(id => IsHolder(c, c.NationById(id)));
                wanted = Math.Min(wanted, againstHolder ? 2 : losing ? 3 : 4);
            }
            var existential = Existential(c, n);
            var avenging = Avenging(c, n) is not null;
            if (existential || avenging) wanted = 1;
            foreach (var other in holders)
            {
                if (other.Id == n.Id || CampaignDiplomacy.Peek(c, n.Id, other.Id) is not { } relation) continue;
                if (relation.TensionStage >= (int)TensionStage.Clashes) wanted = Math.Min(wanted, 3);
                else if (relation.TensionStage >= (int)TensionStage.Crisis) wanted = Math.Min(wanted, 4);
                if (relation.TensionStage >= (int)TensionStage.Crisis && n.NuclearPosture <= 3 &&
                    MovementRules.Roll(c.Seed, n.Id, c.ElapsedDays, 0x7E57 + other.Id) < d.AiNuclearTestChance)
                    Apply(c, n, "test");
            }
            // Slow to move either way: a government does not flip its forces' alert every time a
            // front line wobbles, and a call on the red telephone buys a season before the next rise.
            if (n.NuclearPosture > wanted && c.ElapsedDays - n.LastHotlineDay > d.HotlineCooldownDays) Apply(c, n, "raise");
            else if (n.NuclearPosture < wanted && c.ElapsedDays - n.NuclearPostureDay >= Math.Max(90, d.NuclearPostureCooldownDays))
                Apply(c, n, "stand_down");
            // The order nobody wants to give, by the same rule as the player's: at DEFCON 1, with
            // the war lost on its own ground and the enemy standing on it.
            if (existential && n.NuclearPosture <= 1 && d.AiNuclearStrikeChance > 0f &&
                MovementRules.Roll(c.Seed, n.Id, c.ElapsedDays, 0x5741) < d.AiNuclearStrikeChance)
                Apply(c, n, "strike");
            // And the answer: a holder whose forces were struck answers in kind, or its deterrent
            // was never one.
            // A holder answering another holder answers in kind on its cities: that is what makes a
            // deterrent a deterrent (mutual assured destruction). Anybody else answers the forces.
            else if (avenging && n.NuclearPosture <= 1 && d.AiNuclearRetaliationChance > 0f &&
                MovementRules.Roll(c.Seed, n.Id, c.ElapsedDays, 0x2D5E) < d.AiNuclearRetaliationChance)
                Apply(c, n, IsHolder(c, Avenging(c, n)) && Refusal(c, n, "devastate").Length == 0 ? "devastate" : "strike");
        }
        for (var i = 0; i < holders.Count; i++)
        for (var j = i + 1; j < holders.Count; j++)
        {
            var a = holders[i]; var b = holders[j];
            if (a.IsHuman || b.IsHuman || a.NuclearPosture > 3 || b.NuclearPosture > 3 || AtWarWith(c, a.Id, b.Id)) continue;
            if ((CampaignDiplomacy.Peek(c, a.Id, b.Id)?.Opinion ?? 0f) < -60f) continue;
            // A call is about the two of them: only when their own standoff is at crisis or worse.
            if ((CampaignDiplomacy.Peek(c, a.Id, b.Id)?.TensionStage ?? 0) < (int)TensionStage.Crisis) continue;
            if (MovementRules.Roll(c.Seed, a.Id, c.ElapsedDays, 0x7E1E + b.Id) < d.AiHotlineChance) Apply(c, a, "hotline", b);
        }
    }
}

/// <summary>A holder's nuclear posture: raise, stand_down, test, or hotline to <see cref="TargetId"/>.</summary>
public sealed class NuclearPostureCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string Action { get; init; } = "";
    public int TargetId { get; init; } = -1;
    public bool Changed { get; private set; }

    public void Apply(Campaign campaign)
    {
        Changed = false;
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        // -1 is the legacy automatic target. A missing explicit id must never fall back
        // to another country merely because a save/queued order became stale.
        if (TargetId >= 0 && campaign.NationById(TargetId) is null) return;
        Changed = NuclearPosture.Apply(campaign, nation, Action, campaign.NationById(TargetId));
        if (!Changed)
            DiagnosticLogs.Write("orders", $"NUCLEAR_POSTURE_REFUSED nation={IssuerId} action={Action} reason={NuclearPosture.Refusal(campaign, nation, Action, campaign.NationById(TargetId))}");
    }
}
