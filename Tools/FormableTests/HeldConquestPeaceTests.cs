using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// A winner who already holds a conquered strip does not write a peace that keeps the cheap
/// rump and hands the rest back. Status quo keeps that held ground. Land the winner is
/// losing and does not hold can still go home. A player's brush stays the provinces they named.
/// </summary>
static class HeldConquestPeaceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("held conquest: " + label);
        System.Console.WriteLine("PASS held conquest: " + label);
    }

    public static void Run()
    {
        var c = new Campaign(20261023);
        c.Defines.PeaceWholeRegions = true;
        c.Defines.PeaceShapeAllowCoastalBridgeheads = false;
        c.Defines.PeaceCostPerProvince = 25f;
        var winner = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "WIN", Name = "Winner", Population = 100, Gdp = 20,
            CapitalStateMapIndex = 1, IsHuman = false
        };
        var loser = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "LOS", Name = "Loser", Population = 100, Gdp = 20,
            CapitalStateMapIndex = 5
        };
        c.Nations.Add(winner);
        c.Nations.Add(loser);
        const int width = 8, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        // Home, then a cheap rump, then the expensive conquered strip, then ground not held.
        Cell(1, 1, 1, 1);
        Cell(2, 1, 2, 2);
        Cell(3, 1, 3, 2);
        Cell(4, 1, 4, 2);
        Cell(5, 1, 5, 2);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        var control = (ushort[])map.ProvinceToNation.Clone();
        var occupation = new float[map.ProvinceCount + 1];
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(c, control);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(c, occupation);
        c.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "home", Name = "Home", Population = 1_000 });
        c.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 2, Id = "rump", Name = "Rump", Population = 1_000 });
        c.States.Add(new ScenarioState { MapIndex = 3, NationMapIndex = 2, Id = "field", Name = "Field", Population = 10_000_000 });
        c.States.Add(new ScenarioState { MapIndex = 4, NationMapIndex = 2, Id = "rear", Name = "Rear", Population = 10_000_000 });
        c.States.Add(new ScenarioState { MapIndex = 5, NationMapIndex = 2, Id = "far", Name = "Far", Population = 10_000_000 });

        int At(int region)
        {
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToState[province] == region) return province;
            return 0;
        }
        var home = At(1);
        var rump = At(2);
        var field = At(3);
        var rear = At(4);
        var far = At(5);
        Check(home > 0 && rump > 0 && field > 0 && rear > 0 && far > 0, "the strip has a home, a rump, a field, a rear and a far province");
        Check(map.Neighbors[rump].Contains(home) && map.Neighbors[field].Contains(rump) && map.Neighbors[rear].Contains(field),
            "the conquered ground is one contiguous strip");

        void Hold(int province, float security)
        {
            control[province] = (ushort)winner.MapIndex;
            occupation[province] = security;
        }
        Hold(rump, 1f);
        Hold(field, 0.2f);
        Hold(rear, 0.2f);

        var war = new StrategicWar
        {
            Id = 1, AttackerId = winner.Id, DefenderId = loser.Id, WarScore = 50f,
            AttackerWarSupport = 1f, DefenderWarSupport = 1f, ClaimedStateMapIndex = -1
        };
        foreach (var province in new[] { rump, field, rear, far })
            war.OriginalController[province] = (ushort)loser.MapIndex;
        c.Wars.Add(war);

        var ledger = new PeaceLedger(c, war, winner.Id);
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        var before = CampaignFingerprint.Of(c);
        AiStatecraft.WriteDemand(c, winner, war, ledger, demand, ledger.Budget * c.Defines.AiPeaceBudgetShare);
        var refusal = PeaceConference.BlockReason(c, war, demand);
        Check(CampaignFingerprint.Of(c) == before, "pricing the offer does not change the campaign");
        bool Has(int region) => demand.Provinces.Any(p => map.ProvinceToState[p] == region);
        Check(Has(2) && Has(3) && Has(4) && !Has(5),
            $"the offer keeps the rump, the field and the rear, and not the ground it does not hold ({demand.Provinces.Count})");
        Check(refusal.Length == 0, "the offer is signable: " + refusal);
        Check(ledger.Cost(demand) <= ledger.Budget, "the held strip fits the war score");

        c.Defines.PeaceWholeRegions = false;
        var along = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        AiStatecraft.WriteDemand(c, winner, war, ledger, along, ledger.Budget * c.Defines.AiPeaceBudgetShare);
        bool Along(int region) => along.Provinces.Any(p => map.ProvinceToState[p] == region);
        Check(Along(2) && Along(3), "province peace keeps the conquered strip, not only the cheap rump");
        Check(!Along(5), "province peace does not take ground the army is not standing on");
        c.Defines.PeaceWholeRegions = true;

        winner.IsHuman = true;
        var brush = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        brush.Provinces.Add(rump);
        var brushBefore = CampaignFingerprint.Of(c);
        var painted = PeaceConference.Round(c, war, brush);
        Check(painted.Provinces.Contains(rump) && painted.Provinces.All(p => map.ProvinceToState[p] == 2),
            "a player's brush stays the rump they painted");
        Check(CampaignFingerprint.Of(c) == brushBefore, "reading the brush does not change the campaign");
        winner.IsHuman = false;

        // Losing, and not standing on the far province: it can go home. The held strip stays.
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, 400L * 24);
        war.WarScore = -20f;
        var losing = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        AiStatecraft.WriteDemand(c, winner, war, new PeaceLedger(c, war, winner.Id), losing, 40f);
        Check(!losing.Provinces.Any(p => map.ProvinceToState[p] == 5),
            "a losing offer does not take ground the winner does not hold");

        PeaceNegotiation.Resolve(c, war, winner.Id, PeaceTerms.StatusQuo);
        Check(war.Ended, "status quo is signed (" + war.LastNegotiation + ")");
        bool OwnedByWinner(int province) => map.ProvinceToNation[province] == (ushort)winner.MapIndex;
        Check(OwnedByWinner(rump) && OwnedByWinner(field) && OwnedByWinner(rear),
            "status quo keeps the held strip, including ground that was not yet secure");
        Check(map.ProvinceToNation[far] == (ushort)loser.MapIndex,
            "status quo does not award ground the winner was losing and did not hold");
    }
}
