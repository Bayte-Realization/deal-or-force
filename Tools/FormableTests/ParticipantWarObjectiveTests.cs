using System;
using System.Linq;
using DealOrForce.Simulation;

static class ParticipantWarObjectiveTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("participant objective: " + note); Console.WriteLine("PASS participant objective: " + note); }

    static Campaign Fixture()
    {
        var c = new Campaign(20261018) { ScenarioId = "separate_coalition_fronts" };
        foreach (var tag in new[] { "USA", "CUB", "PRK", "KOR" })
            c.AddNation(new Nation { Tag = tag, MapIndex = 501 + c.Nations.Count, Population = 1000000 });
        const int width = 12, height = 6;
        var regions = new ushort[width * height]; var owners = new ushort[regions.Length];
        // Two land theatres separated by sea: USA-CUB and PRK-KOR.
        var slots = new[] { (1, 1, 3001, 501), (2, 1, 3002, 502), (3, 1, 3005, 502),
                            (1, 4, 3003, 503), (2, 4, 3004, 504), (3, 4, 3006, 504) };
        foreach (var (x, y, state, owner) in slots)
        {
            regions[y * width + x] = (ushort)state; owners[y * width + x] = (ushort)owner;
            c.States.Add(new ScenarioState { Id = "theatre_" + state, MapIndex = state,
                NationMapIndex = owner, Population = 1000000 });
        }
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(regions, owners, width, height, _ => false));
        int P(int state) => Enumerable.Range(1,c.ProvinceMap.ProvinceCount)
            .Single(p => c.ProvinceMap.ProvinceToState[p] == state);
        foreach (var (nation, capital) in new[] { (0,3001), (1,3005), (2,3003), (3,3006) })
        {
            c.Nations[nation].CapitalProvinceId = P(capital);
            c.Nations[nation].CapitalStateMapIndex = capital;
        }
        var war = new StrategicWar { Id = 17, AttackerId = 0, DefenderId = 1,
            ClaimedStateMapIndex = 3002 };
        war.AttackerAllies.Add(3); war.DefenderAllies.Add(2);
        war.AttackerFront.UnionWith(new[] { P(3001), P(3004) });
        war.DefenderFront.UnionWith(new[] { P(3002), P(3003) });
        c.Wars.Add(war);
        return c;
    }

    public static void Run()
    {
        var c = Fixture(); var war = c.Wars[0]; var prk = c.Nations[2];
        var hash = CampaignFingerprint.Of(c);
        Check(FrontPlanner.ObjectiveFor(c,war,prk) == 3006,
            "a Korean participant pursues the local enemy capital, not its Cuban ally's original war goal");
        Check(FrontPlanner.ObjectiveFor(c,war,c.Nations[0]) == 3002,
            "the original claimant retains a still-hostile objective on its own front");
        Check(CampaignFingerprint.Of(c) == hash,
            "operational assessment does not rewrite borders, treaties or authoritative state");
        war.ClaimedStateMapIndex = 3004;
        Check(FrontPlanner.ObjectiveFor(c,war,prk) == 3004,
            "an unfinished declared objective in the local theatre takes priority over a new capital");
        war.ClaimedStateMapIndex = 3002; war.Limited = true;
        Check(FrontPlanner.ObjectiveFor(c,war,prk) == -1,
            "a limited war is neither expanded to another capital nor directed at an ally");
        war.Limited = false;
        var capital = c.Nations[3].CapitalProvinceId;
        c.SetProvinceController(capital,503);
        Check(FrontPlanner.ObjectiveFor(c,war,prk) == -1,
            "an already occupied capital is not repeatedly issued as an unfinished spearhead objective");
        c.SetProvinceController(capital,504);
        var ownPost = war.DefenderFront.Single(p=>c.ProvinceController[p]==503);
        war.DefenderFront.Remove(ownPost);
        Check(FrontPlanner.ObjectiveFor(c,war,prk) == -1,
            "an ally's distant front does not masquerade as this participant's local front");
    }
}
