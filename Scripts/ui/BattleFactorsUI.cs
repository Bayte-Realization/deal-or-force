using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// "Why it goes this way" in the battle report: the attacker's fire and the defender's staying
/// power as two columns of reasons with their multipliers (CombatFactors), the biggest first,
/// green where a reason helps the player's side and red where it hurts. The numbers were always
/// in the battle; this is the first time a player can see which of them is deciding it.
/// </summary>
public partial class DealOrForceGame
{
    private const int BattleFactorRows = 7;

    private void AddBattleFactors(LandBattle battle, bool playerAttacks)
    {
        if (battle.Ended) return;
        SectionRule(_battleDetailControls, "Why it goes this way");
        if (battle.AttackFactors.Count + battle.DefenceFactors.Count == 0)
        {
            _battleDetailControls.AddChild(BattleText(Tr("The reasons appear after the next round."), 12, "text.muted"));
            return;
        }
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 14);
        columns.TooltipText = Ui.Tip(Tr("What multiplies the attacker's fire and the defender's staying power in the last round. Their ratio decides who gives way."));
        columns.MouseFilter = MouseFilterEnum.Stop;
        _battleDetailControls.AddChild(columns);
        columns.AddChild(BattleFactorColumn("Attacker's fire", battle.AttackFactors, playerAttacks));
        columns.AddChild(BattleFactorColumn("Defender's staying power", battle.DefenceFactors, !playerAttacks));
    }

    /// <summary>One side's reasons. `ours` says whether a factor above one is good news.</summary>
    private Control BattleFactorColumn(string heading, IReadOnlyList<BattleFactor> factors, bool ours)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 2);
        var total = CombatFactors.Product(factors);
        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var title = BattleText(Tr(heading), 12, "text.caption");
        title.MouseFilter = MouseFilterEnum.Ignore;
        head.AddChild(title);
        var sum = BattleText("×" + total.ToString("0.00"), 13, FactorTone(total, ours));
        sum.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        sum.AutowrapMode = TextServer.AutowrapMode.Off;
        sum.MouseFilter = MouseFilterEnum.Ignore;
        head.AddChild(sum);
        column.AddChild(head);

        var shown = factors.OrderByDescending(factor => MathF.Abs(MathF.Log(MathF.Max(0.001f, factor.Value)))).ToList();
        if (shown.Count == 0)
            column.AddChild(BattleText(Tr("Everything else is even."), 11, "text.muted"));
        foreach (var factor in shown.Take(BattleFactorRows))
        {
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            var name = BattleText(Tr(factor.Key), 11, "text.body_soft");
            name.AutowrapMode = TextServer.AutowrapMode.Off;
            name.ClipText = true;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.MouseFilter = MouseFilterEnum.Ignore;
            row.AddChild(name);
            var value = BattleText("×" + factor.Value.ToString("0.00"), 11, FactorTone(factor.Value, ours));
            value.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            value.AutowrapMode = TextServer.AutowrapMode.Off;
            value.MouseFilter = MouseFilterEnum.Ignore;
            row.AddChild(value);
            column.AddChild(row);
        }
        if (shown.Count > BattleFactorRows)
        {
            var rest = shown.Skip(BattleFactorRows).ToList();
            var more = BattleText(Loc.Format("All together ×{0:0.00}", total), 11, "text.muted");
            more.TooltipText = Ui.Tip(string.Join("\n", rest.Select(factor => Tr(factor.Key) + "  ×" + factor.Value.ToString("0.00"))));
            more.MouseFilter = MouseFilterEnum.Stop;
            column.AddChild(more);
        }
        return column;
    }

    private static string FactorTone(float value, bool ours)
    {
        if (MathF.Abs(value - 1f) < CombatFactors.Threshold) return "text.body_soft";
        return value > 1f == ours ? "state.positive" : "state.negative";
    }
}
