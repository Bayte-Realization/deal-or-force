using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Regional construction chip rows (+1 / x3 / All, Shift = across all own regions) inside ShellWorkPanelView.
/// </summary>
internal static class ShellRegionBuildTabView
{
    public static void PopulateBuildRows(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Label feedbackLabel,
        Campaign world,
        Nation owner,
        RegionState state,
        Action onQueued)
    {
        foreach (var kind in new[] { BuildingKind.CivilianIndustry, BuildingKind.Infrastructure, BuildingKind.ArmsIndustry, BuildingKind.Barracks })
            AddRegionBuildRow(ctx, bodyRows, feedbackLabel, world, owner, state, kind, onQueued);
    }

    private static void AddRegionBuildRow(
        ShellContext ctx,
        VBoxContainer bodyRows,
        Label feedbackLabel,
        Campaign world,
        Nation owner,
        RegionState state,
        BuildingKind kind,
        Action onQueued)
    {
        var def = BuildingCatalog.Get(kind);
        var cost = ConstructionCost.Of(world, owner, kind);
        var days = ConstructionCost.ExpectedDays(world, owner, kind);

        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 30) };
        row.AddThemeConstantOverride("separation", 6);

        var nm = new Label
        {
            Text = Loc.Get(def.Name),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = Ui.Tip($"{Loc.Get(def.Name)} · {Fmt.Money(cost)} · {days} {Loc.Get("days")}")
        };
        nm.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        nm.AddThemeFontSizeOverride("font_size", 12);
        nm.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        row.AddChild(nm);

        foreach (var (chipText, count) in new[] { ("+1", 1), ("×3", 3), (Loc.Get("shell.chip_all"), 99) })
        {
            var cCount = count;
            var chip = new Button
            {
                Text = chipText,
                CustomMinimumSize = new Vector2(36, 24),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Ui.Tip($"{Loc.Get(def.Name)} ({Fmt.Money(cost)}, {days} {Loc.Get("days")})")
            };
            chip.AddThemeStyleboxOverride("normal", ctx.NineSlice("button_umber_9s", 8, 6, 2, 6, 2));
            chip.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
            chip.AddThemeFontSizeOverride("font_size", 11);
            chip.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
            chip.Pressed += () => QueueRegionalBuild(ctx, feedbackLabel, world, owner, state, kind, cCount, Input.IsKeyPressed(Key.Shift), onQueued);
            row.AddChild(chip);
        }

        bodyRows.AddChild(row);
    }

    private static void QueueRegionalBuild(
        ShellContext ctx,
        Label feedbackLabel,
        Campaign world,
        Nation owner,
        RegionState state,
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
            if (Waterfront.Refusal(world, st.MapIndex, kind).Length > 0) continue;
            for (var k = 0; k < count; k++)
            {
                var running = world.Construction.Count(p => p.NationId == owner.Id);
                if (running >= slots || owner.Treasury < MathF.Min(0.05f, unitCost)) break;
                world.Commands.Enqueue(new QueueConstructionCommand
                {
                    IssuerId = owner.Id,
                    StateMapIndex = st.MapIndex,
                    Building = kind
                });
                world.Commands.ApplyAll(world);
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
