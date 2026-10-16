using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

static class ConstructionFundingTests
{
    private static int failures;
    private static void Check(bool ok, string message)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "construction funding: " + message);
        if (!ok) failures++;
    }

    private static (Campaign C, Nation N, ConstructionProject Project) Fixture(float capacity, float cash, float cost = 4)
    {
        var c = new Campaign(20261015) { ScenarioId = "construction_funding_fixture" };
        var n = new Nation { Id = 0, Tag = "CFD", MapIndex = 501, Population = 1000000,
            Treasury = cash, ConstructionCapacity = capacity };
        c.Nations.Add(n);
        c.States.Add(new ScenarioState { Id = "funded_site", MapIndex = 3001, NationMapIndex = 501,
            NationId = "CFD", Population = 1000000 });
        var project = new ConstructionProject { Id = 1, NationId = n.Id, StateMapIndex = 3001,
            Building = BuildingKind.CivilianIndustry, Cost = cost, DurationDays = 20 };
        c.Construction.Add(project);
        return (c, n, project);
    }

    public static void Run()
    {
        failures = 0;
        var (c, n, project) = Fixture(2, 10);
        for (var day = 0; day < 10; day++) ConstructionSystem.Tick(c);
        Check(c.Construction.Count == 1 && project.ProgressDays == 10 && MathF.Abs(project.Paid - 4) < .00001f,
            "high capacity funds the agreed price before the construction time is complete");
        var cash = n.Treasury;
        for (var day = 0; day < 10; day++) ConstructionSystem.Tick(c);
        Check(c.Construction.Count == 0 && project.ProgressDays == 20,
            "funding early does not skip the remaining construction time");
        Check(MathF.Abs(n.Treasury - 6) < .00001f && MathF.Abs(n.Treasury - cash) < .00001f && project.Paid <= 4,
            "remaining work never charges the full project price a second time");
        Check(c.States[0].Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry) == 1,
            "one fully paid completed site delivers exactly one real building");

        (c, n, project) = Fixture(2, 0); project.Paid = project.Cost; project.ProgressDays = 4;
        ConstructionSystem.Tick(c);
        Check(project.ProgressDays == 5 && n.Treasury == 0 && project.Paid == project.Cost,
            "a fully funded site continues working when the national cash account later reaches zero");
        for (var day = 5; day < 20; day++) ConstructionSystem.Tick(c);
        Check(c.Construction.Count == 0, "a prefunded cashless site can actually finish instead of blocking the queue forever");

        (c, n, project) = Fixture(.25f, 10);
        for (var day = 0; day < 79; day++) ConstructionSystem.Tick(c);
        Check(c.Construction.Count == 1, "low capacity still cannot complete before its bill is funded");
        ConstructionSystem.Tick(c);
        Check(c.Construction.Count == 0 && MathF.Abs(n.Treasury - 6) < .0001f && project.Paid <= 4,
            "low capacity finishes after the same total contract price, not a discounted building");

        (c, n, project) = Fixture(1, 0);
        ConstructionSystem.Tick(c);
        Check(project.ProgressDays == 0 && project.Paid == 0 && c.Construction.Count == 1,
            "an unpaid cashless site remains suspended");
        n.Treasury = .1f; ConstructionSystem.Tick(c);
        Check(project.ProgressDays == 1 && MathF.Abs(project.Paid - .1f) < .000001f && n.Treasury == 0,
            "partial daily funding keeps its existing real-money work semantics");

        (c, n, project) = Fixture(1, 0, 0);
        for (var day = 0; day < 20; day++) ConstructionSystem.Tick(c);
        Check(c.Construction.Count == 0 && n.Treasury == 0,
            "a modded zero-price project needs its work time but not a fabricated cash charge");

        (c, n, project) = Fixture(1, 0); project.Paid = 5;
        ConstructionSystem.Tick(c);
        Check(n.Treasury == 0 && project.Paid == 5 && project.ProgressDays == 1,
            "an old overpaid save resumes work without minting a retrospective refund");
        (c, n, project) = Fixture(1, 10, -4);
        ConstructionSystem.Tick(c);
        Check(n.Treasury == 10 && project.Paid == 0 && project.ProgressDays == 0,
            "an invalid negative-price saved project cannot mint a construction refund");
        (c, n, project) = Fixture(1, 10); project.Paid = float.NaN;
        ConstructionSystem.Tick(c);
        Check(n.Treasury == 10 && project.ProgressDays == 0,
            "non-finite project accounting does not poison the national cash account");
        if (failures > 0) throw new Exception($"construction funding: {failures} failed checks");
    }
}

