using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// One building in every region of an area: Ctrl and drag a box over the map, then pick what to
/// build. Age of History's players single out its selection tool - highlight a region of the map,
/// choose a building, and it is ordered everywhere at once - and placing the same factory in
/// twenty regions one click at a time was the slowest thing a builder did here.
///
/// Every order is the ordinary QueueConstructionCommand, region by region: the same admission,
/// the same price, the same host validation in a network game. Nothing here decides anything
/// the single-region order does not.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _regionGroupBuild;

    private void OnRegionsBoxSelected(IReadOnlyList<int> provinces)
    {
        if (_campaign?.Player is not { } player || _campaign.ProvinceMap is not { } map || !_hudLayer.Visible) return;
        var wanted = new HashSet<int>();
        foreach (var province in provinces)
            if (province > 0 && province < map.ProvinceToState.Length && map.ProvinceToState[province] > 0)
                wanted.Add(map.ProvinceToState[province]);
        // The regions a country builds in: its own and those it administers (ConstructionAdmission).
        var regions = _campaign.States
            .Where(state => wanted.Contains(state.MapIndex) && !state.Unclaimed &&
                            (state.NationMapIndex == player.MapIndex || state.HeldById == player.Id))
            .OrderByDescending(state => state.Population).ToList();
        if (regions.Count == 0)
        {
            PlayStrategicCue("ui_error");
            return;
        }
        ShowRegionGroupBuild(player, regions);
    }

    private void CloseRegionGroupBuild()
    {
        if (IsInstanceValid(_regionGroupBuild)) { _regionGroupBuild.Hide(); _regionGroupBuild.QueueFree(); }
        _regionGroupBuild = null;
    }

    private void ShowRegionGroupBuild(Nation player, List<ScenarioState> regions)
    {
        CloseRegionGroupBuild();
        _regionGroupBuild = new PanelContainer { Name = "RegionGroupBuild", MouseFilter = MouseFilterEnum.Stop };
        _regionGroupBuild.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        _hudLayer.AddChild(_regionGroupBuild);
        _regionGroupBuild.Position = new Vector2(120, 148);
        _regionGroupBuild.Size = new Vector2(640, 520);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        _regionGroupBuild.AddChild(rows);
        rows.AddChild(Title(Simulation.Loc.Format("Build in {0} of your regions", regions.Count), 20, Ui.Colour("text.accent")));
        var names = string.Join(", ", regions.Take(6).Select(region => region.DisplayName)) +
                    (regions.Count > 6 ? Simulation.Loc.Format(" and {0} more", regions.Count - 6) : "");
        var list = WrappedUltimatumText(names, 13);
        rows.AddChild(list);
        var feedback = WrappedUltimatumText("Each button orders one building in every region of the selection that can take it.", 14);
        feedback.Name = "RegionGroupBuildFeedback";
        rows.AddChild(feedback);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 300) };
        rows.AddChild(scroll);
        var table = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        table.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(table);

        foreach (var kind in BuildingCatalog.All.OrderBy(kind => Tr(BuildingCatalog.Get(kind).Name), StringComparer.CurrentCulture))
        {
            var allowed = regions.Where(region => ConstructionAdmission.Refusal(_campaign, player, region, kind).Length == 0).ToList();
            var cost = ConstructionCost.Of(_campaign, player, kind);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            table.AddChild(row);
            var label = Title(Simulation.Loc.Format("{0} · {1} each", Tr(BuildingCatalog.Get(kind).Name), Fmt.Money(cost)), 14,
                Ui.Colour(allowed.Count > 0 ? "text.body" : "text.muted"));
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            var chosen = kind;
            var button = ActionButton(Simulation.Loc.Format("Build in {0}", allowed.Count), () =>
            {
                var before = _campaign.Construction.Count(project => project.NationId == player.Id);
                foreach (var region in allowed)
                    _campaign.Commands.Enqueue(new QueueConstructionCommand { IssuerId = player.Id, StateMapIndex = region.MapIndex, Building = chosen });
                _campaign.Commands.ApplyAll(_campaign);
                var queued = _campaign.Construction.Count(project => project.NationId == player.Id) - before;
                feedback.Text = queued > 0
                    ? Simulation.Loc.Format("Queued {0} × {1} for {2}.", queued, Tr(BuildingCatalog.Get(chosen).Name), Fmt.Money(cost * queued))
                    : Tr("Order submitted. The host will validate the construction sites.");
                PlayStrategicCue(queued > 0 || IsNetworkClient ? "ui_select" : "ui_error");
                Refresh();
            }, 150, 30);
            button.Disabled = allowed.Count == 0;
            button.TooltipText = allowed.Count == regions.Count ? Tr("Every selected region can take it.")
                : allowed.Count == 0 ? (regions.Select(region => ConstructionAdmission.Refusal(_campaign, player, region, chosen)).FirstOrDefault(text => text.Length > 0) ?? "")
                : Simulation.Loc.Format("{0} of the {1} regions can take it.", allowed.Count, regions.Count);
            row.AddChild(button);
        }

        var close = ActionButton("Close", CloseRegionGroupBuild, 0, 34);
        rows.AddChild(close);
        _regionGroupBuild.AddChild(new DecisionDialogLayout { Panel = _regionGroupBuild, PreservePlacement = true,
            AvoidLeft = _hudNavigationPanel, AvoidTop = _hudTopPanel });
        GetViewport().GuiReleaseFocus();
    }
}
