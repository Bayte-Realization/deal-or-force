using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Accession is a treaty before it is a border change. Consent is stored in the
/// existing saved cooldown ledger, and expires: an invitation is not a permanent claim.</summary>
public static class PeacefulFormation
{
    public static string ConsentKey(int founder, string formable) => "union_consent/" + founder + "/" + formable;
    private static string OfferKey(int founder, string formable) => "union_offer/" + founder + "/" + formable;
    public static IEnumerable<int> Territory(Campaign campaign, FormableNation definition) =>
        definition.RequiredProvinces.Concat(definition.RequiredTags.SelectMany(tag =>
            campaign.ScenarioId == "2024" ? ProvinceOwnership.ProvincesOf(tag) :
                campaign.ScenarioTerritories.GetValueOrDefault(tag, Array.Empty<int>()))).Distinct();

    private static bool AtPeace(Campaign campaign, Nation nation) =>
        !campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id));

    public static bool Consented(Campaign campaign, Nation founder, Nation partner, FormableNation definition) =>
        definition.PeacefulAccession && partner.DecisionAvailableDay.TryGetValue(ConsentKey(founder.Id, definition.Id), out var until)
        && until > campaign.ElapsedDays;

    public static bool Participates(Campaign campaign, Nation founder, Nation partner, FormableNation definition)
    {
        if (partner is null || partner.Id == founder.Id) return partner?.Id == founder.Id;
        if (!AtPeace(campaign, partner) || partner.Movement is not null) return false;
        // No foreign dependencies can be signed away by their nominal government.
        if (partner.IsSubject && !partner.AnswersTo(founder.Id)) return false;
        if (partner.IsHuman && campaign.HumanSeats.Count > 1) return false;
        return Consented(campaign, founder, partner, definition) ||
            (definition.SubjectIntegration && partner.AnswersTo(founder.Id) && partner.LibertyDesire <= campaign.Defines.UnionMaximumSubjectLiberty);
    }

    /// <summary>Related peoples make a proposal easier, but never replace consent.</summary>
    public static float CulturalAffinity(Nation founder, Nation partner)
    {
        var ours = DemographyCatalog.PrimaryCultures(founder.Tag);
        var theirs = DemographyCatalog.PrimaryCultures(partner.Tag);
        if (ours.Any(a => theirs.Contains(a, StringComparer.OrdinalIgnoreCase))) return 1f;
        var groups = ours.Select(a => DemographyCatalog.Culture(a)?.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return theirs.Any(a => groups.Contains(DemographyCatalog.Culture(a)?.Group ?? "")) ? .5f : 0f;
    }

    public static float Willingness(Campaign campaign, Nation founder, Nation partner)
    {
        var relation = CampaignDiplomacy.Peek(campaign, founder.Id, partner.Id);
        return M.Clamp((relation?.Opinion ?? 0f) * campaign.Defines.UnionOpinionWeight + CulturalAffinity(founder, partner) * campaign.Defines.UnionCultureWeight +
            (relation?.Stance == DiplomaticStance.Allied ? campaign.Defines.UnionAllianceWeight : 0f) +
            (relation?.TradeAgreement == true ? campaign.Defines.UnionTradeAgreementWeight : 0f) +
            (founder.Government == partner.Government ? campaign.Defines.UnionGovernmentWeight : 0f) +
            (founder.Ideology == partner.Ideology ? campaign.Defines.UnionIdeologyWeight : 0f) +
            (partner.AnswersTo(founder.Id) ? (1f - partner.LibertyDesire) * campaign.Defines.UnionSubjectLoyaltyWeight : 0f) +
            (founder.Stability - campaign.Defines.UnionStabilityBaseline) * campaign.Defines.UnionStabilityWeight, 0f, 100f);
    }

    public static string Refusal(Campaign campaign, Nation founder, Nation partner, FormableNation definition)
    {
        if (campaign is null || founder is null || partner is null || definition is null) return "Choose a founding country and a partner.";
        if (!definition.PeacefulAccession) return "This proclamation has no peaceful accession route.";
        if (!FormableNations.Eligible(founder, definition) || founder.Id == partner.Id || founder.IsSubject ||
            !string.IsNullOrEmpty(founder.FormedNationId)) return "This country cannot lead the foundation.";
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return "Territorial map ready";
        if (!AtPeace(campaign, founder) || !AtPeace(campaign, partner)) return "Both countries must be at peace.";
        if (partner.Movement is not null || (partner.IsSubject && !partner.AnswersTo(founder.Id)))
            return "The partner must control its own foreign policy.";
        if (partner.IsHuman && campaign.HumanSeats.Count > 1) return "Human-controlled countries cannot merge in multiplayer.";
        if (founder.Stability < definition.MinimumStability || partner.Stability < campaign.Defines.UnionMinimumPartnerStability)
            return "Both governments need a stable mandate.";
        var map = campaign.ProvinceMap;
        if (!Territory(campaign, definition).Any(p => p > 0 && p <= map.ProvinceCount && map.ProvinceToNation[p] == partner.MapIndex))
            return "The partner owns no territory required for this foundation.";
        if (Consented(campaign, founder, partner, definition)) return "The accession treaty is already signed.";
        if (campaign.PendingEvents.Any(e => e.NationId == partner.Id && e.Options.Any(o => o.UnionFounderId == founder.Id && o.UnionFormableId == definition.Id)))
            return "The partner is considering the invitation.";
        if (partner.DecisionAvailableDay.GetValueOrDefault(OfferKey(founder.Id, definition.Id)) > campaign.ElapsedDays)
            return "This partner was approached recently.";
        if ((CampaignDiplomacy.Peek(campaign, founder.Id, partner.Id)?.Opinion ?? 0f) < campaign.Defines.UnionMinimumOpinion)
            return Loc.Format("Relations must reach +{0:0} before accession talks.", campaign.Defines.UnionMinimumOpinion);
        return "";
    }

    public static bool Invite(Campaign campaign, Nation founder, Nation partner, FormableNation definition)
    {
        if (Refusal(campaign, founder, partner, definition).Length > 0) return false;
        partner.DecisionAvailableDay[OfferKey(founder.Id, definition.Id)] = campaign.ElapsedDays + Math.Max(1, campaign.Defines.UnionInvitationCooldownDays);
        if (!partner.IsHuman)
        {
            if (Willingness(campaign, founder, partner) >= campaign.Defines.UnionAiAcceptance) return Accept(campaign, founder, partner, definition);
            campaign.Chronicle.Record(campaign, "accession_declined", partner.Id, founder.Id, subject: definition.Name);
            campaign.MarkStrategicChange();
            return false;
        }
        var id = campaign.PendingEvents.Count == 0 ? 1 : campaign.PendingEvents.Max(e => e.Id) + 1;
        campaign.PendingEvents.Add(new PendingCampaignEvent
        {
            Id = id, NationId = partner.Id, CreatedDay = campaign.ElapsedDays, ExpiresDay = campaign.ElapsedDays + Math.Max(1, campaign.Defines.UnionInvitationExpiryDays),
            Title = Loc.Format("Invitation to found {0}", Loc.Get(definition.Name)),
            Description = Loc.Format("{0} proposes a shared state. Consent lasts {1} days. Only declared foundation territory changes hands on proclamation; no land moves today.", founder.DisplayName, Math.Max(1, campaign.Defines.UnionConsentDays)),
            Options = new[]
            {
                new CampaignEventOption { Title = "Sign the accession treaty", Consequence = "Consent to join the declared foundation when it is proclaimed.", UnionFounderId = founder.Id, UnionFormableId = definition.Id },
                new CampaignEventOption { Title = "Retain our independence", Consequence = "No borders or institutions change." }
            }
        });
        campaign.MarkStrategicChange();
        return true;
    }

    public static bool Accept(Campaign campaign, Nation founder, Nation partner, FormableNation definition)
    {
        // Recheck irreversible facts when an old, saved invitation is answered.
        if (founder is null || partner is null || definition is null || !definition.PeacefulAccession ||
            !FormableNations.Eligible(founder, definition) || founder.IsSubject || founder.Id == partner.Id || !string.IsNullOrEmpty(founder.FormedNationId) ||
            founder.Stability < definition.MinimumStability || partner.Stability < campaign.Defines.UnionMinimumPartnerStability ||
            !AtPeace(campaign, founder) || !AtPeace(campaign, partner) ||
            (partner.IsSubject && !partner.AnswersTo(founder.Id)) || partner.Movement is not null ||
            (partner.IsHuman && campaign.HumanSeats.Count > 1)) return false;
        if (campaign.ProvinceMap is null || !Territory(campaign, definition).Any(p => p > 0 && p <= campaign.ProvinceMap.ProvinceCount &&
            campaign.ProvinceMap.ProvinceToNation[p] == partner.MapIndex)) return false;
        partner.DecisionAvailableDay[ConsentKey(founder.Id, definition.Id)] = campaign.ElapsedDays + Math.Max(1, campaign.Defines.UnionConsentDays);
        campaign.Chronicle.Record(campaign, "accession_signed", partner.Id, founder.Id, subject: definition.Name);
        campaign.MarkStrategicChange();
        return true;
    }

    public static void Integrate(Campaign campaign, Nation founder, FormableNation definition)
    {
        var map = campaign.ProvinceMap;
        var provinces = Territory(campaign, definition).Where(p => p > 0 && p <= map.ProvinceCount &&
            map.ProvinceToNation[p] != founder.MapIndex && !map.WaterByProvince[p]).ToArray();
        if (provinces.Length == 0) return;
        var partners = provinces.Select(p => map.ProvinceToNation[p]).Distinct()
            .Select(index => campaign.Nations.Find(n => n.MapIndex == index)).Where(n => n is not null).ToArray();
        var populations = partners.ToDictionary(n => n.Id, n => campaign.StatesOf(n).Sum(s => s.Population));
        // Convert legacy shares against each original economy, not the enlarged one.
        ProductionProgramme.MigrateLegacy(founder, ProductionProgramme.Allocate(campaign, founder));
        foreach (var partner in partners)
            ProductionProgramme.MigrateLegacy(partner, ProductionProgramme.Allocate(campaign, partner));
        var rawAnnual = founder.Gdp / MathF.Max(.0001f, founder.OutputScale);
        StatePartition.Annex(campaign, provinces, founder, asConquest: false);
        foreach (var partner in partners)
        {
            var remaining = campaign.StatesOf(partner).Sum(s => s.Population);
            var share = populations[partner.Id] > 0 ? M.Clamp01(1f - remaining / (float)populations[partner.Id]) : 0f;
            var gdp = partner.Gdp * share;
            rawAnnual += gdp / MathF.Max(.0001f, partner.OutputScale);
            founder.Gdp += gdp;
            partner.Gdp -= gdp;
            partner.Population = remaining;
            if (campaign.StatesOf(partner).Length > 0) continue;
            // Joining is not defeat. Keep people, savings, equipment, training and research.
            founder.Treasury += partner.Treasury; partner.Treasury = 0;
            founder.ManpowerReserve += partner.ManpowerReserve; partner.ManpowerReserve = 0;
            founder.ConvertedCivilianUnits = (int)Math.Min(int.MaxValue, (long)founder.ConvertedCivilianUnits + partner.ConvertedCivilianUnits);
            foreach (var line in partner.ProductionLines.Where(l => l is not null))
            {
                var existing = founder.ProductionLines.Find(l => l is not null && l.Good == line.Good);
                if (existing is null) founder.ProductionLines.Add(new ProductionLine { Good = line.Good, Units = line.Units });
                else existing.Units = (int)Math.Min(int.MaxValue, (long)existing.Units + line.Units);
            }
            partner.ProductionLines.Clear(); partner.ConvertedCivilianUnits = 0;
            foreach (var culture in DemographyCatalog.PrimaryCultures(founder.Tag).Concat(partner.FoundedCultures)
                         .Concat(DemographyCatalog.PrimaryCultures(partner.Tag)))
                if (!founder.FoundedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase)) founder.FoundedCultures.Add(culture);
            foreach (var technology in partner.Technologies)
                if (!founder.Technologies.Contains(technology)) founder.Technologies.Add(technology);
            foreach (var progress in partner.ResearchProgress)
                founder.ResearchProgress[progress.Key] = Math.Max(founder.ResearchProgress.GetValueOrDefault(progress.Key), progress.Value);
            foreach (var army in campaign.Armies.Where(a => a.NationId == partner.Id)) army.NationId = founder.Id;
            foreach (var group in campaign.ArmyGroups.Where(a => a.NationId == partner.Id)) group.NationId = founder.Id;
            foreach (var line in campaign.FrontLines.Where(a => a.NationId == partner.Id))
            { line.NationId = founder.Id; line.BorderRevision = int.MinValue; line.PostsDay = int.MinValue; }
            foreach (var fleet in campaign.Fleets.Where(a => a.NationId == partner.Id)) fleet.NationId = founder.Id;
            foreach (var unit in campaign.SupportFormations.Where(a => a.NationId == partner.Id)) unit.NationId = founder.Id;
            foreach (var order in campaign.SupportOrders.Where(a => a.NationId == partner.Id)) order.NationId = founder.Id;
            foreach (var order in campaign.Recruitment.Where(a => a.NationId == partner.Id)) order.NationId = founder.Id;
            foreach (var project in campaign.Construction.Where(a => a.NationId == partner.Id)) project.NationId = founder.Id;
            foreach (var child in campaign.Nations.Where(n => n.AnswersTo(partner.Id))) child.OverlordId = founder.Id;
            SubjectSystem.Release(campaign, partner);
            partner.CapitalStateMapIndex = 0; partner.CapitalProvinceId = 0;
            if (partner.IsHuman) campaign.ConfigureSinglePlayer(founder.Id);
        }
        founder.Population = campaign.StatesOf(founder).Sum(s => s.Population);
        if (founder.FoundedCultures.Count > 0) DemographyCatalog.Declare(founder.Tag, founder.FoundedCultures);
        if (rawAnnual > .0001f) founder.OutputScale = founder.Gdp / rawAnnual;
        var allocation = ProductionProgramme.Allocate(campaign, founder);
        founder.ArmsFocus = allocation.RawCivilian == 0 ? 0 : allocation.Converted / (float)allocation.RawCivilian;
        campaign.LocateCapitals();
        SupportCore.IndexReach(campaign);
        campaign.MarkStrategicChange();
    }
}

public sealed class InviteFormationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int PartnerId { get; init; }
    public string FormableId { get; init; } = "";
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        PeacefulFormation.Invite(campaign, campaign.NationById(IssuerId),
            campaign.NationById(PartnerId), FormableNations.Find(FormableId));
    }
}
