using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The two things this pass changed about the world of 2024: the wars that were not between
/// states are fought rather than truced, and a country's rulers are drawn from a pool big
/// enough that a name means something.
///
/// Yemen is the case for the first. On 1 January 2024 the Houthis held Sanaa and the north,
/// the government held Marib and the south's coast, and both were shooting at each other;
/// so was the south against the government, in a war the United Arab Emirates was paying
/// for. A truce is a war that has stopped, and none of those had.
///
/// The pools are the case for the second. Forty names in a pool is a world where the same
/// four people keep leading Japan; the check below asks how many different people a country
/// can produce, and insists the number is large enough that a player will meet somebody new.
/// </summary>
static class HotWarAndNamePoolTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("hot wars and name pools: " + label);
        Console.WriteLine("PASS hot wars and name pools: " + label);
    }

    public static void Run()
    {
        Rulers.ResetCatalog();
        Factions.ResetCatalog();
        DemographyCatalog.ResetCatalog();
        GovernmentCatalog.ResetCatalog();
        IdeologyCatalog.ResetCatalog();
        Loc.ResetCatalog();

        var campaign = Campaign.CreateBootstrap(20261019);
        // Who neighbours whom comes off the province graph, and a faction is cut out of the
        // ground it holds: without the map attached there is no ground, and no faction.
        MapRasterCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var mapError),
            "the world map loads: " + mapError);
        campaign.AttachProvinceTopology(topology);
        var yem = campaign.NationByTag("YEM");
        var hth = campaign.NationByTag("HTH");
        var stc = campaign.NationByTag("STC");
        Check(yem is not null && hth is not null && stc is not null, "Yemen and its two factions are founded");

        // ---------------------------------------------------------------- the wars ----------
        var civilWar = campaign.Wars.FirstOrDefault(w =>
            !w.Ended && w.Involves(yem.Id) && w.Involves(hth.Id));
        Check(civilWar is not null, "the Houthi civil war is a live war, not a truce");
        Check(civilWar?.Name == "Yemeni Civil War", "and it is called what the world calls it: " + civilWar?.Name);

        var southernWar = campaign.Wars.FirstOrDefault(w =>
            !w.Ended && w.Involves(yem.Id) && w.Involves(stc.Id));
        Check(southernWar is not null, "the south is at war with the government too: " + southernWar?.Name);

        // A war is only hot if there is somebody to fight it. Each side must hold men.
        Check(civilWar?.AttackerId == yem.Id && civilWar?.DefenderId == hth.Id,
            "the government attacks the Houthis, the way a civil war reads");
        var yemMen = campaign.Armies.Count(a => a.NationId == yem.Id);
        var hthMen = campaign.Armies.Count(a => a.NationId == hth.Id);
        Check(yemMen > 0 && hthMen > 0,
            "both sides have standing armies: " + yemMen + " government, " + hthMen + " Houthi");
        Check(campaign.Armies.Count(a => a.NationId == stc.Id) > 0, "and so does the south");

        // And the truce that used to open the world is gone: no truce clock on either side.
        Check(campaign.Relations.Values.All(d =>
                !((d.A == yem.Id && d.B == hth.Id) || (d.A == hth.Id && d.B == yem.Id)) ||
                d.Stance != DiplomaticStance.Truce),
            "no truce stands between Yemen and the Houthis");
        Check(campaign.Relations.Values.All(d =>
                !((d.A == yem.Id && d.B == stc.Id) || (d.A == stc.Id && d.B == yem.Id)) ||
                d.Stance != DiplomaticStance.Truce),
            "nor between Yemen and the south");

        // Ground actually changes hands, which is what makes a civil war look like one.
        var houthiGround = campaign.ProvinceController.Count(c => c == (ushort)hth.MapIndex);
        var yemGround = campaign.ProvinceController.Count(c => c == (ushort)yem.MapIndex);
        Check(houthiGround > 0 && yemGround > 0,
            "each side holds ground of its own: " + houthiGround + " Houthi, " + yemGround + " government");
        Check(hth.CapitalLongitude > 42 && hth.CapitalLongitude < 46, "the Houthis sit in Sanaa");
        Check(yem.CapitalLongitude > 44 && yem.CapitalLongitude < 46, "the government sits at Marib");

        // The sponsorships that keep the war going.
        Check(campaign.Sponsorships.Count > 0,
            "somebody abroad is paying for it (" + campaign.Sponsorships.Count + " sponsorships)");

        // ------------------------------------------------------------ the name pools -------
        var pools = Rulers.NamePools;
        Check(pools.Count >= 18, "there are at least eighteen cultural name pools (got " + pools.Count + ")");

        // A pool nobody reads is a file. Every pool must carry enough of a kind that a name
        // means something, and the ones this pass rewrote must carry a lot more than that.
        var expanded = new[] { "russian_east_slavic", "ukrainian", "anglo", "french", "germanic", "arabic",
            "persian_kurdish", "turkic", "chinese", "japanese", "korean", "south_asian",
            "southeast_asian", "west_african", "east_horn_african", "default", "hispanic", "baltic", "south_slavic" };
        foreach (var key in expanded)
        {
            Check(pools.TryGetValue(key, out var pool) && pool.Given.Length >= 55 && pool.Family.Length >= 50,
                key + " carries " + (pools.TryGetValue(key, out var p) ? p.Given.Length + "/" + p.Family.Length : "nothing"));
        }
        Check(pools.Values.All(pool => pool.Given.Length >= 10 && pool.Family.Length >= 10),
            "no pool in the world is thin");

        // A pool is only worth its size if the names in it are actually different.
        Check(pools.Values.All(pool => pool.Given.Distinct(StringComparer.Ordinal).Count() == pool.Given.Length),
            "no given name repeats inside a pool");
        Check(pools.Values.All(pool => pool.Family.Distinct(StringComparer.Ordinal).Count() == pool.Family.Length),
            "no family name repeats inside a pool");

        // And the world still reads them: a country's ruler comes from its own people, and
        // two generations of them are two different people.
        foreach (var tag in new[] { "YEM", "JPN", "RUS", "DEU", "USA", "CHN", "IND", "NGA", "ETH", "TUR", "IRN", "ESP" })
        {
            var nation = campaign.NationByTag(tag);
            if (nation is null) continue;
            var names = Enumerable.Range(0, 24)
                .Select(g => Rulers.GenerateName(nation, g, isState: false))
                .ToList();
            var distinct = names.Distinct(StringComparer.Ordinal).Count();
            Check(distinct >= 14,
                tag + " can produce " + distinct + " different politicians out of 24 rolls");
            Check(names.All(n => !string.IsNullOrWhiteSpace(n)), tag + " never produces nobody");
        }

        // The pools must not have quietly replaced the base ones with a smaller set.
        var yemen = campaign.NationByTag("YEM");
        var poolKey = Rulers.PoolFor(yemen);
        Check(pools.TryGetValue(poolKey, out var arabic) && arabic.Given.Length >= 40,
            "Yemen's own pool carries " + arabic.Given.Length + " given names");
        Check(arabic.Family.Any(f => f.Contains('-')),
            "and its families are the ones a Yemeni would have: " +
            string.Join(", ", arabic.Family.Where(f => f.Contains('-')).Take(3)));
    }
}
