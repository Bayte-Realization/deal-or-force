using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Borders that look like borders.
///
/// The plan's own words: the AI keeps borders beautiful, and the rule must reach peace demands and
/// deals, not only purchases. Deals already pay for it (TerritorialCoherence.Penalty); a peace
/// conference did not, so a winner could spend its whole war score on islands of somebody else's
/// land and leave its own border looking like a rash.
///
/// The check here is written against the map itself - province adjacency read straight out of the
/// topology - not against TerritorialCoherence, so the two can disagree and say so.
/// </summary>
static class BorderCoherenceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("border coherence: " + label);
        Console.WriteLine("PASS border coherence: " + label);
    }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(947986505);
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var why))
            throw new Exception(why);
        c.AttachProvinceTopology(topology);
        Check(c.HasOwnedLand(c.NationByTag("USA")), "the fixture has a country with ground to be adjacent to");

        // A province of the defender that borders the attacker's land, and one in a part of the
        // defender that does not. Found from the map, not from the rule being tested.
        Nation attacker = null, defender = null;
        int borderProvince = 0, farProvince = 0;
        foreach (var a in c.Nations)
        {
            if (a.Movement is not null) continue;
            foreach (var d in c.Nations)
            {
                if (d.Id == a.Id || d.Movement is not null || !c.HasOwnedLand(d)) continue;
                var touching = Enumerable.Range(1, topology.ProvinceCount)
                    .Where(p => !topology.IsWater(p) && topology.ProvinceToNation[p] == d.MapIndex)
                    .Where(p => topology.Neighbors[p].Any(q => topology.ProvinceToNation[q] == a.MapIndex))
                    .ToArray();
                if (touching.Length == 0) continue;
                var borderState = topology.ProvinceToState[touching[0]];
                var farStates = c.States
                    .Where(s => s.NationMapIndex == d.MapIndex && s.Population > 0 && s.MapIndex != borderState)
                    .Where(s => Enumerable.Range(1, topology.ProvinceCount)
                        .Where(p => topology.ProvinceToState[p] == s.MapIndex)
                        .SelectMany(p => topology.Neighbors[p])
                        .All(q => topology.ProvinceToNation[q] != a.MapIndex))
                    .ToArray();
                if (farStates.Length == 0) continue;
                var farState = farStates[0];
                var far = Enumerable.Range(1, topology.ProvinceCount)
                    .First(p => topology.ProvinceToState[p] == farState.MapIndex && !topology.IsWater(p));
                attacker = a;
                defender = d;
                borderProvince = touching[0];
                farProvince = far;
                break;
            }
            if (attacker is not null) break;
        }

        Check(attacker is not null && borderProvince > 0 && farProvince > 0,
            "a real border and a real hinterland were found on the map");
        if (attacker is null) return;

        Check(!PeaceLedger.DetachedFromBeneficiary(c, null, attacker, borderProvince),
            "ground on the border is not detached from the country taking it");
        Check(PeaceLedger.DetachedFromBeneficiary(c, null, attacker, farProvince),
            "ground it cannot walk to is priced as an enclave");

        // A movement holds no land: nothing it takes can be detached from ground it does not have.
        var landless = new Nation { Id = 990_001, Tag = "LND", Name = "Landless" };
        Check(!PeaceLedger.DetachedFromBeneficiary(c, null, landless, farProvince),
            "a country with no ground of its own is not charged for enclaves it cannot have");

        // The price is what a conference actually asks, not a rule that exists only in a comment.
        var war = new StrategicWar { Id = 9902, AttackerId = attacker.Id, DefenderId = defender.Id };
        var ledger = new PeaceLedger(c, war, attacker.Id);
        var near = ledger.Price(borderProvince).Cost;
        var far_cost = ledger.Price(farProvince).Cost;
        Check(far_cost > 0f && near > 0f, "both provinces are priced at all");
        var reason = ledger.Price(farProvince).Reason ?? "";
        Check(reason.Contains("detached"), "and the enclave says why it costs more: " + reason);
    }
}
