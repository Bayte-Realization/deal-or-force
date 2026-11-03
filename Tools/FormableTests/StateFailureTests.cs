using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// State failure (StateFailure.cs): a failing state comes apart in waves into several sides, a
/// stable one does not, a revolution changes the government and can leave the old regime in
/// arms, a recovered centre takes a piece back by agreement, and the new saved state stays out
/// of the fingerprint until it is used and survives a snapshot.
/// </summary>
static class StateFailureTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("state failure: " + note); Console.WriteLine("PASS state failure: " + note); }

    const int Regions = 10;

    /// <summary>
    /// One country of ten regions in a row, west to east; the capital is region 0. Regions 7-9
    /// are another people's homeland. The same fixture shape as the coup civil-war test; a
    /// nation's id is its index in the list, so the only country is id 0.
    /// </summary>
    static Campaign Fixture(float stability, int seed = 20261101)
    {
        var c = new Campaign(seed) { ScenarioId = "state_failure_fixture" };
        var parent = new Nation { Id = 0, MapIndex = 500, Tag = "TSF", Name = "Test state", Population = 50_000_000,
            Treasury = 20, Gdp = 400, OutputScale = 1f, Stability = stability, CapitalStateMapIndex = 3001,
            Ideology = "conservatism", Government = "presidential_republic" };
        parent.FoundedCultures.Add("testish");
        // The country's people, as history/countries says it for every country of 2024.
        DemographyCatalog.Declare(parent.Tag, parent.FoundedCultures);
        c.Nations.Add(parent);
        for (var i = 0; i < Regions; i++)
        {
            var other = i >= 7;
            var state = new ScenarioState { MapIndex = 3001 + i, Id = "tsf_" + i, Name = "Region " + i,
                NationMapIndex = parent.MapIndex, NationId = parent.Tag, Population = 5_000_000,
                StandardOfLiving = .3f, Literacy = .7f, Urbanization = .5f, Radicalism = .55f, Stability = .2f,
                SecessionPressure = other ? .6f : 0f };
            state.PopulationGroups.Add(new PopulationGroup { Culture = other ? "otherish" : "testish", Size = 5_000_000 });
            c.States.Add(state);
        }
        var cells = Enumerable.Range(0, 128 * 64).Select(i => (ushort)(3001 + Math.Min(Regions - 1, i % 128 / 13))).ToArray();
        var flags = Enumerable.Repeat((ushort)500, cells.Length).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, flags, 128, 64, _ => false));
        SovereignFinance.NormalizeAccounts(c);
        Day(c, 400);
        return c;
    }

    static void Day(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);

    static void Run(Campaign c, int days)
    {
        for (var i = 0; i < days; i++)
        {
            Day(c, c.ElapsedDays + 1);
            StateFailure.Tick(c);
        }
    }

    public static void Run()
    {
        StableStateHolds();
        FailingStateCascades();
        RevolutionChangesTheGovernment();
        PieceReturnsToARecoveredCentre();
        FingerprintAndSnapshot();
    }

    static void StableStateHolds()
    {
        var c = Fixture(.65f);
        var state = c.Nations[0];
        state.PaymentArrears = 5f;   // a bad year is not a collapse
        Check(StateFailure.CollapseRefusal(c, state).Length > 0 && StateFailure.CollapseChance(c, state) == 0f,
            "a state above the line cannot begin to collapse whatever its signs");
        var before = c.Nations.Count;
        Run(c, 400);
        Check(c.Nations.Count == before && state.CollapseStartDay < 0 && state.LastRevolutionDay < 0,
            "a stable state lives a year without a collapse or a revolution");
        state.Stability = .1f;
        state.PaymentArrears = 0f;
        foreach (var region in c.StatesOf(state)) region.Radicalism = .1f;
        Check(StateFailure.Signs(c, state).Count == 0 && StateFailure.CollapseChance(c, state) == 0f,
            "a weak state with no signs of failure does not collapse either: it has to be earned");
    }

    static void FailingStateCascades()
    {
        var c = Fixture(.08f);
        var state = c.Nations[0];
        state.PaymentArrears = 5f;
        var signs = StateFailure.Signs(c, state);
        Check(signs.Count >= 2 && StateFailure.CollapseChance(c, state) > 0f,
            "an unpaid army and a radicalised people below the line make a collapse possible");
        Check(StateFailure.StartCollapse(c, state), "the collapse begins");
        var afterFirst = c.Nations.Count(n => n.RevoltParentId == state.Id);
        Check(afterFirst >= 2, $"the first wave makes several sides at once ({afterFirst})");
        Run(c, 400);
        var sides = c.Nations.Where(n => n.RevoltParentId == state.Id).ToList();
        Check(sides.Count >= 3, $"later waves add more over the months ({sides.Count} sides)");
        Check(sides.Any(n => n.FoundedCultures.Contains("otherish", StringComparer.OrdinalIgnoreCase)),
            "the other people's homeland leaves as its own state");
        Check(sides.Any(n => n.Name.StartsWith("rising:", StringComparison.Ordinal)),
            "the worst-governed provinces go over to armed sides");
        Check(sides.Count(n => c.Wars.Any(w => !w.Ended && w.Opposes(n.Id, state.Id))) >= 2,
            "the centre is at war with more than one of them at once");
        Check(c.StatesOf(state).Any(s => s.MapIndex == state.CapitalStateMapIndex) &&
              state.Population >= 50_000_000L * c.Defines.CollapseRumpShare - 1,
            $"the centre keeps its capital and its heartland ({state.Population:N0} people left)");
        Check(state.CollapseStartDay < 0 && state.LastCollapseDay >= 0 && StateFailure.CollapseRefusal(c, state).Length > 0,
            "the collapse ends, and the state is spared another for years");
        var names = sides.Where(n => n.Name.StartsWith("rising:", StringComparison.Ordinal)).Select(n => n.Name).ToList();
        Check(names.Distinct().Count() == names.Count, "each armed side is named for its own region");
    }

    static void RevolutionChangesTheGovernment()
    {
        var c = Fixture(.2f);
        var state = c.Nations[0];
        Check(StateFailure.RevolutionRefusal(c, state).Length > 0, "a loyal army keeps a government in place");
        state.DomesticCrisis.CoupPressure = .7f;
        foreach (var region in c.StatesOf(state)) region.Radicalism = region.MapIndex == state.CapitalStateMapIndex ? .8f : .3f;
        // Somewhere the old order is still liked: the far end of the country.
        foreach (var region in c.StatesOf(state).Where(r => r.MapIndex >= 3008)) region.Radicalism = .05f;
        var refusal = StateFailure.RevolutionRefusal(c, state);
        Check(refusal.Length == 0, "a radical capital, a full street and a disloyal army make a revolution possible (" + refusal + ")");
        var oldIdeology = state.Ideology;
        var loyalists = StateFailure.Revolution(c, state, forceCivilWar: true);
        Check(state.Ideology != oldIdeology && state.Government == "provisional_government" && state.LastRevolutionDay == c.ElapsedDays,
            $"the revolution changes the government ({oldIdeology} -> {state.Ideology})");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "revolution" && e.ActorId == state.Id), "and the world hears of it");
        Check(loyalists is not null && loyalists.Ideology == oldIdeology &&
              c.Wars.Any(w => !w.Ended && w.CivilConflict && w.Opposes(loyalists.Id, state.Id)),
            "the old regime's loyal regions fight on for the old creed");
        Check(StateFailure.RevolutionRefusal(c, state).Length > 0, "and no second revolution follows at once");
    }

    static void PieceReturnsToARecoveredCentre()
    {
        var c = Fixture(.08f);
        var state = c.Nations[0];
        state.PaymentArrears = 5f;
        // Nobody of another people is pressing to leave here: every side is the state's own people.
        foreach (var region in c.StatesOf(state).Where(r => r.SecessionPressure > 0f))
        { region.SecessionPressure = 0f; region.Radicalism = .2f; }
        StateFailure.StartCollapse(c, state);
        var piece = c.Nations.FirstOrDefault(n => n.RevoltParentId == state.Id && n.Name.StartsWith("rising:", StringComparison.Ordinal));
        Check(piece is not null, "the collapse leaves a piece of the same people");
        foreach (var war in c.Wars) { war.Ended = true; war.EndedDay = c.ElapsedDays; }
        foreach (var relation in c.Nations.SelectMany(a => c.Nations.Where(b => b.Id != a.Id)
                     .Select(b => CampaignDiplomacy.Get(c, a.Id, b.Id))).Where(r => r is not null))
        { relation.Stance = DiplomaticStance.Neutral; relation.Opinion = 0f; }
        Check(StateFailure.ReunionRefusal(c, piece, state).Length > 0, "a centre still failing takes nothing back");
        state.Stability = .6f;
        piece.Stability = .2f;
        Day(c, c.ElapsedDays + c.Defines.ReunionEarliestDays + 1);
        var refusal = StateFailure.ReunionRefusal(c, piece, state);
        Check(refusal.Length == 0, "a recovered centre and a failing piece of its own people may reunite (" + refusal + ")");
        var people = state.Population;
        Check(StateFailure.Reunite(c, state, piece) && c.Fallen.Contains(piece.Id) && c.StatesOf(piece).Length == 0,
            "the piece returns with its ground");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "collapse_reunion" && e.ActorId == state.Id), "and the reunion is news");
    }

    static void FingerprintAndSnapshot()
    {
        var c = Fixture(.5f);
        var state = c.Nations[0];
        var before = CampaignFingerprint.Of(c);
        state.CollapseStartDay = -1; state.CollapseWaves = 0; state.CollapseStartPopulation = 0;
        state.LastCollapseDay = -99999; state.LastRevolutionDay = -99999;
        Check(CampaignFingerprint.Of(c) == before, "a country that never failed adds nothing to the fingerprint");
        state.LastRevolutionDay = 123;
        var changed = CampaignFingerprint.Of(c);
        Check(changed != before, "a revolution's date is part of the fingerprint");
        state.LastRevolutionDay = -99999;
        Check(CampaignFingerprint.Of(c) == before, "and restoring the default restores the fingerprint");

        var failing = Fixture(.08f);
        var parent = failing.Nations[0];
        parent.PaymentArrears = 5f;
        StateFailure.StartCollapse(failing, parent);
        // A snapshot is of somebody's world: seat a player in one of the new sides.
        failing.ConfigureSinglePlayer(failing.Nations.Last().Id);
        var print = CampaignFingerprint.Of(failing);
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(failing, "state-failure"), "state-failure");
        var again = restored.Campaign?.NationById(parent.Id);
        Check(again is not null && again.CollapseStartDay == parent.CollapseStartDay && again.CollapseWaves == parent.CollapseWaves &&
              again.CollapseStartPopulation == parent.CollapseStartPopulation && again.LastCollapseDay == parent.LastCollapseDay,
            "a collapse under way survives a snapshot " + (restored.Campaign is null ? restored.Warning : ""));
        Check(CampaignFingerprint.Of(restored.Campaign) == print, "and the restored world has the same fingerprint");
    }
}
