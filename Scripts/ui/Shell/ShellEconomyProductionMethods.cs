using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Wave 6C Production Methods selector embedded inside <see cref="ShellEconomyWorkspaceView"/>'s
/// left scroll column (below the 6-month treasury chart). Zero raw Color literals; tabular numeric
/// deltas; observer-mode safe; strictly &lt; 800 lines.
/// </summary>
internal static class ShellEconomyProductionMethods
{
    public static void BuildSection(ShellContext ctx, VBoxContainer leftCol, out VBoxContainer rowsBox)
    {
        var head = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        head.AddThemeConstantOverride("separation", 8);
        var title = new Label { Text = Loc.Get("wave6.ui.pm.heading"), VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        title.AddThemeFontSizeOverride("font_size", 11);
        title.AddThemeColorOverride("font_color", Ui.Colour("shell.brass_hi"));
        head.AddChild(title);
        head.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var sub = new Label { Text = Loc.Get("wave6.ui.pm.subtitle"), VerticalAlignment = VerticalAlignment.Center };
        sub.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        sub.AddThemeFontSizeOverride("font_size", 10);
        sub.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        head.AddChild(sub);
        leftCol.AddChild(head);

        leftCol.AddChild(ShellContext.CreateRuledLine(904, "shell.rule", 1f, 2f));

        rowsBox = new VBoxContainer
        {
            Name = "ShellEconomyProductionMethods",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        rowsBox.AddThemeConstantOverride("separation", 6);
        leftCol.AddChild(rowsBox);
    }

    public static void Refresh(
        ShellContext ctx, VBoxContainer rowsBox, Campaign world, Nation nation, Action onSwitched)
    {
        if (rowsBox is null || world is null || nation is null) return;
        foreach (var child in rowsBox.GetChildren())
        {
            rowsBox.RemoveChild(child);
            child.QueueFree();
        }

        var observing = ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        var chipNorm = ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 8, 3, 8, 3);
        var chipHover = ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 8, 3, 8, 3);
        var chipActive = ctx.BorderedBox("shell.well", "shell.brass_hi", 1, 2, 8, 3, 8, 3);
        var chipDisabled = ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 8, 3, 8, 3);

        var shown = 0;
        foreach (var kind in Enum.GetValues<BuildingKind>())
        {
            var methods = ProductionMethods.ForBuilding(kind);
            if (methods.Count <= 1) continue;
            shown++;

            var buildingId = BuildingCatalog.IdOf(kind);
            var buildingName = Loc.Get(BuildingCatalog.Get(kind).Name);
            var active = ProductionMethods.ActiveFor(nation, kind) ?? methods[0];

            var card = new PanelContainer
            {
                Name = "ShellEcoPmRow_" + buildingId,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipContents = true
            };
            card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 8, 6, 8, 6));
            rowsBox.AddChild(card);

            var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            col.AddThemeConstantOverride("separation", 5);
            card.AddChild(col);

            var top = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            top.AddThemeConstantOverride("separation", 8);
            col.AddChild(top);

            var bLbl = new Label
            {
                Text = buildingName,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            bLbl.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
            bLbl.AddThemeFontSizeOverride("font_size", 12);
            bLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
            top.AddChild(bLbl);

            var deltaLbl = new Label
            {
                Text = FormatMethodDeltas(active),
                VerticalAlignment = VerticalAlignment.Center
            };
            deltaLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            deltaLbl.AddThemeFontSizeOverride("font_size", 11);
            deltaLbl.AddThemeColorOverride("font_color", Ui.Colour(active.IsDefault ? "shell.paper_muted" : "shell.state.positive"));
            top.AddChild(deltaLbl);

            var btnFlow = new HFlowContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipContents = true
            };
            btnFlow.AddThemeConstantOverride("h_separation", 6);
            btnFlow.AddThemeConstantOverride("v_separation", 4);
            col.AddChild(btnFlow);

            for (var i = 0; i < methods.Count; i++)
            {
                var method = methods[i];
                var isCurrent = string.Equals(active.Id, method.Id, StringComparison.OrdinalIgnoreCase);
                var canUse = ProductionMethods.CanUse(world, nation, method, out var refusal);
                var tip = Loc.Get(method.Name) + "\n" +
                          (string.IsNullOrEmpty(method.Description) ? "" : Loc.Get(method.Description) + "\n") +
                          FormatMethodDeltas(method);
                if (!canUse && refusal.Length > 0) tip += "\n" + refusal;
                else if (observing) tip += "\n" + Loc.Get("shell.obs.readonly_banner");

                var btn = new Button
                {
                    Name = "ShellEcoPmBtn_" + method.Id,
                    Text = Loc.Get(method.Name),
                    CustomMinimumSize = new Vector2(0, 26),
                    Disabled = isCurrent || observing || !canUse,
                    MouseDefaultCursorShape = (isCurrent || observing || !canUse)
                        ? Control.CursorShape.Arrow
                        : Control.CursorShape.PointingHand,
                    FocusMode = Control.FocusModeEnum.None,
                    TooltipText = Ui.Tip(tip)
                };
                btn.AddThemeFontOverride("font", ctx.Font("shell.sans"));
                btn.AddThemeFontSizeOverride("font_size", 11);
                btn.AddThemeStyleboxOverride("normal", isCurrent ? chipActive : chipNorm);
                btn.AddThemeStyleboxOverride("hover", isCurrent ? chipActive : chipHover);
                btn.AddThemeStyleboxOverride("pressed", chipActive);
                btn.AddThemeStyleboxOverride("disabled", isCurrent ? chipActive : chipDisabled);
                btn.AddThemeColorOverride("font_color", Ui.Colour(isCurrent ? "shell.brass_hi" : "shell.paper_muted"));
                btn.AddThemeColorOverride("font_hover_color", Ui.Colour("shell.paper"));
                btn.AddThemeColorOverride("font_disabled_color", Ui.Colour(isCurrent ? "shell.brass_hi" : "shell.paper_faint"));

                var capturedBuildingId = buildingId;
                var capturedMethodId = method.Id;
                btn.Pressed += () =>
                {
                    if (ctx.World?.Player is not { } player) return;
                    ctx.PlayCue?.Invoke("ui_select");
                    ctx.World.Commands.Enqueue(new SetProductionMethodCommand
                    {
                        IssuerId = player.Id,
                        BuildingId = capturedBuildingId,
                        MethodId = capturedMethodId
                    });
                    ctx.World.Commands.ApplyAll(ctx.World);
                    onSwitched?.Invoke();
                };
                btnFlow.AddChild(btn);
            }
        }

        if (shown == 0)
        {
            var empty = new Label { Text = Loc.Get("wave6.ui.pm.none") };
            empty.AddThemeFontOverride("font", ctx.Font("shell.sans"));
            empty.AddThemeFontSizeOverride("font_size", 11);
            empty.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            rowsBox.AddChild(empty);
        }
    }

    internal static string FormatMethodDeltas(ProductionMethodDefinition method)
    {
        if (method is null) return "";
        var parts = new List<string>(3);
        if (MathF.Abs(method.OutputMultiplier - 1f) >= 0.005f)
            parts.Add(Loc.Format("wave6.ui.pm.delta_output_fmt", FormatPctDelta(method.OutputMultiplier)));
        if (MathF.Abs(method.JobsMultiplier - 1f) >= 0.005f)
            parts.Add(Loc.Format("wave6.ui.pm.delta_jobs_fmt", FormatPctDelta(method.JobsMultiplier)));
        if (MathF.Abs(method.EnergyMultiplier - 1f) >= 0.005f)
            parts.Add(Loc.Format("wave6.ui.pm.delta_energy_fmt", FormatPctDelta(method.EnergyMultiplier)));
        return parts.Count > 0 ? string.Join(" · ", parts) : Loc.Get("wave6.ui.pm.baseline");
    }

    private static string FormatPctDelta(float mul)
    {
        var pct = (int)MathF.Round((mul - 1f) * 100f);
        return pct >= 0 ? $"+{pct}%" : $"{pct}%";
    }
}
