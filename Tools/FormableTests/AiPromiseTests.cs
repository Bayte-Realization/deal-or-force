using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>Governments the player does not run take the same public bet: a promise with a
/// date on it, measured by the same check, refused for the same reasons - and the world
/// hears about it.</summary>
static class AiPromiseTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL ai promises: " + label);
        Console.WriteLine("PASS ai promises: " + label);
    }

    static void SetDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);

    public static void Run()
    {
        try
        {
            // Injected in the shape a mod writes: one short commitment whose outcome is
            // already true, one long one with the same outcome, and one that can never be
            // kept. All three are readable through the treasury they do not touch.
            var errors = new List<string>();
            using (var doc = JsonDocument.Parse(
                "{\"ai_test_short\":{\"name\":\"AI test short\",\"description\":\"d\",\"duration_days\":20,\"check\":{\"min_stability\":0.0},\"effects\":{}}," +
                "\"ai_test_long\":{\"name\":\"AI test long\",\"description\":\"d\",\"duration_days\":90,\"check\":{\"min_stability\":0.0},\"effects\":{}}," +
                "\"ai_test_never\":{\"name\":\"AI test never\",\"description\":\"d\",\"duration_days\":20,\"check\":{\"max_stability\":-1.0},\"failure_effects\":{}}}"))
                PromiseCatalog.Merge(doc.RootElement, errors);
            Check(errors.Count == 0, "the test promises merged beside the shipped ones");

            // Character off and appetite certain: the only question left is whether and how
            // the AI promises, which is what this suite is about. The appetite is capped at a
            // half per own week by design - a government may gamble, not shout weekly - so a
            // month of own days is the smallest window in which "it promises at all" is a
            // fact of the seed rather than a coin toss.
            var campaign = Campaign.CreateBootstrap(1601);
            campaign.Defines.AiTemperamentSpread = 0f;
            campaign.Defines.AiPromiseAppetite = 1f;

            var madeBefore = AiStatecraft.Tally.GetValueOrDefault("promise_made");
            var madeByHand = 0;
            for (var day = 1; day <= 35; day++)
            {
                SetDay(campaign, day);
                var openBefore = campaign.Nations.Sum(nation => nation.Promises.Count);
                GovernmentPromises.Tick(campaign);
                var settled = campaign.Nations.Sum(nation => nation.Promises.Count);
                AiPromises.Tick(campaign);
                madeByHand += campaign.Nations.Sum(nation => nation.Promises.Count) - settled;
                // Nothing may be settled out of thin air while the AI is promising.
                if (openBefore < settled) throw new Exception("FAIL ai promises: promises vanished unaccounted");
            }

            var promised = campaign.Nations.Where(nation => !nation.IsHuman)
                .Sum(nation => nation.Promises.Count);
            Check(promised > 0, "AI governments promise when their crisis qualifies");
            Check(campaign.Nations.Count(nation => !nation.IsHuman && nation.Promises.Count > 0) > 1,
                "its neighbours promise on their own days too, not one chosen favourite");
            Check(campaign.Nations.Where(nation => !nation.IsHuman)
                    .SelectMany(nation => nation.Promises)
                    .All(promise => promise.DueDay - promise.MadeDay == 20),
                "it takes the shortest commitment on offer, not the longest");
            Check(campaign.Player.Promises.Count == 0, "the player's own government is never promised for");
            Check(AiStatecraft.Tally.GetValueOrDefault("promise_made") - madeBefore == madeByHand,
                "each AI promise is counted exactly once for the bench");
            Check(campaign.Chronicle.Entries.Any(e => e.Kind == "promise_made" && e.ActorId != campaign.Player.Id),
                "an AI promise is public news the day it is made");

            // A disabled mechanic stops the AI on exactly the terms it stops the player.
            campaign.SetFeature("government_promises", false);
            SetDay(campaign, 42);
            AiPromises.Tick(campaign);
            Check(campaign.Nations.Sum(nation => nation.Promises.Count) == promised,
                "a disabled mechanic stops AI promises too");
            campaign.SetFeature("government_promises", true);

            // The rest of the year in the real order: settle first, then promise.
            var keptBefore = AiStatecraft.Tally.GetValueOrDefault("promise_kept");
            var brokenBefore = AiStatecraft.Tally.GetValueOrDefault("promise_broken");
            for (var day = 43; day <= 365; day++)
            {
                SetDay(campaign, day);
                GovernmentPromises.Tick(campaign);
                AiPromises.Tick(campaign);
            }
            Check(AiStatecraft.Tally.GetValueOrDefault("promise_kept") +
                  AiStatecraft.Tally.GetValueOrDefault("promise_broken") > keptBefore + brokenBefore,
                "AI promises settle, kept or broken, like anybody's");
            var cap = Math.Max(1, campaign.Defines.MaxConcurrentPromises);
            Check(campaign.Nations.All(nation => nation.Promises.Count <= cap),
                "the concurrency limit holds for AI promises");
            Check(campaign.Chronicle.Entries.Any(e => e.Kind is "promise_kept" or "promise_broken" &&
                    e.ActorId != campaign.Player.Id),
                "an AI promise's settlement is public news");

            // A cabinet handed to the AI promises like one: it already takes decisions through
            // the same portfolio, so a government that spends but never speaks would be half
            // a delegation.
            var delegated = Campaign.CreateBootstrap(1603);
            delegated.Defines.AiTemperamentSpread = 0f;
            delegated.Defines.AiPromiseAppetite = 1f;
            Delegation.Set(delegated.Player, Delegation.Cabinet, true);
            // Counted as made, not as open: the test promises conclude the day after they are
            // made, so an open-promise count at the end of the window is zero for anyone whose
            // own day was not the last one - which is how this check first reported "no pledge"
            // for a cabinet that had pledged ten times.
            var playerMade = 0;
            for (var day = 1; day <= 70; day++)
            {
                SetDay(delegated, day);
                GovernmentPromises.Tick(delegated);
                var open = delegated.Player.Promises.Count;
                AiPromises.Tick(delegated);
                playerMade += delegated.Player.Promises.Count - open;
            }
            Check(playerMade > 0, "a cabinet handed to the AI pledges in public");

            // One seed, two machines: the same governments promise the same things.
            var first = Campaign.CreateBootstrap(1602);
            var second = Campaign.CreateBootstrap(1602);
            foreach (var world in new[] { first, second })
            {
                world.Defines.AiTemperamentSpread = 0f;
                world.Defines.AiPromiseAppetite = 1f;
            }
            for (var day = 1; day <= 60; day++)
            {
                SetDay(first, day); SetDay(second, day);
                GovernmentPromises.Tick(first); GovernmentPromises.Tick(second);
                AiPromises.Tick(first); AiPromises.Tick(second);
            }
            string Shape(Campaign world) => string.Join(";", world.Nations.Select(nation =>
                nation.Id + ":" + string.Join(",", nation.Promises
                    .Select(promise => promise.Id + "@" + promise.DueDay))));
            Check(Shape(first) == Shape(second), "two campaigns of one seed promise identically");
        }
        finally
        {
            PromiseCatalog.ResetCatalog();
        }
    }
}
