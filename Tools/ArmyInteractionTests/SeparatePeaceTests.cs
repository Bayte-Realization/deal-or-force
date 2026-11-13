using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Leaving a coalition war before it ends (SeparatePeace): on the old lines, giving up held
/// ground, changing sides; what it leaves between the leaver and the allies it left; a leader's
/// subsidy; and the war going on without it.
/// </summary>
static class SeparatePeaceTests
{
    static int failures;
    static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
        if (!ok) failures++;
    }

    public static void Run(ProvinceTopology shared)
    {
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), "a fresh province map: " + message);
        var c = Campaign.CreateBootstrap(20261109);
        c.AttachProvinceTopology(map);
        var ven = c.NationByTag("VEN");
        var guy = c.NationByTag("GUY");
        var bra = c.NationByTag("BRA");
        var col = c.NationByTag("COL");
        var ecu = c.NationByTag("ECU");
        var sur = c.NationByTag("SUR");
        Check(new[] { ven, guy, bra, col, ecu, sur }.All(n => n is not null), "the countries of the test exist");
        foreach (var other in new[] { guy, bra, col, ecu, sur })
        {
            var r = CampaignDiplomacy.Get(c, ven.Id, other.Id);
            r.TruceUntil = 0; r.NonAggressionPact = false; r.Stance = DiplomaticStance.Neutral;
        }
        var war = CampaignDiplomacy.DeclareWar(c, ven.Id, guy.Id, onOwnInitiative: false);
        Check(war is not null, "Venezuela goes to war with Guyana");
        if (war is null) { Environment.Exit(1); return; }
        CampaignDiplomacy.Get(c, bra.Id, guy.Id).Stance = DiplomaticStance.Allied;
        Check(CampaignDiplomacy.JoinWar(c, war, bra.Id, defending: true) && CampaignDiplomacy.JoinWar(c, war, col.Id, defending: true) &&
              CampaignDiplomacy.JoinWar(c, war, sur.Id, defending: true) && CampaignDiplomacy.JoinWar(c, war, ecu.Id, defending: false),
            "Brazil, Colombia and Suriname join Guyana; Ecuador joins Venezuela");

        Check(SeparatePeace.Refusal(c, war, guy.Id).Length > 0, "a war's leader cannot make a separate peace");
        Check(SeparatePeace.Refusal(c, war, bra.Id).Length > 0, "nobody leaves a war in its first weeks");
        war.StartedDay = c.ElapsedDays - 200;
        Check(SeparatePeace.Refusal(c, war, bra.Id).Length == 0, "after two months a member may leave");

        // Brazil holds a Venezuelan province; Venezuela holds a Guyanese one.
        var topology = c.ProvinceMap;
        int First(Nation n) => Enumerable.Range(1, topology.ProvinceCount).First(p => topology.ProvinceToNation[p] == n.MapIndex && !topology.IsWater(p));
        var venProvince = First(ven);
        var guyProvince = First(guy);
        war.OriginalController[venProvince] = c.ProvinceController[venProvince];
        c.SetProvinceController(venProvince, (ushort)bra.MapIndex);
        war.OriginalController[guyProvince] = c.ProvinceController[guyProvince];
        c.SetProvinceController(guyProvince, (ushort)ven.MapIndex);
        var supportBefore = war.DefenderWarSupport;

        var wish = SeparatePeace.Wish(c, war, bra);
        Check(wish >= 0f && wish <= 100f, $"a member's wish to leave is a share ({wish:0})");
        var acceptance = SeparatePeace.Acceptance(c, war, bra.Id, SeparatePeaceTerms.WhiteExit);
        Check(acceptance >= 0f && acceptance <= 100f, $"the other side's acceptance is a share ({acceptance:0})");

        Check(SeparatePeace.Sign(c, war, bra.Id, SeparatePeaceTerms.WhiteExit), "Brazil makes a separate peace on the old lines");
        Check(!war.Involves(bra.Id) && !war.Ended, "Brazil is out and the war goes on without it");
        Check(CampaignDiplomacy.Peek(c, bra.Id, ven.Id)?.Stance == DiplomaticStance.Truce, "Brazil and Venezuela have a truce");
        Check(CampaignDiplomacy.Peek(c, ven.Id, guy.Id)?.Stance == DiplomaticStance.AtWar, "Venezuela and Guyana are still at war");
        var bond = CampaignDiplomacy.Peek(c, bra.Id, guy.Id);
        Check(bond is not null && bond.Stance != DiplomaticStance.Allied && OpinionSystem.Has(bond, "separate_peace_betrayal"),
            "the alliance with Guyana is over, and Guyana remembers being left");
        Check(c.ProvinceController[venProvince] == ven.MapIndex && !war.OriginalController.ContainsKey(venProvince),
            "the Venezuelan province Brazil held goes back to Venezuela");
        Check(c.ProvinceController[guyProvince] == ven.MapIndex, "Venezuela keeps fighting over the Guyanese province it holds");
        Check(war.DefenderWarSupport < supportBefore, "the coalition Brazil left loses war support");
        Check(war.SeparatePeaces.Count == 1 && war.SeparatePeaces[0].NationId == bra.Id, "the war records who left");
        Check(c.Armies.Where(a => a.NationId == bra.Id).All(a => a.AssignedWarId != war.Id), "Brazil's armies are released from the war");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "separate_peace" && e.ActorId == bra.Id), "the world hears of it");

        // Colombia gives up a region Venezuela holds whole.
        var colRegion = c.StatesOf(col).Where(s => !s.Unclaimed)
            .OrderBy(s => Enumerable.Range(1, topology.ProvinceCount).Count(p => topology.ProvinceToState[p] == s.MapIndex)).First();
        var held = Enumerable.Range(1, topology.ProvinceCount).Where(p => topology.ProvinceToState[p] == colRegion.MapIndex).ToList();
        foreach (var p in held) { war.OriginalController[p] = c.ProvinceController[p]; c.SetProvinceController(p, (ushort)ven.MapIndex); }
        Check(SeparatePeace.Sign(c, war, col.Id, SeparatePeaceTerms.Concede), "Colombia leaves, giving up what Venezuela holds");
        Check(war.SeparatePeaces[^1].ProvincesCeded > 0 && held.All(p => topology.ProvinceToNation[p] == ven.MapIndex || c.ProvinceController[p] == col.MapIndex),
            $"the ceded region becomes Venezuela's ({war.SeparatePeaces[^1].ProvincesCeded} provinces), and nothing is left half-held");

        // Ecuador changes sides.
        Check(SeparatePeace.Sign(c, war, ecu.Id, SeparatePeaceTerms.WhiteExit, switchSides: true), "Ecuador changes sides");
        Check(war.IsDefending(ecu.Id) && CampaignDiplomacy.Peek(c, ecu.Id, ven.Id)?.Stance == DiplomaticStance.AtWar,
            "Ecuador now fights Venezuela in the same war");
        Check(OpinionSystem.Has(CampaignDiplomacy.Peek(c, ecu.Id, ven.Id), "switched_sides_against_us"), "Venezuela remembers the turncoat");

        // A leader's subsidy.
        guy.Treasury = 100f;
        var before = sur.Treasury;
        Check(SeparatePeace.Subsidise(c, war, guy.Id, sur.Id).Length == 0 && sur.Treasury > before, "Guyana pays Suriname to stay in");
        Check(SeparatePeace.Subsidise(c, war, guy.Id, sur.Id).Length > 0, "and not again the next day");

        // The command, as a player's order would arrive.
        var count = war.SeparatePeaces.Count;
        new SeparatePeaceCommand { IssuerId = sur.Id, WarId = war.Id, MemberId = sur.Id, Terms = SeparatePeaceTerms.Indemnity }.Apply(c);
        Check(war.SeparatePeaces.Count == count + (war.Involves(sur.Id) ? 0 : 1), "the order either signs or leaves a refusal, nothing between");

        // A month of the world with all this in it.
        for (var day = 0; day < 30; day++) c.TickDay();
        Check(true, "a month passes after the separate peaces");

        Console.WriteLine(failures == 0 ? "separate peace: all passed" : $"separate peace: {failures} FAILED");
        if (failures > 0) Environment.Exit(1);
    }
}
