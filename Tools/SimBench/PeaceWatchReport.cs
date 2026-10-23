using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Watches one war for the two faults a campaign shows and a unit test does not: ground taken
/// in the field that no treaty will ever keep, and formations that leave the order of battle
/// with nobody having fought them.
///
/// `--peace-watch RUS UKR 500` plays the war forward and prints, every thirty days, how much
/// of the defender's ground the attacker stands on, how much of it is SECURED (the share a
/// treaty is allowed to keep), what the war score says, and how many formations each side still
/// has. A formation that disappears without having been in a battle or in a pocket is counted
/// as `vanished` and attributed to its country: that is the shadow of a general staff sending
/// a division home, and it used to be invisible unless somebody watched a ledger by hand.
/// </summary>
internal static class PeaceWatchReport
{
    private readonly record struct Footprint(bool Battle, bool Pocket, int Nation);

    public static int Run(Campaign c, string attackerTag, string defenderTag, int days)
    {
        var attacker = c.NationByTag(attackerTag);
        var defender = c.NationByTag(defenderTag);
        var map = c.ProvinceMap;
        if (attacker is null || defender is null || map is null)
        {
            Console.WriteLine($"PEACE_WATCH no such country: {attackerTag} {defenderTag}");
            return 2;
        }

        var war = c.Wars.Where(w => w.Opposes(attacker.Id, defender.Id))
            .OrderByDescending(w => w.StartedDay).FirstOrDefault();
        if (war is null)
        {
            Console.WriteLine($"PEACE_WATCH no war between {attackerTag} and {defenderTag}");
            return 2;
        }

        var ground = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => map.ProvinceToNation[p] == defender.MapIndex && !map.IsWater(p))
            .ToArray();
        var threshold = c.Defines.OccupationSecureThreshold;
        var footprint = new Dictionary<int, Footprint>();
        var vanished = new Dictionary<string, int>(StringComparer.Ordinal);
        var fought = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastNegotiation = war.LastNegotiation;
        var lastChronicle = 0L;
        var traceChronicle = Environment.GetEnvironmentVariable("DOF_TRACE_CHRONICLE") == "1";
        c.IsPaused = false;

        Console.WriteLine($"PEACE_WATCH war={war.Id} attacker={attacker.Tag} defender={defender.Tag} " +
                          $"ground={ground.Length} threshold={threshold:0.00} forcedEnd={c.Defines.PeaceForcedEndDays} " +
                          $"unlock={war.AiNegotiationUnlockDay} goal={war.ClaimedStateMapIndex} reason={war.Justification}");

        for (var step = 0; step < Math.Clamp(days, 1, 3650); step++)
        {
            c.TickDay();
            c.FinishStep();

            var present = new Dictionary<int, Footprint>();
            foreach (var army in c.Armies)
            {
                if (!war.Involves(army.NationId)) continue;
                present[army.Id] = new Footprint(army.InBattle, army.CutOff, army.NationId);
            }

            foreach (var (id, was) in footprint)
            {
                if (present.ContainsKey(id)) continue;
                var tag = c.NationById(was.Nation)?.Tag ?? "?";
                if (was.Battle || was.Pocket) fought[tag] = fought.GetValueOrDefault(tag) + 1;
                else vanished[tag] = vanished.GetValueOrDefault(tag) + 1;
            }
            footprint.Clear();
            foreach (var (id, now) in present) footprint[id] = now;

            if (traceChronicle)
            {
                foreach (var fact in c.Chronicle.Entries)
                {
                    if (fact.Sequence <= lastChronicle) continue;
                    lastChronicle = fact.Sequence;
                    if (!(fact.Kind.Contains("war") || fact.Kind.Contains("peace") ||
                          fact.Kind.Contains("state") || fact.Kind.Contains("annex") ||
                          fact.Kind.Contains("restor") || fact.Kind.Contains("secess") ||
                          fact.Kind.Contains("independ") || fact.Kind.Contains("union") ||
                          fact.Kind.Contains("capitul") || fact.Kind.Contains("collapse"))) continue;
                    Console.WriteLine($"PEACE_EVENT day={fact.Day} kind={fact.Kind} " +
                                      $"actor={fact.ActorName} target={fact.TargetName} " +
                                      $"subject={fact.Subject} place={fact.Place}");
                }
            }

            if (war.LastNegotiation != lastNegotiation)
            {
                Console.WriteLine($"PEACE_TALK day={c.ElapsedDays} score={war.WarScore:0.0} " +
                                  $"pending={c.NationById(war.PendingOfferFromId)?.Tag ?? "-"} " +
                                  $"terms={(war.PendingOfferFromId >= 0 ? war.PendingOfferTerms.ToString() : "-")} " +
                                  $"said={war.LastNegotiation}");
                lastNegotiation = war.LastNegotiation;
            }

            if (c.ElapsedDays % 30 != 0) continue;
            var controllers = c.ProvinceController;
            var occupation = c.ProvinceOccupation;
            var held = ground.Count(p => controllers[p] == attacker.MapIndex);
            var secured = ground.Count(p => controllers[p] == attacker.MapIndex &&
                                            occupation[p] >= threshold);
            var meanOccupation = held == 0 ? 0f : ground.Where(p => controllers[p] == attacker.MapIndex)
                .Average(p => occupation[p]);
            var own = c.Armies.Where(a => a.NationId == attacker.Id).ToArray();
            var theirs = c.Armies.Where(a => a.NationId == defender.Id).ToArray();
            var ownMilitia = own.Count(a => a.TemplateId == MilitiaCore.TemplateId);
            var theirMilitia = theirs.Count(a => a.TemplateId == MilitiaCore.TemplateId);
            Console.WriteLine($"PEACE_WATCH day={c.ElapsedDays} score={war.WarScore:0.0} " +
                              $"held={held}/{ground.Length} secured={secured} mean_occ={meanOccupation:0.00} " +
                              $"armies={attacker.Tag}:{own.Length}/{own.Sum(a => a.Divisions):0.0} " +
                              $"militia={attacker.Tag}:{ownMilitia} {defender.Tag}:{theirMilitia} " +
                              $"{defender.Tag}:{theirs.Length}/{theirs.Sum(a => a.Divisions):0.0} " +
                              $"vanished={string.Join(",", vanished.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))}");
        }

        var heldAtEnd = ground.Count(p => c.ProvinceController[p] == attacker.MapIndex);
        var securedAtEnd = ground.Count(p => c.ProvinceController[p] == attacker.MapIndex &&
                                             c.ProvinceOccupation[p] >= threshold);
        Console.WriteLine($"PEACE_RESULT ended={war.Ended} endedDay={war.EndedDay} winner={c.NationById(war.WinnerId)?.Tag} " +
                          $"score={war.WarScore:0.0} annexed={war.AnnexedProvinces} " +
                          $"heldAfter={heldAtEnd}/{ground.Length} securedAfter={securedAtEnd} " +
                          $"summary={war.PeaceSummary}");
        Console.WriteLine($"PEACE_CHURN fought={string.Join(",", fought.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))} " +
                          $"vanished={string.Join(",", vanished.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))}");
        return c.SimulationFault.Length == 0 ? 0 : 1;
    }
}
