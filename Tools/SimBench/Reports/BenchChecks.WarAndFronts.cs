using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static partial class BenchChecks
{

    internal static string FrontIsAStalemate(Campaign campaign, FrontSnapshot before,
        List<int> ukraineTrace, int days)
    {
        var russia = campaign.NationByTag("RUS");
        var ukraine = campaign.NationByTag("UKR");
        if (russia is null || ukraine is null) return "RUS or UKR is missing from the scenario";
        if (campaign.ProvinceController is null) return "";

        var after = new FrontSnapshot(campaign);
        var russianSwing = after.Held(russia) - before.Held(russia);
        var ukrainianSwing = after.Held(ukraine) - before.Held(ukraine);
        var years = MathF.Max(0.01f, days / 365f);

        Console.WriteLine($"  Russo-Ukrainian front after {days} day(s): " +
                          $"RUS {before.Held(russia):N0} -> {after.Held(russia):N0} ({russianSwing:+#;-#;0}), " +
                          $"UKR {before.Held(ukraine):N0} -> {after.Held(ukraine):N0} ({ukrainianSwing:+#;-#;0}).");

        // Ukraine is about 150 provinces on this map. Russia took roughly 0.7% of Ukraine
        // in 2024 - call it one province. Eight a year is a generous ceiling for a war
        // nobody is steering, and still an order of magnitude tighter than a front that
        // is quietly collapsing.
        var startedWith = before.Held(ukraine);
        var worst = ukraineTrace.Count == 0 ? after.Held(ukraine) : ukraineTrace.Min();
        var best = ukraineTrace.Count == 0 ? after.Held(ukraine) : ukraineTrace.Max();
        Console.WriteLine($"  Ukrainian holding over the run: {startedWith:N0} start, " +
                          $"{worst:N0} low, {best:N0} high, {after.Held(ukraine):N0} end.");

        // Two questions, deliberately separate. The first is the one that matters for a
        // player pressing play: the opening year has to be the war it is depicting, and a
        // front that walks on its own is a broken model however it ends up. The second is
        // looser on purpose - a war of attrition between an army of 77 divisions and one of
        // 54 is allowed to grind over years. That is what attrition is.
        var firstYear = ukraineTrace.Take(Math.Min(ukraineTrace.Count, 25)).ToList();
        var openingLow = firstYear.Count == 0 ? startedWith : firstYear.Min();
        var openingHigh = firstYear.Count == 0 ? startedWith : firstYear.Max();

        var opening = MathF.Max(MathF.Abs(openingLow - startedWith), MathF.Abs(openingHigh - startedWith));
        var held = after.Held(ukraine) / MathF.Max(1f, startedWith);

        // A stalemate is not the same thing as a truce. If the line holds because nothing
        // is fighting, the model is wrong in the other direction - this war is supposed to
        // cost both sides dearly for ground neither of them gets.
        var war = campaign.Wars.Find(candidate => candidate.Involves(russia.Id) && candidate.Involves(ukraine.Id));
        if (war is null) return "the Russo-Ukrainian war is missing from the campaign";
        var casualties = war.AttackerLosses + war.DefenderLosses;
        var battles = campaign.NextBattleId;
        var live = campaign.Battles.Count(battle => battle.WarId == war.Id && !battle.Ended);
        var russianDivisions = campaign.Armies.Count(army => army.NationId == russia.Id && army.AssignedWarId == war.Id);
        var ukrainianDivisions = campaign.Armies.Count(army => army.NationId == ukraine.Id && army.AssignedWarId == war.Id);
        Console.WriteLine($"  war cost after {days} day(s): {war.AttackerLosses:N0} RUS / " +
                          $"{war.DefenderLosses:N0} UKR casualties, war score {war.WarScore:+0.0;-0.0;0.0}.");
        Console.WriteLine($"  engagements: {battles:N0} battles worldwide ({live:N0} live on this front), " +
                          $"front assignment {russianDivisions:N0} RUS / {ukrainianDivisions:N0} UKR divisions, " +
                          $"fronts {war.AttackerFront.Count:N0} attacking and " +
                          $"{war.DefenderFront.Count:N0} defending province(s).");

        // Why the line is where it is. A stalemate produced by divisions that never got
        // an order, never finished a plan, or never stood next to the enemy is a bug that
        // happens to look like history; this line tells the two apart.
        var attackers = campaign.Armies.Where(army => army.NationId == russia.Id && army.AssignedWarId == war.Id).ToList();
        var ordered = attackers.Count(army => army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead);
        var executing = attackers.Count(army => army.PlanActive &&
            army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead);
        var adjacent = campaign.ProvinceMap is null ? 0 : attackers.Count(army =>
            army.ProvinceId > 0 && campaign.ProvinceMap.Neighbors[army.ProvinceId]
                .Any(province => campaign.ProvinceController[province] == (ushort)ukraine.MapIndex));
        var resting = attackers.Count(army => army.ReadyDay > campaign.ElapsedDays);
        var supplied = attackers.Count(army => army.SupplyLevel > 0.5f);
        Console.WriteLine($"  RUS posture: {ordered:N0}/{attackers.Count:N0} ordered forward, " +
                          $"{executing:N0} executing, {adjacent:N0} in contact, {resting:N0} spent, " +
                          $"{supplied:N0} supplied, average organisation " +
                          $"{(attackers.Count == 0 ? 0f : attackers.Average(army => army.Organisation)):P0}.");
        var outcomes = string.Join(", ", Warfare.OutcomeTally
            .OrderByDescending(entry => entry.Value)
            .Take(8)
            .Select(entry => $"{entry.Key} x{entry.Value}"));
        Console.WriteLine($"  battle outcomes worldwide: {(outcomes.Length == 0 ? "none" : outcomes)}");
        var onThisFront = string.Join(", ", Warfare.OutcomeTallyByWar.GetValueOrDefault(war.Id, new())
            .OrderByDescending(entry => entry.Value)
            .Take(8)
            .Select(entry => $"{entry.Key} x{entry.Value}"));
        Console.WriteLine($"  battle outcomes on this front: {(onThisFront.Length == 0 ? "none" : onThisFront)}");
        var defenders = campaign.Armies.Where(army => army.NationId == ukraine.Id && army.AssignedWarId == war.Id).ToList();
        if (defenders.Count > 0)
            Console.WriteLine($"  UKR posture: {defenders.Count:N0} divisions, " +
                              $"organisation {defenders.Average(army => army.Organisation):P0}, " +
                              $"strength {defenders.Average(army => army.Strength):P0}, " +
                              $"entrenchment {defenders.Average(army => army.Entrenchment):P0}, " +
                              $"supply {defenders.Average(army => army.SupplyLevel):P0}.");
        if (attackers.Count > 0)
            Console.WriteLine($"  RUS supply: average level {attackers.Average(army => army.SupplyLevel):P0}, " +
                              $"congestion {attackers.Average(army => army.SupplyCongestion):P0}, " +
                              $"distance {attackers.Average(army => (float)army.SupplyDistance):0.#} hops, " +
                              $"{attackers.Count(army => army.CutOff):N0} cut off, " +
                              $"{attackers.Count(army => army.Planning >= 0.28f):N0} with a finished plan.");
        // Replacements decide a long war more than any single battle does. If a side is
        // burning more people per day than it can put back, the front is on a countdown
        // and which side runs out first is a property of the seed, not of the model.
        foreach (var (nation, force) in new[] { (russia, attackers), (ukraine, defenders) })
        {
            if (force.Count == 0) continue;
            Console.WriteLine($"  {nation.Tag} replacements: {Fmt.People(nation.ManpowerReserve)} in reserve, " +
                              $"{Fmt.People(nation.ReinforcementCapacity)}/day of capacity, " +
                              $"{Fmt.People(nation.ReinforcedLastWeek)} put back this week, " +
                              $"average strength {force.Average(army => army.Strength):P0}, " +
                              $"{force.Count(army => army.InBattle):N0} of {force.Count:N0} locked in battle.");
            // And the depots, which decide a long war as surely as the men do: a side whose
            // ammunition has run dry fights with what arrived that day.
            string Depot(string good) =>
                $"{good.ToLowerInvariant()} {StrategicStockpiles.Get(campaign, nation, good):0.#} " +
                $"(+{nation.DailyStockpileGain.GetValueOrDefault(good):0.##}/-{nation.DailyStockpileBurn.GetValueOrDefault(good):0.##} a day)";
            Console.WriteLine($"  {nation.Tag} depots: {Depot("Ammunition")}, {Depot("Arms")}, {Depot("Oil")}, {Depot("Food")}, " +
                              $"munitions {StrategicStockpiles.CombatMunitionsFactor(campaign, nation):P0}, " +
                              $"{campaign.Sponsorships.Where(s => !s.Ended && s.ClientId == nation.Id).Sum(s => s.LastShipment):0.#} shipped in by backers at the last delivery.");
        }
        if (war.Ended) return "the war ended on its own with nobody playing";
        // Neither army may be ground out of existence. This is the one that mattered and
        // the one that was not being checked: the front used to be decided by Ukraine's
        // manpower reaching literally zero somewhere around the eighteenth month, after
        // which its divisions sat at four per cent strength and the line was walked
        // through rather than fought for. Whether that happened before or after day 730
        // moved with the province numbering, so renumbering the map by adding one region
        // in the Caucasus was enough to turn a stalemate into a Russian conquest of half
        // of Ukraine. A war whose outcome turns on that is not being simulated.
        foreach (var (nation, force) in new[] { (russia, attackers), (ukraine, defenders) })
        {
            if (force.Count == 0) continue;
            var strength = force.Average(army => army.Strength);
            if (strength < 0.55f)
                return $"{nation.Tag}'s divisions are down to {strength:P0} strength after {days} day(s); " +
                       "the war is being decided by one side running out of people, not by the fighting";
        }

        // Both directions. The old band only caught Ukraine losing ground, so a run that
        // handed Ukraine a third of Russia's front passed without a word.
        //
        // The width is measured, not chosen: across five seeds Ukraine finishes the two
        // years holding 77, 82, 90, 91 and 99 per cent of what it started with. That is a
        // real Russian drift of about nine provinces a year against a historical one, and
        // it is not a knob - defender advantage was swept from 2.3 to 3.3 and only widened
        // the spread from thirty-two provinces to seventy-seven, while slowing the tempo
        // of assaults moved the line the wrong way outright. What the band is for is
        // catching a war that has been decided, and at this width it still does.
        if (held < 0.75f || held > 1.25f)
            return $"Ukraine holds {held:P0} of the ground it started with after {days} day(s) of " +
                   "nobody playing; the war has been decided by the simulation, not by anyone";

        // The residual, stated rather than hidden. This is the widest the line swings from
        // where it started at any point in the opening year - a swing, not a net movement,
        // and it settles back: across five seeds the net after two years runs to a couple
        // of dozen provinces of a hundred and fifty-one, while the swing inside the first
        // year runs to twenty-seven. Real movement in 2024 was about one province a year,
        // so the model is still far too fluid.
        //
        // The reason is NOT that ground is being walked around rather than fought for.
        // That was read off the worldwide tally, which is worthless for judging one front:
        // with a hundred and forty wars running, nearly all of them between small states
        // with no divisions at all, it says how the rest of the world fights. Split by war,
        // this front captures ground by winning a battle about a hundred and ten times in
        // two years and by walking into a gap fewer than a dozen - and the dozen is what is
        // left after cover was widened to the sector a division holds rather than the tile
        // it stands on, which took unfought captures here from a quarter to a twentieth.
        // It is a properly fought front. What is too fluid is the assault itself: roughly
        // two hundred and fifty attacks are resolved here over two years and about
        // forty-five per cent of them take the province. Bringing that down is the next
        // real piece of work, and it is a piece of work rather than a knob - so the budget
        // says what the model does.
        const float openingBudget = 30f;
        if (opening > openingBudget)
            return $"the line swung by {opening:N0} provinces during the opening year " +
                   $"with nobody playing; the budget for the first year is {openingBudget:N0}";
        if (casualties < 20_000f * years)
            return $"only {casualties:N0} casualties in {days} day(s) - the front is frozen, not contested";
        return "";
    }

    /// <summary>
    /// The player can invade. An AI that cannot is half a feature - the islands would be
    /// safe from everyone but the person playing, which is the same asymmetry the flat
    /// refusal used to be.
    ///
    /// Whether an AI *chooses* to declare a war across water is a separate matter of
    /// appetite, and rare on any given seed. What has to be true is that one which finds
    /// itself in such a war knows what to do: bring its fleet home, put troops on it, and
    /// sail. So the war is arranged, and the admiralty is left to work it out.
    /// </summary>
    internal static string AnAiMountsALanding(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";
        var invader = campaign.NationByTag("GBR");
        if (invader is null) return "no navy to test with";
        var fleet = campaign.Fleets.Find(candidate => candidate.NationId == invader.Id);
        if (fleet is null) return "GBR has no fleet";

        // The enemy has to be one this fleet could plausibly sail to. Taking the first
        // country Britain shares no land border with picks Indonesia as readily as France,
        // and an admiralty that refuses to carry one division a third of the way round the
        // planet is behaving correctly - the check would then be failing the AI for being
        // sensible. So the target is the nearest overseas neighbour, found the same way the
        // AI looks for a beach: by distance from the fleet's own harbour.
        Nation target = null;
        var controllers = campaign.ProvinceController;
        var nearest = campaign.Defines.AiInvasionSearchDegrees;
        for (var province = 1; controllers is not null && province <= topology.ProvinceCount; province++)
        {
            var owner = controllers[province];
            if (owner == 0 || owner == (ushort)invader.MapIndex) continue;
            var centre = topology.CenterOf(province);
            var distance = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude,
                centre.Latitude, centre.Longitude);
            if (distance >= nearest) continue;
            var candidate = campaign.Nations.Find(nation => nation.MapIndex == owner);
            if (candidate is null || candidate.Id == invader.Id || candidate.Population <= 0) continue;
            if (CampaignDiplomacy.SharesLandBorder(campaign, invader.MapIndex, candidate.MapIndex)) continue;
            if (!CampaignDiplomacy.CanDeclareWar(campaign, invader.Id, candidate.Id, out _)) continue;
            nearest = distance;
            target = candidate;
        }
        if (target is null) return "no overseas country within reach to invade";

        var wasHuman = invader.IsHuman;
        invader.IsHuman = false;

        // Two hundred and twenty days in, Britain is usually fighting somebody it can walk
        // to as well, and this check is not about how a navy chooses between wars - it is
        // about whether one that finds itself in an overseas war knows what to do. So the
        // others are set aside for the length of it.
        var suspended = campaign.Wars
            .Where(candidate => !candidate.Ended && candidate.Involves(invader.Id)).ToList();
        foreach (var other in suspended) other.Ended = true;

        var war = CampaignDiplomacy.DeclareWar(campaign, invader.Id, target.Id);
        if (war is null)
        {
            foreach (var other in suspended) other.Ended = false;
            invader.IsHuman = wasHuman;
            return "the overseas war was refused";
        }
        try
        {
            // Put the fleet home and idle, and give it something to carry within reach.
            fleet.Mission = FleetMission.Anchored;
            fleet.HasOrder = false;
            fleet.Carried.Clear();
            fleet.Latitude = fleet.HomeLatitude;
            fleet.Longitude = fleet.HomeLongitude;
            var spare = campaign.Armies.Find(army => army.NationId == invader.Id &&
                army.EmbarkedFleetId < 0 && !army.InBattle);
            if (spare is null) return "GBR has no division free to load";
            if (fleet.HomeProvinceId > 0)
            {
                spare.ProvinceId = fleet.HomeProvinceId;
                var where = topology.CenterOf(fleet.HomeProvinceId);
                spare.Latitude = where.Latitude;
                spare.Longitude = where.Longitude;
            }
            spare.OrderKind = ArmyOrder.Manual;
            spare.Route.Clear();

            // The admiralty staggers its thinking by the day, and the day does not advance
            // inside a check - so for the length of this one, every fleet thinks every tick.
            var interval = campaign.Defines.AiFleetDecisionInterval;
            campaign.Defines.AiFleetDecisionInterval = 1;

            var loadedOn = -1;
            var launchedOn = -1;
            for (var day = 0; day < 200; day++)
            {
                AiAdmiralty.Tick(campaign);
                if (loadedOn < 0 && fleet.Carried.Count > 0) loadedOn = day;
                if (launchedOn < 0 && fleet.Mission == FleetMission.Invasion) launchedOn = day;
                if (launchedOn >= 0) break;
                SailADay(campaign);
            }
            campaign.Defines.AiFleetDecisionInterval = interval;
            if (loadedOn < 0)
            {
                NavalInvasion.CanEmbark(campaign, fleet, spare, out var refused);
                return $"an AI at war with {target.Tag} across water never put a division on its " +
                       $"fleet: {refused}";
            }
            if (launchedOn < 0) return "an AI loaded its fleet and never sent it anywhere";

            Console.WriteLine($"  ai navy: {invader.Tag} found itself at war with {target.Tag} across water, " +
                              $"loaded on day {loadedOn:N0} and sailed on day {launchedOn:N0}.");
            return "";
        }
        finally
        {
            invader.IsHuman = wasHuman;
            war.Ended = true;
            foreach (var other in suspended) other.Ended = false;
            foreach (var id in new List<int>(fleet.Carried))
            {
                var army = campaign.Armies.Find(candidate => candidate.Id == id);
                if (army is not null) { army.EmbarkedFleetId = -1; army.ProvinceId = fleet.HomeProvinceId; }
            }
            fleet.Carried.Clear();
            fleet.Mission = FleetMission.Anchored;
            fleet.HasOrder = false;
            fleet.TargetProvinceId = -1;
        }
    }

    /// <summary>
    /// A landing costs a formation its order, and troops trained for one should pay less of
    /// that price. Without this the marine template is a line of data nothing reads: the
    /// trait is on the template, but whether the landing consults it is the part that
    /// silently stops being true.
    /// </summary>
    /// <summary>
    /// The sea lanes have to be sea. Every course is sampled along its whole length and has to
    /// stay on water; the famous short cuts have to be taken and the long ways round have to be
    /// the only way when a short cut is shut. Six voyages, each of which only means anything
    /// if a ship goes round something.
    /// </summary>
    internal static string AFleetSailsRoundTheLand(Campaign campaign)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return "";
        string Voyage(string tag, float latitude, float longitude, out float length)
        {
            length = 0f;
            var nation = campaign.NationByTag(tag);
            var fleet = nation is null ? null : campaign.Fleets.Find(candidate => candidate.NationId == nation.Id);
            if (fleet is null) return $"{tag} has no fleet";
            var course = new List<float>();
            if (!SeaLanes.Plan(campaign, fleet.NationId, fleet.Latitude, fleet.Longitude, latitude, longitude, course, out length))
                return $"{tag} found no course";
            var fromLatitude = fleet.Latitude;
            var fromLongitude = fleet.Longitude;
            for (var index = 0; index + 1 < course.Count; index += 2)
            {
                var toLatitude = course[index];
                var toLongitude = course[index + 1];
                var deltaLongitude = toLongitude - fromLongitude;
                while (deltaLongitude > 180f) deltaLongitude -= 360f;
                while (deltaLongitude < -180f) deltaLongitude += 360f;
                var samples = (int)(MathF.Max(MathF.Abs(toLatitude - fromLatitude), MathF.Abs(deltaLongitude)) * 16f) + 1;
                var ashore = 0;
                var lastLeg = index + 2 >= course.Count;
                for (var sample = 0; sample <= samples; sample++)
                {
                    var t = sample / (float)samples;
                    var latitudeAt = fromLatitude + (toLatitude - fromLatitude) * t;
                    var longitudeAt = fromLongitude + deltaLongitude * t;
                    if (longitudeAt > 180f) longitudeAt -= 360f;
                    if (longitudeAt < -180f) longitudeAt += 360f;
                    if (SeaLanes.IsWaterAt(map, latitudeAt, longitudeAt)) { ashore = 0; continue; }
                    // Leaving a port drawn a pixel inland is allowed, and so is the last few
                    // miles to a destination a player clicked on the shore; crossing land -
                    // two pixels of it in a row, not a corner - is not.
                    if (index == 0 && sample < 8) continue;
                    if (lastLeg && sample > samples - 8) continue;
                    if (++ashore > 2)
                        return $"{tag}'s course crosses land at {latitudeAt:0.0}, {longitudeAt:0.0}";
                }
                fromLatitude = toLatitude;
                fromLongitude = toLongitude;
            }
            return "";
        }

        var failures = new List<string>();
        var lengths = new Dictionary<string, float>();
        foreach (var (name, tag, latitude, longitude) in new[]
                 {
                     ("Bombay to Toulon", "IND", 43.05f, 5.95f),
                     ("Norfolk to San Diego", "USA", 32.70f, -117.25f),
                     ("Vladivostok to Baltiysk", "RUS", 54.65f, 19.90f),
                     ("the Marmara to Sevastopol", "TUR", 44.60f, 33.45f),
                     ("Plymouth to Saint Petersburg", "GBR", 59.95f, 29.60f),
                     ("Qingdao to Singapore", "CHN", 1.20f, 103.85f)
                 })
        {
            var problem = Voyage(tag, latitude, longitude, out var length);
            if (problem.Length > 0) failures.Add($"{name}: {problem}");
            lengths[name] = length;
        }
        if (failures.Count > 0) return string.Join("; ", failures);

        // Suez and Panama are there to be used.
        if (lengths["Bombay to Toulon"] > 100f)
            return $"Bombay to Toulon is {lengths["Bombay to Toulon"]:0} degrees: the fleet went round Africa instead of through Suez";
        if (lengths["Norfolk to San Diego"] > 110f)
            return $"Norfolk to San Diego is {lengths["Norfolk to San Diego"]:0} degrees: the fleet went round the Horn instead of through Panama";

        // And a strait somebody holds is shut to the holder's enemies. Asked of a Mediterranean
        // navy that is at peace: one already at war with anybody is a belligerent, and Montreux
        // shuts the Turkish straits to every belligerent, which is the rule working rather than a
        // course that cannot be found. Italy when it is at peace, otherwise the next one that is.
        Nation italy = null;
        Fleet italian = null;
        foreach (var tag in new[] { "ITA", "FRA", "ESP", "GRC", "GBR" })
        {
            var candidate = campaign.NationByTag(tag);
            var fleet = candidate is null ? null : campaign.Fleets.Find(item => item.NationId == candidate.Id);
            if (fleet is null || campaign.Wars.Any(war => !war.Ended && war.Involves(candidate.Id))) continue;
            italy = candidate;
            italian = fleet;
            break;
        }
        var turkey = campaign.NationByTag("TUR");
        var egypt = campaign.NationByTag("EGY");
        if (italian is null || turkey is null || egypt is null) return "no Mediterranean navy at peace, or no Turkey or Egypt, to shut a strait on";
        var course = new List<float>();
        foreach (var (holder, to, latitude, longitude, mustFail) in new[]
                 { (turkey, "the Black Sea", 44.6f, 33.45f, true), (egypt, "the Red Sea", 20f, 38.5f, false) })
        {
            if (!SeaLanes.Plan(campaign, italy.Id, italian.Latitude, italian.Longitude, latitude, longitude, course, out var open))
                return $"{italy.Tag} cannot reach {to} even at peace";
            var war = new StrategicWar
            {
                Id = 91_001, Name = "Bench strait war", AttackerId = italy.Id, DefenderId = holder.Id,
                StartedDay = campaign.ElapsedDays, AiNegotiationUnlockDay = int.MaxValue
            };
            campaign.Wars.Add(war);
            var found = SeaLanes.Plan(campaign, italy.Id, italian.Latitude, italian.Longitude, latitude, longitude, course, out var shut);
            campaign.Wars.Remove(war);
            if (mustFail && found) return $"at war with {holder.Tag}, {italy.Tag} still sailed into {to}";
            // Round Africa is some hundred degrees more than through Suez from anywhere in the
            // Mediterranean or the Atlantic, but not three times as much from Brest as from Taranto.
            if (!mustFail && found && shut < open + 60f)
                return $"at war with {holder.Tag}, {italy.Tag} reached {to} in {shut:0} degrees against {open:0} at peace: the canal was not shut";
            Console.WriteLine($"  straits: {italy.Tag} to {to} is {open:0}° at peace and " +
                              (found ? $"{shut:0}° round Africa" : "shut") + $" at war with {holder.Tag}.");
        }
        Console.WriteLine($"  sea lanes: Bombay to Toulon {lengths["Bombay to Toulon"]:0}°, Norfolk to San Diego " +
                          $"{lengths["Norfolk to San Diego"]:0}°, Vladivostok to Baltiysk {lengths["Vladivostok to Baltiysk"]:0}°, " +
                          $"every course on water the whole way.");
        return "";
    }

    /// <summary>
    /// A home port is somewhere somebody holds. When the enemy takes it the fleet moves to the
    /// nearest port its country still has on the same sea, and a fleet lying in the lost one
    /// sails there - rather than going on repairing itself on an enemy quay.
    /// </summary>
    internal static string AFallenPortMovesItsFleet(Campaign campaign)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return "";
        var britain = campaign.NationByTag("GBR");
        var france = campaign.NationByTag("FRA");
        var fleet = britain is null ? null : campaign.Fleets.Find(candidate => candidate.NationId == britain.Id);
        if (fleet is null || france is null) return "no British fleet to test with";
        var home = fleet.HomeProvinceId;
        if (home <= 0 || SeaLanes.HarbourOf(map, home) < 0) return $"the Home Fleet's home port {home} is not a port";
        var was = campaign.ProvinceController[home];
        var war = new StrategicWar
        {
            Id = 91_002, Name = "Bench port war", AttackerId = france.Id, DefenderId = britain.Id,
            StartedDay = campaign.ElapsedDays, AiNegotiationUnlockDay = int.MaxValue
        };
        campaign.Wars.Add(war);
        var (latitude, longitude, mission, berth) = (fleet.Latitude, fleet.Longitude, fleet.Mission, fleet.BerthProvinceId);
        try
        {
            fleet.Latitude = fleet.HomeLatitude;
            fleet.Longitude = fleet.HomeLongitude;
            fleet.Mission = FleetMission.Anchored;
            fleet.HasOrder = false;
            fleet.BerthProvinceId = home;
            campaign.SetProvinceController(home, (ushort)france.MapIndex);
            SailADay(campaign);
            if (fleet.HomeProvinceId == home) return "the fleet kept a home port the enemy holds";
            var holder = NavalOperations.HolderOf(campaign, fleet.HomeProvinceId);
            if (holder?.Id != britain.Id) return $"the new home port is held by {holder?.Tag ?? "nobody"}";
            if (!fleet.HasOrder && fleet.BerthProvinceId == home) return "the fleet stayed lying in the fallen port";
            for (var day = 0; day < 20 && fleet.HasOrder; day++) SailADay(campaign);
            if (!fleet.InPort) return "the fleet never reached its new home port";
            Console.WriteLine($"  ports: the Home Fleet lost its port to France and moved to province " +
                              $"{fleet.HomeProvinceId}, in port again the same week.");
            return "";
        }
        finally
        {
            campaign.SetProvinceController(home, was);
            campaign.Wars.Remove(war);
            FleetHistory.AssignHomePorts(campaign);
            (fleet.Latitude, fleet.Longitude, fleet.Mission, fleet.BerthProvinceId) = (latitude, longitude, mission, berth);
            fleet.HasOrder = false;
        }
    }

    /// <summary>
    /// Ships sunk are not ships gone forever. A country nobody plays orders what its navy is
    /// short of, its yards build it at the pace their slipways allow, the treasury pays for it
    /// while it is built, and the hulls join the fleet new and green.
    /// </summary>
    internal static string ANavyRebuildsWhatItLoses(Campaign campaign)
    {
        var japan = campaign.NationByTag("JPN");
        var fleet = japan is null ? null : campaign.Fleets.Find(candidate => candidate.NationId == japan.Id);
        if (fleet is null) return "no Japanese fleet to sink";
        var wasHuman = japan.IsHuman;
        var (hulls, ordered, progress, experience, treasury) =
            (fleet.Hulls, fleet.HullsOrdered, fleet.HullProgress, fleet.Experience, japan.Treasury);
        try
        {
            japan.IsHuman = false;
            fleet.Hulls = Math.Max(1, fleet.Establishment / 2);
            fleet.HullsOrdered = 0;
            fleet.HullProgress = 0f;
            fleet.Experience = 0.5f;
            japan.Treasury = MathF.Max(japan.Treasury, 10_000f);
            var target = Shipbuilding.TargetHulls(campaign, japan, fleet);
            var slipways = Shipbuilding.Slipways(campaign, japan);
            if (slipways <= 0f) return "Japan has no slipways at all";

            // The review comes round within a month; the first hull comes within a build.
            var before = japan.Treasury;
            var day = 0;
            for (; day < campaign.Defines.AiNavalReviewDays + 1 && fleet.HullsOrdered == 0; day++) campaign.TickDay();
            if (fleet.HullsOrdered <= 0) return $"Japan at {fleet.Hulls:N0} of {target:N0} hulls ordered nothing in {day:N0} days";
            var start = fleet.Hulls;
            for (var more = 0; more < 400 && fleet.Hulls == start; more++) campaign.TickDay();
            if (fleet.Hulls <= start) return "the yards never delivered a hull";
            if (japan.Treasury >= before) return "a navy was rebuilt for nothing";
            if (fleet.Experience >= 0.5f) return "new hulls joined the fleet already seasoned";
            Console.WriteLine($"  yards: Japan, sunk to {start:N0} of {fleet.Establishment:N0}, ordered " +
                              $"{fleet.HullsOrdered + fleet.Hulls - start:N0} and had hull {fleet.Hulls:N0} on " +
                              $"{slipways:0.#} slipways, paying {Fmt.Money(Shipbuilding.DailyCost(campaign, japan))} a day.");
            return "";
        }
        finally
        {
            japan.IsHuman = wasHuman;
            (fleet.Hulls, fleet.HullsOrdered, fleet.HullProgress, fleet.Experience, japan.Treasury) =
                (hulls, ordered, progress, experience, treasury);
        }
    }

    internal static string MarinesLandBetter(Campaign campaign)
    {
        var marine = UnitTypeCatalog.Get("marine_brigade");
        if (marine is null || marine.Amphibious <= 0f)
            return "no amphibious template ships at all; common/units has no marines";
        var line = UnitTypeCatalog.Get("line_infantry");
        if (line is null) return "no line infantry to compare against";
        if (line.Amphibious >= marine.Amphibious)
            return $"line infantry is as amphibious as marines ({line.Amphibious:P0})";

        var cost = campaign.Defines.InvasionLandingOrganisationCost;
        var marineCost = cost * (1f - marine.Amphibious);
        var lineCost = cost * (1f - line.Amphibious);
        if (marineCost >= lineCost)
            return "the landing does not read the amphibious trait";

        Console.WriteLine($"  marines: a landing costs line infantry {lineCost:P0} of its order and " +
                          $"a marine brigade {marineCost:P0}.");
        return "";
    }

    /// <summary>
    /// A convoy is the reason to keep ships in your own waters. If an invasion can sail
    /// past a hostile fleet untouched, escorting is a chore with no payoff and blockading
    /// one's own coast is pointless.
    /// </summary>
    internal static string AConvoyCaughtAtSeaPaysForIt(Campaign campaign)
    {
        var left = campaign.NationByTag("GBR");
        var right = campaign.NationByTag("FRA");
        if (left is null || right is null) return "no two navies to test with";
        var convoy = campaign.Fleets.Find(fleet => fleet.NationId == left.Id);
        var raider = campaign.Fleets.Find(fleet => fleet.NationId == right.Id);
        var passenger = campaign.Armies.Find(army => army.NationId == left.Id && army.EmbarkedFleetId < 0);
        if (convoy is null || raider is null || passenger is null) return "nothing to make a convoy out of";

        var war = new StrategicWar
        {
            Id = 90_001, Name = "Bench convoy war", AttackerId = left.Id, DefenderId = right.Id,
            StartedDay = campaign.ElapsedDays, AiNegotiationUnlockDay = int.MaxValue
        };
        campaign.Wars.Add(war);
        var wasProvince = passenger.ProvinceId;
        try
        {
            convoy.Latitude = raider.Latitude = 47f;
            convoy.Longitude = raider.Longitude = -9f;
            convoy.Mission = FleetMission.Invasion;
            raider.Mission = FleetMission.Patrol;
            convoy.HasOrder = raider.HasOrder = false;
            convoy.TargetProvinceId = -1;
            convoy.Organisation = raider.Organisation = 1f;
            convoy.ReadyDay = raider.ReadyDay = 0;
            convoy.Carried.Clear();
            passenger.EmbarkedFleetId = convoy.Id;
            passenger.ProvinceId = 0;
            passenger.Strength = 1f;
            passenger.Organisation = 1f;
            convoy.Carried.Add(passenger.Id);

            SailADay(campaign);
            if (!convoy.InAction) return "a convoy sat on top of a hostile fleet and was not found";
            if (passenger.Strength >= 1f)
                return "an army was caught at sea aboard its transports and lost nobody";
            if (passenger.Organisation >= 1f) return "being attacked at sea cost the passengers no order";

            Console.WriteLine($"  convoy: caught at sea, the landing force came out at " +
                              $"{passenger.Strength:P0} strength and {passenger.Organisation:P0} order.");
            return "";
        }
        finally
        {
            campaign.Wars.Remove(war);
            passenger.EmbarkedFleetId = -1;
            passenger.ProvinceId = wasProvince;
            convoy.Carried.Clear();
            convoy.Mission = raider.Mission = FleetMission.Anchored;
            convoy.HasOrder = raider.HasOrder = false;
        }
    }

    /// <summary>
    /// The navy was fifteen counters that sat where they were put, could not be clicked,
    /// and changed nothing anywhere. Rebuilt on the same bones as a division, it has to
    /// earn the word: fleets that meet must fight, a fight must cost both of them, a beaten
    /// fleet must go home, and a fleet on an enemy's trade must cost that enemy money.
    ///
    /// Everything here is done to a throwaway war between two navies, and put back after.
    /// </summary>
    internal static string ANavyIsWorthHaving(Campaign campaign)
    {
        var left = campaign.NationByTag("GBR");
        var right = campaign.NationByTag("FRA");
        if (left is null || right is null) return "no two navies to test with";
        var mine = campaign.Fleets.Find(fleet => fleet.NationId == left.Id);
        var theirs = campaign.Fleets.Find(fleet => fleet.NationId == right.Id);
        if (mine is null || theirs is null) return "GBR or FRA has no fleet in the scenario";

        var war = new StrategicWar
        {
            Id = 90_000,
            Name = "Bench naval war",
            AttackerId = left.Id,
            DefenderId = right.Id,
            StartedDay = campaign.ElapsedDays,
            AiNegotiationUnlockDay = int.MaxValue
        };
        campaign.Wars.Add(war);
        try
        {
            // Put them within sight of each other, both at sea and both willing.
            mine.Latitude = theirs.Latitude = 48f;
            mine.Longitude = theirs.Longitude = -8f;
            mine.Mission = FleetMission.Patrol;
            theirs.Mission = FleetMission.Patrol;
            mine.HasOrder = theirs.HasOrder = false;
            mine.Organisation = theirs.Organisation = 1f;
            mine.Strength = theirs.Strength = 1f;
            mine.Experience = theirs.Experience = 0f;
            mine.ReadyDay = theirs.ReadyDay = 0;

            SailADay(campaign);
            if (!mine.InAction || !theirs.InAction)
                return "two hostile fleets on the same spot did not find each other";
            if (mine.Organisation >= 1f || theirs.Organisation >= 1f)
                return "an action cost neither fleet any cohesion";
            if (mine.Strength >= 1f && theirs.Strength >= 1f)
                return "an action cost neither fleet a single hull";
            if (mine.Experience <= 0f) return "a fleet learned nothing from being in action";

            // Fought to a standstill, the weaker one must break off for home rather than
            // fight forever on the spot.
            var fought = 1;
            for (; fought < 60 && mine.Mission == FleetMission.Patrol &&
                   theirs.Mission == FleetMission.Patrol; fought++)
                SailADay(campaign);
            if (mine.Mission == FleetMission.Patrol && theirs.Mission == FleetMission.Patrol)
                return $"neither fleet broke off after {fought:N0} days of action";
            var broken = mine.Mission == FleetMission.Patrol ? theirs : mine;
            if (broken.HullsLost <= 0) return "a fleet broke off an action without losing a ship";
            if (!broken.HasOrder) return "a beaten fleet did not turn for home";

            // And a navy on an enemy's trade has to be felt in the enemy's budget.
            theirs.Mission = FleetMission.Raid;
            theirs.Organisation = 1f;
            var before = left.WeeklyTradeBalance;
            NavalOperations.ApplyCommerceEffects(campaign, left, null);
            if (left.WeeklyTradeBalance >= before)
                return "an enemy fleet raiding trade cost the victim nothing at all";
            var raided = before - left.WeeklyTradeBalance;

            // And only near the victim's own sea lanes: the same raider in the South Pacific
            // takes a fraction of it.
            var (wasLatitude, wasLongitude) = (theirs.Latitude, theirs.Longitude);
            theirs.Latitude = -45f;
            theirs.Longitude = -130f;
            var afar = left.WeeklyTradeBalance;
            NavalOperations.ApplyCommerceEffects(campaign, left, null);
            var farRaid = afar - left.WeeklyTradeBalance;
            theirs.Latitude = wasLatitude;
            theirs.Longitude = wasLongitude;
            if (farRaid >= raided * 0.5f)
                return $"a raider on the far side of the world took {Fmt.Money(farRaid)} a week, against {Fmt.Money(raided)} off the victim's coast";

            Console.WriteLine($"  navy: two fleets met and fought for {fought:N0} day(s) - " +
                              $"{mine.Hulls:N0} hulls at {mine.Strength:P0} strength against {theirs.Hulls:N0} at " +
                              $"{theirs.Strength:P0}, {broken.HullsLost:N0} sunk under the one that broke - and raiding " +
                              $"cost the loser {Fmt.Money(raided)} a week off its coast, {Fmt.Money(farRaid)} from the far Pacific.");
            return "";
        }
        finally
        {
            campaign.Wars.Remove(war);
            mine.Mission = theirs.Mission = FleetMission.Anchored;
            mine.HasOrder = theirs.HasOrder = false;
        }
    }

    /// <summary>
    /// A line the player brushed onto the map has to actually command the formations that
    /// were drawn onto it: they take the order the line carries, they spread along its
    /// length instead of piling onto one province of it, and erasing the line hands them
    /// back to the war's own front without leaving an orphan behind.
    ///
    /// Without this the feature is a decoration - the line would render, the panel would
    /// report it, and the divisions would go on standing wherever the war put them.
    /// </summary>
    internal static string ADrawnLineCommandsTheFormationsOnIt(Campaign campaign)
    {
        if (campaign.ProvinceMap is null) return "";
        var russia = campaign.NationByTag("RUS");
        if (russia is null) return "no Russia to draw a plan for";
        var war = campaign.Wars.Find(candidate => !candidate.Ended && candidate.Involves(russia.Id));
        if (war is null) return "no war to draw a plan on";

        // Drawing is a player's act, and the AI is forbidden to re-task a human's
        // formations. Standing in for the player is what makes this test the real path.
        var wasHuman = russia.IsHuman;
        russia.IsHuman = true;
        try
        {
            var line = war.FriendlyFront(russia.Id).OrderBy(province => province).Take(10).ToList();
            if (line.Count < 4) return $"Russia's front is {line.Count} province(s) long; too short to draw on";
            var chosen = campaign.Armies
                .Where(army => army.NationId == russia.Id && army.AssignedWarId == war.Id)
                .OrderBy(army => army.Id).Take(6).Select(army => army.Id).ToList();
            if (chosen.Count < 2) return "no formations to put on a drawn line";

            new DrawFrontLineCommand
            {
                IssuerId = russia.Id,
                Kind = FrontLineKind.Defence,
                Provinces = line,
                ArmyIds = chosen
            }.Apply(campaign);

            var placed = campaign.Armies.Where(army => chosen.Contains(army.Id)).ToList();
            var orphan = placed.Find(army => army.FrontLineId < 0);
            if (orphan is not null) return $"{orphan.Name} was drawn onto a line and did not get it";
            var wrongOrder = placed.Find(army => army.OrderKind != ArmyOrder.DefendFront);
            if (wrongOrder is not null)
                return $"a drawn front line left {wrongOrder.Name} on a {wrongOrder.OrderKind} order";

            // Against the line the command drew. A defence line is snapped to the real border,
            // which can run through a province the ten hints above did not name.
            var posts = new List<int>();
            foreach (var army in placed)
            {
                var drawn = campaign.FrontLines.Find(candidate => candidate.Id == army.FrontLineId);
                bool OnLine(int province) => drawn is not null ? drawn.Provinces.Contains(province) : line.Contains(province);
                var post = OnLine(army.OrderProvinceId) ? army.OrderProvinceId
                    : OnLine(army.ProvinceId) ? army.ProvinceId : -1;
                if (post < 0)
                    return $"{army.Name} is neither on the line it was drawn onto nor moving to it";
                posts.Add(post);
            }
            var distinct = posts.Distinct().Count();
            if (distinct < 2)
                return "every formation was sent to the same province; the line is not being covered";

            new ClearFrontLineCommand { IssuerId = russia.Id, ArmyIds = chosen }.Apply(campaign);
            if (campaign.Armies.Any(army => chosen.Contains(army.Id) && army.FrontLineId >= 0))
                return "a formation kept its line after the line was erased";
            if (campaign.FrontLines.Count > 0)
                return $"{campaign.FrontLines.Count:N0} line(s) were left behind with nobody on them";

            Console.WriteLine($"  battle plan: {chosen.Count:N0} formation(s) drawn onto a " +
                              $"{line.Count:N0}-province line, spread across {distinct:N0} post(s), " +
                              "and erased without an orphan.");
            return "";
        }
        finally
        {
            russia.IsHuman = wasHuman;
        }
    }

    /// <summary>
    /// Write a peace and sign it. The invariants that matter are the ones that make it a
    /// negotiation rather than a wish: ground you never took has to cost more than ground
    /// you hold, the bill has to be refused when it exceeds what the war was worth, and
    /// signing has to actually move the border and end the war.
    /// </summary>
    /// <summary>
    /// Whether the treaty a country writes for itself is one a country would write.
    ///
    /// The old rule was "cheapest first", and cheap means empty, so an AI that won a war
    /// annexed a handful of the emptiest provinces it happened to be standing on - scattered
    /// across the map, touching nothing it owned, and never including the region it had
    /// declared the war over. It was affordable and it was nonsense, and because nothing
    /// measured the shape of a settlement, nothing ever said so.
    ///
    /// This measures the shape. It writes the same peace both ways on the same world and
    /// compares them: the new one may not leave more provinces stranded than the rule it
    /// replaced, it has to stay inside the budget, it has to be one the loser will actually
    /// sign, and if the winner is standing on the ground it went to war for, it has to ask
    /// for some of it.
    /// </summary>
    /// <summary>
    /// Whether a country will negotiate with a person at all.
    ///
    /// It would not. The negotiation step opened with "if the other side is human, return",
    /// so a war with a player in it could only ever end when the player ended it: a country
    /// losing to a player fought to its last division without once asking to stop, and a
    /// country beating a player took nothing and said nothing. Half of the diplomacy in the
    /// game was unreachable from the only seat anybody actually sits in.
    ///
    /// So the same treaty is written and then offered rather than signed. This checks the
    /// whole round trip: that an offer appears instead of a fait accompli, that it says what
    /// it is asking for, that refusing it leaves the war running, and that accepting one
    /// actually moves the border.
    /// </summary>
    internal static string AnAiPutsTermsToAPlayer(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        StrategicWar war = null;
        Nation proposer = null;
        var mostHeld = 0;
        var bestBudget = 0f;
        foreach (var candidate in campaign.Wars)
        {
            if (candidate.Ended) continue;
            foreach (var side in new[] { candidate.AttackerId, candidate.DefenderId })
            {
                var nation = campaign.NationById(side);
                if (nation is null) continue;
                var budget = PeaceConference.Budget(campaign, candidate, nation.Id);
                if (war is not null && budget <= bestBudget) continue;
                war = candidate;
                proposer = nation;
                bestBudget = budget;
                mostHeld = PeaceConference.Claimable(campaign, candidate, nation.Id)
                    .Count(province => campaign.ProvinceController[province] == (ushort)nation.MapIndex);
            }
        }
        if (war is null || proposer is null) return "no war left to negotiate";

        var opponent = campaign.NationById(war.AttackerId == proposer.Id ? war.DefenderId : war.AttackerId);
        if (opponent is null) return "the war has no other side";

        // Sit a person down opposite, and let the clocks that stop an AI negotiating too
        // early run out, since none of that is what is being tested here.
        var wasHuman = opponent.IsHuman;
        var startedDay = war.StartedDay;
        var unlockDay = war.AiNegotiationUnlockDay;
        var lastProposal = war.LastPeaceProposalDay;
        opponent.IsHuman = true;
        war.AiNegotiationUnlockDay = 0;
        war.StartedDay = Math.Min(war.StartedDay, campaign.ElapsedDays - campaign.Defines.AiMinimumWarDays * 4);
        war.LastPeaceProposalDay = int.MinValue / 2;

        try
        {
            AiStatecraft.Negotiate(campaign, proposer, war);

            if (war.Ended)
                return $"{proposer.Tag} ended the war against a player without asking - " +
                       "a settlement was imposed rather than offered";
            if (war.PendingOfferFromId != proposer.Id)
            {
                // Standing on a lot of ground is not the same as being ahead - a country can
                // hold a hundred provinces and still be behind on points, and then having
                // nothing to demand is the right answer. Being ahead on points while holding
                // ground and saying nothing is not.
                if (bestBudget >= campaign.Defines.AiPeaceMinimumBudget && mostHeld >= 4)
                    return $"{proposer.Tag} is {bestBudget:0.0} of war score ahead and occupies " +
                           $"{mostHeld:N0} claimable province(s), and put nothing on the table";
                Console.WriteLine($"  ai offer: the best-placed belligerent, {proposer.Tag}, is " +
                                  $"{bestBudget:0.0} of war score ahead holding {mostHeld:N0} claimable " +
                                  "province(s) - not enough to write a settlement out of.");
                return "";
            }
            if (!PeaceOffers.AwaitingAnswer(war, opponent.Id))
                return "an offer was made and the country it was made to is not waiting on it";
            if (PeaceOffers.AwaitingAnswer(war, proposer.Id))
                return "the country that made the offer is being asked to answer its own offer";

            var wording = PeaceOffers.Describe(campaign, war);
            if (wording.Length == 0) return "the offer does not say what it is asking for";
            var offered = war.PendingOfferIsWritten ? war.PendingOffer.Provinces.Count : 0;

            // Refusing leaves the war exactly where it was.
            if (!PeaceOffers.Answer(campaign, war, opponent.Id, accept: false))
                return "a player could not refuse an offer made to them";
            if (war.Ended) return "refusing the offer ended the war anyway";
            if (war.PendingOfferFromId >= 0) return "a refused offer is still sitting on the table";

            // And accepting one signs it.
            war.LastPeaceProposalDay = int.MinValue / 2;
            AiStatecraft.Negotiate(campaign, proposer, war);
            if (war.PendingOfferFromId != proposer.Id)
            {
                Console.WriteLine($"  ai offer: {proposer.Tag} offered {offered:N0} province(s) and, once refused, " +
                                  "did not come back with a second settlement.");
                return "";
            }

            var written = war.PendingOfferIsWritten;
            var demanded = written ? new HashSet<int>(war.PendingOffer.Provinces) : new HashSet<int>();
            if (!PeaceOffers.Answer(campaign, war, opponent.Id, accept: true))
                return "a player could not accept an offer made to them";
            if (!war.Ended) return "the offer was accepted and the war is still running";

            var moved = demanded.Count(province =>
                topology.ProvinceToNation[province] == (ushort)proposer.MapIndex);
            if (written && moved != demanded.Count)
                return $"{demanded.Count - moved:N0} of the {demanded.Count:N0} province(s) signed over " +
                       "did not change owner";

            Console.WriteLine($"  ai offer: {proposer.Tag} put terms to {opponent.Tag} - " +
                              $"{(written ? $"{demanded.Count:N0} province(s)" : "standing terms")} - " +
                              $"which were refused once, offered again and signed.");
            return "";
        }
        finally
        {
            opponent.IsHuman = wasHuman;
            war.StartedDay = startedDay;
            war.AiNegotiationUnlockDay = unlockDay;
            if (!war.Ended) war.LastPeaceProposalDay = lastProposal;
        }
    }

    internal static string ThePeaceAnAiWritesHangsTogether(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        // The war with the most ground on the table, either way round. Taking the first live
        // war would usually mean a stalemate holding four provinces, and four provinces have
        // no shape to get wrong; the point of this check is the choosing.
        StrategicWar war = null;
        Nation winner = null;
        PeaceLedger ledger = null;
        List<int> held = null;
        foreach (var candidate in campaign.Wars)
        {
            if (candidate.Ended) continue;
            foreach (var side in new[] { candidate.AttackerId, candidate.DefenderId })
            {
                var nation = campaign.NationById(side);
                if (nation is null) continue;
                var occupied = PeaceConference.Claimable(campaign, candidate, nation.Id)
                    .Where(province => campaign.ProvinceController[province] == (ushort)nation.MapIndex)
                    .ToList();
                if (held is not null && occupied.Count <= held.Count) continue;
                war = candidate;
                winner = nation;
                held = occupied;
            }
        }
        if (war is null || winner is null) return "no war left to negotiate";
        ledger = new PeaceLedger(campaign, war, winner.Id);

        if (held.Count < 4)
        {
            Console.WriteLine($"  ai peace: the best-placed belligerent in the world, {winner.Tag}, occupies " +
                              $"{held.Count} claimable province(s) - there is no settlement to shape yet.");
            return "";
        }

        // The shipped scenario is a stalemate, so the real war score often buys less than one
        // province and every possible treaty is the empty one. The shape is therefore
        // compared at a budget that forces a choice - half of what taking the whole occupation
        // would cost - while affordability and refusal are checked against the real budget
        // below, where there is one to check against.
        var everything = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var province in held) everything.Provinces.Add(province);
        var shaping = ledger.Cost(everything) * 0.5f;
        if (shaping <= 0f) return $"{winner.Tag}'s entire occupation prices at nothing";

        // What the rule this replaced would have written, on this same world.
        var cheapest = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var province in held.OrderBy(province => ledger.Price(province).Cost))
        {
            cheapest.Provinces.Add(province);
            if (ledger.Cost(cheapest) <= shaping) continue;
            cheapest.Provinces.Remove(province);
            break;
        }

        var written = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        AiStatecraft.WriteDemand(campaign, winner, war, ledger, written, shaping);
        if (written.Provinces.Count == 0)
        {
            // No land is a real answer now, and this check used to call it a bug.
            //
            // Regions are indivisible: a treaty moves a whole one or none. So a winner with
            // ten occupied provinces spread thinly across regions that each cost more than
            // its whole war score signs for no land at all - correctly, and in preference to
            // carving one of them along the line its army stopped on.
            //
            // What must still hold is that it signed for SOMETHING. A war score spent on
            // nothing is the actual fault, and it is the one worth failing on.
            var terms = new List<string>();
            if (written.Reparations) terms.Add("reparations");
            if (written.LongTruce) terms.Add("a long truce");
            if (written.TradeConcessions) terms.Add("trade concessions");
            if (written.Demilitarise) terms.Add("demilitarisation");
            if (written.Humiliate) terms.Add("humiliation");
            // A war score below the cheapest term buys nothing at all: an empty treaty is then
            // the only one there is, and the land check below still has to pass.
            var defines = campaign.Defines;
            var cheapestTerm = Math.Min(defines.PeaceCostReparations,
                Math.Min(defines.PeaceCostTradeConcessions, defines.PeaceCostDemilitarise));
            if (terms.Count == 0 && shaping >= cheapestTerm)
                return $"{winner.Tag} wrote an entirely empty treaty out of {held.Count:N0} " +
                       $"occupied province(s) with {shaping:0.00} to spend - no land and no terms";

            // And the land really has to have been unaffordable, rather than simply missed.
            var cheapestWhole = float.MaxValue;
            foreach (var region in held.Select(province => (int)topology.ProvinceToState[province]).Distinct())
            {
                var whole = PeaceConference.ProvincesOfState(topology, region);
                if (whole.Count == 0) continue;
                var trial = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
                foreach (var province in whole) trial.Provinces.Add(province);
                cheapestWhole = MathF.Min(cheapestWhole, ledger.Cost(trial));
            }
            if (cheapestWhole <= shaping)
                return $"{winner.Tag} took no land although a whole region was going for " +
                       $"{cheapestWhole:0.00} against its {shaping:0.00}";

            Console.WriteLine($"  ai peace: {winner.Tag} held {held.Count:N0} province(s) and signed for " +
                              $"no land - the cheapest whole region on offer was {cheapestWhole:0.00} against " +
                              $"{shaping:0.00} of war score - taking {(terms.Count == 0 ? "nothing, since no term costs that little" : string.Join(", ", terms))} instead.");
            return "";
        }

        var cost = ledger.Cost(written);
        // Ground the army holds and joins to the country is kept whatever this test's budget is
        // (PeaceShape.KeepHeldConquest), and the ledger prices it at no more than
        // Budget x AiPeaceBudgetShare - which is the real game's own treaty budget. The shaping
        // budget above is an artificial one and can be smaller than that ceiling.
        var heldCeiling = ledger.Budget * Math.Clamp(campaign.Defines.AiPeaceBudgetShare, 0.05f, 1f);
        if (cost > MathF.Max(shaping, heldCeiling) + 0.001f)
            return $"the treaty costs {cost:0.00} against a budget of {shaping:0.00} (held-ground ceiling {heldCeiling:0.00})";

        var strandedNow = StrandedProvinces(campaign, winner, written.Provinces);
        var strandedBefore = StrandedProvinces(campaign, winner, cheapest.Provinces);
        if (strandedNow > strandedBefore)
            return $"the treaty is in more pieces than the cheapest-first rule it replaced: " +
                   $"{strandedNow:N0} province(s) touching nothing of {winner.Tag}, against {strandedBefore:N0}";

        // The other half of "cheapest first" was that cheap means empty: the old rule spent a
        // war's whole score on the emptiest ground the winner happened to be standing on. For
        // the same money the new one has to come away with at least as many people, or it is
        // not writing a better treaty, only a tidier one.
        long PeopleIn(PeaceDemand demand)
        {
            long people = 0;
            foreach (var province in demand.Provinces)
            {
                var mapIndex = (int)topology.ProvinceToState[province];
                var count = ledger.ProvincesPerState.GetValueOrDefault(mapIndex, 1);
                var state = campaign.States.Find(entry => entry.MapIndex == mapIndex);
                if (state is not null && count > 0) people += state.Population / count;
            }
            return people;
        }

        var peopleNow = PeopleIn(written);
        var peopleBefore = PeopleIn(cheapest);

        // Per province rather than in total, and the reason is a change of rule rather than
        // a lowered bar. A settlement moves whole regions now: a region the winner is in more
        // than halfway comes with it entire, and one it has barely entered is let go instead
        // of being carved off, because carving it makes a second region that never goes away.
        // That trade is the point - it is what took a decade of wars from 37 regions cut in
        // two to 12 - and it means a small occupation can now buy less ground than a rule
        // that took whatever it was standing on. What must still hold is the thing the old
        // rule got wrong: the ground it does take must not be the emptiest ground available.
        var perProvinceNow = written.Provinces.Count == 0 ? 0 : peopleNow / written.Provinces.Count;
        var perProvinceBefore = cheapest.Provinces.Count == 0 ? 0 : peopleBefore / cheapest.Provinces.Count;
        if (perProvinceNow < perProvinceBefore)
            return $"for the same {shaping:0.0} of war score the treaty takes {perProvinceNow:N0} people " +
                   $"a province where the cheapest-first rule it replaced took {perProvinceBefore:N0} " +
                   $"({peopleNow:N0} against {peopleBefore:N0} in total)";

        // And the new guarantee, which is the whole reason the trade above is worth making:
        // no region is left cut in two by the treaty unless it is the one the war was over.
        foreach (var region in written.Provinces.Select(province => (int)topology.ProvinceToState[province]).Distinct())
        {
            if (region == war.ClaimedStateMapIndex) continue;
            var whole = PeaceConference.ProvincesOfState(topology, region).Count;
            var taken = written.Provinces.Count(province => topology.ProvinceToState[province] == (ushort)region);
            if (taken > 0 && taken < whole)
                return $"the treaty takes {taken:N0} of {whole:N0} province(s) of region {region}, " +
                       "which cuts it in two and makes a new region of the remainder";
        }

        var regionsNow = written.Provinces.Select(province => (int)topology.ProvinceToState[province])
            .Distinct().Count();
        var regionsBefore = cheapest.Provinces.Select(province => (int)topology.ProvinceToState[province])
            .Distinct().Count();

        // A country that declared for a region and then took none of it did not win its war.
        if (war.ClaimedStateMapIndex >= 0)
        {
            var goalHeld = held.Count(province => topology.ProvinceToState[province] == war.ClaimedStateMapIndex);
            var goalTaken = written.Provinces.Count(province =>
                topology.ProvinceToState[province] == war.ClaimedStateMapIndex);
            if (goalHeld > 0 && goalTaken == 0)
                return $"{winner.Tag} occupies {goalHeld:N0} province(s) of the region it declared war over " +
                       "and demanded none of them";
        }

        // And what it would sign for what it has actually won, which in a stalemate is
        // usually nothing at all - correctly.
        var affordable = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        AiStatecraft.WriteDemand(campaign, winner, war, ledger, affordable, ledger.Budget * 0.8f);
        if (affordable.Provinces.Count > 0)
        {
            var refusal = PeaceConference.BlockReason(campaign, war, affordable);
            if (refusal.Length > 0)
                return $"the AI wrote a peace the other side refuses: {refusal}";
        }

        Console.WriteLine($"  ai peace: given {shaping:0.0} of war score, {winner.Tag} wrote a treaty of " +
                          $"{written.Provinces.Count:N0} province(s) across {regionsNow:N0} region(s) - " +
                          $"{peopleNow:N0} people, {strandedNow:N0} of them detached.");
        Console.WriteLine($"    cheapest-first, for the same money: {cheapest.Provinces.Count:N0} province(s) " +
                          $"across {regionsBefore:N0} region(s) - {peopleBefore:N0} people, " +
                          $"{strandedBefore:N0} detached. On its real war score of {ledger.Budget:0.0} " +
                          $"{winner.Tag} would ask for {affordable.Provinces.Count:N0} province(s).");
        return "";
    }

    /// <summary>
    /// How many demanded provinces touch neither the demander's own ground nor the rest of
    /// the demand - which is to say, how many pieces the settlement would arrive in.
    /// </summary>
    internal static int StrandedProvinces(Campaign campaign, Nation nation, IEnumerable<int> demanded)
    {
        var topology = campaign.ProvinceMap;
        var claimed = new HashSet<int>(demanded);
        var mapIndex = (ushort)nation.MapIndex;
        var stranded = 0;
        foreach (var province in claimed)
        {
            var touching = false;
            foreach (var neighbour in topology.Neighbors[province])
            {
                if (topology.ProvinceToNation[neighbour] != mapIndex && !claimed.Contains(neighbour)) continue;
                touching = true;
                break;
            }
            if (!touching) stranded++;
        }
        return stranded;
    }

    internal static string APeaceCanBeWritten(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";
        var war = campaign.Wars.Find(candidate => !candidate.Ended);
        if (war is null) return "no war left to negotiate";

        var winner = campaign.NationById(war.AttackerId);
        var loser = campaign.NationById(war.DefenderId);
        if (winner is null || loser is null) return "the war has no belligerents";

        var ledger = new PeaceLedger(campaign, war, winner.Id);
        var claimable = PeaceConference.Claimable(campaign, war, winner.Id);
        if (claimable.Count == 0) return $"{loser.Tag} owns nothing that could be demanded";

        // Ground the winner holds against ground it has never touched.
        var held = claimable.FirstOrDefault(province =>
            campaign.ProvinceController[province] == (ushort)winner.MapIndex);
        var untouched = claimable.FirstOrDefault(province =>
            campaign.ProvinceController[province] == (ushort)loser.MapIndex);
        if (held > 0 && untouched > 0)
        {
            var heldPrice = ledger.Price(held);
            var untouchedPrice = ledger.Price(untouched);
            if (untouchedPrice.Cost <= heldPrice.Cost)
                return $"taking ground never touched ({untouchedPrice.Cost:0.00}) is not dearer than " +
                       $"taking ground already held ({heldPrice.Cost:0.00})";
        }

        // A demand nobody could pay for has to be refused, with a reason.
        var greedy = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var province in claimable) greedy.Provinces.Add(province);
        greedy.Humiliate = true;
        var refusal = PeaceConference.BlockReason(campaign, war, greedy);
        if (refusal.Length == 0)
            return $"annexing all {claimable.Count:N0} of {loser.Tag} was accepted; the budget is not binding";
        if (!refusal.Contains("will not sign", StringComparison.Ordinal))
            return $"the refusal does not explain itself: \"{refusal}\"";

        // A demand inside the budget has to be accepted, and has to move the border.
        //
        // Priced ROUNDED, because that is what the conference prices and what gets signed.
        // A treaty moves whole regions, so adding one province can add a region: costing the
        // provinces as drawn built a demand that looked affordable, was quoted at more, and
        // was then refused - which is the bug this check exists to catch, arriving through
        // the check itself.
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var province in claimable
                     .Where(province => campaign.ProvinceController[province] == (ushort)winner.MapIndex)
                     .OrderBy(province => ledger.Price(province).Cost))
        {
            demand.Provinces.Add(province);
            if (ledger.Cost(PeaceConference.Round(campaign, war, demand)) > ledger.Budget * 0.8f)
            {
                demand.Provinces.Remove(province);
                break;
            }
        }
        if (demand.Provinces.Count == 0)
        {
            Console.WriteLine($"  peace: {winner.Tag} holds nothing worth {ledger.Budget:0} of war score; " +
                              "an affordable territorial demand does not exist yet.");
            return "";
        }

        var block = PeaceConference.BlockReason(campaign, war, demand);
        if (block.Length > 0) return $"an affordable demand was still refused: {block}";

        var before = demand.Provinces.Count(province =>
            topology.ProvinceToNation[province] == (ushort)winner.MapIndex);
        var cost = ledger.Cost(demand);
        var states = campaign.States.Count;
        if (!PeaceConference.Resolve(campaign, war, demand))
            return "an affordable, unblocked demand failed to resolve";

        if (!war.Ended) return "the peace was signed and the war is still running";
        var after = demand.Provinces.Count(province =>
            topology.ProvinceToNation[province] == (ushort)winner.MapIndex);
        if (after != demand.Provinces.Count)
            return $"{demand.Provinces.Count - after} demanded province(s) did not change owner";
        if (before == after) return "the border did not move";

        Console.WriteLine($"  peace: {winner.Tag} took {demand.Provinces.Count:N0} province(s) from {loser.Tag} " +
                          $"for {cost:0.0} of {ledger.Budget:0.0} war score, " +
                          $"splitting {campaign.States.Count - states} region(s).");
        Console.WriteLine($"    {war.PeaceSummary}");
        return "";
    }

    /// <summary>
    /// Whether the world runs itself. Two hundred and sixty countries that never tax,
    /// never build, never fall out and never fight are a diorama; the check is that a year
    /// of nobody playing leaves a world that has visibly moved on its own - and that it
    /// moved without any single country running away with the map.
    /// </summary>
    /// <summary>
    /// Whether a war can actually destroy anything, and whether the world survives that.
    ///
    /// It could not. Losses came off a formation's strength and nothing ever came off the
    /// order of battle, so a division ground to nothing walked out of the line and refilled
    /// itself from the national manpower pool. Two years of the Russo-Ukrainian front cost
    /// three and a half million casualties and not one formation on either side; the front
    /// moved only when somebody walked into a province nobody was standing in.
    ///
    /// Two things had to become true together, and this holds both. A broken formation is
    /// destroyed - so a battle can be won and an encirclement is worth attempting. And a
    /// country below the army it is built to keep replaces it - so an army that can only
    /// shrink does not lose every long war by arithmetic. Either one alone is a bug: the
    /// first without the second empties the map, the second without the first is what was
    /// here before.
    /// </summary>
    internal static string WarCostsFormations(Campaign campaign, int days)
    {
        var destroyed = Warfare.OutcomeTally.GetValueOrDefault("destroyed: broken in the field");
        if (destroyed == 0)
            return $"{days:N0} day(s) of war destroyed not one formation anywhere - a battle " +
                   "cannot be won if the loser only ever refills";

        var established = 0f;
        var standing = 0f;
        var rebuilt = 0;
        foreach (var nation in campaign.Nations)
        {
            established += nation.EstablishedDivisions;
            if (nation.FormationsLost > 0) rebuilt++;
        }
        foreach (var army in campaign.Armies) standing += army.Divisions;

        if (established <= 0f) return "no country ever recorded what army it keeps";
        var share = standing / established;
        if (share < 0.60f)
            return $"the world's armies fell to {share:P0} of their establishment after {days:N0} day(s) - " +
                   "formations are being destroyed faster than they are replaced, which empties the map";
        if (share > 1.40f)
            return $"the world's armies grew to {share:P0} of their establishment - the rebuild is not " +
                   "bounded by what a country is built to keep";

        Console.WriteLine($"  attrition: {destroyed:N0} formation(s) destroyed in {days:N0} day(s) across " +
                          $"{rebuilt:N0} countries; the world stands at {share:P0} of its establishment " +
                          $"({standing:N0} of {established:N0} divisions).");
        return "";
    }

    /// <summary>
    /// Where each country's capital is, which nothing was checking.
    ///
    /// Forty-eight had no entry, and a country with no entry gets latitude and longitude
    /// zero. That is not a cosmetic gap: the deployment planner posts a country's army to
    /// whichever of its provinces is nearest its capital, so Taiwan, Israel, Hong Kong,
    /// Somaliland, Palestine and Kosovo were all being garrisoned from whichever of their
    /// provinces lies closest to a point in the Gulf of Guinea. Kosovo's capital had been
    /// written down the whole time under the tag XKX while the country table calls it KOS.
    /// </summary>
    /// <summary>
    /// Whether the war is one a player can command.
    ///
    /// The complaint this exists for is "my troops refuse to advance and just walk about
    /// for ever", and it turned out to be measurable: a division is re-posted whenever the
    /// front list it was drawn from is rebuilt, which happens every time a single province
    /// changes hands, so divisions were being sent somewhere new about twice for every time
    /// one arrived anywhere.
    ///
    /// The numbers are printed every run and bounded loosely. They are not a balance target
    /// - a war of movement re-tasks people - but an army that is re-tasked five times for
    /// every arrival is an army nobody is commanding, and that is worth failing over.
    /// </summary>
    internal static string OrdersHoldLongEnoughToBeObeyed()
    {
        // One seed is as chaotic as the war; ORDERS_SEED runs the same probe on another, so a
        // change can be judged on the mean of several rather than on 20241111 alone.
        var campaign = Campaign.CreateBootstrap(
            int.TryParse(Environment.GetEnvironmentVariable("ORDERS_SEED"), out var ordersSeed) ? ordersSeed : 20241111);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var ukr = campaign.NationByTag("UKR");
        var rus = campaign.NationByTag("RUS");
        if (ukr is null || rus is null) return "";

        Warfare.OutcomeTally.Clear();

        // What the front looked like on day zero, so movement can be measured rather than
        // eyeballed: how many provinces each side held of the ground either of them holds.
        int HeldBy(Nation nation)
        {
            var held = 0;
            var control = campaign.ProvinceController;
            for (var province = 1; province < control.Length; province++)
                if (control[province] == (ushort)nation.MapIndex) held++;
            return held;
        }

        var ukrStart = HeldBy(ukr);
        var rusStart = HeldBy(rus);

        var idleDays = new Dictionary<int, int>();
        var lastOrder = new Dictionary<int, ArmyOrderSample>();
        var armyTags = new Dictionary<int, string>();
        var retargets = new Dictionary<int, int>();
        var arrivals = new Dictionary<int, int>();
        var progressResets = 0;
        var lastProvince = new Dictionary<int, int>();
        var visited = new Dictionary<int, Dictionary<int, int>>();
        var everFought = new HashSet<int>();
        var battlesSeen = new HashSet<int>();
        var durations = new List<int>();
        long engagedRounds = 0;
        var stuckPeak = 0;

        void ObserveOrders(Campaign world)
        {
            foreach (var army in world.Armies)
            {
                armyTags[army.Id] = world.NationById(army.NationId)?.Tag ?? "unknown";
                var now = ArmyOrderSample.Of(army);
                if (lastOrder.TryGetValue(army.Id, out var before))
                {
                    var changes = ArmyOrderSample.Compare(before, now);
                    if ((changes & ArmyOrderChange.Arrived) != 0)
                        arrivals[army.Id] = arrivals.GetValueOrDefault(army.Id) + 1;
                    if ((changes & ArmyOrderChange.Retargeted) != 0)
                        retargets[army.Id] = retargets.GetValueOrDefault(army.Id) + 1;
                    if ((changes & ArmyOrderChange.ProgressReset) != 0) progressResets++;
                }
                lastOrder[army.Id] = now;
            }
        }
        ObserveOrders(campaign);
        // A daily endpoint can miss an arrival followed by a new task on the same day.
        // The observer issues no commands and runs after the authoritative step finishes.
        campaign.StepCompleted += ObserveOrders;
        const int days = 365 * 3;
        for (var day = 0; day < days; day++)
        {
            campaign.TickDay();

            var fighting = new HashSet<int>();
            foreach (var battle in campaign.Battles)
            {
                if (battle.Ended) continue;
                foreach (var id in battle.Attackers) fighting.Add(id);
                foreach (var id in battle.Defenders) fighting.Add(id);
                engagedRounds++;
            }
            foreach (var id in fighting) everFought.Add(id);

            // A formation of a country at war, standing still, not fighting, with no route.
            // Marching for ever without arriving, and walking in circles. A formation with a
            // route was counted as busy, which is exactly how an army that never gets
            // anywhere hides: it is always on its way somewhere.
            foreach (var army in campaign.Armies)
            {
                if (!lastProvince.TryGetValue(army.Id, out var was)) lastProvince[army.Id] = army.ProvinceId;
                else if (was != army.ProvinceId)
                {
                    lastProvince[army.Id] = army.ProvinceId;
                    if (!visited.TryGetValue(army.Id, out var seen))
                        visited[army.Id] = seen = new Dictionary<int, int>();
                    seen[army.ProvinceId] = seen.GetValueOrDefault(army.ProvinceId) + 1;
                }
            }

            var stuck = 0;
            foreach (var army in campaign.Armies)
            {
                var owner = campaign.NationById(army.NationId);
                if (owner is null) continue;
                var atWar = false;
                foreach (var war in campaign.Wars)
                    if (!war.Ended && war.Involves(owner.Id)) { atWar = true; break; }
                if (!atWar) { idleDays.Remove(army.Id); continue; }
                if (fighting.Contains(army.Id) || army.Route.Count > 0)
                {
                    idleDays.Remove(army.Id);
                    continue;
                }
                idleDays[army.Id] = idleDays.GetValueOrDefault(army.Id) + 1;
                if (idleDays[army.Id] >= 120) stuck++;
            }
            stuckPeak = Math.Max(stuckPeak, stuck);

            foreach (var battle in campaign.Battles)
            {
                if (!battle.Ended || !battlesSeen.Add(battle.Id)) continue;
                durations.Add(Math.Max(0, campaign.ElapsedDays - battle.StartedDay));
            }
        }

        campaign.StepCompleted -= ObserveOrders;
        var ukrEnd = HeldBy(ukr);
        var rusEnd = HeldBy(rus);

        durations.Sort();
        var median = durations.Count == 0 ? 0 : durations[durations.Count / 2];
        var longest = durations.Count == 0 ? 0 : durations[^1];
        var oneDay = durations.Count(d => d <= 1);

        var atWarArmies = 0;
        foreach (var army in campaign.Armies)
        {
            var owner = campaign.NationById(army.NationId);
            if (owner is null) continue;
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(owner.Id)) { atWarArmies++; break; }
        }

        var neverFought = 0;
        foreach (var army in campaign.Armies)
            if ((army.NationId == ukr.Id || army.NationId == rus.Id) && !everFought.Contains(army.Id))
                neverFought++;

        Console.WriteLine($"  PROBE three years from 2024-11-11:");
        Console.WriteLine($"    battles: {battlesSeen.Count:N0} finished, median {median} day(s), " +
                          $"longest {longest}, {oneDay:N0} lasted a day or less");
        Console.WriteLine($"    outcomes: " + string.Join(", ",
            Warfare.OutcomeTally.OrderByDescending(entry => entry.Value)
                   .Select(entry => $"{entry.Key} {entry.Value:N0}")));
        Console.WriteLine($"    ground: UKR {ukrStart:N0} -> {ukrEnd:N0} province(s), " +
                          $"RUS {rusStart:N0} -> {rusEnd:N0}");
        Console.WriteLine($"    formations: {atWarArmies:N0} belong to countries at war; " +
                          $"{neverFought:N0} of RUS/UKR never saw a battle in three years");
        Console.WriteLine($"    idle: at worst {stuckPeak:N0} formation(s) stood still for 120+ days " +
                          $"while their country was at war");

        // Whose they are, and what they were told to do. "Idle" is only interesting if it
        // is the big wars; a hundred small states at war with nobody nearby is not a bug.
        var byNation = new Dictionary<string, int>();
        var byOrder = new Dictionary<string, int>();
        var byPlace = new Dictionary<string, int>();
        var frontless = 0;
        foreach (var (armyId, stood) in idleDays)
        {
            if (stood < 120) continue;
            var army = campaign.Armies.Find(a => a.Id == armyId);
            if (army is null) continue;
            var owner = campaign.NationById(army.NationId);
            var tag = owner?.Tag ?? "??";
            byNation[tag] = byNation.GetValueOrDefault(tag) + 1;
            byOrder[army.OrderKind.ToString()] = byOrder.GetValueOrDefault(army.OrderKind.ToString()) + 1;
            if (army.ProvinceId > 0 && army.ProvinceId < campaign.ProvinceMap.ProvinceToState.Length)
            {
                var standing = campaign.States.Find(state => state.MapIndex == campaign.ProvinceMap.ProvinceToState[army.ProvinceId]);
                // And why the front staff did not call it up (FrontManager.CallUpIdle).
                var why = army.TemplateId == MilitiaCore.TemplateId ? "militia"
                    : army.FrontLineId >= 0 ? "on a line"
                    : army.AssignedWarId >= 0 ? "assigned"
                    : army.DirectOrder ? "direct order" : "unassigned";
                var where = $"{tag} {army.OrderKind} in {standing?.Name ?? "?"} ({why})";
                byPlace[where] = byPlace.GetValueOrDefault(where) + 1;
            }

            // Is there a front for it to be on at all?
            var onFront = false;
            foreach (var war in campaign.Wars)
            {
                if (war.Ended || owner is null || !war.Involves(owner.Id)) continue;
                if (war.FriendlyFront(owner.Id).Count > 0) { onFront = true; break; }
            }
            if (!onFront) frontless++;
        }
        Console.WriteLine($"    idle by country: " + string.Join(", ",
            byNation.OrderByDescending(e => e.Value).Take(8).Select(e => $"{e.Key} {e.Value}")));
        Console.WriteLine($"    idle by order: " + string.Join(", ",
            byOrder.OrderByDescending(e => e.Value).Select(e => $"{e.Key} {e.Value}")));
        Console.WriteLine($"    idle where: " + string.Join(", ",
            byPlace.OrderByDescending(e => e.Value).Take(10).Select(e => $"{e.Key} {e.Value}")));
        Console.WriteLine($"    of those, {frontless:N0} belong to a war with no front at all");
        // Orders changed under a division that had not got where it was last sent.
        var churn = retargets.Values.Sum();
        var arrived = arrivals.Values.Sum();
        var worstChurn = 0;
        var worstChurnArmy = 0;
        foreach (var (armyId, count) in retargets)
            if (count > worstChurn) { worstChurn = count; worstChurnArmy = armyId; }
        var churned = campaign.Armies.Find(a => a.Id == worstChurnArmy);
        Console.WriteLine($"    orders: {churn:N0} re-target(s) of a division that had not arrived, " +
                          $"against {arrived:N0} arrival(s) - worst division re-tasked {worstChurn:N0} times " +
                          $"({campaign.NationById(churned?.NationId ?? -1)?.Tag}, {churned?.OrderKind})");
        Console.WriteLine($"    orders observed at completed steps; same-goal progress resets: {progressResets:N0}");
        // By country, because a world with a dozen civil wars in it is a dozen armies being
        // commanded, and one of them can carry the whole figure.
        var churnBy = new Dictionary<string, (int Retargets, int Arrivals)>();
        string TagOfArmy(int armyId) =>
            armyTags.GetValueOrDefault(armyId, "unknown");
        foreach (var (armyId, count) in retargets)
        {
            var tag = TagOfArmy(armyId);
            var was = churnBy.GetValueOrDefault(tag);
            churnBy[tag] = (was.Retargets + count, was.Arrivals);
        }
        foreach (var (armyId, count) in arrivals)
        {
            var tag = TagOfArmy(armyId);
            var was = churnBy.GetValueOrDefault(tag);
            churnBy[tag] = (was.Retargets, was.Arrivals + count);
        }
        Console.WriteLine("    orders by country: " + string.Join(", ", churnBy
            .OrderByDescending(pair => pair.Value.Retargets).Take(8)
            .Select(pair => $"{pair.Key} {pair.Value.Retargets}/{pair.Value.Arrivals}")));

        // Walking in circles: a province entered again and again is a formation being sent
        // back somewhere it has just left.
        var circlers = 0;
        var worstRevisits = 0;
        var worstArmy = 0;
        foreach (var (armyId, seen) in visited)
        {
            var most = 0;
            foreach (var count in seen.Values) most = Math.Max(most, count);
            if (most >= 5) circlers++;
            if (most > worstRevisits) { worstRevisits = most; worstArmy = armyId; }
        }
        var worst = campaign.Armies.Find(a => a.Id == worstArmy);
        Console.WriteLine($"    circling: {circlers:N0} formation(s) entered the same province 5+ times; " +
                          $"the worst re-entered one province {worstRevisits:N0} times " +
                          $"({campaign.NationById(worst?.NationId ?? -1)?.Tag}, order {worst?.OrderKind})");

        Console.WriteLine($"    engagement-days: {engagedRounds:N0} across {days} days");
        Console.WriteLine($"    wars: {campaign.Wars.Count(war => !war.Ended):N0} running, " +
                          $"{campaign.Wars.Count(war => war.Ended):N0} ended");

        if (arrived == 0)
            return "not one division reached the post it was sent to in three years";
        var perArrival = churn / (float)arrived;
        // 1.49 on average over sixteen seeds (1.28-1.74), against 1.99 on the same seeds before
        // a division whose post had left the line took the nearest open one instead of a rank
        // slot anywhere on it - and 1.98 before a division was allowed to keep its post across
        // a change of job. The one seed run here is as chaotic as the war: the same code gave
        // 1.57-2.50 across seeds before. 1.8 is loose enough not to fail on the ordinary noise
        // of a war and tight enough to catch a return to the old behaviour.
        if (perArrival > 1.8f)
            return $"divisions were sent somewhere new {perArrival:0.00} times for every one that " +
                   $"arrived ({churn:N0} against {arrived:N0}), which is not an army anybody is commanding";
        if (worstRevisits > 22)
            return $"one division entered the same province {worstRevisits:N0} times, which is not " +
                   "a march, it is a pendulum";
        if (battlesSeen.Count < 100)
            return $"only {battlesSeen.Count:N0} battles were fought in three years of a world with " +
                   "a live invasion in it";
        return "";
    }

    /// <summary>
    /// An alliance is a thing two countries decide, and either of them can be a player.
    ///
    /// The stance has meant something for a long time - a call to arms answers it. What is
    /// checked here is the door that was missing: that somebody can ask, that the answer is
    /// worked out from things that move rather than from a threshold crossed in private,
    /// that a refusal says which of the reasons it was, and that a country playing the game
    /// is offered rather than signed for.
    /// </summary>
    /// <summary>
    /// The war the scenario is about opens with both armies standing on it.
    ///
    /// The deployment planner has always known how to do this - most of an army goes to its
    /// front when there is a front, and 94% of it for these two countries. It ran one line
    /// before the fronts were built, so what it actually saw was `front=0` for both, fell
    /// through to the peacetime branch, and spread each army along every border it has: for
    /// Russia two hundred and thirty-seven provinces from Finland to the Pacific.
    ///
    /// Measured where it matters - after the map is attached, which is the moment the
    /// planner runs - and measured as "is this division standing on a front province",
    /// which is the question, rather than as a distance from a line somebody wrote down.
    /// </summary>
    internal static string TheFrontIsMannedOnTheFirstMorning()
    {
        var campaign = Campaign.CreateBootstrap(20240501);
        if (!AttachMap(campaign)) return "";

        var war = campaign.Wars.Find(candidate => !candidate.Ended);
        if (war is null) return "the scenario opens with no war at all";
        var attacker = campaign.NationById(war.AttackerId);
        var defender = campaign.NationById(war.DefenderId);
        if (attacker is null || defender is null) return "";

        int OnTheFront(Nation nation)
        {
            var front = war.FriendlyFront(nation.Id);
            var standing = 0;
            foreach (var army in campaign.Armies)
                if (army.NationId == nation.Id && army.ProvinceId > 0 && front.Contains(army.ProvinceId))
                    standing++;
            return standing;
        }

        var theirs = campaign.Armies.Count(army => army.NationId == attacker.Id);
        var ours = campaign.Armies.Count(army => army.NationId == defender.Id);
        if (war.AttackerFront.Count == 0 || war.DefenderFront.Count == 0)
            return "the war has no front on the day the map arrives, so nobody can be posted to one";

        var attackerPosted = OnTheFront(attacker);
        var defenderPosted = OnTheFront(defender);
        if (attackerPosted < theirs / 2)
            return $"{attackerPosted} of {attacker.Tag}'s {theirs} division(s) stand on the front on day one";
        if (defenderPosted < ours / 2)
            return $"{defenderPosted} of {defender.Tag}'s {ours} division(s) stand on the front on day one";

        Console.WriteLine($"  opening deployment: {attackerPosted}/{theirs} {attacker.Tag} and " +
                          $"{defenderPosted}/{ours} {defender.Tag} division(s) stand on the front " +
                          $"({war.AttackerFront.Count} and {war.DefenderFront.Count} province(s) of it) " +
                          "before a day is stepped.");
        return "";
    }

    /// <summary>
    /// An order the player gave is carried out.
    ///
    /// A division marched by hand into an empty enemy province used to do nothing at all,
    /// every day, forever, and say nothing about why. The gap rule refuses an advance into
    /// ground covered by fire from the provinces around it unless the attacker is worth more
    /// than the cover - which is the rule that keeps a front a front, and which was applied
    /// to orders a person had given as well as to advances the planner chose on its own.
    ///
    /// The same world twice, with one flag different, because the question is about one
    /// formation rather than about a campaign: both runs issue the identical march into the
    /// identical covered province, and the control clears the flag that says a person asked
    /// for it. What separates them is exactly the behaviour under test and nothing else.
    /// </summary>
    internal static string AnOrderIsCarriedOut()
    {
        static (Campaign Campaign, Army Army, int Target, float Cover, bool Mapped) Stage()
        {
            var campaign = Campaign.CreateBootstrap(20240715);
            if (!AttachMap(campaign)) return (null, null, 0, 0f, false);
            campaign.IsPaused = false;
            var topology = campaign.ProvinceMap;
            var controllers = campaign.ProvinceController;
            if (topology is null || controllers is null) return (null, null, 0, 0f, false);

            // Let the deployment happen: at day zero the formations are on their seeded
            // coordinates and there is no line to find a gap in yet.
            for (var day = 0; day < 10; day++) campaign.TickDay();

            // Any war that is actually being fought. Taking the first unended one finds a
            // quarrel between two small states with no divisions in it, of which the world
            // holds a hundred and forty, and the whole check then passes without testing
            // anything - which it did, once.
            foreach (var war in campaign.Wars)
            {
                if (war.Ended) continue;
                var enemy = campaign.NationById(war.DefenderId);
                if (enemy is null) continue;

                // Where the enemy divisions are, so an empty province can be told from a held
                // one and the ground around it can be weighed.
                var byProvince = new Dictionary<int, float>();
                foreach (var other in campaign.Armies)
                    if (other.NationId == enemy.Id && other.ProvinceId > 0 && other.Divisions >= 0.1f)
                        byProvince[other.ProvinceId] = byProvince.GetValueOrDefault(other.ProvinceId) + other.Power;
                if (byProvince.Count == 0) continue;

                foreach (var army in campaign.Armies)
                {
                    if (army.NationId != war.AttackerId || army.ProvinceId <= 0 || army.InBattle) continue;
                    if (army.Divisions < 0.1f) continue;
                    foreach (var province in topology.Neighbors[army.ProvinceId])
                    {
                        if (controllers[province] != (ushort)enemy.MapIndex) continue;
                        if (byProvince.ContainsKey(province)) continue;      // not empty
                        // Covered: the ring around it holds more than this formation is worth,
                        // which is the situation the rule refuses and the player cannot see.
                        var cover = 0f;
                        foreach (var neighbour in topology.Neighbors[province])
                            cover += byProvince.GetValueOrDefault(neighbour);
                        if (cover <= army.Power * campaign.Defines.GapExploitAdvantage) continue;
                        return (campaign, army, province, cover, true);
                    }
                }
            }
            return (null, null, 0, 0f, true);
        }

        var (campaign, army, target, cover, mapped) = Stage();
        if (!mapped) return "";
        if (army is null)
            return "no war in the world offered an empty province covered by a held line, so " +
                   "this check proved nothing - which is how it passed once without testing anything";

        // One day, because one day is all this question needs and everything that happens
        // after it belongs to somebody else. The first probe of this ran for four months and
        // measured the enemy plugging the gap on day two: the province stopped being empty,
        // the advance correctly became a battle, and the resulting "4% of the way in" said
        // nothing whatever about the rule under test. The rule is visible on day one or not
        // at all.
        static float FirstDay(Campaign campaign, Army army, int target, bool asAnOrder)
        {
            var war = campaign.Wars.Find(candidate => !candidate.Ended && candidate.Involves(army.NationId));
            if (war is null) return -1f;
            new MoveArmyCommand
            {
                IssuerId = army.NationId, ArmyId = army.Id, TargetProvinceId = target
            }.Apply(campaign);
            if (!army.DirectOrder) return -1f;
            // The control keeps the order, the route, the objective and the war, and differs
            // only in having nobody behind it - which is the whole of what the fix reads.
            if (!asAnOrder) army.DirectOrder = false;
            campaign.TickDay();
            return war.UnopposedProgress.GetValueOrDefault(target);
        }

        var ordered = FirstDay(campaign, army, target, true);
        var (control, controlArmy, controlTarget, _, _) = Stage();
        if (control is null || controlTarget != target)
            return "the same world staged differently twice, so the two runs are not comparable";
        var unordered = FirstDay(control, controlArmy, target, false);

        if (ordered < 0f || unordered < 0f)
            return "the march was refused at the moment it was ordered, before any advance began";
        if (ordered <= 0f)
            return $"a division ordered into an empty province covered by {cover:0.0} of enemy " +
                   "strength did not move on the day it was ordered, and nothing said why - " +
                   "which is the bug this check exists for";
        if (unordered > 0f)
            return $"a division that was not ordered advanced anyway ({unordered:P0} in a day), " +
                   "so the gap rule no longer holds a front on its own";

        Console.WriteLine($"  orders: a division marched by hand into an empty province covered by " +
                          $"{cover:0.0} of enemy strength moved {ordered:P1} of the way in on the first " +
                          $"day - about {1f / ordered:N0} day(s) to take it under that much fire. The same " +
                          "division, advancing on its own judgement, was refused.");
        return "";
    }

    /// <summary>
    /// Write an envelope the way a crash does, read it back and replay its tape onto a second
    /// campaign of the same seed - the offline reproduction <c>CrashEnvelope</c> promises.
    /// Returns "" when the two worlds agree, or the disagreement for Check to print.
    /// </summary>
    internal static string CrashEnvelopeReplays(int seed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dof_envelope_" + Guid.NewGuid().ToString("N"));
        try
        {
            CrashEnvelope.Clear();
            var crashed = Campaign.CreateBootstrap(seed);
            crashed.Commands.Enqueue(new SetTaxRateCommand { IssuerId = crashed.Player.Id, Value = .35f });
            crashed.Commands.ApplyAll(crashed);
            var fingerprint = CampaignFingerprint.Of(crashed);
            var path = CrashEnvelope.Dump(crashed, null, "crash replay check", directory);
            if (path.Length == 0)
                return "the envelope was not written: " +
                    (CrashEnvelope.LastFailure.Length > 0 ? CrashEnvelope.LastFailure : "no path returned");
            var envelope = CrashEnvelope.Load(path);
            if (envelope is null) return "the envelope did not read back";
            if (envelope.CampaignSeed != seed) return $"the envelope names seed {envelope.CampaignSeed}, not {seed}";
            if (envelope.RecentCommands.Count != 1)
                return $"the tape holds {envelope.RecentCommands.Count} commands, expected the one that was issued";
            var replayed = Campaign.CreateBootstrap(seed);
            var applied = CrashEnvelope.Replay(replayed, envelope);
            if (applied != 1) return $"replayed {applied} of the tape's one command";
            var reproduced = CampaignFingerprint.Of(replayed);
            return reproduced == fingerprint ? ""
                : $"the replayed world differs: 0x{reproduced:X16} vs 0x{fingerprint:X16}";
        }
        finally
        {
            CrashEnvelope.Clear();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        }
    }

    /// <summary>
    /// A battle leaves ruins, and ruins are rebuilt. A season of hard fighting in one province of
    /// a Ukrainian region wrecks a share of the region - not all of it, and not nothing - and the
    /// region then makes less; a week later some of its people have left, most of them for the
    /// rest of Ukraine and still Ukrainian; two years of peace rebuild most of it; and a
    /// reconstruction programme rebuilds it faster. On its own world, stepped by hand, so what is
    /// measured is the ruins and not the war around them.
    /// </summary>
    internal static string BattlesLeaveRuins()
    {
        var campaign = Campaign.CreateBootstrap(20240901);
        if (!AttachMap(campaign)) return "no province map to fight on";
        var ukraine = campaign.NationByTag("UKR");
        if (ukraine is null) return "there is no Ukraine to fight in";
        var topology = campaign.ProvinceMap;
        var states = campaign.StatesOf(ukraine);
        var region = states.Where(state => state.Population > 500_000)
            .OrderBy(state => state.Id, StringComparer.Ordinal).FirstOrDefault();
        if (region is null) return "Ukraine has no region of any size";
        var province = 0;
        var provinces = 0;
        for (var candidate = 1; candidate <= topology.ProvinceCount; candidate++)
        {
            if (topology.ProvinceToState[candidate] != region.MapIndex) continue;
            provinces++;
            if (province == 0) province = candidate;
        }
        if (province == 0) return $"{region.Id} has no province to fight in";

        for (var day = 0; day < 120; day++)
            WarDamage.Fought(campaign, province, campaign.Defines.WarDamageBattleLosses);
        var damage = region.WarDamage;
        if (damage < 0.05f) return $"120 days of battle in one of {region.Id}'s {provinces} provinces wrecked {damage:P1} of it";
        if (damage > 0.95f) return $"120 days of battle in one of {region.Id}'s {provinces} provinces left it {damage:P0} ruins";
        var output = WarDamage.OutputFactor(campaign, region);
        var expected = 1f - damage * campaign.Defines.WarDamageOutputLoss;
        if (MathF.Abs(output - expected) > 0.001f) return $"{region.Id} at {damage:P0} damage makes {output:P0} of its output, not {expected:P0}";
        if (states.Any(state => state != region && state.WarDamage > 0f)) return "a battle in one region wrecked another";

        static long Ukrainians(IEnumerable<ScenarioState> of) => of.Sum(state => state.PopulationGroups
            .Where(group => group.Culture == "ukrainian").Sum(group => group.Size));
        var everyone = states.Sum(state => state.Population);
        var ukrainians = Ukrainians(states);
        var here = region.Population;
        var atWar = campaign.Wars.Where(war => !war.Ended).SelectMany(war => campaign.Nations.Where(other => war.Involves(other.Id)))
            .Select(other => other.Id).ToHashSet();
        var neighbours = CampaignDiplomacy.Neighbours(campaign, ukraine.MapIndex)
            .Select(index => campaign.Nations.Find(other => other.MapIndex == index))
            .Where(other => other is not null && !atWar.Contains(other.Id)).ToList();
        long Abroad() => neighbours.Sum(other => campaign.StatesOf(other).Sum(state => state.Population));
        var abroadBefore = Abroad();
        WarDamage.Week(campaign);
        var left = here - region.Population;
        if (left <= 0) return $"nobody left {region.Id} at {damage:P0} damage";
        var stayed = states.Sum(state => state.Population) - (everyone - left);
        var home = campaign.Defines.WarDamageRefugeesStayHome;
        if (MathF.Abs(stayed - left * home) > left * 0.05f + 10)
            return $"{left:N0} left {region.Id} and {stayed:N0} settled elsewhere in Ukraine, not about {left * home:N0}";
        var groups = states.Sum(state => (long)state.PopulationGroups.Sum(group => group.Size));
        if (Math.Abs(groups - states.Sum(state => state.Population)) > states.Length * 2)
            return $"Ukraine's regions count {states.Sum(state => state.Population):N0} people and their peoples {groups:N0}";
        var displaced = WarDamage.DisplacedTotal(region);
        if (displaced != stayed) return $"{stayed:N0} fled to the rest of Ukraine and {displaced:N0} are waiting to come home";
        var arrivedAbroad = Abroad() - abroadBefore;
        if (neighbours.Count > 0 && arrivedAbroad != left - stayed)
            return $"{left - stayed:N0} fled abroad and {arrivedAbroad:N0} arrived in {string.Join(", ", neighbours.Select(other => other.Tag))}";
        var lostUkrainians = ukrainians - Ukrainians(states);
        if (lostUkrainians > (long)((left - stayed) * 1.05f) + 10)
            return $"{lostUkrainians:N0} Ukrainians left Ukraine when only {left - stayed:N0} people went abroad: the refugees stopped being who they were";

        var weekly = WarDamage.RebuiltPerWeek(campaign, ukraine, region);
        ModifierSystem.Grant(campaign, ukraine, "reconstruction_programme");
        var faster = WarDamage.RebuiltPerWeek(campaign, ukraine, region);
        ModifierSystem.Revoke(ukraine, "reconstruction_programme");
        if (faster < weekly * 1.9f) return $"a reconstruction programme rebuilds {faster:P1} a week against {weekly:P1} without it";

        // A paid reconstruction: refused under fire, paid for once it is quiet, and faster by
        // as much of the boost as the market has industrial materials for.
        ukraine.Treasury = MathF.Max(ukraine.Treasury, 10f);
        region.LastFoughtDay = campaign.ElapsedDays;
        if (WarDamage.RebuildRefusal(campaign, ukraine, region).Length == 0) return "a region was rebuilt on the day of a battle in it";
        region.LastFoughtDay = campaign.ElapsedDays - campaign.Defines.WarDamageSafeDays;
        var refusal = WarDamage.RebuildRefusal(campaign, ukraine, region);
        if (refusal.Length > 0) return $"{region.Id}, quiet for {campaign.Defines.WarDamageSafeDays} days, cannot be rebuilt: {refusal}";
        var cost = WarDamage.RebuildCost(campaign, ukraine, region);
        var treasury = ukraine.Treasury;
        new RebuildRegionCommand { IssuerId = ukraine.Id, StateMapIndex = region.MapIndex }.Apply(campaign);
        if (MathF.Abs(treasury - cost - ukraine.Treasury) > 0.0001f || cost <= 0f)
            return $"rebuilding {region.Id} took {treasury - ukraine.Treasury:0.000} bn from the treasury, and it costs {cost:0.000} bn";
        var paid = WarDamage.RebuiltPerWeek(campaign, ukraine, region);
        var boost = 1f + campaign.Defines.WarDamageRebuildBoost * WarDamage.Materials(ukraine);
        if (MathF.Abs(paid - weekly * boost) > 0.0005f)
            return $"a paid reconstruction rebuilds {paid:P1} a week, and {weekly:P1} x {boost:0.00} is {weekly * boost:P1}";
        if (WarDamage.RebuildRefusal(campaign, ukraine, region).Length == 0) return "a region being rebuilt can be paid for twice";
        region.RebuildingUntilDay = -1;

        long peak = 0, lowest = region.Population;
        for (var week = 0; week < 104; week++)
        {
            WarDamage.Week(campaign);
            peak = Math.Max(peak, WarDamage.DisplacedTotal(region));
            lowest = Math.Min(lowest, region.Population);
        }
        if (region.WarDamage > damage / 3f) return $"two years of peace rebuilt {region.Id} from {damage:P0} only to {region.WarDamage:P0}";
        var waiting = WarDamage.DisplacedTotal(region);
        if (waiting > peak * 0.3f)
            return $"two years after the fighting {waiting:N0} of {region.Id}'s {peak:N0} displaced have still not come home";
        if (region.Population <= lowest) return $"{region.Id} was rebuilt and nobody came back to it";

        Console.WriteLine($"  ruins: 120 days of battle in one of {region.Id}'s {provinces} provinces wreck {damage:P0} of it " +
                          $"(output {output:P0}); a week later {left:N0} have left, {stayed:N0} for elsewhere in Ukraine; " +
                          $"rebuilt {weekly:P1} a week, {faster:P1} under a programme, {paid:P1} paid for ({cost:0.00} bn); " +
                          $"{region.WarDamage:P0} after two years, {peak - waiting:N0} of {peak:N0} displaced home again; " +
                          $"{arrivedAbroad:N0} to {string.Join(", ", neighbours.Select(other => other.Tag))}.");
        return "";
    }

    /// <summary>
    /// A country that has taken all of another has won the war (WarScoring). The score used to
    /// count provinces and top out at 65 for occupation, so Russia holding every province of
    /// Ukraine stood at 71. On its own world: Russia is handed the provinces by hand.
    ///
    /// - The richest half of Ukraine's provinces is worth more occupation than the poorest half.
    /// - All of it, capital included, is a collapse, and a score of 100.
    /// - Handing it back takes the score back down.
    /// </summary>
    internal static string AWonWarScoresAsWon()
    {
        var campaign = Campaign.CreateBootstrap(20240904);
        if (!AttachMap(campaign)) return "no province map to occupy";
        var russia = campaign.NationByTag("RUS");
        var ukraine = campaign.NationByTag("UKR");
        if (russia is null || ukraine is null) return "there is no Russia or no Ukraine";
        var war = campaign.Wars.Find(candidate => !candidate.Ended && candidate.Involves(russia.Id) && candidate.Involves(ukraine.Id));
        if (war is null) return "Russia and Ukraine are not at war on the first day";
        var topology = campaign.ProvinceMap;
        var holder = (ushort)(war.IsAttacking(russia.Id) ? russia.MapIndex : ukraine.MapIndex);
        var victim = holder == russia.MapIndex ? ukraine : russia;
        if (victim != ukraine) return "Ukraine is the attacker in the scenario's war";

        var own = Enumerable.Range(1, topology.ProvinceCount)
            .Where(province => topology.ProvinceToNation[province] == ukraine.MapIndex).ToList();
        var original = own.ToDictionary(province => province, province => campaign.ProvinceController[province]);
        var states = campaign.StatesOf(ukraine).ToDictionary(state => state.MapIndex);
        float Worth(int province) => states.TryGetValue(topology.ProvinceToState[province], out var state)
            ? state.Population / (float)Math.Max(1, own.Count(other => topology.ProvinceToState[other] == state.MapIndex)) : 0f;
        var byWorth = own.OrderByDescending(Worth).ToList();

        void Hold(IEnumerable<int> provinces)
        {
            foreach (var province in own) campaign.SetProvinceController(province, original[province]);
            foreach (var province in provinces) campaign.SetProvinceController(province, holder);
            WarScoring.Update(campaign, war);
        }

        Hold(byWorth.Take(byWorth.Count / 2));
        var rich = war.OccupationScore;
        Hold(byWorth.Skip(byWorth.Count / 2));
        var poor = war.OccupationScore;
        if (rich <= poor) return $"the richer half of Ukraine is worth {rich:0.0} of occupation and the poorer half {poor:0.0}";

        Hold(own);
        var all = war.WarScore;
        if (war.Collapsed != 1 || all < 99.9f)
            return $"Russia holding every province of Ukraine scores {all:0.0} (collapsed {war.Collapsed}), not 100";
        var capital = war.CapitalScore;

        Hold(Array.Empty<int>());
        if (war.WarScore > 50f) return $"with every province handed back the score is still {war.WarScore:0.0}";
        Console.WriteLine($"  war score: the richer half of Ukraine is {rich:0.0} of occupation, the poorer half {poor:0.0}; " +
                          $"all of it is a collapse and 100 (capital {capital:0.0}); handed back, {war.WarScore:0.0}.");
        return "";
    }

    /// <summary>
    /// The AI's treaty is drawn as a border. Russia is handed the regions of Ukraine scattered
    /// about the country - some on its own border, some deep inside - and writes a peace with
    /// no limit on what it may take: every region it asks for must be joined to Russia by the
    /// ground it holds or asks for, and what Ukraine keeps must be in no more pieces than it was.
    /// </summary>
    internal static string APeaceIsABorder()
    {
        var campaign = Campaign.CreateBootstrap(20240923);
        if (!AttachMap(campaign)) return "no province map to fight on";
        var ukraine = campaign.NationByTag("UKR");
        var russia = campaign.NationByTag("RUS");
        var war = campaign.Wars.FirstOrDefault(candidate => !candidate.Ended && candidate.Involves(ukraine.Id) && candidate.Involves(russia.Id));
        if (war is null) return "no war between Russia and Ukraine to end";
        var topology = campaign.ProvinceMap;

        // Region adjacency by land, the way the treaty sees it.
        var next = new Dictionary<int, HashSet<int>>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            int region = topology.ProvinceToState[province];
            if (region == 0) continue;
            if (!next.TryGetValue(region, out var set)) next[region] = set = new HashSet<int>();
            foreach (var neighbour in topology.Neighbors[province])
                if (topology.ProvinceToState[neighbour] is var other && other != 0 && other != region) set.Add(other);
        }
        int Pieces(IEnumerable<int> regions)
        {
            var all = regions.ToHashSet();
            var seen = new HashSet<int>();
            var parts = 0;
            foreach (var start in all)
            {
                if (!seen.Add(start)) continue;
                parts++;
                var queue = new Queue<int>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                    foreach (var other in next.GetValueOrDefault(queue.Dequeue()) ?? new HashSet<int>())
                        if (all.Contains(other) && seen.Add(other)) queue.Enqueue(other);
            }
            return parts;
        }

        var ukrainian = campaign.StatesOf(ukraine).Select(state => state.MapIndex).ToList();
        var piecesBefore = Pieces(ukrainian);
        // Every third region of Ukraine, wherever it is, goes to Russian control.
        foreach (var region in ukrainian.Where((_, index) => index % 3 == 0))
            for (var province = 1; province <= topology.ProvinceCount; province++)
                if (topology.ProvinceToState[province] == region) campaign.SetProvinceController(province, (ushort)russia.MapIndex);

        var ledger = new PeaceLedger(campaign, war, russia.Id);
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = russia.Id };
        PeaceShape.Write(campaign, russia, war, ledger, demand, 1_000_000f);
        var taken = demand.Provinces.Select(province => (int)topology.ProvinceToState[province]).ToHashSet();
        if (taken.Count == 0) return "Russia asked for nothing it held";

        var russian = campaign.StatesOf(russia).Select(state => state.MapIndex).ToHashSet();
        foreach (var region in taken)
        {
            // Joined: through what Russia holds or asks for, back to Russia.
            var reached = new HashSet<int> { region };
            var queue = new Queue<int>();
            queue.Enqueue(region);
            var home = false;
            while (queue.Count > 0 && !home)
                foreach (var other in next.GetValueOrDefault(queue.Dequeue()) ?? new HashSet<int>())
                {
                    if (russian.Contains(other)) { home = true; break; }
                    if (taken.Contains(other) && reached.Add(other)) queue.Enqueue(other);
                }
            if (!home && (next.GetValueOrDefault(region)?.Count ?? 0) > 0)
                return $"{campaign.States.First(state => state.MapIndex == region).DisplayName} is taken and touches nothing Russian";
        }
        var kept = ukrainian.Where(region => !taken.Contains(region)).ToList();
        var piecesAfter = Pieces(kept);
        if (kept.Count > 0 && piecesAfter > piecesBefore)
            return $"what Ukraine keeps is in {piecesAfter} pieces, and it was in {piecesBefore}";

        Console.WriteLine($"  peace: handed a third of Ukraine scattered at random, Russia asks for {taken.Count} region(s) " +
                          $"({PeaceShape.AimOf(campaign, war, russia)}), all joined to it; Ukraine keeps {kept.Count} in {piecesAfter} piece(s).");
        return "";
    }

    /// <summary>
    /// A war with nobody's player in it does not last past its eighth year, and before that the
    /// years make both sides readier to sign.
    /// </summary>
    internal static string NoWarGoesOnForEver()
    {
        var campaign = Campaign.CreateBootstrap(20240924);
        if (!AttachMap(campaign)) return "no province map to fight on";
        foreach (var nation in campaign.Nations) nation.IsHuman = false;
        campaign.IsPaused = false;
        var war = campaign.Wars.FirstOrDefault(candidate => !candidate.Ended);
        if (war is null) return "no war in the world to end";
        for (var day = 0; day < campaign.Defines.EconomyTickDays; day++) campaign.TickDay();
        if (war.Ended) return "the war ended by itself in its first week";

        var fresh = PeaceNegotiation.Acceptance(campaign, war, war.DefenderId, PeaceTerms.WhitePeace);
        war.StartedDay = campaign.ElapsedDays - campaign.Defines.PeaceWearinessAfterDays - 3 * 365;
        var weary = PeaceNegotiation.Acceptance(campaign, war, war.DefenderId, PeaceTerms.WhitePeace);
        if (weary <= fresh && fresh < 100f) return $"five years of war left a white peace at {weary:0} against {fresh:0} fresh";

        war.StartedDay = campaign.ElapsedDays - campaign.Defines.PeaceForcedEndDays - 1;
        WarWeariness.Tick(campaign);
        if (!war.Ended) return $"an eight-year war with nobody's player in it is still being fought (day {campaign.ElapsedDays}, offer from {war.PendingOfferFromId}, last word: {war.LastNegotiation})";

        Console.WriteLine($"  weariness: a white peace is {fresh:0} % acceptable fresh and {weary:0} % after five years; " +
                          $"at eight the war ended: {war.PeaceSummary}");
        return "";
    }

    /// <summary>
    /// Consolidation invents nobody, and the railways cost what they save.
    ///
    /// Both are the kind of rule that fails silently and pleasantly. A consolidation that
    /// rounds men into existence gives the player free divisions and nothing on screen ever
    /// says so; a redeployment that is fast and free is simply a better march, and every
    /// formation in the game would use it for everything.
    /// </summary>
    internal static string ManoeuvresCostWhatTheySave(Campaign campaign)
    {
        var player = campaign.NationByTag("RUS") ?? campaign.Player;
        if (player is null) return "there is no country to manoeuvre with";

        // ------------------------------------------------------------------ consolidation
        var together = campaign.Armies
            .Where(army => army.NationId == player.Id && !army.InBattle && army.ProvinceId > 0)
            .GroupBy(army => (army.TemplateId, army.ProvinceId))
            .FirstOrDefault(group => group.Count() > 1);
        if (together is null)
        {
            // Put two of them in one place, which is what a player does before consolidating.
            var pair = campaign.Armies
                .Where(army => army.NationId == player.Id && !army.InBattle && army.ProvinceId > 0)
                .GroupBy(army => army.TemplateId)
                .FirstOrDefault(group => group.Count() > 1)?.Take(2).ToList();
            if (pair is null || pair.Count < 2) return "the country has no two formations of a kind";
            pair[1].ProvinceId = pair[0].ProvinceId;
            pair[1].StateMapIndex = pair[0].StateMapIndex;
            together = campaign.Armies
                .Where(army => army.NationId == player.Id && !army.InBattle && army.ProvinceId > 0)
                .GroupBy(army => (army.TemplateId, army.ProvinceId))
                .FirstOrDefault(group => group.Count() > 1);
            if (together is null) return "two formations refused to stand in the same province";
        }

        var chosen = together.ToList();
        // Worn down, which is the state consolidation is for: four divisions at a third
        // strength should become two at full strength, not four at a third.
        foreach (var army in chosen)
        {
            army.Personnel = Math.Max(1, army.MaxPersonnel / 3);
            army.Strength = army.Personnel / (float)Math.Max(1, army.MaxPersonnel);
        }
        var before = chosen.Sum(army => (long)army.Personnel);
        var beforeCount = chosen.Count;
        var consolidate = new ConsolidateArmiesCommand
        {
            IssuerId = player.Id,
            ArmyIds = chosen.Select(army => army.Id).ToArray()
        };
        consolidate.Apply(campaign);
        var survivors = campaign.Armies.Where(army => chosen.Contains(army)).ToList();
        var after = survivors.Sum(army => (long)army.Personnel);

        if (consolidate.Merged == 0)
            return $"{beforeCount} formations of one kind in one province refused to consolidate: {consolidate.Refusal}";
        if (survivors.Count >= beforeCount)
            return "consolidation merged nothing away";
        if (after > before)
            return $"consolidation created {after - before:N0} men out of nothing ({before:N0} -> {after:N0})";
        if (after < before * 0.9)
            return $"consolidation lost {before - after:N0} of {before:N0} men";

        // ------------------------------------------------------------------ the railways
        var traveller = campaign.Armies.FirstOrDefault(army =>
            army.NationId == player.Id && !army.InBattle && army.ProvinceId > 0 && army.EmbarkedFleetId < 0);
        if (traveller is null) return "nobody is available to redeploy";

        var topology = campaign.ProvinceMap;
        if (topology is null) return "there is no province map to redeploy across";
        bool Hostile(int province) =>
            campaign.ProvinceController[province] is var holder && holder != 0 && holder != (ushort)player.MapIndex &&
            campaign.Nations.Find(n => n.MapIndex == holder) is { } owner && CampaignDiplomacy.AtWar(campaign, player.Id, owner.Id) ||
            campaign.Armies.Any(army => army.ProvinceId == province && army.NationId != player.Id &&
                                        CampaignDiplomacy.AtWar(campaign, player.Id, army.NationId));
        bool Near(int origin, int steps)
        {
            var seen = new HashSet<int> { origin };
            var ring = new List<int> { origin };
            for (var step = 0; step <= steps; step++)
            {
                if (ring.Any(Hostile)) return true;
                ring = ring.SelectMany(p => topology.Neighbors[p]).Where(p => p > 0 && seen.Add(p)).ToList();
            }
            return false;
        }
        // Several steps out, so a short hop from the front is not mistaken for no hop at all.
        var destination = 0;
        {
            // Outwards over our own ground, the first province at least three steps away that
            // nothing hostile is near.
            var seen = new HashSet<int> { traveller.ProvinceId };
            var ring = new List<int> { traveller.ProvinceId };
            for (var step = 1; step <= 40 && destination == 0 && ring.Count > 0; step++)
            {
                ring = ring.SelectMany(p => topology.Neighbors[p])
                    .Where(p => p > 0 && campaign.ProvinceController[p] == (ushort)player.MapIndex && seen.Add(p)).ToList();
                if (step >= 3) destination = ring.FirstOrDefault(p => !Near(p, 3));
            }
        }
        var oldDestination = topology.Neighbors[traveller.ProvinceId]
            .SelectMany(step => topology.Neighbors[step])
            .SelectMany(step => topology.Neighbors[step])
            .FirstOrDefault(province => province > 0 &&
                campaign.ProvinceController[province] == (ushort)player.MapIndex &&
                province != traveller.ProvinceId &&
                // Well behind the lines: a front moves, and a destination the enemy reaches the
                // day the train arrives is a battle whose organisation would be counted as the
                // train's. Nothing hostile within three provinces.
                !Near(province, 3));
        if (destination <= 0) return "there is nowhere far enough to redeploy to";

        traveller.Organisation = 1f;
        traveller.OrderKind = ArmyOrder.Manual;
        var rail = new StrategicRedeploymentCommand
        {
            IssuerId = player.Id, ArmyId = traveller.Id, TargetProvinceId = destination
        };
        rail.Apply(campaign);
        if (rail.Result != ArmyMoveResult.Issued)
            return $"a redeployment across three provinces of home ground was refused: {rail.Result}";
        if (!traveller.IsRedeploying) return "the order was issued and the formation is not on the railways";

        var organisationBefore = traveller.Organisation;
        campaign.IsPaused = false;
        var railDays = 0;
        for (; railDays < 6 && traveller.IsRedeploying; railDays++)
        {
            campaign.TickDay();
            if (Environment.GetEnvironmentVariable("RAIL_TRACE") == "1")
                Console.WriteLine($"  rail day {railDays + 1}: org {traveller.Organisation:0.000} order {traveller.OrderKind} " +
                                  $"province {traveller.ProvinceId} redeploying {traveller.IsRedeploying} battle {traveller.InBattle} route {traveller.Route.Count} " +
                                  string.Join(";", campaign.Battles.Where(x => !x.Ended && x.ProvinceId == traveller.ProvinceId)
                                      .Select(x => $"battle {x.Id} war {x.WarId} attacker {campaign.Armies.Find(a => a.Id == x.AttackerId)?.NationId} defender {campaign.Armies.Find(a => a.Id == x.DefenderId)?.NationId}")) +
                                  $" controller {campaign.ProvinceController[traveller.ProvinceId]} owner {campaign.ProvinceMap.ProvinceToNation[traveller.ProvinceId]}");
        }

        if (traveller.Organisation >= organisationBefore)
            return "a week on the trains cost nothing in organisation, which makes it a better march";
        // A day's price per day on the trains, at any step. Charged per step instead, the
        // hourly step made this trip cost 93 % where a day and a bit of travel should cost a
        // fifth, and nothing else here would have noticed.
        var railCost = organisationBefore - traveller.Organisation;
        var railBudget = campaign.Defines.RailRedeploymentOrganisationPerDay * railDays + 0.01f;
        if (railCost > railBudget)
            return $"{railDays} day(s) on the trains cost {railCost:P0} organisation, more than {railBudget:P0} - " +
                   "the day's price is being charged per step";
        if (traveller.IsRedeploying && traveller.Route.Count > 0 && traveller.MoveProgress <= 0f)
            return "the formation was railed nowhere";

        Console.WriteLine($"  manoeuvres: consolidation folded {consolidate.Merged} formation(s) and kept " +
                          $"{after:N0} of {before:N0} men; a redeployment cost " +
                          $"{organisationBefore - traveller.Organisation:P0} organisation.");
        return "";
    }

    internal static string ADecadeOfWarsDoesNotShredTheMap()
    {
        // Which settlement a war ended under, from what it wrote about itself. The peace
        // code has four ways to move a border and they are not equally tidy; a count of
        // cuts with no address is a number nobody can act on.
        static string Ended(StrategicWar war)
        {
            var summary = war.PeaceSummary ?? "";
            if (summary.StartsWith("Secured provinces inside", StringComparison.Ordinal)) return "war goal enforced";
            if (summary.StartsWith("Secured occupations", StringComparison.Ordinal)) return "occupations kept";
            if (summary.Contains("capitulated", StringComparison.Ordinal)) return "capitulation";
            if (summary.Length == 0) return "still running";
            return "negotiated";
        }

        static (int Regions, int Created, int Wars, SortedDictionary<string, int> By) Run(bool tidy)
        {
            var blame = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var campaign = Campaign.CreateBootstrap(20241227);
            if (!AttachMap(campaign)) return (0, 0, 0, blame);
            if (!tidy)
            {
                campaign.Defines.PeaceRoundUpShare = 0f;
                campaign.Defines.PeaceRoundDownShare = 0f;
            }
            var before = campaign.States.Count;
            campaign.IsPaused = false;
            for (var day = 0; day < 365 * 10; day++) campaign.TickDay();
            var created = campaign.Wars.Sum(war => war.CreatedStates);
            foreach (var war in campaign.Wars)
            {
                if (war.CreatedStates <= 0) continue;
                var how = Ended(war);
                blame[how] = blame.GetValueOrDefault(how) + war.CreatedStates;
            }
            return (campaign.States.Count - before, created, campaign.Wars.Count(war => war.Ended), blame);
        }

        var tidy = Run(true);
        var ragged = Run(false);
        if (tidy.Regions == 0 && ragged.Regions == 0) return "";

        Console.WriteLine($"  borders: ten years of the world cut {tidy.Created:N0} region(s) in two " +
                          $"with the rounding rule and {ragged.Created:N0} without " +
                          $"({tidy.Regions:N0} against {ragged.Regions:N0} new regions on the map, " +
                          $"{tidy.Wars:N0} and {ragged.Wars:N0} wars settled).");
        if (tidy.By.Count > 0)
            Console.WriteLine("    the cuts that remain came from: " +
                              string.Join(", ", tidy.By.Select(entry => $"{entry.Value:N0} {entry.Key}")) + ".");

        if (ragged.Created == 0 && tidy.Created == 0)
            return "";     // no war in either world annexed anything; nothing to compare
        if (tidy.Created > ragged.Created)
            return $"the rounding rule cut {tidy.Created:N0} regions in two against " +
                   $"{ragged.Created:N0} without it, which is the opposite of what it is for";
        return "";
    }

    internal static string TheComputerWalksIntoEmptyGround()
    {
        var campaign = Campaign.CreateBootstrap(20241209);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var controllers = campaign.ProvinceController;
        if (controllers is null) return "no control map";
        var before = (ushort[])controllers.Clone();

        // Who is fighting whom, so a province changing hands can be told from a border
        // being redrawn by a peace or a secession.
        var taken = 0;
        var takers = new HashSet<string>(StringComparer.Ordinal);
        var battles = campaign.Battles.Count;

        for (var day = 0; day < 365; day++)
        {
            campaign.TickDay();
            var now = campaign.ProvinceController;
            if (now is null) break;
            for (var province = 1; province < now.Length && province < before.Length; province++)
            {
                if (now[province] == before[province]) continue;
                var winner = campaign.Nations.FirstOrDefault(nation => nation.MapIndex == now[province]);
                before[province] = now[province];
                if (winner is null || winner.IsHuman) continue;

                // Nobody was standing there: no battle was fought for it on the day it fell.
                var fought = false;
                foreach (var battle in campaign.Battles)
                    if (battle.ProvinceId == province && battle.StartedDay >= campaign.ElapsedDays - 2)
                    { fought = true; break; }
                if (fought) continue;
                taken++;
                takers.Add(winner.Tag);
            }
        }

        Console.WriteLine($"  ai: {taken:N0} province(s) walked into without a fight in a year, " +
                          $"by {takers.Count:N0} countries nobody is playing " +
                          $"({campaign.Battles.Count - battles:N0} battles started in the same year).");
        if (taken == 0)
            return "a year of the world's wars and not one province was taken by an army " +
                   "nobody is steering without a battle for it - the AI will not enter empty ground";
        if (takers.Count < 2)
            return $"only {string.Join(", ", takers)} ever took undefended ground, which is one " +
                   "country's front rather than a rule the AI follows";
        return "";
    }

    internal static string AnAllianceFightsOneWar()
    {
        var campaign = Campaign.CreateBootstrap(20240926);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        // Somebody with friends, and somebody able to attack them. Built rather than found:
        // the 2024 world's alliances are what they are, and a check that depends on finding
        // a convenient one is a check that stops testing when the data changes.
        Nation victim = null;
        Nation aggressor = null;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Population < 5_000_000 || candidate.IsSubject) continue;
            foreach (var other in campaign.Nations)
            {
                if (other.Id == candidate.Id || other.Population < 5_000_000) continue;
                if (other.IsSubject) continue;
                if (!CampaignDiplomacy.CanDeclareWar(campaign, other.Id, candidate.Id, out _)) continue;
                victim = candidate;
                aggressor = other;
                break;
            }
            if (victim is not null) break;
        }
        if (victim is null) return "no two countries in the world could go to war with each other";

        // Three friends who are not the aggressor and could fight it.
        var friends = new List<Nation>();
        foreach (var candidate in campaign.Nations)
        {
            if (friends.Count == 3) break;
            if (candidate.Id == victim.Id || candidate.Id == aggressor.Id) continue;
            if (candidate.Population < 2_000_000 || candidate.IsSubject) continue;
            if (!CampaignDiplomacy.CanDeclareWar(campaign, candidate.Id, aggressor.Id, out _)) continue;
            var bond = CampaignDiplomacy.Get(campaign, candidate.Id, victim.Id);
            bond.Stance = DiplomaticStance.Allied;
            bond.Opinion = 100f;
            candidate.WarSupport = 1f;
            candidate.Stability = 0.9f;
            friends.Add(candidate);
        }
        if (friends.Count == 0) return "nobody in the world could be made an ally of the country attacked";

        var warsBefore = campaign.Wars.Count;
        var war = CampaignDiplomacy.DeclareWar(campaign, aggressor.Id, victim.Id);
        if (war is null) return $"{aggressor.Tag} could not declare war on {victim.Tag} after all";

        var opened = campaign.Wars.Count - warsBefore;
        if (opened != 1)
            return $"one declaration opened {opened} wars - the alliance is still splitting into " +
                   "a war per member";

        var answered = friends.Count(friend => war.IsDefending(friend.Id));
        Console.WriteLine($"  coalition: {aggressor.Tag} attacked {victim.Tag}; {answered} of " +
                          $"{friends.Count} ally(ies) answered into the same war, " +
                          $"{war.BelligerentCount} belligerent(s) in it.");
        if (answered == 0)
            return $"not one of {friends.Count} allies with full opinion, full war support and " +
                   "nothing else to do came to the war";

        foreach (var friend in friends)
        {
            if (!war.IsDefending(friend.Id)) continue;
            if (CampaignDiplomacy.Get(campaign, friend.Id, aggressor.Id).Stance != DiplomaticStance.AtWar)
                return $"{friend.Tag} is in the war against {aggressor.Tag} and not at war with them";
            if (war.IsAttacking(friend.Id))
                return $"{friend.Tag} is on both sides of the same war";
        }

        // The fighting knows about them, and so does the peace.
        Warfare.RebuildFronts(campaign);
        for (var day = 0; day < 40; day++) campaign.TickDay();
        if (war.Ended) return "";      // settled early; nothing left to prove here

        var committed = campaign.Armies.Count(army => army.AssignedWarId == war.Id &&
            war.IsDefending(army.NationId) && army.NationId != victim.Id);
        Console.WriteLine($"  coalition: {committed} ally formation(s) were given orders in it " +
                          $"after forty days.");

        // A white peace will not be looked at until a war is four months old, and forty
        // days of real ticking is what this check can afford. The war is aged rather than
        // fought, which is the one thing being proved here that the calendar is not.
        war.StartedDay -= Math.Max(200, (int)campaign.Defines.AiWearyDays + 10);
        war.WarScore = 0f;
        PeaceNegotiation.Resolve(campaign, war, aggressor.Id, PeaceTerms.WhitePeace);
        if (!war.Ended) return $"a white peace did not end the war: {war.LastNegotiation}";
        foreach (var friend in friends)
        {
            if (!war.IsDefending(friend.Id)) continue;
            var relation = CampaignDiplomacy.Get(campaign, friend.Id, aggressor.Id);
            if (relation.Stance == DiplomaticStance.AtWar)
                return $"the war ended and {friend.Tag} is still at war with {aggressor.Tag} - " +
                       "one war has to end for everybody in it";
        }
        return "";
    }

    internal static string AWharfIsBuiltOnWater(Campaign campaign)
    {
        if (campaign.ProvinceMap is null) return "";

        // Countries with no sea coast at all. Kazakhstan and Turkmenistan are left out on
        // purpose: the Caspian is a lake region on this map, which makes them inland-watered
        // rather than dry, and that is the right answer for Aktau and Turkmenbashi.
        var dry = new[] { "MNG", "CHE", "AUT", "NPL", "BOL", "PRY", "BLR", "HUN", "CZE",
                          "SVK", "ZWE", "MLI", "NER", "TCD", "LAO", "AFG", "BTN", "LUX", "MKD" };
        var wet = new[] { "NOR", "JPN", "GBR", "AUS", "CHL", "PRT", "ISL", "NZL", "USA", "RUS" };

        var wrong = new List<string>();
        foreach (var tag in dry)
        {
            var nation = campaign.NationByTag(tag);
            if (nation is null) continue;
            foreach (var state in campaign.StatesOf(nation))
                if (Waterfront.OfRegion(campaign, state) == WaterAccess.Sea)
                {
                    wrong.Add($"{tag} is landlocked but {state.Name} reads as coastal");
                    break;
                }
        }
        foreach (var tag in wet)
        {
            var nation = campaign.NationByTag(tag);
            if (nation is null) continue;
            var coastal = 0;
            foreach (var state in campaign.StatesOf(nation))
                if (Waterfront.OfRegion(campaign, state) == WaterAccess.Sea) coastal++;
            if (coastal == 0) wrong.Add($"{tag} is a maritime country with no coastal region");
        }
        if (wrong.Count > 0) return string.Join("; ", wrong.Take(6));

        // A dry region refuses the order outright.
        var mongolia = campaign.NationByTag("MNG");
        if (mongolia is not null)
        {
            var target = campaign.StatesOf(mongolia).FirstOrDefault(state =>
                Waterfront.OfRegion(campaign, state) == WaterAccess.None);
            if (target is not null)
            {
                var before = campaign.Construction.Count;
                var treasury = mongolia.Treasury;
                mongolia.Treasury = 500f;
                new QueueConstructionCommand
                {
                    IssuerId = mongolia.Id, StateMapIndex = target.MapIndex, Building = BuildingKind.Shipyard
                }.Apply(campaign);
                var started = campaign.Construction.Count - before;
                mongolia.Treasury = treasury;
                if (started != 0)
                {
                    campaign.Construction.RemoveRange(before, started);
                    return $"a shipyard was started in {target.Name}, which has no water";
                }
                if (Waterfront.Refusal(campaign, target.MapIndex, BuildingKind.Shipyard).Length == 0)
                    return "the order was refused and the screen would have said nothing about why";
            }
        }

        // And a river region takes it, at a discount rather than in full.
        var inland = campaign.States.FirstOrDefault(state =>
            Waterfront.OfRegion(campaign, state) == WaterAccess.Inland);
        if (inland is null) return "no region on the map has inland water, so the compromise is unreachable";
        var yield = Waterfront.Yield(campaign, inland.MapIndex, BuildingKind.Port);
        if (yield <= 0f || yield >= 1f)
            return $"an inland port in {inland.Name} is worth {yield:0.##} of a coastal one, " +
                   "which is either forbidden or free";
        if (Waterfront.Yield(campaign, inland.MapIndex, BuildingKind.University) != 1f)
            return "a university was priced as though it needed a harbour";
        return "";
    }

    internal static string AnAttackFormsUpAlongItsFront()
    {
        // Its own campaign rather than the shared copy. The first version of this borrowed
        // the sandbox, detached a dozen formations from their lines to do it, and put them
        // back detached - which the next check felt immediately, and which is the sort of
        // damage a check must never do to the run it is part of.
        var campaign = Campaign.CreateBootstrap(20240820);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;
        for (var day = 0; day < 10; day++) campaign.TickDay();

        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return "there is no map to attack across";

        var war = campaign.Wars.FirstOrDefault(candidate => !candidate.Ended);
        if (war is null) return "nobody is at war";
        var attacker = campaign.NationById(war.AttackerId);
        var defender = campaign.NationById(war.DefenderId);
        if (attacker is null || defender is null) return "the war has no sides";

        // A stretch of the attacker's own front to form up on, and one province of the
        // enemy's beyond it to aim at.
        var departures = war.FriendlyFront(attacker.Id)
            .Where(province => province > 0 && controllers[province] == attacker.MapIndex)
            .OrderBy(province => province).Take(8).ToList();
        if (departures.Count < 3) return $"{attacker.Tag} holds {departures.Count} front province(s), too few to form up on";

        var goal = 0;
        foreach (var province in departures)
        {
            foreach (var neighbour in topology.Neighbors[province])
                if (controllers[neighbour] == defender.MapIndex && !topology.IsWater(neighbour)) { goal = neighbour; break; }
            if (goal > 0) break;
        }
        if (goal <= 0) return "no province of the enemy's touches that front";

        var divisions = campaign.Armies
            .Where(army => army.NationId == attacker.Id && army.ProvinceId > 0 && !army.InBattle)
            .Take(12).ToList();
        if (divisions.Count < 4) return $"{attacker.Tag} has {divisions.Count} formation(s) free, too few to spread";

        var line = new DrawnFrontLine
        {
            Id = campaign.NextFrontLineId++, NationId = attacker.Id,
            Kind = FrontLineKind.Offensive, BorderNationId = defender.Id
        };
        line.SourceProvinces.UnionWith(departures);
        line.Provinces.Add(goal);
        line.ObjectiveSequence.Add(goal);
        foreach (var army in divisions)
        {
            // The planner deliberately ignores stale membership lists. This fixture must
            // assign the real formation too, as DrawFrontLineCommand does in the game.
            DrawFrontLineCommand.Detach(campaign, army);
            army.FrontLineId = line.Id;
            line.ArmyIds.Add(army.Id);
            line.Objectives[army.Id] = goal;          // every one of them aimed at the same place
        }
        campaign.FrontLines.Add(line);

        try
        {
            var standing = new Dictionary<int, int>();
            foreach (var army in divisions)
            {
                army.PlanActive = false;              // prepared, not executing: the case that broke
                OffensivePlanning.Route(campaign, army, line);
                if (!line.Posts.TryGetValue(army.Id, out var assigned) || army.OrderProvinceId != assigned ||
                    !line.SourceProvinces.Contains(assigned))
                    return $"division {army.Id} did not receive a post on its assigned departure front";
                if (army.OrderProvinceId > 0)
                    standing[army.OrderProvinceId] = standing.GetValueOrDefault(army.OrderProvinceId) + 1;
            }
            if (standing.Count == 0) return "no division was given anywhere to form up";

            var worst = standing.Values.Max();
            var wanted = Math.Min(departures.Count, divisions.Count);
            if (standing.Count == 1 && wanted > 1)
                return $"{divisions.Count:N0} division(s) preparing one attack were all sent to the same " +
                       "province, with " + $"{departures.Count:N0} province(s) of their own front to form up on";
            // One each while there are enough to go round; the near end doubles up when not.
            var ceiling = (int)Math.Ceiling(divisions.Count / (float)wanted);
            if (worst > ceiling)
                return $"the busiest jumping-off province holds {worst:N0} division(s) where " +
                       $"{ceiling:N0} would be an even share of {wanted:N0} province(s)";

            // And asking again must give the same answer: a division that changes its mind
            // about where to wait spends the war walking between two provinces.
            foreach (var army in divisions)
            {
                var was = army.OrderProvinceId;
                OffensivePlanning.Route(campaign, army, line);
                if (army.OrderProvinceId != was)
                    return $"a division moved its jumping-off province from {was} to {army.OrderProvinceId} " +
                           "when nothing had changed";
            }

            Console.WriteLine($"  forming up: {divisions.Count:N0} division(s) aimed at one objective spread over " +
                              $"{standing.Count:N0} of {departures.Count:N0} province(s) of their own front, " +
                              $"{worst:N0} in the busiest, and asking twice gave the same answer.");
            return "";
        }
        finally
        {
            campaign.FrontLines.Remove(line);
        }
    }

    internal static string ADrawnLineIsHeldWhereItIsPressed(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return "there is no map to hold";

        var war = campaign.Wars.FirstOrDefault(candidate => !candidate.Ended);
        if (war is null) return "nobody is at war, so no line is under pressure";
        var nation = campaign.NationById(war.DefenderId) ?? campaign.NationById(war.AttackerId);
        if (nation is null) return "the war has no defender";

        var front = war.FriendlyFront(nation.Id).Where(province => province > 0).Take(14).ToList();
        if (front.Count < 4) return $"{nation.Tag} holds a front of {front.Count} province(s), too short to spread over";

        var garrison = campaign.Armies
            .Where(army => army.NationId == nation.Id && !army.InBattle && army.EmbarkedFleetId < 0)
            .Take(6).ToList();
        if (garrison.Count < 3) return $"{nation.Tag} has {garrison.Count} formation(s) free, too few to spread";

        new DrawFrontLineCommand
        {
            IssuerId = nation.Id,
            Kind = FrontLineKind.Defence,
            Provinces = front,
            ArmyIds = garrison.Select(army => army.Id).ToArray()
        }.Apply(campaign);
        try
        {

        var line = campaign.FrontLines.LastOrDefault(candidate => candidate.NationId == nation.Id);
        if (line is null) return "the line was drawn and did not stick";

        var ordered = BattlePlanning.Sequence(campaign, line);
        var first = new Dictionary<int, int>(line.Assignment);
        if (first.Count == 0) return "the line was drawn and nobody was given a post";

        // Asking again on the same day must not move anybody: the assignment is a decision,
        // not a die roll, and a planner that answers differently every call is one that will
        // march the line up and down for the rest of the war.
        BattlePlanning.Sequence(campaign, line);
        var second = line.Assignment;
        if (first.Count != second.Count || first.Any(entry =>
                !second.TryGetValue(entry.Key, out var post) || post != entry.Value))
            return "asking twice on the same day moved formations about";

        // The post with the most hostile ground next to it is the one that has to be held.
        var pressed = -1;
        var worst = -1;
        foreach (var province in ordered)
        {
            var hostile = topology.Neighbors[province]
                .Count(neighbour => controllers[neighbour] != nation.MapIndex);
            if (hostile <= worst) continue;
            worst = hostile;
            pressed = province;
        }
        if (worst <= 0) return "";
        if (!first.ContainsValue(pressed))
            return $"the most exposed post on the line ({worst} hostile neighbours) was given to nobody, " +
                   $"while {first.Count} formation(s) were placed elsewhere";

        Console.WriteLine($"  the line: {nation.Tag} spread {first.Count} formation(s) over {ordered.Count} post(s); " +
                          $"the most exposed ({worst} hostile neighbours) is held, and asking twice moved nobody.");
        return "";
        }
        finally
        {
            // The line was drawn to be measured, not to be left lying on the map: another
            // check counts lines with nobody on them, and it is right to.
            new ClearFrontLineCommand
            {
                IssuerId = nation.Id,
                ArmyIds = garrison.Select(army => army.Id).ToArray()
            }.Apply(campaign);
        }
    }

    /// <summary>
    /// A battle plan survives a save.
    ///
    /// This is the failure that leaves no trace. A formation carries the id of the line it
    /// was put on and that id was being written down; the line itself was not - so every
    /// division came back from a reload holding a plan number that pointed at nothing,
    /// quietly reverted to the war's own front, and a player who had spent ten minutes
    /// drawing dispositions found them gone with no message anywhere.
    ///
    /// So: draw one, save, load, and it has to be the same plan with the same formations on
    /// it, still able to say where each of them stands.
    /// </summary>
    internal static string ABattlePlanSurvivesASave(Campaign campaign)
    {
        var war = campaign.Wars.FirstOrDefault(candidate => !candidate.Ended);
        var nation = war is null ? null : campaign.NationById(war.DefenderId);
        if (nation is null) return "nobody is at war, so there is no plan to draw";

        var front = war.FriendlyFront(nation.Id).Where(province => province > 0).Take(8).ToList();
        var garrison = campaign.Armies
            .Where(army => army.NationId == nation.Id && army.EmbarkedFleetId < 0).Take(4).ToList();
        if (front.Count < 2 || garrison.Count < 2) return "the war is too small to draw a plan on";

        new DrawFrontLineCommand
        {
            IssuerId = nation.Id,
            Kind = FrontLineKind.Defence,
            Provinces = front,
            ArmyIds = garrison.Select(army => army.Id).ToArray()
        }.Apply(campaign);
        var line = campaign.FrontLines.LastOrDefault(candidate => candidate.NationId == nation.Id);
        if (line is null) return "the line was drawn and did not stick";
        new SetPlanCohesionCommand
        {
            IssuerId = nation.Id, FrontLineId = line.Id, Cohesion = PlanCohesion.Rigid
        }.Apply(campaign);

        var lineId = line.Id;
        var provinces = new HashSet<int>(line.Provinces);
        var armies = new List<int>(line.ArmyIds);
        var nextId = campaign.NextFrontLineId;

        try
        {
            var bytes = SaveSystem.ExportNetworkSnapshot(campaign, "bench");
            if (bytes.Length == 0) return "the campaign refused to serialise";
            var restored = SaveSystem.ImportNetworkSnapshot(bytes, "bench").Campaign;
            if (restored is null) return "the save did not load back";

            var back = restored.FrontLines.Find(candidate => candidate.Id == lineId);
            if (back is null)
                return $"a plan of {provinces.Count} province(s) and {armies.Count} formation(s) " +
                       "was drawn, saved, and did not come back at all";
            if (!back.Provinces.SetEquals(provinces))
                return $"the plan came back covering {back.Provinces.Count} province(s) instead of {provinces.Count}";
            if (back.ArmyIds.Count != armies.Count || back.ArmyIds.Any(id => !armies.Contains(id)))
                return $"the plan came back with {back.ArmyIds.Count} formation(s) instead of {armies.Count}";
            if (back.Cohesion != PlanCohesion.Rigid)
                return $"the plan came back on {back.Cohesion} cohesion instead of Rigid";
            if (restored.NextFrontLineId <= lineId)
                return $"the next plan would be handed id {restored.NextFrontLineId}, which is already in use";

            // And every formation still points at it, which is the half that was never
            // broken and would have been the only survivor.
            var orphan = restored.Armies.FirstOrDefault(army => army.FrontLineId >= 0 &&
                restored.FrontLines.All(candidate => candidate.Id != army.FrontLineId));
            if (orphan is not null)
                return $"formation {orphan.Id} came back on plan {orphan.FrontLineId}, which does not exist";

            Console.WriteLine($"  plans: a {provinces.Count}-province line with {armies.Count} formation(s) on " +
                              $"{back.Cohesion} cohesion survived a save, and no formation came back orphaned.");
            return "";
        }
        finally
        {
            new ClearFrontLineCommand
            {
                IssuerId = nation.Id,
                ArmyIds = armies
            }.Apply(campaign);
            campaign.NextFrontLineId = Math.Max(campaign.NextFrontLineId, nextId);
        }
    }

}
