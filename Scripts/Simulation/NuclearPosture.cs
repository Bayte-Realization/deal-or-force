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

    public static bool Active(Campaign c) => c is not null && c.Defines.DefconEnabled;

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
                // who is not on its side.
                if (n.NuclearPosture <= 2)
                    foreach (var other in c.Nations)
                        if (other.Id != n.Id && other.Population > 0 && !Allied(c, n.Id, other.Id) &&
                            CampaignDiplomacy.Get(c, other.Id, n.Id) is { } relation)
                            OpinionSystem.Remember(c, relation, "nuclear_threats");
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
                foreach (var other in c.Nations)
                {
                    if (other.Id == n.Id || other.Population <= 0) continue;
                    var relation = CampaignDiplomacy.Get(c, n.Id, other.Id);
                    if (relation is null) continue;
                    if (!Allied(c, n.Id, other.Id)) OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, other.Id, n.Id), "nuclear_test");
                    if (relation.Stance == DiplomaticStance.Rival || relation.TensionStage >= (int)TensionStage.Crisis)
                        relation.Tension = MathF.Min(100f, relation.Tension + d.NuclearTestTension);
                }
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
        if (DefconSystem.CurrentLevel(c) <= 2 && MovementRules.Roll(c.Seed, 0x4E55, c.ElapsedDays, 0x1983) < d.NuclearIncidentChance)
        {
            // Two holders the incident is between: the most alarmed pair.
            var pair = c.Nations.Where(n => n.Population > 0 && IsHolder(c, n)).OrderBy(n => n.NuclearPosture).ThenBy(n => n.Id).Take(2).ToList();
            c.DefconLevel = Math.Max(1, c.DefconLevel - 1);
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
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        Changed = NuclearPosture.Apply(campaign, nation, Action, campaign.NationById(TargetId));
        if (!Changed)
            DiagnosticLogs.Write("orders", $"NUCLEAR_POSTURE_REFUSED nation={IssuerId} action={Action} reason={NuclearPosture.Refusal(campaign, nation, Action, campaign.NationById(TargetId))}");
    }
}
