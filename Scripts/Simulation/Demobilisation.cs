using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// The share of a decommissioned fleet's hull price that comes back to the treasury: scrap,
    /// equipment taken ashore, ships sold on. What has been built is mostly spent.
    /// </summary>
    public float FleetDecommissionRefund { get; set; } = 0.12f;
}

/// <summary>
/// Sending forces home: a division stood down, a fleet decommissioned.
///
/// There was no way for a player to do either. A government that wanted a smaller army could
/// only watch it, and a navy could not be laid up at all - while the computer's own countries
/// stood their unpaid surplus down by a rule of their own (Attrition). Both are orders now, the
/// same for every country: the computer's surplus goes home through StandDown as a player's does.
///
/// A division stood down is not a division lost: its men go back to the manpower reserve
/// (Defines.ArmyStandDownReturn of them - the rest leave the forces for good) and nothing is
/// counted as a casualty. A fleet decommissioned leaves the navy; a share of what its hulls cost
/// comes back (Defines.FleetDecommissionRefund) and the hulls still on the slipways for it are
/// cancelled. Neither is possible in the middle of a fight, nor with anyone aboard.
/// </summary>
public static class Demobilisation
{
    /// <summary>Why a division cannot be stood down now, or empty when it can.</summary>
    public static string ArmyRefusal(Campaign campaign, Nation nation, Army army)
    {
        if (army is null || nation is null || army.NationId != nation.Id) return Loc.Get("That formation is not yours.");
        if (army.InBattle) return Loc.Format("{0} is in action.", army.Name);
        if (army.EmbarkedFleetId >= 0) return Loc.Format("{0} is aboard a ship. Put it ashore first.", army.Name);
        if (army.LentById >= 0) return Loc.Format("{0} is serving abroad as volunteers.", army.Name);
        return "";
    }

    /// <summary>One division home: its men to the reserve, nothing counted as lost.</summary>
    public static void StandDown(Campaign campaign, Nation nation, Army army, string reason)
    {
        nation.ManpowerReserve += army.Personnel * army.Strength * campaign.Defines.ArmyStandDownReturn;
        var name = army.Name;
        Attrition.Destroy(campaign, army, "stood down", lost: false);
        campaign.Chronicle.Record(campaign, "army_stood_down", nation.Id, subject: name, place: reason, isPublic: false);
        DiagnosticLogs.Write("simulation", $"STAND_DOWN nation={nation.Tag} army={army.Id} day={campaign.ElapsedDays} reason={reason}");
        campaign.MarkStrategicChange();
    }

    /// <summary>Why a fleet cannot be decommissioned now, or empty when it can.</summary>
    public static string FleetRefusal(Campaign campaign, Nation nation, Fleet fleet)
    {
        if (fleet is null || nation is null || fleet.NationId != nation.Id) return Loc.Get("That fleet is not yours.");
        if (fleet.Carried.Count > 0) return Loc.Get("Troops are aboard. Put them ashore first.");
        if (fleet.InAction) return Loc.Get("The fleet is in action.");
        return "";
    }

    /// <summary>The fleet leaves the navy: a share of its price back, its slipways cancelled.</summary>
    public static float Decommission(Campaign campaign, Nation nation, Fleet fleet)
    {
        var refund = MathF.Max(0f, fleet.Hulls * campaign.Defines.HullCost * MathF.Max(0f, campaign.Defines.FleetDecommissionRefund));
        nation.Treasury += refund;
        campaign.SeaLifts.RemoveAll(lift => lift.FleetId == fleet.Id);
        campaign.Fleets.Remove(fleet);
        DiagnosticLogs.Write("simulation", $"DECOMMISSION nation={nation.Tag} fleet={fleet.Id} hulls={fleet.Hulls} refund={refund:0.000} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
        return refund;
    }
}

/// <summary>Stand divisions down: their men go back to the reserve (Demobilisation).</summary>
public sealed class DisbandArmiesCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public IReadOnlyList<int> ArmyIds { get; init; } = Array.Empty<int>();

    /// <summary>How many went home, and why the first that could not did not.</summary>
    public int StoodDown { get; private set; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) { Refusal = Loc.Get("No country."); return; }
        foreach (var id in ArmyIds.Distinct().ToArray())
        {
            var army = campaign.Armies.Find(candidate => candidate.Id == id);
            var why = Demobilisation.ArmyRefusal(campaign, nation, army);
            if (why.Length > 0) { if (Refusal.Length == 0) Refusal = why; continue; }
            Demobilisation.StandDown(campaign, nation, army, "ordered");
            StoodDown++;
        }
    }
}

/// <summary>Take a fleet out of the navy (Demobilisation).</summary>
public sealed class DecommissionFleetCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int FleetId { get; init; }

    public string Refusal { get; private set; } = "";
    public float Refund { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var fleet = campaign.Fleets.Find(candidate => candidate.Id == FleetId);
        var why = Demobilisation.FleetRefusal(campaign, nation, fleet);
        if (why.Length > 0) { Refusal = why; return; }
        Refund = Demobilisation.Decommission(campaign, nation, fleet);
    }
}
