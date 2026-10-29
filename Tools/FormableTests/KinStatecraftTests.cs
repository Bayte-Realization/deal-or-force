using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// A country's own hand among its people across a border.
///
/// What the tests hold to: only our own peoples count, and only where they are enough of the
/// region to be worth the risk; the operation writes into state other systems already read -
/// regional unrest, the pair's grievance, a movement's hidden fighters - rather than a counter
/// of its own; it costs what it says, and it is refused in a sentence when it cannot be done;
/// and two worlds with the same seed make the same decision about being caught.
/// </summary>
static class KinStatecraftTests
{
    static void Check(bool done, string label)
    {
        if (!done) throw new Exception("kin statecraft: " + label);
        Console.WriteLine("PASS kin statecraft: " + label);
    }

    const string MovementId = "kin_test_movement";

    static void DeclareCatalog()
    {
        MovementCatalog.ResetCatalog();
        using var document = JsonDocument.Parse("""
        {
          "kin_test_movement": {
            "tag": "KTM", "name": "Kin test rising", "description": "fixture",
            "colour": "#a05050", "peoples": ["english"], "headquarters": "k_1",
            "homeland": ["k_1"], "support": { "k_1": 0.4 },
            "treasury": 1, "cadres": 2000, "legitimacy": 0.3,
            "government": "provisional_government", "ideology": "nationalism",
            "traits": ["armed_wing"],
            "start_active": false
          }
        }
        """);
        MovementCatalog.Merge(document.RootElement, null);
        // The fixture countries are not in history/countries, so who they are for has to be
        // said out loud: english is ours, french is the host's.
        DemographyCatalog.Declare("KAA", new[] { "english" });
        DemographyCatalog.Declare("KHB", new[] { "french" });
    }

    /// <summary>
    /// k_0 is ours and k_1 is the host's, on a border the two share. k_1's people are mostly
    /// the host's own, with a share of ours: enough to work, not enough to be the region.
    /// </summary>
    static Campaign Fixture(out Nation ours, out Nation host, out ScenarioState abroad, out ScenarioState home,
        float ourShareAbroad = 0.3f, bool withMovement = false, float oursTreasury = 5f, float ourManpower = 20_000f,
        float ourPoliticalPoints = 20f)
    {
        var c = new Campaign(20261029) { ScenarioId = "kin_statecraft_fixture" };
        ours = c.AddNation(new Nation
        {
            Tag = "KAA", Name = "Kinland", MapIndex = 601, Population = 4_000_000, Gdp = 400f,
            Treasury = oursTreasury, ManpowerReserve = ourManpower, Stability = .8f, WarSupport = .7f,
            PoliticalPoints = ourPoliticalPoints,
            Government = "parliamentary_republic", Ideology = "liberalism"
        });
        host = c.AddNation(new Nation
        {
            Tag = "KHB", Name = "Hostland", MapIndex = 602, Population = 6_000_000, Gdp = 600f,
            Treasury = 40f, Stability = .8f, Government = "parliamentary_republic", Ideology = "liberalism"
        });
        home = new ScenarioState { Id = "k_0", Name = "Kinland Proper", MapIndex = 3101, NationMapIndex = 601, Population = 4_000_000 };
        home.PopulationGroups.Add(new PopulationGroup { Culture = "english", Religion = "protestant", Size = 4_000_000 });
        abroad = new ScenarioState { Id = "k_1", Name = "Marches", MapIndex = 3102, NationMapIndex = 602, Population = 2_000_000 };
        var ourSize = (long)Math.Round(2_000_000 * ourShareAbroad);
        if (ourSize > 0) abroad.PopulationGroups.Add(new PopulationGroup { Culture = "english", Religion = "protestant", Size = ourSize });
        abroad.PopulationGroups.Add(new PopulationGroup { Culture = "french", Religion = "catholic", Size = 2_000_000 - ourSize });
        c.States.Add(home);
        c.States.Add(abroad);

        // Two blocks of provinces side by side: the border the volunteers have to be able to walk.
        const int width = 32, height = 8;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var cell = y * width + x;
                var left = x < 16;
                states[cell] = (ushort)(left ? 3101 : 3102);
                owners[cell] = (ushort)(left ? 601 : 602);
            }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, width, height, _ => false));
        ours.CapitalStateMapIndex = 3101;
        host.CapitalStateMapIndex = 3102;
        c.ConfigureSinglePlayer(ours.Id);

        if (withMovement)
        {
            Movements.Activate(c, MovementCatalog.Of(MovementId));
            var movement = c.NationByTag("KTM");
            if (movement?.Movement is { } state) state.Support[3102] = 0.4f;
        }
        return c;
    }

    public static void Run()
    {
        try
        {
            DeclareCatalog();

        // ---- measuring: ours are ours, theirs are not --------------------------------
        {
            var c = Fixture(out var ours, out var host, out var abroad, out var home);
            var (count, total, share) = KinStatecraft.Kin(c, ours, abroad);
            Check(count == 600_000, "our people in the marches are counted, and only ours");
            Check(total == 2_000_000, "the region's whole population is the denominator");
            Check(Math.Abs(share - 0.3f) < 0.001f, "a share of the region is a share of it");
            var (theirs, _, _) = KinStatecraft.Kin(c, host, home);
            Check(theirs == 0, "the host has no people of its own in our country");
            Check(KinStatecraft.HostOf(c, abroad)?.Id == host.Id, "the ground belongs to whoever holds it");
        }

        // ---- the sentence, not the disabled button -----------------------------------
        {
            var c = Fixture(out var ours, out _, out var abroad, out var home);
            Check(KinStatecraft.Refusal(c, ours, home, "agitate").Length > 0,
                "our own ground has no border to work across, and says so");
            var thin = Fixture(out var oursThin, out _, out var abroadThin, out _, ourShareAbroad: 0.02f);
            var few = KinStatecraft.Refusal(thin, oursThin, abroadThin, "agitate");
            Check(few.Length > 0 && few.Contains("Marches"), "too few of ours is refused, naming the region");
            Check(KinStatecraft.Refusal(c, ours, abroad, "no_such_action").Length > 0,
                "an action nobody has heard of is refused");
        }

        // ---- working them costs what it says and moves what others read --------------
        {
            var c = Fixture(out var ours, out var host, out var abroad, out _);
            var beforeTreasury = ours.Treasury;
            var beforeRadicalism = abroad.Radicalism;
            var beforeGrievance = CampaignDiplomacy.Peek(c, ours.Id, host.Id)?.GrievanceB ?? 0f;
            Check(KinStatecraft.Cost(c, ours, abroad, "agitate") > 0, "an operation is priced by how many of ours are there");
            Check(KinStatecraft.Refusal(c, ours, abroad, "agitate").Length == 0, "nothing refuses it in this world");
            Check(KinStatecraft.Do(c, ours, abroad, "agitate"), "working our kin is done");
            Check(ours.Treasury < beforeTreasury, "the organisers are paid for");
            Check(abroad.Radicalism > beforeRadicalism, "unrest in the region rises, which the secession rules read");
            var afterGrievance = CampaignDiplomacy.Peek(c, ours.Id, host.Id)?.GrievanceB ?? 0f;
            Check(afterGrievance > beforeGrievance, "the host carries a grievance about it");
            Check(KinStatecraft.DaysRemaining(c, ours, abroad, "agitate") > 0,
                "and the network needs time before it is asked again");
            Check(KinStatecraft.Refusal(c, ours, abroad, "agitate").Length > 0,
                "a second operation at once is refused, not silently dropped");
            Check(c.Chronicle.Entries.Any(entry => (entry.Kind ?? "").StartsWith("kin_agitation", StringComparison.Ordinal)),
                "the act is written down where a player can read it");
        }

        // ---- volunteers: only to a rising of our own people, only across a border we share
        {
            var noMovement = Fixture(out var oursNone, out _, out var abroadNone, out _);
            var refused = KinStatecraft.Refusal(noMovement, oursNone, abroadNone, "volunteers");
            Check(KinStatecraft.KinMovement(noMovement, oursNone, abroadNone) is null,
                "nothing of ours is fighting in the marches yet");
            Check(refused.Length > 0 && refused.Contains("Marches"),
                "volunteers are refused without a rising of ours to join (answer was: " + refused + ")");

            var c = Fixture(out var ours, out _, out var abroad, out _, withMovement: true);
            var movement = c.NationByTag("KTM");
            Check(movement?.Movement is not null, "the fixture's rising exists");
            var beforeHidden = movement.Movement.Infiltrated.GetValueOrDefault(abroad.MapIndex);
            var beforeManpower = ours.ManpowerReserve;
            var men = KinStatecraft.Volunteers(c, ours, abroad);
            Check(men >= 1, "somebody is sent even from a small people");
            Check(KinStatecraft.Refusal(c, ours, abroad, "volunteers").Length == 0,
                "a shared border and a rising of ours is enough");
            Check(KinStatecraft.Do(c, ours, abroad, "volunteers"), "the volunteers go");
            var afterHidden = movement.Movement.Infiltrated.GetValueOrDefault(abroad.MapIndex);
            Check(Math.Abs(afterHidden - (beforeHidden + men)) < 0.5f,
                "and are the hidden fighters an uprising is called from");
            Check(ours.ManpowerReserve < beforeManpower, "they are men out of our own reserve");
            Check(c.Chronicle.Entries.Any(entry => (entry.Kind ?? "").StartsWith("kin_volunteers", StringComparison.Ordinal)),
                "crossing the border is written down");
        }

        // ---- a movement of our own people, founded on their ground -------------------
        {
            // Half the region ours: enough for a movement, and none of ours fighting there yet.
            var c = Fixture(out var ours, out var host, out var abroad, out _, ourShareAbroad: 0.5f,
                oursTreasury: 20f, ourPoliticalPoints: 40f);
            Check(KinStatecraft.Refusal(c, ours, abroad, "found_proxy").Length == 0,
                "a firm footing, a shared border and credit are enough to found a movement");
            var before = c.Nations.Count;
            var beforeMovements = 0;
            foreach (var nation in c.Nations) if (nation.Movement is not null) beforeMovements++;
            Check(KinStatecraft.Do(c, ours, abroad, "found_proxy"), "the movement is founded");
            Check(c.Nations.Count == before + 1, "and it is a country in its own right");

            Nation born = null;
            foreach (var nation in c.Nations)
                if (nation.Movement is not null && nation.Id != ours.Id) born = nation;
            Check(born is not null, "it exists as a movement");
            Check(born.Movement.Landless, "holding no ground, like every movement");
            Check(born.Movement.HeadquartersStateMapIndex == abroad.MapIndex, "with its headquarters in their region");
            Check(born.Movement.SponsoredBy.ContainsKey(ours.Id), "we pay for it");
            Check(born.Movement.RecognisedBy.Contains(ours.Id), "and recognise it");
            Check(born.Movement.Peoples.Count > 0 && DemographyCatalog.IsPrimary(ours.Tag, born.Movement.Peoples[0]),
                "it speaks for our own people");
            Check(born.Movement.Homeland.Contains(abroad.Id), "and claims the ground it was founded in");
            Check(born.Movement.Support.GetValueOrDefault(abroad.MapIndex) > 0f, "with a following on the first day");
            Check(born.Tag != host.Tag && born.Tag != ours.Tag, "under a tag of its own");
            Check(born.Name.Length > 0, "and a name of its own");
            Check(ours.PoliticalPoints < 40f, "founding it costs political credit");
            Check(c.Chronicle.Entries.Any(entry => (entry.Kind ?? "").StartsWith("proxy_founded", StringComparison.Ordinal)),
                "the founding is written down");
            Check(KinStatecraft.Refusal(c, ours, abroad, "found_proxy").Length > 0,
                "a second movement in the same region is refused, because the first is still there");
            _ = beforeMovements;
        }

        // ---- and the refusals that keep it rare ---------------------------------------
        {
            var thin = Fixture(out var oursThin, out _, out var abroadThin, out _, ourShareAbroad: 0.2f, oursTreasury: 20f, ourPoliticalPoints: 40f);
            var few = KinStatecraft.Refusal(thin, oursThin, abroadThin, "found_proxy");
            Check(few.Length > 0 && few.Contains("Marches"), "too few of ours is refused, naming the region");

            var poor = Fixture(out var oursPoor, out _, out var abroadPoor, out _, ourShareAbroad: 0.5f, oursTreasury: 0.0001f, ourPoliticalPoints: 40f);
            Check(KinStatecraft.Refusal(poor, oursPoor, abroadPoor, "found_proxy").Length > 0,
                "and a treasury that cannot pay is refused");

            var unknown = Fixture(out var oursUnknown, out _, out var abroadUnknown, out _, ourShareAbroad: 0.5f, oursTreasury: 20f, ourPoliticalPoints: 0f);
            Check(KinStatecraft.Refusal(unknown, oursUnknown, abroadUnknown, "found_proxy").Length > 0,
                "and a government with no political credit cannot be seen doing it");
        }

        // ---- our offices inside somebody else's region --------------------------------
        {
            // Half the region ours, and a treasury that can pay to open the doors.
            var c = Fixture(out var ours, out var host, out var abroad, out _, ourShareAbroad: 0.5f,
                oursTreasury: 20f, ourPoliticalPoints: 40f);
            host.WeeklyTaxIncome = 10f;
            Check(KinStatecraft.Refusal(c, ours, abroad, "open_administration").Length == 0,
                "a quarter of the region and the money to open them is enough");
            Check(KinStatecraft.Do(c, ours, abroad, "open_administration"), "the offices open");
            Check(ParallelAdministration.Ours(c, ours, abroad), "and they are ours");
            Check(ours.PoliticalPoints < 40f, "opening them costs political credit");

            var beforeHost = host.Treasury;
            var beforeOurs = ours.Treasury;
            var beforeUnrest = abroad.Radicalism;
            ParallelAdministration.TickWeek(c);
            Check(host.Treasury < beforeHost, "the host no longer collects all of its tax here");
            Check(ours.Treasury > beforeOurs, "and what it loses, we take");
            Check(abroad.Radicalism > beforeUnrest, "the ground sours while nobody can see who is doing it");
            Check(abroad.AdministrationTaken > 0f, "and what has been taken is recorded for the panel");
            Check(ParallelAdministration.Describe(c, abroad).Length > 0, "the region can say who runs its paperwork");

            Check(KinStatecraft.Refusal(c, ours, abroad, "open_administration").Length > 0,
                "a second administration in the same region is refused");
            Check(KinStatecraft.Do(c, ours, abroad, "close_administration"), "and ours can be closed");
            Check(!ParallelAdministration.Any(abroad), "after which the region is the host's paperwork again");
            Check(KinStatecraft.Refusal(c, ours, abroad, "close_administration").Length > 0,
                "with nothing left to close");
        }

        // ---- being caught, and being shut ---------------------------------------------
        {
            var c = Fixture(out var ours, out var host, out var abroad, out _, ourShareAbroad: 0.5f,
                oursTreasury: 20f, ourPoliticalPoints: 40f);
            host.WeeklyTaxIncome = 10f;
            // Certain discovery and a host with the strength to act: the worst week it can have.
            c.Defines.KinAdminDiscovery = 1f;
            c.Defines.KinAdminShutdownChance = 1f;
            Check(KinStatecraft.Do(c, ours, abroad, "open_administration"), "the offices open in a hostile world");
            ParallelAdministration.TickWeek(c);
            Check(abroad.AdministrationExposed, "the host notices at once when it is certain to");
            Check(c.Chronicle.Entries.Any(entry => (entry.Kind ?? "") == "administration_exposed"),
                "and says so in public");
            // The same week's shutdown can follow exposure in the next week; one more week proves it.
            ParallelAdministration.TickWeek(c);
            Check(!ParallelAdministration.Any(abroad), "a strong host shuts them down");
            Check(c.Chronicle.Entries.Any(entry => (entry.Kind ?? "") == "administration_shut"),
                "and the shutting is written down");
        }

        // ---- the computer may found a movement, and that changes the list it is reading ----
        {
            // The crash a campaign reported on 24 July 2026: the AI walked `campaign.Nations`
            // while founding a movement, which adds a country to that list mid-enumeration.
            // The treasury has to clear the AI's own floor, which is measured in years of tax
            // income - a real country's, not a fixture's - so it is set to what that floor needs.
            var c = Fixture(out var ours, out var host, out var abroad, out _, ourShareAbroad: 0.5f,
                oursTreasury: 100f, ourPoliticalPoints: 40f);
            CampaignDiplomacy.Get(c, ours.Id, host.Id);           // a relation exists, at opinion 0
            c.Defines.AiKinOpinion = 100f;                        // ...which is bad enough for it
            c.Defines.AiKinProxyChance = 1f;                      // and the rarest act always pays
            // The fixture makes us the human player; the computer's look deliberately skips
            // a player's country, so hand this one to the computer for the test.
            ours.IsHuman = false;
            ours.IsPlayer = false;
            Check(KinStatecraft.ScanSummary(c).Contains("candidates=1"),
                "the month's scan finds our kin in somebody else's ground");
            var before = c.Nations.Count;
            var threw = false;
            try { KinStatecraft.Ai(c); }
            catch (InvalidOperationException) { threw = true; }
            Check(!threw, "the computer's own look does not modify the list it is walking");
            Check(c.Nations.Count == before + 1, "and it did found the movement it was considering");
            // One more pass over the live list, which would have thrown had it been left dirty.
            var movements = 0;
            foreach (var nation in c.Nations) if (nation.Movement is not null) movements++;
            Check(movements == 1, "leaving the world enumerable, with exactly one new movement in it");
            _ = abroad;
        }

        // ---- the same world twice makes the same decision ----------------------------
        {
            var first = Fixture(out var oursFirst, out _, out var abroadFirst, out _);
            var second = Fixture(out var oursSecond, out _, out var abroadSecond, out _);
            KinStatecraft.Do(first, oursFirst, abroadFirst, "agitate");
            KinStatecraft.Do(second, oursSecond, abroadSecond, "agitate");
            Check(Math.Abs(abroadFirst.Radicalism - abroadSecond.Radicalism) < 1e-6f,
                "the same seed and day leave the same unrest behind");
            Check(first.Chronicle.Entries.Count == second.Chronicle.Entries.Count,
                "and write the same number of lines");
            Check(first.Chronicle.Entries.Last().Kind == second.Chronicle.Entries.Last().Kind,
                "including whether the service noticed");
        }

        // ---- a mod can switch the whole thing off -----------------------------------
        {
            var c = Fixture(out var ours, out _, out var abroad, out _);
            c.FeatureOverrides["movements"] = false;
            var before = abroad.Radicalism;
            new KinStatecraftCommand { IssuerId = ours.Id, RegionMapIndex = abroad.MapIndex, Action = "agitate" }.Apply(c);
            Check(Math.Abs(abroad.Radicalism - before) < 1e-6f,
                "with the movements feature off, the command does nothing");
        }

        // ---- the discovery roll is a probability, not a sentence ----------------------
        // Six attempts in a bench year were all caught at a stated 22%, which has an expected
        // chance of one in ten thousand. The effect still applies on exposure, so a roll that
        // never lands reads as "always caught" rather than as a bug - and nobody notices.
        {
            const int samples = 40_000;
            var caughtLow = 0;
            var caughtHigh = 0;
            for (var i = 0; i < samples; i++)
            {
                // Inputs shaped like a real world: country ids into the hundreds, days into
                // decades, a few thousand regions.
                var roll = MovementRules.Roll(947986505, 1 + i % 327, i / 3, 1 + i % 1870);
                if (roll < 0.22f) caughtLow++;
                if (roll < 0.45f) caughtHigh++;
            }
            var low = caughtLow / (float)samples;
            var high = caughtHigh / (float)samples;
            Check(Math.Abs(low - 0.22f) < 0.01f, $"the 22% discovery roll catches about 22% of attempts ({low:P2})");
            Check(Math.Abs(high - 0.45f) < 0.01f, $"the 45% discovery roll catches about 45% of attempts ({high:P2})");
            Check(MovementRules.Roll(947986505, 12, 400, 7) == MovementRules.Roll(947986505, 12, 400, 7),
                "the same country, day and region roll the same number twice");
            var differing = 0;
            for (var day = 0; day < 200; day++)
                if (Math.Abs(MovementRules.Roll(947986505, 12, day, 7) - MovementRules.Roll(947986505, 12, day + 1, 7)) > 1e-6f)
                    differing++;
            Check(differing > 180, "consecutive days are not the same number");
        }

        // ---- the decision to act and the discovery roll are two different dice --------
        // The computer acts on a roll under its own chance (2-10 %) and Do decides exposure on
        // a roll under 15-45 %. One salt for both means the second die is never really thrown:
        // the operation that happened was already below every threshold. Ten years of the bench
        // recorded 26 agitations and 26 exposures before the two were split apart.
        {
            var c = Fixture(out var ours, out _, out var abroad, out _);
            var selectionSalt = abroad.MapIndex;
            Check(KinStatecraft.DiscoverySalt(abroad, "agitate") != selectionSalt,
                "discovery rolls on a different salt than the decision to act");
            var acted = 0;
            var caught = 0;
            for (var day = 0; day < 4000; day++)
            {
                if (MovementRules.Roll(c.Seed, ours.Id, day, selectionSalt) >= 0.10f) continue;
                acted++;
                if (MovementRules.Roll(c.Seed, ours.Id, day,
                        KinStatecraft.DiscoverySalt(abroad, "agitate")) < 0.22f) caught++;
            }
            Check(acted > 300, $"the computer would have acted on {acted} of 4000 days");
            var share = caught / (float)acted;
            Check(Math.Abs(share - 0.22f) < 0.05f,
                $"of the days it acted, about 22% are caught rather than all of them ({share:P1})");
        }
        }
        finally
        {
            MovementCatalog.ResetCatalog();
        }
    }
}
