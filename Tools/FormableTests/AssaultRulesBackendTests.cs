using System;
using DealOrForce.Simulation;

/// <summary>
/// The game now calls the F# pure rule for assault eligibility. This is the deployment
/// check the Tools-only experiment did not cover: the same campaign/army values must yield
/// exactly the same assessment through the F# bridge and through the managed fallback.
/// </summary>
static class AssaultRulesBackendTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("assault rules backend: " + label); Console.WriteLine("PASS assault rules backend: " + label); }

    public static void Run()
    {
        var campaign = new Campaign(9101);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 100L * 24);
        campaign.Defines.AutonomousAssaultMinimumOrganisation = .16f;
        campaign.Defines.AutonomousAssaultMinimumSupply = .20f;
        var army = new Army { Id = 1, NationId = 0 };
        campaign.Armies.Add(army);

        void Reset()
        {
            army.OrderKind = ArmyOrder.AdvanceFront;
            army.PlanActive = true;
            army.DirectOrder = false;
            army.ReadyDay = 0;
            army.Organisation = .80f;
            army.SupplyLevel = .80f;
        }

        var cases = new (string Name, Action Arrange)[]
        {
            ("no assault order", () => army.OrderKind = ArmyOrder.DefendFront),
            ("paused plan", () => army.PlanActive = false),
            ("recovering", () => army.ReadyDay = 103),
            ("low organisation", () => army.Organisation = .10f),
            ("low supply", () => army.SupplyLevel = .05f),
            ("direct order", () => army.DirectOrder = true),
            ("ready", () => { })
        };

        foreach (var (name, arrange) in cases)
        {
            Reset();
            arrange();
            var live = AssaultReadiness.Assess(campaign, army);
            var oracle = AssaultReadiness.AssessManaged(campaign, army);
            Check(live == oracle, $"{name}: F# and managed agree ({live.Reason}/{oracle.Reason})");
        }

        Reset();
        var days = campaign.ElapsedDays;
        var organisation = army.Organisation;
        var supply = army.SupplyLevel;
        var random = campaign.Random.State;
        for (var read = 0; read < 4096; read++) _ = AssaultReadiness.Assess(campaign, army);
        Check(campaign.ElapsedDays == days && army.Organisation == organisation &&
              army.SupplyLevel == supply && campaign.Random.State == random,
            "4096 assessments change no clock, condition or RNG");
        Check(AssaultRulesHook.Installed,
            "the F# rule is installed as the active pure-rule backend");
    }
}
