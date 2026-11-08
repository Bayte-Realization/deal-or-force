using System;
using System.Linq;
using DealOrForce.Simulation;

static class RestorationCycleTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("restoration cycle: " + label); Console.WriteLine("PASS restoration cycle: " + label); }

    static (Campaign C, Nation First, Nation Next, Nation Original, int[] Home) World()
    {
        var c = new Campaign(20261030) { ScenarioId = "restoration_cycle" };
        Nation Add(string tag, int map, long people) => c.AddNation(new Nation {
            Tag = tag, Name = tag, MapIndex = map, Population = people, Gdp = 100,
            Treasury = 100, Stability = .9f, Government = "parliamentary_republic", Ideology = "liberalism" });
        var first = Add("QRA", 501, 1_000_000); first.FoundedCultures.Add("czech");
        var next = Add("QRB", 502, 1_000_000); next.FoundedCultures.Add("czech");
        var original = Add("CZE", 503, 0);
        for (var i = 0; i < 2; i++)
        {
            var region = new ScenarioState { Id = "cycle_" + i, Name = "Cycle " + i,
                MapIndex = 3001 + i, NationMapIndex = 501 + i, NationId = i == 0 ? first.Tag : next.Tag,
                Population = 1_000_000 };
            region.PopulationGroups.Add(new PopulationGroup { Culture = "czech", Religion = "catholic", Size = 1_000_000 });
            c.States.Add(region);
        }
        var states = new ushort[] { 3001, 3001, 3002, 3002, 3001, 3001, 3002, 3002 };
        var owners = new ushort[] { 501, 501, 502, 502, 501, 501, 502, 502 };
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 4, 2, _ => false));
        var home = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => c.ProvinceMap.ProvinceToState[p] == 3001).ToArray();
        c.ScenarioTerritories[original.Tag] = home;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 2000 * 24L);
        c.Defines.AiFormableEarliestDay = 0;
        c.Defines.AiFormableReviewDays = 1;
        return (c, first, next, original, home);
    }

    public static void Run()
    {
        var (c, first, next, original, home) = World();
        const string id = "restore_cze";
        var definition = FormableNations.Find(c, id);
        Check(definition is not null && FormableNations.Evaluate(c, first, definition).CanForm, "first local successor can restore the vanished identity");
        new FormNationCommand { IssuerId = first.Id, FormableId = id }.Apply(c);
        Check(first.FormedNationId == id, "first restoration uses the real command");
        Check(!FormableRestorations.Vanished(c, original) && FormableNations.Find(c, id) is null,
            "a landed successor blocks a second live copy");
        Check(!FormableNations.Evaluate(c, next, definition).Requirements.Last(r => r.Label == Loc.Get(
            "No competing state or government in exile claims this identity")).Met,
            "a previously held definition cannot bypass the live-identity gate");
        StatePartition.Annex(c, home, next, asConquest: false);
        Check(!c.HasOwnedLand(first) && first.FormedNationHistory.Contains(id), "fall retains the old proclamation's historical record");
        Check(FormableRestorations.Vanished(c, original), "a dead predecessor no longer vetoes all later restorations");
        Check(FormableRestorations.In(c).Any(d => d.Id == id), "the campaign catalogue reopens the restoration after the successor falls");
        // Leave the new local successor only the actual homeland: no large-state-to-enclave renaming.
        var outside = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Except(home).ToArray();
        var neighbour = c.AddNation(new Nation { Tag = "QRC", MapIndex = 504, Population = 1_000_000, Gdp = 100, Treasury = 100 });
        StatePartition.Annex(c, outside, neighbour, asConquest: false);
        next.Population = c.StatesOf(next).Sum(s => s.Population);
        definition = FormableNations.Find(c, id);
        Check(definition is not null && FormableNations.Evaluate(c, next, definition).CanForm,
            "the next real holder can meet the ordinary stability, territory and cost gates");
        Check(FormableProclamationAi.Evaluate(c, next, definition).CanProclaim, "the same reopening is available to the situational AI");
        var owners = c.ProvinceMap.ProvinceToNation.ToArray(); var control = c.ProvinceController.ToArray();
        var cash = next.Treasury; var cost = FormableNations.Evaluate(c, next, definition).Cost;
        FormableProclamationAi.Tick(c);
        Check(next.FormedNationId == id && Math.Abs(next.Treasury - (cash - cost)) < .0001f, "a later restoration pays its own full price");
        Check(owners.SequenceEqual(c.ProvinceMap.ProvinceToNation) && control.SequenceEqual(c.ProvinceController),
            "restoring an identity never grants or steals additional ground");
        Check(c.Chronicle.Entries.Count(e => e.Kind == "state_formed" && e.Subject == definition.Name) == 2,
            "both historical restorations remain in the chronicle");
        Check(!FormableNations.Evaluate(c, first, definition).CanForm && first.FormedNationHistory.Contains(id),
            "the old founder is not given repeat rewards or a cleared history");
        c.ConfigureSinglePlayer(neighbour.Id);
        var snapshot = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "cycle"), "cycle");
        Check(snapshot.Success && snapshot.Campaign.NationById(next.Id).FormedNationId == id &&
            snapshot.Campaign.NationById(first.Id).FormedNationHistory.Contains(id), "both identities and their separate histories survive save/load: " + snapshot.Warning);

        (c, first, next, original, home) = World();
        first.FormedNationId = id;
        StatePartition.Annex(c, home, next, asConquest: false);
        first.Movement = new MovementState { Exile = true, Landless = true, Dissolved = false };
        Check(!FormableRestorations.Vanished(c, original), "a surviving government in exile keeps its identity, even without land");
        // Prime the cache, then dissolve without moving a border or advancing a day.
        Check(!FormableRestorations.In(c).Any(d => d.Id == id), "live exile keeps the catalogue closed");
        MovementFall.Dissolve(c, first, "test dissolution");
        Check(FormableRestorations.In(c).Any(d => d.Id == id), "dissolution invalidates restoration availability immediately on pause");
        original.Movement = new MovementState { Exile = true, Landless = true, Dissolved = true };
        c.MovementRevision++;
        Check(FormableRestorations.Vanished(c, original), "a dissolved original exile is not a permanent identity lock either");
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        _ = FormableRestorations.In(c); _ = FormableNations.Find(c, id);
        Check(CampaignFingerprint.Of(c) == before && random == c.Random.State, "availability reads do not alter state or random draws");

        (c, first, next, original, home) = World();
        // A hand-written id and an automatic restore_<tag> id still name one identity.
        var errors = new System.Collections.Generic.List<string>();
        using (var json = System.Text.Json.JsonDocument.Parse("{\"cycle_authored_restoration\":{\"name\":\"Czechia restored\",\"restores\":\"cze\",\"eligible_cultures\":[\"czech\"],\"required_tags\":[\"CZE\"]}}"))
            FormableNations.Merge(json.RootElement, errors);
        Check(errors.Count == 0 && FormableNations.Find("cycle_authored_restoration")?.Restores == "CZE", "authored restoration identity is loaded and normalised");
        first.FormedNationId = "cycle_authored_restoration";
        Check(!FormableRestorations.Vanished(c, original), "authored and generated restorations share the same live identity guard");
        StatePartition.Annex(c, home, original, asConquest: false);
        Check(FormableRestorations.HasLivingIdentity(c, "CZE") && !FormableNations.Evaluate(c, next,
            FormableNations.Find("cycle_authored_restoration")).Requirements.Last(r => r.Label == Loc.Get(
                "No competing state or government in exile claims this identity")).Met,
            "a returned original government also blocks a competing authored proclamation");
        errors.Clear();
        using (var json = System.Text.Json.JsonDocument.Parse("{\"cycle_wrong_restoration\":{\"name\":\"Wrong territory\",\"restores\":\"CZE\",\"eligible_cultures\":[\"czech\"],\"required_tags\":[\"RUS\"]}}"))
            FormableNations.Merge(json.RootElement, errors);
        Check(errors.Count > 0 && FormableNations.Find("cycle_wrong_restoration") is null,
            "authored restorations cannot substitute unrelated territory for the restored homeland");
    }
}
