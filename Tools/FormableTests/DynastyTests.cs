using System;
using DealOrForce.Simulation;

/// <summary>
/// Dynasties are data and a claim, not rulers: what a test can hold in its hands is the
/// catalogue, the casus belli it feeds, and the dormancy of the union kind - the one
/// property that keeps an eighteenth-century mechanic from rewriting the twenty-first.
/// No campaign world is needed for any of it but the claim itself, and the claim needs
/// only two crowns and a stability number.
/// </summary>
static class DynastyTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("dynasty: " + label); Console.WriteLine("PASS dynasty: " + label); }

    public static void Run()
    {
        Check(DynastyCatalog.Shares("GBR", "CAN"), "two crowns of one house know each other");
        Check(DynastyCatalog.Shares("NOR", "DNK"), "the northern cousins share a house");
        Check(!DynastyCatalog.Shares("SAU", "JOR"), "rival houses are not cousins");
        Check(DynastyCatalog.DynastyOf("ESP").Length > 0, "the catalogue answers for a crown it knows");
        Check(DynastyCatalog.DynastyOf("FRA") == "", "a republic wears no house");

        var c = new Campaign(824, new Defines());
        var gbr = new Nation { Tag = "GBR", MapIndex = 501, Government = "constitutional_monarchy",
            Stability = 0.8f, Population = 60_000_000, Gdp = 1000, Treasury = 50, OutputScale = 1 };
        var can = new Nation { Tag = "CAN", MapIndex = 502, Government = "constitutional_monarchy",
            Stability = 0.2f, Population = 38_000_000, Gdp = 800, Treasury = 40, OutputScale = 1 };
        c.AddNation(gbr);
        c.AddNation(can);

        Check(Statecraft.CasusBelli(c, gbr, can) == "dynastic_claim",
            "a cousin crown in crisis is a reason of the oldest kind");
        can.Stability = 0.8f;
        Check(Statecraft.CasusBelli(c, gbr, can) == "",
            "a stable cousin is not a claim");
        gbr.Government = "presidential_republic";
        can.Stability = 0.2f;
        Check(Statecraft.CasusBelli(c, gbr, can) == "",
            "a republic has no cousins to restore");

        Check(SubjectCatalog.Knows("personal_union"), "the union kind exists for whoever earns it");
        var imposable = false;
        foreach (var kind in SubjectCatalog.Imposable)
            if (kind.Id == "personal_union") imposable = true;
        Check(!imposable, "an ordinary peace never hands a country over as a union");
        Check(CasusBelliNames.Of("dynastic_claim").Length > 0,
            "the reason has a sentence a player can read");
    }
}
