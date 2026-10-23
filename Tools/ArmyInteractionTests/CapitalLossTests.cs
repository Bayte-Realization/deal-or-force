using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class CapitalLossTests
{
    static readonly int[] Home = { 1, 2, 5, 6 };
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL capital loss: " + label);
        Console.WriteLine("PASS: capital loss " + label);
    }

    static Campaign Fixture()
    {
        var c = new Campaign(20261021) { ScenarioId = "capital_loss_fixture" };
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 2400L);
        c.Nations.Add(new Nation { Id = 0, MapIndex = 600, Tag = "CL0", Name = "Incumbent",
            Population = 1_000_000, Gdp = 100, Treasury = 20, Stability = .7f, OutputScale = 1 });
        c.Nations.Add(new Nation { Id = 1, MapIndex = 601, Tag = "CL1", Name = "Opponent",
            Population = 1_000_000, Gdp = 100, Treasury = 20, Stability = .7f, OutputScale = 1 });
        const int width = 32, height = 16;
        var regions = new ushort[width * height];
        var owners = new ushort[regions.Length];
        var rgb = new byte[regions.Length * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var province = y / 8 * 4 + x / 8 + 1;
                var index = y * width + x;
                regions[index] = (ushort)(30000 + province);
                owners[index] = (ushort)(Home.Contains(province) ? 600 : 601);
                rgb[index * 3 + 2] = (byte)province;
            }
        var map = ProvinceTopology.FromColorRaster(regions, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 8).ToDictionary(p => p, p => p));
        for (var p = 1; p <= 8; p++)
            c.States.Add(new ScenarioState { Id = "cl" + p, Name = "Region " + p,
                MapIndex = 30000 + p, NationMapIndex = map.ProvinceToNation[p],
                NationId = Home.Contains(p) ? "CL0" : "CL1", Population = p is 2 or 5 ? 10000 : 5000 });
        c.AttachProvinceTopology(map);
        for (var n = 0; n < 2; n++)
        {
            var nation = c.Nations[n];
            var capital = n == 0 ? 1 : 3;
            nation.CapitalProvinceId = capital;
            nation.CapitalStateMapIndex = 30000 + capital;
            nation.CapitalName = n == 0 ? "Old city" : "Enemy city";
            (nation.CapitalLatitude, nation.CapitalLongitude) = map.CenterOf(capital);
        }
        c.Armies.Clear();
        c.Armies.Add(new Army { Id = 11, NationId = 0, ProvinceId = 2, StateMapIndex = 30002,
            Personnel = 10000, MaxPersonnel = 15000, Divisions = 1, Strength = .7f,
            Organisation = .8f, Equipment = .6f, Cohesion = .7f, AssignedWarId = 771 });
        c.Wars.Add(new StrategicWar { Id = 771, AttackerId = 1, DefenderId = 0 });
        c.ConfigureSinglePlayer(0);
        SovereignFinance.NormalizeAccounts(c);
        EconomySystem.Explain(c);
        return c;
    }

    static void Occupy(Campaign c, params int[] provinces)
    {
        foreach (var province in provinces) c.SetProvinceController(province, 601);
    }

    static void Cede(Campaign c, params int[] provinces)
    {
        // Deliberately leave region metadata stale: the province owner is authoritative.
        foreach (var province in provinces)
        {
            c.ProvinceMap.ProvinceToNation[province] = 601;
            c.SetProvinceController(province, 601);
        }
        c.MarkMapChanged();
    }

    static string Seats(Campaign c) => JsonSerializer.Serialize(new
    { c.Nations, c.CapitalRevision, c.Random.State }, Json);

    static void OccupationAndReturn()
    {
        var c = Fixture(); var n = c.Nations[0]; var war = c.Wars[0];
        var cash = n.Treasury; var stability = n.StabilityGovernance; var support = n.WarSupport;
        var paidDay = n.CapitalMovedDay; var random = c.Random.State;
        Occupy(c, 1);
        WarScoring.Update(c, war); var capitalScore = war.CapitalScore;
        Check(capitalScore > 0, "capturing the original capital produces war score");
        CapitalCore.Tick(c);
        Check(n.CapitalProvinceId == 1 && n.CapitalStateMapIndex == 30001 && n.CapitalName == "Old city" &&
            n.GovernmentSeatProvinceId == 2 && CapitalCore.EffectiveProvinceId(c, n) == 2,
            "occupation evacuates government without moving the legal capital; ties use lowest region id");
        Check(CapitalCore.HasSeat(c, n) && CapitalCore.EffectiveName(c, n) == c.States[1].DisplayName &&
            CapitalCore.EffectiveLatitude(c, n) == c.ProvinceMap.CenterOf(2).Latitude &&
            CapitalCore.EffectiveLongitude(c, n) == c.ProvinceMap.CenterOf(2).Longitude,
            "effective name and coordinates point inside the controlled recipient province");
        WarScoring.Update(c, war);
        Check(war.CapitalScore == capitalScore && n.Treasury == cash &&
            n.CapitalMovedDay == paidDay && c.Random.State == random,
            "evacuation preserves capital war score, money, paid cooldown and RNG");
        var crisisGovernance = MathF.Max(-StabilityCore.Weights.GovernanceLimit, stability - .12f);
        var crisisSupport = MathF.Max(.05f, support - .15f);
        Check(n.StabilityGovernance == crisisGovernance && n.WarSupport == crisisSupport,
            "the first evacuation applies exactly the current clamped governance and war-support crisis");
        var before = Seats(c); var fingerprint = CampaignFingerprint.Of(c);
        for (var i = 0; i < 5; i++)
        {
            _ = CapitalCore.HasSeat(c, n); _ = CapitalCore.EffectiveName(c, n);
            _ = CapitalCore.EffectiveLatitude(c, n); _ = CapitalCore.EffectiveLongitude(c, n);
            _ = CapitalCore.EffectiveProvinceId(c, n);
        }
        Check(Seats(c) == before && CampaignFingerprint.Of(c) == fingerprint,
            "reading effective seat does not mutate saved fields, revision, RNG or fingerprint");
        c.States[4].Population = 50000;
        var revision = c.CapitalRevision;
        CapitalCore.Tick(c); CapitalCore.Tick(c);
        Check(n.GovernmentSeatProvinceId == 2 && c.CapitalRevision == revision &&
            n.StabilityGovernance == crisisGovernance && n.WarSupport == crisisSupport &&
            n.Treasury == cash && n.CapitalMovedDay == paidDay && c.Random.State == random,
            "a usable emergency seat neither churns nor reapplies the evacuation crisis on repeated ticks");
        Occupy(c, 2); CapitalCore.Tick(c);
        Check(n.GovernmentSeatProvinceId == 5 && CapitalCore.EffectiveProvinceId(c, n) == 5 &&
            n.StabilityGovernance == crisisGovernance && n.WarSupport == crisisSupport,
            "losing the emergency seat chooses the next usable region without applying the first-evacuation crisis again");
        c.SetProvinceController(1, 600); CapitalCore.Tick(c);
        Check(n.GovernmentSeatProvinceId == 0 && CapitalCore.EffectiveProvinceId(c, n) == 1 &&
            CapitalCore.EffectiveName(c, n) == CountryNames.Capital(n),
            "liberation returns the government to its legal city");
        var recoveredGovernance = MathF.Min(StabilityCore.Weights.GovernanceLimit, crisisGovernance + .10f);
        var recoveredSupport = MathF.Min(1f, crisisSupport + .12f);
        Check(n.StabilityGovernance == recoveredGovernance && n.WarSupport == recoveredSupport &&
            n.Treasury == cash && n.CapitalMovedDay == paidDay && c.Random.State == random,
            "liberation grants exactly the clamped recovery once without refunding cash or bypassing the paid cooldown");
        revision = c.CapitalRevision;
        for (var tick = 0; tick < 5; tick++) CapitalCore.Tick(c);
        WarScoring.Update(c, war);
        Check(c.CapitalRevision == revision && c.Random.State == random &&
            n.StabilityGovernance == recoveredGovernance && n.WarSupport == recoveredSupport &&
            n.Treasury == cash && n.CapitalMovedDay == paidDay && war.CapitalScore == 0,
            "a settled return cannot farm recovery and the liberated legal capital no longer contributes enemy war score");

        c = Fixture(); n = c.Nations[0];
        Occupy(c, 1, 5);
        Check(Factions.HoldsTheState(c, c.Nations[1], n), "civil-war capital plus majority fixture qualifies before evacuation");
        CapitalCore.Tick(c);
        Check(n.GovernmentSeatProvinceId == 2 && Factions.HoldsTheState(c, c.Nations[1], n),
            "evacuation does not erase the incumbent-capital condition for civil-war victory");
    }

    static void CrisisClamps()
    {
        var c = Fixture(); var n = c.Nations[0];
        n.WarSupport = .06f;
        n.StabilityGovernance = -StabilityCore.Weights.GovernanceLimit + .01f;
        Occupy(c, 1); CapitalCore.Tick(c);
        Check(n.WarSupport == .05f && n.StabilityGovernance == -StabilityCore.Weights.GovernanceLimit,
            "evacuation crisis respects the support floor and governance limit");
        var crisis = Seats(c); CapitalCore.Tick(c); CapitalCore.Tick(c);
        Check(Seats(c) == crisis, "the clamped crisis is not a recurring daily penalty");

        n.WarSupport = .99f;
        n.StabilityGovernance = StabilityCore.Weights.GovernanceLimit - .01f;
        c.SetProvinceController(1, 600); CapitalCore.Tick(c);
        Check(n.WarSupport == 1f && n.StabilityGovernance == StabilityCore.Weights.GovernanceLimit,
            "liberation recovery respects the support ceiling and governance limit");
        var recovery = Seats(c); CapitalCore.Tick(c); CapitalCore.Tick(c);
        Check(Seats(c) == recovery, "the clamped recovery is not a recurring daily bonus");
    }

    static void OwnershipAndSurvival()
    {
        var c = Fixture(); var n = c.Nations[0];
        Cede(c, 1); Occupy(c, 2);
        CapitalCore.Tick(c);
        Check(n.CapitalProvinceId == 5 && n.CapitalStateMapIndex == 30005 && n.CapitalName == "Region 5" &&
            n.GovernmentSeatProvinceId == 0 && CapitalCore.HasSeat(c, n),
            "permanent cession replaces the legal capital with owned controlled ground despite stale region owner");

        c = Fixture(); n = c.Nations[0];
        Cede(c, 1, 2, 5);
        foreach (var state in c.States) state.NationMapIndex = 601;
        c.InvalidateStateIndex();
        CapitalCore.Tick(c);
        Check(c.StatesOf(n).Length == 0 && n.CapitalProvinceId == 6 && CapitalCore.HasSeat(c, n) &&
            c.Armies.Any(a => a.NationId == n.Id),
            "an owned province fragment survives even when no region metadata row belongs to the country");

        c = Fixture(); n = c.Nations[0];
        Occupy(c, Home); var army = c.Armies[0];
        var revision = c.CapitalRevision;
        CapitalCore.Tick(c);
        Check(n.CapitalProvinceId == 1 && n.GovernmentSeatProvinceId == -1 && !CapitalCore.HasSeat(c, n) &&
            c.Armies.Contains(army) && !c.Wars[0].Ended,
            "total occupation hides the government seat without destroying armies or ending the war");
        Check(c.CapitalRevision == revision + 1,
            "losing the last usable seat invalidates visible markers even when there was no temporary seat");
        revision = c.CapitalRevision; CapitalCore.Tick(c); CapitalCore.Tick(c);
        Check(c.CapitalRevision == revision && n.GovernmentSeatProvinceId == -1,
            "repeated fully occupied days do not churn the missing-seat revision");
        c.SetProvinceController(6, 600); CapitalCore.Tick(c);
        Check(n.GovernmentSeatProvinceId == 6 && CapitalCore.HasSeat(c, n) && c.CapitalRevision == revision + 1,
            "liberating one owned province restores a government seat and invalidates markers once");
        c.SetProvinceController(1, 600); revision = c.CapitalRevision; CapitalCore.Tick(c);
        Check(n.GovernmentSeatProvinceId == 0 && CapitalCore.EffectiveProvinceId(c, n) == 1 && c.CapitalRevision == revision + 1,
            "liberating the legal city clears the no-seat history and restores the legal marker");

        c = Fixture(); n = c.Nations[0];
        Cede(c, Home); CapitalCore.Tick(c);
        Check(n.CapitalProvinceId == 0 && n.CapitalStateMapIndex == 0 && n.GovernmentSeatProvinceId == 0 &&
            !CapitalCore.HasCapital(c, n) && !CapitalCore.HasSeat(c, n) && c.Armies.All(a => a.NationId != n.Id),
            "actual loss of all owned land still removes the capital and surrenders remaining formations");

        c = Fixture(); n = c.Nations[0]; Occupy(c, 1);
        c.DestroyedProvinces.Add(2); c.States[4].Kind = "Lake";
        CapitalCore.Tick(c);
        Check(n.GovernmentSeatProvinceId == 6,
            "destroyed ground and water regions cannot host the evacuated government");
    }

    static void CapitalRegionFallback()
    {
        foreach (var permanent in new[] { false, true })
        {
            var c = Fixture(); var n = c.Nations[0];
            c.ProvinceMap.ProvinceToState[2] = 30001;
            c.States[0].Population = 1_000_000;
            if (permanent) Cede(c, 1); else Occupy(c, 1);
            CapitalCore.Tick(c);
            Check(CapitalCore.EffectiveProvinceId(c, n) == 2 && n.CapitalStateMapIndex == 30001 &&
                c.ProvinceMap.ProvinceToNation[2] == n.MapIndex && c.ProvinceController[2] == n.MapIndex &&
                (permanent ? n.CapitalProvinceId == 2 && n.GovernmentSeatProvinceId == 0 && n.CapitalName == "Region 1" :
                    n.CapitalProvinceId == 1 && n.GovernmentSeatProvinceId == 2 && n.CapitalName == "Old city"),
                "the larger controlled capital-region remnant wins the weighted selection, with legal repair only after cession; permanent=" + permanent);

            c = Fixture(); n = c.Nations[0];
            c.ProvinceMap.ProvinceToState[2] = 30001;
            c.States[0].Population = 1_000;
            if (permanent) Cede(c, 1); else Occupy(c, 1);
            CapitalCore.Tick(c);
            Check(CapitalCore.EffectiveProvinceId(c, n) == 5 &&
                c.ProvinceMap.ProvinceToNation[5] == n.MapIndex && c.ProvinceController[5] == n.MapIndex &&
                (permanent ? n.CapitalProvinceId == 5 && n.CapitalStateMapIndex == 30005 && n.GovernmentSeatProvinceId == 0 :
                    n.CapitalProvinceId == 1 && n.CapitalStateMapIndex == 30001 && n.GovernmentSeatProvinceId == 5),
                "a more populous safe region outweighs a small capital-region remnant without moving an occupied legal capital; permanent=" + permanent);

            c = Fixture(); n = c.Nations[0];
            foreach (var province in Home) c.ProvinceMap.ProvinceToState[province] = 30001;
            if (permanent) Cede(c, 1); else Occupy(c, 1);
            CapitalCore.Tick(c);
            var effective = CapitalCore.EffectiveProvinceId(c, n);
            Check(effective > 0 && effective != 1 && c.ProvinceMap.ProvinceToState[effective] == 30001 &&
                c.ProvinceMap.ProvinceToNation[effective] == n.MapIndex && c.ProvinceController[effective] == n.MapIndex &&
                c.Armies.Any(a => a.NationId == n.Id),
                "surviving controlled ground in the capital region remains a valid last-region fallback; permanent=" + permanent);
        }
    }

    static void PaidChoice()
    {
        var c = Fixture(); var n = c.Nations[0]; Occupy(c, 1);
        CapitalCore.Tick(c); var cost = CapitalCore.Cost(c, n); var cash = n.Treasury;
        var command = new MoveCapitalCommand { IssuerId = n.Id, StateMapIndex = 30002 };
        command.Apply(c);
        Check(command.Moved && n.CapitalProvinceId == 2 && n.GovernmentSeatProvinceId == 0 &&
            Math.Abs(n.Treasury - (cash - cost)) < .0001f && n.CapitalMovedDay == c.ElapsedDays,
            "making the temporary seat a permanent legal capital still uses the paid command and cooldown");
        var snapshot = Seats(c); cash = n.Treasury;
        var second = new MoveCapitalCommand { IssuerId = n.Id, StateMapIndex = 30005 };
        second.Apply(c);
        Check(!second.Moved && Seats(c) == snapshot && n.Treasury == cash,
            "evacuation cannot bypass the ordinary permanent relocation cooldown");

        c = Fixture(); n = c.Nations[0]; Occupy(c, 2);
        var before = Seats(c);
        command = new MoveCapitalCommand { IssuerId = n.Id, StateMapIndex = 30002 };
        command.Apply(c);
        Check(!command.Moved && Seats(c) == before, "the paid move cannot choose an enemy-controlled destination");
    }

    static Campaign Restore(Campaign original)
    {
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(original, "capital-loss"), "capital-loss");
        Check(loaded.Success, "actual network snapshot imports: " + loaded.Warning);
        loaded.Campaign.AttachProvinceTopology(original.ProvinceMap.CloneForOverrides());
        return loaded.Campaign;
    }

    static void Persistence()
    {
        var c = Fixture(); Occupy(c, 1); CapitalCore.Tick(c);
        var fingerprint = CampaignFingerprint.Of(c); var restored = Restore(c);
        Check(restored.Nations[0].GovernmentSeatProvinceId == 2 && restored.Nations[0].CapitalProvinceId == 1 &&
            CapitalCore.EffectiveProvinceId(restored, restored.Nations[0]) == 2 && CampaignFingerprint.Of(restored) == fingerprint,
            "a current saved evacuation preserves legal city, temporary seat and fingerprint on attach");
        c = Fixture(); Occupy(c, Home); CapitalCore.Tick(c);
        fingerprint = CampaignFingerprint.Of(c); restored = Restore(c);
        Check(restored.Nations[0].GovernmentSeatProvinceId == -1 && !CapitalCore.HasSeat(restored, restored.Nations[0]) &&
            CampaignFingerprint.Of(restored) == fingerprint,
            "the saved no-seat sentinel survives network restore without reviving the occupied legal marker");
        Check(JsonSerializer.Deserialize<Nation>("{\"Id\":0,\"CapitalProvinceId\":1}", Json).GovernmentSeatProvinceId == 0,
            "legacy rows without government-seat state default to the legal capital");

        // A valid snapshot can be taken after a controller changes but before the daily
        // capital pass. Attaching topology must not invent an unrecorded evacuation.
        c = Fixture(); Occupy(c, 1); fingerprint = CampaignFingerprint.Of(c); restored = Restore(c);
        Check(restored.Nations[0].GovernmentSeatProvinceId == 0 && restored.Nations[0].CapitalProvinceId == 1 &&
            CampaignFingerprint.Of(restored) == fingerprint,
            "a valid mid-day snapshot before evacuation retains its exact authoritative fingerprint");

        c = Fixture(); Cede(c, 1); restored = Restore(c);
        Check(restored.Nations[0].CapitalProvinceId == 1 && restored.Nations[0].CapitalName == "Old city" &&
            !CapitalCore.HasSeat(restored, restored.Nations[0]),
            "attaching a ceded-capital snapshot never rebinds the old city name to another owned province");
        CapitalCore.Tick(restored);
        Check(restored.Nations[0].CapitalProvinceId == 2 && restored.Nations[0].CapitalName == "Region 2" &&
            CapitalCore.HasSeat(restored, restored.Nations[0]),
            "the explicit repair pass moves a saved ceded capital to a real usable region");

        var folder = Path.Combine(Path.GetTempPath(), "dof-capital-loss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            c = Fixture(); Occupy(c, 1); CapitalCore.Tick(c); c.Defines.SaveTextFormat = true;
            SaveSystem.Save(folder, "evacuated", c, "capital-loss");
            var saved = SaveSystem.Load(folder, "evacuated", "capital-loss");
            Check(saved.Success, "actual disk save of relocated government loads: " + saved.Warning);
            saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
            Check(saved.Campaign.Nations[0].GovernmentSeatProvinceId == 2 &&
                CapitalCore.EffectiveProvinceId(saved.Campaign, saved.Campaign.Nations[0]) == 2 &&
                CampaignFingerprint.Of(saved.Campaign) == CampaignFingerprint.Of(c),
                "temporary government seat and current authoritative state survive actual disk round trip");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    public static void Run()
    {
        OccupationAndReturn(); CrisisClamps(); OwnershipAndSurvival(); CapitalRegionFallback(); PaidChoice(); Persistence();
    }
}
