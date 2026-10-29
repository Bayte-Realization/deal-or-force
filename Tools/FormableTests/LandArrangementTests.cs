using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Leases and troops in without a war (LandArrangementSystem): the same orders for the player and the computer.</summary>
static class LandArrangementTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("land arrangements: " + note); Console.WriteLine("PASS land arrangements: " + note); }

    // Two countries side by side: AAA holds the western half (regions 3001, 3002), BBB the eastern
    // (3003, 3004). Region 3003 touches AAA and its people are AAA's own.
    static Campaign Fixture()
    {
        var c = new Campaign(20261029) { ScenarioId = "land_arrangement_fixture" };
        var tenant = new Nation { Id = 0, MapIndex = 500, Tag = "AAA", Name = "Tenantland", Population = 10_000_000,
            Treasury = 50, Gdp = 200, OutputScale = 1f, Stability = .7f, CapitalStateMapIndex = 3001 };
        var owner = new Nation { Id = 1, MapIndex = 501, Tag = "BBB", Name = "Ownerland", Population = 10_000_000,
            Treasury = 10, Gdp = 50, OutputScale = 1f, Stability = .6f, CapitalStateMapIndex = 3004 };
        tenant.FoundedCultures.Add("tenantish");
        owner.FoundedCultures.Add("ownerish");
        c.Nations.Add(tenant);
        c.Nations.Add(owner);
        for (var i = 0; i < 4; i++)
        {
            var nation = i < 2 ? tenant : owner;
            var region = new ScenarioState { MapIndex = 3001 + i, Id = "land_" + i, Name = "Region " + i,
                NationMapIndex = nation.MapIndex, NationId = nation.Tag, Population = 2_500_000,
                StandardOfLiving = .5f, Literacy = .8f, Urbanization = .5f, RegionalOutputValue = 0.2f };
            region.PopulationGroups.Add(new PopulationGroup
            {
                Culture = i == 2 || i < 2 ? "tenantish" : "ownerish", Size = 2_500_000, Profession = "worker"
            });
            c.States.Add(region);
        }
        var cells = Enumerable.Range(0, 8192).Select(i => (ushort)(3001 + i % 128 / 32)).ToArray();
        var flags = Enumerable.Range(0, 8192).Select(i => (ushort)(i % 128 < 64 ? 500 : 501)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, flags, 128, 64, _ => false));
        SovereignFinance.NormalizeAccounts(c);
        // Attaching the map seats each government in its first region; put them where the fixture says.
        (tenant.CapitalStateMapIndex, tenant.CapitalProvinceId) = (3001, FirstProvince(c, 3001));
        (owner.CapitalStateMapIndex, owner.CapitalProvinceId) = (3004, FirstProvince(c, 3004));
        c.Armies.Add(new Army { Id = 1, NationId = tenant.Id, Divisions = 6, Strength = 1f, Organisation = 1f,
            ProvinceId = FirstProvince(c, 3001), StateMapIndex = 3001 });
        c.Armies.Add(new Army { Id = 2, NationId = owner.Id, Divisions = 2, Strength = 1f, Organisation = 1f,
            ProvinceId = FirstProvince(c, 3003), StateMapIndex = 3003 });
        CampaignDiplomacy.Get(c, tenant.Id, owner.Id).Opinion = 60f;
        return c;
    }

    static int FirstProvince(Campaign c, int region) =>
        Enumerable.Range(1, c.ProvinceMap.ProvinceCount).First(p => c.ProvinceMap.ProvinceToState[p] == region);

    static bool HeldBy(Campaign c, int region, Nation nation) =>
        Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToState[p] == region)
            .All(p => c.ProvinceController[p] == nation.MapIndex);

    static void Day(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);

    static LandArrangementCommand Order(Campaign c, Nation issuer, string action, int region = -1, int offer = -1)
    {
        var command = new LandArrangementCommand { IssuerId = issuer.Id, Action = action, RegionMapIndex = region, OfferId = offer };
        command.Apply(c);
        return command;
    }

    public static void Run()
    {
        Leases();
        TroopsWithoutWar();
        Republic();
        PlayerDesk();
        Persistence();
    }

    static void Leases()
    {
        var c = Fixture();
        var (tenant, owner) = (c.Nations[0], c.Nations[1]);
        var capital = Order(c, tenant, "request_lease", 3004);
        Check(capital.Result.Length > 0 && c.LandArrangements.Count == 0, "no government lets its capital");
        var asked = Order(c, tenant, "request_lease", 3003);
        Check(asked.Done && c.LandArrangements.Count == 1 && HeldBy(c, 3003, tenant),
            "a friendly owner lets the region, and the tenant holds it on the map");
        Check(c.States.First(s => s.MapIndex == 3003).NationMapIndex == owner.MapIndex, "the land stays the owner's in law");
        Occupations.Tick(c);
        Check(HeldBy(c, 3003, tenant), "the end of no war sends the tenant home: it holds by arrangement");
        var lease = c.LandArrangements[0];
        var (before, ownerBefore) = (tenant.Treasury, owner.Treasury);
        Day(c, 5);
        LandArrangementSystem.Tick(c);
        Check(tenant.Treasury < before && Math.Abs(owner.Treasury - ownerBefore - (before - tenant.Treasury)) < 1e-4f,
            "the rent goes from the tenant to the owner every week");
        Check(Order(c, owner, "end_lease", 3003).Done && lease.LeavingOnDay > c.ElapsedDays, "the owner may end it: the tenant gets time to leave");
        var notoriety = tenant.Notoriety;
        Check(Order(c, tenant, "stay", 3003).Done && lease.Kind == LandArrangementSystem.Presence && tenant.Notoriety > notoriety,
            "a tenant told to leave may stay - a presence now, and the world notices");
        Check(Order(c, tenant, "withdraw", 3003).Done && c.LandArrangements.Count == 0 && HeldBy(c, 3003, owner),
            "and may go home, handing the ground back");
    }

    static void TroopsWithoutWar()
    {
        var c = Fixture();
        var (tenant, owner) = (c.Nations[0], c.Nations[1]);
        Check(LandArrangementSystem.PresenceClaim(c, tenant, c.States.First(s => s.MapIndex == 3003)) == "compatriots",
            "our people across the border are a reason we can give");
        Check(Order(c, tenant, "send_troops", 3004).Result.Length > 0, "a capital is not taken without a war");
        var sent = Order(c, tenant, "send_troops", 3003);
        Check(sent.Done && HeldBy(c, 3003, tenant) && !c.Wars.Any(w => !w.Ended), "troops in, no war declared");
        Check(c.Armies.First(a => a.NationId == owner.Id).StateMapIndex != 3003, "the owner's garrison is shown the road out");
        Check(CampaignDiplomacy.Get(c, tenant.Id, owner.Id).Reasons.Any(r => r.Id == "troops_on_our_land"), "the owner remembers it");
        Check(Order(c, tenant, "referendum", 3003).Result.Length > 0, "a vote is not held the next morning");
        Day(c, c.Defines.PresenceReferendumDays + 1);
        var vote = Order(c, tenant, "referendum", 3003);
        Check(vote.Done && c.States.First(s => s.MapIndex == 3003).NationMapIndex == tenant.MapIndex && c.LandArrangements.Count == 0,
            "after the vote the region is the holder's in law as well");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "annexed_by_referendum"), "and the chronicle records the count");
    }

    static void Republic()
    {
        var c = Fixture();
        var (tenant, owner) = (c.Nations[0], c.Nations[1]);
        Check(Order(c, tenant, "send_troops", 3003).Done, "troops in, for a republic this time");
        Check(Order(c, tenant, "republic", 3003).Result.Length > 0 && c.LandArrangements.Count == 1, "a republic is not proclaimed the next morning either");
        Day(c, c.Defines.PresenceReferendumDays + 1);
        var nations = c.Nations.Count;
        var proclaimed = Order(c, tenant, "republic", 3003);
        var born = c.Nations.Count > nations ? c.Nations[^1] : null;
        Check(proclaimed.Done && born is not null && c.States.First(s => s.MapIndex == 3003).NationMapIndex == born.MapIndex,
            "the region becomes a state of its own: " + proclaimed.Result);
        Check(born.AnswersTo(tenant.Id) && born.SecededFromId == owner.Id,
            "which answers to the holder, and which its old country counts as a breakaway");
        Check(c.LandArrangements.Count == 0 && c.Chronicle.Entries.Any(e => e.Kind == "republic_proclaimed"), "the presence is over and the world hears of it");
    }

    static void PlayerDesk()
    {
        var c = Fixture();
        var (tenant, owner) = (c.Nations[0], c.Nations[1]);
        c.ConfigureSinglePlayer(owner.Id);
        var asked = Order(c, tenant, "request_lease", 3003);
        Check(!asked.Done && c.LandArrangements.Count == 0 && c.LeaseOffers.Count(o => o.State == "pending" && o.ToId == owner.Id) == 1,
            "a player is asked, not answered for: the offer lies on the desk");
        var offer = c.LeaseOffers[0];
        Check(Order(c, owner, "accept_offer", offer: offer.Id).Done && c.LandArrangements.Count == 1 && HeldBy(c, 3003, tenant),
            "accepting it lets the land");
        var lease = c.LandArrangements[0];
        Day(c, lease.EndDay + 2 - (lease.EndDay + 2) % 7 + 5);
        LandArrangementSystem.Tick(c);
        var renewal = c.LeaseOffers.FirstOrDefault(o => o.Renewal && o.State == "pending");
        Check(renewal is not null, "when the term runs out the player decides whether it goes on");
        Check(Order(c, owner, "refuse_offer", offer: renewal.Id).Done && lease.LeavingOnDay > c.ElapsedDays,
            "refusing it gives the tenant its months to leave");
    }

    static void Persistence()
    {
        var c = Fixture();
        var before = CampaignFingerprint.Of(c);
        Order(c, c.Nations[0], "request_lease", 3003);
        Check(CampaignFingerprint.Of(c) != before, "a lease is saved state the fingerprint sees");
    }
}
