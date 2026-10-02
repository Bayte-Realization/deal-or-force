using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Who a war has killed, wounded and taken, and what becomes of the ones it took.
///
/// A war used to keep one number per side - shown as "dead", and made of everybody a battle
/// cost, the wounded included - and nothing else. What a pocket did to the men inside it was
/// in none of it. The strength a cut-off formation lost every day came off the formation and
/// out of the world without being counted anywhere, and when the pocket finally went, the
/// formation was removed with everybody still in it. So closing a pocket, the one manoeuvre
/// that ought to show in a war's butcher's bill, cost the side inside it nothing anybody could
/// see.
///
/// Now every man a war takes is one of three things, and the third is new:
///
///   killed     gone
///   wounded    most of them go back to their formation once treated (WoundedReturningShare)
///   captured   held by the other side, and sent home by an exchange or by the peace
///
/// The totals on the war keep their meaning - the war score and home support read them - so
/// what has changed there is only that a pocket now puts its losses in.
/// </summary>
public static class WarLosses
{
    /// <summary>Put one side's losses on the war, broken down.</summary>
    public static void Record(StrategicWar war, int nationId, float killed, float wounded, float captured)
    {
        if (war is null) return;
        killed = MathF.Max(0f, killed);
        wounded = MathF.Max(0f, wounded);
        captured = MathF.Max(0f, captured);
        var total = killed + wounded + captured;
        if (total <= 0f) return;

        if (war.IsAttacking(nationId))
        {
            war.AttackerLosses += total;
            war.AttackerKilled += killed;
            war.AttackerWounded += wounded;
            war.AttackerCaptured += captured;
        }
        else
        {
            war.DefenderLosses += total;
            war.DefenderKilled += killed;
            war.DefenderWounded += wounded;
            war.DefenderCaptured += captured;
        }
        if (captured > 0f) war.Prisoners[nationId] = war.Prisoners.GetValueOrDefault(nationId) + captured;
    }

    /// <summary>
    /// Wounded who died because nobody could reach them. Already counted as losses, so the
    /// total does not move: they change column.
    /// </summary>
    public static void DiedOfWounds(StrategicWar war, int nationId, float men)
    {
        if (war is null || men <= 0f) return;
        if (war.IsAttacking(nationId))
        {
            men = MathF.Min(men, war.AttackerWounded);
            war.AttackerWounded -= men;
            war.AttackerKilled += men;
        }
        else
        {
            men = MathF.Min(men, war.DefenderWounded);
            war.DefenderWounded -= men;
            war.DefenderKilled += men;
        }
    }

    /// <summary>
    /// The war a formation is losing men in. The one it is posted to first; failing that the
    /// war against whoever holds the ground it is standing on, which for a pocket is the
    /// country that closed it; failing that any war its country is fighting.
    /// </summary>
    public static StrategicWar WarOf(Campaign campaign, Army army)
    {
        if (campaign is null || army is null) return null;
        StrategicWar fallback = null;
        var holder = -1;
        var controllers = campaign.ProvinceController;
        if (controllers is not null && army.ProvinceId > 0 && army.ProvinceId < controllers.Length)
        {
            var map = controllers[army.ProvinceId];
            foreach (var nation in campaign.Nations)
                if (nation.MapIndex == map) { holder = nation.Id; break; }
        }
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(army.NationId)) continue;
            if (war.Id == army.AssignedWarId) return war;
            if (holder >= 0 && war.Opposes(army.NationId, holder)) fallback = war;
            else fallback ??= war;
        }
        return fallback;
    }

    /// <summary>
    /// A formation that surrenders: the pocket that has collapsed, or the formation beaten with
    /// nowhere to fall back to. A share die in the last of it and the rest are taken, together
    /// with the wounded it was carrying - who are already on the war's books as wounded, and so
    /// move from that column to this one rather than being counted twice.
    /// </summary>
    public static void Surrender(Campaign campaign, Army army)
    {
        var war = WarOf(campaign, army);
        if (war is null) return;
        var defines = campaign.Defines;
        var standing = MathF.Max(0f, army.Divisions * MilitaryBootstrap.DivisionSize * army.Strength);
        var killed = standing * M.Clamp01(defines.SurrenderKilledShare);
        Record(war, army.NationId, killed, 0f, standing - killed);

        var carried = MathF.Max(0f, army.Recovering);
        if (carried <= 0f) return;
        if (war.IsAttacking(army.NationId))
        {
            carried = MathF.Min(carried, war.AttackerWounded);
            war.AttackerWounded -= carried;
            war.AttackerCaptured += carried;
        }
        else
        {
            carried = MathF.Min(carried, war.DefenderWounded);
            war.DefenderWounded -= carried;
            war.DefenderCaptured += carried;
        }
        war.Prisoners[army.NationId] = war.Prisoners.GetValueOrDefault(army.NationId) + carried;
    }

    /// <summary>Men of one country the other side holds in this war.</summary>
    public static float HeldFrom(StrategicWar war, int nationId) =>
        war?.Prisoners.GetValueOrDefault(nationId) ?? 0f;

    /// <summary>
    /// Everybody still held in a war that is over goes home. Once a day, over every war rather
    /// than at each of the places a war can end - a peace treaty, a subjugation, a country that
    /// stopped existing - because a prisoner forgotten by one of them would be held for ever.
    /// </summary>
    public static void Repatriate(Campaign campaign)
    {
        foreach (var war in campaign.Wars)
        {
            if (!war.Ended || war.Prisoners.Count == 0) continue;
            foreach (var pair in war.Prisoners)
                Release(campaign, pair.Key, pair.Value, war, "peace");
            war.Prisoners.Clear();
        }
    }

    /// <summary>
    /// One for one: as many of each side's men as the smaller of the two holds. Both governments
    /// get the same number back, which is why belligerents who will agree on nothing else still
    /// agree on this.
    /// </summary>
    public static float Exchange(Campaign campaign, StrategicWar war, int first, int second)
    {
        if (campaign is null || war is null || war.Ended || !war.Opposes(first, second)) return 0f;
        var men = MathF.Min(HeldFrom(war, first), HeldFrom(war, second));
        if (men < MathF.Max(1f, campaign.Defines.PrisonerExchangeMinimum)) return 0f;
        foreach (var side in new[] { first, second })
        {
            war.Prisoners[side] = HeldFrom(war, side) - men;
            if (war.Prisoners[side] < 1f) war.Prisoners.Remove(side);
            Release(campaign, side, men, war, "exchange");
        }
        war.LastPrisonerExchangeDay = campaign.ElapsedDays;
        return men;
    }

    /// <summary>Why an exchange cannot happen now, or empty when it can.</summary>
    public static string ExchangeRefusal(Campaign campaign, StrategicWar war, int first, int second)
    {
        if (war is null || war.Ended || !war.Opposes(first, second)) return "Only two sides of the same war can exchange prisoners.";
        var minimum = MathF.Max(1f, campaign.Defines.PrisonerExchangeMinimum);
        if (HeldFrom(war, first) < minimum) return "The enemy holds none of our men.";
        if (HeldFrom(war, second) < minimum) return "We hold none of theirs to offer.";
        var wait = war.LastPrisonerExchangeDay < 0 ? 0 :
            war.LastPrisonerExchangeDay + campaign.Defines.PrisonerExchangeIntervalDays - campaign.ElapsedDays;
        if (wait > 0) return Loc.Format("The last exchange was too recent; the next can be arranged in {0} day(s).", wait);
        return "";
    }

    private static void Release(Campaign campaign, int nationId, float men, StrategicWar war, string how)
    {
        if (men <= 0f) return;
        var nation = campaign.NationById(nationId);
        if (nation is null || nation.Population <= 0) return;
        var home = men * M.Clamp01(campaign.Defines.PrisonersRepatriatedShare);
        nation.ManpowerReserve += home;
        DiagnosticLogs.Write("simulation",
            $"PRISONERS_RELEASED war={war.Id} nation={nation.Tag} held={DiagnosticLogs.N(men)} home={DiagnosticLogs.N(home)} by={how}");
    }
}

/// <summary>
/// Offer the other side a prisoner exchange. One for one, as many as the smaller side holds,
/// and at most once a month - see WarLosses.Exchange.
/// </summary>
public sealed class ExchangePrisonersCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int WarId { get; init; }
    public int CounterpartId { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var war = campaign.Wars.Find(candidate => candidate.Id == WarId);
        if (war is null || war.Ended || !war.Involves(IssuerId)) return;
        var refusal = WarLosses.ExchangeRefusal(campaign, war, IssuerId, CounterpartId);
        if (refusal.Length > 0) { war.LastNegotiation = Loc.Get(refusal); return; }
        var men = WarLosses.Exchange(campaign, war, IssuerId, CounterpartId);
        var counterpart = campaign.NationById(CounterpartId);
        war.LastNegotiation = Loc.Format("{0} agreed to an exchange: {1} prisoners on each side are going home.",
            counterpart?.DisplayName ?? "", men.ToString("N0"));
    }
}
