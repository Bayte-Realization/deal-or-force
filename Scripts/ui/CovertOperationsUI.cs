using System;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Covert operations on the province card: with a network of ours in the owner's country, what it
/// could do there - against this region, or the country itself - and what it would cost, how
/// long it would take, how likely it is to work and to be traced. While one is under way the card
/// offers to call it off instead.
/// </summary>
public partial class DealOrForceGame
{
    private void AddCovertCardActions(Nation foreign, ScenarioState region, System.Action<string> section,
        System.Action<string, string, System.Action, string, string> action)
    {
        var player = _campaign?.Player;
        if (player is null || foreign is null || foreign.Movement is not null || !_campaign.FeatureEnabled("intelligence") ||
            !player.IntelNetworks.ContainsKey(foreign.Id) && CovertOperations.UnderWay(player, foreign.Id) is null)
            return;
        section("COVERT OPERATIONS");
        var running = CovertOperations.UnderWay(player, foreign.Id);
        if (running is not null)
        {
            var kind = CovertOperations.Find(running.Kind);
            var left = Math.Max(0, running.DueDay - _campaign.ElapsedDays);
            var where = running.RegionMapIndex >= 0 ? _campaign.States.Find(s => s.MapIndex == running.RegionMapIndex)?.DisplayName : null;
            var label = where is null
                ? Simulation.Loc.Format("Call off: {0}, {1} days left", Tr(kind?.Name ?? running.Kind), left)
                : Simulation.Loc.Format("Call off: {0} in {1}, {2} days left", Tr(kind?.Name ?? running.Kind), where, left);
            action("close", label, () =>
            {
                _campaign.Commands.Enqueue(new CovertOperationCommand { IssuerId = player.Id, TargetId = foreign.Id, Action = "cancel" });
                _campaign.Commands.ApplyAll(_campaign);
                CloseProvinceCard();
                SetArmyFeedback(Tr("The operation is called off. What it cost is spent."));
            }, "", Tr("The agents stand down. The money is not coming back."));
            return;
        }
        var chance = CovertOperations.SuccessChance(_campaign, player, foreign);
        var defines = _campaign.Defines;
        foreach (var kind in CovertOperations.All)
        {
            var aimed = kind.Regional ? region : null;
            var refusal = CovertOperations.Refusal(_campaign, player, foreign, kind, aimed);
            // What would be stolen is named on the button: the player pays to learn something in particular.
            var stolen = kind.Effect == "theft" ? CovertOperations.TheftTarget(player, foreign) : null;
            var label = kind.Regional && region is not null ? Simulation.Loc.Format("{0}: {1}", Tr(kind.Name), region.DisplayName)
                : stolen is not null ? Simulation.Loc.Format("{0}: {1}", Tr(kind.Name), Tr(stolen.Name))
                : Tr(kind.Name);
            var help = Simulation.Loc.Format(
                "{0}\n{1} · {2} days · works {3}\nTraced: {4} if it works, {5} if it fails\n" +
                "Afterwards the network lies low for {6} days. Traced, it loses half its agents, they will not forget it, and it lies low for {7} days.",
                Tr(kind.Description), Fmt.Money(CovertOperations.Cost(_campaign, player, kind)), kind.Days, Fmt.Percent(chance),
                Fmt.Percent(CovertOperations.ExposureChance(_campaign, foreign, kind, true)),
                Fmt.Percent(CovertOperations.ExposureChance(_campaign, foreign, kind, false)),
                defines.CovertQuietDays, defines.CovertQuietAfterExposureDays);
            var captured = kind;
            // A refused operation still shows what it would be: most refusals only mean "not yet".
            action(CovertIcon(kind.Effect), label, () => StartCovertFromMenu(foreign, captured, aimed),
                refusal.Length > 0 ? refusal + "\n\n" + help : "", help);
        }
    }

    private static string CovertIcon(string effect) => effect switch
    {
        "sabotage" => "industry",
        "unrest" => "unrest",
        "opposition" => "movement_fundraise",
        "theft" => "research",
        "separatism" => "movement_fundraise",
        _ => "search"
    };

    private void StartCovertFromMenu(Nation target, CovertOperationDefinition kind, ScenarioState region)
    {
        var command = new CovertOperationCommand
        {
            IssuerId = _campaign.Player.Id, TargetId = target.Id, OperationId = kind.Id, RegionMapIndex = region?.MapIndex ?? -1
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        CloseProvinceCard();
        PlayStrategicCue(command.Refusal.Length > 0 ? "action_denied" : "covert_launched");
        SetArmyFeedback(command.Refusal.Length > 0
            ? Tr(command.Refusal)
            : Simulation.Loc.Format("The operation is under way. It comes due in {0} days.", kind.Days));
    }
}
