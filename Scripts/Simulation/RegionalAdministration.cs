using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool RegionalAdministrationEnabled { get; set; } = true;
    public int AiAdministrationMaxRegions { get; set; } = 10;
    public float AiAdministrationMaxPopulationShare { get; set; } = .50f;
    public float AiAdministrationCultureShare { get; set; } = .30f;
    public float SubjectLandGrantPoliticalCostPerRegion { get; set; } = 3f;
    public int SubjectLandGrantCooldownDays { get; set; } = 90;
    /// <summary>A subject may not be grown past this share of its overlord's people by land grants.</summary>
    public float SubjectLandGrantMaxPopulationShare { get; set; } = .20f;
    /// <summary>
    /// Nor past this multiple of the people it already has.
    ///
    /// The share above is measured against the overlord, which for a great power is a very
    /// large number: a fifth of China is two hundred and eighty million people, and a city of
    /// six hundred thousand that received them would be a fifth of a country on the map while
    /// still being a city. A grant is meant to hand ground to an administration that can run
    /// it, so the ceiling has to be relative to the administration as well as to the overlord
    /// - and a micro-territory is capped by its own size however big its master is.
    /// </summary>
    public float SubjectLandGrantMaxGrowthFactor { get; set; } = 8f;
    /// <summary>How many regions a micro-city enclave may hold at all, whatever the population arithmetic says.</summary>
    public int MicroEnclaveMaxRegions { get; set; } = 2;
}

/// <summary>
/// Cause: peripheral governance pressure. Facts: connected, culturally related regions and
/// a suitable existing administration. Consequence: delegate one viable package rather
/// than create a succession of microstates. Action: release, enlarge, integrate or free
/// that administration through the existing subject lifecycle. No second ownership model.
/// </summary>
public static class RegionalAdministration
{
    public static string DominantCulture(ScenarioState state) => EconomySystem.Cultures(state)
        .OrderByDescending(p => p.Size).ThenBy(p => p.Culture, StringComparer.Ordinal).FirstOrDefault().Culture ?? "";

    /// <summary>
    /// What kind of administration a package should become. A large, culturally foreign bloc
    /// is a special administrative region, not a one-region puppet; a small kindred frontier
    /// is an ordinary satellite or dominion. The data file can rename or retune the kind.
    /// </summary>
    public static string SubjectTypeFor(Nation owner, ScenarioState seed, int packageSize)
    {
        if (owner is null || seed is null) return "satellite";
        var foreign = EconomySystem.ForeignShare(seed, owner.Tag);
        if ((packageSize >= 4 || foreign >= .80f) && SubjectCatalog.Knows("special_administrative_region"))
            return "special_administrative_region";
        return owner.Government is "federation" or "parliamentary_republic" or "constitutional_monarchy"
            ? "dominion" : "satellite";
    }

    public static int[] Package(Campaign c, Nation owner, ScenarioState seed, Nation recipient = null,
        int maxRegions = -1)
    {
        if (c?.ProvinceMap is null || owner is null || seed is null ||
            !c.Defines.RegionalAdministrationEnabled) return Array.Empty<int>();
        var available = CountryRelease.ReleasableStates(c, owner).ToDictionary(s => s.MapIndex);
        if (!available.ContainsKey(seed.MapIndex)) return Array.Empty<int>();
        var secure = SecureRegions(c, owner, available.Keys);
        var configured = Math.Clamp(c.Defines.AiAdministrationMaxRegions, 1, 64);
        var maxCount = maxRegions > 0 ? Math.Min(maxRegions, configured) : configured;
        var capShare = recipient is null
            ? Math.Clamp(c.Defines.AiAdministrationMaxPopulationShare, .01f, .80f)
            : Math.Clamp(c.Defines.SubjectLandGrantMaxPopulationShare, .01f, .75f);
        var cap = owner.Population * capShare;
        if (seed.Population > cap) return Array.Empty<int>();
        // The same ceiling `MayGrant` applies, applied here so a package is never built that
        // the grant will then refuse. A micro-territory is bounded by its own size.
        if (recipient is not null)
        {
            var grown = c.StatesOf(recipient).Sum(state => state.Population);
            var factor = Math.Clamp(c.Defines.SubjectLandGrantMaxGrowthFactor, 1f, 64f);
            if (grown > 0) cap = Math.Min(cap, grown * factor - seed.Population);
            if (RegionalAdministration.IsMicroCityEnclave(recipient))
                maxCount = Math.Min(maxCount, Math.Max(1, c.Defines.MicroEnclaveMaxRegions - c.StatesOf(recipient).Length));
            if (cap <= 0 || maxCount <= 0) return Array.Empty<int>();
        }
        var culture = DominantCulture(seed);
        var group = DemographyCatalog.Culture(culture)?.Group ?? "";
        var seedCores = seed.CoreTags.Concat(seed.Claims)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, owner.Tag, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seedUnruly = seed.Radicalism >= c.Defines.AiReleaseRegionHeatThreshold || seed.SecessionPressure >= 0.55f;
        var selected = new HashSet<int>();
        var queue = new Queue<int>(); queue.Enqueue(seed.MapIndex);
        long people = 0;
        while (queue.Count > 0 && selected.Count < maxCount)
        {
            var id = queue.Dequeue();
            if (selected.Contains(id) || !available.TryGetValue(id, out var state) || people + state.Population > cap) continue;
            if (!secure.Contains(id)) continue;
            // Never package an overlord's own majority-culture heartland! A special administrative
            // region or frontier vassal is for distinct peoples or genuinely non-core peripheries,
            // never for handing Sichuan, Guangdong and half of China to Macao!
            var foreignShare = EconomySystem.ForeignShare(state, owner.Tag);
            if (foreignShare < 0.50f && !state.CoreTags.Concat(state.Claims).Any(seedCores.Contains))
                continue;
            var local = DominantCulture(state);
            var related = local == culture || group.Length > 0 && DemographyCatalog.Culture(local)?.Group == group;
            var regionalCultures = EconomySystem.Cultures(state);
            var total = regionalCultures.Sum(p => p.Size);
            var kindred = regionalCultures.Where(p => p.Culture == culture ||
                group.Length > 0 && DemographyCatalog.Culture(p.Culture)?.Group == group).Sum(p => p.Size);
            var threshold = float.IsFinite(c.Defines.AiAdministrationCultureShare)
                ? Math.Clamp(c.Defines.AiAdministrationCultureShare, 0, 1) : .35f;
            var sharedCore = seedCores.Count > 0 &&
                state.CoreTags.Concat(state.Claims).Any(seedCores.Contains);
            var unrulyFrontier = seedUnruly && foreignShare >= 0.65f &&
                (state.Radicalism >= c.Defines.AiReleaseRegionHeatThreshold * 0.85f || state.SecessionPressure >= 0.50f);
            // No stretching across a culturally unrelated stable core to reach an enclave.
            if (id != seed.MapIndex && !sharedCore && !unrulyFrontier &&
                (!related || total <= 0 || kindred / (double)total < threshold ||
                foreignShare < .5f)) continue;
            selected.Add(id); people += state.Population;
            foreach (var next in TerritorialCoherence.AdjacentRegions(c, id))
                if (!selected.Contains(next)) queue.Enqueue(next);
        }
        if (recipient is not null && !selected.Any(id => TerritorialCoherence.TouchesOwnedGround(c, recipient, id)))
            return Array.Empty<int>();
        return selected.OrderBy(id => id).ToArray();
    }

    public static bool IsMicroCityEnclave(Nation n) =>
        n is not null && (
            SubjectCatalog.Of(n.SubjectType)?.ReceivesLandGrants == false ||
            n.Tag is "MAC" or "HKG" or "GIB" or "MCO" or "SMR" or "VAT" or "LIE" or "AND" or "CEU" or "MEL" ||
            n.Name.Contains("S.A.R", StringComparison.OrdinalIgnoreCase) ||
            n.DisplayName.Contains("S.A.R", StringComparison.OrdinalIgnoreCase)
        );

    public static int[] QuickGrantPackage(Campaign c, Nation owner, Nation subject, int maxRegions = 0)
    {
        if (c?.ProvinceMap is null || owner is null || subject is null || !subject.AnswersTo(owner.Id))
            return Array.Empty<int>();
        if (IsMicroCityEnclave(subject))
            return Array.Empty<int>();
        var subCulture = !string.IsNullOrEmpty(subject.DeclaredCulture)
            ? subject.DeclaredCulture
            : DemographyCatalog.PrimaryCulture(subject.Tag);
        var subGroup = DemographyCatalog.Culture(subCulture)?.Group ?? "";
        var subCores = c.StatesOf(subject).SelectMany(s => s.CoreTags.Concat(s.Claims))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, owner.Tag, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var releasable = CountryRelease.ReleasableStates(c, owner);
        var secure = SecureRegions(c, owner, releasable.Select(s => s.MapIndex));
        var borderCandidates = releasable
            .Where(s => secure.Contains(s.MapIndex) &&
                        TerritorialCoherence.TouchesOwnedGround(c, subject, s.MapIndex) &&
                        (EconomySystem.ForeignShare(s, owner.Tag) >= 0.55f ||
                         s.CoreTags.Concat(s.Claims).Any(subCores.Contains) ||
                         s.CoreTags.Contains(subject.Tag, StringComparer.OrdinalIgnoreCase) ||
                         s.Claims.Contains(subject.Tag, StringComparer.OrdinalIgnoreCase)) &&
                        (!s.CoreTags.Contains(owner.Tag, StringComparer.OrdinalIgnoreCase) ||
                         s.CoreTags.Contains(subject.Tag, StringComparer.OrdinalIgnoreCase) ||
                         s.Claims.Contains(subject.Tag, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(s =>
            {
                var dom = DominantCulture(s);
                var score = 0f;
                if (string.Equals(dom, subCulture, StringComparison.OrdinalIgnoreCase)) score += 3f;
                else if (subGroup.Length > 0 && string.Equals(DemographyCatalog.Culture(dom)?.Group, subGroup, StringComparison.OrdinalIgnoreCase)) score += 2f;
                if (s.CoreTags.Concat(s.Claims).Any(subCores.Contains)) score += 2.5f;
                score += EconomySystem.ForeignShare(s, owner.Tag) + s.Radicalism + s.SecessionPressure;
                return score;
            })
            .ToList();
        if (borderCandidates.Count == 0) return Array.Empty<int>();
        var pkg = Package(c, owner, borderCandidates[0], subject, maxRegions);
        return pkg.Length > 0 ? pkg : new[] { borderCandidates[0].MapIndex };
    }

    public static Nation MatchingSubject(Campaign c, Nation owner, ScenarioState seed)
    {
        var culture = DominantCulture(seed);
        var group = DemographyCatalog.Culture(culture)?.Group ?? "";
        var package = Package(c, owner, seed);
        if (package.Length == 0) return null;
        var seedCores = seed.CoreTags.Concat(seed.Claims)
            .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, owner.Tag, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return c.Nations.Where(n => n.AnswersTo(owner.Id) && !c.Fallen.Contains(n.Id) &&
                !n.SubjectIntegrating && !n.SubjectConspiring && !IsMicroCityEnclave(n) && n.LibertyDesire < .65f &&
                package.Any(id => TerritorialCoherence.TouchesOwnedGround(c, n, id)) &&
                (string.Equals(n.DeclaredCulture, culture, StringComparison.OrdinalIgnoreCase) ||
                 n.FoundedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase) ||
                 (group.Length > 0 && string.Equals(DemographyCatalog.Culture(n.DeclaredCulture)?.Group, group, StringComparison.OrdinalIgnoreCase)) ||
                 c.StatesOf(n).SelectMany(s => s.CoreTags.Concat(s.Claims)).Any(seedCores.Contains)))
            .OrderByDescending(n => string.Equals(n.DeclaredCulture, culture, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(n => n.Id)
            .FirstOrDefault();
    }

    public static bool MayGrant(Campaign c, Nation owner, Nation subject, IEnumerable<int> indices,
        out float price, out string refusal)
    {
        price = 0; refusal = "";
        if (c?.ProvinceMap is null || owner is null || subject is null || !c.Defines.RegionalAdministrationEnabled ||
            !c.FeatureEnabled("subjects")) { refusal = Loc.Get("Regional administration is disabled."); return false; }
        if (!subject.AnswersTo(owner.Id) || c.Fallen.Contains(subject.Id) || owner.IsSubject)
        { refusal = Loc.Get("Only a direct subject can receive an administrative land grant."); return false; }
        if (SubjectCatalog.Of(subject.SubjectType) is { ReceivesLandGrants: false } ||
            IsMicroCityEnclave(subject))
        { refusal = Loc.Get("This kind of dependency cannot receive regional land grants."); return false; }
        if (!RegionalResourcePartition.ValidAccounts(owner) || !RegionalResourcePartition.ValidAccounts(subject))
        { refusal = Loc.Get("Invalid national accounts prevent an administrative transfer."); return false; }
        if (subject.SubjectIntegrating || subject.SubjectConspiring || CampaignDiplomacy.AtWar(c, owner.Id, subject.Id))
        { refusal = Loc.Get("End integration or the subject's rebellion before transferring land."); return false; }
        var ids = (indices ?? Array.Empty<int>()).Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0 || ids.Length > 128) { refusal = Loc.Get("Select a valid regional package."); return false; }
        var available = CountryRelease.ReleasableStates(c, owner).ToDictionary(s => s.MapIndex);
        var secure = SecureRegions(c, owner, ids);
        if (ids.Any(id => !available.ContainsKey(id) || !secure.Contains(id)))
        { refusal = Loc.Get("Only owned, fully controlled non-capital regions can be granted."); return false; }
        var movedPeople = c.States.Where(state => ids.Contains(state.MapIndex)).Sum(state => state.Population);
        var ownerPeople = Math.Max(1L, c.StatesOf(owner).Sum(state => state.Population));
        var subjectPeople = c.StatesOf(subject).Sum(state => state.Population);
        var maxShare = Math.Clamp(c.Defines.SubjectLandGrantMaxPopulationShare, .01f, .75f);
        if (subjectPeople + movedPeople > ownerPeople * (double)maxShare)
        {
            refusal = Loc.Format("A subject cannot grow beyond {0:P0} of its overlord's population by land grants.", maxShare);
            return false;
        }
        // And it cannot grow past a multiple of itself. The overlord's share above is the
        // outer bound; this is the one that actually decides the size of the thing on the map,
        // because a city of six hundred thousand measured against a fifth of China is a
        // country and measured against itself is a city.
        var growthFactor = Math.Clamp(c.Defines.SubjectLandGrantMaxGrowthFactor, 1f, 64f);
        if (subjectPeople > 0 && subjectPeople + movedPeople > subjectPeople * growthFactor)
        {
            refusal = Loc.Format("A subject cannot be grown past {0:0.#} times its own population by land grants.", growthFactor);
            return false;
        }
        // A micro-territory stays a micro-territory. This is the rule the map is asking for:
        // Hong Kong may gain a district, it may not gain Sichuan.
        var enclaveCeiling = Math.Max(1, c.Defines.MicroEnclaveMaxRegions);
        if (IsMicroCityEnclave(subject) && c.StatesOf(subject).Length + ids.Length > enclaveCeiling)
        {
            refusal = Loc.Format("A micro-territory cannot hold more than {0} region(s).", enclaveCeiling);
            return false;
        }
        var reached = c.StatesOf(subject).Select(s => s.MapIndex).ToHashSet();
        var pending = ids.ToHashSet();
        bool progress;
        do
        {
            progress = false;
            foreach (var id in pending.OrderBy(id => id).ToArray())
                if (TerritorialCoherence.AdjacentRegions(c, id).Any(reached.Contains))
                { pending.Remove(id); reached.Add(id); progress = true; }
        } while (progress && pending.Count > 0);
        if (pending.Count > 0) { refusal = Loc.Get("The complete package must connect to the subject's existing territory."); return false; }
        if (c.ElapsedDays - subject.LastSubjectInteractionDay < Math.Max(0, c.Defines.SubjectLandGrantCooldownDays))
        { refusal = Loc.Get("The subject's administration is still reorganising after the last interaction."); return false; }
        var unit = c.Defines.SubjectLandGrantPoliticalCostPerRegion;
        if (!float.IsFinite(unit) || unit < 0) { refusal = Loc.Get("Invalid administrative transfer price."); return false; }
        price = unit * ids.Length;
        if (!float.IsFinite(owner.PoliticalPoints) || owner.PoliticalPoints < price)
        { refusal = Loc.Format("Requires {0:0.#} political points.", price); return false; }
        return true;
    }

    private static HashSet<int> SecureRegions(Campaign c, Nation owner, IEnumerable<int> regions)
    {
        var map = c.ProvinceMap; var wanted = regions.ToHashSet();
        var found = new HashSet<int>(); var blocked = new HashSet<int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            int id = map.ProvinceToState[p];
            if (!wanted.Contains(id)) continue;
            found.Add(id);
            if (map.ProvinceToNation[p] != owner.MapIndex || c.ProvinceController[p] != owner.MapIndex) blocked.Add(id);
        }
        found.ExceptWith(blocked);
        return found;
    }

    public static bool Grant(Campaign c, Nation owner, Nation subject, IEnumerable<int> indices, out string feedback)
    {
        var ids = (indices ?? Array.Empty<int>()).Distinct().OrderBy(id => id).ToArray();
        if (!MayGrant(c, owner, subject, ids, out var price, out feedback)) return false;
        var movedPeople = c.States.Where(s => ids.Contains(s.MapIndex)).Sum(s => s.Population);
        var share = Math.Clamp(movedPeople / (double)Math.Max(1, c.StatesOf(owner).Sum(s => s.Population)), 0, 1);
        var ownerCapacity = ProductionCapacity.Of(owner); var subjectCapacity = ProductionCapacity.Of(subject);
        var gdp = owner.Gdp * (float)share;
        var provinces = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => ids.Contains(c.ProvinceMap.ProvinceToState[p])).ToArray();
        owner.PoliticalPoints -= price;
        StatePartition.Annex(c, provinces, subject, asConquest: false);
        RegionalResourcePartition.RecountResidents(c, owner, subject);
        owner.Gdp -= gdp; subject.Gdp += gdp;
        owner.ProductionReferenceInitialized = subject.ProductionReferenceInitialized = true;
        owner.ProductionReferencePopulation = owner.Population; subject.ProductionReferencePopulation = subject.Population;
        owner.ProductionReferenceGdp = ownerCapacity * (1f - (float)share);
        subject.ProductionReferenceGdp = subjectCapacity + ownerCapacity * (float)share;
        foreach (var state in c.States.Where(s => ids.Contains(s.MapIndex)))
        {
            state.SecessionPressure = MathF.Max(0f, state.SecessionPressure * 0.70f);
            state.Radicalism = MathF.Max(0f, state.Radicalism - 0.10f);
        }
        // Short-term gratitude for territorial expansion; long-term LibertyDesireOf scales with their larger size.
        var gratitude = Math.Min(0.18f, 0.06f * ids.Length);
        subject.SubjectLibertyModifier = MathF.Max(-0.55f, subject.SubjectLibertyModifier - gratitude);
        subject.LibertyDesire = MathF.Max(0f, subject.LibertyDesire - gratitude);
        subject.LastSubjectInteractionDay = c.ElapsedDays;
        subject.SubjectIntegrationProgress = MathF.Max(0, subject.SubjectIntegrationProgress * (1f - (float)share));
        c.LocateCapitals(); CampaignDiplomacy.ForgetBorders();
        NationalModifiers.Invalidate(owner); NationalModifiers.Invalidate(subject);
        c.MarkStrategicChange(); c.MarkMapChanged();
        c.Chronicle.Record(c, "subject_land_grant", owner.Id, subject.Id, place: string.Join(",", ids));
        feedback = Loc.Format("Transferred {0} region(s) to {1}.", ids.Length, subject.DisplayName);
        return true;
    }
}

public sealed class GrantSubjectRegionsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int SubjectId { get; init; }
    public int[] StateMapIndices { get; init; } = Array.Empty<int>();
    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        RegionalAdministration.Grant(c, c.NationById(IssuerId), c.NationById(SubjectId), StateMapIndices, out _);
    }
}
