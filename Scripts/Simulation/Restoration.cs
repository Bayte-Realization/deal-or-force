using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The least time, in days, between two restoration proclamations by one government.</summary>
    public int RestorationCooldownDays { get; set; } = 180;
    /// <summary>What the proclamation itself costs, in billions - offices, documents, the move home.</summary>
    public float RestorationCost { get; set; } = 0.02f;
    /// <summary>How warmly the country holding the homeland must think of the exiled government to hand it back.</summary>
    public float RestorationConsentOpinion { get; set; } = 35f;
}

/// <summary>
/// The ground a country lost, brought back under its own flag.
///
/// A country that loses the last of its ground either goes into exile or is gone
/// (<see cref="MovementFall"/>). An exile keeps everything that made it that country - its tag,
/// its name, its colour, its government, the peoples it is for - and holds no ground anywhere,
/// so there is no second country of that name for the restoration to stand beside. What it
/// lacks is the ground. This is the way back: it is restored to its own homeland - the ground
/// the world first saw it holding, and the titles it kept there
/// (<see cref="TerritorialClaims.TitleOf"/>) - and stands again as the country it was.
///
/// Two ways home, and both are a real transfer rather than a renaming:
/// a military return, when the government already stands on the region's ground, and a
/// negotiated return, when the country holding it thinks well enough of the exile to give it
/// back. Either way the region changes owner through the same <see cref="StatePartition"/> and
/// resource accounting every other border change uses, so its people, its output and its debts
/// are counted once and under one flag - never duplicated, never conjured.
///
/// A restoration is not a claim and not a conquest: it needs the ground, and it needs the
/// country to be genuinely gone, so a state that already stands is not "restored" a second
/// time - it takes the rest of its homeland back the way any country does.
/// </summary>
public static class Restoration
{
    /// <summary>Whether the region is the country's own remembered or authored homeland.</summary>
    public static bool IsHomeland(Campaign campaign, Nation nation, ScenarioState region) =>
        campaign is not null && nation is not null && region is not null && !region.Unclaimed &&
        TerritorialClaims.TitleOf(campaign, nation, region) == TerritorialTitle.Homeland;

    /// <summary>Whether the exiled government actually stands on the region's ground.</summary>
    public static bool Controls(Campaign campaign, Nation nation, ScenarioState region)
    {
        var topology = campaign?.ProvinceMap;
        var control = campaign?.ProvinceController;
        if (topology is null || control is null || nation is null || region is null) return false;
        for (var province = 1; province <= topology.ProvinceCount && province < control.Length; province++)
            if (topology.ProvinceToState[province] == (ushort)region.MapIndex &&
                control[province] == (ushort)nation.MapIndex) return true;
        return false;
    }

    /// <summary>The country that governs the region now, if a real one does.</summary>
    public static Nation Holder(Campaign campaign, ScenarioState region) =>
        region is null ? null : campaign.Nations.Find(n => n.MapIndex == region.NationMapIndex);

    /// <summary>Whether the holder would give the region back: a friend, at peace, not the exile itself.</summary>
    public static bool Consents(Campaign campaign, Nation nation, Nation holder) =>
        campaign is not null && nation is not null && holder is not null && holder.Id != nation.Id &&
        holder.Movement is null && !CampaignDiplomacy.AtWar(campaign, nation.Id, holder.Id) &&
        CampaignDiplomacy.Get(campaign, holder.Id, nation.Id).Opinion >= campaign.Defines.RestorationConsentOpinion;

    /// <summary>Why the country cannot be restored to this region now; empty when it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, ScenarioState region)
    {
        var state = nation?.Movement;
        if (state is null || state.Dissolved) return Loc.Get("Only a country that still has a government can be restored.");
        if (!state.Exile) return Loc.Get("This is not a government in exile.");
        if (region is null) return Loc.Get("Choose a region first.");
        if (!IsHomeland(campaign, nation, region))
            return Loc.Get("A country can only be restored to its own homeland.");
        if (region.NationMapIndex == nation.MapIndex) return Loc.Get("That ground is already ours.");
        // No active duplicate: the country must not already stand anywhere under its own name.
        if (campaign.HasOwnedLand(nation))
            return Loc.Get("The country already stands on its own ground.");
        if (campaign.Nations.Any(other => other.Id != nation.Id &&
                string.Equals(other.Tag, nation.Tag, StringComparison.OrdinalIgnoreCase) && campaign.HasOwnedLand(other)))
            return Loc.Get("Another country already bears this name.");
        if (!float.IsFinite(region.Stability) || !float.IsFinite(region.StandardOfLiving) ||
            !float.IsFinite(region.Literacy) || !float.IsFinite(nation.Gdp))
            return Loc.Get("Invalid simulation values prevent this action. Pause and inspect the campaign.");
        var holder = Holder(campaign, region);
        if (holder is not null && !float.IsFinite(holder.Gdp))
            return Loc.Get("Invalid simulation values prevent this action. Pause and inspect the campaign.");
        if (!Controls(campaign, nation, region) && !Consents(campaign, nation, holder))
            return Loc.Get("The holder will not hand it back, and we do not hold it.");
        return "";
    }

    public static bool CanRestore(Campaign campaign, Nation nation, ScenarioState region) =>
        Refusal(campaign, nation, region).Length == 0;

    /// <summary>The largest piece of its own homeland the country can be restored to now, or nothing.</summary>
    public static ScenarioState BestRegion(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return null;
        ScenarioState best = null;
        foreach (var region in campaign.States)
        {
            if (!CanRestore(campaign, nation, region)) continue;
            if (best is null || region.Population > best.Population ||
                region.Population == best.Population && region.MapIndex < best.MapIndex) best = region;
        }
        return best;
    }

    /// <summary>Bring the country home: the region becomes its own again, with its people and output.</summary>
    public static bool Return(Campaign campaign, Nation nation, ScenarioState region)
    {
        if (Refusal(campaign, nation, region).Length != 0) return false;
        var holder = Holder(campaign, region);
        // A return the exile does not already stand on is one the holder agreed to.
        var negotiated = !Controls(campaign, nation, region);
        if (!MovementRules.TakeRegion(campaign, nation, region, holder, restoration: true)) return false;
        nation.Movement.LastReport = Loc.Format("The government has returned to {0}: {1} stands again.",
            region.DisplayName, nation.Name);
        campaign.Chronicle.Record(campaign, "restoration", nation.Id, holder?.Id ?? -1, place: region.DisplayName);
        if (negotiated && holder is not null)
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, holder.Id, nation.Id), "restored_us");
        campaign.MarkStrategicChange();
        campaign.MarkMapChanged();
        return true;
    }
}
