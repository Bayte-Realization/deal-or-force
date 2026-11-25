using System;
using DealOrForce.Simulation;

/// <summary>Why money does not pile up (TreasuryDynamics): erosion, spending pressure, crises and their contagion.</summary>
static class TreasuryDynamicsTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("treasury dynamics: " + note); Console.WriteLine("PASS treasury dynamics: " + note); }

    static Campaign Fixture()
    {
        var c = new Campaign(20261030) { ScenarioId = "treasury_fixture" };
        for (var id = 0; id < 3; id++)
            c.Nations.Add(new Nation { Id = id, MapIndex = 600 + id, Tag = "TR" + id, Name = "Treasury " + id,
                Population = 10_000_000, Gdp = 100, Treasury = 10, OutputScale = 1f, Stability = .6f, Inflation = .02f });
        return c;
    }

    static void Week(Campaign c, int week) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)(week * 7 + 6) * 24);

    public static void Run()
    {
        TreasuryDynamics.ResetCatalog();
        try
        {
            var c = Fixture();
            c.Defines.FinancialCrisisChance = 0f;
            c.Defines.PandemicChance = 0f;
            foreach (var shock in TreasuryDynamics.ShockKinds) shock.AnnualChance = 0f;
            var (calm, hoarder, inflating) = (c.Nations[0], c.Nations[1], c.Nations[2]);
            hoarder.Treasury = 80f;
            inflating.Treasury = 10f;
            inflating.Inflation = 0.6f;
            Week(c, 1);
            TreasuryDynamics.Tick(c);
            Check(Math.Abs(calm.Treasury - 10f) < 1e-4f, "a modest treasury at low inflation keeps its value");
            Check(hoarder.Treasury < 80f, "a hoard above the comfortable is spent down");
            Check(inflating.Treasury < 10f, "high inflation eats reserves");
            Check(c.WorldLeverage > c.Defines.StartingLeverage - 1e-4f, "calm builds leverage");

            // A crisis in one country travels to the partner that trades with it, not to the one that does not.
            var crisis = Fixture();
            crisis.TradeRoutes.Add(new TradeRoute { SellerId = 0, BuyerId = 1, Commodity = Commodity.Machinery, Volume = 10, Value = 50 });
            crisis.Defines.ContagionStrength = 10f;
            crisis.Defines.ContagionFloor = 0f;
            TreasuryDynamics.Strike(crisis, crisis.Nations[0], 1f, true);
            // The bailout is a timed cut to the struck country's income now (IncomePenalties), not a lump
            // out of its reserves: it still pays, over the slump, and nobody else pays for it.
            Check(crisis.Nations[0].Treasury == 10f && crisis.Nations[0].IncomePenalties is { Count: 1 } &&
                  crisis.Nations[0].IncomePenalties[0].Source == "Financial crisis" && crisis.Nations[0].RecessionDepth > 0f,
                "the struck country pays the bailout out of its income for a time and falls into recession");
            Check(crisis.Nations[2].IncomePenalties is null, "a bystander pays none of it");
            Week(crisis, 2);
            Check(TreasuryDynamics.OutputFactor(crisis, crisis.Nations[0]) < 1f, "and its output is down");
            typeof(TreasuryDynamics).GetMethod("Spread", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { crisis, crisis.Nations[0], 1f });
            Check(crisis.Nations[1].RecessionDepth > 0f, "its trading partner catches it");
            Check(crisis.Nations[2].RecessionDepth == 0f, "a country that does not trade with either does not");
            Check(crisis.Nations[1].RecessionDepth < crisis.Nations[0].RecessionDepth, "and the domino is weaker than the first blow");

            var print = CampaignFingerprint.Of(Fixture());
            var changed = Fixture();
            changed.Nations[0].RecessionDepth = 0.05f;
            changed.Nations[0].RecessionUntilDay = 300;
            Check(CampaignFingerprint.Of(changed) != print, "a recession is saved state the fingerprint sees");
        }
        finally
        {
            TreasuryDynamics.ResetCatalog();
        }
    }
}
