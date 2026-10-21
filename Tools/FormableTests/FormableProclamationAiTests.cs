using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class FormableProclamationAiTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS AI proclamation: " + message); }

    private static void Day(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, day * 24L);

    private static (Campaign C, Nation Founder, Nation Other, int[] Land) Fixture(string tag = "QPA", string culture = "czech")
    {
        var c = new Campaign(20261021) { ScenarioId = "proclamation_fixture" };
        var n = new Nation { Id = 0, Tag = tag, MapIndex = 501, Name = "Founder", Population = 3000000,
            Gdp = 100, Treasury = 100, Stability = .95f, Ideology = "liberalism", Government = "parliamentary_republic",
            TaxRate = .3f, MilitarySpending = .04f, Literacy = .9f, StandardOfLiving = .8f, ConstructionCapacity = 1 };
        n.FoundedCultures.Add(culture);
        var other = new Nation { Id = 1, Tag = "QPB", MapIndex = 502, Name = "Other", Population = 1000000,
            Gdp = 100, Treasury = 100, Stability = .9f, Ideology = "liberalism", Government = "parliamentary_republic" };
        c.Nations.Add(n); c.Nations.Add(other);
        for (var i = 0; i < 4; i++)
        {
            var state = new ScenarioState { Id = "proclamation_" + i, Name = "Region " + i, MapIndex = 3001 + i,
                NationMapIndex = i < 3 ? 501 : 502, NationId = i < 3 ? tag : other.Tag, Population = 1000000 };
            state.PopulationGroups.Add(new PopulationGroup { Culture = culture, Religion = "catholic", Size = 1000000 });
            c.States.Add(state);
        }
        var states = new ushort[64]; var owners = new ushort[64];
        for (var y = 0; y < 4; y++) for (var x = 0; x < 16; x++)
        { states[y * 16 + x] = (ushort)(3001 + x / 4); owners[y * 16 + x] = (ushort)(x < 12 ? 501 : 502); }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 16, 4, _ => false));
        c.ConfigureSinglePlayer(other.Id);
        c.Defines.AiFormableEarliestDay = 0;
        c.Defines.AiFormableReviewDays = 1;
        Day(c, 2000);
        return (c, n, other, Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToNation[p] == n.MapIndex).ToArray());
    }

    private static FormableNation Definition(Nation n, int[] land, string id = "proclamation_fixture") =>
        new(id, "Fixture union", "A test foundation", new[] { n.Tag }, Array.Empty<string>(), land,
            .7f, .02f, .04f, .015f, Array.Empty<string>(), Array.Empty<string>(), "")
        { AiFoundation = true, EligibleCultures = new[] { "czech" } };

    private static void Add(FormableNation d)
    {
        var errors = new List<string>();
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [d.Id] = new { name = d.Name, description = d.Description, eligible_tags = d.EligibleTags,
                eligible_cultures = d.EligibleCultures, required_tags = d.RequiredTags, required_provinces = d.RequiredProvinces,
                minimum_stability = d.MinimumStability, gdp_cost_share = d.CostShare, governance_gain = d.GovernanceGain,
                productivity_gain = d.ProductivityGain, required_ideology = d.RequiredIdeology, required_government = d.RequiredGovernment,
                ai_foundation = d.AiFoundation, peaceful_accession = d.PeacefulAccession, subject_integration = d.SubjectIntegration,
                required_religions = d.RequiredReligions, required_religion_groups = d.RequiredReligionGroups,
                minimum_religion_share = d.MinimumReligionShare, supersedes = d.Supersedes }
        }));
        FormableNations.Merge(data.RootElement, errors);
        if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
    }

    private static void Profiles(string json)
    {
        using var data = JsonDocument.Parse(json);
        var errors = new List<string>(); FormableProclamationAi.Merge(data.RootElement, errors);
        if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
    }

    public static void Run()
    {
        FormableNations.ResetCatalog(); FormableProclamationAi.ResetCatalog();
        try
        {
            ReadyAndReadOnly();
            CountryAndPolitics();
            Constraints();
            CooldownsAndExecution();
            PartnersAndReligion();
            ProfilesAndSave();
            ActualSovietEurasianChoice();
            GlobalAiLoop();
            RuleFingerprints();
            PartialRestorationKeepsForeignLand();
        }
        finally { FormableNations.ResetCatalog(); FormableProclamationAi.ResetCatalog(); }
    }

    private static void ReadyAndReadOnly()
    {
        var (c, n, _, land) = Fixture(); var d = Definition(n, land);
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        var status = FormableProclamationAi.Evaluate(c, n, d);
        Check(status.CanProclaim && status.IdentityShare == 1 && status.Cost == 2 && status.Reserve >= 2,
            "a settled independent government can found a fully secured non-accession project at the real price");
        Check(before == CampaignFingerprint.Of(c) && random == c.Random.State,
            "assessment changes no campaign field, account, timer or random draw");
        Check(!FormableProclamationAi.Evaluate(c, n, d with { AiFoundation = false }).CanProclaim,
            "an explicit AI opt-out remains authoritative");
        Add(d);
        var chosen = FormableProclamationAi.Choose(c, n);
        Check(chosen?.Definition.Id == d.Id && CampaignFingerprint.Of(c) == before && random == c.Random.State,
            "choosing a complete project is also read-only");
        c.Defines.AiFormableMinimumScore = 2;
        Check(FormableProclamationAi.Choose(c, n) is null,
            "an available button need not be an identity the government wants");
    }

    private static void CountryAndPolitics()
    {
        var (c, n, _, land) = Fixture();
        var royal = Definition(n, land, "aa_royal_fixture"); var civic = Definition(n, land, "zz_civic_fixture");
        Add(royal); Add(civic);
        Profiles("""{"aa_royal_fixture":{"preferred_governments":["absolute_monarchy"],"preferred_ideologies":["conservatism"],"preferred_courses":["institutional_continuity"]},"zz_civic_fixture":{"preferred_governments":["parliamentary_republic"],"preferred_ideologies":["liberalism"],"preferred_courses":["civic_pluralism"]}}""");
        Check(FormableProclamationAi.Choose(c, n)?.Definition.Id == civic.Id,
            "a liberal parliamentary cabinet chooses its civic project, not the alphabetically first restoration");
        n.Government = "absolute_monarchy"; n.Ideology = "conservatism";
        Check(FormableProclamationAi.Choose(c, n)?.Definition.Id == royal.Id,
            "the same territory under a monarchist cabinet produces a different reasoned preference");
        // AttachProvinceTopology initializes a real social agenda. Start this isolated
        // feature-boundary comparison with no convictions, not an unknown existing bonus.
        n.NationalCourse.Initialized = false; n.NationalCourse.Convictions.Clear(); NationalModifiers.Invalidate(n);
        var baseline = FormableProclamationAi.Evaluate(c, n, royal).Score;
        n.NationalCourse.Initialized = true; n.NationalCourse.Convictions["institutional_continuity"] = .8f;
        Check(FormableProclamationAi.Evaluate(c, n, royal).Score > baseline,
            "live social priorities strengthen the matching project");
        c.SetFeature("national_courses", false);
        Check(Math.Abs(FormableProclamationAi.Evaluate(c, n, royal).Score - baseline) < .00001f,
            "disabled courses leave no hidden political preference");
        var unrelated = royal with { EligibleTags = new[] { "QPX" }, EligibleCultures = Array.Empty<string>() };
        Check(!FormableProclamationAi.Evaluate(c, n, unrelated).CanProclaim,
            "owning the land alone gives an unrelated country no right to proclaim it");
        var identityBefore = FormableProclamationAi.Evaluate(c, n, royal).Score;
        foreach (var s in c.States) foreach (var p in s.PopulationGroups) p.Culture = "han";
        Check(FormableProclamationAi.Evaluate(c, n, royal).Score < identityBefore,
            "resident peoples, not the tag alone, contribute to founding confidence");
    }

    private static void Constraints()
    {
        var (c, n, other, land) = Fixture(); var d = Definition(n, land);
        n.IsHuman = true; Delegation.Set(n, Delegation.Diplomacy, true); Delegation.Set(n, Delegation.Cabinet, true);
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim && FormableProclamationAi.Choose(c, n) is null,
            "delegating ordinary diplomacy or cabinet work does not authorize renaming a human's country");
        n.IsHuman = false; n.IsPlayer = true;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "the local player seat is never auto-proclaimed"); n.IsPlayer = false;
        c.HumanSeats.Add(n.Id);
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "a human seat remains protected even when transient country flags are inconsistent"); c.HumanSeats.Remove(n.Id);
        n.SubjectType = "puppet"; n.OverlordId = other.Id;
        Check(!FormableNations.Evaluate(c, n, d).CanForm && !FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "a subject cannot invent its own sovereign union through either AI or the shared founding command"); n.SubjectType = "";
        c.Fallen.Add(n.Id);
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "a fallen state cannot proclaim"); c.Fallen.Remove(n.Id);
        n.Movement = new MovementState { Landless = true };
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "a landless state cannot proclaim"); n.Movement = null;
        n.Movement = new MovementState { Landless = false, StatehoodDay = 0 };
        Check(FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "an established sovereign successor retains founding rights despite its movement origins");
        n.Movement.StatehoodDay = c.ElapsedDays;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "a newborn movement state first consolidates its own government");
        n.Movement.StatehoodDay = 0; n.Movement.Dissolved = true;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "a dissolved movement cannot exploit a remembered statehood day"); n.Movement = null;
        c.Defines.AiFormableProclamations = false;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "the dedicated AI switch disables proclamation"); c.Defines.AiFormableProclamations = true;
        c.SetFeature("formable_nations", false);
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "the existing mechanic switch disables the new planner too"); c.SetFeature("formable_nations", true);
        c.Wars.Add(new StrategicWar { AttackerId = n.Id, DefenderId = other.Id });
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "wartime does not bypass the player's peace requirement"); c.Wars.Clear();
        n.Stability = .4f;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "unsettled domestic politics block founding"); n.Stability = .95f;
        c.ProvinceController[land[0]] = (ushort)other.MapIndex;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "ownership on paper does not replace actual territorial control"); c.ProvinceController[land[0]] = (ushort)n.MapIndex;
        n.Treasury = 3;
        Check(FormableNations.Evaluate(c, n, d).CanForm && !FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "the AI can defer an affordable proclamation to protect its cash runway"); n.Treasury = 100;
        n.PaymentArrears = 1;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "unpaid public bills take priority over discretionary founding"); n.PaymentArrears = 0;
        n.DebtCondition = DebtCondition.Default;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim, "a sovereign default takes priority even with cash in hand");
    }

    private static void CooldownsAndExecution()
    {
        var (c, n, _, land) = Fixture();
        var first = Definition(n, land, "base_proclamation_fixture");
        var next = Definition(n, land, "greater_proclamation_fixture") with { Supersedes = new[] { first.Id } };
        Add(first); Add(next);
        n.FormedNationId = first.Id; n.FormedNationHistory.Add(first.Id); n.FormedNationDay = c.ElapsedDays;
        Check(FormableNations.Evaluate(c, n, next).CanForm && !FormableProclamationAi.Evaluate(c, n, next).CanProclaim,
            "a permitted successor waits for the previous proclamation to settle");
        n.FormedNationDay = c.ElapsedDays - c.Defines.AiFormableMinimumIntervalDays;
        Check(FormableProclamationAi.Evaluate(c, n, next).CanProclaim,
            "a legitimate Supersedes path remains available after its cooldown");
        n.UnionCohesion = .3f;
        Check(!FormableProclamationAi.Evaluate(c, n, next).CanProclaim, "a fracturing union is consolidated before another expansion"); n.UnionCohesion = 1;
        n.LastSecessionDay = c.ElapsedDays - 10;
        Check(!FormableProclamationAi.Evaluate(c, n, next).CanProclaim, "recent territorial secession creates a recovery pause"); n.LastSecessionDay = -9999;
        n.DomesticCrisis.CoupPressure = 1;
        Check(!FormableProclamationAi.Evaluate(c, n, next).CanProclaim, "an impending coup is a real reason to defer another identity"); n.DomesticCrisis.CoupPressure = 0;
        var cash = n.Treasury; var cost = FormableNations.Evaluate(c, n, next).Cost;
        var random = c.Random.State;
        FormableProclamationAi.Tick(c);
        Check(n.FormedNationId == next.Id && n.Treasury == cash - cost && n.FormedNationHistory.Contains(next.Id) && n.FormedNationDay == c.ElapsedDays,
            "the actual director executes one standard priced command and records its identity history");
        var after = CampaignFingerprint.Of(c); FormableProclamationAi.Tick(c);
        Check(after == CampaignFingerprint.Of(c) && random == c.Random.State,
            "repeating a review on the same day spends nothing and draws no random numbers");
        Check(!FormableProclamationAi.Evaluate(c, n, first).CanProclaim,
            "previous identities cannot be cycled for repeated founding rewards");
    }

    private static void PartnersAndReligion()
    {
        var (c, n, other, land) = Fixture();
        var all = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).ToArray();
        var d = Definition(n, all, "partner_proclamation_fixture") with { SubjectIntegration = true };
        other.IsHuman = false; other.IsPlayer = false; c.HumanSeats.Clear();
        other.SubjectType = "puppet"; other.OverlordId = n.Id; other.LibertyDesire = 1;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "the planner cannot absorb a disloyal partner by merely choosing its flag");
        other.LibertyDesire = 0;
        Check(FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "an existing loyal integration route uses the same authoritative partner requirement");
        other.IsHuman = true;
        Check(!FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "loyalty never replaces explicit consent for a human-controlled single-player dependency");
        var consenting = d with { PeacefulAccession = true };
        other.DecisionAvailableDay[PeacefulFormation.ConsentKey(n.Id, d.Id)] = c.ElapsedDays + 10;
        Check(FormableProclamationAi.Evaluate(c, n, consenting).CanProclaim,
            "an explicit unexpired human accession treaty remains a valid single-player route");
        other.DecisionAvailableDay.Clear();
        other.IsHuman = false;
        c.HumanSeats.Add(other.Id);
        Check(!PeacefulFormation.Participates(c, n, other, d) && !FormableNations.Evaluate(c, n, d).CanForm &&
                !FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "a recorded human partner seat is protected even if its transient human flag is missing"); c.HumanSeats.Clear();
        var religious = d with { RequiredReligions = new[] { "sunni" }, MinimumReligionShare = .6f };
        Check(!FormableProclamationAi.Evaluate(c, n, religious).CanProclaim,
            "neither political preference nor a cultural stereotype replaces the actual religious mandate");
        foreach (var s in c.StatesOf(n)) foreach (var p in s.PopulationGroups) p.Religion = "sunni";
        Check(FormableProclamationAi.Evaluate(c, n, religious).CanProclaim,
            "a changed resident religious mandate genuinely changes readiness");
    }

    private static void ProfilesAndSave()
    {
        var errors = new List<string>();
        var prior = FormableProclamationAi.Preference("soviet_union");
        foreach (var json in new[] { """{"soviet_union":{"ai_priority":1.5}}""",
                     """{"soviet_union":{"preferred_ideologies":["not_an_ideology"]}}""",
                     """{"soviet_union":{"preferred_courses":["not_a_course"]}}""" })
        { using var document = JsonDocument.Parse(json); FormableProclamationAi.Merge(document.RootElement, errors); }
        Check(errors.Count == 3 && ReferenceEquals(prior, FormableProclamationAi.Preference("soviet_union")),
            "invalid mod preferences diagnose errors and preserve the previous valid policy");
        Profiles("""{"soviet_union":{"ai_priority":0.6}}""");
        Check(FormableProclamationAi.Preference("soviet_union").PreferredIdeologies.SequenceEqual(prior.PreferredIdeologies),
            "a priority-only mod overlay retains political and social preferences");
        FormableProclamationAi.ResetCatalog();
        var (c, n, _, land) = Fixture(); var d = Definition(n, land, "save_proclamation_fixture"); Add(d);
        // Synthetic countries must have contemporary opened books before a strict
        // snapshot comparison; otherwise restore correctly migrates their legacy default.
        SovereignFinance.NormalizeAccounts(c); EconomySystem.Explain(c);
        foreach (var country in c.Nations) country.OutputScale = 1;
        var choice = FormableProclamationAi.Choose(c, n);
        n.DecisionAvailableDay[FormableProclamationAi.ReviewKey] = c.ElapsedDays + 30;
        var snapshot = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "proclamation-ai"), "proclamation-ai");
        Check(snapshot.Success, "the real save/network snapshot accepts the additive policy definitions");
        var restored = snapshot.Campaign; var founder = restored.NationById(n.Id);
        restored.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(founder.DecisionAvailableDay.GetValueOrDefault(FormableProclamationAi.ReviewKey) == c.ElapsedDays + 30 &&
            restored.Defines.AiFormableMinimumIntervalDays == c.Defines.AiFormableMinimumIntervalDays,
            "the existing saved ledger and defines preserve review cooldown and policy across reload");
        Check(restored.Random.State == c.Random.State && CampaignFingerprint.Of(restored) == CampaignFingerprint.Of(c),
            "the actual snapshot plus reattached tactical map restores the complete fingerprint and random stream");
        Check(FormableProclamationAi.Choose(restored, founder)?.Definition.Id == choice?.Definition.Id,
            "the same saved situation produces the same best proclamation without new random state");
    }

    private static void GlobalAiLoop()
    {
        var (c, n, _, land) = Fixture("QPG");
        var d = Definition(n, land, "global_proclamation_fixture"); Add(d);
        Profiles("""{"global_proclamation_fixture":{"ai_priority":1,"preferred_ideologies":["liberalism"],"preferred_governments":["parliamentary_republic"]}}""");
        n.ConsolidationProject = d.Id;
        AiStatecraft.Tick(c);
        Check(n.FormedNationId == d.Id && c.Chronicle.Entries.Any(e => e.Kind == "state_formed" && e.ActorId == n.Id),
            "the real global AI statecraft loop reaches the planner and records a real proclamation");
    }

    private static void RuleFingerprints()
    {
        var changes = new (string Name, Action<Defines> Change)[]
        {
            (nameof(Defines.AiFormableProclamations), d => d.AiFormableProclamations = !d.AiFormableProclamations),
            (nameof(Defines.AiFormableReviewDays), d => d.AiFormableReviewDays++),
            (nameof(Defines.AiFormableEarliestDay), d => d.AiFormableEarliestDay++),
            (nameof(Defines.AiFormableMinimumIntervalDays), d => d.AiFormableMinimumIntervalDays++),
            (nameof(Defines.AiFormableCrisisRecoveryDays), d => d.AiFormableCrisisRecoveryDays++),
            (nameof(Defines.AiFormableTreasuryReserveShare), d => d.AiFormableTreasuryReserveShare += .01f),
            (nameof(Defines.AiFormableMinimumScore), d => d.AiFormableMinimumScore += .01f)
        };
        foreach (var change in changes)
        {
            var (c, _, _, _) = Fixture(); var before = CampaignFingerprint.Of(c);
            change.Change(c.Defines);
            Check(CampaignFingerprint.Of(c) != before, "different authoritative rules are detected by the fingerprint: " + change.Name);
        }
    }

    private static void ActualSovietEurasianChoice()
    {
        // Real catalogue definitions and real policy profiles, with a tiny custom scenario
        // whose authored lands are all secured. No manual Evaluate substitute or free cost.
        var (c, n, _, land) = Fixture("RUS", "russian");
        var soviet = FormableNations.Find("soviet_union"); var eurasian = FormableNations.Find("eurasian_union");
        foreach (var tag in soviet.RequiredTags.Concat(eurasian.RequiredTags).Distinct()) c.ScenarioTerritories[tag] = land;
        n.Government = "one_party_state"; n.Ideology = "communism";
        Check(FormableNations.Evaluate(c, n, soviet).CanForm && FormableNations.Evaluate(c, n, eurasian).CanForm,
            "both actual Soviet and Eurasian projects are territorially ready in the same world");
        Check(FormableProclamationAi.Evaluate(c, n, soviet).Score > FormableProclamationAi.Evaluate(c, n, eurasian).Score &&
            FormableProclamationAi.Choose(c, n)?.Definition.Id == soviet.Id,
            "a communist party cabinet prefers the actual Soviet project rather than clicking every formable");
        n.Government = "presidential_republic"; n.Ideology = "liberalism";
        Check(FormableProclamationAi.Evaluate(c, n, eurasian).Score > FormableProclamationAi.Evaluate(c, n, soviet).Score &&
            FormableProclamationAi.Choose(c, n)?.Definition.Id == eurasian.Id,
            "a market-oriented presidential cabinet prefers the actual Eurasian project on identical land");
        Check(FormableNations.Evaluate(c, n, soviet).CanForm,
            "soft AI preferences do not remove the player's existing noncommunist Soviet restoration option");
    }

    private static void PartialRestorationKeepsForeignLand()
    {
        PartialRestoration(false);
        PartialRestoration(true);
    }

    private static void PartialRestoration(bool humanRemainder)
    {
        var (c, n, other, land) = Fixture();
        if (!humanRemainder) { other.IsHuman = false; other.IsPlayer = false; c.HumanSeats.Clear(); }
        var foreign = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).First(p => c.ProvinceMap.ProvinceToNation[p] == other.MapIndex);
        var gone = new Nation { Id = 2, Tag = "CZE", Name = "Czechia", MapIndex = 503, Population = 0, Color = "#123456" };
        c.Nations.Add(gone);
        c.ScenarioTerritories[gone.Tag] = new[] { land[0], foreign };
        var d = FormableNations.Find(c, "restore_cze");
        var readiness = FormableNations.Evaluate(c, n, d);
        Check(d is { Restores: "CZE", AiFoundation: true } && readiness.CanForm && readiness.Owned == 1 && readiness.Total == 2,
            "the generated restoration legitimately permits a cultural successor holding half its authored homeland");
        Check(FormableProclamationAi.Evaluate(c, n, d).CanProclaim,
            "a real restoration right provides an AI motive without annexing the " + (humanRemainder ? "human" : "AI") + " remainder");
        Check(!PeacefulFormation.Participates(c, n, other, d), "a living foreign owner has not consented merely because its land is claimed");
        var owners = (ushort[])c.ProvinceMap.ProvinceToNation.Clone(); var control = (ushort[])c.ProvinceController.Clone();
        var population = other.Population; var cash = other.Treasury; var gdp = other.Gdp;
        new FormNationCommand { IssuerId = n.Id, FormableId = d.Id }.Apply(c);
        Check(n.FormedNationId == d.Id && n.FormedNationHistory.Contains(d.Id),
            "the ordinary founding command really proclaims the partial restoration");
        Check(c.ProvinceMap.ProvinceToNation.SequenceEqual(owners) && c.ProvinceController.SequenceEqual(control) &&
            other.Population == population && other.Treasury == cash && other.Gdp == gdp && !c.Fallen.Contains(other.Id),
            "restoration never annexes the remaining foreign homeland or erases an unconsenting living partner");
    }
}
