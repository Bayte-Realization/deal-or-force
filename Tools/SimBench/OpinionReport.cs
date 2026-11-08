using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--opinion [days] [--provoke TAG]`: how negative the world's opinion of itself gets, and why.
///
/// Written for a player's complaint: a handful of dishonest acts by China and every capital on
/// earth sat at minus a hundred. The report answers three questions once a year. How the world's
/// relations are spread (mean, the tenth percentile, the share below minus fifty). How the great
/// powers stand with one another, which is where a player looks first. And which remembered
/// reasons carry the weight, summed over every relation in the world - the line that says whether
/// a grievance is between the two countries involved or has leaked out to everybody watching.
///
/// `--provoke TAG` makes the named country do roughly what the player did on day thirty - become
/// notorious as if it had taken land, and test a nuclear weapon - and then prints what a fixed
/// panel of near and far, friendly and hostile governments thinks of it, with each opinion's
/// largest reasons, a month and a year later.
/// </summary>
internal static class OpinionReport
{
    private static readonly string[] Panel = { "GER", "FRA", "GBR", "USA", "BRA", "ARG", "ZAF", "IND", "JPN", "RUS", "PAK", "PRK", "VNM", "AUS" };

    public static int Run(Campaign c, int days, string provoke)
    {
        c.IsPaused = false;
        var actor = string.IsNullOrEmpty(provoke) ? null : c.NationByTag(provoke);
        const int provokeDay = 30;
        World(c);
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (actor is not null && day == provokeDay) Provoke(c, actor);
            if (actor is not null && (day == provokeDay + 30 || day == provokeDay + 365 || day == days)) PanelOf(c, actor);
            if (day % 365 == 0 || day == days) World(c);
        }
        return 0;
    }

    private static void Provoke(Campaign c, Nation actor)
    {
        actor.Notoriety = MathF.Min(100f, actor.Notoriety + 45f);
        actor.PoliticalPoints = MathF.Max(actor.PoliticalPoints, 200f);
        actor.LastNuclearTestDay = -99999;
        var tested = NuclearPosture.IsHolder(c, actor) && NuclearPosture.Apply(c, actor, "test");
        Console.WriteLine($"PROVOKE day {c.ElapsedDays}: {actor.Tag} notoriety {actor.Notoriety:0}, nuclear test {(tested ? "made" : "refused: " + NuclearPosture.Refusal(c, actor, "test"))}");
    }

    private static void PanelOf(Campaign c, Nation actor)
    {
        Console.WriteLine($"PANEL day {c.ElapsedDays}: opinions of {actor.Tag}");
        foreach (var tag in Panel)
        {
            var other = c.NationByTag(tag);
            if (other is null || other.Id == actor.Id || other.Population <= 0) continue;
            var relation = CampaignDiplomacy.Peek(c, actor.Id, other.Id);
            if (relation is null) { Console.WriteLine($"  {tag}: no relation"); continue; }
            OpinionSystem.Explain(c, relation);
            var parts = relation.OpinionDrivers.Entries.OrderBy(e => -MathF.Abs(e.Value)).Take(5)
                .Select(e => $"{e.Source} {e.Value:+0.0;-0.0}");
            Console.WriteLine($"  {tag} {relation.Opinion,5:0} [{relation.Stance}] {string.Join(" | ", parts)}");
        }
    }

    private static void World(Campaign c)
    {
        var alive = c.Nations.Where(n => n.Population > 0 && n.Movement is null).Select(n => n.Id).ToHashSet();
        var relations = c.Relations.Values.Where(r => alive.Contains(r.A) && alive.Contains(r.B)).ToList();
        if (relations.Count == 0) return;
        var opinions = relations.Select(r => r.Opinion).OrderBy(v => v).ToArray();
        var powers = AllianceSystem.GreatPowers(c);
        var between = relations.Where(r => powers.Contains(r.A) && powers.Contains(r.B)).ToList();
        Console.WriteLine($"OPINION day {c.ElapsedDays}: relations={relations.Count} mean={opinions.Average():0.0} p10={opinions[opinions.Length / 10]:0} " +
                          $"below-50={opinions.Count(v => v <= -50f) * 100f / opinions.Length:0.0}% below-25={opinions.Count(v => v <= -25f) * 100f / opinions.Length:0.0}% " +
                          $"above+25={opinions.Count(v => v >= 25f) * 100f / opinions.Length:0.0}%");
        int Count(string kind) => c.Chronicle.Entries.Count(e => e.Kind == kind);
        Console.WriteLine($"  WARS total={c.Wars.Count} live={c.Wars.Count(w => !w.Ended)} interstate={c.Wars.Count(w => !w.CivilConflict)} " +
                          $"apologies={Count("apology")} aid={Count("humanitarian_aid")} missions={Count("diplomatic_mission_started")} embargoes={Count("embargo_started")}");
        if (between.Count > 0)
            Console.WriteLine($"  POWERS pairs={between.Count} mean={between.Average(r => r.Opinion):0.0} min={between.Min(r => r.Opinion):0} " +
                              $"below-50={between.Count(r => r.Opinion <= -50f)} below-25={between.Count(r => r.Opinion <= -25f)}");

        // Every remembered reason, weighed the way the opinion panel weighs it, added up over the world.
        var sum = new Dictionary<string, (float Weight, int Count)>();
        var third = 0f; var direct = 0f;
        foreach (var relation in relations)
            foreach (var reason in relation.Reasons)
            {
                var weight = OpinionSystem.Weight(c, reason);
                if (MathF.Abs(weight) < 0.05f) continue;
                var held = sum.GetValueOrDefault(reason.Id);
                sum[reason.Id] = (held.Weight + weight, held.Count + 1);
                if (weight < 0f)
                {
                    if (OpinionCatalog.Find(reason.Id) is { ThirdParty: true }) third += weight; else direct += weight;
                }
            }
        Console.WriteLine($"  NEGATIVE total direct={direct:0} third-party={third:0}");
        foreach (var (id, (weight, count)) in sum.Where(p => p.Value.Weight < 0f).OrderBy(p => p.Value.Weight).Take(14))
            Console.WriteLine($"  REASON {id,-32} total={weight,8:0} held={count,5} mean={weight / count,6:0.0}");
        foreach (var (id, (weight, count)) in sum.Where(p => p.Value.Weight > 0f).OrderByDescending(p => p.Value.Weight).Take(6))
            Console.WriteLine($"  GOOD   {id,-32} total={weight,8:0} held={count,5} mean={weight / count,6:0.0}");
    }
}
