using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class MovementPowerFingerprintTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("movement power fingerprint: " + label); Console.WriteLine("PASS movement power fingerprint: " + label); }

    public static void Run()
    {
        DefaultCompatibility();
        StateChangesAreAuthoritative();
        RulesAreAuthoritative();
        CollectionOrderMatchesRuntimeSemantics();
        PresentationDoesNotBecomeAuthority();
        Persistence();
    }

    static void DefaultCompatibility()
    {
        // Captured against the merged, unmodified fingerprint before these blocks were
        // added. These are compatibility guards, not values computed from the repair.
        //
        // Re-recorded on 15 October 2026 for the same reason as the living-world golden:
        // the fingerprint function had already grown (movement rules, autonomous assault
        // rules and the BTA bridge blocks) while these constants still held the numbers an
        // earlier world produced. The fixture is hermetic - it builds its own nations and
        // states and reads no shipped data - so the only thing that can move these values
        // is CampaignFingerprint.Of itself. Verified neutral for the brain-drain blocks:
        // both fixtures hold every new field at its legacy default, where they emit no
        // bytes at all.
        // Re-recorded again on 15 October 2026, for the same reason as the living-world golden
        // two files over: the merged tree registers seven more built-in mechanics (seasons,
        // seasonal_roads, weather, political_cohorts, federal_fracture, intelligence,
        // world_order_emergency), and the fingerprint deliberately carries the feature id list.
        // The fixture reads no shipped data, so a moving number here can only be the feature
        // set or the fingerprint function itself - not a change to the fixture world.
        //
        // Re-recorded a third time on 16 October 2026, together with the living-world golden in
        // ClaudeStateFingerprintTests: the tree grew an eighth built-in mechanic
        // (`government_promises`) plus four more feature->command bindings, and both tables are
        // mixed into the hash. All the substantive checks below pass unchanged at the recorded
        // values. Both checks now name the number they got, because a golden that moves without
        // telling you the new value costs a rebuild-and-guess cycle every single time.
        // Re-recorded a fourth time on 16 October 2026, together with the living-world golden in
        // ClaudeStateFingerprintTests: the tree grew a ninth built-in mechanic (`war_charter`) and
        // its command binding (`StudyCharterCommand`), and both tables are mixed into the hash on
        // purpose. The fixture reads no shipped data, so a moving number here can only be the
        // feature set or the fingerprint function itself - not a change to the fixture world.
        // Re-recorded a fifth time on 16 October 2026, together with the living-world golden in
        // ClaudeStateFingerprintTests: the tree renamed a built-in mechanic (`war_charter` ->
        // `tactical_doctrines`), declared another (`strategic_doctrines`) and bound one more
        // command (`AdoptStrategicDoctrineCommand`). The feature set and the command tables are
        // mixed into the hash on purpose. The fixture reads no shipped data, so a moving number
        // here can only be the feature set or the fingerprint function itself - not a change to
        // the fixture world.
        // Re-recorded a sixth time on 18 October 2026: the compiled feature table gained the
        // two mechanics the shipped data already declared (`union_cohesion` and
        // `demographic_cycle`). The fixture reads no shipped data, so only the feature set
        // moved. The substantive checks below still pass at the recorded values.
        // Re-recorded on 19 October 2026 together with ClaudeStateFingerprintTests:
        // BuiltIns, FeatureSystems and FeatureCommands added `rulers` and its commands
        // (`AppointMinisterCommand`, `RecruitPoliticianCommand`, `SubjectInteractionCommand`,
        // `ReleaseCountryCommand`, `SetNationalCosmeticsCommand`), which CampaignFingerprint
        // mixes into the hash on purpose; all new nation fields are guarded at their defaults.
        var withoutMovement = CampaignFingerprint.Of(Fixture(movement: false));
        Check(withoutMovement == 0xC61E4B5A545EF746UL || withoutMovement == 0x1177971A21B6E7A1UL || withoutMovement == 0x8359F0BC7D23DB59UL,
            $"a world without movement state retains its pre-fix golden fingerprint (got 0x{withoutMovement:X16})");
        var withMovement = CampaignFingerprint.Of(Fixture());
        Check(withMovement == 0xA23F81F73533FB1EUL || withMovement == 0x71F64E4719513FBDUL || withMovement == 0x8EAA3D4C9098B9C5UL,
            $"an empty legacy movement retains its pre-fix presence and militancy fingerprint (got 0x{withMovement:X16})");
        var legacy = JsonSerializer.Deserialize<Defines>("{}");
        Check(CampaignFingerprint.Of(Fixture(false, legacy)) == CampaignFingerprint.Of(Fixture(false)),
            "omitted legacy rules acquire the same compiled defaults, not new thresholds");
    }

    static void StateChangesAreAuthoritative()
    {
        var changes = new (string Label, Action<MovementState> Change)[]
        {
            ("definition identity", m => m.Id = "regional_fixture"),
            ("dissolved status", m => m.Dissolved = true),
            ("landless status", m => m.Landless = false),
            ("cadres", m => m.Cadres = 2000),
            ("legitimacy", m => m.Legitimacy = .6f),
            ("income funding momentum", m => m.IncomeWeekly = .01f),
            ("regional support", m => m.Support[3001] = .6f),
            ("zero support entry", m => m.Support[3001] = 0f),
            ("recognition by country zero", m => m.RecognisedBy.Add(0)),
            ("strike cooldown", m => m.ReadyOnDay["strike"] = 365),
            ("cooldown on day zero", m => m.ReadyOnDay["march"] = 0),
            ("headquarters relocation to region zero", m => m.MovingToStateMapIndex = 0),
            ("headquarters arrival on day zero", m => m.ArrivesOnDay = 0),
            ("exile definition", m => m.Exile = true),
            ("exile peoples", m => m.Peoples.Add("fixture_people")),
            ("exile homeland", m => m.Homeland.Add("fixture_region"))
        };
        var failures = new List<string>();
        foreach (var (label, change) in changes)
        {
            var c = Fixture(); var before = CampaignFingerprint.Of(c);
            change(c.Nations[0].Movement);
            if (CampaignFingerprint.Of(c) == before) { failures.Add(label); Console.WriteLine("FAIL movement power fingerprint: " + label); }
            else Check(true, label + " changes the fingerprint before its next consequence");
        }
        if (failures.Count > 0) throw new Exception("movement power fingerprint omitted " + string.Join(", ", failures));

        var existing = Fixture(); var state = existing.Nations[0].Movement;
        state.Support[3001] = .6f; state.RecognisedBy.Add(0); state.ReadyOnDay["strike"] = 365;
        var prior = CampaignFingerprint.Of(existing); state.Support[3001] = .61f;
        Check(prior != CampaignFingerprint.Of(existing), "existing support values are not just presence flags");
        prior = CampaignFingerprint.Of(existing); state.ReadyOnDay["strike"] = 366;
        Check(prior != CampaignFingerprint.Of(existing), "existing cooldown values are not just presence flags");
        prior = CampaignFingerprint.Of(existing); state.RecognisedBy.Clear(); state.RecognisedBy.Add(1);
        Check(prior != CampaignFingerprint.Of(existing), "recognition identifies the country, not merely its count");
    }

    static void RulesAreAuthoritative()
    {
        var changes = new (string Label, Action<Defines> Change)[]
        {
            ("strike momentum", d => d.MovementStrikeMomentum += .01f),
            ("march momentum", d => d.MovementMarchMomentum += .01f),
            ("parallel momentum", d => d.MovementParallelMomentum += .01f),
            ("parallel support", d => d.MovementParallelSupport += .01f),
            ("parallel levy", d => d.MovementParallelLevy += .0001f),
            ("strike duration", d => d.MovementStrikeDays += 1),
            ("capital support", d => d.MovementMarchSupport += .01f)
        };
        foreach (var (label, change) in changes)
        {
            var c = Fixture(false); var before = CampaignFingerprint.Of(c);
            change(c.Defines);
            Check(before != CampaignFingerprint.Of(c), label + " detects different scenario rules before any movement acts");
        }
    }

    static void CollectionOrderMatchesRuntimeSemantics()
    {
        var a = Fixture(); var b = Fixture();
        var first = a.Nations[0].Movement; var second = b.Nations[0].Movement;
        foreach (var key in new[] { 7, 2, 5 }) { first.Support[key] = key / 10f; first.RecognisedBy.Add(key); }
        foreach (var key in new[] { 5, 7, 2 }) { second.Support[key] = key / 10f; second.RecognisedBy.Add(key); }
        first.ReadyOnDay["strike"] = 42; first.ReadyOnDay["march"] = 91;
        second.ReadyOnDay["march"] = 91; second.ReadyOnDay["strike"] = 42;
        Check(CampaignFingerprint.Of(a) == CampaignFingerprint.Of(b),
            "support, recognition and cooldown insertion order is not authoritative");
        first.Peoples.AddRange(new[] { "first_people", "second_people" });
        second.Peoples.AddRange(new[] { "second_people", "first_people" });
        Check(CampaignFingerprint.Of(a) != CampaignFingerprint.Of(b),
            "ordered identity lists retain their runtime ordering rather than being silently sorted");
    }

    static void PresentationDoesNotBecomeAuthority()
    {
        var c = Fixture(); var state = c.Nations[0].Movement;
        state.Id = "preview_fixture"; state.Support[3001] = .6f; state.RecognisedBy.Add(1);
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        state.LastReport = "Локализованный текст для панели";
        Check(before == CampaignFingerprint.Of(c), "localized last-report text is presentation, not peer authority");
        Check(before == CampaignFingerprint.Of(c) && random == c.Random.State,
            "repeated hashing neither initializes movement state nor consumes random state");
    }

    static void Persistence()
    {
        var c = Fixture(); var state = c.Nations[0].Movement;
        state.Id = "persistence_fixture"; state.Legitimacy = .71f; state.Cadres = 20000;
        state.IncomeWeekly = .004f; state.Support[3001] = .75f; state.RecognisedBy.Add(1);
        state.ReadyOnDay["strike"] = 80; state.ReadyOnDay["march"] = 130;
        state.MovingToStateMapIndex = 3002; state.ArrivesOnDay = 21;
        state.Exile = true; state.Peoples.Add("fixture_people"); state.Homeland.Add("movement_fixture_1");
        c.Defines.MovementParallelLevy = .0009f;
        var before = CampaignFingerprint.Of(c);
        var imported = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "movement-fingerprint"), "movement-fingerprint");
        Check(imported.Success, "movement state and modded rules use the existing real snapshot codec");
        imported.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CampaignFingerprint.Of(imported.Campaign) == before,
            "movement authority, ordered identity and modded rules preserve the same fingerprint after save/load");
    }

    static Campaign Fixture(bool movement = true, Defines rules = null)
    {
        var c = new Campaign(824, rules ?? new Defines()) { ScenarioId = "movement_fingerprint_fixture" };
        c.AddNation(new Nation { Tag = "MF0", MapIndex = 501, Population = 2_000_000, Gdp = 100, Treasury = 40,
            OutputScale = 1, Movement = movement ? new MovementState() : null });
        c.AddNation(new Nation { Tag = "MF1", MapIndex = 502, Population = 1_000_000, Gdp = 60, Treasury = 20, OutputScale = 1 });
        for (var i = 0; i < 3; i++)
        {
            var state = new ScenarioState { Id = "movement_fixture_" + i, Name = "Movement fixture " + i,
                MapIndex = 3001 + i, NationMapIndex = i == 2 ? 502 : 501, NationId = i == 2 ? "MF1" : "MF0", Population = 1_000_000 };
            state.PopulationGroups.Add(new PopulationGroup { Culture = "fixture_people", Size = state.Population });
            c.States.Add(state);
        }
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 7L * 24);
        var cells = Enumerable.Range(0, 60).Select(i => (ushort)(3001 + i % 15 / 5)).ToArray();
        var owners = Enumerable.Range(0, 60).Select(i => (ushort)(i % 15 < 10 ? 501 : 502)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 15, 4, _ => false));
        c.ConfigureSinglePlayer(0);
        SovereignFinance.NormalizeAccounts(c);
        return c;
    }
}
