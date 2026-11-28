using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Regional construction cards (+1 / x3 / All / −, Shift = across all own regions) inside ShellWorkPanelView.
/// Lists every building from <see cref="BuildingCatalog.All"/> with category filters, full
/// <see cref="ConstructionAdmission.Refusal"/> validation, and a one-click jump to the full Construction Workspace.
/// </summary>
internal static class ShellRegionBuildTabView
{
    public static void PopulateBuildRows(
        ShellContext ctx, VBoxContainer bodyRows, Label feedbackLabel,
        Campaign world, Nation owner, ScenarioState state, Action onQueued)
    {
        var observer = ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        AddCategoryAndCatalogBar(ctx, bodyRows, onQueued);

        var activeCat = ctx.RegionBuildCategory;
        foreach (var kind in BuildingCatalog.All)
        {
            if (activeCat != ConstructionCategory.All && ShellConstructionCards.CategoryOf(kind) != activeCat)
                continue;
            AddRegionBuildRow(ctx, bodyRows, feedbackLabel, world, owner, state, kind, observer, onQueued);
        }
    }

    private static void AddCategoryAndCatalogBar(ShellContext ctx, VBoxContainer bodyRows, Action onQueued)
    {
        var bar = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        bar.AddThemeConstantOverride("separation", 3);
        bodyRows.AddChild(bar);

        var chipNorm = ctx.NineSlice("button_umber_9s", 8, 4, 2, 4, 2, "shell.plate_top", "shell.engrave", 2);
        var chipOn = ctx.PressedWellStyle(underlineBottom: true, padX: 4, padY: 2);
        foreach (var (cat, locKey) in ShellConstructionCards.Categories)
        {
            var captured = cat;
            var on = ctx.RegionBuildCategory == cat;
            var b = new Button
            {
                Name = "ShellRegBuildCat_" + cat, Text = Loc.Get(locKey),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 24),
                ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = Ui.Tip(Loc.Get(locKey)),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
            };
            b.AddThemeStyleboxOverride("normal", on ? chipOn : chipNorm);
            b.AddThemeFontOverride("font", ctx.Font("shell.sans"));
            b.AddThemeFontSizeOverride("font_size", 10);
            b.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
            b.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ctx.RegionBuildCategory = captured; onQueued?.Invoke(); };
            bar.AddChild(b);
        }

        var fullBtn = new Button
        {
            Name = "ShellRegBuildOpenFull", Text = Loc.Get("shell.ws.build.open_catalog"),
            CustomMinimumSize = new Vector2(64, 24), ClipText = true,
            TooltipText = Ui.Tip(Loc.Get("shell.tab_build")),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
        };
        fullBtn.AddThemeStyleboxOverride("normal", ctx.NineSlice("button_brass_cta_9s", 8, 6, 2, 6, 2, "shell.plate_top", "shell.brass_hi", 2));
        fullBtn.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        fullBtn.AddThemeFontSizeOverride("font_size", 10);
        fullBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        fullBtn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ctx.OpenConstruction?.Invoke(); };
        bar.AddChild(fullBtn);
    }

    private static void AddRegionBuildRow(
        ShellContext ctx, VBoxContainer bodyRows, Label feedbackLabel,
        Campaign world, Nation owner, ScenarioState state, BuildingKind kind, bool observer, Action onQueued)
    {
        var def = BuildingCatalog.Get(kind);
        var cost = ConstructionCost.Of(world, owner, kind);
        var days = ConstructionCost.ExpectedDays(world, owner, kind);
        var builtLv = state.Buildings.GetValueOrDefault(kind);
        var blockedReason = ConstructionAdmission.Refusal(world, owner, state, kind);
        var disabled = observer || !string.IsNullOrEmpty(blockedReason);
        var lowFunds = owner.Treasury < MathF.Min(0.05f, cost);

        var tipText = $"{Loc.Get(def.Name)} ({Fmt.Money(cost)} · {days} {Loc.Get("days")})\n{Loc.Get(def.Description)}";
        if (!string.IsNullOrEmpty(blockedReason)) tipText += "\n" + Loc.Format("shell.ws.reg.build_blocked_fmt", blockedReason);

        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true, TooltipText = Ui.Tip(tipText) };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.plate_bottom", disabled ? "shell.rule" : "shell.engrave", 1, 3, 6, 4, 6, 4));
        bodyRows.AddChild(card);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 5);
        card.AddChild(row);

        if (ShellConstructionCards.BuildingIcon(ctx, kind) is { } bldIco)
        {
            row.AddChild(new TextureRect
            {
                Texture = bldIco, CustomMinimumSize = new Vector2(20, 20),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Modulate = Ui.Colour(disabled ? "shell.paper_ghost" : "shell.icon"),
                MouseFilter = Control.MouseFilterEnum.Ignore
            });
        }

        var infoCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        infoCol.AddThemeConstantOverride("separation", 0);
        row.AddChild(infoCol);

        var nm = new Label { Text = Loc.Get(def.Name), VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        nm.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        nm.AddThemeFontSizeOverride("font_size", 12);
        nm.AddThemeColorOverride("font_color", Ui.Colour(disabled ? "shell.paper_faint" : "shell.paper"));
        infoCol.AddChild(nm);

        var meta = new Label { Text = Loc.Format("shell.ws.reg.build_meta_fmt", builtLv, Fmt.Money(cost), days), VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        meta.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        meta.AddThemeFontSizeOverride("font_size", 10);
        meta.AddThemeColorOverride("font_color", Ui.Colour(lowFunds ? "shell.state.warning_text" : "shell.paper_faint"));
        infoCol.AddChild(meta);

        var chipNormal = ctx.NineSlice("button_umber_9s", 8, 5, 2, 5, 2, "shell.plate_top", "shell.engrave", 2);
        var chipHover = ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 5, 2, 5, 2);
        var chipDisabled = ctx.BorderedBox("shell.well_bottom", "shell.rule", 1, 2, 5, 2, 5, 2);

        if (builtLv > 0 && !observer)
        {
            var dem = MakeChip(ctx, "−", false, 24, Loc.Format("shell.ws.build.demolish_tip_fmt", state.DisplayName, Fmt.Money(cost * world.Defines.DemolitionRefund)), chipNormal, chipHover, chipDisabled);
            dem.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ImmediateCampaignCommands.Commit(world, new DemolishBuildingCommand { IssuerId = owner.Id, StateMapIndex = state.MapIndex, Building = kind }); onQueued?.Invoke(); ctx.RefreshAll?.Invoke(); };
            row.AddChild(dem);
        }

        foreach (var (chipText, count) in new[] { ("+1", 1), ("×3", 3), (Loc.Get("shell.chip_all"), 99) })
        {
            var cCount = count;
            var chip = MakeChip(ctx, chipText, disabled, 30, tipText, chipNormal, chipHover, chipDisabled);
            chip.Pressed += () => QueueRegionalBuild(ctx, feedbackLabel, world, owner, state, kind, cCount, Input.IsKeyPressed(Key.Shift), onQueued);
            row.AddChild(chip);
        }
    }

    private static Button MakeChip(ShellContext ctx, string text, bool disabled, int minW, string tip, StyleBox norm, StyleBox hov, StyleBox dis)
    {
        var chip = new Button
        {
            Text = text, Disabled = disabled, CustomMinimumSize = new Vector2(minW, 24),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, TooltipText = Ui.Tip(tip),
            MouseDefaultCursorShape = disabled ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        chip.AddThemeStyleboxOverride("normal", norm);
        chip.AddThemeStyleboxOverride("hover", hov);
        chip.AddThemeStyleboxOverride("disabled", dis);
        chip.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        chip.AddThemeFontSizeOverride("font_size", 10);
        chip.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        chip.AddThemeColorOverride("font_disabled_color", Ui.Colour("shell.paper_ghost"));
        return chip;
    }

    private static void QueueRegionalBuild(
        ShellContext ctx, Label feedbackLabel, Campaign world, Nation owner,
        ScenarioState state, BuildingKind kind, int count, bool everywhere, Action onQueued)
    {
        ctx.PlayCue?.Invoke("ui_select");
        var targets = everywhere ? world.StatesOf(owner).ToArray() : new[] { state };
        var queued = 0;
        foreach (var st in targets)
        {
            for (var k = 0; k < count; k++)
            {
                if (ConstructionAdmission.Refusal(world, owner, st, kind).Length > 0) break;
                ImmediateCampaignCommands.Commit(world, new QueueConstructionCommand { IssuerId = owner.Id, StateMapIndex = st.MapIndex, Building = kind });
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
