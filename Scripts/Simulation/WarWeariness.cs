using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>A war this old starts to weigh on both governments: every year after it adds this much to what they will accept, up to the cap.</summary>
    public int PeaceWearinessAfterDays { get; set; } = 450;
    public float PeaceWearinessPerYear { get; set; } = 15f;
    public float PeaceWearinessCap { get; set; } = 45f;
    /// <summary>A war whose score has moved less than this over half a year is standing still.</summary>
    public float WarStalledScore { get; set; } = 6f;
    /// <summary>After this long a side that is not winning a war that stands still asks for a white peace, and after the second, anyone does.</summary>
    public int AiStalemateDays { get; set; } = 450;
    public int AiWearyDays { get; set; } = 900;
    /// <summary>A war with nobody's player in it ends of exhaustion at this age: each side keeps the enemy ground it holds.</summary>
    public int PeaceForcedEndDays { get; set; } = 1825;
    /// <summary>An AI war with no battle and no province ever contested ends in white peace after this many days. Zero disables it.</summary>
    public int AiNoContactPeaceDays { get; set; } = 365;
    /// <summary>Earlier white peace for an AI war with no front and no land or naval route on either side. Zero disables the early check.</summary>
    public int AiUnreachableWarPeaceDays { get; set; } = 180;
    /// <summary>At that end, a lead of this much war score keeps its ground; less is a white peace.</summary>
    public float PeaceForcedEndLead { get; set; } = 15f;
    /// <summary>A war score this decided lifts the lock a scenario puts on its opening wars' negotiations.</summary>
    public float AiDecisiveWarScore { get; set; } = 80f;
    /// <summary>A side this far behind, or with this much of its worth occupied, is losing the war - and then collapsing.</summary>
    public float WarGoingBadlyScore { get; set; } = -30f;
    public float WarGoingBadlyOccupied { get; set; } = 0.2f;
    public float FrontCollapsingScore { get; set; } = -80f;
    public float FrontCollapsingOccupied { get; set; } = 0.5f;
    /// <summary>A people's young men and women coming of age, as a share of the population a year; the most a country can have in reserve.</summary>
    public float ManpowerGrowthPerYear { get; set; } = 0.0015f;
    public float MobilisationCeilingShare { get; set; } = 0.05f;

    /// <summary>
    /// Days of replacements - at the country's own daily capacity to send them forward - kept
    /// back to refill the formations it has rather than spent raising new ones. Without it
    /// Ukraine doubled its army to 116 divisions in five months, had nobody left to replace a
    /// single loss, and collapsed in the sixth. Measured in its own replacements, not in its
    /// people: a quarter of the mobilisation ceiling was 250,000 men for Ukraine, which no
    /// reserve at war ever reached again, so it raised nothing after the first winter.
    /// </summary>
    public float ReplacementReserveDays { get; set; } = 60f;

    /// <summary>Average organisation from which a force at war attacks in full (WarCore.Readiness).</summary>
    public float AiOffensiveReadyOrganisation { get; set; } = 0.55f;
    /// <summary>Average organisation at which it stops attacking until it has recovered.</summary>
    public float AiOffensiveExhaustedOrganisation { get; set; } = 0.30f;
    /// <summary>How much more of the army goes forward in a side's offensive season, and a two-fifths share of it less in its lull (WarCore.OffensivePulse); 0 is the same pressure every day.</summary>
    public float AiOffensivePulseStrength { get; set; } = 0.6f;
    /// <summary>How much faster men are called up by a country at war whose own ground is being taken.</summary>
    public float MobilisationMultiplier { get; set; } = 4f;
    /// <summary>How deep into debt a country being overrun goes to raise an army, as a share of its GDP.</summary>
    public float WarDebtLimitShare { get; set; } = 0.4f;
}

public sealed partial class StrategicWar
{
    /// <summary>The war score half a year ago, the day it was read, and whether it has moved since.</summary>
    public float ScoreBack;
    public int ScoreBackDay = -1;
    public bool Stalled;
}

/// <summary>
/// No war goes on for ever.
///
/// They used to: the side that was not winning only asked for peace once its people had given
/// up on the war, which in a country that was not losing badly never happened; the side that
/// was winning on points and holding none of the other's ground kept pushing because stopping
/// would win it nothing; and what either would accept stopped growing with the war's length
/// after a year and a half. So a war with no breakthrough simply never ended.
///
/// Three things end it now. A war that has stood still for half a year after its second year is
/// a stalemate, and the side not winning it asks for a white peace; after the fourth year anyone
/// does. Every year after the second makes both governments readier to accept terms at all -
/// the weariness of a long war is on both sides. And a war with no player in it that reaches its
/// eighth year ends of exhaustion: whoever is clearly ahead keeps what it securely holds, drawn
/// into a border (PeaceShape), and otherwise it is a white peace. A player's war is never ended
/// for them - but the other side becomes ever readier to sign.
/// </summary>
public static class WarWeariness
{
    /// <summary>Derived fatigue, not a second stored meter. Only a belligerent can be
    /// weary of its current war; support is the existing national willingness to fight.</summary>
    public static float NationalFatigue(Campaign campaign, Nation nation)
    {
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nation.Id)) return M.Clamp01(1f - nation.WarSupport);
        return 0f;
    }
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(WarWeariness))) return;
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) return;
        var defines = campaign.Defines;
        Mobilise(campaign);
        foreach (var war in campaign.Wars.ToList())
        {
            if (war.Ended) continue;
            // How it is going, felt at home: a side losing badly stops believing in it, and a side
            // whose front is collapsing - or half of whose country is under the enemy - stops
            // believing in anything. It is what lets a beaten country bring itself to sign.
            foreach (var attacking in new[] { true, false })
            {
                var score = attacking ? war.WarScore : -war.WarScore;
                var occupied = attacking ? war.AttackerOccupiedValue : war.DefenderOccupiedValue;
                var mood = HomeFrontMood(defines, score, occupied);
                if (mood is null) continue;
                foreach (var id in war.Side(attacking))
                    if (campaign.NationById(id) is { } side && side.Movement is null)
                        ModifierSystem.Grant(campaign, side, mood, defines.EconomyTickDays * 2);
            }

            // The weight of it: a long war is felt at home - in the support for it, in how settled
            // the country is and in what it makes - and the longer the heavier.
            var age = campaign.ElapsedDays - war.StartedDay;
            if (NoContactPeaceReady(campaign, war))
            {
                PeaceNegotiation.Resolve(campaign, war, war.AttackerId, PeaceTerms.WhitePeace);
                if (war.Ended) continue;
            }
            var weight = LengthWeight(defines, age);
            if (weight is not null)
            {
                foreach (var id in war.Side(true).Concat(war.Side(false)))
                    if (campaign.NationById(id) is { } fighting && fighting.Movement is null)
                        ModifierSystem.Grant(campaign, fighting, weight, defines.EconomyTickDays * 2);
            }

            // Anti-war exhaustion & civil pressure when public support collapses
            foreach (var id in war.Side(true).Concat(war.Side(false)))
            {
                if (campaign.NationById(id) is { } fighting && fighting.Movement is null)
                {
                    var fatigue = NationalFatigue(campaign, fighting);
                    if (fatigue > 0.65f)
                    {
                        ModifierSystem.Grant(campaign, fighting, "anti_war_protests", defines.EconomyTickDays * 2);
                        if ((campaign.ElapsedDays + fighting.Id * 13) % 45 == 0)
                            campaign.Chronicle.Record(campaign, "anti_war_protests", fighting.Id);
                    }
                }
            }
            // Whether it has moved: read every half year.
            if (war.ScoreBackDay < 0) { war.ScoreBack = war.WarScore; war.ScoreBackDay = campaign.ElapsedDays; }
            else if (campaign.ElapsedDays - war.ScoreBackDay >= 182)
            {
                war.Stalled = MathF.Abs(war.WarScore - war.ScoreBack) < defines.WarStalledScore;
                war.ScoreBack = war.WarScore;
                war.ScoreBackDay = campaign.ElapsedDays;
            }

            if (!ExhaustionPeaceDue(campaign, war)) continue;
            var proposer = war.WarScore >= 0f ? war.AttackerId : war.DefenderId;
            PeaceNegotiation.Resolve(campaign, war, proposer, StalemateTerms(campaign, war));
            if (war.Ended) war.PeaceSummary = Loc.Format("Ended of exhaustion after {0} years: {1}",
                (campaign.ElapsedDays - war.StartedDay) / 365, war.PeaceSummary);
        }
    }

    /// <summary>
    /// Men and women coming of age, and called up. The reserve every country started with used to
    /// be all it would ever have - Ukraine had drafted its last man by the end of the first year -
    /// so each week a small share of the population comes of age into it, up to a ceiling, and a
    /// country at war whose own ground is being taken calls its people up several times as fast.
    /// </summary>
    private static void Mobilise(Campaign campaign)
    {
        var defines = campaign.Defines;
        var overrun = new HashSet<int>();
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            if (war.AttackerOccupiedValue > 0.02f) foreach (var id in war.Side(true)) overrun.Add(id);
            if (war.DefenderOccupiedValue > 0.02f) foreach (var id in war.Side(false)) overrun.Add(id);
        }
        foreach (var nation in campaign.Nations)
        {
            if (nation.Movement is not null || nation.Population <= 0) continue;
            var ceiling = nation.Population * defines.MobilisationCeilingShare;
            if (nation.ManpowerReserve >= ceiling) continue;
            var grown = nation.Population * defines.ManpowerGrowthPerYear / 52f;
            if (overrun.Contains(nation.Id)) grown *= defines.MobilisationMultiplier * MathF.Max(0.25f, nation.WarSupport);
            nation.ManpowerReserve = MathF.Min(ceiling, nation.ManpowerReserve + grown);
        }
    }

    /// <summary>Whether a country can raise one more formation and still keep its replacement reserve.</summary>
    public static bool MayRaise(Campaign campaign, Nation nation)
    {
        var keep = MathF.Max(0f, nation.ReinforcementCapacity) * MathF.Max(0f, campaign.Defines.ReplacementReserveDays);
        return nation.ManpowerReserve - MilitaryBootstrap.DivisionSize >= keep;
    }

    /// <summary>Whether this country is at war and has lost ground to the enemy - fighting for its life.</summary>
    public static bool Overrun(Campaign campaign, Nation nation)
    {
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            var occupied = war.IsAttacking(nation.Id) ? war.AttackerOccupiedValue : war.DefenderOccupiedValue;
            if (occupied > 0.02f) return true;
        }
        return false;
    }

    /// <summary>Whether any country on either side is somebody's.</summary>
    public static bool PlayedByAnyone(Campaign campaign, StrategicWar war) =>
        war.Side(true).Concat(war.Side(false)).Any(id => campaign.NationById(id)?.IsHuman == true);

    /// <summary>
    /// A declaration that never became a fight. This is deliberately narrower than a
    /// stalemate: even an unopposed province capture is territorial progress, and a war
    /// involving a player must remain their decision. The scenario's peace lock applies.
    /// </summary>
    public static bool NoContactPeaceReady(Campaign campaign, StrategicWar war)
    {
        if (campaign is null || war is null || war.Ended || campaign.Defines.AiNoContactPeaceDays <= 0 ||
            campaign.ElapsedDays < war.AiNegotiationUnlockDay || PlayedByAnyone(campaign, war) ||
            war.AttackerLosses + war.DefenderLosses >= .001f || war.OriginalController.Count > 0)
            return false;
        if (campaign.Battles.Any(battle => !battle.Ended && battle.WarId == war.Id)) return false;
        var age = campaign.ElapsedDays - war.StartedDay;
        if (age >= campaign.Defines.AiNoContactPeaceDays) return true;
        if (campaign.Defines.AiUnreachableWarPeaceDays <= 0 ||
            age < campaign.Defines.AiUnreachableWarPeaceDays ||
            war.AttackerFront.Count > 0 || war.DefenderFront.Count > 0) return false;

        // Check every belligerent, not only the two leaders: a coalition member might
        // still have a viable land or amphibious route even when its leader does not.
        foreach (var attackerId in war.Side(true))
        foreach (var defenderId in war.Side(false))
        {
            var attacker = campaign.NationById(attackerId);
            var defender = campaign.NationById(defenderId);
            if (attacker is null || defender is null) continue;
            if (AiStatecraft.CanStageWar(campaign, attacker, defender) ||
                AiStatecraft.CanStageWar(campaign, defender, attacker)) return false;
        }
        return true;
    }

    /// <summary>What the length of the war adds to what either side will accept.</summary>
    public static float Weariness(Campaign campaign, StrategicWar war)
    {
        var defines = campaign.Defines;
        var age = campaign.ElapsedDays - war.StartedDay;
        if (age >= defines.PeaceForcedEndDays && !PlayedByAnyone(campaign, war)) return 100f;
        var years = MathF.Max(0f, age - defines.PeaceWearinessAfterDays) / 365f;
        return MathF.Min(defines.PeaceWearinessCap, years * defines.PeaceWearinessPerYear);
    }

    /// <summary>
    /// The peace a deadlock ends in: the status quo when either side holds some of the other's
    /// ground - the line becomes the border - and a white peace when nobody holds anything.
    /// Holding is control. A province the army has taken is not handed back because its
    /// occupation meter has not yet crossed the secure line.
    /// </summary>
    public static PeaceTerms StalemateTerms(Campaign campaign, StrategicWar war)
    {
        var controllers = campaign.ProvinceController;
        if (controllers is null) return PeaceTerms.WhitePeace;
        foreach (var pair in war.OriginalController)
            if (pair.Key > 0 && pair.Key < controllers.Length && controllers[pair.Key] != pair.Value)
                return PeaceTerms.StatusQuo;
        return PeaceTerms.WhitePeace;
    }

    /// <summary>Whether a country that is not winning, or anyone at all, should ask to stop now.</summary>
    public static bool TimeToStop(Campaign campaign, StrategicWar war, bool winning)
    {
        var defines = campaign.Defines;
        var age = campaign.ElapsedDays - war.StartedDay;
        // A larger war and a civil war take longer to give up on (Escalation).
        var delay = Escalation.StalemateDelay(campaign, war);
        if (age >= defines.AiWearyDays + delay) return true;
        return !winning && war.Stalled && age >= defines.AiStalemateDays + delay;
    }

    /// <summary>The home-front modifier Tick grants a side, or null when the front is holding.</summary>
    public static string HomeFrontMood(Defines defines, float score, float occupied)
    {
        if (defines is null) return null;
        if (score <= defines.FrontCollapsingScore || occupied >= defines.FrontCollapsingOccupied)
            return "front_collapsing";
        if (score <= defines.WarGoingBadlyScore || occupied >= defines.WarGoingBadlyOccupied)
            return "war_going_badly";
        return null;
    }

    /// <summary>The length modifier Tick grants both sides, or null before the war is long.</summary>
    public static string LengthWeight(Defines defines, int age)
    {
        if (defines is null || age < defines.PeaceWearinessAfterDays) return null;
        return age >= defines.AiWearyDays ? "exhausted_by_war" : "long_war";
    }

    /// <summary>
    /// The same faster call-up <see cref="Mobilise"/> applies once this country's own ground
    /// is being taken and it still has room under the mobilisation ceiling.
    /// </summary>
    public static bool FasterCallUp(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || nation.Movement is not null || nation.Population <= 0) return false;
        if (!Overrun(campaign, nation)) return false;
        var ceiling = nation.Population * campaign.Defines.MobilisationCeilingShare;
        return nation.ManpowerReserve < ceiling;
    }

    /// <summary>The exhaustion end Tick writes when nobody being played is in the war.</summary>
    public static bool ExhaustionPeaceDue(Campaign campaign, StrategicWar war)
    {
        if (campaign is null || war is null || war.Ended) return false;
        if (campaign.ElapsedDays - war.StartedDay < campaign.Defines.PeaceForcedEndDays) return false;
        return !PlayedByAnyone(campaign, war) && war.PendingOfferFromId < 0;
    }

    /// <summary>Whether Tick would end this war itself: no contact, or exhaustion with no player in it.</summary>
    public static bool PeaceCanBeForced(Campaign campaign, StrategicWar war) =>
        NoContactPeaceReady(campaign, war) || ExhaustionPeaceDue(campaign, war);

    /// <summary>
    /// The war-weariness sentences already decided for this country. Empty at peace.
    /// Reading them does not grant a modifier, call anyone up, or end the war.
    /// </summary>
    public static string Explain(Campaign campaign, Nation nation) => Wear(campaign, nation).Text;

    /// <summary>
    /// What belongs beside the war-support percent: the weariness sentences, and one coup
    /// line when <see cref="Coups.Notice"/> already has one. Defections expose no read-only
    /// line, so nothing here predicts a desertion.
    /// </summary>
    public static (string Text, int Level) BesideWarSupport(Campaign campaign, Nation nation)
    {
        var wear = Wear(campaign, nation);
        var coup = Coups.Notice(campaign, nation);
        if (coup.Text.Length == 0) return wear;
        if (wear.Text.Length == 0) return coup;
        return (wear.Text + "\n" + coup.Text, wear.Level > coup.Level ? wear.Level : coup.Level);
    }

    static (string Text, int Level) Wear(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || nation.Movement is not null) return ("", 0);
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(WarWeariness))) return ("", 0);
        var defines = campaign.Defines;
        string mood = null;
        string length = null;
        var forced = false;
        var atWar = false;
        var wars = campaign.Wars;
        for (var i = 0; i < wars.Count; i++)
        {
            var war = wars[i];
            if (war is null || war.Ended || !war.Involves(nation.Id)) continue;
            atWar = true;
            var attacking = war.IsAttacking(nation.Id);
            var score = attacking ? war.WarScore : -war.WarScore;
            var occupied = attacking ? war.AttackerOccupiedValue : war.DefenderOccupiedValue;
            var nextMood = HomeFrontMood(defines, score, occupied);
            if (Rank(nextMood) > Rank(mood)) mood = nextMood;
            var nextLength = LengthWeight(defines, campaign.ElapsedDays - war.StartedDay);
            if (Rank(nextLength) > Rank(length)) length = nextLength;
            if (PeaceCanBeForced(campaign, war)) forced = true;
        }
        if (!atWar) return ("", 0);

        var lines = new List<string>(4);
        var level = 0;
        void Add(string line, int rank)
        {
            if (line.Length == 0) return;
            lines.Add(line);
            if (rank > level) level = rank;
        }
        Add(Sentence(mood switch
        {
            "front_collapsing" => "The front is collapsing",
            "war_going_badly" => "The war is going badly",
            _ => ""
        }), mood == "front_collapsing" ? 2 : 1);
        Add(Sentence(length switch
        {
            "exhausted_by_war" => "Exhausted by war",
            "long_war" => "A long war",
            _ => ""
        }), 1);
        if (FasterCallUp(campaign, nation))
            Add(Sentence("Its own ground is being taken, so call-up is running faster."), 1);
        if (forced) Add(Sentence("A peace can be forced."), 1);
        return lines.Count == 0 ? ("", 0) : (string.Join("\n", lines), level);
    }

    static int Rank(string mark) => mark switch
    {
        "front_collapsing" or "exhausted_by_war" => 2,
        "war_going_badly" or "long_war" => 1,
        _ => 0
    };

    static string Sentence(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        var text = Loc.Get(key);
        if (text.Length == 0) return "";
        var end = text[text.Length - 1];
        return end is '.' or '!' or '?' or '…' ? text : text + ".";
    }
}
