using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class MovementPowerStagingTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("movement power staging: " + label); Console.WriteLine("PASS movement power staging: " + label); }

    public static void Run()
    {
        OriginalOrderAndAmounts();
        DisabledAndUnscheduledAreNoOps();
        PublicFramesAndEagerStepsAgree();
        ExternalOrderCompletesTheMovementReview();
    }

    static Campaign World(int day = 3)
    {
        var c = Campaign.CreateBootstrap(27);
        var player = c.NationByTag("KOR");
        c.ConfigureSinglePlayer(player.Id);
        c.ObserverMode = true;
        c.Defines.HoursPerTick = 24;
        c.Defines.EventIntervalDays = 0;
        var region = c.StatesOf(player).First();
        c.AttachProvinceTopology(ProvinceTopology.Build(
            Enumerable.Repeat((ushort)region.MapIndex, 16).ToArray(),
            Enumerable.Repeat((ushort)player.MapIndex, 16).ToArray(), 4, 4, _ => false));
        c.Armies.Clear();
        foreach (var movement in c.Nations.Where(n => n.Movement is { Dissolved: false } &&
                     Movements.DefinitionOf(n)?.Has("for_hire") != true).Take(8))
        {
            movement.Treasury = 2f;
            var state = movement.Movement;
            state.Legitimacy = .95f; state.Cadres = 24000; state.IncomeWeekly = .01f;
            foreach (var host in c.Nations.Where(n => n.Movement is null).Take(8)) state.RecognisedBy.Add(host.Id);
            foreach (var s in c.States) state.Support[s.MapIndex] = .8f;
            state.ReadyOnDay["march"] = 0;
            state.ReadyOnDay["strike"] = 0;
        }
        // A human movement still administers, but must never have an AI action chosen.
        var human = c.Nations.First(n => n.Movement is { Dissolved: false });
        human.IsHuman = true;
        // Keep a real pending action cooldown in the authoritative fixture.
        human.Movement.ReadyOnDay["strike"] = 17;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);
        c.Date.Advance(day * 24);
        c.IsPaused = false;
        SovereignFinance.NormalizeAccounts(c);
        return c;
    }

    static Campaign WorldWithDue()
    {
        // Movement power is staggered by nation, the same way the weekly movement review
        // already was. Pick a day on which the review actually produces a strike, march or
        // administration, so the staging contract is exercised rather than an empty day.
        for (var day = 3; day < 17; day++)
        {
            var probe = World(day);
            if (!probe.Nations.Any(n => n.Movement is { Dissolved: false } &&
                MovementRules.OnItsDay(probe, n.Id, 7))) continue;
            MovementPower.Tick(probe);
            if (probe.Chronicle.Read(-1, -1, true)
                .Any(entry => entry.Kind is "general_strike" or "march_failed" or "march_toppled" or "march_concessions"))
                return World(day);
        }
        return World(3);
    }

    static void OriginalOrderAndAmounts()
    {
        var original = WorldWithDue(); var staged = WorldWithDue(); var eager = WorldWithDue();
        Check(CampaignFingerprint.Of(original) == CampaignFingerprint.Of(staged) &&
              CampaignFingerprint.Of(staged) == CampaignFingerprint.Of(eager), "paired seed 27 worlds start identically");
        var before = CampaignFingerprint.Of(original);
        OriginalTick(original);
        var stages = MovementPower.TickStages(staged).Count();
        MovementPower.Tick(eager);
        var wanted = CampaignFingerprint.Of(original);
        var due = staged.Nations.Count(n => n.Movement is { Dissolved: false } &&
            MovementRules.OnItsDay(staged, n.Id, 7));
        Check(stages == due,
            "each due movement, including a human, receives exactly one bounded stage");
        Check(wanted == CampaignFingerprint.Of(staged) && wanted == CampaignFingerprint.Of(eager),
            "original sequential review, staged review and public eager wrapper have identical authoritative results");
        Check(original.Random.State == staged.Random.State && staged.Random.State == eager.Random.State,
            "actor staging neither consumes nor reorders campaign randomness");
        Check(wanted != before && original.Chronicle.Read(-1, -1, true)
                .Any(e => e.Kind is "general_strike" or "march_failed" or "march_toppled" or "march_concessions"),
            "comparison includes real administration and strike or march consequences, not only idle actors");
        var human = staged.Nations.First(n => n.Movement is not null && n.IsHuman);
        Check(human.Movement.ReadyOnDay["strike"] == 17 && human.Movement.ReadyOnDay.GetValueOrDefault("march") == 0,
            "human review keeps its planned cooldown and does not select a march");
    }

    static void DisabledAndUnscheduledAreNoOps()
    {
        Check(!MovementPower.TickStages(null).Any(), "null world has no stages");
        foreach (var kind in new[] { "feature", "system" })
        {
            var c = World(3);
            if (kind == "feature") c.SetFeature("movements", false);
            if (kind == "system")
            { c.Defines.Features["test_movement_gate"] = false; c.Defines.FeatureSystems[nameof(MovementPower)] = "test_movement_gate"; }
            var before = CampaignFingerprint.Of(c); var random = c.Random.State;
            Check(!MovementPower.TickStages(c).Any(), kind + " gate yields no actor");
            MovementPower.Tick(c);
            Check(before == CampaignFingerprint.Of(c) && random == c.Random.State,
                kind + " gate leaves money, support, actions and random state unchanged");
        }
    }

    static void PublicFramesAndEagerStepsAgree()
    {
        var eager = World(2); var staged = World(2);
        var frames = 0;
        for (var day = 0; day < 9; day++)
        {
            eager.Tick();
            var dayFrames = 0;
            // Guard each day, like StageBudgetTests. The former cumulative 5000
            // counted nine days together and rejected a finite sequence of more finely
            // split passes even though every day's eager/staged fingerprints agree.
            while (!staged.StepFor(0))
            {
                frames++;
                if (++dayFrames > 5000) throw new Exception("Movement staged day did not finish within 5000 stages.");
            }
            Console.WriteLine($"MOVEMENT_DAY_SLICES day={staged.ElapsedDays} passes={dayFrames + 1} limit=5000");
            Check(eager.ElapsedHours == staged.ElapsedHours && !staged.MidStep &&
                CampaignFingerprint.Of(eager) == CampaignFingerprint.Of(staged),
                "public StepFor(0) and Tick agree after day " + staged.ElapsedDays);
        }
        Check(frames > 8 && eager.Random.State == staged.Random.State,
            "many public frame boundaries preserve the same seed 27 world including pending movement cooldowns");
    }

    static void ExternalOrderCompletesTheMovementReview()
    {
        var expected = WorldWithDue(); var staged = WorldWithDue();
        SimProfiler.Reset();
        var frames = 0;
        while (SimProfiler.All.FirstOrDefault(s => s.Name == "movement power")?.Runs < 1 ||
               !SimProfiler.All.Any(s => s.Name == "movement power"))
        {
            staged.StepFor(0);
            if (++frames > 1000) throw new Exception("Could not reach the first movement actor boundary.");
        }
        Check(staged.MidStep && SimProfiler.All.Single(s => s.Name == "movement power").Runs == 1,
            "a public zero-budget frame can stop after exactly one movement has finished its review");
        var completed = 0; staged.StepCompleted += _ => completed++;
        expected.Tick();
        expected.Commands.Enqueue(new SetTaxRateCommand { IssuerId = expected.Player.Id, Value = .22f });
        expected.Commands.ApplyAll(expected);
        staged.Commands.Enqueue(new SetTaxRateCommand { IssuerId = staged.Player.Id, Value = .22f });
        staged.Commands.ApplyAll(staged);
        Check(!staged.MidStep && completed == 1 && CampaignFingerprint.Of(staged) == CampaignFingerprint.Of(expected),
            "an external order drains the remaining movement actors and whole day before changing its tax, exactly once");
    }

    // The pre-staging loop, retained only as the semantic oracle. Uses the same public
    // authoritative eligibility/command seams; the original levy arithmetic is below.
    static void OriginalTick(Campaign c)
    {
        if (c is null || !c.FeatureEnabled("movements") || !MechanicFeatures.SystemEnabled(c, nameof(MovementPower))) return;
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not { Dissolved: false } state) continue;
            if (!MovementRules.OnItsDay(c, nation.Id, 7)) continue;
            if (MovementPower.Momentum(c, nation) >= c.Defines.MovementParallelMomentum) OriginalAdminister(c, nation, state);
            if (nation.IsHuman) continue;
            foreach (var action in new[] { "march", "strike" })
            {
                if (MovementActions.Refusal(c, nation, action, -1, -1).Length > 0) continue;
                if (MovementRules.Roll(c.Seed, nation.Id, c.ElapsedDays, action == "march" ? 4721 : 4722) > .25f) continue;
                new MovementActionCommand { IssuerId = nation.Id, Action = action }.Apply(c);
                break;
            }
        }
    }

    static void OriginalAdminister(Campaign c, Nation nation, MovementState state)
    {
        var levy = 0f;
        foreach (var region in c.States)
        {
            if (region.NationMapIndex == nation.MapIndex ||
                state.Support.GetValueOrDefault(region.MapIndex) < c.Defines.MovementParallelSupport) continue;
            var host = c.Nations.Find(n => n.MapIndex == region.NationMapIndex);
            if (host is null || host.Movement is not null) continue;
            var output = MathF.Max(0f, host.Gdp) * region.Population / MathF.Max(1f, host.Population);
            var take = output * M.Clamp01(c.Defines.MovementParallelLevy);
            if (take <= 0f || host.Treasury < take) continue;
            host.Treasury -= take; levy += take;
            region.Stability = M.Clamp01(region.Stability - .002f);
        }
        if (levy <= 0f) return;
        nation.Treasury += levy; state.IncomeWeekly += levy;
    }
}
