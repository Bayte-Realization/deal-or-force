using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class MovementPowerStateTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("movement power state: " + label); Console.WriteLine("PASS movement power state: " + label); }

    public static void Run()
    {
        using var definition = JsonDocument.Parse("""
            { "momentum_fixture": { "tag": "MPX", "name": "Momentum fixture",
              "peoples": ["fixture_supporter"], "homeland": [], "traits": [] } }
            """);
        MovementCatalog.Merge(definition.RootElement, null);
        try
        {
            SameDayRecognitionCanEnableARealStrike();
            ChangedInputsAreReadNow();
            PopulationMembershipIsReadNow();
            LostFollowingCannotStrike();
            SaveLoadHasNoHiddenMomentumState();
        }
        finally { MovementCatalog.ResetCatalog(); }
    }

    static void SameDayRecognitionCanEnableARealStrike()
    {
        var observed = StrikeAfterRecognition(readPanelFirst: true);
        var unobserved = StrikeAfterRecognition(readPanelFirst: false);
        Check(observed == unobserved,
            "identical recognition and strike commands have identical fingerprints with or without an earlier UI read");
    }

    static ulong StrikeAfterRecognition(bool readPanelFirst)
    {
        var (c, movement, host, recognizer, region) = Fixture();
        if (readPanelFirst)
        {
            var before = CampaignFingerprint.Of(c); var random = c.Random.State;
            var initial = MovementPower.Momentum(c, movement);
            Check(initial < c.Defines.MovementStrikeMomentum && initial > .38f,
                "the displayed initial following is just below the strike threshold");
            Check(before == CampaignFingerprint.Of(c) && random == c.Random.State,
                "showing momentum changes no authoritative state or random state");
        }
        new MovementActionCommand { IssuerId = movement.Id, Action = "recognition", TargetNationId = recognizer.Id }.Apply(c);
        Check(movement.Movement.RecognisedBy.Contains(recognizer.Id), "the same-day public command really obtains recognition");
        new MovementActionCommand { IssuerId = movement.Id, Action = "strike" }.Apply(c);
        Check(ModifierSystem.Has(host, "general_strike") && movement.Movement.ReadyOnDay.ContainsKey("strike"),
            (readPanelFirst ? "a displayed movement" : "an undisplayed movement") + " can act on its freshly earned recognition");
        return CampaignFingerprint.Of(c);
    }

    static void ChangedInputsAreReadNow()
    {
        foreach (var input in new[] { "support", "legitimacy", "recognition", "cadres", "income" })
        {
            var (c, movement, _, recognizer, region) = Fixture();
            var before = MovementPower.Momentum(c, movement);
            var state = movement.Movement;
            switch (input)
            {
                case "support": state.Support[region.MapIndex] += .1f; break;
                case "legitimacy": state.Legitimacy += .1f; break;
                case "recognition": state.RecognisedBy.Add(recognizer.Id); break;
                case "cadres": state.Cadres += 2000f; break;
                case "income": state.IncomeWeekly = .005f; break;
            }
            var expected = .35f * state.Support[region.MapIndex] + .25f * state.Legitimacy +
                .15f * MathF.Min(1f, state.RecognisedBy.Count / 8f) + .15f * MathF.Min(1f, state.Cadres / 20000f) +
                .10f * MathF.Min(1f, state.IncomeWeekly * 52f / .5f);
            var now = MovementPower.Momentum(c, movement);
            Check(now > before && Math.Abs(now - expected) < .00001f,
                "same-day " + input + " is read from the actual current world, not a presentation cache");
        }
    }

    static void PopulationMembershipIsReadNow()
    {
        var (c, movement, _, _, region) = Fixture();
        var initial = MovementPower.Momentum(c, movement);
        region.PopulationGroups.Single().Culture = "fixture_other_people";
        var expected = .25f * movement.Movement.Legitimacy + .15f * movement.Movement.Cadres / 20000f;
        Check(MovementPower.Momentum(c, movement) < initial && Math.Abs(MovementPower.Momentum(c, movement) - expected) < .00001f,
            "same-day population membership is counted afresh, without priming the day-only people index");
    }

    static void SaveLoadHasNoHiddenMomentumState()
    {
        var (c, movement, _, _, region) = Fixture();
        MovementPower.Momentum(c, movement);
        movement.Movement.Support[region.MapIndex] = .9f;
        var current = MovementPower.Momentum(c, movement);
        var fingerprint = CampaignFingerprint.Of(c);
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "momentum"), "momentum");
        Check(saved.Success, "current movement state travels through the real save codec: " + saved.Warning);
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        var restoredMomentum = MovementPower.Momentum(saved.Campaign, saved.Campaign.NationById(movement.Id));
        Check(Math.Abs(restoredMomentum - current) < .00001f,
            "loading produces the same momentum without missing presentation history: " + current + " -> " + restoredMomentum);
        Check(CampaignFingerprint.Of(saved.Campaign) == fingerprint,
            "the movement's authoritative fingerprint survives save/load");
    }

    static void LostFollowingCannotStrike()
    {
        var (c, movement, host, _, region) = Fixture();
        c.Defines.MovementStrikeMomentum = .35f;
        Check(MovementActions.Refusal(c, movement, "strike", -1, -1).Length == 0,
            "the real following initially permits a strike and its preview can be opened");
        // Standing abroad and organisers remain, but a historical Support entry does
        // not create residents after those peoples have ceased to live in this region.
        region.PopulationGroups.Single().Culture = "fixture_other_people";
        movement.Movement.Legitimacy = 1f; movement.Movement.Cadres = 20000f;
        movement.Movement.IncomeWeekly = .01f;
        Check(MovementPower.Momentum(c, movement) >= c.Defines.MovementStrikeMomentum &&
            MovementActions.Refusal(c, movement, "strike", -1, -1).Length > 0,
            "recognition and resources alone cannot invent a country containing a resident following");
        var before = CampaignFingerprint.Of(c); var cash = movement.Treasury; var random = c.Random.State;
        new MovementActionCommand { IssuerId = movement.Id, Action = "strike" }.Apply(c);
        Check(!ModifierSystem.Has(host, "general_strike") && movement.Treasury == cash &&
            !movement.Movement.ReadyOnDay.ContainsKey("strike") && CampaignFingerprint.Of(c) == before && c.Random.State == random,
            "the rejected public command neither strikes a stale target nor consumes cash, cooldown or random state");
    }

    static (Campaign Campaign, Nation Movement, Nation Host, Nation Recognizer, ScenarioState Region) Fixture()
    {
        var c = new Campaign(721, new Defines()) { ScenarioId = "momentum_state_fixture" };
        var host = c.AddNation(new Nation { Tag = "MHS", MapIndex = 501, Population = 1_000_000,
            Treasury = 30, Gdp = 60, Stability = .6f, CapitalStateMapIndex = 3001 });
        var recognizer = c.AddNation(new Nation { Tag = "MRC", MapIndex = 502, Treasury = 20, Population = 1_000_000 });
        var movement = c.AddNation(new Nation { Tag = "MPX", MapIndex = 503, Treasury = 1,
            Movement = new MovementState { Id = "momentum_fixture", Legitimacy = .56f, Cadres = 1000 } });
        var region = new ScenarioState { MapIndex = 3001, NationMapIndex = host.MapIndex, NationId = host.Tag,
            Id = "momentum_region", Name = "Momentum region", Population = 1_000_000, Stability = .6f };
        region.PopulationGroups.Add(new PopulationGroup { Culture = "fixture_supporter", Size = region.Population });
        c.States.Add(region); c.InvalidateStateIndex();
        movement.Movement.Support[region.MapIndex] = .7f;
        CampaignDiplomacy.Get(c, recognizer.Id, movement.Id).Opinion = 100f;
        // Use a running campaign, not a day-zero bookmark that initializes national
        // courses and deployments when its raster is attached.
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 7L * 24);
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)3001, 32).ToArray(),
            Enumerable.Repeat((ushort)501, 32).ToArray(), 8, 4, _ => false));
        c.ConfigureSinglePlayer(movement.Id);
        SovereignFinance.NormalizeAccounts(c);
        foreach (var nation in c.Nations) nation.OutputScale = 1f;
        return (c, movement, host, recognizer, region);
    }
}
