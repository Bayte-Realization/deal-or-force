using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--conquest [serbia|world]`: what happens to a country after it takes somebody else's land.
///
/// Written for the owner's complaint of 17 November 2026: he played Serbia, unified Yugoslavia,
/// annexed Bulgaria, and was strangled by risings - "several times in a row on the same lands".
/// Before anything is designed to answer that, this says in numbers where the stability went and
/// how often the same ground rose again. It only reads the world after staging it: the campaign
/// runs exactly as it would.
///
///   serbia (default) - the owner's case staged on the 2024 start. Serbia takes the whole of
///     Bosnia and Herzegovina, Montenegro, North Macedonia, Croatia, Slovenia and Kosovo, and then
///     Bulgaria, through StatePartition.Annex with asConquest - the call every peace treaty and
///     annexation ends in. The beaten countries' formations are destroyed first, as the war that
///     won the ground would have done, and every conquered region is left at least as radical as
///     a war leaves it (`--war-radicalism R`, default 0.20: the median the computer's own
///     conquests leave, measured by the `world` mode). Options:
///       --as-player          Serbia is a seat nobody plays: the computer's cabinet does nothing
///                            for it, which is the player who does not know what to press.
///       --heed-alerts        with --as-player: once a week the seat reads its situation report and,
///                            when the "conquered ground strains the state" line is there and the
///                            grant is not refused, presses the one button that line offers - the
///                            player who reads his list of things to do and nothing more.
///       --declare-peoples    counterfactual: the Yugoslav peoples counted as Serbia's own, the
///                            way a proclaimed Yugoslavia should count them.
///       --bulgaria-day N     Bulgaria is taken on day N rather than at once.
///   world - an ordinary campaign: every conquest the computer makes, who took what, how long
///     the conqueror's stability took to come back, and every rising on the conquered ground.
/// </summary>
internal static class ConquestReport
{
    static readonly string[] Yugoslav = { "BIH", "MNE", "MKD", "HRV", "SVN", "KOS" };
    static readonly int[] Snapshots = { 0, 365, 730, 1825, 3650 };

    public static int Run(Campaign c, int days, string[] args)
    {
        var at = Array.IndexOf(args, "--conquest");
        var mode = at >= 0 && at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[at + 1] : "serbia";
        return mode == "world" ? World(c, days, OptionText(args, "--watch")) : Serbia(c, days, args);
    }

    // ---------------------------------------------------------------- the staged case --

    static int Serbia(Campaign c, int days, string[] args)
    {
        var srb = ByTag(c, "SRB");
        if (srb is null) { Console.WriteLine("CONQUEST no SRB"); return 2; }
        var warRadicalism = Option(args, "--war-radicalism", 0.20f);
        var bulgariaDay = (int)Option(args, "--bulgaria-day", 0f);
        var asPlayer = args.Contains("--as-player");
        var declare = args.Contains("--declare-peoples");
        // The neighbours' answer to a conquest this size is a coalition war (Statecraft), which is
        // a fair answer and a different question. Quiet: the notoriety is wiped after the taking,
        // so what is left is the conquered ground itself.
        var quiet = args.Contains("--quiet-world");
        var heed = asPlayer && args.Contains("--heed-alerts");
        var pressed = 0;
        if (asPlayer) c.ConfigureSinglePlayer(srb.Id);
        if (declare)
            DemographyCatalog.Declare(srb.Tag, new[] { "serbian", "croatian", "slovene", "bosniak", "montenegrin", "macedonian" });

        Console.WriteLine($"CONQUEST serbia seed={c.Seed} days={days} war_radicalism={warRadicalism:0.00} " +
                          $"as_player={asPlayer} heed_alerts={heed} declare_peoples={declare} bulgaria_day={bulgariaDay} quiet_world={quiet}");
        Console.WriteLine($"BEFORE SRB stability={srb.Stability:0.000} regions={c.StatesOf(srb).Length} people={srb.Population:N0} bias={srb.StabilityBias:0.000}");

        var home = new HashSet<int>(c.StatesOf(srb).Select(TerritorialClaims.Origin));
        var tracked = new Dictionary<int, (string Name, string From)>();
        foreach (var s in c.StatesOf(srb)) tracked[TerritorialClaims.Origin(s)] = (s.Name, "SRB");
        foreach (var tag in Yugoslav) Take(c, srb, tag, warRadicalism, tracked);
        var proclaim = OptionText(args, "--proclaim");
        if (proclaim.Length > 0) Proclaim(c, srb, proclaim);
        if (bulgariaDay <= 0) Take(c, srb, "BGR", warRadicalism, tracked);
        if (quiet) srb.Notoriety = 0f;

        var watch = new Watch(c, srb, tracked);
        c.IsPaused = false;
        Snapshot(c, srb, tracked, home, 0);
        var floorUntil = -1; var daysBelowLine = 0;
        var series = new List<float>();
        for (var day = 1; day <= days; day++)
        {
            if (day == bulgariaDay)
            {
                Take(c, srb, "BGR", warRadicalism, tracked); watch.Add(c, tracked);
                if (quiet) srb.Notoriety = 0f;
            }
            if (heed && day % 7 == 0 && ConquestStrainNotice.Read(c, srb) is { TargetId: > 0 } notice &&
                c.StateByMapIndex(notice.TargetId) is { } asked && RegionalAutonomy.Refusal(c, srb, asked).Length == 0)
            {
                new GrantRegionalAutonomyCommand { IssuerId = srb.Id, StateMapIndex = asked.MapIndex }.Apply(c);
                pressed++;
            }
            var radBefore = MeanRadicalism(c, srb, _ => true);
            c.TickDay();
            watch.Day(c, day);
            // A day on which the whole country's radicalism moved by more than half a point is a
            // day something outside the weekly forecast happened to it: say what the state of the
            // country was, so the cause can be found rather than guessed.
            var radJump = MeanRadicalism(c, srb, _ => true) - radBefore;
            if (Math.Abs(radJump) >= 0.005) Diagnose(c, srb, day, radJump);
            var stability = srb.Stability;
            if (stability <= StabilityCore.Weights.Floor + 0.02f) floorUntil = day;
            series.Add(stability);
            if (stability < c.Defines.CivilWarStabilityThreshold) daysBelowLine++;
            if (day % 91 == 0)
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "CURVE day={0} stab={1:0.000} target={2:0.000} overstretch={3:0.000} rad_home={4:0.000} rad_taken={5:0.000} held_taken={6} wars={7} mandate={8:0.000} mandate_floor={9:0.000} formed={10} cohesion={11:0.000}",
                    day, stability, StabilityCore.Preview(c, srb), srb.ImperialOverstretch,
                    MeanRadicalism(c, srb, s => home.Contains(TerritorialClaims.Origin(s))),
                    MeanRadicalism(c, srb, s => !home.Contains(TerritorialClaims.Origin(s))),
                    c.StatesOf(srb).Count(s => !home.Contains(TerritorialClaims.Origin(s))),
                    c.Wars.Count(w => !w.Ended && w.Involves(srb.Id)),
                    // A government without a mandate breeds radicalism in every region it holds
                    // (PoliticalMood), and that is not in the radicalism forecast a player reads.
                    PoliticalMood.Mandate(c, srb),
                    c.Defines.PoliticalMoodMandateMultiple / Math.Max(1, IdeologyCatalog.All.Count),
                    srb.FormedNationId.Length == 0 ? "-" : srb.FormedNationId, srb.UnionCohesion));
            if (Array.IndexOf(Snapshots, day) >= 0) Snapshot(c, srb, tracked, home, day);
        }
        if (Array.IndexOf(Snapshots, days) < 0) Snapshot(c, srb, tracked, home, days);
        watch.Report(c, "SRB");
        if (heed) Console.WriteLine($"HEED pressed={pressed}");
        // Measured from the worst of it: stability closes a tenth of its gap a week, so on the first
        // morning after the taking it still reads what the country was, and "above 0.40 on day 1"
        // says nothing about getting out.
        var low = 0;
        for (var i = 1; i < series.Count; i++) if (series[i] < series[low]) low = i;
        int After(float line) { for (var i = low; i < series.Count; i++) if (series[i] >= line) return i + 1; return -1; }
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "RECOVERY lowest={0:0.000}@{1} last_day_at_floor={2} back_above_0.30={3} back_above_0.40={4} days_below_civil_war_line={5} of {6}",
            series.Count == 0 ? 0 : series[low], low + 1, floorUntil, After(0.30f), After(0.40f), daysBelowLine, days));
        return 0;
    }

    /// <summary>
    /// The owner unified Yugoslavia and proclaimed it before he turned on Bulgaria. The command is
    /// the one a player presses; only the founding stability gate is lifted for the afternoon and
    /// the treasury topped up to its price, because a staged conquest has not had the years of
    /// peace a real founder would have had to reach them.
    /// </summary>
    static void Proclaim(Campaign c, Nation n, string id)
    {
        var definition = FormableNations.Find(c, id);
        if (definition is null) { Console.WriteLine($"PROCLAIM {id}: no such formable"); return; }
        var stability = n.Stability;
        n.Stability = MathF.Max(stability, definition.MinimumStability + 0.01f);
        var status = FormableNations.Evaluate(c, n, definition);
        if (n.Treasury < status.Cost) n.Treasury = status.Cost;
        new FormNationCommand { IssuerId = n.Id, FormableId = id }.Apply(c);
        n.Stability = stability;
        Console.WriteLine($"PROCLAIM {id}: formed={n.FormedNationId == definition.Id} cohesion={n.UnionCohesion:0.000} " +
                          $"own_peoples={string.Join(",", DemographyCatalog.PrimaryCultures(n.Tag))} unmet=" +
                          string.Join(";", status.Requirements.Where(r => !r.Met).Select(r => r.Label)));
    }

    static string OptionText(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : "";
    }

    static void Diagnose(Campaign c, Nation n, int day, double jump)
    {
        var sources = NationalModifiers.Sources(n)
            .Where(src => src.Modifiers.TryGetValue("radicalism_weekly", out var r) && r != 0f)
            .Select(src => $"{src.Source}:{src.Modifiers["radicalism_weekly"]:+0.0000;-0.0000}");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "RADJUMP day={0} delta={1:+0.0000;-0.0000} mean={2:0.000} stab={3:0.000} infl={4:0.000} shortage={5:0.000} debt={6} arrears={7:0.00} treasury={8:0.00} " +
            "gov={9} ideology={10} focus={11} mandate={12:0.000} mod_rad={13:+0.0000;-0.0000} [{14}] movements={15} stress={16:0.000}",
            day, jump, MeanRadicalism(c, n, _ => true), n.Stability, n.Inflation, n.ShortageIndex, n.DebtCondition, n.PaymentArrears,
            n.Treasury, n.Government, n.Ideology, n.GovernmentFocus, PoliticalMood.Mandate(c, n),
            NationalModifiers.Of(n, "radicalism_weekly"), string.Join(",", sources),
            c.Nations.Count(o => o.Movement is { Dissolved: false } && c.StatesOf(n).Any(s => s.MapIndex == o.Movement.HeadquartersStateMapIndex)),
            c.StatesOf(n).Select(s => s.LivelihoodStress).DefaultIfEmpty(0f).Average()));
    }

    /// <summary>The whole of one country, taken the way a treaty takes it.</summary>
    static void Take(Campaign c, Nation taker, string tag, float warRadicalism, Dictionary<int, (string, string)> tracked)
    {
        var loser = ByTag(c, tag);
        if (loser is null || c.ProvinceMap is null) { Console.WriteLine($"TAKE {tag}: not in this world"); return; }
        foreach (var army in c.Armies.Where(a => a.NationId == loser.Id).ToList())
            Attrition.Destroy(c, army, "conquest_staged");
        var map = c.ProvinceMap;
        var provinces = new List<int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (map.ProvinceToNation[p] == loser.MapIndex) provinces.Add(p);
        var before = c.StatesOf(loser).Select(s => s.MapIndex).ToHashSet();
        StatePartition.Annex(c, provinces, taker, asConquest: true);
        var people = 0L;
        foreach (var s in c.StatesOf(taker))
        {
            if (!before.Contains(s.MapIndex) && !before.Contains(TerritorialClaims.Origin(s))) continue;
            s.Radicalism = MathF.Max(s.Radicalism, warRadicalism);
            tracked[TerritorialClaims.Origin(s)] = (s.Name, tag);
            people += s.Population;
        }
        Console.WriteLine($"TAKE {tag}: provinces={provinces.Count} regions={before.Count} people={people:N0} day={c.ElapsedDays}");
    }

    static void Snapshot(Campaign c, Nation n, Dictionary<int, (string Name, string From)> tracked, HashSet<int> home, int day)
    {
        var drivers = new Breakdown();
        var target = StabilityCore.Preview(c, n, drivers);
        var parts = string.Join(" | ", drivers.Entries.Select(e => $"{e.Source}={e.Value:+0.000;-0.000}"));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "SNAP day={0} stability={1:0.000} target={2:0.000} overstretch={3:0.000} strained_weight={4:0.00} capacity={5} regions={6} people={7:N0}",
            day, n.Stability, target, n.ImperialOverstretch, StrainedWeight(c, n), c.Defines.BaseImperialCapacity,
            c.StatesOf(n).Length, c.StatesOf(n).Sum(s => s.Population)));
        Console.WriteLine($"SNAP_PARTS day={day} {parts}");
        // A country that has lost every region still has a row in the world; it has no region to forecast.
        var largest = c.StatesOf(n).Where(s => !home.Contains(TerritorialClaims.Origin(s)))
            .OrderByDescending(s => s.Population).FirstOrDefault() ?? c.StatesOf(n).FirstOrDefault();
        if (largest is not null)
        {
            var radicalism = RadicalismCore.Forecast(c, n, largest);
            Console.WriteLine($"SNAP_RAD_FORECAST day={day} largest_taken_region " +
                              string.Join(" | ", radicalism.Entries.Select(e => $"{e.Source}={e.Value * 100:+0.000;-0.000}pp")));
        }
        foreach (var group in c.StatesOf(n).GroupBy(s => tracked.TryGetValue(TerritorialClaims.Origin(s), out var t) ? t.From : "new")
                     .OrderBy(g => g.Key))
        {
            long people = group.Sum(s => s.Population);
            double w(Func<ScenarioState, float> f) => people <= 0 ? 0 : group.Sum(s => f(s) * (double)s.Population) / people;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "SNAP_FROM day={0} from={1} regions={2} people={3:N0} stability={4:0.000} radicalism={5:0.000} foreign={6:0.000} pressure={7:0.000} acceptance={8:0.000} autonomous={9} homeland={10}",
                day, group.Key, group.Count(), people, w(s => s.Stability), w(s => s.Radicalism),
                w(s => EconomySystem.ForeignShare(s, n.Tag)), w(s => s.SecessionPressure), w(s => s.Acceptance),
                group.Count(RegionalAutonomy.Autonomous), group.Count(s => TerritorialClaims.TitleOf(c, n, s) == TerritorialTitle.Homeland)));
        }
        foreach (var s in c.StatesOf(n).OrderBy(s => s.MapIndex))
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "SNAP_REGION day={0} id={1} name=\"{2}\" from={3} title={4} pop={5:N0} stability={6:0.000} radicalism={7:0.000} foreign={8:0.000} living={9:0.000} pressure={10:0.000} recovery_days={11} " +
                "acceptance={12:0.000} autonomous={13} exhausted_days={14}",
                day, s.MapIndex, s.Name, tracked.TryGetValue(TerritorialClaims.Origin(s), out var t) ? t.From : "new",
                TerritorialClaims.TitleOf(c, n, s), s.Population, s.Stability, s.Radicalism,
                EconomySystem.ForeignShare(s, n.Tag), s.StandardOfLiving, s.SecessionPressure, RevoltRecovery.DaysLeft(c, n, s),
                s.Acceptance, s.AutonomousSinceDay, RisingAftermath.DaysLeft(c, s)));
    }

    /// <summary>
    /// The same sum StabilityCore.Overstretch makes, before it is turned into a cost: read from the
    /// rule itself, so the report cannot drift from it when acceptance and autonomy change the weights.
    /// </summary>
    static float StrainedWeight(Campaign c, Nation nation) => StabilityCore.StrainedWeight(c, nation);

    static double MeanRadicalism(Campaign c, Nation n, Func<ScenarioState, bool> which)
    {
        double sum = 0, people = 0;
        foreach (var s in c.StatesOf(n))
        {
            if (!which(s)) continue;
            sum += s.Radicalism * (double)s.Population;
            people += s.Population;
        }
        return people > 0 ? sum / people : 0;
    }

    // ------------------------------------------------------------ who holds what, daily --

    /// <summary>
    /// Every change of hands of one country's ground, by original region, so a region that rose,
    /// was taken back and rose again counts twice under one name however it was cut up between.
    /// </summary>
    sealed class Watch
    {
        readonly int _nation;
        readonly Dictionary<int, (string Name, string From)> _tracked;
        readonly Dictionary<int, int> _owner = new();
        readonly Dictionary<int, int> _rose = new();
        readonly Dictionary<string, int> _kinds = new();
        readonly Dictionary<string, int> _chronicle = new();
        readonly HashSet<int> _wars = new();
        long _seen;

        public Watch(Campaign c, Nation nation, Dictionary<int, (string Name, string From)> tracked)
        {
            _nation = nation.Id;
            _tracked = tracked;
            Add(c, tracked);
            _seen = c.Chronicle.Entries.Count == 0 ? 0 : c.Chronicle.Entries.Max(e => e.Sequence);
        }

        public void Add(Campaign c, Dictionary<int, (string Name, string From)> tracked)
        {
            foreach (var (origin, owner) in Owners(c))
                if (tracked.ContainsKey(origin) && !_owner.ContainsKey(origin)) _owner[origin] = owner;
        }

        static Dictionary<int, int> Owners(Campaign c)
        {
            var best = new Dictionary<int, (int Owner, long People)>();
            foreach (var s in c.States)
            {
                if (s.Population <= 0) continue;
                var origin = TerritorialClaims.Origin(s);
                if (!best.TryGetValue(origin, out var b) || s.Population > b.People) best[origin] = (s.NationMapIndex, s.Population);
            }
            return best.ToDictionary(p => p.Key, p => p.Value.Owner);
        }

        public void Day(Campaign c, int day)
        {
            var self = c.NationById(_nation);
            var today = new List<ChronicleEntry>();
            foreach (var entry in c.Chronicle.Entries)
            {
                if (entry.Sequence <= _seen) continue;
                _seen = entry.Sequence;
                today.Add(entry);
                var mine = entry.ActorId == _nation || entry.TargetId == _nation ||
                           c.NationById(entry.ActorId)?.SecededFromId == _nation;
                if (mine) _chronicle[entry.Kind] = _chronicle.GetValueOrDefault(entry.Kind) + 1;
            }
            // Every war it enters and leaves, with whom and why: the war term is the largest
            // single cost a stability can carry, and a count of wars says nothing about it.
            foreach (var war in c.Wars)
            {
                if (!war.Involves(_nation)) continue;
                if (!war.Ended && _wars.Add(war.Id))
                    Console.WriteLine($"WAR_START day={day} id={war.Id} attacker={c.NationById(war.AttackerId)?.Tag} defender={c.NationById(war.DefenderId)?.Tag} " +
                                      $"why={war.Justification} civil={war.CivilConflict} allies_a={string.Join(",", war.AttackerAllies.Select(id => c.NationById(id)?.Tag))} " +
                                      $"allies_d={string.Join(",", war.DefenderAllies.Select(id => c.NationById(id)?.Tag))}");
                else if (war.Ended && _wars.Remove(war.Id))
                    Console.WriteLine($"WAR_END day={day} id={war.Id} winner={c.NationById(war.WinnerId)?.Tag ?? "-"} lasted={war.EndedDay - war.StartedDay} \"{war.PeaceSummary}\"");
            }
            foreach (var (origin, owner) in Owners(c))
            {
                if (!_owner.TryGetValue(origin, out var was) || was == owner) continue;
                _owner[origin] = owner;
                var to = c.NationByMapIndex(owner);
                // What the chronicle wrote that day about the new holder: the road the ground left by,
                // which SecededFromId alone cannot tell - a release, a sale and a rising all carry it.
                // Sofia "rose twice" in a measured Yugoslavia; the second time it was sold back to a
                // reborn Bulgaria in a deal (seed 7, day 3215).
                var road = string.Join(",", today.Where(e => to is not null && (e.ActorId == to.Id || e.TargetId == to.Id))
                    .Select(e => e.Kind).Distinct());
                var kind = road.Contains("deal_signed") && to?.Id != self?.Id ? "deal"
                    : road.Contains("country_released") ? "released" : Kind(c, self, was, to, day);
                _kinds[kind] = _kinds.GetValueOrDefault(kind) + 1;
                if (kind is "secession" or "civil_war" or "movement") _rose[origin] = _rose.GetValueOrDefault(origin) + 1;
                Console.WriteLine($"HANDS day={day} region=\"{_tracked[origin].Name}\" origin={origin} from_country={_tracked[origin].From} " +
                                  $"was={c.NationByMapIndex(was)?.Tag ?? "-"} now={to?.Tag ?? "-"} kind={kind} " +
                                  $"stab={self?.Stability:0.000} rad={c.States.Where(s => TerritorialClaims.Origin(s) == origin).Select(s => s.Radicalism).DefaultIfEmpty().Max():0.000} " +
                                  $"road={(road.Length == 0 ? "-" : road)}");
            }
        }

        static string Kind(Campaign c, Nation self, int was, Nation to, int day)
        {
            if (self is null) return "gone";
            if (to?.Id == self.Id) return c.NationByMapIndex(was)?.SecededFromId == self.Id ? "crushed_back" : "taken_back";
            if (was != self.MapIndex) return "between_others";
            if (to is null) return "unowned";
            if (to.Movement is not null) return "movement";
            if (to.SecededFromId == self.Id) return to.LastCivilWarDay >= day - 1 ? "civil_war" : "secession";
            if (to.AnswersTo(self.Id)) return "released";
            return "lost_to_" + to.Tag;
        }

        public void Report(Campaign c, string tag)
        {
            Console.WriteLine("HANDS_TOTAL " + string.Join(" ", _kinds.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
            Console.WriteLine($"RISINGS total={_rose.Values.Sum()} regions_that_rose={_rose.Count} rose_twice_or_more={_rose.Count(r => r.Value >= 2)} " +
                              $"most={(_rose.Count == 0 ? 0 : _rose.Values.Max())}");
            foreach (var (origin, times) in _rose.OrderByDescending(r => r.Value).ThenBy(r => r.Key))
                Console.WriteLine($"ROSE region=\"{_tracked[origin].Name}\" from_country={_tracked[origin].From} times={times}");
            Console.WriteLine("CHRONICLE " + string.Join(" ", _chronicle.OrderByDescending(k => k.Value).Take(40).Select(k => $"{k.Key}={k.Value}")));
            Console.WriteLine($"AUTONOMY granted={_chronicle.GetValueOrDefault("autonomy_granted")} revoked={_chronicle.GetValueOrDefault("autonomy_revoked")}");
            Console.WriteLine($"HOMELAND integrated={_chronicle.GetValueOrDefault("homeland_integrated")} exhausted_risings={_chronicle.GetValueOrDefault("rising_exhausted")}");
            var self = c.NationById(_nation);
            var lost = _owner.Where(o => o.Value != self?.MapIndex).ToList();
            var lostPeople = c.States.Where(s => lost.Any(l => l.Key == TerritorialClaims.Origin(s))).Sum(s => s.Population);
            Console.WriteLine($"LOST at_end regions={lost.Count} of {_owner.Count} people={lostPeople:N0} holders=" +
                              string.Join(",", lost.GroupBy(l => c.NationByMapIndex(l.Value)?.Tag ?? "-").Select(g => $"{g.Key}:{g.Count()}")));
        }
    }

    // --------------------------------------------------------------- the ordinary world --

    sealed class Episode
    {
        public int Conqueror;
        public string Tag = "";
        public int FirstDay, LastDay;
        public float StabilityBefore, Lowest = 1f;
        public int LowestDay;
        public long People;
        public readonly HashSet<string> From = new();
        public readonly HashSet<int> Origins = new();
        public readonly List<float> RadicalismAtTransfer = new();
        public readonly Dictionary<int, int> Rose = new();
        public int DaysBelowLine;
        public readonly List<float> Series = new();

        /// <summary>
        /// The first day after the worst of it that the conqueror stood within three points of where
        /// it stood before the first conquest, or -1. Read from the whole series rather than on the
        /// day: stability closes a tenth of its gap a week, so a conquest has not yet shown in the
        /// figure on the day after it, and "back where it was" on that day means nothing.
        /// </summary>
        public int Recovered()
        {
            if (Series.Count == 0) return -1;
            var low = 0;
            for (var i = 1; i < Series.Count; i++) if (Series[i] < Series[low]) low = i;
            for (var i = low; i < Series.Count; i++) if (Series[i] >= StabilityBefore - 0.03f) return FirstDay + i;
            return -1;
        }
    }

    static int World(Campaign c, int days, string watch = "")
    {
        Console.WriteLine($"CONQUEST world seed={c.Seed} days={days}");
        c.IsPaused = false;
        var owner = c.States.ToDictionary(s => s.MapIndex, s => s.NationMapIndex);
        var yesterday = c.Nations.ToDictionary(n => n.Id, n => n.Stability);
        var episodes = new Dictionary<int, Episode>();
        var allRadicalism = new List<float>();
        // What the world did with its conquered ground, read from the chronicle as it is written:
        // the chronicle is a ring of 2048 lines, and reading it at the end would report the last
        // two thousand of ten years as if they were all of them.
        var facts = new Dictionary<string, int>();
        var factsBy = new Dictionary<(string Kind, int Actor), int>();
        var seen = c.Chronicle.Entries.Count == 0 ? 0L : c.Chronicle.Entries.Max(e => e.Sequence);
        string[] counted = { "civil_war", "coup_civil_war", "secession", "state_collapse", "revolution",
            "homeland_integrated", "autonomy_granted", "autonomy_revoked", "rising_exhausted" };
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            foreach (var entry in c.Chronicle.Entries)
            {
                if (entry.Sequence <= seen) continue;
                seen = entry.Sequence;
                if (Array.IndexOf(counted, entry.Kind) < 0) continue;
                facts[entry.Kind] = facts.GetValueOrDefault(entry.Kind) + 1;
                factsBy[(entry.Kind, entry.ActorId)] = factsBy.GetValueOrDefault((entry.Kind, entry.ActorId)) + 1;
            }
            if (day % 365 == 0 || day == days) WorldStability(c, day);
            if (watch.Length > 0 && day % 91 == 0) WatchCountry(c, ByTag(c, watch), day);
            foreach (var s in c.States)
            {
                if (!owner.TryGetValue(s.MapIndex, out var was)) { owner[s.MapIndex] = s.NationMapIndex; continue; }
                if (was == s.NationMapIndex) continue;
                owner[s.MapIndex] = s.NationMapIndex;
                var from = c.NationByMapIndex(was);
                var to = c.NationByMapIndex(s.NationMapIndex);
                if (from is null || to is null || s.Population <= 0) continue;
                var origin = TerritorialClaims.Origin(s);
                // A rising on ground somebody conquered: counted against that conquest.
                if (episodes.TryGetValue(from.Id, out var held) && held.Origins.Contains(origin) &&
                    (to.Movement is not null || to.SecededFromId == from.Id))
                {
                    held.Rose[origin] = held.Rose.GetValueOrDefault(origin) + 1;
                    Console.WriteLine($"ROSE day={day} conqueror={from.Tag} region=\"{s.Name}\" by={to.Tag} rad={s.Radicalism:0.000} stab={from.Stability:0.000}");
                    continue;
                }
                if (!Conquest(c, from, to, day)) continue;
                if (!episodes.TryGetValue(to.Id, out var e))
                    episodes[to.Id] = e = new Episode
                    {
                        Conqueror = to.Id, Tag = to.Tag, FirstDay = day,
                        StabilityBefore = yesterday.GetValueOrDefault(to.Id, to.Stability)
                    };
                e.LastDay = day;
                e.From.Add(from.Tag);
                if (e.Origins.Add(origin)) e.People += s.Population;
                e.RadicalismAtTransfer.Add(s.Radicalism);
                allRadicalism.Add(s.Radicalism);
                Console.WriteLine($"CONQUEST_TRANSFER day={day} by={to.Tag} from={from.Tag} region=\"{s.Name}\" pop={s.Population:N0} " +
                                  $"rad={s.Radicalism:0.000} foreign={EconomySystem.ForeignShare(s, to.Tag):0.00} stab_before={e.StabilityBefore:0.000}");
            }
            foreach (var e in episodes.Values)
            {
                var n = c.NationById(e.Conqueror);
                if (n is null) continue;
                if (n.Stability < e.Lowest) { e.Lowest = n.Stability; e.LowestDay = day; }
                if (n.Stability < c.Defines.CivilWarStabilityThreshold) e.DaysBelowLine++;
                e.Series.Add(n.Stability);
            }
            foreach (var n in c.Nations) yesterday[n.Id] = n.Stability;
        }
        foreach (var e in episodes.Values.OrderByDescending(e => e.People))
        {
            var n = c.NationById(e.Conqueror);
            var stillHeld = c.States.Count(s => s.NationMapIndex == n?.MapIndex && e.Origins.Contains(TerritorialClaims.Origin(s)));
            var rad = e.RadicalismAtTransfer.OrderBy(x => x).ToList();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "EPISODE conqueror={0} from={1} first_day={2} last_day={3} regions={4} people={5:N0} rad_at_transfer_median={6:0.000} " +
                "stab_before={7:0.000} lowest={8:0.000}@{9} recovered_day={10} stab_end={11:0.000} overstretch_end={12:0.000} days_below_line={13} " +
                "risings={14} regions_rose={15} rose_twice={16} still_held={17} autonomies={18} integrated={19}",
                e.Tag, string.Join("+", e.From.OrderBy(t => t)), e.FirstDay, e.LastDay, e.Origins.Count, e.People,
                rad.Count == 0 ? 0 : rad[rad.Count / 2], e.StabilityBefore, e.Lowest, e.LowestDay, e.Recovered(),
                n?.Stability ?? 0, n?.ImperialOverstretch ?? 0, e.DaysBelowLine, e.Rose.Values.Sum(), e.Rose.Count,
                e.Rose.Count(r => r.Value >= 2), stillHeld,
                factsBy.GetValueOrDefault(("autonomy_granted", e.Conqueror)),
                factsBy.GetValueOrDefault(("homeland_integrated", e.Conqueror))));
        }
        Console.WriteLine("WORLD_FACTS " + string.Join(" ", counted.Select(k => $"{k}={facts.GetValueOrDefault(k)}")));
        // Who gave autonomy, conqueror or not: the computer has to be seen using the lever.
        Console.WriteLine("AUTONOMY granted=" + facts.GetValueOrDefault("autonomy_granted") + " by=" +
                          string.Join(",", factsBy.Where(f => f.Key.Kind == "autonomy_granted").OrderByDescending(f => f.Value)
                              .ThenBy(f => f.Key.Actor).Select(f => $"{c.NationById(f.Key.Actor)?.Tag ?? "?"}:{f.Value}")));
        Console.WriteLine("HOMELAND integrated=" + facts.GetValueOrDefault("homeland_integrated") + " by=" +
                          string.Join(",", factsBy.Where(f => f.Key.Kind == "homeland_integrated").OrderByDescending(f => f.Value)
                              .ThenBy(f => f.Key.Actor).Select(f => $"{c.NationById(f.Key.Actor)?.Tag ?? "?"}:{f.Value}")));
        var sorted = allRadicalism.OrderBy(x => x).ToList();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "WORLD conquerors={0} regions_conquered={1} radicalism_at_transfer p25={2:0.000} median={3:0.000} p75={4:0.000} " +
            "recovered={5} never_recovered={6} risings={7} rose_twice={8}",
            episodes.Count, sorted.Count, Pct(sorted, 0.25f), Pct(sorted, 0.5f), Pct(sorted, 0.75f),
            episodes.Values.Count(e => e.Recovered() >= 0), episodes.Values.Count(e => e.Recovered() < 0),
            episodes.Values.Sum(e => e.Rose.Values.Sum()), episodes.Values.Sum(e => e.Rose.Count(r => r.Value >= 2))));
        return 0;
    }

    /// <summary>
    /// The world's stability in one line: the median and mean of every country that holds ground,
    /// and how many are below the civil-war line. A change meant for conquered ground must not move
    /// the rest of the world, and this is the line that would show it.
    /// </summary>
    static void WorldStability(Campaign c, int day)
    {
        var values = c.Nations.Where(n => n.Movement is null && c.StatesOf(n).Length > 0 && float.IsFinite(n.Stability))
            .Select(n => n.Stability).OrderBy(v => v).ToList();
        if (values.Count == 0) return;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "WORLD_STABILITY day={0} countries={1} median={2:0.000} mean={3:0.000} below_civil_war_line={4}",
            day, values.Count, values[values.Count / 2], values.Average(),
            values.Count(v => v < c.Defines.CivilWarStabilityThreshold)));
    }

    /// <summary>
    /// `--watch TAG` in world mode: one country every quarter - stability, its target and the three
    /// heaviest terms, the overstretch and what it is made of. Written for the one world (seed 42)
    /// where Russia held its Ukrainian conquest at 0.62 before the slice and fell to 0.04 after it,
    /// because an episode line says that it happened and not why.
    /// </summary>
    static void WatchCountry(Campaign c, Nation n, int day)
    {
        if (n is null) return;
        var drivers = new Breakdown();
        var target = StabilityCore.Preview(c, n, drivers);
        var states = c.StatesOf(n);
        var unentitled = states.Where(s => ConquestAcceptance.Unentitled(c, n, s)).ToList();
        var divergent = states.Count(s => s.MapIndex != n.CapitalStateMapIndex && s.PoliticalDivergence >= 0.50f &&
                                          !unentitled.Contains(s));
        var subjects = c.Nations.Count(o => o.Id != n.Id && o.AnswersTo(n.Id));
        var worst = drivers.Entries.OrderBy(e => e.Value).Take(4)
            .Select(e => string.Format(CultureInfo.InvariantCulture, "{0}:{1:+0.000;-0.000}", e.Source, e.Value));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "WATCH day={0} tag={1} stab={2:0.000} target={3:0.000} overstretch={4:0.000} strain={5:0.00} unentitled={6} " +
            "unentitled_rad={7:0.000} unentitled_acc={8:0.000} autonomous={9} divergent={10} subjects={11} regions={12} enemies={13} [{14}]",
            day, n.Tag, n.Stability, target, n.ImperialOverstretch, StabilityCore.StrainedWeight(c, n), unentitled.Count,
            unentitled.Count == 0 ? 0 : unentitled.Average(s => s.Radicalism),
            unentitled.Count == 0 ? 0 : unentitled.Average(s => s.Acceptance),
            states.Count(RegionalAutonomy.Autonomous), divergent, subjects, states.Length,
            c.Nations.Count(o => o.Id != n.Id && CampaignDiplomacy.AtWar(c, o.Id, n.Id)),
            string.Join(" ", worst)));
    }

    /// <summary>Ground moving between two sides of a war that is on or ended in the last two days.</summary>
    static bool Conquest(Campaign c, Nation from, Nation to, int day)
    {
        if (from.Movement is not null || to.Movement is not null || to.SecededFromId == from.Id || from.SecededFromId == to.Id) return false;
        foreach (var w in c.Wars)
        {
            if (w.CivilConflict || !w.Involves(from.Id) || !w.Involves(to.Id) || w.IsAttacking(from.Id) == w.IsAttacking(to.Id)) continue;
            if (!w.Ended || w.EndedDay >= c.ElapsedDays - 2) return true;
        }
        return false;
    }

    static float Pct(List<float> sorted, float p) => sorted.Count == 0 ? 0f : sorted[Math.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];

    static Nation ByTag(Campaign c, string tag) =>
        c.Nations.FirstOrDefault(n => n.Tag == tag && n.Movement is null && c.StatesOf(n).Length > 0);

    static float Option(string[] args, string name, float fallback)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length && float.TryParse(args[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;
    }
}
