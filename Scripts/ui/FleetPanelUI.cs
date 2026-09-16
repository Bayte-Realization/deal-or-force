using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Commanding a fleet.
///
/// The navy existed as fifteen counters that sat where they were put and could not be
/// clicked. This is the other half of making it a navy: select one, see what state it is
/// in, tell it what to do, and right-click the map to send it there.
///
/// The panel is deliberately the army panel's smaller sibling. A fleet is built on the
/// same bones as a division - hulls with an organisation, a strength and a supply - so it
/// is read the same way, and a player who has learned one has learned the other. What it
/// does not have is the army panel's battle plans, stances and reinforcement priorities:
/// a fleet has one mission and five of them to choose from.
/// </summary>
public partial class DealOrForceGame
{
    private Control _fleetPanel;
    private Fleet _selectedFleet;
    private Label _fleetTitle;
    private Label _fleetSubtitle;
    private Label _fleetCondition;
    private Label _fleetFeedback;
    private readonly System.Collections.Generic.Dictionary<FleetMission, Button> _fleetMissionButtons = new();

    private static string MissionName(FleetMission mission) => mission switch
    {
        FleetMission.Anchored => "In port",
        FleetMission.Patrol => "Patrol",
        FleetMission.Blockade => "Blockade",
        FleetMission.Raid => "Raid trade",
        FleetMission.Invasion => "Invasion",
        _ => "Escort"
    };

    private static string MissionHelp(FleetMission mission) => mission switch
    {
        FleetMission.Anchored =>
            "Return to port and stay there. Repairs, restores cohesion and refills stores fastest, and does nothing else.",
        FleetMission.Patrol =>
            "Hold station at sea and fight what comes. Costs stores; the further from home, the faster.",
        FleetMission.Blockade =>
            "Sit on an enemy coast. Cuts what reaches their provinces by sea, so an army on that shore stops being supplied.",
        FleetMission.Raid =>
            "Hunt enemy merchant shipping. Takes money out of their weekly trade for as long as it lasts.",
        FleetMission.Invasion =>
            "Carrying an army to a coast. Right-click enemy shore to send it; what it carries goes ashore on arrival.",
        _ => "Guard your own shipping against enemy raiders, and keep the money coming in."
    };

    private void OnFleetPicked(Fleet fleet)
    {
        if (!_hudLayer.Visible || fleet is null) return;
        if (!CanInspectNation(fleet.NationId)) return;
        // One selection at a time: an order has to be unambiguous about who it is for.
        ClearArmySelectionAndClose();
        _selectedFleet = fleet;
        _globe.SelectFleet(fleet.Id);
        SetFleetPanelVisible(true);
        ShowSelectedFleet();
    }

    private void CloseFleetPanel()
    {
        _selectedFleet = null;
        _globe.SelectFleet(-1);
        SetFleetPanelVisible(false);
    }

    private void SetFleetPanelVisible(bool visible)
    {
        if (_fleetPanel is null) return;
        _fleetPanel.Visible = visible;
    }

    private void ShowSelectedFleet()
    {
        if (_selectedFleet is null || !IsInstanceValid(_fleetTitle)) return;
        var fleet = _selectedFleet;
        var owner = _campaign.NationById(fleet.NationId);
        var mine = owner is not null && _campaign.Player is not null && owner.Id == _campaign.Player.Id;

        _fleetTitle.Text = Loc.Get(fleet.Name);
        _fleetSubtitle.Text = Simulation.Loc.Format("{0} · {1:N0} hulls · {2}",
                                  owner?.Tag ?? "?", fleet.Hulls, MissionName(fleet.Mission)) +
                              (fleet.IsMoving ? Tr(" · under way") : "") +
                              (fleet.InAction ? Tr(" · in action") : "");
        var carried = NavalInvasion.CarriedDivisions(_campaign, fleet);
        var capacity = NavalInvasion.Capacity(_campaign, fleet);
        _fleetCondition.Text = Simulation.Loc.Format(
            "Strength {0:P0}    Cohesion {1:P0}\n" +
            "Stores {2:P0}    Readiness {3:P0}\n" +
            "Aboard {4:0.#} of {5:0.#} divisions",
            fleet.Strength, fleet.Organisation, fleet.Supply, fleet.Readiness, carried, capacity);

        foreach (var (mission, button) in _fleetMissionButtons)
        {
            if (!IsInstanceValid(button)) continue;
            button.Disabled = !mine;
            StyleButton(button, mine && fleet.Mission == mission);
        }
        if (!IsInstanceValid(_fleetFeedback)) return;
        _fleetFeedback.Text = mine
            ? "Right-click the map to send this fleet. The mission is what it does when it gets there."
            : $"{owner?.Name ?? "Another country"}'s fleet. You may look, not order.";
    }

    private void IssueFleetMission(FleetMission mission)
    {
        if (_selectedFleet is null || _campaign.Player is null) return;
        if (_selectedFleet.NationId != _campaign.Player.Id) return;
        _campaign.Commands.Enqueue(new SetFleetOrderCommand
        {
            IssuerId = _campaign.Player.Id,
            FleetId = _selectedFleet.Id,
            Mission = mission
        });
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedFleet();
        SetFleetFeedback(mission == FleetMission.Anchored
            ? "Ordered home."
            : $"Now on {MissionName(mission).ToLowerInvariant()}.");
    }

    /// <summary>Send the selected fleet to a point on the globe, keeping its mission.</summary>
    private bool SendSelectedFleet(float latitude, float longitude, int provinceId = -1)
    {
        if (_selectedFleet is null || _campaign.Player is null) return false;
        if (_selectedFleet.NationId != _campaign.Player.Id) return false;
        // A loaded fleet right-clicked at a shore is an invasion; an empty one is a move.
        // The player does not have to say which, because there is nothing else either could
        // have meant.
        if (_selectedFleet.Carried.Count > 0 && provinceId > 0)
        {
            _campaign.Commands.Enqueue(new LaunchInvasionCommand
            {
                IssuerId = _campaign.Player.Id,
                FleetId = _selectedFleet.Id,
                ProvinceId = provinceId
            });
            _campaign.Commands.ApplyAll(_campaign);
            ShowSelectedFleet();
            var reach = NavalOperations.Distance(_selectedFleet.Latitude, _selectedFleet.Longitude,
                latitude, longitude);
            SetFleetFeedback($"Invasion under way — about " +
                             $"{Mathf.CeilToInt(reach / Mathf.Max(0.01f, _campaign.Defines.FleetDegreesPerDay)):N0} " +
                             "day(s) to the beach.");
            return true;
        }

        var mission = _selectedFleet.Mission == FleetMission.Anchored
            ? FleetMission.Patrol
            : _selectedFleet.Mission;
        _campaign.Commands.Enqueue(new SetFleetOrderCommand
        {
            IssuerId = _campaign.Player.Id,
            FleetId = _selectedFleet.Id,
            Mission = mission,
            HasDestination = true,
            Latitude = latitude,
            Longitude = longitude
        });
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedFleet();
        var away = NavalOperations.Distance(_selectedFleet.Latitude, _selectedFleet.Longitude, latitude, longitude);
        var days = Mathf.CeilToInt(away / Mathf.Max(0.01f, _campaign.Defines.FleetDegreesPerDay));
        SetFleetFeedback($"Under way — about {days:N0} day(s) out, on {MissionName(mission).ToLowerInvariant()}.");
        return true;
    }

    /// <summary>
    /// Take aboard whatever of the player's divisions are standing at this fleet's port.
    /// Loading is deliberately one button rather than a manifest: the constraint that
    /// matters is how much fits, and the player can see that on the panel.
    /// </summary>
    private void LoadTroops()
    {
        if (_selectedFleet is null || _campaign.Player is null) return;
        if (_selectedFleet.NationId != _campaign.Player.Id) return;

        var wanted = new System.Collections.Generic.List<int>();
        var refused = "";
        foreach (var army in _campaign.Armies)
        {
            if (army.NationId != _campaign.Player.Id || army.EmbarkedFleetId >= 0) continue;
            if (NavalInvasion.CanEmbark(_campaign, _selectedFleet, army, out var reason)) wanted.Add(army.Id);
            else if (refused.Length == 0 && reason.Length > 0) refused = reason;
        }
        if (wanted.Count == 0)
        {
            SetFleetFeedback(refused.Length > 0
                ? refused
                : "No division of yours is standing at this fleet's port.");
            return;
        }

        _campaign.Commands.Enqueue(new EmbarkArmiesCommand
        {
            IssuerId = _campaign.Player.Id,
            FleetId = _selectedFleet.Id,
            ArmyIds = wanted
        });
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedFleet();
        SetFleetFeedback($"{_selectedFleet.Carried.Count:N0} division(s) aboard. " +
                         "Right-click an enemy shore to land them there.");
    }

    private void UnloadTroops()
    {
        if (_selectedFleet is null || _campaign.Player is null) return;
        if (_selectedFleet.NationId != _campaign.Player.Id || _selectedFleet.Carried.Count == 0) return;
        var port = _selectedFleet.HomeProvinceId;
        var landed = NavalInvasion.Disembark(_campaign, _selectedFleet, port);
        _selectedFleet.Mission = FleetMission.Anchored;
        _selectedFleet.TargetProvinceId = -1;
        ShowSelectedFleet();
        SetFleetFeedback($"{landed:N0} division(s) put ashore.");
    }

    private void SetFleetFeedback(string text)
    {
        if (IsInstanceValid(_fleetFeedback)) _fleetFeedback.Text = text;
    }

    private void BuildFleetPanel(Control parent)
    {
        var panel = new PanelContainer { Name = "FleetPanel", Visible = false, ClipContents = true };
        panel.AddThemeStyleboxOverride("panel", PanelStyle(PanelSurface.Command));
        Ui.Place(panel, "hud.fleet_panel");
        parent.AddChild(panel);
        _fleetPanel = panel;

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 16);
        panel.AddChild(margin);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        margin.AddChild(rows);

        var head = new HBoxContainer();
        rows.AddChild(head);
        var heading = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        head.AddChild(heading);
        _fleetTitle = Title("Fleet", 18, Ui.Colour("text.accent"));
        heading.AddChild(_fleetTitle);
        _fleetSubtitle = Title("", 12, Ui.Colour("text.muted"));
        heading.AddChild(_fleetSubtitle);
        head.AddChild(ActionButton("×", CloseFleetPanel, 38, 34));

        _fleetCondition = Title("", 13, Ui.Colour("text.body_soft"));
        rows.AddChild(_fleetCondition);

        rows.AddChild(Title("MISSION", 11, Ui.Colour("text.caption")));
        var missions = new VBoxContainer();
        missions.AddThemeConstantOverride("separation", 4);
        rows.AddChild(missions);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 4);
        missions.AddChild(top);
        AddMissionButton(top, FleetMission.Anchored, 96);
        AddMissionButton(top, FleetMission.Patrol, 96);
        AddMissionButton(top, FleetMission.Escort, 96);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 4);
        missions.AddChild(bottom);
        AddMissionButton(bottom, FleetMission.Blockade, 148);
        AddMissionButton(bottom, FleetMission.Raid, 148);

        rows.AddChild(Title("TROOPS", 11, Ui.Colour("text.caption")));
        var cargo = new HBoxContainer();
        cargo.AddThemeConstantOverride("separation", 4);
        rows.AddChild(cargo);
        var load = ActionButton("Load troops", LoadTroops, 148, 32);
        load.TooltipText = Ui.Tip(Tr("Load troops\nTake aboard your divisions standing at this fleet's port. " +
                           "What fits is set by the hulls."));
        cargo.AddChild(load);
        var unload = ActionButton("Put ashore", UnloadTroops, 148, 32);
        unload.TooltipText = Ui.Tip(Tr("Put ashore\nLand what the fleet is carrying on the nearest friendly coast."));
        cargo.AddChild(unload);

        _fleetFeedback = Title("", 11, Ui.Colour("text.hint"));
        _fleetFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_fleetFeedback);
    }

    private void AddMissionButton(Container parent, FleetMission mission, int width)
    {
        var button = ActionButton(MissionName(mission), () => IssueFleetMission(mission), width, 32);
        button.TooltipText = Ui.Tip(Simulation.Loc.Format("{0}\n{1}", MissionName(mission), MissionHelp(mission)));
        _fleetMissionButtons[mission] = button;
        parent.AddChild(button);
    }
}
