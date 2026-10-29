using System;
using System.Collections.Generic;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Who a country was born out of, and why it must be remembered rather than guessed.
///
/// A breakaway written back into its parent at a peace clears the live claim (`SecededFromId`) —
/// right for the war, wrong for the memory — and the next quarrel over the same ground used to
/// fall back to reading tag prefixes. That rule also said yes to two unrelated countries sharing
/// a prefix, which is how wars between distinct countries were labelled civil conflicts and
/// quietly changed their peace terms. The tests below hold the new order: recorded link first,
/// permanent record next, faction data and rising names after that, and a legacy prefix rule only
/// for a save that predates the record.
/// </summary>
static class RevoltLineageTests
{
    static void Check(bool done, string label)
    {
        if (!done) throw new Exception("revolt lineage: " + label);
        Console.WriteLine("PASS revolt lineage: " + label);
    }

    static Campaign Fixture(out Nation parent, out Nation breakaway)
    {
        var c = new Campaign(20261029) { ScenarioId = "revolt_lineage_fixture" };
        parent = c.AddNation(new Nation { Tag = "RPZ", Name = "Parentland", MapIndex = 701, Population = 5_000_000, Gdp = 500f, Treasury = 50f });
        breakaway = c.AddNation(new Nation { Tag = "RPZA", Name = "South Parentland", MapIndex = 702, Population = 1_000_000, Gdp = 100f, Treasury = 10f });
        return c;
    }

    public static void Run()
    {
        // ---- the live claim ------------------------------------------------------------
        {
            Fixture(out var parent, out var born);
            born.SecededFromId = parent.Id;
            Check(Reintegration.IsBreakawayOf(born, parent), "a recorded claim is a breakaway");
            Check(!Reintegration.IsBreakawayOf(parent, born), "and it is not symmetric");
            Check(!Reintegration.IsBreakawayOf(parent, parent), "a country is not its own breakaway");
            Check(!Reintegration.IsBreakawayOf(null, parent), "nothing is nobody's breakaway");
        }

        // ---- the memory outlives the claim ---------------------------------------------
        {
            Fixture(out var parent, out var born);
            born.SecededFromId = parent.Id;
            born.RevoltParentId = parent.Id;
            // A peace writes the country back in and clears the claim, exactly as Factions does.
            born.SecededFromId = -1;
            Check(Reintegration.IsBreakawayOf(born, parent),
                "a reconciled breakaway is still that country's own, after the claim is cleared");
        }

        // ---- resemblance is not evidence -----------------------------------------------
        {
            var c = new Campaign(20261029) { ScenarioId = "revolt_lineage_prefix" };
            var parent = c.AddNation(new Nation { Tag = "CHA", Name = "Chad", MapIndex = 711, Population = 2_000_000 });
            var other = c.AddNation(new Nation { Tag = "CHAD", Name = "Chad Republic", MapIndex = 712, Population = 1_000_000 });
            var stranger = c.AddNation(new Nation { Tag = "MLI", Name = "Mali", MapIndex = 713, Population = 1_000_000 });
            other.FoundedInRevolt = "national";
            // The game has since recorded that this country came out of somebody else entirely.
            other.RevoltParentId = stranger.Id;
            Check(!Reintegration.IsBreakawayOf(other, parent),
                "a shared tag prefix is not enough once the game recorded a different parent");
            Check(Reintegration.IsBreakawayOf(other, stranger),
                "and the recorded parent is the one that answers yes");

            // The same country in a save that predates the record: the old rule still applies,
            // because there is nothing better to read.
            other.RevoltParentId = -1;
            Check(Reintegration.IsBreakawayOf(other, parent),
                "a legacy save keeps the old prefix rule rather than losing its civil wars");
        }

        // ---- a rising under arms, and a faction the data names --------------------------
        {
            Fixture(out var parent, out var rebel);
            rebel.Name = "rising:RPZ:RPZ:1971";
            Check(Reintegration.IsBreakawayOf(rebel, parent), "a rising's name carries its parent");

            Fixture(out var government, out var faction);
            Factions.Merge(FactionDocument(), null);
            faction.FactionId = "revolt_lineage_fixture_faction";
            Check(Reintegration.IsBreakawayOf(faction, government),
                "the faction data names the parent, which needs no live claim");
        }

        // ---- and the record is visible to the fingerprint -------------------------------
        {
            var first = Fixture(out var parentA, out var bornA);
            var second = Fixture(out var parentB, out var bornB);
            var plain = CampaignFingerprint.Of(first);
            var claimed = CampaignFingerprint.Of(second);
            Check(plain == claimed, "two identical worlds fingerprint alike before anything happens");
            bornB.RevoltParentId = parentB.Id;
            Check(CampaignFingerprint.Of(second) != plain,
                "recording who a country came out of changes the fingerprint");
            _ = bornA; _ = parentA;
        }
    }

    /// <summary>A one-entry faction document in the shipped shape, with a unique id for the fixture.</summary>
    static JsonElement FactionDocument()
    {
        using var document = JsonDocument.Parse("""
        {
          "factions": [
            {
              "id": "revolt_lineage_fixture_faction",
              "tag": "RPZB", "name": "Fixture faction", "parent": "RPZ",
              "colour": "#808080"
            }
          ]
        }
        """);
        return document.RootElement.Clone();
    }
}
