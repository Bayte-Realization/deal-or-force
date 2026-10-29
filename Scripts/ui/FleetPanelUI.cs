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
    private Label _fleetVoyage;
    private Label _fleetYards;
    private Button _fleetOrderHulls;
    private Button _fleetCancelHulls;
    private CheckButton _fleetAdmiralty;
    private Label _fleetFeedback;
    private double _fleetSinceRefresh;

    /// <summary>How many hulls one press of the yard button lays down.</summary>
    private const int HullsPerOrder = 5;
    private readonly System.Collections.Generic.Dictionary<FleetMission, Button> _fleetMissionButtons = new();

    private static string MissionName(FleetMission mission) => Simulation.Loc.Get(mission switch
    {
        FleetMission.Anchored => "In port",
        FleetMission.Patrol => "Patrol",
        FleetMission.Blockade => "Blockade",
        FleetMission.Raid => "Raid trade",
        FleetMission.Invasion => "Invasion",
        _ => "Escort"
    });

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
        if (!_campaign.FeatureEnabled("navy")) return;
        if (!_hudLayer.Visible || fleet is null) return;
        if (!CanInspectNation(fleet.NationId)) return;
        // One selection at a time: an order has to be unambiguous about who it is for.
        if (_supportLayer is { Visible: true }) CloseSupportScreen();
        _globe.SelectSupportFormation(-1);
        ClearArmySelectionAndClose();
        _selectedFleet = fleet;
        _globe.SelectFleet(fleet.Id);
        SetFleetPanelVisible(true);
        ShowSelectedFleet();
        SetFleetFeedback(DefaultFleetHint(fleet));
    }

    private string DefaultFleetHint(Fleet fleet)
    {
        var owner = _campaign.NationById(fleet.NationId);
        var mine = owner is not null && _campaign.Player is not null && owner.Id == _campaign.Player.Id;
        return mine
            ? Tr("Right-click the map to send this fleet. The mission is what it does when it gets there.")
            : Simulation.Loc.Format("A fleet of {0}. You may look, not order.",
                owner?.DisplayName ?? Tr("Another country"));
    }

    /// <summary>
    /// The panel follows the fleet while it is open: where it is, how far it has to go, what
    /// its yards are doing. Four times a second - a fleet moves three degrees a day, and a
    /// panel that only changed when it was clicked said "in port" for a squadron halfway to
    /// the Cape.
    /// </summary>
    private void KeepFleetPanel(double delta)
    {
        if (_selectedFleet is null || _fleetPanel is null || !_fleetPanel.Visible) return;
        if (!_campaign.Fleets.Contains(_selectedFleet))
        {
            // Sunk, to the last hull.
            CloseFleetPanel();
            return;
        }
        _fleetSinceRefresh += delta;
        if (_fleetSinceRefresh < 0.25) return;
        _fleetSinceRefresh = 0;
        ShowSelectedFleet();
    }

    private string ProvincePlace(int province)
    {
        var map = _campaign.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return Tr("the open sea");
        var region = map.ProvinceToState[province];
        var state = _campaign.States.Find(candidate => candidate.MapIndex == region);
        return state?.DisplayName ?? Tr("an unnamed coast");
    }

    private void CloseFleetPanel()
    {
        _selectedFleet = null;
        _globe.SelectFleet(-1);
        _globe.ShowFleetRoute(null);
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

        // Where it is and where it is going - the course, not the crow's line.
        if (IsInstanceValid(_fleetVoyage))
        {
            string where;
            if (fleet.InPort)
                where = Simulation.Loc.Format("In port at {0}.", ProvincePlace(fleet.BerthProvinceId));
            else if (fleet.HasOrder)
            {
                var days = NavalOperations.DaysToGo(_campaign, fleet);
                where = Simulation.Loc.Format(Simulation.Plural.Key("Under way, {0:N0} day to go.", days), days);
            }
            else
            {
                // Stores last as long as the burn at this distance from home allows.
                var burn = _campaign.Defines.FleetSupplyAtSeaPerDay * (0.45f + 0.55f * Mathf.Clamp(
                    NavalOperations.Distance(fleet.Latitude, fleet.Longitude, fleet.HomeLatitude, fleet.HomeLongitude) /
                    Mathf.Max(1f, _campaign.Defines.FleetSupplyRangeDegrees), 0f, 1f));
                var lasting = Mathf.FloorToInt(fleet.Supply / Mathf.Max(0.0001f, burn));
                where = Simulation.Loc.Format(Simulation.Plural.Key("At sea, stores for about {0:N0} day.", lasting), lasting);
            }
            where += "\n" + Simulation.Loc.Format("Home port: {0}.", ProvincePlace(fleet.HomeProvinceId));
            if (fleet.HullsLost > 0)
                where += "\n" + Simulation.Loc.Format(Simulation.Plural.Key("{0:N0} hull lost in action.", fleet.HullsLost), fleet.HullsLost);
            _fleetVoyage.Text = where;
        }

        // What the yards are doing for it.
        if (IsInstanceValid(_fleetYards) && owner is not null)
        {
            var slipways = Shipbuilding.Slipways(_campaign, owner);
            string yards;
            if (fleet.HullsOrdered > 0)
            {
                var next = Shipbuilding.DaysToNextHull(_campaign, fleet);
                yards = next < 0
                    ? Simulation.Loc.Format(Simulation.Plural.Key("{0:N0} hull on order, waiting for a slipway.", fleet.HullsOrdered), fleet.HullsOrdered)
                    : Simulation.Loc.Format(Simulation.Plural.Key("{0:N0} hull on order, the next in {1:N0} days.", fleet.HullsOrdered),
                        fleet.HullsOrdered, next);
                if (mine)
                    yards += "\n" + Simulation.Loc.Format("The yards cost {0} a day.", Fmt.Money(Shipbuilding.DailyCost(_campaign, owner)));
            }
            else
                yards = slipways <= 0f
                    ? Tr("No naval yards: build a shipyard on the coast to lay down hulls.")
                    : Simulation.Loc.Format("Nothing on the slipways. The yards take {0:0.#} hulls at a time.", slipways);
            _fleetYards.Text = yards;
            if (IsInstanceValid(_fleetOrderHulls))
            {
                _fleetOrderHulls.Disabled = !mine || slipways <= 0f;
                // Written here rather than when the panel is built: the price is a define, and
                // a mod that changes it has been loaded by now.
                var tip = Ui.Tip(Simulation.Loc.Format(
                    "Lay down {0} hulls\nEach costs {1}, paid day by day while it is on the slipway, and takes about {2:N0} days. " +
                    "The yards build as many at once as they have slipways; an empty treasury stops them.",
                    HullsPerOrder, Fmt.Money(_campaign.Defines.HullCost), _campaign.Defines.HullBuildDays));
                if (_fleetOrderHulls.TooltipText != tip) _fleetOrderHulls.TooltipText = tip;
            }
            if (IsInstanceValid(_fleetCancelHulls)) _fleetCancelHulls.Disabled = !mine || fleet.HullsOrdered <= 0;
        }

        foreach (var (mission, button) in _fleetMissionButtons)
        {
            if (!IsInstanceValid(button)) continue;
            button.Disabled = !mine;
            StyleButton(button, mine && fleet.Mission == mission);
        }
        if (IsInstanceValid(_fleetAdmiralty))
        {
            _fleetAdmiralty.Visible = mine;
            var delegated = owner is not null && Delegation.Holds(owner, Delegation.Navy);
            _fleetAdmiralty.SetPressedNoSignal(delegated);
            _fleetAdmiralty.Text = Tr(delegated ? "The admiralty commands the navy" : "Let the admiralty command the navy");
        }
        _globe.ShowFleetRoute(fleet);
    }

    private void OnAdmiraltyToggled(bool delegated)
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetDelegationCommand
        {
            IssuerId = _campaign.Player.Id,
            Portfolio = Delegation.Navy,
            Delegated = delegated
        });
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedFleet();
        SetFleetFeedback(delegated
            ? Tr("The admiralty has the navy. It will look at each fleet within the next few days.")
            : Tr("The navy is yours again. Every fleet stays where the admiralty left it."));
    }

    /// <summary>Lay down hulls for the selected fleet, or cancel what has not been finished.</summary>
    private void OrderHulls(int count) => OrderHullsWithMode(count, MobilisationMode.Standard);

    private void OrderHullsWithMode(int count, MobilisationMode mode)
    {
        if (_selectedFleet is null || _campaign.Player is null) return;
        var order = new OrderHullsCommand
        {
            IssuerId = _campaign.Player.Id,
            FleetId = _selectedFleet.Id,
            Count = count,
            Mode = mode
        };
        _campaign.Commands.Enqueue(order);
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedFleet();
        if (order.Refusal.Length > 0) { SetFleetFeedback(order.Refusal); return; }
        var cost = Fmt.Money(_campaign.Defines.HullCost * System.Math.Abs(count));
        SetFleetFeedback(count > 0
            ? Simulation.Loc.Format("{0:N0} hulls laid down ({1}): {2} over the time they take to build.", count, mode, cost)
            : Tr("The unfinished hulls are cancelled. What was spent on them is spent."));
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
            ? Tr("Ordered home.")
            : Simulation.Loc.Format("Now on {0}.", MissionName(mission).ToLowerInvariant()));
    }

    /// <summary>Send the selected fleet to a point on the globe, keeping its mission.</summary>
    private bool SendSelectedFleet(float latitude, float longitude, int provinceId = -1)
    {
        if (_selectedFleet is null || _campaign.Player is null) return false;
        if (_selectedFleet.NationId != _campaign.Player.Id) return false;
        // A loaded fleet right-clicked at a shore is an invasion; an empty one is a move.
        // The player does not have to say which, because there is nothing else either could
        // have meant.
        if (_selectedFleet.Carried.Count > 0 && provinceId > 0 &&
            SeaLanes.HarbourOf(_campaign.ProvinceMap, provinceId) >= 0 &&
            !NavalOperations.FriendlyPort(_campaign, _selectedFleet.NationId, provinceId))
        {
            var launch = new LaunchInvasionCommand
            {
                IssuerId = _campaign.Player.Id,
                FleetId = _selectedFleet.Id,
                ProvinceId = provinceId
            };
            _campaign.Commands.Enqueue(launch);
            _campaign.Commands.ApplyAll(_campaign);
            ShowSelectedFleet();
            if (launch.Refusal.Length > 0) { SetFleetFeedback(launch.Refusal); return true; }
            var beach = NavalOperations.DaysToGo(_campaign, _selectedFleet);
            SetFleetFeedback(Simulation.Loc.Format(
                Simulation.Plural.Key("Invasion under way — about {0:N0} day to the beach.", beach), beach));
            return true;
        }

        // A loaded fleet sent to a friendly coast is taking its passengers there, so it goes
        // to anchor; any other fleet keeps what it was doing, and one lying in port puts to
        // sea on patrol.
        var mission = _selectedFleet.Mission == FleetMission.Anchored && _selectedFleet.Carried.Count == 0
            ? FleetMission.Patrol
            : _selectedFleet.Mission == FleetMission.Invasion ? FleetMission.Anchored : _selectedFleet.Mission;
        if (_selectedFleet.Carried.Count > 0 && provinceId > 0) mission = FleetMission.Anchored;
        var order = new SetFleetOrderCommand
        {
            IssuerId = _campaign.Player.Id,
            FleetId = _selectedFleet.Id,
            Mission = mission,
            HasDestination = true,
            Latitude = latitude,
            Longitude = longitude
        };
        _campaign.Commands.Enqueue(order);
        _campaign.Commands.ApplyAll(_campaign);
        ShowSelectedFleet();
        if (order.Refusal.Length > 0) { SetFleetFeedback(order.Refusal); return true; }
        var days = NavalOperations.DaysToGo(_campaign, _selectedFleet);
        SetFleetFeedback(Simulation.Loc.Format(
            Simulation.Plural.Key("Under way — about {0:N0} day out, on {1}.", days),
            days, MissionName(mission).ToLowerInvariant()));
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
        SetFleetFeedback(Simulation.Loc.Format(
                             Simulation.Plural.Key("{0:N0} division aboard.", _selectedFleet.Carried.Count),
                             _selectedFleet.Carried.Count) + " " +
                         Tr("Right-click an enemy shore to land them there."));
    }

    private void UnloadTroops()
    {
        if (_selectedFleet is null || _campaign.Player is null) return;
        if (_selectedFleet.NationId != _campaign.Player.Id || _selectedFleet.Carried.Count == 0) return;
        // Where the fleet is: the nearest coast of one's own or an ally's within reach of the
        // boats. It used to be the home port, wherever the fleet happened to be, so a division
        // "put ashore" in mid-Atlantic stepped off in Plymouth.
        var landed = NavalInvasion.PutAshore(_campaign, _selectedFleet, out var refused);
        ShowSelectedFleet();
        if (landed == 0) { SetFleetFeedback(refused); return; }
        SetFleetFeedback(Simulation.Loc.Format(
            Simulation.Plural.Key("{0:N0} division put ashore.", landed), landed));
    }

    private void SetFleetFeedback(string text)
    {
        if (IsInstanceValid(_fleetFeedback)) _fleetFeedback.Text = text;
    }

    private void BuildFleetPanel(Control parent)
    {
        // The army panel's frame, inset and header, so the two read as the siblings they are.
        // This one was assembled by hand - the command artwork's raw nine-slice with sixteen
        // pixels of margin - and the artwork draws its ornamental rail about thirty pixels in,
        // so the name, the subtitle and the close button all sat on the frame.
        var panel = TexturedPanel(parent, PanelSurface.Command);
        panel.Name = "FleetPanel";
        panel.Visible = false;
        Ui.Place(panel, "hud.fleet_panel");
        _fleetPanel = panel;

        var rows = PanelRows(panel, 22, 16, 6);
        rows.ClipContents = true;

        var head = new HBoxContainer { ClipContents = true };
        head.AddThemeConstantOverride("separation", 8);
        rows.AddChild(head);
        _fleetTitle = Title("Fleet", 19, Ui.Colour("text.accent"));
        _fleetTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _fleetTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        _fleetTitle.ClipText = true;
        head.AddChild(_fleetTitle);
        head.AddChild(ActionButton("×", CloseFleetPanel, 32, 28));

        // The whole width for the line under the name, as the army panel gives its own.
        // Wrapping, and not clipped: a clipped wrapping label asks for no height at all, and
        // the layout audit caught it being given one pixel.
        _fleetSubtitle = Title("", 12, Ui.Colour("text.muted"));
        _fleetSubtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_fleetSubtitle);

        _fleetCondition = Title("", 13, Ui.Colour("text.body_soft"));
        rows.AddChild(_fleetCondition);

        _fleetVoyage = Title("", 12, Ui.Colour("text.muted"));
        _fleetVoyage.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_fleetVoyage);

        // The whole navy can be handed to the admiralty, the same staff that runs every other
        // country's: it sends fleets out and brings them home to refit, mounts landings in a
        // war across water, and keeps the yards at what the budget allows.
        _fleetAdmiralty = new CheckButton { Text = Tr("Let the admiralty command the navy") };
        _fleetAdmiralty.TooltipText = Ui.Tip(Tr(
            "Hand every fleet to the admiralty - the staff that commands every other country's navy. " +
            "At peace it keeps them in port; at war it blockades, raids or escorts by who has the " +
            "stronger fleet, brings a fleet home before its stores run out, lands troops across water, " +
            "and lays down hulls to keep the navy at what the military budget pays for. " +
            "Your own orders still go through, and it takes them over again on its next look. Take it back whenever you like."));
        _fleetAdmiralty.Toggled += OnAdmiraltyToggled;
        rows.AddChild(_fleetAdmiralty);

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

        // The yards. Hulls are laid down for this fleet and join it as they are finished.
        rows.AddChild(Title("YARDS & NAVAL MOBILISATION", 11, Ui.Colour("text.caption")));
        _fleetYards = Title("", 12, Ui.Colour("text.body_soft"));
        _fleetYards.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_fleetYards);
        var yards = new HBoxContainer();
        yards.AddThemeConstantOverride("separation", 4);
        rows.AddChild(yards);
        _fleetOrderHulls = ActionButton(Simulation.Loc.Format("Lay down {0} hulls", HullsPerOrder),
            () => OrderHulls(HullsPerOrder), 148, 30);
        yards.AddChild(_fleetOrderHulls);
        _fleetCancelHulls = ActionButton("Cancel orders", () => OrderHulls(-_selectedFleet?.HullsOrdered ?? 0), 148, 30);
        _fleetCancelHulls.TooltipText = Ui.Tip(Tr("Cancel orders\nStop every hull not yet finished for this fleet. What was spent on them is not returned."));
        yards.AddChild(_fleetCancelHulls);

        var rapidYards = new HBoxContainer();
        rapidYards.AddThemeConstantOverride("separation", 4);
        rows.AddChild(rapidYards);
        rapidYards.AddChild(ActionButton("Rapid +15 hulls", () => OrderHullsWithMode(15, MobilisationMode.RapidCallUp), 148, 28));
        rapidYards.AddChild(ActionButton("Emergency +30 hulls", () => OrderHullsWithMode(30, MobilisationMode.TotalMobilisation), 148, 28, true));

        _fleetFeedback = Title("", 11, Ui.Colour("text.hint"));
        _fleetFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_fleetFeedback);
    }

    private void AddMissionButton(Container parent, FleetMission mission, int width)
    {
        var button = ActionButton(MissionName(mission), () => IssueFleetMission(mission), width, 32);
        button.SetMeta("atlas_tab_button", true);
        StyleButton(button, false);
        button.TooltipText = Ui.Tip(Simulation.Loc.Format("{0}\n{1}", MissionName(mission), MissionHelp(mission)));
        _fleetMissionButtons[mission] = button;
        parent.AddChild(button);
    }
}
