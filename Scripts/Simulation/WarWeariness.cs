using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>A war this old starts to weigh on both governments: every year after it adds this much to what they will accept, up to the cap.</summary>
    public int PeaceWearinessAfterDays { get; set; } = 730;
    public float PeaceWearinessPerYear { get; set; } = 12f;
    public float PeaceWearinessCap { get; set; } = 40f;
    /// <summary>A war whose score has moved less than this over half a year is standing still.</summary>
    public float WarStalledScore { get; set; } = 8f;
    /// <summary>After this long a side that is not winning a war that stands still asks for a white peace, and after the second, anyone does.</summary>
    public int AiStalemateDays { get; set; } = 730;
    public int AiWearyDays { get; set; } = 1460;
    /// <summary>A war with nobody's player in it ends of exhaustion at this age: each side keeps what it holds securely.</summary>
    public int PeaceForcedEndDays { get; set; } = 2920;
    /// <summary>At that end, a lead of this much war score keeps its ground; less is a white peace.</summary>
    public float PeaceForcedEndLead { get; set; } = 15f;
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
    public static void Tick(Campaign campaign)
    {
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) return;
        var defines = campaign.Defines;
        foreach (var war in campaign.Wars.ToList())
        {
            if (war.Ended) continue;
            // The weight of it: a long war is felt at home - in the support for it, in how settled
            // the country is and in what it makes - and the longer the heavier.
            var age = campaign.ElapsedDays - war.StartedDay;
            if (age >= defines.PeaceWearinessAfterDays)
            {
                var weight = age >= defines.AiWearyDays ? "exhausted_by_war" : "long_war";
                foreach (var id in war.Side(true).Concat(war.Side(false)))
                    if (campaign.NationById(id) is { } fighting && fighting.Movement is null)
                        ModifierSystem.Grant(campaign, fighting, weight, defines.EconomyTickDays * 2);
            }
            // Whether it has moved: read every half year.
            if (war.ScoreBackDay < 0) { war.ScoreBack = war.WarScore; war.ScoreBackDay = campaign.ElapsedDays; }
            else if (campaign.ElapsedDays - war.ScoreBackDay >= 182)
            {
                war.Stalled = MathF.Abs(war.WarScore - war.ScoreBack) < defines.WarStalledScore;
                war.ScoreBack = war.WarScore;
                war.ScoreBackDay = campaign.ElapsedDays;
            }

            if (campaign.ElapsedDays - war.StartedDay < defines.PeaceForcedEndDays) continue;
            if (PlayedByAnyone(campaign, war) || war.PendingOfferFromId >= 0) continue;
            var proposer = war.WarScore >= 0f ? war.AttackerId : war.DefenderId;
            PeaceNegotiation.Resolve(campaign, war, proposer, StalemateTerms(campaign, war));
            if (war.Ended) war.PeaceSummary = Loc.Format("Ended of exhaustion after {0} years: {1}",
                (campaign.ElapsedDays - war.StartedDay) / 365, war.PeaceSummary);
        }
    }

    /// <summary>Whether any country on either side is somebody's.</summary>
    public static bool PlayedByAnyone(Campaign campaign, StrategicWar war) =>
        war.Side(true).Concat(war.Side(false)).Any(id => campaign.NationById(id)?.IsHuman == true);

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
    /// ground securely - the line becomes the border - and a white peace when nobody holds anything.
    /// </summary>
    public static PeaceTerms StalemateTerms(Campaign campaign, StrategicWar war)
    {
        var occupation = campaign.ProvinceOccupation;
        var controllers = campaign.ProvinceController;
        if (occupation is null || controllers is null) return PeaceTerms.WhitePeace;
        foreach (var pair in war.OriginalController)
            if (controllers[pair.Key] != pair.Value && occupation[pair.Key] >= campaign.Defines.OccupationSecureThreshold)
                return PeaceTerms.StatusQuo;
        return PeaceTerms.WhitePeace;
    }

    /// <summary>Whether a country that is not winning, or anyone at all, should ask to stop now.</summary>
    public static bool TimeToStop(Campaign campaign, StrategicWar war, bool winning)
    {
        var defines = campaign.Defines;
        var age = campaign.ElapsedDays - war.StartedDay;
        if (age >= defines.AiWearyDays) return true;
        return !winning && war.Stalled && age >= defines.AiStalemateDays;
    }
}
