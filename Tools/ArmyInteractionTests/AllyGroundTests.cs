using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Land a war cannot hand out: a friend's, and a far shore's.
///
/// Brazil came out of a war fought beside Austria owning Lower Austria. A province retaken from
/// the enemy became the property of whoever's division walked in, and a status-quo peace let
/// every side keep what it held - its allies' land included. And any beach a navy held, on any
/// continent, anchored a treaty that then walked inland from it. These are the rules that stop
/// both: ground freed from the enemy goes back to its owner, a peace hands out only the enemy's
/// ground, and a landing anchors a treaty only a narrow sea from the winner's own coast.
/// </summary>
static class AllyGroundTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL ally ground: " + name);
        Console.WriteLine("PASS ally ground: " + name);
    }

    public static void Run(Campaign c)
    {
        var map = c.ProvinceMap;
        var deu = c.NationByTag("DEU");
        var aut = c.NationByTag("AUT");
        var ita = c.NationByTag("ITA");
        int[] Owned(Nation nation) => Enumerable.Range(1, map.ProvinceCount)
            .Where(p => map.ProvinceToNation[p] == nation.MapIndex && !map.IsWater(p)).ToArray();
        var austrian = Owned(aut);
        var german = Owned(deu);

        // Germany against Austria, and Italy on Austria's side.
        var war = new StrategicWar { Id = 992, AttackerId = deu.Id, DefenderId = aut.Id, StartedDay = c.ElapsedDays - 400 };
        war.DefenderAllies.Add(ita.Id);
        c.Wars.Add(war);

        var overrun = austrian[0];
        c.SetProvinceController(overrun, (ushort)deu.MapIndex);
        Check(Warfare.Liberator(c, overrun, ita) == aut.MapIndex,
            "an Austrian province Italy takes back from Germany is Austria's again, not Italy's");
        Check(Warfare.Liberator(c, overrun, deu) == deu.MapIndex, "a province Germany takes from Austria is Germany's to hold");
        Check(Warfare.Liberator(c, german[0], ita) == ita.MapIndex, "a German province Italy takes is Italy's to hold");
        Check(!PeaceNegotiation.EnemyGround(c, war, overrun, ita.Id) && PeaceNegotiation.EnemyGround(c, war, overrun, deu.Id),
            "Austrian ground is Germany's enemy's ground and not Italy's");
        Check(PeaceShape.SettleProvinces(c, war, austrian.Take(3), ita).Count == 0,
            "a treaty settling what Italy holds hands it none of Austria's land");

        // A status quo: each side keeps what it holds securely - of the enemy's.
        // On Germany's border, and one whose loss leaves the rest of Austria in one piece - a
        // region that cut Austria in two would be given back by the treaty for that reason alone.
        bool LeavesAustriaWhole(int region)
        {
            var rest = austrian.Where(p => map.ProvinceToState[p] != region).ToHashSet();
            var seen = new HashSet<int> { rest.First() };
            var queue = new Queue<int>(seen);
            while (queue.Count > 0)
                foreach (var n in map.Neighbors[queue.Dequeue()])
                    if (rest.Contains(n) && seen.Add(n)) queue.Enqueue(n);
            return seen.Count == rest.Count;
        }
        var border = c.States.Where(s => s.NationMapIndex == aut.MapIndex)
            .First(s => austrian.Any(p => map.ProvinceToState[p] == s.MapIndex &&
                                          map.Neighbors[p].Any(n => map.ProvinceToNation[n] == deu.MapIndex)) &&
                        LeavesAustriaWhole(s.MapIndex));
        var held = austrian.Where(p => map.ProvinceToState[p] == border.MapIndex).ToArray();
        var friend = austrian.First(p => map.ProvinceToState[p] != border.MapIndex);
        foreach (var p in held.Append(overrun).Distinct())
        {
            war.OriginalController[p] = (ushort)aut.MapIndex;
            c.SetProvinceController(p, (ushort)deu.MapIndex);
            c.ProvinceOccupation[p] = 1f;
        }
        // An Austrian province Italian troops came to hold before the rule above - an old save.
        war.OriginalController[friend] = (ushort)aut.MapIndex;
        c.SetProvinceController(friend, (ushort)ita.MapIndex);
        c.ProvinceOccupation[friend] = 1f;
        PeaceNegotiation.Resolve(c, war, aut.Id, PeaceTerms.StatusQuo);
        Check(war.Ended, $"the status quo is signed ({war.LastNegotiation})");
        Check(map.ProvinceToNation[friend] == aut.MapIndex && c.ProvinceController[friend] == aut.MapIndex,
            "the Austrian province Italy held goes back to Austria, owned and held");
        Check(held.All(p => map.ProvinceToNation[p] == deu.MapIndex),
            $"the Austrian region Germany held securely on its own border becomes German ({border.Name})");

        // Across a narrow sea, not an ocean.
        var bra = c.NationByTag("BRA");
        var prt = c.NationByTag("PRT");
        var jpn = c.NationByTag("JPN");
        var kor = c.NationByTag("KOR");
        var coast = SeaLanes.CoastalProvinces(map).ToHashSet();
        var sea = new StrategicWar { Id = 993, AttackerId = bra.Id, DefenderId = prt.Id, StartedDay = c.ElapsedDays - 400 };
        sea.AttackerAllies.Add(jpn.Id);
        sea.DefenderAllies.Add(kor.Id);
        c.Wars.Add(sea);
        int Landing(Nation winner, Nation loser, bool nearest)
        {
            var shores = Owned(winner).Where(coast.Contains).Select(p => map.CenterOf(p)).ToList();
            float Km(int p)
            {
                var (lat, lon) = map.CenterOf(p);
                return shores.Min(s => Distance(lat, lon, s.Latitude, s.Longitude));
            }
            var regions = Owned(loser).Where(coast.Contains).GroupBy(p => (int)map.ProvinceToState[p])
                .Select(g => (Region: g.Key, Km: g.Min(Km))).ToList();
            var pick = nearest ? regions.OrderBy(r => r.Km).First() : regions.OrderByDescending(r => r.Km).First();
            Console.WriteLine($"  {winner.Tag} landing in {c.States.First(s => s.MapIndex == pick.Region).Name}, {pick.Km:0} km from its own coast");
            foreach (var p in Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToState[p] == pick.Region))
            {
                c.SetProvinceController(p, (ushort)winner.MapIndex);
                c.ProvinceOccupation[p] = 1f;
            }
            return pick.Region;
        }
        var lisbon = Landing(bra, prt, nearest: true);
        var busan = Landing(jpn, kor, nearest: true);
        Check(!PeaceShape.CanAnchor(c, bra, lisbon), "a Brazilian landing in Portugal, an ocean away, anchors no treaty");
        Check(PeaceShape.CanAnchor(c, jpn, busan), "a Japanese landing across the Korea Strait does");
        c.Defines.PeaceShapeBridgeheadKm = 0f;
        Check(PeaceShape.CanAnchor(c, bra, lisbon), "and with the reach switched off (0) the ocean is no bar, for a mod that wants it");
        c.Defines.PeaceShapeBridgeheadKm = 600f;

        // A dependency across an ocean stays a dependency; one next door can be taken in.
        c.Wars.Remove(sea);
        var puppet = SubjectCatalog.Of("puppet");
        void Held(Nation overlord, Nation subject)
        {
            Check(SubjectSystem.Subjugate(c, overlord, subject, "puppet", out var refusal), $"{subject.Tag} answers to {overlord.Tag} ({refusal})");
            subject.SubjectSinceDay = c.ElapsedDays - puppet.AnnexAfterDays - 1;
            subject.LibertyDesire = 0f;
        }
        var fra = c.NationByTag("FRA");
        var lux = c.NationByTag("LUX");
        Held(bra, prt);
        Held(fra, lux);
        Check(!SubjectSystem.MayAnnex(c, bra, prt, out var farRefusal) && farRefusal.Contains("too far"),
            $"Brazil cannot absorb a Portugal that answers to it ({farRefusal})");
        Check(SubjectSystem.MayAnnex(c, fra, lux, out var nearRefusal), $"France can absorb a Luxembourg that answers to it {nearRefusal}");
        // A strait from its nearest islands is not a strait from the country: the reach is the capital's.
        var idn = c.NationByTag("IDN");
        var aus = c.NationByTag("AUS");
        var tur = c.NationByTag("TUR");
        var cyp = c.NationByTag("CYP");
        Held(idn, aus);
        Held(tur, cyp);
        Check(!SubjectSystem.MayAnnex(c, idn, aus, out var continent),
            $"Indonesia cannot absorb an Australia that answers to it, for all the Torres Strait ({continent})");
        Check(SubjectSystem.MayAnnex(c, tur, cyp, out var island), $"Turkey can absorb a Cyprus that answers to it {island}");
        // And the reach is a sea crossing, not a line over somebody else's country.
        var cze = c.NationByTag("CZE");
        var mkd = c.NationByTag("MKD");
        Held(ita, cze);
        Held(tur, mkd);
        Check(!SubjectSystem.MayAnnex(c, ita, cze, out var bohemia),
            $"Italy cannot absorb a Czechia that answers to it: no border, no coast to cross to ({bohemia})");
        Check(!SubjectSystem.MayAnnex(c, tur, mkd, out var vardar),
            $"nor Turkey a North Macedonia behind Greece and Bulgaria ({vardar})");
    }

    static float Distance(float lat1, float lon1, float lat2, float lon2)
    {
        var p1 = lat1 * MathF.PI / 180f;
        var p2 = lat2 * MathF.PI / 180f;
        var h = MathF.Pow(MathF.Sin((p2 - p1) / 2), 2) +
                MathF.Cos(p1) * MathF.Cos(p2) * MathF.Pow(MathF.Sin((lon2 - lon1) * MathF.PI / 360f), 2);
        return 2f * 6371f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(h)));
    }
}
