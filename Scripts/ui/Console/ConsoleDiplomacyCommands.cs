using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Developer console commands for war, diplomacy, alliances, subjects, armies, and fleets.
/// </summary>
internal static class ConsoleDiplomacyCommands
{
    public static void Register(DeveloperConsole console, ConsoleContext ctx, RegisterConsoleCommand add)
    {
        // WAR
        add("wars", "WAR", "wars", "Every war being fought, with its score and its fronts.", _ => Wars(ctx));
        add("declare", "WAR", "declare RUS", "Declare war, and say why if it is refused.", a => Declare(ctx, a));
        add("whitepeace", "WAR", "whitepeace [RUS]",
            "End a war where it stands, taking nothing.", a => WhitePeace(ctx, a), "endwar");
        add("warscore", "WAR", "warscore 60",
            "Set the score of the war you are fighting, to reach a peace worth testing.",
            a => WarScore(ctx, a));
        add("peace", "WAR", "peace", "Open the peace conference for your war.",
            _ => PeaceConference(console, ctx), "conference");
        add("battles", "WAR", "battles",
            "Every battle being fought right now: where, who, how it is going.", _ => Battles(ctx));
        add("fronts", "WAR", "fronts", "The length and score of every front.", _ => Fronts(ctx));
        add("occupy", "WAR", "occupy [PROVINCE]",
            "Hand a province to your country's control. The selected one unless a number is given.",
            a => Occupy(ctx, a));
        add("annex", "WAR", "annex LUX",
            "Take a whole country. Blunt on purpose - it is for reaching a state of the world, " +
            "not for playing.", a => Annex(ctx, a));

        // DIPLOMACY
        add("alliances", "DIPLOMACY", "alliances [DEU]",
            "Who a country is allied to, who it has guaranteed, and what is on its table.",
            a => Alliances(ctx, a), "allies");
        add("ally", "DIPLOMACY", "ally FRA",
            "Offer an alliance, or accept the one you have been offered. What they answer " +
            "and why is printed.", a => Ally(ctx, a), "alliance");
        add("unally", "DIPLOMACY", "unally FRA",
            "Walk out of one. They remember it.", a => Unally(ctx, a), "breakalliance");
        add("guarantee", "DIPLOMACY", "guarantee LUX [off]",
            "Promise to fight for a country that has not asked you to be its ally - the " +
            "cheap half of an alliance, and the useful half of being a great power.",
            a => Guarantee(ctx, a));
        add("subjects", "DIPLOMACY", "subjects [DEU]",
            "Who answers to a country, under what arrangement, and how badly each of them " +
            "wants out.", a => Subjects(ctx, a), "puppets");
        add("puppet", "DIPLOMACY", "puppet LUX [puppet|protectorate|satellite|dominion|client]",
            "Make a country answer to yours. The kinds come from common/subjects, so a mod " +
            "may add its own.", a => Subjugate(ctx, a), "subjugate");
        add("release", "DIPLOMACY", "release LUX", "Let one go.", a => Release(ctx, a));
        add("integrate", "DIPLOMACY", "integrate LUX",
            "Take a country that answers to yours into it. Only after the years its kind asks " +
            "for, and only while it is not trying to leave - `subjects` says how far off both are.",
            a => Integrate(ctx, a), "absorb");
        add("relations", "DIPLOMACY", "relations [DEU]",
            "How the world feels about a country, warmest and coldest first, with the reasons.",
            a => Relations(ctx, a), "opinions");
        add("opinion", "DIPLOMACY", "opinion RUS -100",
            "Set what another country thinks of you.", a => Opinion(ctx, a));

        // ARMIES
        add("armies", "ARMIES", "armies [DEU]",
            "What a country has under arms, and where the weight of it is.",
            a => Armies(ctx, a), "divisions");
        add("orders", "ARMIES", "orders",
            "What the selected divisions were told to do, when, how far along they are, and " +
            "what the last attempt to move them returned. This is what a division that will " +
            "not advance is diagnosed with.", _ => Orders(ctx), "why");
        add("spawn", "ARMIES", "spawn 3 [line_infantry]",
            "Put divisions in the capital, fully formed, without training them.",
            a => Spawn(ctx, a));
        add("teleport", "ARMIES", "teleport PROVINCE",
            "Move the selected divisions to a province at once.", a => Teleport(ctx, a), "tp");
        add("heal", "ARMIES", "heal [all]",
            "Fill the selected divisions back up - men, morale, equipment, supply. " +
            "`heal all` does the whole army.", a => Heal(ctx, a), "reinforce");
        add("kill", "ARMIES", "kill [all]",
            "Delete the selected divisions, or the whole army with `all`.", a => Kill(ctx, a), "disband");
        add("groups", "ARMIES", "groups", "Army headquarters and what hangs off them.", _ => Groups(ctx));
        add("fleets", "ARMIES", "fleets [DEU]", "Ships, where they are and what they are doing.",
            a => Fleets(ctx, a), "navy");
    }

    private static string Wars(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var running = campaign.Wars.Where(war => !war.Ended).ToArray();
        if (running.Length == 0) return "The world is at peace.";
        return string.Join("\n", running.Select(war =>
        {
            var attacker = campaign.NationById(war.AttackerId);
            var defender = campaign.NationById(war.DefenderId);
            return ConsoleContext.EscapeBb($"  {attacker?.Tag} vs {defender?.Tag} · {war.Name}\n" +
                            $"      score {war.WarScore:0.0} (battles {war.BattleScore:0.0}, " +
                            $"occupation {war.OccupationScore:0.0}) · started day {war.StartedDay:N0}\n" +
                            $"      fronts {war.AttackerFront.Count:N0}/{war.DefenderFront.Count:N0} · " +
                            $"losses {war.AttackerLosses:N0}/{war.DefenderLosses:N0}");
        }));
    }

    private static string Declare(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var target = ctx.ResolveNation(argument);
        if (target is null) return "Usage: declare RUS";
        if (target.Id == campaign.Player.Id) return "A country cannot declare war on itself.";
        if (!CampaignDiplomacy.CanDeclareWar(campaign, campaign.Player.Id, target.Id, out var reason))
            return $"Refused: {ConsoleContext.EscapeBb(reason)}";
        var war = CampaignDiplomacy.DeclareWar(campaign, campaign.Player.Id, target.Id);
        ctx.Refresh();
        return war is null ? "The declaration was refused." : $"War declared: {ConsoleContext.EscapeBb(war.Name)}.";
    }

    private static string WhitePeace(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var other = ctx.ResolveNation(argument);
        var wars = campaign.Wars.Where(war => !war.Ended && war.Involves(campaign.Player.Id) &&
                                               (other is null || argument.Trim().Length == 0 ||
                                                war.Involves(other.Id))).ToArray();
        if (wars.Length == 0) return "No war to end.";
        foreach (var war in wars)
        {
            war.Ended = true;
            war.EndedDay = campaign.ElapsedDays;
            war.WinnerId = -1;
            war.PeaceSummary = "White peace, from the console.";
        }
        campaign.MarkStrategicChange();
        ctx.Refresh();
        return $"Ended {wars.Length} war(s) where they stood.";
    }

    private static string WarScore(ConsoleContext ctx, string value)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || !ConsoleContext.TryFloat(value, out var score)) return "Usage: warscore 60";
        var war = campaign.Wars.FirstOrDefault(candidate =>
            !candidate.Ended && candidate.Involves(campaign.Player.Id));
        if (war is null) return $"{campaign.Player.Name} is not at war.";
        var low = MathF.Min(campaign.Defines.WarScoreMin, campaign.Defines.WarScoreMax);
        var high = MathF.Max(campaign.Defines.WarScoreMin, campaign.Defines.WarScoreMax);
        var signed = war.AttackerId == campaign.Player.Id ? score : -score;
        war.WarScore = Math.Clamp(signed, low, high);
        ctx.Refresh();
        return $"{ConsoleContext.EscapeBb(war.Name)}: war score set to {war.WarScore:0.0}. `peace` opens the conference.";
    }

    private static string PeaceConference(DeveloperConsole console, ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var war = campaign.Wars.FirstOrDefault(candidate => !candidate.Ended && candidate.Involves(campaign.Player.Id));
        if (war is null) return $"{campaign.Player.Name} is not at war.";
        console.Close();
        ctx.OpenPeaceConference(war);
        return "";
    }

    private static string Battles(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var fighting = campaign.Battles.Where(battle => !battle.Ended).ToArray();
        if (fighting.Length == 0) return "Nothing is being fought over anywhere.";
        return string.Join("\n", fighting.Take(30).Select(battle =>
        {
            var attacker = campaign.NationById(battle.AttackerId);
            var defender = campaign.NationById(battle.DefenderId);
            return $"  #{battle.Id} province {battle.ProvinceId:N0} · {attacker?.Tag} attacking {defender?.Tag} · " +
                   $"{battle.Attackers.Count}v{battle.Defenders.Count} · started day {battle.StartedDay:N0} · " +
                   $"round {battle.Rounds.Count}";
        }).Append(fighting.Length > 30 ? $"  …and {fighting.Length - 30:N0} more" : ""));
    }

    private static string Fronts(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var running = campaign.Wars.Where(war => !war.Ended).ToArray();
        if (running.Length == 0) return "There are no fronts; the world is at peace.";
        return string.Join("\n", running.Select(war =>
        {
            var drawn = campaign.FrontLines.Count(line =>
                line.NationId == war.AttackerId || line.NationId == war.DefenderId);
            return ConsoleContext.EscapeBb($"{war.Name}: {war.AttackerFront.Count:N0}/{war.DefenderFront.Count:N0} front " +
                            $"province(s) · {drawn} drawn line(s) · score {war.WarScore:0.0} · " +
                            $"last rebuilt day {war.LastFrontDay:N0}");
        }));
    }

    private static string Occupy(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || campaign.ProvinceMap is null) return "No campaign.";
        var province = ctx.GetSelectedProvinceId();
        if (argument.Trim().Length > 0 && !int.TryParse(argument.Trim(), out province))
            return "Usage: occupy [PROVINCE_ID]";
        if (province <= 0 || province > campaign.ProvinceMap.ProvinceCount)
            return "Select a province, or give its number.";
        if (!campaign.SetProvinceController(province, (ushort)campaign.Player.MapIndex))
            return $"Province #{province:N0} would not change hands.";
        campaign.MarkMapChanged();
        ctx.Refresh();
        return $"Province #{province:N0} is now held by {campaign.Player.Tag}.";
    }

    private static string Annex(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || campaign.ProvinceMap is null) return "No campaign.";
        var target = ctx.ResolveNation(argument);
        if (target is null || target.Id == campaign.Player.Id) return "Usage: annex LUX";
        var map = campaign.ProvinceMap;
        var taken = new List<int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == target.MapIndex) taken.Add(province);
        if (taken.Count == 0) return $"{target.Tag} holds no ground.";
        StatePartition.Annex(campaign, taken, campaign.Player, asConquest: true);
        campaign.MarkMapChanged();
        ctx.Refresh();
        return $"{campaign.Player.Tag} annexed {taken.Count:N0} province(s) from {target.Name}.";
    }

    private static string Alliances(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var nation = ctx.ResolveNation(argument);
        if (nation is null) return "No such country.";

        var lines = new List<string>();
        foreach (var ally in AllianceSystem.AlliesOf(campaign, nation))
        {
            var relation = CampaignDiplomacy.Get(campaign, nation.Id, ally.Id);
            var since = relation.AllianceSinceDay > -9000
                ? $", since day {relation.AllianceSinceDay:N0}"
                : "";
            lines.Add($"  ally · {ally.Tag} {ConsoleContext.EscapeBb(ally.Name)} · opinion {relation.Opinion:+0;-0;0}{since}");
        }
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
            if (relation is null) continue;
            if (relation.GuaranteedBy(nation.Id))
                lines.Add($"  guarantees · {other.Tag} {ConsoleContext.EscapeBb(other.Name)}");
            if (relation.GuaranteedBy(other.Id))
                lines.Add($"  guaranteed by · {other.Tag} {ConsoleContext.EscapeBb(other.Name)}");
            if (relation.NonAggressionPact)
                lines.Add($"  pact · {other.Tag} {ConsoleContext.EscapeBb(other.Name)}");
        }

        var offered = AllianceSystem.OfferedTo(campaign, nation);
        if (offered is not null)
            lines.Insert(0, $"  ON THE TABLE · {offered.Tag} {ConsoleContext.EscapeBb(offered.Name)} has offered an alliance. " +
                            "`ally " + offered.Tag + "` accepts it.");
        return lines.Count == 0
            ? $"{nation.Tag} has signed nothing with anybody."
            : string.Join("\n", lines);
    }

    private static string Ally(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var target = ctx.ResolveNation(argument);
        if (target is null) return "Usage: ally FRA";

        var offered = AllianceSystem.OfferedTo(campaign, campaign.Player);
        if (offered is not null && offered.Id == target.Id)
        {
            AllianceSystem.Answer(campaign, campaign.Player, true, out var outcome);
            ctx.Refresh();
            return ConsoleContext.EscapeBb(outcome);
        }

        var why = new Breakdown();
        var willingness = AllianceSystem.Willingness(campaign, target, campaign.Player, why);
        var signed = AllianceSystem.Propose(campaign, campaign.Player, target, out var answer);
        ctx.Refresh();
        var parts = new List<string> { ConsoleContext.EscapeBb(answer) };
        if (!signed && why.HasEntries)
        {
            parts.Add($"  willingness {willingness:0.00} against {campaign.Defines.AllianceWillingness:0.00} needed:");
            foreach (var entry in why.Entries)
                parts.Add($"    {ConsoleContext.EscapeBb(entry.Source)} {entry.Value:+0.00;-0.00;0.00}");
        }
        return string.Join("\n", parts);
    }

    private static string Unally(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var target = ctx.ResolveNation(argument);
        if (target is null) return "Usage: unally FRA";
        if (!AllianceSystem.Break(campaign, campaign.Player, target, out var refusal))
            return $"Refused: {ConsoleContext.EscapeBb(refusal)}";
        ctx.Refresh();
        return $"{campaign.Player.Tag} is no longer allied to {ConsoleContext.EscapeBb(target.Name)}.";
    }

    private static string Guarantee(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var words = ConsoleContext.Words(argument);
        if (words.Length == 0) return "Usage: guarantee LUX [off]";
        var target = ctx.ResolveNation(words[0]);
        if (target is null) return $"No country called '{ConsoleContext.EscapeBb(words[0])}'.";
        var on = words.Length < 2 || !string.Equals(words[1], "off", StringComparison.OrdinalIgnoreCase);
        new GuaranteeCommand { IssuerId = campaign.Player.Id, TargetId = target.Id, Guarantee = on }
            .Apply(campaign);
        ctx.Refresh();
        var relation = CampaignDiplomacy.Get(campaign, campaign.Player.Id, target.Id);
        return relation.GuaranteedBy(campaign.Player.Id)
            ? $"{campaign.Player.Tag} guarantees {ConsoleContext.EscapeBb(target.Name)}."
            : $"{campaign.Player.Tag} no longer guarantees {ConsoleContext.EscapeBb(target.Name)}.";
    }

    private static string Subjects(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var nation = ctx.ResolveNation(argument);
        if (nation is null) return "No such country.";

        var lines = new List<string>();
        var mine = SubjectSystem.SubjectsOf(campaign, nation);
        foreach (var subject in mine)
        {
            var kind = SubjectCatalog.Of(subject.SubjectType);
            var held = campaign.ElapsedDays - subject.SubjectSinceDay;
            var grace = kind is not null && held < kind.GraceDays
                ? $", settling for another {kind.GraceDays - held:N0} day(s)"
                : "";
            lines.Add($"  {subject.Tag} {ConsoleContext.EscapeBb(subject.Name)} · {kind?.Name ?? subject.SubjectType} · " +
                      $"wants out {subject.LibertyDesire:P0} of the way " +
                      $"(pull {SubjectSystem.LibertyDesireOf(campaign, subject):P0}){grace}");
        }

        var over = SubjectSystem.OverlordOf(campaign, nation);
        if (over is not null)
            lines.Insert(0, $"  {nation.Tag} itself answers to {over.Tag} as a " +
                            $"{SubjectCatalog.Of(nation.SubjectType)?.Name ?? nation.SubjectType}.");
        if (lines.Count == 0)
            return $"{nation.Tag} holds nobody and answers to nobody. " +
                   $"Kinds available: {string.Join(", ", SubjectCatalog.All.Select(kind => kind.Id))}";
        return string.Join("\n", lines);
    }

    private static string Subjugate(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var words = ConsoleContext.Words(argument);
        if (words.Length == 0)
            return "Usage: puppet LUX [kind]. Kinds: " +
                   string.Join(", ", SubjectCatalog.All.Select(kind => kind.Id));
        var target = ctx.ResolveNation(words[0]);
        if (target is null) return $"No country called '{ConsoleContext.EscapeBb(words[0])}'.";
        var kindId = words.Length > 1 ? words[1] : "puppet";
        if (!SubjectSystem.Subjugate(campaign, campaign.Player, target, kindId, out var refusal))
            return $"Refused: {ConsoleContext.EscapeBb(refusal)}";
        ctx.Refresh();
        var kind = SubjectCatalog.Of(kindId);
        return $"{target.Name} answers to {campaign.Player.Tag} as a {kind?.Name ?? kindId}. " +
               $"Tribute {kind?.Tribute:P0} of its taxes; flag {FlagCatalog.Resolve(campaign, target)}.";
    }

    private static string Release(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var target = ctx.ResolveNation(argument);
        if (target is null || !target.IsSubject)
            return "Usage: release LUX - and it has to answer to somebody.";
        var was = campaign.NationById(target.OverlordId);
        SubjectSystem.Release(campaign, target);
        ctx.Refresh();
        return $"{target.Name} no longer answers to {was?.Tag ?? "anybody"}.";
    }

    private static string Integrate(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var target = ctx.ResolveNation(argument);
        if (target is null) return "Usage: integrate LUX";
        if (!SubjectSystem.Annex(campaign, campaign.Player, target, out var refusal))
            return $"Refused: {ConsoleContext.EscapeBb(refusal)}";
        ctx.Refresh();
        return $"{target.Name} is part of {campaign.Player.Name} now.";
    }

    private static string Relations(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        var rows = campaign.Nations
            .Where(other => other.Id != nation.Id)
            .Select(other => (Other: other, Relation: CampaignDiplomacy.Get(campaign, nation.Id, other.Id)))
            .Where(pair => pair.Relation is not null && MathF.Abs(pair.Relation.Opinion) > 0.5f)
            .OrderByDescending(pair => pair.Relation.Opinion)
            .ToArray();
        if (rows.Length == 0) return $"Nobody has an opinion about {nation.Tag} worth the name.";

        string Row((Nation Other, DiplomaticRelation Relation) pair)
        {
            var marks = new List<string>();
            if (pair.Relation.TradeAgreement) marks.Add("trade");
            if (pair.Relation.NonAggressionPact) marks.Add("pact");
            if (pair.Relation.GuaranteeByA || pair.Relation.GuaranteeByB) marks.Add("guarantee");
            if (pair.Relation.EmbargoByA || pair.Relation.EmbargoByB) marks.Add("embargo");
            if (CampaignDiplomacy.AtWar(campaign, pair.Other.Id, nation.Id)) marks.Add("AT WAR");
            var reason = pair.Relation.Reasons.Count == 0
                ? ""
                : " · " + string.Join(", ", pair.Relation.Reasons.Take(2).Select(item => item.Id));
            return $"  {pair.Other.Tag} {pair.Relation.Opinion,7:+0.0;-0.0;0.0}" +
                   (marks.Count == 0 ? "" : "  [" + string.Join(" ", marks) + "]") + reason;
        }

        return ConsoleContext.EscapeBb($"{nation.Tag}: warmest and coldest of {rows.Length:N0} opinion(s)\n" +
                        string.Join("\n", rows.Take(8).Select(Row)) + "\n  …\n" +
                        string.Join("\n", rows.TakeLast(8).Select(Row)));
    }

    private static string Opinion(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var words = ConsoleContext.Words(argument);
        if (words.Length < 2 || !ConsoleContext.TryFloat(words[1], out var opinion)) return "Usage: opinion RUS -100";
        var other = ctx.ResolveNation(words[0]);
        if (other is null) return $"No country called '{ConsoleContext.EscapeBb(words[0])}'.";
        var relation = CampaignDiplomacy.Get(campaign, campaign.Player.Id, other.Id);
        if (relation is null) return "There is no relation between those two to set.";
        relation.Opinion = Math.Clamp(opinion, -200f, 200f);
        ctx.Refresh();
        return $"{other.Tag} now thinks {relation.Opinion:+0.0;-0.0;0.0} of {campaign.Player.Tag}.";
    }

    private static string Armies(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        var divisions = campaign.Armies.Where(army => army.NationId == nation.Id).ToArray();
        if (divisions.Length == 0) return $"{nation.Tag} has nothing under arms.";

        var byRegion = divisions.GroupBy(army => army.StateMapIndex)
            .OrderByDescending(group => group.Sum(army => army.Personnel))
            .Take(6)
            .Select(group =>
            {
                var state = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == group.Key);
                return $"      {state?.Name ?? "unknown"} · {group.Count()} division(s), " +
                       $"{group.Sum(army => army.Personnel):N0} men";
            });

        return ConsoleContext.EscapeBb(
            $"{nation.Tag}: {divisions.Length:N0} division(s) · {divisions.Sum(army => army.Personnel):N0} men · " +
            $"{campaign.ArmyGroups.Count(group => group.NationId == nation.Id)} headquarters\n" +
            $"  fighting {divisions.Count(army => army.InBattle)} · marching {divisions.Count(army => army.IsMoving)} · " +
            $"cut off {divisions.Count(army => army.CutOff)} · recovering {divisions.Count(army => army.Recovering > 0f)}\n" +
            $"  average morale {divisions.Average(army => army.Organisation):P0} · " +
            $"strength {divisions.Average(army => army.Strength):P0} · " +
            $"supply {divisions.Average(army => army.SupplyLevel):P0}\n" +
            "  heaviest regions:\n" + string.Join("\n", byRegion));
    }

    private static string Orders(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var selected = ctx.GetSelectedArmies();
        var chosen = selected.Count > 0
            ? selected.ToArray()
            : campaign.Armies.Where(army => army.NationId == campaign.Player.Id).Take(12).ToArray();
        if (chosen.Length == 0) return "No divisions to report on.";

        return string.Join("\n", chosen.Select(army =>
        {
            var since = army.OrderIssuedDay <= -9000
                ? "never given one"
                : $"{campaign.ElapsedDays - army.OrderIssuedDay:N0} day(s) ago";
            var progress = army.OrderRouteSteps > 0
                ? $"{army.OrderStepsCompleted}/{army.OrderRouteSteps} steps"
                : "no route";
            var attempt = army.LastMoveResultDay < 0
                ? "never tried to move"
                : $"{army.LastMoveResult} on day {army.LastMoveResultDay:N0}";
            var state = army.InBattle ? "in battle" :
                army.CutOff ? "cut off" :
                army.IsMoving ? "marching" :
                army.Recovering > 0f ? "recovering" : "standing";
            return ConsoleContext.EscapeBb(
                $"  {army.Name} · {state} · province {army.ProvinceId:N0}\n" +
                $"      order {army.OrderKind} -> province {army.OrderProvinceId:N0}, issued {since}\n" +
                $"      {progress} · queued {army.Route.Count} · planned {army.PlannedRoute.Count} · " +
                $"last attempt: {attempt}\n" +
                $"      morale {army.Organisation:P0} · strength {army.Strength:P0} · supply {army.SupplyLevel:P0} · " +
                $"stance {army.Stance}");
        }));
    }

    private static string Spawn(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || campaign.ProvinceMap is null) return "No campaign.";
        var words = ConsoleContext.Words(argument);
        var count = words.Length > 0 && int.TryParse(words[0], out var parsed) ? parsed : 1;
        if (count is < 1 or > 50) return "Usage: spawn 3 [template] - between 1 and 50.";
        var templateId = words.Length > 1 ? words[1] : "line_infantry";
        var template = UnitTypeCatalog.All.FirstOrDefault(unit =>
            string.Equals(unit.Id, templateId, StringComparison.OrdinalIgnoreCase));
        if (template is null)
            return $"No template '{templateId}'. Known: " +
                   string.Join(", ", UnitTypeCatalog.All.Select(unit => unit.Id));

        var nation = campaign.Player;
        var province = RecruitmentSystem.DeploymentProvince(campaign, nation.Id, nation.CapitalStateMapIndex);
        if (province <= 0) return "No controlled province in the capital region to put them in.";
        var position = campaign.ProvinceMap.CenterOf(province);
        var nextId = campaign.Armies.Count == 0 ? 1 : campaign.Armies.Max(army => army.Id) + 1;
        var serial = campaign.Armies.Count(army => army.NationId == nation.Id) + 1;
        for (var index = 0; index < count; index++)
            campaign.Armies.Add(new Army
            {
                Id = nextId + index,
                NationId = nation.Id,
                StateMapIndex = nation.CapitalStateMapIndex,
                ProvinceId = province,
                Name = $"{serial + index}. {nation.Tag} Division",
                TemplateId = template.Id,
                Divisions = 1,
                Personnel = template.RecruitmentPersonnel,
                MaxPersonnel = template.RecruitmentPersonnel,
                Organisation = 1f,
                Strength = 1f,
                Equipment = 1f,
                Experience = 0.10f,
                Cohesion = 1f,
                SupplyLevel = 1f,
                OrderKind = ArmyOrder.Manual,
                PlanActive = false,
                Latitude = position.Latitude,
                Longitude = position.Longitude
            });
        campaign.MarkStrategicChange();
        ctx.Refresh();
        return $"Raised {count} {template.Id} division(s) at {nation.CapitalName}, province #{province:N0}.";
    }

    private static string Teleport(ConsoleContext ctx, string value)
    {
        var campaign = ctx.Campaign;
        if (campaign?.ProvinceMap is null || !int.TryParse(value, out var province) ||
            province <= 0 || province > campaign.ProvinceMap.ProvinceCount)
            return "Usage: teleport PROVINCE_ID";
        var targets = ctx.GetSelectedArmies().Where(army => army.NationId == campaign.Player.Id).ToArray();
        if (targets.Length == 0) return "Select one or more divisions first.";
        var center = campaign.ProvinceMap.CenterOf(province);
        foreach (var division in targets)
        {
            division.ProvinceId = province;
            division.StateMapIndex = campaign.ProvinceMap.ProvinceToState[province];
            division.Latitude = center.Latitude;
            division.Longitude = center.Longitude;
            division.Route.Clear();
            division.PlannedRoute.Clear();
            division.MoveProgress = 0f;
            division.OrderRouteSteps = 0;
            division.OrderStepsCompleted = 0;
        }
        campaign.MarkStrategicChange();
        return $"Teleported {targets.Length} division(s) to province #{province:N0}.";
    }

    private static string Heal(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var all = argument.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        var chosen = all
            ? campaign.Armies.Where(army => army.NationId == campaign.Player.Id).ToArray()
            : ctx.GetSelectedArmies().Where(army => army.NationId == campaign.Player.Id).ToArray();
        if (chosen.Length == 0) return "Select divisions, or use `heal all`.";
        foreach (var army in chosen)
        {
            army.Personnel = army.MaxPersonnel;
            army.Organisation = 1f;
            army.Strength = 1f;
            army.Equipment = 1f;
            army.Cohesion = 1f;
            army.SupplyLevel = 1f;
            army.Recovering = 0f;
        }
        campaign.MarkStrategicChange();
        ctx.Refresh();
        return $"Filled {chosen.Length:N0} division(s) back up.";
    }

    private static string Kill(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var all = argument.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        var doomed = all
            ? campaign.Armies.Where(army => army.NationId == campaign.Player.Id).ToList()
            : ctx.GetSelectedArmies().Where(army => army.NationId == campaign.Player.Id).ToList();
        if (doomed.Count == 0) return "Select divisions, or use `kill all`.";
        ctx.ClearArmySelectionAndClose();
        foreach (var army in doomed) campaign.Armies.Remove(army);
        campaign.MarkStrategicChange();
        ctx.Refresh();
        return $"Removed {doomed.Count:N0} division(s).";
    }

    private static string Groups(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var groups = campaign.ArmyGroups.Where(group => group.NationId == campaign.Player.Id).ToArray();
        if (groups.Length == 0) return $"{campaign.Player.Tag} has no army headquarters.";
        return string.Join("\n", groups.Select(group =>
        {
            var under = campaign.Armies.Count(army => army.ArmyGroupId == group.Id);
            return ConsoleContext.EscapeBb($"  #{group.Id} {group.Name} · {under:N0} division(s)");
        }));
    }

    private static string Fleets(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        var fleets = campaign.Fleets.Where(fleet => fleet.NationId == nation.Id).ToArray();
        if (fleets.Length == 0) return $"{nation.Tag} has no fleets.";
        return string.Join("\n", fleets.Take(20).Select(fleet =>
            ConsoleContext.EscapeBb($"  {fleet.Name} · {fleet.Hulls:N0} hull(s) · " +
                     $"{fleet.Latitude:0.0}, {fleet.Longitude:0.0} · {fleet.Mission}")));
    }
}
