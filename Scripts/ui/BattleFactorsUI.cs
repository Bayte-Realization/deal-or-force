using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Visual combat modifier chips and side-by-side fire/defence factor breakdown inside BattleDossierUI.
/// Reads live <see cref="BattleTelemetrySnapshot"/> and <see cref="CombatFactors"/> read-only so
/// players immediately grasp terrain, river, flanking, support, combined-arms, and supply effects.
/// </summary>
public partial class DealOrForceGame
{
    private const int BattleFactorRows = 7;

    private void AddBattleModifierChips(VBoxContainer parent, BattleTelemetrySnapshot telemetry, LandBattle battle, bool playerAttacks)
    {
        if (battle.Ended) return;
        var chips = new HFlowContainer
        {
            Name = "BattleModifierChips",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipContents = true
        };
        chips.AddThemeConstantOverride("h_separation", 5);
        chips.AddThemeConstantOverride("v_separation", 4);

        var added = 0;
        void AddChip(string text, string toneToken, string tooltip)
        {
            var pill = new PanelContainer
            {
                ClipContents = true,
                TooltipText = Ui.Tip(tooltip),
                MouseFilter = MouseFilterEnum.Stop
            };
            var sb = new StyleBoxFlat
            {
                BgColor = Ui.Colour("surface.inset"),
                BorderColor = Ui.Colour(toneToken),
                ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2
            };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(3);
            pill.AddThemeStyleboxOverride("panel", sb);

            var lbl = BattleText(text, 11, toneToken, tabular: true);
            lbl.AutowrapMode = TextServer.AutowrapMode.Off;
            lbl.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            lbl.MouseFilter = MouseFilterEnum.Ignore;
            pill.AddChild(lbl);
            chips.AddChild(pill);
            added++;
        }

        // 1. Terrain chip
        if (!string.IsNullOrWhiteSpace(telemetry.TerrainName))
            AddChip(Tr(telemetry.TerrainName), "button.primary_border",
                Tr("Terrain") + ": " + Tr(telemetry.TerrainName));

        // 2. Multi-direction flanking chip
        if (telemetry.AttackDirections > 1)
        {
            var flankTone = playerAttacks ? "state.positive" : "state.negative";
            AddChip(
                Loc.Format("shell.ws.battle.flank_chip_fmt", telemetry.AttackDirections, telemetry.FlankingEntrenchmentBypass),
                flankTone,
                Loc.Format("shell.ws.battle.flank_tip_fmt", telemetry.AttackDirections, telemetry.FlankingEntrenchmentBypass));
        }

        // 3. River crossing chip
        if (telemetry.RiverCrossingStrength > 0.01f)
        {
            var retained = WaterConditions.AttackMultiplier(_campaign, telemetry.RiverCrossingStrength, battle.ProvinceId);
            var riverTone = playerAttacks ? "state.negative" : "state.positive";
            AddChip(
                Loc.Format("shell.ws.battle.river_chip_fmt", retained),
                riverTone,
                Tr("River crossing") + ": " + Loc.Format("{0:P0}", retained));
        }

        // 4. Pocket collapse / Encirclement chip
        if (telemetry.HasPocketCollapse)
            AddChip(Tr("shell.ws.battle.pocket_chip"), playerAttacks ? "state.positive" : "state.negative", Tr("shell.ws.battle.pocket_chip"));
        else if (telemetry.HasEncircledDefenders)
            AddChip(Tr("shell.ws.battle.encircled_chip"), "state.warning", Tr("shell.ws.battle.encircled_chip"));

        // 5. Combined arms bonus (attacker or defender)
        var ownSide = playerAttacks ? telemetry.Attacker : telemetry.Defender;
        var enemySide = playerAttacks ? telemetry.Defender : telemetry.Attacker;
        if (ownSide.CombinedArmsBonus > 1.01f)
            AddChip(Loc.Format("shell.ws.battle.combined_arms_chip_fmt", ownSide.CombinedArmsBonus), "state.positive",
                Loc.Format("shell.ws.battle.combined_arms_chip_fmt", ownSide.CombinedArmsBonus));
        else if (enemySide.CombinedArmsBonus > 1.01f)
            AddChip(Loc.Format("shell.ws.battle.combined_arms_chip_fmt", enemySide.CombinedArmsBonus), "state.negative",
                Loc.Format("shell.ws.battle.combined_arms_chip_fmt", enemySide.CombinedArmsBonus));

        // 6. Long-range support fire
        if (telemetry.Attacker.SupportFire + telemetry.Defender.SupportFire >= 0.1f)
            AddChip(Loc.Format("shell.ws.battle.support_chip_fmt", telemetry.Attacker.SupportFire, telemetry.Defender.SupportFire),
                "state.info",
                Tr("Long-range fire") + $": {telemetry.Attacker.SupportFire:0.#} / {telemetry.Defender.SupportFire:0.#}");

        // 7. Top telemetry modifier chips (deduplicated)
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chip in ownSide.Modifiers.Concat(enemySide.Modifiers))
        {
            if (added >= 8 || !seenKeys.Add(chip.Key)) continue;
            if (MathF.Abs(chip.Multiplier - 1f) < CombatFactors.Threshold) continue;
            var isOurs = ownSide.Modifiers.Contains(chip);
            var tone = chip.IsPositive == isOurs ? "state.positive" : "state.negative";
            var label = $"{Tr(chip.Label)} ×{chip.Multiplier:0.00}";
            AddChip(label, tone, $"{Tr(chip.Label)}: ×{chip.Multiplier:0.00} ({chip.Category})");
        }

        if (added > 0)
            parent.AddChild(chips);
    }

    private void AddBattleFactors(VBoxContainer parent, LandBattle battle, bool playerAttacks)
    {
        if (battle.Ended) return;
        SectionRule(parent, "Why it goes this way");
        if (battle.AttackFactors.Count + battle.DefenceFactors.Count == 0)
        {
            parent.AddChild(BattleText(Tr("The reasons appear after the next round."), 12, "text.muted"));
            return;
        }
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        columns.AddThemeConstantOverride("separation", 12);
        columns.TooltipText = Ui.Tip(Tr("What multiplies the attacker's fire and the defender's staying power in the last round. Their ratio decides who gives way."));
        columns.MouseFilter = MouseFilterEnum.Stop;
        parent.AddChild(columns);
        columns.AddChild(BattleFactorColumn("Attacker's fire", battle.AttackFactors, playerAttacks));
        columns.AddChild(BattleFactorColumn("Defender's staying power", battle.DefenceFactors, !playerAttacks));
    }

    /// <summary>One side's reasons. `ours` says whether a factor above one is good news.</summary>
    private Control BattleFactorColumn(string heading, IReadOnlyList<BattleFactor> factors, bool ours)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 3);
        var total = CombatFactors.Product(factors);
        var head = new HBoxContainer { ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        var title = BattleText(Tr(heading), 12, "text.caption");
        title.MouseFilter = MouseFilterEnum.Ignore;
        head.AddChild(title);
        var sum = BattleText("×" + total.ToString("0.00"), 13, FactorTone(total, ours), tabular: true);
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
            var tone = FactorTone(factor.Value, ours);
            var rowWrap = new VBoxContainer { ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
            rowWrap.AddThemeConstantOverride("separation", 1);

            var row = new HBoxContainer { ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
            var name = BattleText(Tr(factor.Key), 11, "text.body_soft");
            name.AutowrapMode = TextServer.AutowrapMode.Off;
            name.ClipText = true;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.MouseFilter = MouseFilterEnum.Ignore;
            row.AddChild(name);
            var value = BattleText("×" + factor.Value.ToString("0.00"), 11, tone, tabular: true);
            value.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            value.AutowrapMode = TextServer.AutowrapMode.Off;
            value.MouseFilter = MouseFilterEnum.Ignore;
            row.AddChild(value);
            rowWrap.AddChild(row);

            // Subtle magnitude bar showing relative weight of this factor
            var mag = Math.Clamp(MathF.Abs(MathF.Log(MathF.Max(0.05f, factor.Value))) / 0.9f, 0.04f, 1f);
            var track = new ColorRect
            {
                Color = Ui.Colour("surface.inset"),
                CustomMinimumSize = new Vector2(0, 3),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Ignore
            };
            var fill = new ColorRect
            {
                Color = Ui.Colour(tone, 0.78f),
                AnchorRight = mag,
                AnchorBottom = 1f,
                MouseFilter = MouseFilterEnum.Ignore
            };
            track.AddChild(fill);
            rowWrap.AddChild(track);
            column.AddChild(rowWrap);
        }
        if (shown.Count > BattleFactorRows)
        {
            var rest = shown.Skip(BattleFactorRows).ToList();
            var more = BattleText(Loc.Format("All together ×{0:0.00}", total), 11, "text.muted", tabular: true);
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
