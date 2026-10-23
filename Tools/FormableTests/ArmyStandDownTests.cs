using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The general staff stands a formation down only when a peace has left a surplus the
/// treasury cannot pay, and never one that is holding a war. A player who has not handed
/// the army over is left alone. The same rule is what every other government uses.
/// </summary>
static class ArmyStandDownTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("army stand-down: " + label);
        System.Console.WriteLine("PASS army stand-down: " + label);
    }

    public static void Run()
    {
        PlayerKeepsWhatTheyDidNotHandOver();
        WarKeepsTheFormationOnIt();
        BrokePeaceStandsDownASurplusAndSaysWhy();
    }

    static Army Unit(Campaign c, int nationId, int id, float strength, int province = 0)
    {
        var army = new Army
        {
            Id = id, NationId = nationId, Name = "Formation " + id, Divisions = 1f,
            Strength = strength, Organisation = 1f, SupplyLevel = 1f, Equipment = 1f,
            PlanActive = true, OrderKind = ArmyOrder.Manual, AssignedWarId = -1,
            ProvinceId = province, Personnel = 15_000, MaxPersonnel = 15_000
        };
        c.Armies.Add(army);
        return army;
    }

    static void Ready(Nation nation)
    {
        nation.FoundingDivisions = 1f;
        nation.EstablishedDivisions = 1f;
        nation.MilitarySpending = 0f;
        nation.ManpowerReserve = 0f;
        nation.Treasury = 0f;
    }

    static void PlayerKeepsWhatTheyDidNotHandOver()
    {
        var c = new Campaign(20261023);
        var player = new Nation { Id = 0, MapIndex = 1, Tag = "PLY", Name = "Player", IsHuman = true, Gdp = 10, Population = 1_000_000 };
        c.Nations.Add(player);
        Delegation.Set(player, Delegation.Army, delegated: false);
        Ready(player);
        player.Treasury = 0f;
        Unit(c, player.Id, 1, 1f);
        Unit(c, player.Id, 2, 0.4f);
        Attrition.Tick(c);
        Check(!Delegation.Holds(player, Delegation.Army), "the army was not handed to the staff");
        Check(c.Armies.Count(a => a.NationId == player.Id) == 2, "a player who kept the army does not lose a formation");
        Check(!c.Chronicle.Entries.Any(e => e.Kind == "army_stood_down"), "and the staff writes no stand-down");
    }

    static void WarKeepsTheFormationOnIt()
    {
        var c = new Campaign(20261023);
        var nation = new Nation { Id = 0, MapIndex = 1, Tag = "WAR", Name = "At war", Gdp = 10, Population = 1_000_000 };
        var other = new Nation { Id = 1, MapIndex = 2, Tag = "FOE", Name = "Foe", Gdp = 10, Population = 1_000_000 };
        c.Nations.Add(nation);
        c.Nations.Add(other);
        Ready(nation);
        var war = new StrategicWar { Id = 7, AttackerId = nation.Id, DefenderId = other.Id };
        war.AttackerFront.Add(4);
        c.Wars.Add(war);
        var onTheWar = Unit(c, nation.Id, 1, 0.3f, province: 4);
        onTheWar.AssignedWarId = war.Id;
        var onTheFront = Unit(c, nation.Id, 2, 0.3f, province: 4);
        onTheFront.AssignedWarId = -1;
        Unit(c, nation.Id, 3, 1f);
        Attrition.Tick(c);
        Check(c.Armies.Any(a => a.Id == onTheWar.Id), "a formation assigned to a war that is still going is not disbanded");
        Check(c.Armies.Any(a => a.Id == onTheFront.Id), "a formation standing on that war's front is not disbanded");
        Check(c.Armies.Count(a => a.NationId == nation.Id) == 3, "a country at war does not stand its army down");
    }

    static void BrokePeaceStandsDownASurplusAndSaysWhy()
    {
        var c = new Campaign(20261023);
        var nation = new Nation { Id = 0, MapIndex = 1, Tag = "PAX", Name = "At peace", Gdp = 10, Population = 1_000_000 };
        c.Nations.Add(nation);
        Ready(nation);
        var field = Unit(c, nation.Id, 1, 1f);
        var surplus = Unit(c, nation.Id, 2, 0.4f);
        Attrition.Tick(c);
        Check(!c.Armies.Any(a => a.Id == surplus.Id), "an unpaid surplus at peace is stood down");
        Check(c.Armies.Any(a => a.Id == field.Id), "the field formation is not deleted with it");
        var note = c.Chronicle.Entries.FirstOrDefault(e => e.Kind == "army_stood_down");
        Check(note is not null && note.Place == "unpaid_surplus" && note.Subject == surplus.Name,
            "the stand-down names the unpaid surplus, it is not a silent delete");

        var rich = new Campaign(20261023);
        var payer = new Nation { Id = 0, MapIndex = 1, Tag = "RIC", Name = "Can pay", Gdp = 10, Treasury = 50f, Population = 1_000_000 };
        rich.Nations.Add(payer);
        Ready(payer);
        payer.Treasury = 50f;
        Unit(rich, payer.Id, 1, 1f);
        Unit(rich, payer.Id, 2, 0.4f);
        Attrition.Tick(rich);
        Check(rich.Armies.Count == 2, "a surplus the treasury can pay is not stood down");
    }
}
