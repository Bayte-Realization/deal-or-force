using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Two numberings, one country: its place in the nation list (Nation.Id, what an army, a war and
/// a battle carry) and its colour on the map (Nation.MapIndex, what ProvinceToNation and
/// ProvinceController hold). They differ for every real country - France is nation 20 and map
/// number 22 - and two places compared one with the other:
///
/// - the militia's daily refill (MilitiaCore) asked whether the province's controller equalled the
///   army's NationId, so a drained militia filled again for nobody - or, by a coincidence of
///   numbers, on the ground of whichever country's map number equalled its list number;
/// - the homeland bonus (CombatCore) asked whether the province's owner equalled the battle's
///   DefenderId, so a defender on its own soil was never paid the HomelandDefenseBonus.
///
/// Pinned on a fresh 2024 world, with the decoy the old comparisons would have taken.
/// </summary>
static class NationNumberingTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("nation numbering: " + note); Console.WriteLine("PASS nation numbering: " + note); }

    static Campaign World(int seed)
    {
        var c = Campaign.CreateBootstrap(seed);
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        return c;
    }

    public static void Run()
    {
        MilitiaRefill();
        Homeland();
    }

    static Army Militia(Campaign c, Nation nation, int province, int personnel)
    {
        var army = new Army
        {
            Id = c.Armies.Count == 0 ? 1 : c.Armies.Max(a => a.Id) + 1,
            NationId = nation.Id, TemplateId = MilitiaCore.TemplateId, Name = "Test militia " + province,
            ProvinceId = province, StateMapIndex = c.ProvinceMap.ProvinceToState[province],
            Personnel = personnel, MaxPersonnel = 10_000, Divisions = 1f, Strength = personnel / 10_000f, Organisation = 1f
        };
        c.Armies.Add(army);
        return army;
    }

    static void MilitiaRefill()
    {
        var c = World(20261119);
        var map = c.ProvinceMap;
        // A country whose list number is somebody else's map number: the old comparison's decoy.
        Nation home = null, decoy = null, enemy = null;
        foreach (var candidate in c.Nations.OrderBy(n => n.Id))
        {
            if (candidate.Id == candidate.MapIndex || candidate.Movement is not null || c.StatesOf(candidate).Length < 2) continue;
            var other = c.NationByMapIndex(candidate.Id);
            if (other is null || other.Id == candidate.Id || c.StatesOf(other).Length == 0) continue;
            var neighbour = c.Nations.FirstOrDefault(n => n.Id != candidate.Id && n.Id != other.Id && n.Movement is null &&
                                                          c.StatesOf(n).Length > 0);
            if (neighbour is null) continue;
            (home, decoy, enemy) = (candidate, other, neighbour);
            break;
        }
        Check(home is not null, "a country whose list number differs from its map number and names another's ground");
        Check(home.Id != home.MapIndex && decoy.MapIndex == home.Id,
            $"{home.Tag} is nation {home.Id} and map number {home.MapIndex}; map number {home.Id} is {decoy.Tag}");

        // The refill runs while somebody is fighting; this war is the home country's own.
        if (!c.Wars.Any(w => !w.Ended && w.Involves(home.Id)))
            c.Wars.Add(new StrategicWar { Id = c.Wars.Count == 0 ? 1 : c.Wars.Max(w => w.Id) + 1,
                AttackerId = enemy.Id, DefenderId = home.Id, StartedDay = 0 });

        int Province(Func<int, bool> where) =>
            Enumerable.Range(1, map.ProvinceCount).First(p => !map.IsWater(p) && where(p));
        var held = Province(p => map.ProvinceToNation[p] == home.MapIndex && c.ProvinceController[p] == home.MapIndex);
        var lost = Province(p => p != held && map.ProvinceToNation[p] == home.MapIndex && c.ProvinceController[p] == home.MapIndex);
        c.SetProvinceController(lost, (ushort)enemy.MapIndex);
        var decoyGround = Province(p => map.ProvinceToNation[p] == decoy.MapIndex && c.ProvinceController[p] == decoy.MapIndex);

        var onHeld = Militia(c, home, held, 2_000);
        var onLost = Militia(c, home, lost, 2_000);
        var onDecoy = Militia(c, home, decoyGround, 2_000);
        var full = Militia(c, home, held, 10_000);

        var share = c.Defines.MilitiaVolunteerSharePerDay;
        var daily = (int)MathF.Max(1f, 10_000 * share);
        MilitiaCore.Tick(c);
        Check(onHeld.Personnel == 2_000 + daily, $"a militia below strength on ground its country holds fills by {daily} a day ({onHeld.Personnel})");
        MilitiaCore.Tick(c);
        Check(onHeld.Personnel == 2_000 + 2 * daily, "and again the next day");
        Check(onLost.Personnel == 2_000, "on ground the enemy holds it does not fill");
        Check(onDecoy.Personnel == 2_000,
            $"on {decoy.Tag}'s ground - the map number equal to {home.Tag}'s list number - it does not fill either");
        Check(full.Personnel == 10_000, "a formation at strength is not filled past it");
        for (var day = 0; day < 60; day++) MilitiaCore.Tick(c);
        Check(onHeld.Personnel == onHeld.MaxPersonnel, "held long enough, it is back at full strength and no further");
    }

    /// <summary>A defender on its own soil is paid the homeland bonus; one fighting elsewhere is not.</summary>
    static void Homeland()
    {
        var c = World(20261120);
        c.IsPaused = false;
        var map = c.ProvinceMap;
        int home = 0, away = 0;
        for (var day = 0; day < 30 && (home == 0 || away == 0); day++)
        {
            c.TickDay();
            foreach (var battle in c.Battles)
            {
                if (battle.Ended || battle.DefenceFactors.Count == 0 || battle.ProvinceId <= 0) continue;
                var defender = c.NationById(battle.DefenderId);
                if (defender is null) continue;
                var owned = map.ProvinceToNation[battle.ProvinceId] == defender.MapIndex;
                var paid = battle.DefenceFactors.Any(f => f.Key == "Homeland");
                if (owned != paid)
                    throw new Exception($"nation numbering: battle {battle.Id} in province {battle.ProvinceId}: defender " +
                                        $"{defender.Tag} owns it={owned}, homeland bonus paid={paid}");
                if (owned) home++; else away++;
            }
        }
        Check(home > 0, $"battle-days fought on the defender's own soil carry the homeland bonus ({home})");
        Console.WriteLine($"PASS nation numbering: battle-days away from the defender's soil seen: {away}, none paid the bonus");
    }
}
