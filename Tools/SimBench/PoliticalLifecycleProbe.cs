using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Opt-in bounded observation of real world politics, not another simulation path.</summary>
internal static class PoliticalLifecycleProbe
{
    public static int Run(Campaign world, int days)
    {
        var chairs = new Dictionary<int, (string State, string Government)>();
        var ministers = new Dictionary<(int, string), (string Name, int Born)>();
        var stateChanges = 0; var governmentChanges = 0; var ministerChanges = 0;
        var electionResults = 0; var electionTransfers = 0; var electionRefusals = 0; var administrativeGrants = 0;
        long lastFact = 0;
        var fail = 0; var clock = Stopwatch.StartNew();
        world.IsPaused = false;
        for (var day = 0; day < days; day++)
        {
            world.TickDay();
            foreach (var fact in world.Chronicle.Entries.Where(e => e.Sequence > lastFact))
            {
                if (fact.Kind is "election_renewed" or "election_lost") electionResults++;
                if (fact.Kind == "election_transfer") electionTransfers++;
                if (fact.Kind == "election_refused") electionRefusals++;
                if (fact.Kind == "subject_land_grant") administrativeGrants++;
                lastFact = Math.Max(lastFact, fact.Sequence);
            }
            foreach (var nation in world.Nations)
            {
                if (nation.Population <= 0 || nation.Movement is not null || world.Fallen.Contains(nation.Id)) continue;
                var head = Rulers.HeadOfState(world, nation);
                var government = Rulers.HeadOfGovernment(world, nation);
                if (head.Age > world.Defines.RulerMaxAge || government.Age > world.Defines.RulerMaxAge)
                { Console.WriteLine($"FAIL personnel age {nation.Tag} day={world.ElapsedDays} {head.Age}/{government.Age}"); return 1; }
                if (chairs.TryGetValue(nation.Id, out var old))
                {
                    if (old.State != head.Name) stateChanges++;
                    if (old.Government != government.Name) governmentChanges++;
                }
                chairs[nation.Id] = (head.Name, government.Name);
                foreach (var (portfolio, person) in nation.CabinetPeople)
                {
                    var key = (nation.Id, portfolio);
                    if (ministers.TryGetValue(key, out var previous))
                    {
                        if (previous.Name != person.Name) ministerChanges++;
                        else if (previous.Born != person.BirthYear)
                        { Console.WriteLine($"FAIL minister rejuvenated {nation.Tag}/{portfolio}"); return 1; }
                    }
                    else ministerChanges++;
                    ministers[key] = (person.Name, person.BirthYear);
                }
            }
            if (day % 365 == 0)
            {
                var before = CampaignFingerprint.Of(world); var rng = world.Random.State;
                foreach (var nation in world.Nations) _ = Rulers.CandidatePool(world, nation);
                if (before != CampaignFingerprint.Of(world) || rng != world.Random.State)
                { Console.WriteLine("FAIL candidate inspection mutated world"); return 1; }
                Console.WriteLine($"PERSONNEL_YEAR day={world.ElapsedDays} states={stateChanges} governments={governmentChanges} appointments={ministerChanges}");
            }
        }
        if (days >= 3650 && (stateChanges == 0 || ministerChanges == 0)) fail++;
        Console.WriteLine($"PERSONNEL_LIFECYCLE seed={world.Seed} days={days} states={stateChanges} governments={governmentChanges} appointments={ministerChanges} " +
            $"elections={electionResults} transfers={electionTransfers} refusals={electionRefusals} grants={administrativeGrants} " +
            $"fingerprint={CampaignFingerprint.Of(world):X16} seconds={clock.Elapsed.TotalSeconds:0.0} failures={fail}");
        return fail == 0 ? 0 : 1;
    }
}
