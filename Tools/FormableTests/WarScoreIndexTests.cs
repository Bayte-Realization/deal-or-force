using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

static class WarScoreIndexTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("war score index: " + name);
        Console.WriteLine("PASS war score index: " + name);
    }

    public static void Run()
    {
        var campaign = new Campaign(8);
        var attacker = new Nation { Id = 0, MapIndex = 1, Tag = "ATK", Name = "Attacker", Gdp = 10f, CapitalStateMapIndex = 10 };
        var defender = new Nation { Id = 1, MapIndex = 2, Tag = "DEF", Name = "Defender", Gdp = 10f, CapitalStateMapIndex = 20 };
        campaign.Nations.Add(attacker);
        campaign.Nations.Add(defender);
        Check(attacker.Id == 0 && campaign.Nations[0] == attacker, "nation id 0 stays at index 0");
        campaign.States.Add(new ScenarioState { MapIndex = 10, NationMapIndex = 1, Population = 1000, Name = "Attacker home" });
        campaign.States.Add(new ScenarioState { MapIndex = 20, NationMapIndex = 2, Population = 1000, Name = "Defender home" });
        // Province 5 sits in the attacker's region but belongs to nobody in the war.
        // Province 6 does the same on the defender's side. The capital and the goal
        // still count them; the occupation denominator does not.
        var state = new ushort[] { 0, 10, 10, 20, 20, 10, 20 };
        var owner = new ushort[] { 0, 1, 1, 2, 2, 9, 9 };
        var map = Map(state, owner);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).SetValue(campaign, (ushort[])owner.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation)).SetValue(campaign, new float[state.Length]);
        Check(campaign.SetProvinceController(3, 1), "one defender province is occupied");

        var war = new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1, ClaimedStateMapIndex = 20,
            AttackerWarSupport = 1f, DefenderWarSupport = 1f };
        var other = new Campaign(9);
        var otherAttacker = new Nation { Id = 0, MapIndex = 1, Tag = "OAT", Gdp = 10f, CapitalStateMapIndex = 10 };
        var otherDefender = new Nation { Id = 1, MapIndex = 2, Tag = "ODE", Gdp = 10f, CapitalStateMapIndex = 20 };
        other.Nations.Add(otherAttacker);
        other.Nations.Add(otherDefender);
        other.States.Add(new ScenarioState { MapIndex = 10, NationMapIndex = 1, Population = 1000 });
        other.States.Add(new ScenarioState { MapIndex = 20, NationMapIndex = 2, Population = 1000 });
        var otherMap = Map(new ushort[] { 0, 10, 20 }, new ushort[] { 0, 1, 2 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap)).SetValue(other, otherMap);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController)).SetValue(other, (ushort[])otherMap.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation)).SetValue(other, new float[3]);
        var otherWar = new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1, ClaimedStateMapIndex = -1,
            AttackerWarSupport = 1f, DefenderWarSupport = 1f };

        var built = WarScoring.StateIndexBuilds;
        WarScoring.Update(campaign, war);
        Check(Near(war.OccupationScore, 25f) && Near(war.CapitalScore, 4f) && Near(war.ObjectiveScore, 0f)
            && Near(war.WarScore, 29f) && war.Collapsed == 0,
            "one occupied province of three in the capital is 25 occupation, 4 capitals and no goal majority");
        Check(WarScoring.StateIndexBuilds == built + 1, "the first war builds the province lists");
        WarScoring.Update(campaign, war);
        Check(WarScoring.StateIndexBuilds == built + 1 && Near(war.WarScore, 29f),
            "a second reading on the same map does not walk it again and does not move the score");
        WarScoring.Update(other, otherWar);
        Check(WarScoring.StateIndexBuilds == built + 2, "a second campaign does not reuse the first campaign's lists");
        WarScoring.Update(campaign, war);
        Check(WarScoring.StateIndexBuilds == built + 2 && Near(war.WarScore, 29f),
            "coming back to the first campaign reuses its own lists and keeps its score");

        Check(campaign.SetProvinceController(4, 1), "the rest of the defender's own ground is occupied");
        WarScoring.Update(campaign, war);
        Check(war.Collapsed == 1 && war.WarScore == campaign.Defines.WarScoreMax && Near(war.OccupationScore, 50f),
            "occupying the defender's own ground, and not the neutral pore, collapses the score to the ceiling");
        Check(WarScoring.StateIndexBuilds == built + 2,
            "a new occupation reads the live controller and does not rebuild the ownership lists");
        FSharpDifferentialTests();
    }

    static void FSharpDifferentialTests()
    {
        Check(WarScoreRulesHook.IsInstalled, "F# war score rules hook is installed");
        var defines = new Defines();
        var occupations = new[] { 0f, 0.25f, 0.59f, 0.60f, 0.89f, 0.90f, 1.0f };
        var strengths = new[] { 0f, 0.10f, 0.15f, 0.16f, 0.50f, 1.0f };
        var scores = new[] { -150f, -100f, -50f, 0f, 25f, 100f, 150f };

        var cases = 0;
        foreach (var defOcc in occupations)
        foreach (var defStr in strengths)
        foreach (var atkOcc in occupations)
        foreach (var atkStr in strengths)
        foreach (var sc in scores)
        {
            cases++;
            // C# baseline evaluation
            var expectedDefCollapsed = defOcc >= defines.WarScoreCollapseOccupation ||
                (defOcc >= defines.WarScoreCollapseOccupationBroken && defStr <= defines.WarScoreCollapseArmy);
            var expectedAtkCollapsed = atkOcc >= defines.WarScoreCollapseOccupation ||
                (atkOcc >= defines.WarScoreCollapseOccupationBroken && atkStr <= defines.WarScoreCollapseArmy);
            var expectedCollapsed = expectedDefCollapsed ? 1 : expectedAtkCollapsed ? -1 : 0;
            var low = MathF.Min(defines.WarScoreMin, defines.WarScoreMax);
            var high = MathF.Max(defines.WarScoreMin, defines.WarScoreMax);
            var expectedScore = expectedCollapsed > 0 ? high : expectedCollapsed < 0 ? low : WarScoring.ClampScore(defines, sc);

            // F# evaluation
            var ok = WarScoreRulesHook.TryEvaluate(
                sc, 0f, 0f, 0f, 0f, 0f, 0f,
                defOcc, defStr, atkOcc, atkStr,
                defines.WarScoreCollapseOccupation, defines.WarScoreCollapseOccupationBroken, defines.WarScoreCollapseArmy,
                defines.WarScoreMin, defines.WarScoreMax,
                out var collapsed, out var actualScore);

            if (!ok || collapsed != expectedCollapsed || MathF.Abs(actualScore - expectedScore) > 0.0001f)
            {
                throw new Exception($"F# WarScore mismatch at case {cases}: expected ({expectedCollapsed}, {expectedScore}), got ({collapsed}, {actualScore})");
            }
        }
        Check(cases > 10000, $"F# war score rules match C# across {cases} combinations");
    }

    static bool Near(float value, float expected) => MathF.Abs(value - expected) < 0.05f;

    static ProvinceTopology Map(ushort[] state, ushort[] owner)
    {
        var neighbors = new int[state.Length][];
        for (var i = 0; i < neighbors.Length; i++) neighbors[i] = Array.Empty<int>();
        var constructor = typeof(ProvinceTopology).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (ProvinceTopology)constructor.Invoke(new object[]
        {
            new ushort[1], state, owner, new int[state.Length], neighbors, 1, 1
        });
    }
}
