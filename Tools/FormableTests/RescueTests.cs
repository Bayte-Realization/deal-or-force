using System;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// A country losing a war asks somebody in (AiStatecraft.SeekRescue). The plea is the war
/// screen's own: an AI donor answers it at once, a player is never asked from here, and nobody
/// is asked twice inside the cooldown.
/// </summary>
static class RescueTests
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("rescue: " + name);
        Console.WriteLine("PASS rescue: " + name);
    }

    public static void Run()
    {
        AnAllyComesWhenGroundFalls();
        NoTroubleNoPlea();
        ARefusalIsNotRepeated();
        APlayerIsNotAskedFromHere();
        OnlyTheDefenderAsks();
        LapsedClocksDoNotFillTheList();
    }

    // Ukraine (map 2) held provinces 1-3 and lost province 4 to Russia (map 3) in this war.
    static Campaign War(out Nation ukraine, out Nation poland, out Nation russia, bool lost = true,
        bool humanPoland = false, float fondness = 100f)
    {
        var c = new Campaign(41);
        c.FeatureOverrides["diplomacy"] = true;
        ukraine = Nation(0, 2, "UKR");
        poland = Nation(1, 4, "POL");
        poland.IsHuman = humanPoland;
        russia = Nation(2, 3, "RUS");
        c.Nations.Add(ukraine);
        c.Nations.Add(poland);
        c.Nations.Add(russia);
        // Poland reaches Russia by sea: no map in this test, so no land border either.
        c.Fleets.Add(new Fleet { Id = 1, NationId = poland.Id, Hulls = 6 });
        var bond = CampaignDiplomacy.Get(c, poland.Id, ukraine.Id);
        bond.Stance = DiplomaticStance.Allied;
        bond.Opinion = fondness;
        var war = new StrategicWar { Id = 5, AttackerId = russia.Id, DefenderId = ukraine.Id, Name = "Invasion" };
        c.Wars.Add(war);
        var control = new ushort[] { 0, 2, 2, 2, lost ? (ushort)3 : (ushort)2 };
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(c, control);
        if (lost) war.OriginalController[4] = 2;
        // An invader in the field: the ground fell to somebody stronger than Ukraine alone.
        c.Armies.Add(new Army { Id = 9, NationId = russia.Id, Divisions = 6, Strength = 1f, Organisation = 1f, Equipment = 1f });
        if (!lost) c.Armies.Add(new Army { Id = 10, NationId = ukraine.Id, Divisions = 6, Strength = 1f, Organisation = 1f, Equipment = 1f });
        return c;
    }

    static Nation Nation(int id, int map, string tag) => new()
    {
        Id = id, MapIndex = map, Tag = tag, Name = tag, Population = 1_000_000, Gdp = 400, Treasury = 12
    };

    static void AnAllyComesWhenGroundFalls()
    {
        var c = War(out var ukraine, out var poland, out _);
        var asked = AiStatecraft.SeekRescue(c, ukraine, c.Wars[0]);
        Check(asked && c.Wars[0].Involves(poland.Id) && c.InterventionRequests.Count == 1 &&
              c.InterventionRequests[0].State == WarInterventionState.Joined && !c.InterventionRequests[0].HumanConsent,
            "an ally at peace is asked once a quarter of the ground has fallen, and comes");
    }

    static void NoTroubleNoPlea()
    {
        var c = War(out var ukraine, out var poland, out _, lost: false);
        Check(!AiStatecraft.SeekRescue(c, ukraine, c.Wars[0]) && c.InterventionRequests.Count == 0 &&
              !c.Wars[0].Involves(poland.Id),
            "a country holding all its ground against an equal does not ask");
    }

    static void ARefusalIsNotRepeated()
    {
        var c = War(out var ukraine, out var poland, out _, fondness: -100f);
        var first = AiStatecraft.SeekRescue(c, ukraine, c.Wars[0]);
        var refused = c.InterventionRequests.Count == 1 && c.InterventionRequests[0].State == WarInterventionState.Declined;
        var again = AiStatecraft.SeekRescue(c, ukraine, c.Wars[0]);
        Check(first && refused && !again && c.InterventionRequests.Count == 1 && !c.Wars[0].Involves(poland.Id) &&
              CampaignDiplomacy.Get(c, poland.Id, ukraine.Id).Stance == DiplomaticStance.Allied,
            "a refusal leaves the alliance and is not asked again inside the cooldown");
    }

    static void APlayerIsNotAskedFromHere()
    {
        var c = War(out var ukraine, out var poland, out _, humanPoland: true);
        Check(!AiStatecraft.SeekRescue(c, ukraine, c.Wars[0]) && c.InterventionRequests.Count == 0 &&
              !c.Wars[0].Involves(poland.Id),
            "a player ally is left to the inbox review, not pleaded with every round");
    }

    static void LapsedClocksDoNotFillTheList()
    {
        var c = War(out var ukraine, out var poland, out _);
        for (var i = 0; i < WarInterventions.MaxCooldowns; i++)
            c.InterventionCooldowns.Add(new WarInterventionCooldown { RequesterId = 100 + i, DonorId = 1, ReadyOnDay = -1 });
        var asked = AiStatecraft.SeekRescue(c, ukraine, c.Wars[0]);
        Check(asked && c.Wars[0].Involves(poland.Id) && c.InterventionCooldowns.Count == 1,
            "two hundred and fifty-six lapsed cooldowns do not refuse the next plea, and are cleared");
    }

    static void OnlyTheDefenderAsks()
    {
        var c = War(out _, out var poland, out var russia);
        var invader = AiStatecraft.SeekRescue(c, russia, c.Wars[0]);
        var belarus = Nation(3, 5, "BLR");
        c.Nations.Add(belarus);
        c.Wars[0].DefenderAllies.Add(belarus.Id);
        var friend = AiStatecraft.SeekRescue(c, belarus, c.Wars[0]);
        Check(!invader && !friend && c.InterventionRequests.Count == 0 && !c.Wars[0].Involves(poland.Id),
            "neither the invader nor a friend already in the war pleads for more");
    }
}
