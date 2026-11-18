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

    /// <summary>
    /// The hole the whole naval side was shaped around: every war across water was refused
    /// with "naval invasions are not ported yet", so every island was permanently safe and
    /// a navy had nothing to do.
    ///
    /// Four things have to be true for the word to be earned. A country with ships may
    /// declare a war it has no land border for, and one without ships may not. Divisions
    /// can be loaded onto a fleet, and while aboard they hold no ground - or an invasion
    /// force would still be standing on its own coast. A loaded fleet sent at a shore
    /// arrives and puts them there. And the landing costs them their order, because coming
    /// off a ship onto somebody else's beach should not be free.
    /// </summary>
    internal static string AnArmyCanBePutAshore(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";
        var invader = campaign.NationByTag("GBR");
        if (invader is null) return "no invader to test with";
        var fleet = campaign.Fleets.Find(candidate => candidate.NationId == invader.Id);
        if (fleet is null) return "GBR has no fleet";

        // Somebody across water that GBR shares no land border with - and who has a coast on
        // the same sea, since a landing is made from a ship. The first country in the list
        // used to do, which was as likely to be Mongolia as France.
        var basin = SeaLanes.BasinAt(topology, fleet.Latitude, fleet.Longitude);
        Nation target = null;
        var beach = -1;
        var nearestBeach = float.MaxValue;
        foreach (var province in SeaLanes.CoastalProvinces(topology))
        {
            if (SeaLanes.HarbourBasin(topology, province) != basin) continue;
            var holder = NavalOperations.HolderOf(campaign, province);
            if (holder is null || holder.Id == invader.Id || holder.Population <= 0) continue;
            if (!SeaLanes.TryHarbourPoint(topology, province, out var latitude, out var longitude)) continue;
            var distance = NavalOperations.Distance(fleet.Latitude, fleet.Longitude, latitude, longitude);
            if (distance >= nearestBeach) continue;
            if (CampaignDiplomacy.SharesLandBorder(campaign, invader.MapIndex, holder.MapIndex)) continue;
            if (!CampaignDiplomacy.CanDeclareWar(campaign, invader.Id, holder.Id, out _)) continue;
            nearestBeach = distance;
            target = holder;
            beach = province;
        }
        if (target is null) return "no overseas country to invade";
        if (!CampaignDiplomacy.CanDeclareWar(campaign, invader.Id, target.Id, out var refusal))
            return $"a country with {fleet.Hulls:N0} hulls was refused a war across water: {refusal}";

        var landless = campaign.Nations.Find(nation => nation.Population > 0 &&
            !campaign.Fleets.Exists(candidate => candidate.NationId == nation.Id));
        if (landless is not null)
        {
            var far = campaign.Nations.Find(nation => nation.Id != landless.Id && nation.Population > 0 &&
                !CampaignDiplomacy.SharesLandBorder(campaign, landless.MapIndex, nation.MapIndex));
            if (far is not null && CampaignDiplomacy.CanDeclareWar(campaign, landless.Id, far.Id, out _))
                return $"{landless.Tag} has no ships at all and was still allowed a war across water";
        }

        var war = CampaignDiplomacy.DeclareWar(campaign, invader.Id, target.Id);
        if (war is null) return "the overseas war was refused after all";
        try
        {
            var troops = campaign.Armies.FindAll(army => army.NationId == invader.Id && army.EmbarkedFleetId < 0);
            if (troops.Count == 0) return "GBR has no divisions to load";
            var loaded = troops.GetRange(0, Math.Min(2, troops.Count));
            foreach (var army in loaded)
            {
                // Put them in the fleet's own port so they can walk up the gangway.
                if (fleet.HomeProvinceId > 0)
                {
                    army.ProvinceId = fleet.HomeProvinceId;
                    var where = topology.CenterOf(fleet.HomeProvinceId);
                    army.Latitude = where.Latitude;
                    army.Longitude = where.Longitude;
                }
                army.Organisation = 1f;
            }
            fleet.HasOrder = false;
            fleet.Mission = FleetMission.Anchored;

            new EmbarkArmiesCommand
            {
                IssuerId = invader.Id, FleetId = fleet.Id,
                ArmyIds = loaded.ConvertAll(army => army.Id)
            }.Apply(campaign);
            if (fleet.Carried.Count == 0) return "no division could be loaded onto a fleet in its own port";
            var aboard = campaign.Armies.Find(army => army.Id == fleet.Carried[0]);
            if (aboard is null || aboard.ProvinceId > 0)
                return "a formation aboard a ship was still holding ground ashore";

            // An inland province is refused: a landing is made on a coast.
            var inland = -1;
            for (var province = 1; province <= topology.ProvinceCount && inland < 0; province++)
                if (campaign.ProvinceController[province] == (ushort)target.MapIndex &&
                    SeaLanes.HarbourOf(topology, province) < 0) inland = province;
            if (inland > 0)
            {
                var refused = new LaunchInvasionCommand { IssuerId = invader.Id, FleetId = fleet.Id, ProvinceId = inland };
                refused.Apply(campaign);
                if (refused.Refusal.Length == 0) return "a landing was ordered on a province with no coast";
            }

            var launch = new LaunchInvasionCommand { IssuerId = invader.Id, FleetId = fleet.Id, ProvinceId = beach };
            launch.Apply(campaign);
            if (fleet.Mission != FleetMission.Invasion) return $"the fleet refused the invasion order: {launch.Refusal}";

            var before = aboard.Organisation;
            var sailed = 0;
            for (; sailed < 400 && fleet.Carried.Count > 0; sailed++) SailADay(campaign);
            if (fleet.Carried.Count > 0) return $"the fleet never arrived after {sailed:N0} days at sea";
            if (aboard.ProvinceId <= 0) return "the landing left the formation nowhere";
            if (aboard.Organisation >= before) return "coming ashore on a hostile beach cost nothing";

            Console.WriteLine($"  invasion: {invader.Tag} declared war on {target.Tag} across water, loaded " +
                              $"{loaded.Count:N0} division(s) onto {fleet.Hulls:N0} hulls, sailed {sailed:N0} day(s) " +
                              $"and landed at {aboard.Organisation:P0} organisation against {before:P0} aboard.");
            return "";
        }
        finally
        {
            war.Ended = true;
            foreach (var id in new List<int>(fleet.Carried))
            {
                var army = campaign.Armies.Find(candidate => candidate.Id == id);
                if (army is not null) army.EmbarkedFleetId = -1;
            }
            fleet.Carried.Clear();
            fleet.Mission = FleetMission.Anchored;
            fleet.HasOrder = false;
            fleet.TargetProvinceId = -1;
        }
    }

    /// <summary>
    /// Whether a country at war holds a LINE or stands in a PROVINCE.
    ///
    /// A front is held by being on it. A country that puts a third of its field army into
    /// one province has not concentrated, it has abandoned the rest of the line - and the
    /// side opposite walks through the part nobody is standing on. It is the failure mode a
    /// player sees first and the one the bench could not see at all: every check about
    /// spacing asked about six divisions over nine posts, and the arithmetic that spaces
    /// them only goes wrong once a country has enough of them for the spacing to matter.
    ///
    /// Militia is excluded, as everywhere else: it holds the region it stood up in and was
    /// never part of the field army's order of battle.
    /// </summary>
    internal static string ArmiesHoldLinesRatherThanProvinces(Campaign campaign)
    {
        if (campaign.ProvinceMap is null) return "";

        // Enough of a war to have a line, and enough divisions for a pile to be a choice
        // rather than an accident of having only a few.
        const int enough = 10;
        const double floorShare = 0.34;

        // What a country with this much line is allowed to put in one province. An even
        // spread over N posts is about 1/N each; an army concentrates where it is
        // attacking, so twice that is fair. A long front falls back to the flat third.
        static double Allowed(int front) =>
            front <= 0 ? 1.0 : Math.Min(1.0, Math.Max(floorShare, 2.0 / front));

        var worstShare = 0.0;
        var worstExcess = double.NegativeInfinity;
        var worstName = "";
        var worstStack = 0;
        var worstTotal = 0;
        var worstWhere = "";
        var worstFront = 0;
        var worstFighting = 0;
        var worstArrived = 0;
        var worstPassing = 0;
        var measured = 0;
        var afloat = 0;

        foreach (var nation in campaign.Nations)
        {
            var war = campaign.Wars.Find(candidate => !candidate.Ended && candidate.Involves(nation.Id));
            if (war is null) continue;

            var byProvince = new Dictionary<int, int>();
            var total = 0;
            foreach (var army in campaign.Armies)
            {
                if (army.NationId != nation.Id || army.TemplateId == MilitiaCore.TemplateId) continue;
                if (army.OrderKind == ArmyOrder.Manual) continue;

                // Not on the map is not a province. `ProvinceId` is -1 before an army is
                // placed and 0 while it is aboard a fleet, and bucketing by province id
                // without this puts every one of them in the same imaginary place and
                // reports the result as the worst pile in the world.
                if (army.ProvinceId <= 0) { afloat++; continue; }

                total++;
                // A formation marching through on its way to a post elsewhere is part of the
                // army and not part of the pile: it is leaving. Counting it stood the check on
                // the timing of a snapshot - Iran's sixteen divisions on their post were well
                // inside what a four-province front allows, and seven more passing through the
                // same province towards the other posts made it "a pile". One that has stopped
                // short of its post, with no road left, is standing there and still counts.
                if (army.Route.Count > 0 && army.OrderProvinceId > 0 && army.OrderProvinceId != army.ProvinceId)
                    continue;
                byProvince[army.ProvinceId] = byProvince.GetValueOrDefault(army.ProvinceId) + 1;
            }
            if (total < enough) continue;
            measured++;

            var stack = 0;
            var stackAt = 0;
            foreach (var (province, count) in byProvince)
                if (count > stack) { stack = count; stackAt = province; }

            // Divisions in a battle are not a crowd, they are the battle. An assault is
            // several formations in one province on purpose, and counting them as an
            // abandoned line fails the planner for doing exactly what it should.
            var fighting = campaign.Armies.Count(other =>
                other.NationId == nation.Id && other.ProvinceId == stackAt && other.InBattle);
            var idleStack = Math.Max(0, stack - fighting);
            var share = idleStack / (double)total;

            // How much line there is to hold, and whether the stack is a fight rather than
            // a crowd. A country whose front is one province long cannot spread over forty,
            // and divisions standing in a battle are standing where the war is.
            var front = war.FriendlyFront(nation.Id)?.Count ?? 0;
            var excess = share - Allowed(front);
            if (excess <= worstExcess) continue;

            worstExcess = excess;
            worstShare = share;
            worstName = $"{nation.Tag} {nation.DisplayName}";
            worstStack = stack;
            worstTotal = total;
            worstFront = front;
            worstWhere = string.Join(", ", byProvince
                .OrderByDescending(entry => entry.Value)
                .Take(4)
                .Select(entry => $"{entry.Value:N0} in province {entry.Key:N0}"));
            worstFighting = fighting;
            // Standing on your post is holding a line. Standing in a province on the way to
            // somewhere else is a traffic jam, and the two look identical to a head count.
            worstArrived = campaign.Armies.Count(other =>
                other.NationId == nation.Id && other.ProvinceId == stackAt &&
                other.OrderProvinceId == stackAt);
            worstPassing = campaign.Armies.Count(other =>
                other.NationId == nation.Id && other.ProvinceId == stackAt &&
                other.OrderProvinceId > 0 && other.OrderProvinceId != stackAt);
        }

        if (measured == 0) return "";
        if (worstExcess > 0)
            return $"{worstName} has {worstStack:N0} of its {worstTotal:N0} field divisions standing in " +
                   $"one province, {worstStack - worstFighting:N0} of them not fighting " +
                   $"({worstShare:P0}, and {Allowed(worstFront):P0} is what a front of " +
                   $"{worstFront:N0} province(s) allows) - that is a pile, not a front, and the rest of " +
                   $"the line is held by nobody. Its largest posts: {worstWhere}; of the stack " +
                   $"{worstArrived:N0} are standing on the post they were given, {worstPassing:N0} " +
                   $"are passing through on the way somewhere else, {worstFighting:N0} are in a battle";

        Console.WriteLine($"  fronts: {measured:N0} country(ies) at war with {enough}+ field divisions; " +
                          $"the worst pile is {worstName} with {worstStack:N0} of {worstTotal:N0} in one " +
                          $"province ({worstShare:P0} against the {Allowed(worstFront):P0} a front of " +
                          $"{worstFront:N0} allows); {afloat:N0} division(s) were at sea or not yet " +
                          "placed and are not part of anybody's line.");
        return "";
    }

    /// <summary>
    /// A lake is a place, and nothing walks on it.
    ///
    /// Water used to be paint over land: a province could span half a reservoir and a column
    /// could march across Lake Michigan without anything noticing. Now every lake above the
    /// threshold is a region, so three things have to hold together - it belongs to nobody,
    /// nobody lives on it, and no route on foot ever enters one. (It used to have to belong to
    /// somebody; item 39 made lakes the first regions with no owner, drawn as water rather than
    /// in the colour of whoever held the longest shore.) The third is the one that
    /// matters and the easiest to lose: it is enforced in one place each for movement, fronts
    /// and supply, and any of those three could be edited back out without a symptom until
    /// somebody watched an army walk over Baikal.
    /// </summary>
    internal static string LakesArePlacesArmiesCannotWalkOn(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "the map has no provinces";

        var lakes = campaign.States.Where(LakeRegions.IsWaterState).ToList();
        if (lakes.Count == 0) return "the world has no lakes at all, which no map of Earth manages";

        var faults = new List<string>();
        foreach (var lake in lakes)
        {
            if (lake.NationMapIndex > 0)
                faults.Add($"{lake.Name} belongs to {campaign.Nations.Find(n => n.MapIndex == lake.NationMapIndex)?.Tag ?? "somebody"}, and a lake is nobody's ground");
            for (var province = 1; province <= topology.ProvinceCount; province++)
                if (topology.ProvinceToState[province] == lake.MapIndex && topology.ProvinceToNation[province] != 0)
                {
                    faults.Add($"{lake.Name} has water owned by map index {topology.ProvinceToNation[province]}");
                    break;
                }
            if (lake.Population > 0) faults.Add($"{lake.Name} has {Fmt.People(lake.Population)} living on the water");
        }

        var water = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.IsWater(province)) water++;
        if (water == 0) faults.Add("no province is marked as water, so the lakes are decoration");

        // Walk out of a lakeside province and see whether anything offers to cross.
        var controllers = campaign.ProvinceController;
        var crossings = 0;
        var sampled = 0;
        for (var province = 1; province <= topology.ProvinceCount && sampled < 400; province++)
        {
            if (topology.IsWater(province) || controllers is null) continue;
            var touchesWater = false;
            foreach (var neighbour in topology.Neighbors[province])
                if (topology.IsWater(neighbour)) { touchesWater = true; break; }
            if (!touchesWater) continue;
            sampled++;

            var nation = controllers[province];
            foreach (var neighbour in topology.Neighbors[province])
            {
                if (!topology.IsWater(neighbour)) continue;
                var route = topology.FindRoute(province, neighbour, nation);
                if (route.Length > 0) crossings++;
            }
        }
        if (crossings > 0) faults.Add($"{crossings:N0} route(s) march straight into open water");

        if (faults.Count > 0) return string.Join("; ", faults.Take(6));
        Console.WriteLine($"  lakes: {lakes.Count:N0} region(s) over {water:N0} province(s) of water, " +
            $"none of them anybody's, empty of people and closed to marching ({sampled:N0} shoreline(s) tried).");
        return "";
    }

    /// <summary>
    /// A country arms when it has reason to, and sends men home when it has not.
    ///
    /// The rebuild used to aim at the army a country began the campaign with, with a ceiling
    /// of exactly one, so nobody in the world could arm: Russia at war, Poland next door to
    /// it and Ireland behind two seas all kept the army they had on the first morning. The
    /// establishment follows the military budget now, and the budget already answers to war,
    /// to a rival, to a neighbour's war and to what the treasury bears.
    ///
    /// Checked against the figures rather than against a campaign, because the point is the
    /// shape of the answer: the same country, the same people, the same treasury, and only
    /// the situation different.
    /// </summary>
    internal static string AnArmyAnswersToTheSituation()
    {
        var campaign = Campaign.CreateBootstrap(20240501);
        var nation = campaign.NationByTag("POL");
        if (nation is null) return "";
        var defines = campaign.Defines;

        nation.FoundingDivisions = 40f;
        nation.EstablishedDivisions = 40f;
        nation.ManpowerReserve = 40f * MilitaryBootstrap.DivisionSize * 2f;

        nation.MilitarySpending = defines.AiPeacetimeMilitarySpending;
        var quiet = Attrition.Establishment(campaign, nation);
        nation.MilitarySpending = defines.AiTenseMilitarySpending;
        var tense = Attrition.Establishment(campaign, nation);
        nation.MilitarySpending = defines.AiWartimeMilitarySpending;
        var fighting = Attrition.Establishment(campaign, nation);

        if (!(fighting > tense && tense > quiet))
            return $"the same country wants {quiet:0.0} division(s) at peace, {tense:0.0} under " +
                   $"threat and {fighting:0.0} at war - that is not an order";
        if (fighting > 40f * defines.ArmyDemandCeiling + 0.01f)
            return $"a country at war wants {fighting:0.0} division(s), over its own ceiling of " +
                   $"{40f * defines.ArmyDemandCeiling:0.0}";
        if (quiet < 40f * defines.ArmyDemandFloor - 0.01f)
            return $"a country at peace wants {quiet:0.0} division(s), under its own floor";

        // Men, not just money. The same wartime budget with an empty reserve buys less.
        nation.MilitarySpending = defines.AiWartimeMilitarySpending;
        nation.ManpowerReserve = 0f;
        var empty = Attrition.Establishment(campaign, nation);
        if (empty >= fighting)
            return $"a country with nobody left to call up still wants {empty:0.0} division(s) " +
                   $"against {fighting:0.0} with a full reserve";

        // Two neighbours, same budget, same size, different armies - or every border in the
        // world is a mirror.
        var other = campaign.NationByTag("CZE");
        if (other is null) return "";
        other.FoundingDivisions = 40f;
        other.EstablishedDivisions = 40f;
        other.ManpowerReserve = nation.ManpowerReserve = 40f * MilitaryBootstrap.DivisionSize * 2f;
        other.MilitarySpending = nation.MilitarySpending = defines.AiTenseMilitarySpending;
        if (MathF.Abs(Attrition.Establishment(campaign, nation) -
                      Attrition.Establishment(campaign, other)) < 0.01f)
            return "two countries with the same budget want armies of exactly the same size";

        Console.WriteLine($"  establishment: forty divisions become {quiet:0.0} at peace, {tense:0.0} " +
                          $"under threat and {fighting:0.0} at war; {empty:0.0} with an empty reserve.");
        return "";
    }

    /// <summary>
    /// Every order a player can give crosses the wire intact.
    ///
    /// The codec finds commands by reflection, so a new one is carried automatically - and
    /// that is exactly why this is worth checking. Automatic means nobody looks, and a
    /// command whose fields do not survive the round trip fails only in multiplayer, only
    /// for the client, and only as "nothing happened when I pressed the key".
    /// </summary>
    internal static string EveryOrderCrossesTheWire()
    {
        var orders = new ICampaignCommand[]
        {
            new HaltArmyCommand { IssuerId = 7, ArmyId = 31 },
            new RebuildRegionCommand { IssuerId = 7, StateMapIndex = 1234 },
            new DetachArmyFromPlanCommand { IssuerId = 7, ArmyId = 31 },
            new ReturnArmyToPlanCommand { IssuerId = 7, ArmyId = 31, FrontLineId = 4 },
            new SupportAttackCommand { IssuerId = 7, ArmyId = 31, SupportOnly = true },
            new StrategicRedeploymentCommand { IssuerId = 7, ArmyId = 31, TargetProvinceId = 9012 },
            new ConsolidateArmiesCommand { IssuerId = 7, ArmyIds = new[] { 31, 32, 33 } },
            new SetPlanCohesionCommand { IssuerId = 7, FrontLineId = 4, Cohesion = PlanCohesion.Immobile }
        };

        foreach (var order in orders)
        {
            if (!NetworkCommandCodec.TryEncode(order, out var name, out var json))
                return $"{order.GetType().Name} cannot be sent to another player at all";
            if (!NetworkCommandCodec.TryDecode(name, json, out var back))
                return $"{name} was sent and could not be read back";
            if (back.IssuerId != order.IssuerId)
                return $"{name} came back issued by {back.IssuerId} instead of {order.IssuerId}";
            switch (back)
            {
                case ReturnArmyToPlanCommand ret when ret.FrontLineId != 4:
                    return "a return-to-plan order lost which plan it was returning to";
                case StrategicRedeploymentCommand rail when rail.TargetProvinceId != 9012:
                    return "a redeployment order lost its destination";
                case ConsolidateArmiesCommand fold when fold.ArmyIds.Count != 3:
                    return $"a consolidation order arrived naming {fold.ArmyIds.Count} formation(s) instead of three";
                case SetPlanCohesionCommand cohesion when cohesion.Cohesion != PlanCohesion.Immobile:
                    return $"a cohesion order arrived as {cohesion.Cohesion} instead of Immobile";
                case SupportAttackCommand support when !support.SupportOnly:
                    return "a support order arrived as an ordinary attack, which would take the ground";
            }
        }

        Console.WriteLine($"  wire: {orders.Length} order(s) encoded, sent and read back with every field intact.");
        return "";
    }

    /// <summary>
    /// A simulated day may not hand the collector more than a budget of garbage.
    ///
    /// Wall time on a machine that is also running an editor is noise, and this bench says
    /// so everywhere else. Allocation is not noise: it is the same on every machine, and it
    /// is what the player actually feels, because every megabyte comes back later as a
    /// collection pause in the middle of a war.
    ///
    /// A day cost 1.35 MB and thirty gen-0 collections a simulated year. Almost all of it
    /// was four habits rather than four algorithms - a LINQ Contains on a list, a closure
    /// in a RemoveAll, a JSON trigger re-parsed per question, and prose composed for
    /// cabinets nobody was reading. The budget below is the measured figure with room to
    /// breathe; it is here so that the next feature has to notice when it spends it.
    ///
    /// Tightened from 420 kB once the fifth habit turned up: an AI asking, for every one of
    /// its divisions in every one of its wars every day, what sort of war it was fighting -
    /// and answering by filtering the world's whole order of battle into a fresh array and
    /// averaging it twice. That alone was 116 kB of the day. A budget nothing ever comes
    /// near is not a budget, so it now sits where the next feature will feel it.
    /// </summary>
    internal static string ADayDoesNotBuryTheCollector(double bytesPerDay, int hoursPerTick)
    {
        // What one step hands the collector. This is the figure the budget was measured
        // against and the one a new habit shows up in, because it does not move when the
        // pace does.
        const double perStepBudget = 300 * 1024;
        var stepsADay = Math.Max(1, 24 / Math.Max(1, hoursPerTick));
        var perStep = bytesPerDay / stepsADay;

        // And what a day costs, which is what a second of play at speed five costs thirty
        // times over. It scales with the pace by definition: twenty-four steps of a day's
        // marching allocate twenty-four times what one does, and no amount of tidying takes
        // that back to one. The ceiling is the per-step budget spread over the day.
        var perDayBudget = perStepBudget * stepsADay;

        var worst = SimProfiler.All
            .OrderByDescending(entry => entry.AllocatedBytes)
            .FirstOrDefault();
        var blame = worst is null ? "" :
            $" The largest single pass is '{worst.Name}' at " +
            $"{worst.AllocatedBytes / (double)Math.Max(1, worst.Runs) / 1024.0:N0} kB a run.";

        if (perStep > perStepBudget)
            return $"one step allocated {perStep / 1024.0:N0} kB, over the {perStepBudget / 1024:N0} kB " +
                   $"budget - a day at {hoursPerTick}h a step came to {bytesPerDay / 1024.0:N0} kB." + blame;

        Console.WriteLine($"  garbage: {perStep / 1024.0:N0} kB a step against a {perStepBudget / 1024:N0} kB " +
                          $"budget; at {hoursPerTick}h a step that is {bytesPerDay / 1024.0:N0} kB a campaign " +
                          $"day against {perDayBudget / 1024:N0}.{blame}");
        return "";
    }

    /// <summary>
    /// The same seed gives the same world, twice.
    ///
    /// This is the foundation multiplayer stands on and the only one worth checking first.
    /// Peers do not exchange the world; they exchange the orders given to it and each runs
    /// the same simulation. If two runs of the same seed diverge on their own, they will
    /// diverge across a network too - and the symptom there is not an error but two players
    /// slowly coming to disagree about where the front is, which is unfixable after the fact
    /// and nearly impossible to diagnose from a save.
    ///
    /// A divergence here is nearly always one of three things: a dictionary iterated in hash
    /// order, a clock or an unseeded random read during a tick, or a collection sorted with
    /// no tie-break. All three are silent, and all three show up as the day this stops
    /// matching.
    /// </summary>
    /// <summary>
    /// Whether two campaigns alive at the same time stay out of each other's way.
    ///
    /// This is the multiplayer failure that leaves no trace. Several systems cache expensive
    /// answers - who borders whom, what every country's army is worth, how far apart two
    /// capitals are - and they were stamped with the day, or the map revision, or the number
    /// of countries. All of those are correct for exactly one world at a time, and a client
    /// rebuilding itself from a host snapshot holds two: the new campaign and the old one
    /// that has not been collected yet. Whichever ticks first stamps the cache; the other
    /// reads some other world's answers, agrees with nobody, and diverges without an error
    /// anywhere.
    ///
    /// So: run a campaign alone, then run it again interleaved with a different one, and
    /// require the two results to be identical to the bit.
    /// </summary>
    /// <summary>
    /// Multiplayer is lockstep: peers exchange orders, not worlds, and each runs the same
    /// simulation over the same seed. That holds exactly as long as the two simulations do
    /// the same arithmetic - and the moment the length of a step became a setting, two peers
    /// could be doing different amounts of it without either of them knowing.
    ///
    /// So: two worlds stepped to the same date by different-sized steps must not fingerprint
    /// alike, or the divergence is invisible; and two worlds stepped the same way must, or
    /// the host would be sending snapshots to a client that is perfectly fine.
    /// </summary>
    /// <summary>
    /// A client too far behind is sent the world and then trusted to be in it. If the
    /// snapshot drops anything the simulation reads, it is not - and the next divergence
    /// looks like a new desync rather than a snapshot that never worked.
    /// </summary>
    /// <summary>
    /// The first thing two worlds disagree about, in words.
    ///
    /// Walks what the fingerprint walks, in the same order. A fingerprint that does not
    /// match is a fact with no next step in it; this is the next step.
    /// </summary>
    internal static string WhatDiffers(Campaign left, Campaign right)
    {
        if (left.ElapsedHours != right.ElapsedHours)
            return $"the hour ({left.ElapsedHours:N0} against {right.ElapsedHours:N0})";
        if (left.States.Count != right.States.Count)
            return $"how many regions exist ({left.States.Count:N0} against {right.States.Count:N0})";
        if (left.Nations.Count != right.Nations.Count)
            return $"how many countries exist ({left.Nations.Count:N0} against {right.Nations.Count:N0})";
        if (left.Armies.Count != right.Armies.Count)
            return $"how many formations exist ({left.Armies.Count:N0} against {right.Armies.Count:N0})";

        foreach (var mine in left.Nations)
        {
            var theirs = right.NationById(mine.Id);
            if (theirs is null) return $"{mine.Tag} is missing from the client";
            if (mine.Population != theirs.Population)
                return $"{mine.Tag}'s population ({mine.Population:N0} against {theirs.Population:N0})";
            foreach (var (what, a, b) in new[]
                     {
                         ("treasury", mine.Treasury, theirs.Treasury),
                         ("output", mine.Gdp, theirs.Gdp),
                         ("stability", mine.Stability, theirs.Stability),
                         ("stability baseline", mine.StabilityBias, theirs.StabilityBias),
                         ("governance", mine.StabilityGovernance, theirs.StabilityGovernance),
                         ("war support", mine.WarSupport, theirs.WarSupport),
                         ("manpower", mine.ManpowerReserve, theirs.ManpowerReserve)
                     })
                if (MathF.Abs(a - b) > 0.0000001f)
                    return $"{mine.Tag}'s {what} ({a:0.######} against {b:0.######})";
        }

        foreach (var mine in left.Armies)
        {
            var theirs = right.Armies.Find(army => army.Id == mine.Id);
            if (theirs is null) return $"formation {mine.Id} ({mine.Name}) is missing from the client";
            if (mine.ProvinceId != theirs.ProvinceId)
                return $"where formation {mine.Id} is ({mine.ProvinceId} against {theirs.ProvinceId})";
            if (mine.Personnel != theirs.Personnel)
                return $"formation {mine.Id}'s strength ({mine.Personnel:N0} against {theirs.Personnel:N0})";
            foreach (var (what, a, b) in new[]
                     {
                         ("organisation", mine.Organisation, theirs.Organisation),
                         ("strength", mine.Strength, theirs.Strength),
                         ("entrenchment", mine.Entrenchment, theirs.Entrenchment)
                     })
                if (MathF.Abs(a - b) > 0.0000001f)
                    return $"formation {mine.Id}'s {what} ({a:0.######} against {b:0.######})";
            if (mine.Route.Count != theirs.Route.Count)
                return $"formation {mine.Id}'s route ({mine.Route.Count} step(s) against {theirs.Route.Count})";
        }

        foreach (var mine in left.Wars)
        {
            var theirs = right.Wars.Find(war => war.Id == mine.Id);
            if (theirs is null) return $"war {mine.Id} ({mine.Name}) is missing from the client";
            if (mine.Ended != theirs.Ended) return $"whether war {mine.Id} has ended";
            if (MathF.Abs(mine.OccupationScore - theirs.OccupationScore) > 0.0000001f)
                return $"war {mine.Id}'s occupation score";
            if (MathF.Abs(mine.BattleScore - theirs.BattleScore) > 0.0000001f)
                return $"war {mine.Id}'s battle score";
        }

        var ours = left.ProvinceController;
        var yours = right.ProvinceController;
        if (ours is null || yours is null) return "one of them has no political map at all";
        if (ours.Length != yours.Length)
            return $"how many provinces there are ({ours.Length:N0} against {yours.Length:N0})";
        for (var province = 0; province < ours.Length; province++)
            if (ours[province] != yours[province])
                return $"who holds province {province} ({ours[province]} against {yours[province]})";

        return "nothing this check knows how to look at, which means the fingerprint reads " +
               "something this does not";
    }

    internal static string ASnapshotCarriesTheWholeWorld(int seed = 20240808)
    {
        var host = Campaign.CreateBootstrap(seed);
        if (!AttachMap(host)) return "the host province map could not be attached";
        host.IsPaused = false;
        // Long enough to have the state worth losing: regions cut by the 2024 borders,
        // stability baselines taken, militia part-way towards standing up.
        for (var day = 0; day < 120; day++) host.TickDay();
        if (!string.IsNullOrEmpty(host.SimulationFault) || host.ElapsedDays != 120)
            return $"the host did not complete the 120-day setup: day {host.ElapsedDays}, {host.SimulationFault}";

        var hourly = Environment.GetEnvironmentVariable("SNAP_HOURLY") == "1";
        if (hourly) host.Defines.HoursPerTick = 1;

        var readiness = host.Nations.Sum(nation => nation.MilitiaReadiness.Count);
        var carved = host.States.Count(state => state.ParentMapIndex != 0);
        var biased = host.Nations.Count(nation => MathF.Abs(nation.StabilityBias) > 0.0001f);

        var wire = SaveSystem.ExportNetworkSnapshot(host, "bench");
        if (wire.Length == 0) return "the host produced an empty snapshot";
        var result = SaveSystem.ImportNetworkSnapshot(wire, "bench");
        if (result.Campaign is null)
            return $"the snapshot would not load back: {result.Warning}";

        var client = result.Campaign;
        // The same thing the game does next: a restored campaign stashes its political layer
        // and only puts it back when the map is attached, which on a client is the globe
        // binding the topology. Comparing before that compares a world against half of one.
        if (!AttachMap(client)) return "the client province map could not be attached";
        if (client.ElapsedHours != host.ElapsedHours)
            return $"the clocks differ: hour {host.ElapsedHours:N0} against {client.ElapsedHours:N0}";

        var lost = host.Nations.Sum(nation => nation.MilitiaReadiness.Count) - readiness;
        if (lost != 0) return "exporting the world changed the world it was exported from";
        if (client.Nations.Sum(nation => nation.MilitiaReadiness.Count) != readiness)
            return $"militia readiness did not survive the snapshot: {readiness:N0} region(s) were " +
                   "part-way to raising a formation, and the client would raise them on different days";
        if (client.States.Count(state => state.ParentMapIndex != 0) != carved)
            return $"the regions the borders cut did not survive: {carved:N0} were carved";
        if (client.Nations.Count(nation => MathF.Abs(nation.StabilityBias) > 0.0001f) != biased)
            return $"the stability baselines did not survive: {biased:N0} countries had one";

        // The fronts a war has reached, which the snapshot carries and the client must not
        // recompute. They are not in the fingerprint - they are a set of province ids rather
        // than a number - and they feed supply and combat, so a client that rebuilt them
        // fought a slightly different war from the host and nothing said so.
        foreach (var war in host.Wars)
        {
            var twin = client.Wars.Find(other => other.Id == war.Id);
            if (twin is null) return $"war {war.Id} is missing from the client";
            if (war.AttackerFront.Count != twin.AttackerFront.Count ||
                war.DefenderFront.Count != twin.DefenderFront.Count)
                return $"war {war.Id}'s front did not survive: the host holds " +
                       $"{war.AttackerFront.Count}/{war.DefenderFront.Count} province(s) and the " +
                       $"client {twin.AttackerFront.Count}/{twin.DefenderFront.Count}";
        }

        var before = CampaignFingerprint.Of(host);
        var after = CampaignFingerprint.Of(client);
        if (before != after)
            return "the client is not in the world it was sent: " + WhatDiffers(host, client);

        // And it has to stay that way: a snapshot that matches now and diverges on the next
        // step has carried the numbers and not the state behind them.
        client.IsPaused = false;
        if (Environment.GetEnvironmentVariable("SNAP_DIFF") == "1")
            Console.WriteLine("  snapshot at the moment of sending: " + SnapshotDifferences(host, client));
        for (var day = 0; day < (hourly ? 240 : 10); day++)
        {
            var started = host.ElapsedHours;
            if (hourly) { host.Tick(); client.Tick(); } else { host.TickDay(); client.TickDay(); }
            if (!string.IsNullOrEmpty(host.SimulationFault) || !string.IsNullOrEmpty(client.SimulationFault))
                return $"a peer faulted after adoption: host={host.SimulationFault}; client={client.SimulationFault}";
            if (host.ElapsedHours != started + (hourly ? 1 : 24) || client.ElapsedHours != host.ElapsedHours)
                return $"the peers did not advance the requested step after hour {started}: " +
                       $"host={host.ElapsedHours}, client={client.ElapsedHours}";
            if (CampaignFingerprint.Of(host) == CampaignFingerprint.Of(client)) continue;
            if (Environment.GetEnvironmentVariable("SNAP_ARMIES") == "1")
            {
                var theirs = client.Armies.Select(a => a.Id).ToHashSet();
                var ours = host.Armies.Select(a => a.Id).ToHashSet();
                foreach (var army in host.Armies.Where(a => !theirs.Contains(a.Id)))
                    Console.WriteLine($"  host-only army {army.Id}: {army.Name} nation={army.NationId} " +
                                      $"tpl={army.TemplateId} prov={army.ProvinceId} men={army.Personnel} " +
                                      $"order={army.OrderKind} stance={army.Stance} div={army.Divisions:0.##} " +
                                      $"group={army.ArmyGroupId} war={army.AssignedWarId}");
                foreach (var army in client.Armies.Where(a => !ours.Contains(a.Id)))
                    Console.WriteLine($"  client-only army {army.Id}: {army.Name} nation={army.NationId} " +
                                      $"tpl={army.TemplateId} prov={army.ProvinceId} men={army.Personnel} " +
                                      $"order={army.OrderKind} stance={army.Stance} div={army.Divisions:0.##} " +
                                      $"group={army.ArmyGroupId} war={army.AssignedWarId}");
                foreach (var nation in host.Nations)
                {
                    var twin = client.NationById(nation.Id);
                    if (twin is null) continue;
                    var a = nation.MilitiaReadiness.OrderBy(p => p.Key).ToArray();
                    var b = twin.MilitiaReadiness.OrderBy(p => p.Key).ToArray();
                    if (a.Length != b.Length || a.Where((p, i) => p.Key != b[i].Key || MathF.Abs(p.Value - b[i].Value) > 0.0001f).Any())
                        Console.WriteLine($"  militia readiness differs for nation {nation.Id} ({nation.Tag}): " +
                                          $"host {a.Length} entries, client {b.Length}");
                }
                var hostOrderIds = host.Recruitment.Select(o => o.Id).ToHashSet();
                foreach (var order in host.Recruitment)
                {
                    var twin = client.Recruitment.Find(o => o.Id == order.Id);
                    var deployHost = DealOrForce.Simulation.RecruitmentSystem.DeploymentProvince(host, order.NationId, order.StateMapIndex);
                    var deployClient = twin is null ? -999 : DealOrForce.Simulation.RecruitmentSystem.DeploymentProvince(client, twin.NationId, twin.StateMapIndex);
                    Console.WriteLine($"  host order {order.Id}: nation={order.NationId} state={order.StateMapIndex} " +
                                      $"trained={order.TrainedDays}/{order.DurationDays} last={order.LastProcessedDay} " +
                                      $"deploy={deployHost} | client trained={(twin?.TrainedDays ?? -1)}/{(twin?.DurationDays ?? -1)} " +
                                      $"last={twin?.LastProcessedDay ?? -1} deploy={deployClient}");
                }
                foreach (var order in client.Recruitment.Where(o => !hostOrderIds.Contains(o.Id)))
                {
                    var deployClient = DealOrForce.Simulation.RecruitmentSystem.DeploymentProvince(client, order.NationId, order.StateMapIndex);
                    var nation = client.NationById(order.NationId);
                    var state = client.States.FirstOrDefault(s => s.MapIndex == order.StateMapIndex);
                    Console.WriteLine($"  client-only order {order.Id}: nation={order.NationId}({nation?.Tag}) state={order.StateMapIndex} " +
                                      $"trained={order.TrainedDays}/{order.DurationDays} last={order.LastProcessedDay} deploy={deployClient} " +
                                      $"stateNation={(state?.NationMapIndex ?? -1)} nationMap={(nation?.MapIndex ?? -1)} " +
                                      $"capState={nation?.CapitalStateMapIndex ?? -1}");
                }
                Console.WriteLine($"  recruitment queues: host={host.Recruitment.Count} client={client.Recruitment.Count}; " +
                                  $"JOR armies host={host.Armies.Count(a => a.NationId == 143)} client={client.Armies.Count(a => a.NationId == 143)}");
                foreach (var army in host.Armies.Where(a => a.NationId == 143).OrderBy(a => a.Id))
                    Console.WriteLine($"  host JOR army {army.Id}: {army.Name} prov={army.ProvinceId} tpl={army.TemplateId} men={army.Personnel} plan={army.PlanActive}");
                foreach (var army in client.Armies.Where(a => a.NationId == 143).OrderBy(a => a.Id))
                    Console.WriteLine($"  client JOR army {army.Id}: {army.Name} prov={army.ProvinceId} tpl={army.TemplateId} men={army.Personnel} plan={army.PlanActive}");
            }
            // The first day they part, and where: the saved worlds compared field by field.
            return $"the two worlds agreed at the moment of the snapshot and diverged on {(hourly ? "step" : "day")} {day + 1}, " +
                   "so something the simulation reads is not in it: " + WhatDiffers(host, client) +
                   " | first differences: " + SnapshotDifferences(host, client);
        }

        Console.WriteLine($"  snapshot: a world of {host.States.Count:N0} region(s) ({carved:N0} carved by " +
                          $"borders), {biased:N0} stability baselines and {readiness:N0} region(s) part-way " +
                          "to militia went over the wire and came back identical, and stayed identical for " +
                          $"ten more days (seed {seed}, step {(hourly ? 1 : 24)}h).");
        return "";
    }

    /// <summary>The first few paths at which two worlds' network snapshots differ.</summary>
    internal static string SnapshotDifferences(Campaign one, Campaign other) =>
        SnapshotDifferences(ReadSnapshot(one), ReadSnapshot(other));

    /// <summary>The same diff against a snapshot taken earlier, at the moment it diverged.</summary>
    internal static string SnapshotDifferences(Campaign one, byte[] wire) =>
        SnapshotDifferences(ReadSnapshot(one), ReadWire(wire));

    internal static System.Text.Json.Nodes.JsonNode ReadSnapshot(Campaign campaign)
    {
        var wire = SaveSystem.ExportNetworkSnapshot(campaign, "bench");
        return ReadWire(wire);
    }

    internal static System.Text.Json.Nodes.JsonNode ReadWire(byte[] wire)
    {
        using var input = new MemoryStream(wire);
        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        return System.Text.Json.Nodes.JsonNode.Parse(gzip);
    }

    internal static string SnapshotDifferences(System.Text.Json.Nodes.JsonNode rootA,
        System.Text.Json.Nodes.JsonNode rootB)
    {
        var found = new List<string>();
        void Walk(System.Text.Json.Nodes.JsonNode a, System.Text.Json.Nodes.JsonNode b, string path)
        {
            if (found.Count >= 8) return;
            if (System.Text.Json.Nodes.JsonNode.DeepEquals(a, b)) return;
            if (a is System.Text.Json.Nodes.JsonObject oa && b is System.Text.Json.Nodes.JsonObject ob)
            {
                foreach (var key in oa.Select(pair => pair.Key).Union(ob.Select(pair => pair.Key)))
                    Walk(oa[key], ob[key], path + "." + key);
                return;
            }
            if (a is System.Text.Json.Nodes.JsonArray aa && b is System.Text.Json.Nodes.JsonArray ab && aa.Count == ab.Count)
            {
                for (var i = 0; i < aa.Count; i++) Walk(aa[i], ab[i], $"{path}[{i}]");
                return;
            }
            var left = a?.ToJsonString() ?? "null";
            var right = b?.ToJsonString() ?? "null";
            found.Add($"{path}: {left[..Math.Min(60, left.Length)]} vs {right[..Math.Min(60, right.Length)]}");
        }
        Walk(rootA, rootB, "");
        return found.Count == 0 ? "none in the snapshot itself" : string.Join("; ", found);
    }

    internal static string PeersMustStepTheSameWay()
    {
        static Campaign Run(int hoursPerTick, int hours)
        {
            var campaign = Campaign.CreateBootstrap(20240707);
            campaign.Defines.HoursPerTick = hoursPerTick;
            campaign.IsPaused = false;
            for (var elapsed = 0; elapsed < hours; elapsed += hoursPerTick) campaign.Tick();
            return campaign;
        }

        const int hours = 24 * 24;
        var host = Run(24, hours);
        var alike = Run(24, hours);
        var apart = Run(72, hours);

        var hostPrint = CampaignFingerprint.Of(host);
        if (CampaignFingerprint.Of(alike) != hostPrint)
            return "two peers stepping the same way came out with different worlds, so the host " +
                   "would resynchronise a client that is perfectly fine";
        if (host.ElapsedHours != apart.ElapsedHours)
            return $"the two runs did not reach the same hour: {host.ElapsedHours:N0} against " +
                   $"{apart.ElapsedHours:N0}, so this proves nothing about the fingerprint";
        if (CampaignFingerprint.Of(apart) == hostPrint)
            return "a peer stepping three days at a time fingerprints the same as one stepping " +
                   "one, so a desynchronised client would never be told";

        Console.WriteLine($"  lockstep: {hours / 24} day(s) at 24 hours a step agree with themselves " +
                          $"and disagree with 72, which is what the fingerprint is for.");
        return "";
    }

    internal static string TwoWorldsDoNotShareNotes(int days)
    {
        // Each world run by itself, first, so there is something to be different from.
        // Both of them: whichever campaign ticks second is the one that reads the first
        // one's notes, so checking only the leader would miss the whole problem.
        var firstAlone = Campaign.CreateBootstrap(20250303);
        IsolationTrace.Print(firstAlone, "first-created");
        var secondAlone = Campaign.CreateBootstrap(19991231);
        IsolationTrace.Print(firstAlone, "after-second-created");
        if (!AttachMap(firstAlone) || !AttachMap(secondAlone))
            return "no province map, so there is nothing to share";
        // The two worlds have to be genuinely different, or a cache leaking between them
        // leaks nearly the right answer and nothing shows. Halving one world's armies makes
        // every strength comparison in it different from the other's.
        Halve(secondAlone);
        IsolationTrace.Print(firstAlone, "first-mapped");
        firstAlone.IsPaused = false;
        secondAlone.IsPaused = false;
        // Day-by-day fingerprints of the solo runs, so a mismatch can name the first day the
        // shared-process world stopped agreeing with the single-world one instead of only the
        // last field that still carries the difference.
        var aloneFirst = new List<ulong>(days);
        var aloneSecond = new List<ulong>(days);
        var unseenFirst = new List<ulong>(days);
        var unseenSecond = new List<ulong>(days);
        var probeSubs = Environment.GetEnvironmentVariable("DOF_ISOLATION_PROBE") == "1";
        var probeFirst = new List<ulong[]>(days);
        var probeSecond = new List<ulong[]>(days);
        var probeSame = new List<ulong[]>(days);
        var probeOther = new List<ulong[]>(days);
        for (var day = 0; day < days; day++)
        {
            firstAlone.TickDay();
            aloneFirst.Add(CampaignFingerprint.Of(firstAlone));
            unseenFirst.Add(UnseenWorldHash(firstAlone));
            if (probeSubs) probeFirst.Add(SubsystemProbe(firstAlone));
        }
        for (var day = 0; day < days; day++)
        {
            secondAlone.TickDay();
            aloneSecond.Add(CampaignFingerprint.Of(secondAlone));
            unseenSecond.Add(UnseenWorldHash(secondAlone));
            if (probeSubs) probeSecond.Add(SubsystemProbe(secondAlone));
        }
        IsolationTrace.Print(firstAlone, "first-finished");
        var expectedFirst = CampaignFingerprint.Of(firstAlone);
        var expectedSecond = CampaignFingerprint.Of(secondAlone);

        var same = Campaign.CreateBootstrap(20250303);
        IsolationTrace.Print(same, "repeat-created");
        var other = Campaign.CreateBootstrap(19991231);
        IsolationTrace.Print(same, "repeat-after-other-created");
        if (!AttachMap(same) || !AttachMap(other)) return "the second pair got no province map";
        Halve(other);
        IsolationTrace.Print(same, "repeat-mapped");
        same.IsPaused = false;
        other.IsPaused = false;
        var divergedDay = -1;
        var divergedWhich = "";
        var unseenDay = -1;
        var unseenWhich = "";
        byte[] divergedWire = null;
        for (var day = 0; day < days; day++)
        {
            same.TickDay();
            IsolationTrace.Print(same, "repeat-ticked");
            other.TickDay();
            if (probeSubs) { probeSame.Add(SubsystemProbe(same)); probeOther.Add(SubsystemProbe(other)); }
            if (unseenDay < 0)
            {
                if (UnseenWorldHash(same) != unseenFirst[day]) { unseenDay = day; unseenWhich = "first"; }
                else if (UnseenWorldHash(other) != unseenSecond[day]) { unseenDay = day; unseenWhich = "second"; }
            }
            if (divergedDay < 0)
            {
                if (CampaignFingerprint.Of(same) != aloneFirst[day])
                {
                    divergedDay = day;
                    divergedWhich = "first";
                    divergedWire = SaveSystem.ExportNetworkSnapshot(same, "bench");
                }
                else if (CampaignFingerprint.Of(other) != aloneSecond[day])
                {
                    divergedDay = day;
                    divergedWhich = "second";
                    divergedWire = SaveSystem.ExportNetworkSnapshot(other, "bench");
                }
            }
        }
        if (unseenDay >= 0)
        {
            Console.WriteLine($"ISOLATION_UNSEEN first unfingerprinted-field divergence day={unseenDay} world={unseenWhich} " +
                              $"(fingerprint first diverged day={divergedDay})");
            // Name the regions and building kinds that parted, not just the day: replay both
            // sides to that morning and diff their building tables directly.
            var replayA = Campaign.CreateBootstrap(20250303);
            var replayB = Campaign.CreateBootstrap(19991231);
            if (AttachMap(replayA) && AttachMap(replayB))
            {
                Halve(replayB);
                replayA.IsPaused = false;
                replayB.IsPaused = false;
                var sharedWorld = unseenWhich == "first" ? replayA : replayB;
                for (var step = 0; step <= unseenDay; step++) { replayA.TickDay(); replayB.TickDay(); }
                var soloReplay = Campaign.CreateBootstrap(unseenWhich == "first" ? 20250303 : 19991231);
                if (AttachMap(soloReplay))
                {
                    if (unseenWhich == "second") Halve(soloReplay);
                    soloReplay.IsPaused = false;
                    for (var step = 0; step <= unseenDay; step++) soloReplay.TickDay();
                    DiffBuildings(sharedWorld, soloReplay, unseenDay);
                }
            }
        }

        if (probeSubs)
        {
            var names = new[] { "relations", "market", "stability", "tradepartners", "nationalcourse" };
            for (var slot = 0; slot < names.Length; slot++)
            {
                var firstDiff = -1;
                for (var day = 0; day < days; day++)
                    if (probeSame[day][slot] != probeFirst[day][slot] ||
                        probeOther[day][slot] != probeSecond[day][slot]) { firstDiff = day; break; }
                Console.WriteLine($"ISOLATION_PROBE {names[slot]} firstdiff={(firstDiff < 0 ? "none" : firstDiff.ToString())}");
            }
        }

        var leaderMatches = CampaignFingerprint.Of(same) == expectedFirst;
        var followerMatches = CampaignFingerprint.Of(other) == expectedSecond;
        if (!leaderMatches || !followerMatches)
        {
            Console.WriteLine("ISOLATION_DIFF " + SnapshotDifferences(leaderMatches ? secondAlone : firstAlone,
                leaderMatches ? other : same));
            if (divergedDay >= 0 && divergedWire is not null)
            {
                Console.WriteLine($"ISOLATION_FIRST_DIVERGENCE day={divergedDay} world={divergedWhich}");
                // Replay the diverged world alone up to that day, so the field-level diff is
                // taken at the moment of divergence rather than after the residue has healed.
                var probeSeed = divergedWhich == "first" ? 20250303 : 19991231;
                var probe = Campaign.CreateBootstrap(probeSeed);
                if (AttachMap(probe))
                {
                    if (divergedWhich == "second") Halve(probe);
                    probe.IsPaused = false;
                    for (var step = 0; step <= divergedDay; step++) probe.TickDay();
                    Console.WriteLine($"ISOLATION_DAY{divergedDay}_DIFF " +
                                      SnapshotDifferences(probe, divergedWire));
                }
                if (Environment.GetEnvironmentVariable("DOF_ISOLATION_STAGE_TRACE") == "1")
                {
                    StageBisect(divergedWhich == "first" ? 20250303 : 19991231,
                        divergedWhich == "first" ? 19991231 : 20250303, divergedDay);
                    if (unseenDay >= 0)
                        UnseenStageBisect(unseenWhich == "first" ? 20250303 : 19991231,
                            unseenWhich == "first" ? 19991231 : 20250303, unseenDay);
                }
            }
            return $"the campaign that ticked {(leaderMatches ? "second" : "first")} came out " +
                   $"different when run beside another one for {days} day(s)" +
                   (divergedDay >= 0 ? $" - first divergence on day {divergedDay} of the {divergedWhich} world" : "") +
                   " - something cached is " +
                   "stamped with the day, the map revision or the country count rather than the " +
                   "campaign, and a multiplayer client rebuilding from a snapshot will " +
                   "desynchronise on it";
        }

        Console.WriteLine($"  isolation: a campaign ticked beside a different one for {days} day(s) " +
                          "and came out identical to the bit.");
        return "";
    }

    /// <summary>
    /// Re-run the diverging day with the profiler's stage probe armed, once beside the
    /// other world and once alone, and name the first measured pass whose army fields
    /// stop agreeing. Only runs when DOF_ISOLATION_STAGE_TRACE=1.
    /// </summary>
    internal static void StageBisect(int probeSeed, int otherSeed, int divergedDay)
    {
        // Creation and attach order mirrors the interleaved run: 20250303 first.
        var first = Campaign.CreateBootstrap(20250303);
        var second = Campaign.CreateBootstrap(19991231);
        var probeWorld = probeSeed == 20250303 ? first : second;
        var contaminator = probeSeed == 20250303 ? second : first;
        if (!AttachMap(first) || !AttachMap(second))
        {
            Console.WriteLine("ISOLATION_STAGE no province map");
            return;
        }
        Halve(second);
        first.IsPaused = false;
        second.IsPaused = false;
        for (var day = 0; day < divergedDay; day++)
        {
            first.TickDay();
            second.TickDay();
        }

        var sharedTrace = new List<(string Stage, ulong[][] Groups)>();
        // The real loop ticks 20250303 before 19991231 every day; keep that order so the
        // probe sees the same static-cache history the failing run did.
        if (probeSeed == 20250303)
        {
            Arm(sharedTrace, probeWorld);
            try { probeWorld.TickDay(); } finally { SimProfiler.StageProbe = null; }
            contaminator.TickDay();
        }
        else
        {
            contaminator.TickDay();
            Arm(sharedTrace, probeWorld);
            try { probeWorld.TickDay(); } finally { SimProfiler.StageProbe = null; }
        }

        var solo = Campaign.CreateBootstrap(probeSeed);
        if (!AttachMap(solo))
        {
            Console.WriteLine("ISOLATION_STAGE solo got no province map");
            return;
        }
        if (probeSeed == 19991231) Halve(solo);
        solo.IsPaused = false;
        for (var day = 0; day < divergedDay; day++) solo.TickDay();
        var soloTrace = new List<(string Stage, ulong[][] Groups)>();
        Arm(soloTrace, solo);
        try { solo.TickDay(); } finally { SimProfiler.StageProbe = null; }

        var limit = Math.Min(soloTrace.Count, sharedTrace.Count);
        var firstDiff = -1;
        var diffGroup = -1;
        for (var i = 0; i < limit; i++)
        {
            if (soloTrace[i].Stage != sharedTrace[i].Stage) { firstDiff = i; diffGroup = -2; break; }
            for (var g = 0; g < ArmyFieldGroups; g++)
                if (!soloTrace[i].Groups[g].SequenceEqual(sharedTrace[i].Groups[g]))
                {
                    firstDiff = i;
                    diffGroup = g;
                    break;
                }
            if (firstDiff >= 0) break;
        }
        if (firstDiff < 0 && soloTrace.Count != sharedTrace.Count) { firstDiff = limit; diffGroup = -2; }
        if (firstDiff < 0)
        {
            Console.WriteLine($"ISOLATION_STAGE {soloTrace.Count} measured passes agree - " +
                              "the divergence is written outside a measured stage");
            return;
        }
        Console.WriteLine($"ISOLATION_STAGE first-diff index={firstDiff} " +
                          $"stage='{(firstDiff < soloTrace.Count ? soloTrace[firstDiff].Stage : "-")}' " +
                          $"group={(diffGroup >= 0 ? ArmyGroupNames[diffGroup] : "stage-name")}");
        // Walk the whole armed day for the differing group: the first index where the two
        // series part is the hour the field was written, not just the day it was noticed.
        if (diffGroup >= 0)
        {
            var g = diffGroup;
            var groupFirst = -1;
            for (var i = 0; i < limit; i++)
                if (!soloTrace[i].Groups[g].SequenceEqual(sharedTrace[i].Groups[g])) { groupFirst = i; break; }
            Console.WriteLine($"ISOLATION_STAGE group '{ArmyGroupNames[g]}' first differs at trace index {groupFirst} " +
                              $"of {limit} (stage '{(groupFirst >= 0 && groupFirst < soloTrace.Count ? soloTrace[groupFirst].Stage : "-")}')");
            var differing = new List<int>();
            if (groupFirst >= 0)
                for (var a = 0; a < soloTrace[groupFirst].Groups[g].Length; a++)
                    if (soloTrace[groupFirst].Groups[g][a] != sharedTrace[groupFirst].Groups[g][a]) differing.Add(a);
            Console.WriteLine($"ISOLATION_STAGE armies differing in '{ArmyGroupNames[g]}' at that stage: {differing.Count} " +
                              $"(first ids: {string.Join(",", differing.Take(8).Select(i => solo.Armies[i].Id))})");
            DumpGroupSeries(soloTrace, sharedTrace, g, differing);
            var wanted = differing.Count > 0 ? solo.Armies[differing[0]].Id : -1;
            var rawEnv = Environment.GetEnvironmentVariable("DOF_ISOLATION_ARMY");
            if (int.TryParse(rawEnv, out var rawId)) wanted = rawId;
            if (wanted >= 0) RawArmyTrace(probeSeed, otherSeed, divergedDay, wanted);
        }
    }

    /// <summary>
    /// The same bisect as <see cref="StageBisect"/>, but over the fields the campaign
    /// fingerprint does not mix: march progress, routes, order bookkeeping, supply detail.
    /// The first fingerprint-visible divergence can sit weeks downstream of the write that
    /// caused it; this names the stage and formation at the day the invisible state parts.
    /// </summary>
    internal static void UnseenStageBisect(int probeSeed, int otherSeed, int divergedDay)
    {
        var first = Campaign.CreateBootstrap(20250303);
        var second = Campaign.CreateBootstrap(19991231);
        var probeWorld = probeSeed == 20250303 ? first : second;
        var contaminator = probeSeed == 20250303 ? second : first;
        if (!AttachMap(first) || !AttachMap(second))
        {
            Console.WriteLine("ISOLATION_UNSEEN_STAGE no province map");
            return;
        }
        Halve(second);
        first.IsPaused = false;
        second.IsPaused = false;
        for (var day = 0; day < divergedDay; day++)
        {
            first.TickDay();
            second.TickDay();
        }

        var sharedTrace = new List<(string Stage, ulong[][] Groups)>();
        if (probeSeed == 20250303)
        {
            SimProfiler.StageProbe = name => sharedTrace.Add((name, UnseenFieldsGroups(probeWorld)));
            try { probeWorld.TickDay(); } finally { SimProfiler.StageProbe = null; }
            contaminator.TickDay();
        }
        else
        {
            contaminator.TickDay();
            SimProfiler.StageProbe = name => sharedTrace.Add((name, UnseenFieldsGroups(probeWorld)));
            try { probeWorld.TickDay(); } finally { SimProfiler.StageProbe = null; }
        }

        var solo = Campaign.CreateBootstrap(probeSeed);
        if (!AttachMap(solo))
        {
            Console.WriteLine("ISOLATION_UNSEEN_STAGE solo got no province map");
            return;
        }
        if (probeSeed == 19991231) Halve(solo);
        solo.IsPaused = false;
        for (var day = 0; day < divergedDay; day++) solo.TickDay();
        var soloTrace = new List<(string Stage, ulong[][] Groups)>();
        SimProfiler.StageProbe = name => soloTrace.Add((name, UnseenFieldsGroups(solo)));
        try { solo.TickDay(); } finally { SimProfiler.StageProbe = null; }

        var limit = Math.Min(soloTrace.Count, sharedTrace.Count);
        var firstDiff = -1;
        var diffGroup = -1;
        for (var i = 0; i < limit; i++)
        {
            if (soloTrace[i].Stage != sharedTrace[i].Stage) { firstDiff = i; diffGroup = -2; break; }
            for (var g = 0; g < UnseenFieldGroups; g++)
                if (!soloTrace[i].Groups[g].SequenceEqual(sharedTrace[i].Groups[g]))
                {
                    firstDiff = i;
                    diffGroup = g;
                    break;
                }
            if (firstDiff >= 0) break;
        }
        if (firstDiff < 0 && soloTrace.Count != sharedTrace.Count) { firstDiff = limit; diffGroup = -2; }
        if (firstDiff < 0)
        {
            Console.WriteLine($"ISOLATION_UNSEEN_STAGE {soloTrace.Count} measured passes agree - " +
                              "the unseen fields parted outside a measured stage");
            return;
        }
        Console.WriteLine($"ISOLATION_UNSEEN_STAGE first-diff index={firstDiff} " +
                          $"stage='{(firstDiff < soloTrace.Count ? soloTrace[firstDiff].Stage : "-")}' " +
                          $"group={(diffGroup >= 0 ? UnseenGroupNames[diffGroup] : "stage-name")}");
        if (diffGroup < 0) return;
        var g2 = diffGroup;
        var groupFirst = -1;
        for (var i = 0; i < limit; i++)
            if (!soloTrace[i].Groups[g2].SequenceEqual(sharedTrace[i].Groups[g2])) { groupFirst = i; break; }
        var differing = new List<int>();
        var lengthGap = 0;
        if (groupFirst >= 0)
        {
            var soloGroup = soloTrace[groupFirst].Groups[g2];
            var sharedGroup = sharedTrace[groupFirst].Groups[g2];
            lengthGap = soloGroup.Length - sharedGroup.Length;
            // A formation can fall during the day, so the two captures are not always the
            // same length; only the shared prefix can be compared index for index.
            for (var a = 0; a < Math.Min(soloGroup.Length, sharedGroup.Length); a++)
                if (soloGroup[a] != sharedGroup[a]) differing.Add(a);
        }
        Console.WriteLine($"ISOLATION_UNSEEN_STAGE group '{UnseenGroupNames[g2]}' first differs at trace index {groupFirst} " +
                          $"of {limit} (stage '{(groupFirst >= 0 && groupFirst < soloTrace.Count ? soloTrace[groupFirst].Stage : "-")}')" +
                          (lengthGap != 0 ? $" - army count differs by {lengthGap}" : ""));
        Console.WriteLine($"ISOLATION_UNSEEN_STAGE armies differing: {differing.Count} " +
                          $"(first ids: {string.Join(",", differing.Take(8).Select(i => i < solo.Armies.Count ? solo.Armies[i].Id : -1))})");
        if (differing.Count > 0 && differing[0] < solo.Armies.Count)
        {
            var tracedId = solo.Armies[differing[0]].Id;
            UnseenRawTrace(probeSeed, otherSeed, divergedDay, tracedId);
            FactorTrace(probeSeed, otherSeed, divergedDay, tracedId);
        }
    }

    internal const int UnseenFieldGroups = 4;

    internal static readonly string[] UnseenGroupNames = { "march/route", "order", "supply", "condition" };

    /// <summary>Per formation, the unfingerprinted fields in four mixes: the march itself, the order it carries, its supply detail, and its condition.</summary>
    internal static ulong[][] UnseenFieldsGroups(Campaign campaign)
    {
        var march = new ulong[campaign.Armies.Count];
        var order = new ulong[campaign.Armies.Count];
        var supply = new ulong[campaign.Armies.Count];
        var condition = new ulong[campaign.Armies.Count];
        for (var i = 0; i < march.Length; i++)
        {
            var army = campaign.Armies[i];
            var m = 14695981039346656037UL;
            m = ArmyMix(m, (uint)army.Id);
            m = ArmyMix(m, BitConverter.SingleToUInt32Bits(army.MoveProgress));
            m = ArmyMix(m, (uint)army.PlannedRoute.Count);
            foreach (var step in army.PlannedRoute) m = ArmyMix(m, (uint)step);
            m = ArmyMix(m, (uint)army.Route.Count);
            m = ArmyMix(m, (uint)army.LastMoveResult);
            m = ArmyMix(m, (uint)army.LastMoveResultDay);
            m = ArmyMix(m, BitConverter.SingleToUInt32Bits(army.LastReinforced));
            march[i] = m;

            var o = 14695981039346656037UL;
            o = ArmyMix(o, (uint)army.Id);
            o = ArmyMix(o, (uint)army.OrderKind);
            o = ArmyMix(o, (uint)army.OrderProvinceId);
            o = ArmyMix(o, (uint)army.OrderStateMapIndex);
            o = ArmyMix(o, (uint)army.ObjectiveStateMapIndex);
            o = ArmyMix(o, army.DirectOrder ? 1u : 0u);
            o = ArmyMix(o, (uint)army.OrderRouteSteps);
            o = ArmyMix(o, (uint)army.OrderStepsCompleted);
            o = ArmyMix(o, (uint)army.OrderIssuedDay);
            o = ArmyMix(o, army.PlanActive ? 1u : 0u);
            o = ArmyMix(o, BitConverter.SingleToUInt32Bits(army.Planning));
            o = ArmyMix(o, (uint)army.ReadyDay);
            o = ArmyMix(o, (uint)army.FrontLineId);
            o = ArmyMix(o, (uint)army.StateMapIndex);
            order[i] = o;

            var s = 14695981039346656037UL;
            s = ArmyMix(s, (uint)army.Id);
            s = ArmyMix(s, BitConverter.SingleToUInt32Bits(army.SupplyLevel));
            s = ArmyMix(s, (uint)army.SupplyDistance);
            s = ArmyMix(s, (uint)army.SupplySourceStateMapIndex);
            s = ArmyMix(s, BitConverter.SingleToUInt32Bits(army.SupplyCongestion));
            supply[i] = s;

            var c = 14695981039346656037UL;
            c = ArmyMix(c, (uint)army.Id);
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Divisions));
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Recovering));
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Experience));
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Cohesion));
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Recon));
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Engineers));
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Artillery));
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Latitude));
            c = ArmyMix(c, BitConverter.SingleToUInt32Bits(army.Longitude));
            condition[i] = c;
        }
        return new[] { march, order, supply, condition };
    }

    /// <summary>Field-level unseen trace for one formation across the diverging day.</summary>
    internal static void UnseenRawTrace(int probeSeed, int otherSeed, int divergedDay, int armyId)
    {
        var first = Campaign.CreateBootstrap(20250303);
        var second = Campaign.CreateBootstrap(19991231);
        var probeWorld = probeSeed == 20250303 ? first : second;
        var contaminator = probeSeed == 20250303 ? second : first;
        if (!AttachMap(first) || !AttachMap(second)) return;
        Halve(second);
        first.IsPaused = false;
        second.IsPaused = false;
        for (var day = 0; day < divergedDay; day++) { first.TickDay(); second.TickDay(); }

        var sharedRaw = new List<string>();
        ArmUnseenRaw(sharedRaw, probeWorld, armyId);
        try
        {
            if (probeSeed == 20250303) { probeWorld.TickDay(); contaminator.TickDay(); }
            else { contaminator.TickDay(); probeWorld.TickDay(); }
        }
        finally { SimProfiler.StageProbe = null; }

        var solo = Campaign.CreateBootstrap(probeSeed);
        if (!AttachMap(solo)) return;
        if (probeSeed == 19991231) Halve(solo);
        solo.IsPaused = false;
        for (var day = 0; day < divergedDay; day++) solo.TickDay();
        var soloRaw = new List<string>();
        ArmUnseenRaw(soloRaw, solo, armyId);
        try { solo.TickDay(); } finally { SimProfiler.StageProbe = null; }

        var limit = Math.Min(soloRaw.Count, sharedRaw.Count);
        var firstDiff = -1;
        for (var i = 0; i < limit; i++)
            if (soloRaw[i] != sharedRaw[i]) { firstDiff = i; break; }
        Console.WriteLine($"ISOLATION_UNSEEN_RAW army={armyId} stages={limit} firstDiff={firstDiff}");
        if (firstDiff < 0) return;
        var lo = Math.Max(0, firstDiff - 6);
        var hi = Math.Min(limit, firstDiff + 10);
        for (var i = lo; i < hi; i++)
        {
            if (i == firstDiff || soloRaw[i] != sharedRaw[i])
                Console.WriteLine($"ISOLATION_UNSEEN_RAW [{i}] {(i == firstDiff ? ">>" : "  ")} solo='{soloRaw[i]}' shared='{sharedRaw[i]}'");
        }
    }

    /// <summary>
    /// Names the march multiplier that parted: replays the unseen-divergence day with
    /// MilitaryOperations.MarchProbe armed on one formation, once beside the other world
    /// and once alone, and prints the first step whose factor string differs.
    /// </summary>
    internal static void FactorTrace(int probeSeed, int otherSeed, int divergedDay, int armyId)
    {
        var first = Campaign.CreateBootstrap(20250303);
        var second = Campaign.CreateBootstrap(19991231);
        var probeWorld = probeSeed == 20250303 ? first : second;
        var contaminator = probeSeed == 20250303 ? second : first;
        if (!AttachMap(first) || !AttachMap(second)) return;
        Halve(second);
        first.IsPaused = false;
        second.IsPaused = false;
        for (var day = 0; day < divergedDay; day++) { first.TickDay(); second.TickDay(); }

        var sharedTrace = new List<string>();
        MilitaryOperations.MarchProbeArmyId = armyId;
        MilitaryOperations.MarchProbe = (c, army, factors) =>
        {
            if (c.ElapsedDays == divergedDay) sharedTrace.Add($"h{c.ElapsedHours}:{factors}");
        };
        try
        {
            if (probeSeed == 20250303) { probeWorld.TickDay(); contaminator.TickDay(); }
            else { contaminator.TickDay(); probeWorld.TickDay(); }
        }
        finally { MilitaryOperations.MarchProbe = null; MilitaryOperations.MarchProbeArmyId = -1; }

        var solo = Campaign.CreateBootstrap(probeSeed);
        if (!AttachMap(solo)) return;
        if (probeSeed == 19991231) Halve(solo);
        solo.IsPaused = false;
        for (var day = 0; day < divergedDay; day++) solo.TickDay();
        var soloTrace = new List<string>();
        MilitaryOperations.MarchProbeArmyId = armyId;
        MilitaryOperations.MarchProbe = (c, army, factors) =>
        {
            if (c.ElapsedDays == divergedDay) soloTrace.Add($"h{c.ElapsedHours}:{factors}");
        };
        try { solo.TickDay(); } finally { MilitaryOperations.MarchProbe = null; MilitaryOperations.MarchProbeArmyId = -1; }

        var limit = Math.Min(soloTrace.Count, sharedTrace.Count);
        var firstDiff = -1;
        for (var i = 0; i < limit; i++)
            if (soloTrace[i] != sharedTrace[i]) { firstDiff = i; break; }
        Console.WriteLine($"ISOLATION_FACTOR army={armyId} day={divergedDay} steps={limit} firstDiff={firstDiff}");
        if (firstDiff < 0) return;
        var lo = Math.Max(0, firstDiff - 4);
        var hi = Math.Min(limit, firstDiff + 6);
        for (var i = lo; i < hi; i++)
            Console.WriteLine($"ISOLATION_FACTOR [{i}] {(i == firstDiff ? ">>" : "  ")} solo='{soloTrace[i]}' shared='{sharedTrace[i]}'");
    }

    internal static void ArmUnseenRaw(List<string> trace, Campaign world, int armyId)
    {
        SimProfiler.StageProbe = name =>
        {
            var army = world.Armies.Find(a => a.Id == armyId);
            trace.Add(army is null ? "absent" :
                $"{name}|mv={army.MoveProgress:R}|pr={army.PlannedRoute.Count}[{string.Join(',', army.PlannedRoute.Take(4))}]|" +
                $"rt={army.Route.Count}[{string.Join(',', army.Route.Take(4))}]|ok={(int)army.OrderKind}|op={army.OrderProvinceId}|" +
                $"os={army.OrderStateMapIndex}|obj={army.ObjectiveStateMapIndex}|dir={army.DirectOrder}|" +
                $"ors={army.OrderRouteSteps}|osc={army.OrderStepsCompleted}|oid={army.OrderIssuedDay}|" +
                $"sl={army.SupplyLevel:R}|sd={army.SupplyDistance}|sss={army.SupplySourceStateMapIndex}|sc={army.SupplyCongestion:R}|" +
                $"rd={army.ReadyDay}|fl={army.FrontLineId}|sm={army.StateMapIndex}|plan={army.PlanActive}|pl={army.Planning:R}|" +
                $"lmr={(int)army.LastMoveResult}|lmrd={army.LastMoveResultDay}|lr={army.LastReinforced:R}");
        };
    }

    /// <summary>
    /// Third pass: replay the diverging day once more and print the target formation's raw
    /// fields at every measured stage, so a transient route or battle assignment shows as
    /// the hours it covered instead of a single end-of-day number.
    /// </summary>
    internal static void RawArmyTrace(int probeSeed, int otherSeed, int divergedDay, int armyId)
    {
        var first = Campaign.CreateBootstrap(20250303);
        var second = Campaign.CreateBootstrap(19991231);
        var probeWorld = probeSeed == 20250303 ? first : second;
        var contaminator = probeSeed == 20250303 ? second : first;
        if (!AttachMap(first) || !AttachMap(second)) return;
        Halve(second);
        first.IsPaused = false;
        second.IsPaused = false;
        for (var day = 0; day < divergedDay; day++) { first.TickDay(); second.TickDay(); }

        var sharedRaw = new List<string>();
        ArmRaw(sharedRaw, probeWorld, armyId);
        try
        {
            if (probeSeed == 20250303) { probeWorld.TickDay(); contaminator.TickDay(); }
            else { contaminator.TickDay(); probeWorld.TickDay(); }
        }
        finally { SimProfiler.StageProbe = null; }

        var solo = Campaign.CreateBootstrap(probeSeed);
        if (!AttachMap(solo)) return;
        if (probeSeed == 19991231) Halve(solo);
        solo.IsPaused = false;
        for (var day = 0; day < divergedDay; day++) solo.TickDay();
        var soloRaw = new List<string>();
        ArmRaw(soloRaw, solo, armyId);
        try { solo.TickDay(); } finally { SimProfiler.StageProbe = null; }

        var limit = Math.Min(soloRaw.Count, sharedRaw.Count);
        var firstDiff = -1;
        for (var i = 0; i < limit; i++)
            if (soloRaw[i] != sharedRaw[i]) { firstDiff = i; break; }
        Console.WriteLine($"ISOLATION_RAW army={armyId} stages={limit} firstDiff={firstDiff}");
        if (firstDiff < 0) return;
        var lo = Math.Max(0, firstDiff - 6);
        var hi = Math.Min(limit, firstDiff + 10);
        for (var i = lo; i < hi; i++)
            Console.WriteLine($"ISOLATION_RAW [{i}] {(i == firstDiff ? ">>" : "  ")} solo='{soloRaw[i]}' shared='{sharedRaw[i]}'");
    }

    internal static void ArmRaw(List<string> trace, Campaign world, int armyId)
    {
        SimProfiler.StageProbe = name =>
        {
            var army = world.Armies.Find(a => a.Id == armyId);
            trace.Add(army is null ? "absent" :
                $"{name}|prov={army.ProvinceId}|route={army.Route.Count}|plan={army.PlannedRoute.Count}|" +
                $"move={army.MoveProgress:R}|battle={army.BattleId}|order={(int)army.OrderKind}|" +
                $"stance={(int)army.Stance}|ent={army.Entrenchment:R}|supply={army.SupplyLevel:R}");
        };
    }

    internal const int ArmyFieldGroups = 3;

    internal static readonly string[] ArmyGroupNames = { "position/order", "attrition", "entrenchment" };

    /// <summary>For each differing army, the stage-by-stage values of the group's fields.</summary>
    internal static void DumpGroupSeries(List<(string Stage, ulong[][] Groups)> solo,
        List<(string Stage, ulong[][] Groups)> shared, int group, List<int> armies)
    {
        // Reconstruct per-stage per-army field detail is not stored; instead print, for the
        // first differing army, its hash series around the first difference so the eye can
        // see whether it flips once or drifts.
        foreach (var a in armies.Take(3))
        {
            var flips = 0;
            var firstAt = -1;
            for (var i = 0; i < Math.Min(solo.Count, shared.Count); i++)
            {
                var d = solo[i].Groups[group][a] != shared[i].Groups[group][a];
                if (d && firstAt < 0) firstAt = i;
                if (d) flips++;
            }
            Console.WriteLine($"ISOLATION_SERIES armyIdx={a} group='{ArmyGroupNames[group]}' firstDiffStage={firstAt} " +
                              $"stagesDiffering={flips}/{Math.Min(solo.Count, shared.Count)}");
        }
    }

    internal static void Arm(List<(string Stage, ulong[][] Groups)> trace, Campaign world)
    {
        SimProfiler.StageProbe = name => trace.Add((name, ArmyFieldsGroups(world)));
    }

    /// <summary>
    /// Per formation, three independent mixes so a divergence names its field group:
    /// where it stands and what it was told to do, what the fighting did to it, and the
    /// digging - the field the failing snapshot happened to show.
    /// </summary>
    internal static ulong[][] ArmyFieldsGroups(Campaign campaign)
    {
        var position = new ulong[campaign.Armies.Count];
        var attrition = new ulong[campaign.Armies.Count];
        var entrenchment = new ulong[campaign.Armies.Count];
        for (var i = 0; i < position.Length; i++)
        {
            var army = campaign.Armies[i];
            var p = 14695981039346656037UL;
            p = ArmyMix(p, (uint)army.Id);
            p = ArmyMix(p, (uint)army.ProvinceId);
            p = ArmyMix(p, (uint)army.Route.Count);
            p = ArmyMix(p, (uint)army.PlannedRoute.Count);
            p = ArmyMix(p, BitConverter.SingleToUInt32Bits(army.MoveProgress));
            p = ArmyMix(p, army.InBattle ? 1u : 0u);
            p = ArmyMix(p, (uint)army.BattleId);
            p = ArmyMix(p, (uint)(int)army.Stance);
            p = ArmyMix(p, army.CutOff ? 1u : 0u);
            p = ArmyMix(p, (uint)army.DaysCutOff);
            p = ArmyMix(p, (uint)army.OrderKind);
            position[i] = p;

            var a = 14695981039346656037UL;
            a = ArmyMix(a, BitConverter.SingleToUInt32Bits(army.Organisation));
            a = ArmyMix(a, BitConverter.SingleToUInt32Bits(army.Strength));
            a = ArmyMix(a, BitConverter.SingleToUInt32Bits(army.SupplyLevel));
            a = ArmyMix(a, BitConverter.SingleToUInt32Bits(army.Planning));
            a = ArmyMix(a, BitConverter.SingleToUInt32Bits(army.Experience));
            a = ArmyMix(a, BitConverter.SingleToUInt32Bits(army.Cohesion));
            a = ArmyMix(a, BitConverter.SingleToUInt32Bits(army.Equipment));
            a = ArmyMix(a, (uint)army.Personnel);
            attrition[i] = a;

            entrenchment[i] = ArmyMix(14695981039346656037UL, BitConverter.SingleToUInt32Bits(army.Entrenchment));
        }
        return new[] { position, attrition, entrenchment };
    }

    internal static ulong ArmyMix(ulong hash, uint value) => (hash ^ value) * 1099511628211UL;

    /// <summary>
    /// The formation fields the campaign fingerprint deliberately does not mix - march
    /// progress, planned routes, supply detail, order bookkeeping. Two worlds can agree on
    /// every fingerprinted byte and still be marching differently; this hash is how the
    /// isolation bench sees that before it reaches a visible field.
    /// </summary>
    /// <summary>
    /// Everything the campaign fingerprint does not mix that can still move the world:
    /// the formation fields of <see cref="UnseenArmyHash"/> plus every region's building
    /// counts - the fingerprint never reads Buildings, so a construction divergence sits
    /// invisible until roads, supply or taxes carry it into a mixed field weeks later.
    /// </summary>
    /// <summary>Prints every region whose building counts differ between two same-seed worlds.</summary>
    internal static void DiffBuildings(Campaign shared, Campaign solo, int day)
    {
        var soloByIndex = new Dictionary<int, ScenarioState>();
        foreach (var state in solo.States) soloByIndex[state.MapIndex] = state;
        var printed = 0;
        foreach (var state in shared.States.OrderBy(s => s.MapIndex))
        {
            if (!soloByIndex.TryGetValue(state.MapIndex, out var other)) continue;
            var kinds = state.Buildings.Keys.Union(other.Buildings.Keys).Distinct().OrderBy(k => (int)k);
            foreach (var kind in kinds)
            {
                var a = state.Buildings.GetValueOrDefault(kind);
                var b = other.Buildings.GetValueOrDefault(kind);
                if (a == b) continue;
                var owner = shared.Nations.FirstOrDefault(n => n.MapIndex == state.NationMapIndex);
                Console.WriteLine($"ISOLATION_BUILDING day={day} state={state.MapIndex}({state.Id}) kind={kind} " +
                                  $"shared={a} solo={b} owner={owner?.Tag ?? "?"}");
                if (++printed >= 20) return;
            }
        }
        if (printed == 0) Console.WriteLine($"ISOLATION_BUILDING day={day} building tables identical - divergence is elsewhere");
    }

    /// <summary>
    /// Per-subsystem world hashes for DOF_ISOLATION_PROBE: which derived table first stops
    /// agreeing between a solo world and the same world ticked beside another. Slots:
    /// relations, market, stability, tradepartners, nationalcourse.
    /// </summary>
    internal static ulong[] SubsystemProbe(Campaign campaign)
    {
        var relations = 14695981039346656037UL;
        foreach (var relation in campaign.Relations.Values)
        {
            relations = ArmyMix(relations, (uint)relation.A);
            relations = ArmyMix(relations, (uint)relation.B);
            relations = ArmyMix(relations, BitConverter.SingleToUInt32Bits(relation.Opinion));
            relations = ArmyMix(relations, (uint)relation.Stance);
            relations = ArmyMix(relations, (uint)relation.Reasons.Count);
        }
        var market = 14695981039346656037UL;
        foreach (var nation in campaign.Nations)
            foreach (var quote in nation.Market.Values)
            {
                market = ArmyMix(market, (uint)quote.Commodity);
                market = ArmyMix(market, BitConverter.SingleToUInt32Bits(quote.Production));
                market = ArmyMix(market, BitConverter.SingleToUInt32Bits(quote.Demand));
                market = ArmyMix(market, BitConverter.SingleToUInt32Bits(quote.Imports));
                market = ArmyMix(market, BitConverter.SingleToUInt32Bits(quote.Exports));
                market = ArmyMix(market, BitConverter.SingleToUInt32Bits(quote.Price));
                market = ArmyMix(market, BitConverter.SingleToUInt32Bits(quote.Satisfaction));
            }
        var stability = 14695981039346656037UL;
        foreach (var nation in campaign.Nations)
        {
            stability = ArmyMix(stability, BitConverter.SingleToUInt32Bits(nation.Stability));
            stability = ArmyMix(stability, BitConverter.SingleToUInt32Bits(nation.StabilityDrivers?.Total ?? 0f));
            stability = ArmyMix(stability, BitConverter.SingleToUInt32Bits(nation.IncomeFactor));
            stability = ArmyMix(stability, BitConverter.SingleToUInt32Bits(nation.Gdp));
        }
        var trade = 14695981039346656037UL;
        foreach (var nation in campaign.Nations)
            foreach (var (partner, value) in nation.TradePartners)
            {
                trade = ArmyMix(trade, (uint)nation.Id);
                trade = ArmyMix(trade, (uint)partner);
                trade = ArmyMix(trade, BitConverter.SingleToUInt32Bits(value));
            }
        var course = 14695981039346656037UL;
        foreach (var nation in campaign.Nations)
            foreach (var (id, conviction) in nation.NationalCourse.Convictions)
            {
                course = ArmyMix(course, (uint)nation.Id);
                foreach (var character in id) course = ArmyMix(course, character);
                course = ArmyMix(course, BitConverter.SingleToUInt32Bits(conviction));
            }
        return new[] { relations, market, stability, trade, course };
    }

    internal static ulong UnseenWorldHash(Campaign campaign)
    {
        var hash = UnseenArmyHash(campaign);
        var states = campaign.States.ToArray();
        Array.Sort(states, static (a, b) => a.MapIndex.CompareTo(b.MapIndex));
        foreach (var state in states)
        {
            hash = ArmyMix(hash, (uint)state.MapIndex);
            var kinds = new List<BuildingKind>(state.Buildings.Keys);
            kinds.Sort(static (a, b) => ((int)a).CompareTo((int)b));
            foreach (var kind in kinds)
            {
                hash = ArmyMix(hash, (uint)kind);
                hash = ArmyMix(hash, (uint)state.Buildings[kind]);
            }
        }
        return hash;
    }

    internal static ulong UnseenArmyHash(Campaign campaign)
    {
        var hash = 14695981039346656037UL;
        foreach (var army in campaign.Armies)
        {
            hash = ArmyMix(hash, (uint)army.Id);
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.MoveProgress));
            hash = ArmyMix(hash, (uint)army.PlannedRoute.Count);
            foreach (var step in army.PlannedRoute) hash = ArmyMix(hash, (uint)step);
            hash = ArmyMix(hash, (uint)army.OrderKind);
            hash = ArmyMix(hash, (uint)army.OrderProvinceId);
            hash = ArmyMix(hash, (uint)army.OrderStateMapIndex);
            hash = ArmyMix(hash, (uint)army.ObjectiveStateMapIndex);
            hash = ArmyMix(hash, army.DirectOrder ? 1u : 0u);
            hash = ArmyMix(hash, (uint)army.OrderRouteSteps);
            hash = ArmyMix(hash, (uint)army.OrderStepsCompleted);
            hash = ArmyMix(hash, (uint)army.OrderIssuedDay);
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.SupplyLevel));
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Planning));
            hash = ArmyMix(hash, (uint)army.SupplyDistance);
            hash = ArmyMix(hash, (uint)army.SupplySourceStateMapIndex);
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.SupplyCongestion));
            hash = ArmyMix(hash, (uint)army.ReadyDay);
            hash = ArmyMix(hash, (uint)army.FrontLineId);
            hash = ArmyMix(hash, (uint)army.StateMapIndex);
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Divisions));
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Recovering));
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Experience));
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Cohesion));
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Recon));
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Engineers));
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Artillery));
            hash = ArmyMix(hash, army.PlanActive ? 1u : 0u);
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Latitude));
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.Longitude));
            hash = ArmyMix(hash, (uint)army.LastMoveResult);
            hash = ArmyMix(hash, (uint)army.LastMoveResultDay);
            hash = ArmyMix(hash, BitConverter.SingleToUInt32Bits(army.LastReinforced));
        }
        return hash;
    }

    /// <summary>Take half the world's formations away, deterministically.</summary>
    internal static void Halve(Campaign campaign)
    {
        for (var index = campaign.Armies.Count - 1; index >= 0; index--)
            if (campaign.Armies[index].Id % 2 == 0) campaign.Armies.RemoveAt(index);
    }

    internal static string TheSameSeedGivesTheSameWorld(int days)
    {
        var first = Campaign.CreateBootstrap(20260830);
        if (!AttachMap(first)) return "no province map, so there is nothing to diverge about";
        var second = Campaign.CreateBootstrap(20260830);
        if (!AttachMap(second)) return "the second campaign got no province map";

        first.IsPaused = false;
        second.IsPaused = false;
        for (var day = 1; day <= days; day++)
        {
            first.TickDay();
            second.TickDay();
            var left = CampaignFingerprint.Of(first);
            var right = CampaignFingerprint.Of(second);
            if (left == right) continue;
            return $"two runs of seed 20260830 disagreed on day {day} - " +
                   "multiplayer cannot be stable while this is true. Look for a dictionary " +
                   "iterated in hash order, a clock read inside a tick, or a sort with no tie-break";
        }

        Console.WriteLine($"  determinism: two runs of one seed agreed to the bit for {days} day(s) - " +
                          $"{first.Armies.Count:N0} formations and {first.Nations.Count:N0} countries.");
        return "";
    }

}
