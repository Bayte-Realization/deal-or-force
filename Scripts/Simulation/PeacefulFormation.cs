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

    private static readonly HashSet<int> EmptyIntSet = new();

    public static int[] Territory(Campaign campaign, FormableNation definition)
    {
        if (campaign?.ProvinceMap is null || definition is null) return Array.Empty<int>();
        var cache = campaign.Caches.PeacefulFormation;
        lock (cache.TerritoryGate)
            return TerritoryUnlocked(campaign, cache, definition);
    }

    /// <summary>
    /// Scratch for building a definition's set of provinces. Kept per thread because the gate
    /// above serialises the build: the set holds a few thousand ints and it used to be allocated
    /// afresh for every definition on every change of the region layout, which measured as
    /// 6,4 MB of garbage over twelve days for numbers that are discarded the moment the array is
    /// published. The array itself is still fresh, because a caller keeps it.
    /// </summary>
    [ThreadStatic] private static HashSet<int> _territoryScratch;

    private static int[] TerritoryUnlocked(Campaign campaign, CampaignCaches.PeacefulFormationCache cache, FormableNation definition)
    {
        var map = campaign.ProvinceMap;
        if (!ReferenceEquals(cache.TerritoryMap, map))
        {
            cache.TerritoryMap = map;
            cache.TerritoryRegionLayoutRevision = campaign.RegionLayoutRevision;
            cache.TerritoryStateCount = campaign.States.Count;
            cache.StaticTerritory.Clear();
            cache.StaticLandTerritory.Clear();
            cache.Territory.Clear();
            cache.LandTerritory.Clear();
        }
        else if (cache.TerritoryRegionLayoutRevision != campaign.RegionLayoutRevision ||
                 cache.TerritoryStateCount != campaign.States.Count)
        {
            cache.TerritoryRegionLayoutRevision = campaign.RegionLayoutRevision;
            cache.TerritoryStateCount = campaign.States.Count;
            cache.Territory.Clear();
            cache.LandTerritory.Clear();
        }

        // Definitions with no RequiredRegions depend ONLY on constant RequiredProvinces + RequiredTags
        // (authored scenario territories) — never invalidated by peace treaties or state partitions!
        var isStatic = definition.RequiredRegions.Length == 0;
        var dict = isStatic ? cache.StaticTerritory : cache.Territory;
        if (dict.TryGetValue(definition.Id, out var cached)) return cached;

        var set = _territoryScratch ??= new HashSet<int>();
        set.Clear();
        foreach (var p in definition.RequiredProvinces) set.Add(p);
        if (!isStatic)
            foreach (var p in RegionProvinces(campaign, definition)) set.Add(p);
        foreach (var tag in definition.RequiredTags)
        {
            var provinces = campaign.ScenarioId == "2024"
                ? ProvinceOwnership.ProvincesOf(tag)
                : campaign.ScenarioTerritories.GetValueOrDefault(tag, Array.Empty<int>());
            foreach (var p in provinces) set.Add(p);
        }
        var result = set.ToArray();
        dict[definition.Id] = result;
        return result;
    }

    internal static HashSet<int> LandTerritory(Campaign campaign, FormableNation definition)
    {
        if (campaign?.ProvinceMap is null || definition is null) return EmptyIntSet;
        var cache = campaign.Caches.PeacefulFormation;
        lock (cache.TerritoryGate)
        {
            var territory = TerritoryUnlocked(campaign, cache, definition);
            var isStatic = definition.RequiredRegions.Length == 0;
            var landDict = isStatic ? cache.StaticLandTerritory : cache.LandTerritory;
            if (landDict.TryGetValue(definition.Id, out var cached)) return cached;
            var map = campaign.ProvinceMap;
            var land = new HashSet<int>();
            for (var i = 0; i < territory.Length; i++)
            {
                var p = territory[i];
                if (p > 0 && p <= map.ProvinceCount && !map.WaterByProvince[p]) land.Add(p);
            }
            landDict[definition.Id] = land;
            return land;
        }
    }

    /// <summary>The provinces of a formable's named regions, and of the pieces a border has
    /// since cut out of them.</summary>
    public static IEnumerable<int> RegionProvinces(Campaign campaign, FormableNation definition) =>
        RegionProvinceList(campaign, definition);

    /// <summary>
    /// The same answer, worked out once per region layout instead of once per question.
    ///
    /// Evaluating one foundation asks for it, every candidate partner's refusal asks for it, and
    /// each answer walked every province in the world and built two sets of regions. Keyed on
    /// <see cref="Campaign.RegionLayoutRevision"/> and <see cref="Campaign.States"/> count so
    /// ordinary peace treaties never flush unchanged region shapes.
    /// </summary>
    [ThreadStatic] private static HashSet<int> _regionNamedScratch;
    [ThreadStatic] private static HashSet<int> _regionWantedScratch;
    [ThreadStatic] private static List<int> _regionProvincesScratch;

    private static int[] RegionProvinceList(Campaign campaign, FormableNation definition)
    {
        if (definition is null || campaign?.ProvinceMap is null || definition.RequiredRegions.Length == 0)
            return Array.Empty<int>();
        var cache = campaign.Caches.PeacefulFormation;
        lock (cache.RegionGate)
        {
            var map = campaign.ProvinceMap;
            if (!ReferenceEquals(cache.RegionMap, map) ||
                cache.RegionLayoutRevision != campaign.RegionLayoutRevision ||
                cache.RegionStateCount != campaign.States.Count)
            {
                cache.RegionMap = map;
                cache.RegionLayoutRevision = campaign.RegionLayoutRevision;
                cache.RegionStateCount = campaign.States.Count;
                cache.Regions.Clear();
            }
            if (cache.Regions.TryGetValue(definition.Id, out var cached)) return cached;
            var named = _regionNamedScratch ??= new HashSet<int>();
            named.Clear();
            var required = definition.RequiredRegions;
            var states = campaign.States;
            for (var si = 0; si < states.Count; si++)
            {
                var st = states[si];
                for (var ri = 0; ri < required.Length; ri++)
                {
                    if (string.Equals(st.Id, required[ri], StringComparison.OrdinalIgnoreCase))
                    {
                        named.Add(st.MapIndex);
                        break;
                    }
                }
            }
            var wanted = _regionWantedScratch ??= new HashSet<int>();
            wanted.Clear();
            for (var si = 0; si < states.Count; si++)
            {
                var st = states[si];
                if (named.Contains(st.MapIndex) || named.Contains(st.ParentMapIndex))
                    wanted.Add(st.MapIndex);
            }
            var provinces = _regionProvincesScratch ??= new List<int>();
            provinces.Clear();
            for (var p = 1; p <= map.ProvinceCount; p++)
                if (wanted.Contains(map.ProvinceToState[p])) provinces.Add(p);
            var result = provinces.ToArray();
            provinces.Clear();
            named.Clear();
            wanted.Clear();
            cache.Regions[definition.Id] = result;
            return result;
        }
    }

    private static bool AtPeace(Campaign campaign, Nation nation)
    {
        var live = Warfare.LiveWars(campaign);
        for (var i = 0; i < live.Count; i++)
        {
            var w = live[i];
            if (!w.Ended && w.Involves(nation.Id)) return false;
        }
        return true;
    }

    // An observer watches; nobody sits at the country's desk to answer an invitation, so the
    // country answers it the way any government nobody plays would. Asked, it was handed a
    // window it could not see - and observer mode then took the first option, which signed
    // Spain into a Mexican union nobody had chosen.
    private static bool HumanControlled(Campaign campaign, Nation nation) =>
        !campaign.NobodyPlays && (nation.IsHuman || nation.IsPlayer || campaign.HumanSeats.Contains(nation.Id));

    public static bool Consented(Campaign campaign, Nation founder, Nation partner, FormableNation definition) =>
        definition.PeacefulAccession && partner.DecisionAvailableDay.Count > 0 &&
        partner.DecisionAvailableDay.TryGetValue(ConsentKey(founder.Id, definition.Id), out var until) &&
        until > campaign.ElapsedDays;

    public static bool Participates(Campaign campaign, Nation founder, Nation partner, FormableNation definition)
    {
        if (partner is null || partner.Id == founder.Id) return partner?.Id == founder.Id;
        if (!AtPeace(campaign, partner) || !FormableProclamationAi.EstablishedCountry(partner) || partner.Landless) return false;
        // No foreign dependencies can be signed away by their nominal government.
        if (partner.IsSubject && !partner.AnswersTo(founder.Id)) return false;
        if (HumanControlled(campaign, partner))
            return campaign.HumanSeats.Count <= 1 && Consented(campaign, founder, partner, definition);
        return Consented(campaign, founder, partner, definition) ||
            (definition.SubjectIntegration && partner.AnswersTo(founder.Id) && partner.LibertyDesire <= campaign.Defines.UnionMaximumSubjectLiberty);
    }

    /// <summary>Related peoples make a proposal easier, but never replace consent.</summary>
    public static float CulturalAffinity(Nation founder, Nation partner)
    {
        var ours = DemographyCatalog.PrimaryCultures(founder.Tag);
        var theirs = DemographyCatalog.PrimaryCultures(partner.Tag);
        // Two handfuls of names: walked directly. This was three LINQ pipelines and a HashSet
        // built per pair, and it is asked once per candidate partner of every foundation.
        foreach (var a in ours)
            foreach (var b in theirs)
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1f;
        foreach (var a in ours)
        {
            var group = DemographyCatalog.Culture(a)?.Group;
            if (string.IsNullOrWhiteSpace(group)) continue;
            foreach (var b in theirs)
                if (string.Equals(DemographyCatalog.Culture(b)?.Group, group, StringComparison.OrdinalIgnoreCase)) return .5f;
        }
        return 0f;
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
        if (!FormableNations.Eligible(campaign, founder, definition) || founder.Id == partner.Id || founder.IsSubject ||
            !FormableNations.CompatibleProclamation(founder, definition)) return "This country cannot lead the foundation.";
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return "Territorial map ready";
        if (!AtPeace(campaign, founder) || !AtPeace(campaign, partner)) return "Both countries must be at peace.";
        if (!FormableProclamationAi.EstablishedCountry(partner) || partner.Landless || (partner.IsSubject && !partner.AnswersTo(founder.Id)))
            return "The partner must control its own foreign policy.";
        if (HumanControlled(campaign, partner) && campaign.HumanSeats.Count > 1) return "Human-controlled countries cannot merge in multiplayer.";
        if (founder.Stability < definition.MinimumStability || partner.Stability < campaign.Defines.UnionMinimumPartnerStability)
            return "Both governments need a stable mandate.";
        if (!FormableNations.ReligionMandate(campaign, founder, definition))
            return "The founder lacks the required religious population mandate.";
        var map = campaign.ProvinceMap;
        var territory = Territory(campaign, definition);
        var ownsTerritory = false;
        for (var i = 0; i < territory.Length; i++)
        {
            var p = territory[i];
            if (p > 0 && p <= map.ProvinceCount && map.ProvinceToNation[p] == partner.MapIndex)
            {
                ownsTerritory = true;
                break;
            }
        }
        if (!ownsTerritory)
            return "The partner owns no territory required for this foundation.";
        if (Consented(campaign, founder, partner, definition)) return "The accession treaty is already signed.";
        for (var i = 0; i < campaign.PendingEvents.Count; i++)
        {
            var e = campaign.PendingEvents[i];
            if (e.NationId != partner.Id) continue;
            for (var oi = 0; oi < e.Options.Length; oi++)
            {
                var o = e.Options[oi];
                if (o.UnionFounderId == founder.Id && o.UnionFormableId == definition.Id)
                    return "The partner is considering the invitation.";
            }
        }
        if (partner.DecisionAvailableDay.Count > 0 &&
            partner.DecisionAvailableDay.GetValueOrDefault(OfferKey(founder.Id, definition.Id)) > campaign.ElapsedDays)
            return "This partner was approached recently.";
        if ((CampaignDiplomacy.Peek(campaign, founder.Id, partner.Id)?.Opinion ?? 0f) < campaign.Defines.UnionMinimumOpinion)
            return Loc.Format("Relations must reach +{0:0} before accession talks.", campaign.Defines.UnionMinimumOpinion);
        return "";
    }

    public static bool Invite(Campaign campaign, Nation founder, Nation partner, FormableNation definition)
    {
        if (Refusal(campaign, founder, partner, definition).Length > 0) return false;
        partner.DecisionAvailableDay[OfferKey(founder.Id, definition.Id)] = campaign.ElapsedDays + Math.Max(1, campaign.Defines.UnionInvitationCooldownDays);
        if (!HumanControlled(campaign, partner))
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
            !FormableNations.Eligible(campaign, founder, definition) || founder.IsSubject || founder.Id == partner.Id || !FormableNations.CompatibleProclamation(founder, definition) ||
            founder.Stability < definition.MinimumStability || partner.Stability < campaign.Defines.UnionMinimumPartnerStability ||
            !FormableNations.ReligionMandate(campaign, founder, definition) ||
            !AtPeace(campaign, founder) || !AtPeace(campaign, partner) ||
            (partner.IsSubject && !partner.AnswersTo(founder.Id)) || !FormableProclamationAi.EstablishedCountry(partner) || partner.Landless ||
            (HumanControlled(campaign, partner) && campaign.HumanSeats.Count > 1)) return false;
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
        // Some restorations need only half of their historical homeland. Proclaiming one
        // is not permission to acquire the remainder from an unconsenting neighbour.
        var participating = campaign.Nations.Where(n => n.Id != founder.Id && Participates(campaign, founder, n, definition))
            .Select(n => n.MapIndex).ToHashSet();
        var provinces = Territory(campaign, definition).Where(p => p > 0 && p <= map.ProvinceCount &&
            p < campaign.ProvinceController.Length && participating.Contains(map.ProvinceToNation[p]) &&
            campaign.ProvinceController[p] == map.ProvinceToNation[p] &&
            !map.WaterByProvince[p]).ToArray();
        if (provinces.Length == 0) return;
        var partners = provinces.Select(p => map.ProvinceToNation[p]).Distinct()
            .Select(index => campaign.Nations.Find(n => n.MapIndex == index)).Where(n => n is not null).ToArray();
        var populations = partners.ToDictionary(n => n.Id, n => campaign.StatesOf(n).Sum(s => s.Population));
        // Convert legacy shares against each original economy, not the enlarged one.
        ProductionProgramme.MigrateLegacy(founder, ProductionProgramme.Allocate(campaign, founder));
        foreach (var partner in partners)
            ProductionProgramme.MigrateLegacy(partner, ProductionProgramme.Allocate(campaign, partner));
        var rawAnnual = founder.Gdp / MathF.Max(.0001f, founder.OutputScale);
        StatePartition.Annex(campaign, provinces, founder, asConquest: false, byConsent: true);
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
            // The former government signed its accession, rather than fleeing a conquest.
            // Retain its homeland titles for future organic separatism, but do not create
            // a government in exile on the very next MovementFall pass.
            campaign.Fallen.Add(partner.Id);
            SovereignFinance.MergeAccounts(campaign, founder, partner);
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
            partner.GovernmentSeatProvinceId = 0;
            if (HumanControlled(campaign, partner)) campaign.ConfigureSinglePlayer(founder.Id);
        }
        founder.Population = campaign.StatesOf(founder).Sum(s => s.Population);
        if (founder.FoundedCultures.Count > 0) DemographyCatalog.Declare(founder.Tag, founder.FoundedCultures);
        if (rawAnnual > .0001f) founder.OutputScale = founder.Gdp / rawAnnual;
        var allocation = ProductionProgramme.Allocate(campaign, founder);
        founder.ArmsFocus = allocation.RawCivilian == 0 ? 0 : allocation.Converted / (float)allocation.RawCivilian;
        campaign.LocateCapitals(preserveExisting: true);
        SupportCore.IndexReach(campaign);
        campaign.MarkStrategicChange();
    }

    /// <summary>Direct bilateral integration of a partner state (e.g. cultural shard or consented merger).</summary>
    public static void IntegratePartner(Campaign campaign, Nation founder, Nation partner, bool transferPlayerSeat = true,
        bool combineCultures = true)
    {
        if (campaign is null || founder is null || partner is null || founder.Id == partner.Id) return;
        var map = campaign.ProvinceMap;
        if (map is not null)
        {
            var provinces = new List<int>();
            for (var p = 1; p <= map.ProvinceCount; p++)
            {
                if (map.ProvinceToNation[p] == partner.MapIndex && !map.WaterByProvince[p])
                    provinces.Add(p);
            }
            if (provinces.Count > 0)
                StatePartition.Annex(campaign, provinces, founder, asConquest: false, byConsent: true);
        }

        ProductionProgramme.MigrateLegacy(founder, ProductionProgramme.Allocate(campaign, founder));
        ProductionProgramme.MigrateLegacy(partner, ProductionProgramme.Allocate(campaign, partner));

        var rawAnnual = founder.Gdp / MathF.Max(.0001f, founder.OutputScale);
        var gdp = partner.Gdp;
        rawAnnual += gdp / MathF.Max(.0001f, partner.OutputScale);
        founder.Gdp += gdp;
        partner.Gdp = 0;
        partner.Population = 0;

        campaign.Fallen.Add(partner.Id);
        SovereignFinance.MergeAccounts(campaign, founder, partner);
        founder.ManpowerReserve += partner.ManpowerReserve; partner.ManpowerReserve = 0;
        founder.ConvertedCivilianUnits = (int)Math.Min(int.MaxValue, (long)founder.ConvertedCivilianUnits + partner.ConvertedCivilianUnits);
        foreach (var line in partner.ProductionLines.Where(l => l is not null))
        {
            var existing = founder.ProductionLines.Find(l => l is not null && l.Good == line.Good);
            if (existing is null) founder.ProductionLines.Add(new ProductionLine { Good = line.Good, Units = line.Units });
            else existing.Units = (int)Math.Min(int.MaxValue, (long)existing.Units + line.Units);
        }
        partner.ProductionLines.Clear(); partner.ConvertedCivilianUnits = 0;
        if (combineCultures)
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
        partner.GovernmentSeatProvinceId = 0;
        if (partner.IsHuman && transferPlayerSeat) campaign.ConfigureSinglePlayer(founder.Id);

        founder.Population = campaign.StatesOf(founder).Sum(s => s.Population);
        if (founder.FoundedCultures.Count > 0) DemographyCatalog.Declare(founder.Tag, founder.FoundedCultures);
        if (rawAnnual > .0001f) founder.OutputScale = founder.Gdp / rawAnnual;
        var allocation = ProductionProgramme.Allocate(campaign, founder);
        founder.ArmsFocus = allocation.RawCivilian == 0 ? 0 : allocation.Converted / (float)allocation.RawCivilian;
        campaign.LocateCapitals(preserveExisting: true);
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
