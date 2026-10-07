using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--factions` lists the de facto powers the world opened with - what ground each was cut
/// out of, how many people and formations it has, how it stands with its parent - then plays
/// the days and lists them again: still there, larger, smaller, written back in, at war with
/// whom. The question is whether the frozen conflicts and the civil wars of 2024 are on the
/// map at all, and whether they move once the clock runs.
/// </summary>
internal static class FactionReport
{
    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        var factions = c.Nations.Where(n => n.FactionId.Length > 0).ToArray();
        Console.WriteLine($"FACTIONS {factions.Length} of {Factions.All.Count} defined");
        foreach (var definition in Factions.All)
            if (factions.All(n => n.FactionId != definition.Id))
                Console.WriteLine($"  MISSING {definition.Tag} ({definition.Id}) parent={definition.Parent}");
        foreach (var faction in factions) Line(c, faction, "START");
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 365 == 0)
            {
                Console.WriteLine($"-- year {day / 365}");
                foreach (var faction in factions) Line(c, faction, "   ");
            }
        }
        Console.WriteLine($"-- end, day {c.ElapsedDays}");
        foreach (var faction in factions) Line(c, faction, "END");
        foreach (var war in c.Wars.Where(w => factions.Any(f => w.Involves(f.Id))))
        {
            var a = c.NationById(war.AttackerId);
            var d = c.NationById(war.DefenderId);
            var backers = ProxyWars.For(c, war)
                .Select(s => $"{c.NationById(s.SponsorId)?.Tag}->{c.NationById(s.ClientId)?.Tag}:{s.Level}");
            Console.WriteLine($"WAR '{war.Name}' {a?.Tag}-{d?.Tag} day {war.StartedDay}" +
                              (war.Ended ? $" ended {war.EndedDay} winner={(war.WinnerId >= 0 ? c.NationById(war.WinnerId)?.Tag : "-")} :: {war.PeaceSummary}"
                                         : $" fighting score={war.WarScore:0.0}") +
                              $" backers=[{string.Join(" ", backers)}]");
        }
        return 0;
    }

    /// <summary>
    /// `--provinces TAG`: every land province of a country with its middle, its region and its
    /// owner - what an author drawing a faction's area rings needs to see, because the province
    /// map is coarse in exactly the deserts and mountains where these wars are fought.
    /// </summary>
    public static int Provinces(Campaign c, string tag)
    {
        var map = c.ProvinceMap;
        foreach (var t in tag.Split(','))
        {
            var nation = c.NationByTag(t);
            if (nation is null) { Console.WriteLine($"no country '{t}'"); continue; }
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                if (map.IsWater(province)) continue;
                var owner = map.ProvinceToNation[province];
                if (owner != nation.MapIndex) continue;
                var centre = map.CenterOf(province);
                var state = c.States.Find(s => s.MapIndex == map.ProvinceToState[province]);
                Console.WriteLine($"P {t} {province} {centre.Longitude:0.00} {centre.Latitude:0.00} {state?.Id} {state?.Name}");
            }
        }
        return 0;
    }

    /// <summary>
    /// `--judge A-B,C-D`: every ninety days, what A makes of a war on B, item by item - the
    /// value, the price and the reason it could give (AiStatecraft.ExplainWar). For finding out
    /// why a government attacked somebody, which the war ledger can only report.
    /// </summary>
    public static int Judge(Campaign c, int days, string pairs)
    {
        c.IsPaused = false;
        var list = pairs.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('-'))
            .Where(pair => pair.Length == 2)
            .Select(pair => (c.NationByTag(pair[0]), c.NationByTag(pair[1])))
            .Where(pair => pair.Item1 is not null && pair.Item2 is not null)
            .ToArray();
        void Print()
        {
            foreach (var (a, b) in list)
                Console.WriteLine($"JUDGE day {c.ElapsedDays} {AiStatecraft.ExplainWar(c, a, b)}" +
                                  (CampaignDiplomacy.AtWar(c, a.Id, b.Id) ? " AT WAR" : ""));
        }
        Print();
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (day % 90 == 0) Print();
        }
        return 0;
    }

    private static void Line(Campaign c, Nation faction, string label)
    {
        var map = c.ProvinceMap;
        int owned = 0, heldByOthers = 0, holdsOfOthers = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.ProvinceToNation[province] == faction.MapIndex)
            {
                owned++;
                if (c.ProvinceController[province] != faction.MapIndex) heldByOthers++;
            }
            else if (c.ProvinceController[province] == faction.MapIndex) holdsOfOthers++;
        }
        var parent = c.NationById(faction.SecededFromId);
        var relation = parent is null ? null : CampaignDiplomacy.Peek(c, parent.Id, faction.Id);
        var armies = c.Armies.Where(a => a.NationId == faction.Id).ToArray();
        var militia = armies.Count(a => a.TemplateId == MilitiaCore.TemplateId);
        var parentArmies = parent is null ? 0 : c.Armies.Count(a => a.NationId == parent.Id);
        var wars = c.Wars.Where(w => !w.Ended && w.Involves(faction.Id))
            .Select(w => string.Join("/", w.EnemiesOf(faction.Id).Select(id => c.NationById(id)?.Tag)));
        var regions = c.StatesOf(faction);
        var names = string.Join(", ", regions.OrderByDescending(s => s.Population).Take(4).Select(s => s.Name));
        var overlord = faction.IsSubject ? $" subject-of={c.NationById(faction.OverlordId)?.Tag}" : "";
        Console.WriteLine($"{label} {faction.Tag,-4} {faction.Name,-38} parent={parent?.Tag} {relation?.Stance} " +
                          $"provinces={owned} (lost {heldByOthers}, holds {holdsOfOthers} more) regions={regions.Length} " +
                          $"people={faction.Population:N0} gdp={faction.Gdp:0.0} div={armies.Length} ({militia} militia) vs {parentArmies} " +
                          $"stab={faction.Stability:0.00}{overlord} wars=[{string.Join(" ", wars)}] {(c.Fallen.Contains(faction.Id) ? "FALLEN" : "")}");
        Console.WriteLine($"      {names}");
    }
}
