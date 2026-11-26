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
    /// <summary>Active nuclear doctrine (WAR-02). Default RetaliatoryTriad preserves legacy saves.</summary>
    public NuclearDoctrine NuclearDoctrine = NuclearDoctrine.RetaliatoryTriad;
    /// <summary>Day the nuclear doctrine was last changed.</summary>
    public int NuclearDoctrineDay = -100000;
    /// <summary>Bilateral strategic arms control pacts: partner NationId -> expiry ElapsedDay.</summary>
    public Dictionary<int, int> ArmsControlPacts = new();
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
    public bool AiNuclearPosture { get; set; } = true;
    /// <summary>The weekly chance a computer-run holder in a standoff tests a weapon, when it may.</summary>
    public float AiNuclearTestChance { get; set; } = 0.02f;
    /// <summary>The weekly chance two alarmed computer-run holders pick up the red telephone.</summary>
    public float AiHotlineChance { get; set; } = 0.1f;

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
    public float AiNuclearStrikeChance { get; set; } = 0.1f;

    /// <summary>
    /// The nuclear umbrella: what attacking a country costs the attacker for each step an allied
    /// holder's own alert stands below 4. An alert raised is a guarantee made visible.
    /// </summary>
    public float AiNuclearUmbrellaPrice { get; set; } = 1f;

    /// <summary>
    /// The second strike: the days a holder whose forces were struck may answer wherever the
    /// striker's forces stand; the notoriety a first use earns (an answer earns none); and the
    /// weekly chance a computer-run holder gives the answer. A deterrent is as good as the
    /// certainty of the answer.
    /// </summary>
    public int NuclearRetaliationDays { get; set; } = 60;
    public float NuclearFirstUseNotoriety { get; set; } = 40f;
    public float AiNuclearRetaliationChance { get; set; } = 0.5f;
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

    /// <summary>WAR-02: Nuclear doctrine switching, triad maintenance upkeep, and bilateral arms control treaties.</summary>
    public float NuclearDoctrineSwitchCost { get; set; } = 15f;
    public int NuclearDoctrineCooldownDays { get; set; } = 180;
    /// <summary>Weekly strategic triad upkeep as a small bounded share of holder GDP (discounted by arms control).</summary>
    public float NuclearTriadWeeklyGdpShare { get; set; } = 0.000018f;
    public int ArmsControlPactDays { get; set; } = 1825;
    public float ArmsControlCost { get; set; } = 12f;
    public float ArmsControlUpkeepDiscount { get; set; } = 0.25f;
    public float ArmsControlTensionDrop { get; set; } = 15f;
    public bool AiArmsControlEnabled { get; set; } = true;
    public float AiArmsControlChance { get; set; } = 0.015f;
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
    public static bool IsHolder(Campaign c, Nation n) => n is not null && DefconSystem.CachedDeterrenceTier(c, n) >= 1;

    public static bool Active(Campaign c) => DefconSystem.Enabled(c) && Deterrence.IsNuclearEraEnabled(c);

    private static int EffectivePostureCooldown(Campaign c, Nation n)
    {
        var baseDays = Math.Max(0, c.Defines.NuclearPostureCooldownDays);
        return n is not null && n.NuclearDoctrine == NuclearDoctrine.EscalationToDeescalate
            ? Math.Max(1, (int)MathF.Round(baseDays * 0.67f))
            : baseDays;
    }

    /// <summary>True if <paramref name="n"/> has at least one active bilateral arms control pact.</summary>
    public static bool HasActiveArmsControl(Campaign c, Nation n)
    {
        if (c is null || n is null || n.ArmsControlPacts is null || n.ArmsControlPacts.Count == 0) return false;
        foreach (var (partnerId, expiryDay) in n.ArmsControlPacts)
        {
            if (expiryDay <= c.ElapsedDays) continue;
            var partner = c.NationById(partnerId);
            if (partner is { Population: > 0 } && IsHolder(c, partner) && !AtWarWith(c, n.Id, partnerId))
                return true;
        }
        return false;
    }

    /// <summary>True if two nuclear powers currently share an active bilateral arms control treaty.</summary>
    public static bool HasArmsControlWith(Campaign c, Nation a, Nation b)
    {
        if (c is null || a is null || b is null || a.ArmsControlPacts is null) return false;
        return a.ArmsControlPacts.TryGetValue(b.Id, out var exp) && exp > c.ElapsedDays;
    }

    /// <summary>Weekly treasury upkeep of a nuclear holder's strategic triad, adjusted by doctrine and arms control.</summary>
    public static float WeeklyTriadUpkeep(Campaign c, Nation n)
    {
        if (!Active(c) || !IsHolder(c, n) || n.Population <= 0 || n.Gdp <= 0f) return 0f;
        var share = MathF.Max(0f, c.Defines.NuclearTriadWeeklyGdpShare);
        if (share <= 0f) return 0f;
        var tier = Math.Max(1, Deterrence.TierOf(c, n));
        var upkeep = n.Gdp * share * tier * Deterrence.UpkeepMultiplier(n.NuclearDoctrine);
        if (HasActiveArmsControl(c, n))
            upkeep *= 1f - M.Clamp(c.Defines.ArmsControlUpkeepDiscount, 0f, 0.75f);
        return MathF.Max(0f, upkeep);
    }

    public static bool CanSetDoctrine(Campaign c, Nation n, NuclearDoctrine doctrine, out string reason)
    {
        reason = "";
        if (!Active(c)) { reason = Loc.Get("nuc.refuse.era_disabled"); return false; }
        if (!IsHolder(c, n)) { reason = Loc.Get("Your country holds no nuclear deterrent."); return false; }
        if (n.NuclearDoctrine == doctrine) { reason = Loc.Get("nuc.refuse.same_doctrine"); return false; }
        var cooldown = Math.Max(0, c.Defines.NuclearDoctrineCooldownDays);
        if (c.ElapsedDays - n.NuclearDoctrineDay < cooldown)
        {
            reason = Loc.Format("nuc.refuse.doctrine_cooldown", c.ElapsedDays - n.NuclearDoctrineDay, cooldown);
            return false;
        }
        var cost = MathF.Max(0f, c.Defines.NuclearDoctrineSwitchCost);
        if (n.PoliticalPoints < cost)
        {
            reason = Loc.Format("nuc.refuse.doctrine_cost", cost, n.PoliticalPoints);
            return false;
        }
        return true;
    }

    public static bool SetDoctrine(Campaign c, Nation n, NuclearDoctrine doctrine)
    {
        if (!CanSetDoctrine(c, n, doctrine, out _)) return false;
        n.PoliticalPoints -= MathF.Max(0f, c.Defines.NuclearDoctrineSwitchCost);
        n.NuclearDoctrine = doctrine;
        n.NuclearDoctrineDay = c.ElapsedDays;
        if (doctrine == NuclearDoctrine.EscalationToDeescalate)
        {
            var active = c.ActiveNations;
            for (var i = 0; i < active.Count; i++)
            {
                var other = active[i];
                if (other.Id == n.Id || other.Population <= 0 || Allied(c, n.Id, other.Id)) continue;
                var rel = CampaignDiplomacy.Peek(c, n.Id, other.Id);
                if (rel is not null && (rel.Stance == DiplomaticStance.Rival || IsHolder(c, other)))
                    rel.Tension = MathF.Min(100f, rel.Tension + 6f);
            }
        }
        else if (doctrine == NuclearDoctrine.NoFirstUse)
        {
            var active = c.ActiveNations;
            for (var i = 0; i < active.Count; i++)
            {
                var other = active[i];
                if (other.Id == n.Id || other.Population <= 0 || !IsHolder(c, other)) continue;
                var rel = CampaignDiplomacy.Peek(c, n.Id, other.Id);
                if (rel is not null && rel.Tension > 0f)
                    rel.Tension = MathF.Max(0f, rel.Tension - 4f);
            }
        }
        c.Chronicle.Record(c, "nuclear_doctrine_changed", n.Id, -1, Loc.Get(Deterrence.DoctrineNameKey(doctrine)));
        c.MarkStrategicChange();
        return true;
    }

    public static bool CanProposeArmsControl(Campaign c, Nation proposer, Nation target, out string reason)
    {
        reason = "";
        if (!Active(c)) { reason = Loc.Get("nuc.refuse.era_disabled"); return false; }
        if (!IsHolder(c, proposer) || !IsHolder(c, target)) { reason = Loc.Get("nuc.refuse.arms_holders"); return false; }
        if (proposer.Id == target.Id || proposer.Population <= 0 || target.Population <= 0)
        { reason = Loc.Get("nuc.refuse.arms_holders"); return false; }
        if (AtWarWith(c, proposer.Id, target.Id)) { reason = Loc.Get("nuc.refuse.arms_at_war"); return false; }
        if (HasArmsControlWith(c, proposer, target)) { reason = Loc.Get("nuc.refuse.arms_active"); return false; }
        var cost = MathF.Max(0f, c.Defines.ArmsControlCost);
        if (proposer.PoliticalPoints < cost)
        {
            reason = Loc.Format("nuc.refuse.arms_cost", cost, proposer.PoliticalPoints);
            return false;
        }
        return true;
    }

    public static bool TargetAcceptsArmsControl(Campaign c, Nation proposer, Nation target, out string reason)
    {
        reason = "";
        if (proposer is null || target is null) { reason = Loc.Get("nuc.refuse.arms_holders"); return false; }
        if (target.NuclearStruckBy == proposer.Id && c.ElapsedDays - target.NuclearStruckDay < 3650)
        { reason = Loc.Get("nuc.refuse.arms_hostile"); return false; }
        var rel = CampaignDiplomacy.Peek(c, target.Id, proposer.Id);
        if ((rel?.Opinion ?? 0f) < -45f)
        { reason = Loc.Get("nuc.refuse.arms_hostile"); return false; }
        if (target.NuclearDoctrine == NuclearDoctrine.EscalationToDeescalate && target.NuclearPosture <= 2)
        { reason = Loc.Get("nuc.refuse.arms_doctrine"); return false; }
        return true;
    }

    public static bool SignArmsControl(Campaign c, Nation proposer, Nation target)
    {
        if (!CanProposeArmsControl(c, proposer, target, out _)) return false;
        if (!TargetAcceptsArmsControl(c, proposer, target, out _)) return false;
        var d = c.Defines;
        proposer.PoliticalPoints -= MathF.Max(0f, d.ArmsControlCost);
        var expiry = c.ElapsedDays + Math.Max(30, d.ArmsControlPactDays);
        proposer.ArmsControlPacts ??= new Dictionary<int, int>();
        target.ArmsControlPacts ??= new Dictionary<int, int>();
        proposer.ArmsControlPacts[target.Id] = expiry;
        target.ArmsControlPacts[proposer.Id] = expiry;

        var drop = MathF.Max(0f, d.ArmsControlTensionDrop);
        if (CampaignDiplomacy.Get(c, proposer.Id, target.Id) is { } forth)
        {
            forth.Tension = MathF.Max(0f, forth.Tension - drop);
            OpinionSystem.Remember(c, forth, "nuclear_arms_control_pact");
        }
        if (CampaignDiplomacy.Get(c, target.Id, proposer.Id) is { } back)
        {
            back.Tension = MathF.Max(0f, back.Tension - drop);
            OpinionSystem.Remember(c, back, "nuclear_arms_control_pact");
        }
        c.Chronicle.Record(c, "nuclear_arms_control_signed", proposer.Id, target.Id);
        c.MarkStrategicChange();
        return true;
    }

    public static bool CancelArmsControl(Campaign c, Nation issuer, Nation target, bool dueToWar = false)
    {
        if (c is null || issuer is null || target is null) return false;
        var hadA = issuer.ArmsControlPacts is not null && issuer.ArmsControlPacts.Remove(target.Id);
        var hadB = target.ArmsControlPacts is not null && target.ArmsControlPacts.Remove(issuer.Id);
        if (!hadA && !hadB) return false;
        if (CampaignDiplomacy.Get(c, target.Id, issuer.Id) is { } back)
        {
            back.Tension = MathF.Min(100f, back.Tension + (dueToWar ? 15f : 10f));
            OpinionSystem.Remember(c, back, "nuclear_arms_control_broken");
        }
        if (CampaignDiplomacy.Get(c, issuer.Id, target.Id) is { } forth)
            forth.Tension = MathF.Min(100f, forth.Tension + (dueToWar ? 15f : 10f));
        c.Chronicle.Record(c, "nuclear_arms_control_broken", issuer.Id, target.Id);
        c.MarkStrategicChange();
        return true;
    }

    /// <summary>The most alarmed holder's posture, 5 when none has raised its alert.</summary>
    public static int Lowest(Campaign c, out Nation holder)
    {
        holder = null;
        var lowest = 5;
        if (!Active(c)) return lowest;
        var active = c.ActiveNations;
        for (var i = 0; i < active.Count; i++)
        {
            var n = active[i];
            if (n.NuclearPosture < lowest && n.Population > 0 && IsHolder(c, n)) { lowest = n.NuclearPosture; holder = n; }
        }
        return Math.Clamp(lowest, 1, 5);
    }

    /// <summary>The alert this country's forces stand at: its own posture, or the world's if that is higher.</summary>
    public static int Effective(Campaign c, Nation n) =>
        IsHolder(c, n) ? Math.Min(DefconSystem.CurrentLevel(c), Math.Clamp(n.NuclearPosture, 1, 5)) : DefconSystem.CurrentLevel(c);

    private static bool AtWarWith(Campaign c, int a, int b)
    {
        var open = Warfare.OpenWars(c);
        for (var i = 0; i < open.Count; i++)
        {
            var w = open[i];
            if (!w.Ended && w.Opposes(a, b)) return true;
        }
        return false;
    }

    /// <summary>Player-visible country targets follow the same non-civil wars as automatic
    /// targeting. Choosing a country never creates a war or bypasses readiness and cooldown.</summary>
    public static IReadOnlyList<Nation> Targets(Campaign c, Nation n) => c is null || n is null ? Array.Empty<Nation>() :
        Warfare.OpenWars(c).Where(w => !w.Ended && !w.CivilConflict && w.Involves(n.Id)).SelectMany(w => w.EnemiesOf(n.Id)).Distinct()
            .Select(c.NationById).Where(t => t is { Population: > 0 }).OrderBy(t => t.Id).ToArray();

    public static string Refusal(Campaign c, Nation n, string action, Nation target = null)
    {
        if (!Active(c)) return Loc.Get("The nuclear alert is switched off in this campaign.");
        if (!IsHolder(c, n)) return Loc.Get("Your country holds no nuclear deterrent.");
        var d = c.Defines;
        var postureCooldown = EffectivePostureCooldown(c, n);
        switch (action)
        {
            case "raise":
                if (n.NuclearPosture <= 1) return Loc.Get("The forces already stand at the highest alert.");
                if (c.ElapsedDays - n.NuclearPostureDay < postureCooldown)
                    return Loc.Format("The posture was changed {0} days ago; the next change after {1}.",
                        c.ElapsedDays - n.NuclearPostureDay, postureCooldown);
                if (n.PoliticalPoints < d.NuclearPostureRaiseCost)
                    return Loc.Format("Raising the alert costs {0:0} political credit; there is {1:0.#}.", d.NuclearPostureRaiseCost, n.PoliticalPoints);
                return "";
            case "stand_down":
                if (n.NuclearPosture >= 5) return Loc.Get("The forces are already at routine readiness.");
                if (c.ElapsedDays - n.NuclearPostureDay < postureCooldown)
                    return Loc.Format("The posture was changed {0} days ago; the next change after {1}.",
                        c.ElapsedDays - n.NuclearPostureDay, postureCooldown);
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
                if (!Deterrence.AllowsFirstUse(n.NuclearDoctrine))
                {
                    var avengedCity = Avenging(c, n);
                    if (avengedCity is null || (target is not null && avengedCity.Id != target.Id))
                        return Loc.Get("nuc.refuse.no_first_use");
                }
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
                if (!Deterrence.AllowsFirstUse(n.NuclearDoctrine))
                {
                    var avengedStrike = Avenging(c, n);
                    if (avengedStrike is null || (target is not null && avengedStrike.Id != target.Id))
                        return Loc.Get("nuc.refuse.no_first_use");
                }
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
                {
                    var active = c.ActiveNations;
                    for (var i = 0; i < active.Count; i++)
                    {
                        var other = active[i];
                        if (other.Id != n.Id && other.Population > 0 && !Allied(c, n.Id, other.Id))
                            OpinionWitnesses.Witness(c, other, n, null, "nuclear_threats");
                    }
                }
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
                var activeForTest = c.ActiveNations;
                for (var i = 0; i < activeForTest.Count; i++)
                {
                    var other = activeForTest[i];
                    if (other.Id == n.Id || other.Population <= 0) continue;
                    if (!Allied(c, n.Id, other.Id)) OpinionWitnesses.Witness(c, other, n, null, "nuclear_test");
                    var relation = CampaignDiplomacy.Peek(c, n.Id, other.Id);
                    if (relation is null) continue;
                    if (relation.Stance == DiplomaticStance.Rival || relation.TensionStage >= (int)TensionStage.Crisis)
                        relation.Tension = MathF.Min(100f, relation.Tension + d.NuclearTestTension * Deterrence.ThreatMultiplier(n.NuclearDoctrine));
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
    [ThreadStatic] private static Dictionary<int, float> _strikeWeightScratch;
    [ThreadStatic] private static HashSet<int> _existentialEnemiesScratch;
    [ThreadStatic] private static List<Nation> _weekHoldersScratch;
    [ThreadStatic] private static HashSet<int> _weekHolderIdsScratch;
    [ThreadStatic] private static HashSet<int> _batchAllEnemiesScratch;
    [ThreadStatic] private static HashSet<int> _batchPendingHoldersScratch;
    [ThreadStatic] private static Dictionary<int, (HashSet<int> Enemies, int AvengedId, int MapIndex)> _batchHolderInfoScratch;
    [ThreadStatic] private static HashSet<long> _weekWarPairsScratch;
    [ThreadStatic] private static HashSet<int> _weekNonCivilWarHoldersScratch;
    [ThreadStatic] private static HashSet<int> _weekStrikeHoldersScratch;
    [ThreadStatic] private static List<DiplomaticRelation> _weekTensionRelationsScratch;
    [ThreadStatic] private static Dictionary<int, List<(Nation Other, DiplomaticRelation Relation)>> _weekCrisisByHolderScratch;
    [ThreadStatic] private static List<List<(Nation Other, DiplomaticRelation Relation)>> _weekCrisisPoolScratch;
    [ThreadStatic] private static Dictionary<int, Nation> _holderByIdScratch;

    private sealed class SubjectHolderCache
    {
        public int Day = int.MinValue;
        public int NationCount = -1;
        public readonly List<Nation> SubjectHolders = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, SubjectHolderCache> SubjectHolderCaches = new();

    private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    internal static void FillActiveHolderTensionRelations(Campaign c, HashSet<int> holderSet, List<int> sortedHolderIds,
        List<DiplomaticRelation> into)
    {
        into.Clear();
        var holderCount = sortedHolderIds.Count;
        if (c is null || holderCount < 2) return;
        var pairChecks = (long)holderCount * (holderCount - 1) / 2;
        var relations = c.Relations;
        if (pairChecks <= relations.Count)
        {
            for (var i = 0; i < holderCount; i++)
            {
                var idA = sortedHolderIds[i];
                for (var j = i + 1; j < holderCount; j++)
                {
                    var idB = sortedHolderIds[j];
                    var key = ((long)idA << 32) | (uint)idB;
                    if (!relations.TryGetValue(key, out var rel)) continue;
                    if (rel.Tension >= 15f || rel.TensionStage >= (int)TensionStage.Crisis || rel.CrisisOpenedDay >= 0)
                        into.Add(rel);
                }
            }
            return;
        }
        foreach (var rel in relations.Values)
        {
            if (rel.Tension < 15f && rel.TensionStage < (int)TensionStage.Crisis && rel.CrisisOpenedDay < 0) continue;
            if (!holderSet.Contains(rel.A) || !holderSet.Contains(rel.B)) continue;
            into.Add(rel);
        }
        if (into.Count > 1)
            into.Sort(static (left, right) =>
            {
                var la = Math.Min(left.A, left.B);
                var ra = Math.Min(right.A, right.B);
                var cmp = la.CompareTo(ra);
                if (cmp != 0) return cmp;
                return Math.Max(left.A, left.B).CompareTo(Math.Max(right.A, right.B));
            });
    }

    internal static void FillHoldersWithStrikeTarget(Campaign c, IReadOnlyList<StrategicWar> openWars,
        HashSet<int> candidateHolderIds, HashSet<int> into)
    {
        into.Clear();
        var topology = c?.ProvinceMap;
        if (topology is null || candidateHolderIds is null || candidateHolderIds.Count == 0 || openWars.Count == 0) return;
        var allEnemies = _batchAllEnemiesScratch ??= new HashSet<int>();
        var pending = _batchPendingHoldersScratch ??= new HashSet<int>();
        var info = _batchHolderInfoScratch ??= new Dictionary<int, (HashSet<int>, int, int)>();
        allEnemies.Clear();
        pending.Clear();
        foreach (var kv in info) kv.Value.Enemies.Clear();

        for (var wi = 0; wi < openWars.Count; wi++)
        {
            var war = openWars[wi];
            if (war.Ended || war.CivilConflict) continue;
            if (candidateHolderIds.Contains(war.AttackerId)) AddWarEnemies(c, war, war.AttackerId, pending, allEnemies, info);
            if (candidateHolderIds.Contains(war.DefenderId)) AddWarEnemies(c, war, war.DefenderId, pending, allEnemies, info);
        }
        if (pending.Count == 0) return;

        var counterforce = c.Defines.NuclearCounterforce;
        var armies = c.Armies;
        var provinceCount = topology.ProvinceCount;
        var owners = topology.ProvinceToNation;
        for (var ai = 0; ai < armies.Count; ai++)
        {
            var army = armies[ai];
            if (army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 || army.ProvinceId > provinceCount ||
                army.Divisions < 0.1f || army.Personnel * army.Strength <= 0f || !allEnemies.Contains(army.NationId))
                continue;
            foreach (var holderId in pending)
            {
                if (!info.TryGetValue(holderId, out var h)) continue;
                if (!h.Enemies.Contains(army.NationId)) continue;
                if ((h.AvengedId >= 0 && army.NationId == h.AvengedId) ||
                    owners[army.ProvinceId] == h.MapIndex ||
                    counterforce)
                {
                    into.Add(holderId);
                }
            }
            if (into.Count > 0)
            {
                pending.ExceptWith(into);
                if (pending.Count == 0) break;
            }
        }
        allEnemies.Clear();
        pending.Clear();

        static void AddWarEnemies(Campaign campaign, StrategicWar war, int holderId, HashSet<int> pendingSet,
            HashSet<int> allEnemyIds, Dictionary<int, (HashSet<int> Enemies, int AvengedId, int MapIndex)> map)
        {
            var holder = campaign.NationById(holderId);
            if (holder is null) return;
            if (!map.TryGetValue(holderId, out var entry))
            {
                entry = (new HashSet<int>(), -1, 0);
                map[holderId] = entry;
            }
            if (pendingSet.Add(holderId))
            {
                var avenged = Avenging(campaign, holder);
                map[holderId] = (entry.Enemies, avenged?.Id ?? -1, holder.MapIndex);
            }
            foreach (var enemyId in war.EnemiesOf(holderId))
            {
                entry.Enemies.Add(enemyId);
                allEnemyIds.Add(enemyId);
            }
        }
    }

    public static int StrikeTarget(Campaign c, Nation n, out HashSet<int> enemies, Nation target = null)
    {
        enemies = new HashSet<int>();
        var topology = c.ProvinceMap;
        if (topology is null || n is null) return 0;
        var open = Warfare.OpenWars(c);
        for (var wi = 0; wi < open.Count; wi++)
        {
            var war = open[wi];
            if (!war.Ended && !war.CivilConflict && war.Involves(n.Id))
                foreach (var id in war.EnemiesOf(n.Id)) enemies.Add(id);
        }
        if (target is not null)
        {
            if (!enemies.Contains(target.Id)) { enemies.Clear(); return 0; }
            enemies.Clear();
            enemies.Add(target.Id);
        }
        if (enemies.Count == 0) return 0;
        // An answer to a strike falls on the striker's strongest force wherever it stands; failing
        // that, and always otherwise, the weapon defends our own soil.
        var avenged = Avenging(c, n);
        if (target is not null && avenged?.Id != target.Id) avenged = null;
        var weight = _strikeWeightScratch ??= new Dictionary<int, float>();
        var passes = c.Defines.NuclearCounterforce ? 3 : 2;
        var armies = c.Armies;
        for (var pass = avenged is null ? 1 : 0; pass < passes; pass++)
        {
            weight.Clear();
            for (var ai = 0; ai < armies.Count; ai++)
            {
                var army = armies[ai];
                if (!enemies.Contains(army.NationId) || army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 ||
                    army.ProvinceId > topology.ProvinceCount || army.Divisions < 0.1f) continue;
                if (pass == 0 ? army.NationId != avenged.Id : pass == 1 && topology.ProvinceToNation[army.ProvinceId] != n.MapIndex) continue;
                weight[army.ProvinceId] = weight.GetValueOrDefault(army.ProvinceId) + army.Personnel * army.Strength;
            }
            var best = 0;
            var bestWeight = 0f;
            foreach (var (province, value) in weight)
                if (value > bestWeight || value == bestWeight && province < best) { best = province; bestWeight = value; }
            if (best > 0) { weight.Clear(); return best; }
        }
        weight.Clear();
        return 0;
    }

    private static bool HasStrikeTarget(Campaign c, Nation n)
    {
        var topology = c.ProvinceMap;
        if (topology is null || n is null) return false;
        var enemies = _existentialEnemiesScratch ??= new HashSet<int>();
        enemies.Clear();
        var open = Warfare.OpenWars(c);
        for (var wi = 0; wi < open.Count; wi++)
        {
            var war = open[wi];
            if (!war.Ended && !war.CivilConflict && war.Involves(n.Id))
                foreach (var id in war.EnemiesOf(n.Id)) enemies.Add(id);
        }
        if (enemies.Count == 0) return false;
        var avenged = Avenging(c, n);
        var counterforce = c.Defines.NuclearCounterforce;
        var armies = c.Armies;
        for (var ai = 0; ai < armies.Count; ai++)
        {
            var army = armies[ai];
            if (army.EmbarkedFleetId >= 0 || army.ProvinceId <= 0 ||
                army.ProvinceId > topology.ProvinceCount || army.Divisions < 0.1f ||
                army.Personnel * army.Strength <= 0f || !enemies.Contains(army.NationId)) continue;
            if ((avenged is not null && army.NationId == avenged.Id) ||
                topology.ProvinceToNation[army.ProvinceId] == n.MapIndex ||
                counterforce)
            {
                enemies.Clear();
                return true;
            }
        }
        enemies.Clear();
        return false;
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
            foreach (var war in Warfare.OpenWars(c))
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
        var activeForStrike = c.ActiveNations;
        for (var i = 0; i < activeForStrike.Count; i++)
        {
            var other = activeForStrike[i];
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
        var shadowDays = c.Defines.NuclearShadowDays;
        var active = c.ActiveNations;
        for (var i = 0; i < active.Count; i++)
        {
            var n = active[i];
            if (c.ElapsedDays - n.LastNuclearStrikeDay < shadowDays &&
                (latest is null || n.LastNuclearStrikeDay > latest.LastNuclearStrikeDay)) latest = n;
        }
        return latest;
    }

    /// <summary>
    /// Defeat on its own soil: a war (not a civil one) this country is losing past -60, or one in
    /// which the enemy holds its capital - and an enemy force standing on its ground to strike.
    /// </summary>
    internal static bool Existential(Campaign c, Nation n)
    {
        if (c is null || n is null) return false;
        var controllers = c.ProvinceController;
        var capitalLost = controllers is not null && n.CapitalProvinceId > 0 && n.CapitalProvinceId < controllers.Length &&
                          controllers[n.CapitalProvinceId] != n.MapIndex;
        var open = Warfare.OpenWars(c);
        for (var wi = 0; wi < open.Count; wi++)
        {
            var war = open[wi];
            if (war.Ended || war.CivilConflict || !war.Involves(n.Id)) continue;
            if (capitalLost || (war.IsAttacking(n.Id) ? war.WarScore : -war.WarScore) < -60f)
                return HasStrikeTarget(c, n);
        }
        return false;
    }

    /// <summary>
    /// The umbrella over <paramref name="target"/>: the most alarmed holder allied to it (or that it
    /// answers to), neither the attacker nor on the attacker's side, prices a war against it by
    /// how far its alert stands below 4. A holder at routine readiness protects nobody this way -
    /// raising the alert is how a guarantee becomes believable.
    /// </summary>
    internal static void WarmUmbrellaCache(Campaign c)
    {
        if (c is null) return;
        var cache = SubjectHolderCaches.GetOrCreateValue(c);
        if (cache.Day == c.ElapsedDays && cache.NationCount == c.ActiveNations.Count) return;
        lock (cache)
        {
            if (cache.Day == c.ElapsedDays && cache.NationCount == c.ActiveNations.Count) return;
            cache.SubjectHolders.Clear();
            var active = c.ActiveNations;
            for (var i = 0; i < active.Count; i++)
            {
                var candidate = active[i];
                if (candidate.IsSubject && candidate.Population > 0 && IsHolder(c, candidate))
                    cache.SubjectHolders.Add(candidate);
            }
            cache.NationCount = active.Count;
            cache.Day = c.ElapsedDays;
        }
    }

    public static float UmbrellaPrice(Campaign c, Nation attacker, Nation target, out Nation patron)
    {
        patron = null;
        if (!Active(c) || attacker is null || target is null || c.Defines.AiNuclearUmbrellaPrice <= 0f) return 0f;
        var bestScore = 0f;
        Nation bestPatron = null;
        var allies = c.Caches.Protectors.AlliesOf(c, target.Id);
        for (var i = 0; i < allies.Length; i++)
            ConsiderUmbrellaHolder(c, attacker, target, c.NationById(allies[i]), ref bestScore, ref bestPatron);
        if (target.IsSubject && target.OverlordId >= 0)
            ConsiderUmbrellaHolder(c, attacker, target, c.NationById(target.OverlordId), ref bestScore, ref bestPatron);
        WarmUmbrellaCache(c);
        var cache = SubjectHolderCaches.GetOrCreateValue(c);
        var shList = cache.SubjectHolders;
        for (var i = 0; i < shList.Count; i++)
        {
            var sh = shList[i];
            if (sh.AnswersTo(target.Id))
                ConsiderUmbrellaHolder(c, attacker, target, sh, ref bestScore, ref bestPatron);
        }
        patron = bestPatron;
        return bestScore * c.Defines.AiNuclearUmbrellaPrice;
    }

    private static void ConsiderUmbrellaHolder(Campaign c, Nation attacker, Nation target, Nation holder,
        ref float bestScore, ref Nation bestPatron)
    {
        if (holder is null || holder.NuclearPosture >= 4 || holder.Id == attacker.Id || holder.Id == target.Id || holder.Population <= 0) return;
        if (Allied(c, holder.Id, attacker.Id) || !IsHolder(c, holder)) return;
        var score = (4 - holder.NuclearPosture) * Deterrence.UmbrellaMultiplier(holder.NuclearDoctrine);
        if (score > bestScore) { bestScore = score; bestPatron = holder; }
    }

    private static bool Allied(Campaign c, int a, int b) =>
        CampaignDiplomacy.Peek(c, a, b) is { Stance: DiplomaticStance.Allied } ||
        (c.NationById(a)?.AnswersTo(b) ?? false) || (c.NationById(b)?.AnswersTo(a) ?? false);

    private static void PruneArmsControlPacts(Campaign c)
    {
        List<int> expired = null;
        var active = c.ActiveNations;
        for (var i = 0; i < active.Count; i++)
        {
            var nation = active[i];
            if (nation.ArmsControlPacts is null || nation.ArmsControlPacts.Count == 0) continue;
            expired?.Clear();
            foreach (var (partnerId, expiryDay) in nation.ArmsControlPacts)
            {
                var partner = c.NationById(partnerId);
                if (partner is null || partner.Population <= 0 || nation.Population <= 0 || expiryDay <= c.ElapsedDays)
                {
                    (expired ??= new List<int>()).Add(partnerId);
                    continue;
                }
                if (AtWarWith(c, nation.Id, partnerId))
                    (expired ??= new List<int>()).Add(-partnerId - 1);
            }
            if (expired is null || expired.Count == 0) continue;
            foreach (var entry in expired)
            {
                if (entry >= 0) nation.ArmsControlPacts.Remove(entry);
                else
                {
                    var partnerId = -entry - 1;
                    var partner = c.NationById(partnerId);
                    if (partner is not null) CancelArmsControl(c, nation, partner, dueToWar: true);
                    else nation.ArmsControlPacts.Remove(partnerId);
                }
            }
        }
    }

    /// <summary>
    /// The week: triad upkeep, arms control maintenance, coercion felt by every enemy of an alarmed
    /// holder, the computer's governments' own decisions, and at the brink the incidents nobody chose.
    /// Runs on the DEFCON review day.
    /// </summary>
    public static void Week(Campaign c)
    {
        foreach (var _ in WeekStages(c)) { }
    }

    public static IEnumerable<int> WeekStages(Campaign c)
    {
        if (!Active(c)) yield break;
        var d = c.Defines;
        var liveWars = Warfare.LiveWars(c);
        PruneArmsControlPacts(c);
        var holders = _weekHoldersScratch ??= new List<Nation>(16);
        var holderIds = _weekHolderIdsScratch ??= new HashSet<int>(16);
        holders.Clear();
        holderIds.Clear();
        var nations = c.ActiveNations;
        for (var i = 0; i < nations.Count; i++)
        {
            var nation = nations[i];
            if (nation.Population <= 0 || !IsHolder(c, nation)) continue;
            holders.Add(nation);
            holderIds.Add(nation.Id);
        }
        for (var hi = 0; hi < holders.Count; hi++)
        {
            var holder = holders[hi];
            var upkeep = WeeklyTriadUpkeep(c, holder);
            if (upkeep > 0f)
            {
                holder.Treasury -= upkeep;
                MoneyLedger.Book(holder, MoneyFlow.AlertUpkeep, -upkeep);
            }
            if (holder.NuclearPosture >= 3) continue;
            var pressure = d.NuclearCoercionWarSupport * (3 - holder.NuclearPosture) *
                Deterrence.CoercionMultiplier(holder.NuclearDoctrine);
            for (var wi = 0; wi < liveWars.Count; wi++)
            {
                var war = liveWars[wi];
                if (war.Ended || !war.Involves(holder.Id)) continue;
                foreach (var enemyId in war.EnemiesOf(holder.Id))
                    if (c.NationById(enemyId) is { } enemy && !holderIds.Contains(enemy.Id))
                        enemy.WarSupport = M.Clamp01(enemy.WarSupport - pressure);
            }
        }
        yield return 0;
        if (d.AiNuclearPosture)
            foreach (var _ in AiWeekStages(c, liveWars, holders, holderIds))
                yield return 0;
        // A false warning in a tense season (DEFCON 3) takes the world to the brink's antechamber,
        // never past it: 1983 was a satellite mistaking sunlight for missiles, and the man on
        // duty did not pass it on. It used to fire at DEFCON 2 as well and take the world to 1 -
        // a level reached by accident inside the first few months of most campaigns.
        if (DefconSystem.CurrentLevel(c) == 3 && MovementRules.Roll(c.Seed, 0x4E55, c.ElapsedDays, 0x1983) < d.NuclearIncidentChance)
        {
            // Two holders the incident is between: the most alarmed pair.
            Nation first = null, second = null;
            for (var hi = 0; hi < holders.Count; hi++)
            {
                var candidate = holders[hi];
                if (first is null || candidate.NuclearPosture < first.NuclearPosture ||
                    (candidate.NuclearPosture == first.NuclearPosture && candidate.Id < first.Id))
                {
                    second = first;
                    first = candidate;
                }
                else if (second is null || candidate.NuclearPosture < second.NuclearPosture ||
                    (candidate.NuclearPosture == second.NuclearPosture && candidate.Id < second.Id))
                {
                    second = candidate;
                }
            }
            c.DefconLevel = Math.Max(2, c.DefconLevel - 1);
            c.DefconDaysInStage = 0;
            c.DefconPeakLevel = Math.Min(c.DefconPeakLevel, c.DefconLevel);
            c.Chronicle.Record(c, "nuclear_incident", first?.Id ?? c.Player?.Id ?? 0,
                second?.Id ?? -1, c.DefconLevel.ToString());
            c.MarkStrategicChange();
        }
        holders.Clear();
        holderIds.Clear();
    }

    /// <summary>
    /// What a computer-run holder does, by the same rules and prices as the player: raise the
    /// alert while a war against another holder or a lost war on its own ground lasts, or while a
    /// standoff with another holder is at crisis; stand down two months after the danger passes;
    /// sometimes test a weapon in a standoff; and talk on the red telephone when both are alarmed.
    /// </summary>
    private static IEnumerable<int> AiWeekStages(Campaign c, IReadOnlyList<StrategicWar> liveWars,
        List<Nation> holders, HashSet<int> holderIds)
    {
        var d = c.Defines;
        holders.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        var warPairs = _weekWarPairsScratch ??= new HashSet<long>();
        var warHolders = _weekNonCivilWarHoldersScratch ??= new HashSet<int>();
        var candidateStrikeHolders = _batchPendingHoldersScratch ??= new HashSet<int>();
        var strikeHolders = _weekStrikeHoldersScratch ??= new HashSet<int>();
        warPairs.Clear();
        warHolders.Clear();
        candidateStrikeHolders.Clear();
        strikeHolders.Clear();
        var controllers = c.ProvinceController;
        for (var wi = 0; wi < liveWars.Count; wi++)
        {
            var war = liveWars[wi];
            if (war.Ended) continue;
            if (holderIds.Contains(war.AttackerId))
            {
                if (!war.CivilConflict) warHolders.Add(war.AttackerId);
                foreach (var eid in war.EnemiesOf(war.AttackerId))
                    if (holderIds.Contains(eid)) warPairs.Add(PairKey(war.AttackerId, eid));
            }
            if (holderIds.Contains(war.DefenderId))
            {
                if (!war.CivilConflict) warHolders.Add(war.DefenderId);
                foreach (var eid in war.EnemiesOf(war.DefenderId))
                    if (holderIds.Contains(eid)) warPairs.Add(PairKey(war.DefenderId, eid));
            }
        }
        if (warHolders.Count > 0)
        {
            for (var hi = 0; hi < holders.Count; hi++)
            {
                var h = holders[hi];
                if (!warHolders.Contains(h.Id)) continue;
                var capLost = controllers is not null && h.CapitalProvinceId > 0 && h.CapitalProvinceId < controllers.Length &&
                              controllers[h.CapitalProvinceId] != h.MapIndex;
                var losing60 = false;
                if (!capLost)
                {
                    for (var wi = 0; wi < liveWars.Count; wi++)
                    {
                        var w = liveWars[wi];
                        if (!w.Ended && !w.CivilConflict && w.Involves(h.Id) &&
                            (w.IsAttacking(h.Id) ? w.WarScore : -w.WarScore) < -60f)
                        { losing60 = true; break; }
                    }
                }
                if (capLost || losing60) candidateStrikeHolders.Add(h.Id);
            }
            if (candidateStrikeHolders.Count > 0)
                FillHoldersWithStrikeTarget(c, liveWars, candidateStrikeHolders, strikeHolders);
            candidateStrikeHolders.Clear();
        }

        var tenseRelations = _weekTensionRelationsScratch ??= new List<DiplomaticRelation>();
        FillActiveHolderTensionRelations(c, holderIds, BuildSortedIds(holders), tenseRelations);
        var holderById = _holderByIdScratch ??= new Dictionary<int, Nation>();
        holderById.Clear();
        for (var hi = 0; hi < holders.Count; hi++) holderById[holders[hi].Id] = holders[hi];

        var crisisByHolder = _weekCrisisByHolderScratch ??= new Dictionary<int, List<(Nation Other, DiplomaticRelation Relation)>>();
        var crisisPool = _weekCrisisPoolScratch ??= new List<List<(Nation Other, DiplomaticRelation Relation)>>();
        foreach (var list in crisisByHolder.Values) { list.Clear(); crisisPool.Add(list); }
        crisisByHolder.Clear();
        for (var ri = 0; ri < tenseRelations.Count; ri++)
        {
            var rel = tenseRelations[ri];
            if (rel.TensionStage < (int)TensionStage.Crisis) continue;
            if (!holderById.TryGetValue(rel.A, out var na) || !holderById.TryGetValue(rel.B, out var nb)) continue;
            AddCrisisPartner(crisisByHolder, crisisPool, na.Id, nb, rel);
            AddCrisisPartner(crisisByHolder, crisisPool, nb.Id, na, rel);
        }
        foreach (var list in crisisByHolder.Values)
            if (list.Count > 1) list.Sort(static (l, r) => l.Other.Id.CompareTo(r.Other.Id));

        for (var idx = 0; idx < holders.Count; idx++)
        {
            if (idx > 0 && (idx % 4 == 0 || Campaign.StageOverdue())) yield return 0;
            var n = holders[idx];
            if (n.IsHuman) continue;
            var wanted = 5;
            if (warHolders.Contains(n.Id))
            {
                for (var wi = 0; wi < liveWars.Count; wi++)
                {
                    var war = liveWars[wi];
                    if (war.Ended || !war.Involves(n.Id) || war.CivilConflict) continue;
                    var losing = (war.IsAttacking(n.Id) ? war.WarScore : -war.WarScore) < -40f;
                    var againstHolder = false;
                    foreach (var enemyId in war.EnemiesOf(n.Id))
                        if (holderIds.Contains(enemyId)) { againstHolder = true; break; }
                    wanted = Math.Min(wanted, againstHolder ? 2 : losing ? 3 : 4);
                }
            }
            var existential = strikeHolders.Contains(n.Id);
            var avenging = Avenging(c, n) is not null;
            if (existential || avenging) wanted = 1;
            if (crisisByHolder.TryGetValue(n.Id, out var partners))
            {
                for (var oi = 0; oi < partners.Count; oi++)
                {
                    var (other, relation) = partners[oi];
                    if (relation.TensionStage >= (int)TensionStage.Clashes) wanted = Math.Min(wanted, 3);
                    else if (relation.TensionStage >= (int)TensionStage.Crisis) wanted = Math.Min(wanted, 4);
                    if (relation.TensionStage >= (int)TensionStage.Crisis && n.NuclearPosture <= 3 &&
                        MovementRules.Roll(c.Seed, n.Id, c.ElapsedDays, 0x7E57 + other.Id) < d.AiNuclearTestChance)
                        Apply(c, n, "test");
                }
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
        yield return 0;
        for (var ti = 0; ti < tenseRelations.Count; ti++)
        {
            if (ti > 0 && (ti % 8 == 0 || Campaign.StageOverdue())) yield return 0;
            var rel = tenseRelations[ti];
            if (rel.TensionStage < (int)TensionStage.Crisis || rel.Opinion < -60f) continue;
            var loId = Math.Min(rel.A, rel.B);
            var hiId = Math.Max(rel.A, rel.B);
            if (!holderById.TryGetValue(loId, out var a) || !holderById.TryGetValue(hiId, out var b)) continue;
            if (a.IsHuman || b.IsHuman || a.NuclearPosture > 3 || b.NuclearPosture > 3 || warPairs.Contains(PairKey(a.Id, b.Id))) continue;
            if (MovementRules.Roll(c.Seed, a.Id, c.ElapsedDays, 0x7E1E + b.Id) < d.AiHotlineChance) Apply(c, a, "hotline", b);
        }
        if (d.AiArmsControlEnabled && d.AiArmsControlChance > 0f)
        {
            for (var ti = 0; ti < tenseRelations.Count; ti++)
            {
                var rel = tenseRelations[ti];
                if (rel.Tension < 15f || rel.Opinion < -20f) continue;
                var loId = Math.Min(rel.A, rel.B);
                var hiId = Math.Max(rel.A, rel.B);
                if (!holderById.TryGetValue(loId, out var a) || !holderById.TryGetValue(hiId, out var b)) continue;
                if (a.IsHuman || b.IsHuman || warPairs.Contains(PairKey(a.Id, b.Id)) || HasArmsControlWith(c, a, b)) continue;
                if (MovementRules.Roll(c.Seed, a.Id, c.ElapsedDays, 0x4152 + b.Id) < d.AiArmsControlChance)
                    SignArmsControl(c, a, b);
            }
        }
        tenseRelations.Clear();
        holderById.Clear();
        warPairs.Clear();
        warHolders.Clear();
        strikeHolders.Clear();

        static List<int> BuildSortedIds(List<Nation> list)
        {
            var ids = _weekSortedHolderIdsScratch ??= new List<int>(list.Count);
            ids.Clear();
            for (var i = 0; i < list.Count; i++) ids.Add(list[i].Id);
            return ids;
        }

        static void AddCrisisPartner(Dictionary<int, List<(Nation Other, DiplomaticRelation Relation)>> map,
            List<List<(Nation Other, DiplomaticRelation Relation)>> pool, int id, Nation other, DiplomaticRelation relation)
        {
            if (!map.TryGetValue(id, out var list))
            {
                if (pool.Count > 0) { list = pool[^1]; pool.RemoveAt(pool.Count - 1); }
                else list = new List<(Nation, DiplomaticRelation)>(4);
                map[id] = list;
            }
            list.Add((other, relation));
        }
    }

    [ThreadStatic] private static List<int> _weekSortedHolderIdsScratch;
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

/// <summary>Switch a nuclear holder's strategic doctrine (WAR-02) via lockstep command.</summary>
public sealed class SetNuclearDoctrineCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public NuclearDoctrine Doctrine { get; init; } = NuclearDoctrine.RetaliatoryTriad;
    public bool Changed { get; private set; }

    public void Apply(Campaign campaign)
    {
        Changed = false;
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        Changed = NuclearPosture.SetDoctrine(campaign, nation, Doctrine);
        if (!Changed)
        {
            NuclearPosture.CanSetDoctrine(campaign, nation, Doctrine, out var reason);
            DiagnosticLogs.Write("orders", $"NUCLEAR_DOCTRINE_REFUSED nation={IssuerId} doctrine={Doctrine} reason={reason}");
        }
    }
}

/// <summary>Propose or withdraw from a bilateral strategic arms control treaty between nuclear holders (WAR-02).</summary>
public sealed class NuclearArmsControlCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; } = -1;
    public bool Propose { get; init; } = true;
    public bool Changed { get; private set; }

    public void Apply(Campaign campaign)
    {
        Changed = false;
        if (!MechanicFeatures.Allows(campaign, this)) return;
        if (!NuclearPosture.Active(campaign)) return;
        var issuer = campaign.NationById(IssuerId);
        var target = campaign.NationById(TargetId);
        if (issuer is null || target is null) return;
        if (Propose)
        {
            Changed = NuclearPosture.SignArmsControl(campaign, issuer, target);
            if (!Changed)
            {
                var reason = "";
                if (NuclearPosture.CanProposeArmsControl(campaign, issuer, target, out reason))
                    NuclearPosture.TargetAcceptsArmsControl(campaign, issuer, target, out reason);
                DiagnosticLogs.Write("orders", $"ARMS_CONTROL_REFUSED nation={IssuerId} target={TargetId} reason={reason}");
            }
        }
        else
        {
            Changed = NuclearPosture.CancelArmsControl(campaign, issuer, target, dueToWar: false);
        }
    }
}
