using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--taiwan-return`: tracks Taiwan (TWN) vs China (CHN) over N campaign days with pure AI,
/// reporting decisions, wars, amphibious landings, mainland bridgeheads, volunteer rallies,
/// defections, occupied/owned mainland states, and proclamation of Reunified China.
/// </summary>
internal static class TaiwanReturnReport
{
    public static int Run(Campaign c, int days)
    {
        c.IsPaused = false;
        MainlandReclamation.Tally.Clear();
        NavalInvasion.Tally.Clear();

        var twn = c.NationByTag("TWN");
        var chn = c.NationByTag("CHN");
        if (twn is null || chn is null)
        {
            Console.WriteLine("TAIWAN_RETURN missing TWN or CHN");
            return 2;
        }

        // Make sure nobody is human so both sides are pure AI.
        twn.IsHuman = false;
        chn.IsHuman = false;

        var chnOriginStates = c.StatesOf(chn).Select(s => s.MapIndex).ToHashSet();
        var twnStartStates = c.StatesOf(twn).Length;
        Console.WriteLine($"TAIWAN_RETURN seed={c.Seed} days={days}");
        Console.WriteLine($"  START TWN: states={twnStartStates} divs={CountDivs(c, twn.Id)} marines={CountMarines(c, twn.Id)} " +
                          $"hulls={CountHulls(c, twn.Id)} gdp={twn.Gdp:0.0} stab={twn.Stability:0.00} ws={twn.WarSupport:0.00} " +
                          $"canStage={AiStatecraft.CanStageWar(c, twn, chn)}");
        Console.WriteLine($"  START CHN: states={chnOriginStates.Count} divs={CountDivs(c, chn.Id)} " +
                          $"hulls={CountHulls(c, chn.Id)} gdp={chn.Gdp:0.0} stab={chn.Stability:0.00} ws={chn.WarSupport:0.00}");

        var seenChronicle = 0L;
        var seenEndedWars = new HashSet<int>();
        var maxTwnMainlandProvinces = 0;
        var maxTwnMainlandStatesOwned = 0;
        var warsStarted = 0;
        var bridgeheads = 0;
        var rallies = 0;
        var defections = 0;
        var proclaimedReunified = false;

        for (var day = 1; day <= days; day++)
        {
            c.TickDay();

            foreach (var war in c.Wars)
            {
                if (!war.Ended || !war.Involves(twn.Id) || !seenEndedWars.Add(war.Id)) continue;
                var winTag = c.NationById(war.WinnerId)?.Tag ?? "none";
                Console.WriteLine($"  [WAR_END day {war.EndedDay}] #{war.Id} {war.Name}: winner={winTag} score={war.WarScore:+0.0;-0.0} " +
                                  $"annexed={war.AnnexedProvinces} lastNeg='{war.LastNegotiation}' summary='{war.PeaceSummary}'");
            }

            foreach (var entry in c.Chronicle.Entries)
            {
                if (entry.Sequence <= seenChronicle) continue;
                seenChronicle = entry.Sequence;
                var involvesPair = entry.ActorId == twn.Id || entry.TargetId == twn.Id ||
                                   (entry.Kind.StartsWith("mainland_", StringComparison.Ordinal));
                if (!involvesPair) continue;
                if (entry.Kind is "mainland_campaign_launched" or "mainland_bridgehead" or "mainland_rally" or
                    "mainland_defection" or "war_started" or "peace_signed" or "state_formed" or
                    "military_intervention" or "civil_war")
                {
                    Console.WriteLine($"  [day {entry.Day,4}] {entry.Kind}: actor={entry.ActorName} target={entry.TargetName} subj={entry.Subject}");
                    if (entry.Kind == "mainland_campaign_launched") warsStarted++;
                    if (entry.Kind == "mainland_bridgehead") bridgeheads++;
                    if (entry.Kind == "mainland_rally") rallies++;
                    if (entry.Kind == "mainland_defection") defections++;
                }
            }

            if (day % 30 == 0 || day == days)
            {
                var mainlandControlled = CountMainlandProvincesControlledBy(c, twn, chnOriginStates);
                var mainlandOwned = c.StatesOf(twn).Count(s => chnOriginStates.Contains(s.MapIndex));
                if (mainlandControlled > maxTwnMainlandProvinces) maxTwnMainlandProvinces = mainlandControlled;
                if (mainlandOwned > maxTwnMainlandStatesOwned) maxTwnMainlandStatesOwned = mainlandOwned;
                if (string.Equals(twn.FormedNationId, "china_reunified", StringComparison.OrdinalIgnoreCase))
                    proclaimedReunified = true;

                if (day % 90 == 0 || day == days || (day >= 240 && day <= 450 && day % 30 == 0))
                {
                    var allTwnWars = Warfare.OpenWars(c).Where(w => !w.Ended && w.Involves(twn.Id))
                        .Select(w => $"#{w.Id}:{w.Name}({(w.IsAttacking(twn.Id) ? w.WarScore : -w.WarScore):+0.0;-0.0})").ToArray();
                    var warStr = allTwnWars.Length == 0 ? "peace" : string.Join(";", allTwnWars);
                    var readiness = MainlandReclamation.ExplainAiReadiness(c, twn, chn);
                    var fl = c.Fleets.FirstOrDefault(f => f.NationId == twn.Id);
                    var flStr = fl is null ? "none" : $"{fl.Mission}(h={fl.Hulls},s={fl.Strength:0.00},o={fl.Organisation:0.00},car={fl.Carried.Count},tgt={fl.TargetProvinceId},inPort={fl.InPort},ord={fl.HasOrder},lat={fl.Latitude:0.0},lon={fl.Longitude:0.0})";
                    var navTally = string.Join(",", NavalInvasion.Tally.Select(kv => $"{kv.Key}={kv.Value}"));
                    var frontCount = c.Armies.Count(a => a.NationId == twn.Id && a.FrontLineId >= 0);
                    var formStr = "";
                    if (mainlandOwned >= 15 && string.IsNullOrEmpty(twn.FormedNationId))
                    {
                        var cr = FormableNations.Find("china_reunified");
                        if (cr is not null)
                        {
                            var aiEv = FormableProclamationAi.Evaluate(c, twn, cr);
                            var ev = FormableNations.Evaluate(c, twn, cr);
                            var unmet = string.Join("|", ev.Requirements.Where(r => !r.Met).Select(r => r.Label));
                            formStr = $" form=[can={ev.CanForm},ai={aiEv.CanProclaim},ref='{aiEv.Refusal}',unmet='{unmet}',tr={twn.Treasury:0.0},cost={ev.Cost:0.0},gdp={twn.Gdp:0.0},stab={twn.Stability:0.00}]";
                        }
                    }
                    Console.WriteLine($"  day {day,4}: TWN states={c.StatesOf(twn).Length}(mlOwned={mainlandOwned},mlCtrl={mainlandControlled}) " +
                                      $"divs={CountDivs(c, twn.Id)}(fl={frontCount}) hulls={CountHulls(c, twn.Id)} " +
                                      $"fleet={flStr} nav=[{navTally}] wars=[{warStr}] ai={readiness}{formStr} | CHN divs={CountDivs(c, chn.Id)} stab={chn.Stability:0.00}");
                }
            }
        }

        Console.WriteLine($"SUMMARY seed={c.Seed} twnAlive={twn.Population > 0} chnAlive={chn.Population > 0} " +
                          $"campaigns={warsStarted} bridgeheads={bridgeheads} rallies={rallies} defections={defections} " +
                          $"maxMlCtrlProv={maxTwnMainlandProvinces} maxMlOwnedStates={maxTwnMainlandStatesOwned} " +
                          $"finalTwnStates={c.StatesOf(twn).Length} finalChnStates={c.StatesOf(chn).Length} " +
                          $"reunified={proclaimedReunified} formed='{twn.FormedNationId}' " +
                          $"tally=[{string.Join(", ", MainlandReclamation.Tally.Select(kv => $"{kv.Key}={kv.Value}"))}]");
        return 0;
    }

    private static int CountDivs(Campaign c, int nationId) =>
        c.Armies.Count(a => a.NationId == nationId && a.Strength > 0.05f);

    private static int CountMarines(Campaign c, int nationId) =>
        c.Armies.Count(a => a.NationId == nationId && a.TemplateId == "marine_brigade");

    private static int CountHulls(Campaign c, int nationId) =>
        c.Fleets.Where(f => f.NationId == nationId).Sum(f => f.Hulls);

    private static int CountMainlandProvincesControlledBy(Campaign c, Nation twn, HashSet<int> chnOriginStates)
    {
        var map = c.ProvinceMap;
        var ctrl = c.ProvinceController;
        if (map is null || ctrl is null) return 0;
        var count = 0;
        var twnMap = (ushort)twn.MapIndex;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.IsWater(p)) continue;
            if (ctrl[p] == twnMap && chnOriginStates.Contains(map.ProvinceToState[p])) count++;
        }
        return count;
    }
}
