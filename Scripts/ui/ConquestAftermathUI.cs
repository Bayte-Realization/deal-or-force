using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// What the region card says about conquered ground: how far its people have accepted the state,
/// what is holding that back, whether it governs itself, and whether it is still exhausted after a
/// crushed rising. Every figure is read from the rule that applies it (ConquestAcceptance.Read), so
/// the card cannot promise a pace the weekly pass does not keep.
/// </summary>
public partial class DealOrForceGame
{
    private void AddConquestAftermathRows(Nation owner, ScenarioState state)
    {
        if (owner is null || state is null) return;
        var d = _campaign.Defines;
        var outlook = ConquestAcceptance.Read(_campaign, owner, state);
        if (outlook.Shown)
        {
            var help = Simulation.Loc.Format(
                "How far the people here have come to accept this state. It grows each quiet week on ground that is not the country's homeland: fastest below {0} radicalism, half as fast again for a people of the same family as ours, twice as fast with autonomy; it stops above {1} radicalism or under a foreign army. It eases what the minority costs in stability and anger by up to half and lightens the region in imperial overstretch. At 100% the region becomes part of the homeland.",
                Fmt.Percent(d.AcceptanceCalmRadicalism), Fmt.Percent(d.AcceptanceStallRadicalism));
            var tone = outlook.Acceptance >= 0.66f ? "state.positive" : outlook.Acceptance >= 0.33f ? "text.bright" : "state.warning";
            CardRow(_regionCardRegionRows, "Acceptance of rule", Fmt.Percent(outlook.Acceptance), help, tone);
            var meter = BattleGauge(outlook.Acceptance, tone, "AcceptanceMeter");
            meter.CustomMinimumSize = new Vector2(0, 4);
            meter.TooltipText = Ui.Tip(help);
            _regionCardRegionRows.AddChild(meter);
            string pace;
            var paceTone = "text.body_soft";
            if (outlook.Acceptance >= 1f) pace = Tr("this month");
            else if (outlook.StallKey.Length > 0)
            {
                pace = Simulation.Loc.Format(outlook.StallKey, Fmt.Percent(d.AcceptanceStallRadicalism));
                paceTone = "state.warning";
            }
            else pace = outlook.YearsLeft > 30 ? Tr("in more than 30 years at this pace")
                : Simulation.Loc.Format("in about {0} years at this pace", outlook.YearsLeft);
            CardRow(_regionCardRegionRows, "Becomes homeland", pace, help, paceTone);
        }
        if (RegionalAutonomy.Autonomous(state))
            CardRow(_regionCardRegionRows, "Autonomy",
                Simulation.Loc.Format("since {0}, keeps {1} of its taxes",
                    LandArrangementSystem.DateOfDay(_campaign, state.AutonomousSinceDay), Fmt.Percent(d.AutonomyRevenueShare)),
                Simulation.Loc.Format("The region governs itself and keeps {0} of its taxes: {1} a week. It weighs {2} as much in imperial overstretch, takes in less grievance and comes to accept the state twice as fast.",
                    Fmt.Percent(d.AutonomyRevenueShare), Fmt.Money(RegionalAutonomy.WeeklyPrice(_campaign, owner, state)),
                    Fmt.Percent(d.AutonomyOverstretchWeight)), "state.positive");
        if (outlook.ExhaustedDaysLeft > 0)
            CardRow(_regionCardRegionRows, "Exhausted after a crushed rising",
                Simulation.Loc.Format("{0} more days", outlook.ExhaustedDaysLeft),
                Simulation.Loc.Format("Its rising was crushed: until then grievance, overstretch and occupation feed its radicalism at {0} of the usual rate. It is not protection against a new rising - that lasts {1} days from the crushing - but it lets the region calm down before the protection ends.",
                    Fmt.Percent(d.RisingExhaustionInflow), d.ReintegrationRecoveryDays), "text.bright");
    }
}
