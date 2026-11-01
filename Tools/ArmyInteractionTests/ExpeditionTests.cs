using System;
using System.Linq;
using DealOrForce.Simulation;

static class ExpeditionTests
{
    static int failures;
    static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "expeditions: " + label);
        if (!ok) failures++;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);

    /// <summary>Volunteers change flags for a war and come home after it; an airlift goes where no march goes.</summary>
    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(947986505);
        c.AttachProvinceTopology(map);
        c.Defines.AiVolunteerChance = 0f;
        c.Wars.Clear(); c.MarkWarsChanged();
        var pk = c.NationByTag("PAK"); var ind = c.NationByTag("IND"); var rus = c.NationByTag("RUS"); var fr = c.NationByTag("FRA");
        foreach (var n in new[] { pk, ind, rus, fr }) n.IsHuman = false;
        SetDay(c, 300);
        rus.PoliticalPoints = 100f; rus.Treasury = 500f;
        var war = new StrategicWar { Id = 91003, AttackerId = pk.Id, DefenderId = ind.Id, StartedDay = 250 };
        c.Wars.Add(war); c.MarkWarsChanged();

        Check(Expeditions.VolunteerRefusal(c, rus, fr).Length > 0, "nobody sends volunteers to a country at peace");
        Check(Expeditions.VolunteerRefusal(c, ind, pk).Length > 0, "nor to its own enemy");
        Check(Expeditions.VolunteerRefusal(c, rus, ind).Length == 0, "a country at peace may send them to one at war");

        var army = c.Armies.Where(a => a.NationId == rus.Id && !a.InBattle && a.EmbarkedFleetId < 0)
            .OrderBy(a => a.Divisions).First();
        var tensionBefore = CampaignDiplomacy.Get(c, pk.Id, rus.Id).Tension;
        var order = new ExpeditionCommand { IssuerId = rus.Id, ArmyId = army.Id, Action = Expeditions.Volunteer, TargetId = ind.Id };
        order.Apply(c);
        Check(order.Done && army.NationId == ind.Id && army.LentById == rus.Id, "the formation serves under the host's flag");
        Check(c.ProvinceMap.ProvinceToNation[army.ProvinceId] == ind.MapIndex && army.ReadyDay > 300, "it lands in the host's country, not ready at once");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "volunteers_sent" && e.ActorId == rus.Id && e.TargetId == ind.Id), "the world hears of it");
        Check(CampaignDiplomacy.Get(c, pk.Id, rus.Id).Tension > tensionBefore, "and the side it fights against takes note");
        Check(!c.Wars.Any(w => w.Involves(rus.Id)), "the sender stays at peace");

        // The war ends: they come home.
        war.Ended = true; c.Wars.Remove(war); c.MarkWarsChanged();
        SetDay(c, 301);
        Expeditions.Tick(c);
        Check(army.NationId == rus.Id && army.LentById < 0 && c.ProvinceMap.ProvinceToNation[army.ProvinceId] == rus.MapIndex,
            "with the war over the volunteers go home");

        // An airlift: from home to a far corner of the country, out of action meanwhile, and paid for.
        var far = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => map.ProvinceToNation[p] == rus.MapIndex && !map.IsWater(p))
            .OrderByDescending(p => SupportCore.DistanceKm(c, army.ProvinceId, p)).First();
        army.ReadyDay = 0;
        var money = rus.Treasury;
        var fly = new ExpeditionCommand { IssuerId = rus.Id, ArmyId = army.Id, Action = Expeditions.Airlift, TargetId = far };
        fly.Apply(c);
        Check(fly.Done && army.ProvinceId == far && rus.Treasury < money && army.ReadyDay > 301, "an airlift lands where it was sent, at a price and a delay");
        var foreignGround = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToNation[p] == fr.MapIndex && !map.IsWater(p));
        Check(Expeditions.AirliftRefusal(c, rus, army, foreignGround).Length > 0, "nobody is flown onto ground it has no right to");
        if (failures > 0) throw new Exception($"{failures} expedition checks failed");
    }
}
