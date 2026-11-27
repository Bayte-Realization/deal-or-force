using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Regional construction cards (+1 / x3 / All, Shift = across all own regions) inside ShellWorkPanelView.
/// Displays each building's icon, current regional level, cost &amp; duration, and exact refusal reasons
/// on hover so players never have to hunt tooltips to build or upgrade infrastructure.
/// </summary>
internal static class ShellRegionBuildTabView
{
    private static readonly BuildingKind[] QuickBuildKinds =
    {
        BuildingKind.CivilianIndustry,
        BuildingKind.Infrastructure,
        BuildingKind.ArmsIndustry,
        BuildingKind.Barracks,
        BuildingKind.PowerPlant,
        BuildingKind.ExtractionIndustry
    };

    public static void PopulateBuildRows(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Label feedbackLabel,
        Campaign world,
        Nation owner,
        ScenarioState state,
        Action onQueued)
    {
        var running = world.Construction.Count(p => p.NationId == owner.Id);
        var slots = ConstructionCost.Limit(world, owner, world.Defines.MaxConstructionProjects);
        var observer = ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;

        foreach (var kind in QuickBuildKinds)
            AddRegionBuildRow(ctx, bodyRows, feedbackLabel, world, owner, state, kind, running, slots, observer, onQueued);
    }

    private static void AddRegionBuildRow(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Label feedbackLabel,
        Campaign world,
        Nation owner,
        ScenarioState state,
        BuildingKind kind,
        int running,
        int slots,
        bool observer,
        Action onQueued)
    {
        var def = BuildingCatalog.Get(kind);
        var cost = ConstructionCost.Of(world, owner, kind);
        var days = ConstructionCost.ExpectedDays(world, owner, kind);
        var builtLv = state.Buildings.GetValueOrDefault(kind);

        var refusal = BuildingRules.Refusal(world, owner, kind);
        if (string.IsNullOrEmpty(refusal))
            refusal = Waterfront.Refusal(world, state.MapIndex, kind);
        var slotsFull = running >= slots;
        var lowFunds = owner.Treasury < MathF.Min(0.05f, cost);
        var blockedReason = !string.IsNullOrEmpty(refusal)
            ? Loc.Get(refusal)
            : slotsFull || lowFunds
                ? Loc.Get("All construction slots are busy or treasury is insufficient.")
                : "";
        var disabled = observer || !string.IsNullOrEmpty(blockedReason);

        var tipText = $"{Loc.Get(def.Name)} ({Fmt.Money(cost)} · {days} {Loc.Get("days")})\n{Loc.Get(def.Description)}";
        if (!string.IsNullOrEmpty(blockedReason))
            tipText += "\n" + Loc.Format("shell.ws.reg.build_blocked_fmt", blockedReason);

        var card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            TooltipText = Ui.Tip(tipText)
        };
        card.AddThemeStyleboxOverride("panel",
            ctx.BorderedBox("shell.plate_bottom", disabled ? "shell.rule" : "shell.engrave", 1, 3, 6, 4, 6, 4));
        bodyRows.AddChild(card);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        card.AddChild(row);

        var bldIco = ctx.Icon($"icon_building_{kind.ToString().ToLowerInvariant()}") ?? ctx.Icon("icon_industry");
        if (bldIco is not null)
        {
            row.AddChild(new TextureRect
            {
                Texture = bldIco,
                CustomMinimumSize = new Vector2(20, 20),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Modulate = Ui.Colour(disabled ? "shell.paper_ghost" : "shell.icon"),
                MouseFilter = Control.MouseFilterEnum.Ignore
            });
        }

        var infoCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        infoCol.AddThemeConstantOverride("separation", 0);
        row.AddChild(infoCol);

        var nm = new Label
        {
            Text = Loc.Get(def.Name),
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        nm.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        nm.AddThemeFontSizeOverride("font_size", 12);
        nm.AddThemeColorOverride("font_color", Ui.Colour(disabled ? "shell.paper_faint" : "shell.paper"));
        infoCol.AddChild(nm);

        var meta = new Label
        {
            Text = Loc.Format("shell.ws.reg.build_meta_fmt", builtLv, Fmt.Money(cost), days),
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        meta.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        meta.AddThemeFontSizeOverride("font_size", 10);
        meta.AddThemeColorOverride("font_color", Ui.Colour(lowFunds ? "shell.state.warning_text" : "shell.paper_faint"));
        infoCol.AddChild(meta);

        var chipNormal = ctx.NineSlice("button_umber_9s", 8, 5, 2, 5, 2, "shell.plate_top", "shell.engrave", 2);
        var chipHover = ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 5, 2, 5, 2);
        var chipDisabled = ctx.BorderedBox("shell.well_bottom", "shell.rule", 1, 2, 5, 2, 5, 2);

        foreach (var (chipText, count) in new[] { ("+1", 1), ("×3", 3), (Loc.Get("shell.chip_all"), 99) })
        {
            var cCount = count;
            var chip = new Button
            {
                Text = chipText,
                Disabled = disabled,
                CustomMinimumSize = new Vector2(32, 24),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseDefaultCursorShape = disabled ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Ui.Tip(tipText)
            };
            chip.AddThemeStyleboxOverride("normal", chipNormal);
            chip.AddThemeStyleboxOverride("hover", chipHover);
            chip.AddThemeStyleboxOverride("disabled", chipDisabled);
            chip.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
            chip.AddThemeFontSizeOverride("font_size", 10);
            chip.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
            chip.AddThemeColorOverride("font_disabled_color", Ui.Colour("shell.paper_ghost"));
            chip.Pressed += () => QueueRegionalBuild(ctx, feedbackLabel, world, owner, state, kind, cCount, Input.IsKeyPressed(Key.Shift), onQueued);
            row.AddChild(chip);
        }
    }

    private static void QueueRegionalBuild(
        ShellContext ctx,
        Label feedbackLabel,
        Campaign world,
        Nation owner,
        ScenarioState state,
        BuildingKind kind,
        int count,
        bool everywhere,
        Action onQueued)
    {
        ctx.PlayCue?.Invoke("ui_select");
        var targets = everywhere ? world.StatesOf(owner).ToArray() : new[] { state };
        var queued = 0;
        var slots = ConstructionCost.Limit(world, owner, world.Defines.MaxConstructionProjects);
        var unitCost = ConstructionCost.Of(world, owner, kind);

        foreach (var st in targets)
        {
            if (BuildingRules.Refusal(world, owner, kind).Length > 0) break;
            if (Waterfront.Refusal(world, st.MapIndex, kind).Length > 0) continue;
            for (var k = 0; k < count; k++)
            {
                var running = world.Construction.Count(p => p.NationId == owner.Id);
                if (running >= slots || owner.Treasury < MathF.Min(0.05f, unitCost)) break;
                ImmediateCampaignCommands.Commit(world, new QueueConstructionCommand
                {
                    IssuerId = owner.Id,
                    StateMapIndex = st.MapIndex,
                    Building = kind
                });
                queued++;
            }
        }

        feedbackLabel.Visible = true;
        feedbackLabel.Text = queued > 0
            ? Loc.Format("shell.queued_sites_fmt", queued, Loc.Get(BuildingCatalog.Get(kind).Name))
            : Loc.Get("All construction slots are busy or treasury is insufficient.");
        onQueued?.Invoke();
        ctx.RefreshAll?.Invoke();
    }
}
