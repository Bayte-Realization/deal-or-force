// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using DealOrForce.Simulation;
using Experiment = DealOrForce.Rules.AssaultRules;
using ExperimentalAssessment = DealOrForce.Rules.Assessment;
using ExperimentalReason = DealOrForce.Rules.Reason;

internal static class Program
{
    private const int RandomCases = 100_000;
    private const uint RandomSeed = 0xA55A2026;
    private static readonly Campaign World = new(20261016, new Defines());
    private static readonly Army Formation = new() { Id = 1, NationId = 0 };
    // Fixture only: set extreme clocks without simulating billions of campaign days.
    private static readonly Action<Campaign, long> SetHours = typeof(Campaign)
        .GetProperty(nameof(Campaign.ElapsedHours))!.GetSetMethod(nonPublic: true)!
        .CreateDelegate<Action<Campaign, long>>();
    private static long _cases;
    private static uint _bits = RandomSeed;

    private static readonly float[] Boundaries =
    {
        float.NegativeInfinity, -float.MaxValue, -1f, -float.Epsilon, -0.0f, 0f, float.Epsilon,
        MathF.BitDecrement(.16f), .16f, MathF.BitIncrement(.16f),
        MathF.BitDecrement(.20f), .20f, MathF.BitIncrement(.20f),
        .5f, MathF.BitDecrement(1f), 1f, MathF.BitIncrement(1f), float.MaxValue,
        float.PositiveInfinity, float.NaN,
        BitConverter.Int32BitsToSingle(unchecked((int)0x7F800001)),
        BitConverter.Int32BitsToSingle(unchecked((int)0xFFC01234))
    };

    private static readonly int[] Days =
    {
        int.MinValue, int.MinValue + 1, -1_000_000, -1, 0, 1, 1_000_000, int.MaxValue - 1, int.MaxValue
    };

    private static int Main()
    {
        try
        {
            Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; " +
                $"architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Require(Enum.GetNames<AssaultReadiness.Reason>().SequenceEqual(Enum.GetNames<ExperimentalReason>()),
                "reason names/order match the source contract");
            World.Nations.Add(new Nation { Id = 0, Tag = "TEST", Name = "Assault rules fixture", Population = 1_000_000 });
            World.Armies.Add(Formation);
            Phase("explicit semantics", ExplicitSemantics);
            Phase("all condition/floor boundary combinations", BoundaryMatrix);
            Phase("order/plan/direct/recovery/invalid priority combinations", PriorityMatrix);
            Phase("all recovery day boundary pairs", RecoveryMatrix);
            Phase("missing context combinations", MissingContexts);
            Phase("deterministic raw-bit random cases", RandomMatrix);
            CheckReadOnly();
            Console.WriteLine($"ASSAULT_RULES_EXPERIMENT_PASS cases={_cases} failures=0 random_seed=0x{RandomSeed:X8} random_cases={RandomCases}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"ASSAULT_RULES_EXPERIMENT_FAIL cases={_cases} {error}");
            return 1;
        }
    }

    private static void Phase(string name, Action run)
    {
        var before = _cases;
        run();
        Console.WriteLine($"PASS {name}: {_cases - before} differential cases");
    }

    private static void ExplicitSemantics()
    {
        ExperimentalAssessment Check(ExperimentalReason expected, ArmyOrder order = ArmyOrder.AdvanceFront,
            bool plan = true, bool direct = false, int ready = 0, int elapsed = 0,
            float org = 1, float supply = 1, float orgFloor = .16f, float supplyFloor = .20f)
        {
            var result = Compare(order, plan, direct, ready, elapsed, org, supply, orgFloor, supplyFloor);
            Require(result.Reason == expected, $"expected {expected}, got {result.Reason}");
            return result;
        }
        Check(ExperimentalReason.Ready);
        Check(ExperimentalReason.NoAssaultOrder, ArmyOrder.DefendFront, false, true, 1, 0, float.NaN);
        Check(ExperimentalReason.PlanPaused, plan: false, direct: true, ready: 1, org: float.NaN);
        Check(ExperimentalReason.Recovering, direct: true, ready: 1, org: float.NaN);
        Check(ExperimentalReason.Unavailable, direct: true, org: float.NaN);
        Check(ExperimentalReason.DirectOrder, direct: true, org: 0, supply: 0);
        Check(ExperimentalReason.LowOrganisation, org: .16f, supply: .20f);
        Check(ExperimentalReason.LowSupply, supply: .20f);
        Check(ExperimentalReason.Ready, org: MathF.BitIncrement(.16f), supply: MathF.BitIncrement(.20f));
        Check(ExperimentalReason.Ready, org: 0, supply: 0, orgFloor: 0, supplyFloor: 0);
        Check(ExperimentalReason.Ready, org: 0, supply: 0, orgFloor: -1, supplyFloor: -1);
        Check(ExperimentalReason.LowOrganisation, orgFloor: 2);
        Check(ExperimentalReason.LowOrganisation, org: .16f, orgFloor: float.NaN);
        Check(ExperimentalReason.LowSupply, supply: .20f, supplyFloor: float.PositiveInfinity);
        Require(Check(ExperimentalReason.Recovering, ready: int.MaxValue, elapsed: int.MinValue).RecoveryDays == int.MaxValue,
            "positive widened recovery difference saturates");
        Require(Check(ExperimentalReason.Ready, ready: int.MinValue, elapsed: int.MaxValue).RecoveryDays == 0,
            "negative widened recovery difference clamps to zero");
        Check(ExperimentalReason.Ready, ArmyOrder.Spearhead);
    }

    private static void BoundaryMatrix()
    {
        foreach (var org in Boundaries)
        foreach (var supply in Boundaries)
        foreach (var orgFloor in Boundaries)
        foreach (var supplyFloor in Boundaries)
        foreach (var direct in new[] { false, true })
            Compare(ArmyOrder.AdvanceFront, true, direct, 0, 0, org, supply, orgFloor, supplyFloor);
    }

    private static void PriorityMatrix()
    {
        var orders = Enum.GetValues<ArmyOrder>().Concat(new[] { (ArmyOrder)(-1), (ArmyOrder)int.MaxValue });
        foreach (var order in orders)
        foreach (var plan in new[] { false, true })
        foreach (var direct in new[] { false, true })
        foreach (var ready in new[] { -1, 0, 1, int.MaxValue })
        foreach (var org in Boundaries)
        foreach (var supply in Boundaries)
            Compare(order, plan, direct, ready, 0, org, supply, .16f, .20f);
    }

    private static void RecoveryMatrix()
    {
        foreach (var ready in Days)
        foreach (var elapsed in Days)
        foreach (var order in new[] { ArmyOrder.AdvanceFront, ArmyOrder.Spearhead, ArmyOrder.Manual })
        foreach (var plan in new[] { false, true })
        foreach (var direct in new[] { false, true })
            Compare(order, plan, direct, ready, elapsed, 1, 1, .16f, .20f);
    }

    private static void MissingContexts()
    {
        foreach (var hasCampaign in new[] { false, true })
        foreach (var hasArmy in new[] { false, true })
        foreach (var ready in Days)
        foreach (var org in Boundaries)
            Compare(ArmyOrder.Spearhead, true, true, ready, int.MinValue, org, float.NegativeInfinity,
                float.NaN, float.PositiveInfinity, hasCampaign, hasArmy);
    }

    private static void RandomMatrix()
    {
        for (var index = 0; index < RandomCases; index++)
            Compare((ArmyOrder)((int)(Next() % 7) - 1), (Next() & 1) != 0, (Next() & 1) != 0,
                unchecked((int)Next()), unchecked((int)Next()), FloatBits(), FloatBits(), FloatBits(), FloatBits(),
                (Next() & 31) != 0, (Next() & 31) != 0);
    }

    private static ExperimentalAssessment Compare(ArmyOrder order, bool plan, bool direct, int ready, int elapsed,
        float org, float supply, float orgFloor, float supplyFloor, bool hasCampaign = true, bool hasArmy = true)
    {
        Formation.OrderKind = order; Formation.PlanActive = plan; Formation.DirectOrder = direct;
        Formation.ReadyDay = ready; Formation.Organisation = org; Formation.SupplyLevel = supply;
        World.Defines.AutonomousAssaultMinimumOrganisation = orgFloor;
        World.Defines.AutonomousAssaultMinimumSupply = supplyFloor;
        SetHours(World, (long)elapsed * 24);
        Require(World.ElapsedDays == elapsed, "fixture clock round trip");
        var randomBefore = World.Random.State;
        var expected = LegacyAssaultReadiness.Assess(hasCampaign ? World : null, hasArmy ? Formation : null);
        var integrated = AssaultReadiness.Assess(hasCampaign ? World : null, hasArmy ? Formation : null);
        Require(Same(expected, integrated), "live C# adapter matches the frozen C# oracle in every field");
        var actual = Experiment.Assess(hasCampaign, hasArmy, order is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead,
            plan, direct, ready, elapsed, org, supply, orgFloor, supplyFloor);
        _cases++;
        if (expected.Ready != actual.Ready || (int)expected.Reason != (int)actual.Reason ||
            Bits(expected.Organisation) != Bits(actual.Organisation) ||
            Bits(expected.MinimumOrganisation) != Bits(actual.MinimumOrganisation) ||
            Bits(expected.Supply) != Bits(actual.Supply) || Bits(expected.MinimumSupply) != Bits(actual.MinimumSupply) ||
            expected.RecoveryDays != actual.RecoveryDays)
            throw new InvalidOperationException($"case {_cases}: order={order}, plan={plan}, direct={direct}, ready={ready}, elapsed={elapsed}, " +
                $"org=0x{Bits(org):X8}, supply=0x{Bits(supply):X8}, floors=0x{Bits(orgFloor):X8}/0x{Bits(supplyFloor):X8}, " +
                $"contexts={hasCampaign}/{hasArmy}; C#={expected}; F#={actual}");
        Require(World.Random.State == randomBefore && World.ElapsedDays == elapsed && Formation.OrderKind == order &&
            Formation.PlanActive == plan && Formation.DirectOrder == direct && Formation.ReadyDay == ready &&
            Bits(Formation.Organisation) == Bits(org) && Bits(Formation.SupplyLevel) == Bits(supply) &&
            Bits(World.Defines.AutonomousAssaultMinimumOrganisation) == Bits(orgFloor) &&
            Bits(World.Defines.AutonomousAssaultMinimumSupply) == Bits(supplyFloor), "input fields and RNG unchanged");
        return actual;
    }

    private static void CheckReadOnly()
    {
        Compare(ArmyOrder.Spearhead, true, false, 7, 7, .1f, .6f, .16f, .2f);
        var fingerprint = CampaignFingerprint.Of(World);
        var random = World.Random.State;
        var hours = World.ElapsedHours;
        var armySnapshot = JsonSerializer.Serialize(Formation, new JsonSerializerOptions { IncludeFields = true });
        var orgFloor = Bits(World.Defines.AutonomousAssaultMinimumOrganisation);
        var supplyFloor = Bits(World.Defines.AutonomousAssaultMinimumSupply);
        for (var index = 0; index < 4096; index++)
        {
            var expected = AssaultReadiness.Assess(World, Formation);
            var actual = Experiment.Assess(true, true, true, Formation.PlanActive, Formation.DirectOrder,
                Formation.ReadyDay, World.ElapsedDays, Formation.Organisation, Formation.SupplyLevel,
                World.Defines.AutonomousAssaultMinimumOrganisation, World.Defines.AutonomousAssaultMinimumSupply);
            Require(expected.Reason == AssaultReadiness.Reason.LowOrganisation && actual.Reason == ExperimentalReason.LowOrganisation,
                "repeated assessment sees live input");
        }
        Require(CampaignFingerprint.Of(World) == fingerprint && World.Random.State == random && World.ElapsedHours == hours &&
            JsonSerializer.Serialize(Formation, new JsonSerializerOptions { IncludeFields = true }) == armySnapshot &&
            Bits(World.Defines.AutonomousAssaultMinimumOrganisation) == orgFloor &&
            Bits(World.Defines.AutonomousAssaultMinimumSupply) == supplyFloor, "state/RNG unchanged");
        Console.WriteLine("PASS state/RNG unchanged: 4096 repeated calls per assessor; fingerprint, army, clock and floors unchanged");
    }

    private static bool Same(AssaultAssessment a, AssaultAssessment b) =>
        a.Ready == b.Ready && a.Reason == b.Reason && a.RecoveryDays == b.RecoveryDays &&
        Bits(a.Organisation) == Bits(b.Organisation) && Bits(a.MinimumOrganisation) == Bits(b.MinimumOrganisation) &&
        Bits(a.Supply) == Bits(b.Supply) && Bits(a.MinimumSupply) == Bits(b.MinimumSupply);

    private static uint Next()
    {
        _bits ^= _bits << 13; _bits ^= _bits >> 17; _bits ^= _bits << 5;
        return _bits;
    }
    private static float FloatBits() => BitConverter.Int32BitsToSingle(unchecked((int)Next()));
    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
