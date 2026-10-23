using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>What merging two subject administrations costs in political points.</summary>
    public float SubjectConsolidationPoliticalCost { get; set; } = 6f;
    /// <summary>Extra price for every region the absorbed administration brings.</summary>
    public float SubjectConsolidationCostPerRegion { get; set; } = 1.5f;
    /// <summary>How long an overlord waits before another administrative merger.</summary>
    public int SubjectConsolidationCooldownDays { get; set; } = 180;
    /// <summary>How many regions one AI land grant may move at a time.</summary>
    public int AiSubjectGrantMaxRegions { get; set; } = 3;
}

public readonly record struct SubjectMerge(Nation Keep, Nation Absorb, float Price);

/// <summary>
/// Turning a crowd of tiny dependencies into one administration.
///
/// A colonial power with six one-region puppets is not running six policies; it is running
/// one frontier badly. This layer merges adjacent subjects of the same overlord into the
/// larger of the two, keeps the surviving administration's subject status, moves its armies,
/// treasury, production and people with the land, and records the act. It is deliberately
/// separate from annexation: the overlord still does not own the ground, it owns a bigger
/// client.
/// </summary>
public static class SubjectBlocs
{
    public static IEnumerable<SubjectMerge> Candidates(Campaign campaign, Nation owner)
    {
        if (campaign?.ProvinceMap is null || owner is null) yield break;
        var subjects = SubjectSystem.SubjectsOf(campaign, owner)
            .Where(subject => !campaign.Fallen.Contains(subject.Id) && campaign.StatesOf(subject).Length > 0)
            .ToArray();
        for (var i = 0; i < subjects.Length; i++)
            for (var j = i + 1; j < subjects.Length; j++)
            {
                var a = subjects[i];
                var b = subjects[j];
                var keep = a.Population >= b.Population ? a : b;
                var absorb = ReferenceEquals(keep, a) ? b : a;
                if (!CanMerge(campaign, owner, keep, absorb, out var price, out _)) continue;
                yield return new SubjectMerge(keep, absorb, price);
            }
    }

    /// <summary>The best merger that involves this subject, from either side of the pair.</summary>
    public static SubjectMerge? BestFor(Campaign campaign, Nation owner, Nation subject)
    {
        if (campaign is null || owner is null || subject is null) return null;
        foreach (var pair in Candidates(campaign, owner)
                     .Where(pair => pair.Keep.Id == subject.Id || pair.Absorb.Id == subject.Id)
                     .OrderBy(pair => pair.Absorb.Population))
            return pair;
        return null;
    }

    public static bool CanMerge(Campaign campaign, Nation owner, Nation keep, Nation absorb,
        out float price, out string refusal)
    {
        price = 0f;
        refusal = "";
        if (campaign?.ProvinceMap is null || owner is null || keep is null || absorb is null ||
            keep.Id == absorb.Id) { refusal = Loc.Get("Choose two different subjects."); return false; }
        if (!keep.AnswersTo(owner.Id) || !absorb.AnswersTo(owner.Id))
        { refusal = Loc.Get("Both administrations must answer to the same overlord."); return false; }
        if (campaign.Fallen.Contains(keep.Id) || campaign.Fallen.Contains(absorb.Id) ||
            keep.SubjectIntegrating || absorb.SubjectIntegrating ||
            keep.SubjectConspiring || absorb.SubjectConspiring)
        { refusal = Loc.Get("End integration or a conspiracy before merging administrations."); return false; }
        if (CampaignDiplomacy.AtWar(campaign, owner.Id, keep.Id) ||
            CampaignDiplomacy.AtWar(campaign, owner.Id, absorb.Id) ||
            CampaignDiplomacy.AtWar(campaign, keep.Id, absorb.Id))
        { refusal = Loc.Get("Administrations at war cannot be merged."); return false; }
        if (RegionalAdministration.IsMicroCityEnclave(keep) ||
            RegionalAdministration.IsMicroCityEnclave(absorb))
        { refusal = Loc.Get("A micro-territory cannot be merged into a regional administration."); return false; }
        if (campaign.Wars.Any(war => !war.Ended && war.Involves(absorb.Id) && !war.Involves(keep.Id)))
        { refusal = Loc.Get("An administration fighting its own war cannot be dissolved into another."); return false; }

        var keepRegions = campaign.StatesOf(keep);
        var absorbRegions = campaign.StatesOf(absorb);
        if (keepRegions.Length == 0 || absorbRegions.Length == 0)
        { refusal = Loc.Get("A landless administration cannot be merged."); return false; }
        if (keep.Population < absorb.Population)
        { refusal = Loc.Get("The larger administration must be the one that survives."); return false; }

        var touch = absorbRegions.Any(region =>
            TerritorialCoherence.TouchesOwnedGround(campaign, keep, region.MapIndex));
        if (!touch)
        { refusal = Loc.Get("The two administrations must share a border."); return false; }

        var affinity = PeacefulFormation.CulturalAffinity(keep, absorb);
        if (affinity < .5f && absorbRegions.Length > 2)
        { refusal = Loc.Get("Only small or kindred administrations can be merged."); return false; }

        price = campaign.Defines.SubjectConsolidationPoliticalCost +
                Math.Max(0f, absorbRegions.Length - 1) * campaign.Defines.SubjectConsolidationCostPerRegion;
        if (!float.IsFinite(price) || price < 0f)
        { refusal = Loc.Get("Invalid administrative merger price."); return false; }
        if (!float.IsFinite(owner.PoliticalPoints) || owner.PoliticalPoints < price)
        { refusal = Loc.Format("Requires {0:0.#} political points.", price); return false; }

        var key = MergeKey(keep, absorb);
        if (owner.DecisionAvailableDay.GetValueOrDefault(key) > campaign.ElapsedDays)
        { refusal = Loc.Get("The administrations are still reorganising after a merger."); return false; }
        return true;
    }

    public static string MergeKey(Nation keep, Nation absorb) =>
        "subject_merge/" + (keep?.Id ?? -1) + "/" + (absorb?.Id ?? -1);

    public static bool Merge(Campaign campaign, Nation owner, Nation keep, Nation absorb, out string feedback)
    {
        feedback = "";
        if (!CanMerge(campaign, owner, keep, absorb, out var price, out var refusal))
        { feedback = refusal; return false; }

        var map = campaign.ProvinceMap;
        var provinces = Enumerable.Range(1, map.ProvinceCount)
            .Where(province => map.ProvinceToNation[province] == absorb.MapIndex && !map.IsWater(province))
            .ToArray();
        if (provinces.Length == 0) { feedback = Loc.Get("The administration has no land to merge."); return false; }

        var absorbPopulation = campaign.StatesOf(absorb).Sum(state => state.Population);
        var absorbGdp = absorb.Gdp;
        var absorbManpower = absorb.ManpowerReserve;
        var absorbConverted = absorb.ConvertedCivilianUnits;
        var absorbLines = absorb.ProductionLines.Where(line => line is not null).ToList();
        var absorbCultures = absorb.FoundedCultures.ToList();
        var absorbTechnologies = absorb.Technologies.ToList();
        var absorbResearch = absorb.ResearchProgress.ToList();
        var absorbChildren = campaign.Nations.Where(nation => nation.AnswersTo(absorb.Id)).ToArray();

        ProductionProgramme.MigrateLegacy(keep, ProductionProgramme.Allocate(campaign, keep));
        ProductionProgramme.MigrateLegacy(absorb, ProductionProgramme.Allocate(campaign, absorb));
        StatePartition.Annex(campaign, provinces, keep, asConquest: false);
        SovereignFinance.MergeAccounts(campaign, keep, absorb);
        keep.Gdp += absorbGdp; absorb.Gdp = 0f;
        keep.ManpowerReserve += absorbManpower; absorb.ManpowerReserve = 0;
        keep.ConvertedCivilianUnits = (int)Math.Min(int.MaxValue,
            (long)keep.ConvertedCivilianUnits + absorbConverted);
        absorb.ConvertedCivilianUnits = 0;
        foreach (var line in absorbLines)
        {
            var existing = keep.ProductionLines.Find(candidate => candidate is not null && candidate.Good == line.Good);
            if (existing is null) keep.ProductionLines.Add(new ProductionLine { Good = line.Good, Units = line.Units });
            else existing.Units = (int)Math.Min(int.MaxValue, (long)existing.Units + line.Units);
        }
        absorb.ProductionLines.Clear();
        foreach (var culture in absorbCultures)
            if (!keep.FoundedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase)) keep.FoundedCultures.Add(culture);
        foreach (var technology in absorbTechnologies)
            if (!keep.Technologies.Contains(technology)) keep.Technologies.Add(technology);
        foreach (var progress in absorbResearch)
            keep.ResearchProgress[progress.Key] = Math.Max(keep.ResearchProgress.GetValueOrDefault(progress.Key), progress.Value);

        foreach (var army in campaign.Armies.Where(army => army.NationId == absorb.Id)) army.NationId = keep.Id;
        foreach (var group in campaign.ArmyGroups.Where(group => group.NationId == absorb.Id)) group.NationId = keep.Id;
        foreach (var line in campaign.FrontLines.Where(line => line.NationId == absorb.Id))
        { line.NationId = keep.Id; line.BorderRevision = int.MinValue; line.PostsDay = int.MinValue; }
        foreach (var fleet in campaign.Fleets.Where(fleet => fleet.NationId == absorb.Id)) fleet.NationId = keep.Id;
        foreach (var unit in campaign.SupportFormations.Where(unit => unit.NationId == absorb.Id)) unit.NationId = keep.Id;
        foreach (var order in campaign.SupportOrders.Where(order => order.NationId == absorb.Id)) order.NationId = keep.Id;
        foreach (var order in campaign.Recruitment.Where(order => order.NationId == absorb.Id)) order.NationId = keep.Id;
        foreach (var project in campaign.Construction.Where(project => project.NationId == absorb.Id)) project.NationId = keep.Id;
        foreach (var child in absorbChildren) child.OverlordId = keep.Id;

        SubjectSystem.Release(campaign, absorb);
        absorb.OverlordId = 0;
        absorb.SubjectType = "";
        campaign.Fallen.Add(absorb.Id);
        absorb.CapitalStateMapIndex = 0;
        absorb.CapitalProvinceId = 0;
        absorb.GovernmentSeatProvinceId = 0;
        absorb.Population = 0;
        keep.Population = campaign.StatesOf(keep).Sum(state => state.Population);
        keep.LibertyDesire = MathF.Max(0f, keep.LibertyDesire - .05f);
        keep.SubjectLibertyModifier = Math.Clamp(keep.SubjectLibertyModifier - .03f, -.55f, .55f);
        keep.LastSubjectInteractionDay = campaign.ElapsedDays;
        owner.PoliticalPoints -= price;
        owner.DecisionAvailableDay[MergeKey(keep, absorb)] =
            campaign.ElapsedDays + Math.Max(0, campaign.Defines.SubjectConsolidationCooldownDays);

        campaign.LocateCapitals(preserveExisting: true);
        CampaignDiplomacy.ForgetBorders();
        NationalModifiers.Invalidate(owner);
        NationalModifiers.Invalidate(keep);
        campaign.MarkMapChanged();
        campaign.MarkStrategicChange();
        campaign.Chronicle.Record(campaign, "subject_consolidated", keep.Id, absorb.Id);
        feedback = Loc.Format("Merged {0} into {1}: {2} region(s), {3} people.",
            absorb.DisplayName, keep.DisplayName, provinces.Length, Fmt.People(absorbPopulation));
        return true;
    }


    /// <summary>
    /// Once a month an AI overlord makes one of its clients larger or folds one micro-client
    /// into a neighbour. It does not do both to the same administration in one pass, and it
    /// leaves a subject that is already close to breaking alone: a merger is a reward for a
    /// working arrangement, not a last-minute patch over a rebellion.
    /// </summary>
    public static void TickAi(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays % 30 != 23 || !campaign.FeatureEnabled("subjects")) return;
        foreach (var owner in campaign.Nations.ToArray())
        {
            if (owner.IsHuman || owner.IsSubject || owner.Movement is not null || owner.Population < 5_000_000) continue;
            var subjects = SubjectSystem.SubjectsOf(campaign, owner)
                .Where(subject => !campaign.Fallen.Contains(subject.Id) && campaign.StatesOf(subject).Length > 0 &&
                                  !subject.SubjectIntegrating && !subject.SubjectConspiring &&
                                  !RegionalAdministration.IsMicroCityEnclave(subject) &&
                                  subject.LibertyDesire < .55f)
                .OrderBy(subject => subject.Population)
                .ToArray();
            if (subjects.Length == 0) continue;

            var grow = subjects.FirstOrDefault(subject =>
                !RegionalAdministration.IsMicroCityEnclave(subject) &&
                subject.Population <= owner.Population * .35f);
            if (grow is not null)
            {
                var package = RegionalAdministration.QuickGrantPackage(campaign, owner, grow,
                    campaign.Defines.AiSubjectGrantMaxRegions);
                if (package.Length > 0 &&
                    RegionalAdministration.MayGrant(campaign, owner, grow, package, out _, out _))
                    new GrantSubjectRegionsCommand
                    {
                        IssuerId = owner.Id, SubjectId = grow.Id, StateMapIndices = package
                    }.Apply(campaign);
            }

            var pair = Candidates(campaign, owner)
                .Where(candidate => candidate.Absorb.Population <= candidate.Keep.Population * .60f &&
                                    candidate.Keep.LibertyDesire < .70f && candidate.Absorb.LibertyDesire < .70f)
                .OrderBy(candidate => candidate.Absorb.Population)
                .FirstOrDefault();
            if (pair.Keep is not null) Merge(campaign, owner, pair.Keep, pair.Absorb, out _);
        }
    }

}

public sealed class ConsolidateSubjectsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int KeepId { get; init; }
    public int AbsorbId { get; init; }

    public void Apply(Campaign campaign)
    {
        if (campaign is null || !MechanicFeatures.Allows(campaign, this)) return;
        var owner = campaign.NationById(IssuerId);
        var keep = campaign.NationById(KeepId);
        var absorb = campaign.NationById(AbsorbId);
        SubjectBlocs.Merge(campaign, owner, keep, absorb, out _);
    }
}
