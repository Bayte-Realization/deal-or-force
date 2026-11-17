using System.Collections.Generic;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Which city the government moves to.
///
/// A capital is a city (CapitalCore), and "Move the capital here" used to pick the city for the
/// player: the first one the map happens to list in the region. He was not asked, was not told
/// which it would be, and could not move between two cities of one region at all. Now the order
/// names the city: one usable city in the region and the button moves there and says so; several,
/// and a short list opens under the pointer - every city of the region the government could sit
/// in today, equal, by name.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _capitalCityChooser;
    private VBoxContainer _capitalCityRows;
    private ScenarioState _capitalCityRegion;

    /// <summary>The cities of the selected region the player's government could move into, for both move buttons.</summary>
    private List<CityDefinition> CapitalCityChoices() =>
        _selectedState is null || _campaign?.Player is null
            ? new List<CityDefinition>()
            : CapitalCore.Candidates(_campaign, _campaign.Player, _selectedState);

    /// <summary>
    /// Why neither a city of this region nor the region itself can take the capital now, or empty.
    /// The capital's own region is open when it has another city to move to.
    /// </summary>
    private string CapitalMoveRefusal()
    {
        if (_selectedState is null || _campaign?.Player is null) return "Choose a region.";
        var player = _campaign.Player;
        var cities = CapitalCityChoices();
        return cities.Count > 0
            ? CapitalCore.Refusal(_campaign, player, _selectedState, cities[0].Id)
            : CapitalCore.Refusal(_campaign, player, _selectedState);
    }

    /// <summary>What the move button promises, naming the city when there is only one to name.</summary>
    private string CapitalMoveTarget()
    {
        var cities = CapitalCityChoices();
        return cities.Count == 1 ? cities[0].DisplayName
            : cities.Count > 1 ? Loc.Format("a city of {0} that you choose", _selectedState.DisplayName)
            : _selectedState.DisplayName;
    }

    /// <summary>Of the cities the government could move into in a region, the one nearest a province; null when the region has none.</summary>
    private CityDefinition NearestCapitalChoice(Nation nation, ScenarioState region, int provinceId)
    {
        var map = _campaign?.ProvinceMap;
        var cities = CapitalCore.Candidates(_campaign, nation, region);
        if (cities.Count == 0 || map is null || provinceId <= 0 || provinceId > map.ProvinceCount) return cities.Count > 0 ? cities[0] : null;
        var centre = map.CenterOf(provinceId);
        CityDefinition best = null;
        var bestDistance = float.MaxValue;
        foreach (var city in cities)
        {
            // Near enough for choosing between neighbours: degrees, with longitude narrowed by latitude.
            var dLat = city.Latitude - centre.Latitude;
            var dLon = Mathf.Wrap(city.Longitude - centre.Longitude, -180f, 180f) * Mathf.Cos(Mathf.DegToRad(centre.Latitude));
            var distance = dLat * dLat + dLon * dLon;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = city;
        }
        return best;
    }

    private void ChooseCapitalCity()
    {
        if (_selectedState is null || _campaign?.Player is null) return;
        var cities = CapitalCityChoices();
        // No city of ours on the map here: the government goes to the region, under its name.
        if (cities.Count == 0) { OrderCapitalMove(null); return; }
        if (cities.Count == 1) { OrderCapitalMove(cities[0]); return; }

        if (_capitalCityChooser is null || !GodotObject.IsInstanceValid(_capitalCityChooser))
        {
            _capitalCityChooser = new PanelContainer { Name = "CapitalCityChooser", ZIndex = 60, MouseFilter = MouseFilterEnum.Stop };
            _capitalCityChooser.AddThemeStyleboxOverride("panel", ShellPanelStyle());
            _hudLayer.AddChild(_capitalCityChooser);
            var margin = Margin(_capitalCityChooser, 10, 8);
            var scroll = new ScrollContainer
            {
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                CustomMinimumSize = new Vector2(300, 0)
            };
            margin.AddChild(scroll);
            _capitalCityRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _capitalCityRows.AddThemeConstantOverride("separation", 4);
            scroll.AddChild(_capitalCityRows);
        }
        foreach (var child in _capitalCityRows.GetChildren()) { _capitalCityRows.RemoveChild(child); child.QueueFree(); }

        _capitalCityRegion = _selectedState;
        var player = _campaign.Player;
        var head = Title(Loc.Format("Move the capital to a city of {0}", _selectedState.DisplayName), 13, Ui.Colour("text.bright"));
        head.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _capitalCityRows.AddChild(head);
        var price = Title(Loc.Format("{0} from the treasury · {1} of stability", Fmt.Money(CapitalCore.Cost(_campaign, player)),
            _campaign.Defines.CapitalMoveStabilityCost.ToString("P0")), 11, Ui.Colour("text.muted"));
        _capitalCityRows.AddChild(price);
        foreach (var city in cities)
        {
            var captured = city;
            var refusal = CapitalCore.Refusal(_campaign, player, _selectedState, city.Id);
            var row = ActionButton(city.DisplayName, () => OrderCapitalMove(captured), 0, 30);
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.Alignment = HorizontalAlignment.Left;
            row.Disabled = refusal.Length > 0;
            row.TooltipText = Ui.Tip(refusal.Length > 0 ? Tr(refusal) : Loc.Format("Move the government to {0}.", city.DisplayName));
            _capitalCityRows.AddChild(row);
        }
        var cancel = ActionButton("Cancel", CloseCapitalCityChooser, 0, 28);
        cancel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _capitalCityRows.AddChild(cancel);

        // Under the pointer, kept on the screen; a long list scrolls inside a fixed height.
        var screen = GetViewportRect().Size;
        var height = Mathf.Min(screen.Y - 40f, 62f + 34f * (cities.Count + 1));
        ((ScrollContainer)_capitalCityRows.GetParent()).CustomMinimumSize = new Vector2(300, height - 16f);
        var at = GetViewport().GetMousePosition() + new Vector2(12, -height * .5f);
        at.X = Mathf.Clamp(at.X, 8f, screen.X - 332f);
        at.Y = Mathf.Clamp(at.Y, 8f, screen.Y - height - 8f);
        _capitalCityChooser.Position = at;
        _capitalCityChooser.Size = new Vector2(320, height);
        _capitalCityChooser.Visible = true;
        _capitalCityChooser.MoveToFront();
    }

    private void CloseCapitalCityChooser()
    {
        if (_capitalCityChooser is not null && GodotObject.IsInstanceValid(_capitalCityChooser)) _capitalCityChooser.Visible = false;
        _capitalCityRegion = null;
    }

    /// <summary>The list belongs to the region it was opened for; another selection puts it away.</summary>
    private void SyncCapitalCityChooser()
    {
        if (_capitalCityChooser is { Visible: true } && !ReferenceEquals(_capitalCityRegion, _selectedState)) CloseCapitalCityChooser();
    }

    private void OrderCapitalMove(CityDefinition city)
    {
        var region = _capitalCityRegion ?? _selectedState;
        CloseCapitalCityChooser();
        if (region is null || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var command = new MoveCapitalCommand { IssuerId = player.Id, StateMapIndex = region.MapIndex, CityId = city?.Id ?? "" };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        var feedback = command.Moved
            ? Loc.Format("The government has moved to {0}.", CountryNames.Capital(player, _campaign))
            : Tr(CapitalCore.Refusal(_campaign, player, region, city?.Id));
        if (_regionActionFeedback is not null) _regionActionFeedback.Text = feedback;
        if (_regionCardFeedback is not null) _regionCardFeedback.Text = feedback;
        RefreshRegionViews();
    }
}
