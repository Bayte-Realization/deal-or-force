using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _regionControls = null!;
    private Label _regionActionFeedback = null!;
    private int _selectedProvinceId = -1;

    private void BuildRegionInteractionControls(VBoxContainer parent)
    {
        _regionControls = new VBoxContainer { Visible = false };
        _regionControls.AddThemeConstantOverride("separation", 6);
        parent.AddChild(_regionControls);
        _regionControls.AddChild(Title("REGION & PROVINCE", 12, Ui.Colour("text.caption")));

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 5);
        _regionControls.AddChild(tabs);
        tabs.AddChild(ActionButton("Overview", ShowRegionOverview, 92, 31, true));
        tabs.AddChild(ActionButton("Population", ShowRegionPopulation, 105, 31));
        tabs.AddChild(ActionButton("Province", ShowProvinceOverview, 92, 31));

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 5);
        _regionControls.AddChild(actions);
        var develop = ActionButton("Develop", () => QueueRegionalBuilding(BuildingKind.Infrastructure), 92, 31, true);
        develop.TooltipText = "Queue infrastructure in this region. Construction uses the national treasury, materials and construction capacity.";
        actions.AddChild(develop);
        var industry = ActionButton("Industry", () => QueueRegionalBuilding(BuildingKind.CivilianIndustry), 92, 31);
        industry.TooltipText = "Queue civilian industry in this region.";
        actions.AddChild(industry);
        var raise = ActionButton("Raise army", RaiseRegionalArmy, 105, 31);
        raise.TooltipText = "Choose a template, training region and paid recruitment batch.";
        actions.AddChild(raise);

        var strategic = new HBoxContainer();
        strategic.AddThemeConstantOverride("separation", 5);
        _regionControls.AddChild(strategic);
        strategic.AddChild(ActionButton("Extract", () => QueueRegionalBuilding(BuildingKind.ExtractionIndustry), 92, 31));
        strategic.AddChild(ActionButton("Power", () => QueueRegionalBuilding(BuildingKind.PowerPlant), 92, 31));
        strategic.AddChild(ActionButton("Arms", () => QueueRegionalBuilding(BuildingKind.ArmsIndustry), 105, 31));

        _regionActionFeedback = Title("", 12, Ui.Colour("text.accent_dim"));
        _regionActionFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _regionControls.AddChild(_regionActionFeedback);
    }

    private void HideRegionInteractionControls()
    {
        if (_regionControls is not null) _regionControls.Visible = false;
    }

    /// <summary>
    /// Everything a region is, in rows a player can read at a glance and interrogate one at
    /// a time. The blob of text this replaced held most of the same numbers and none of the
    /// meaning: a figure with no explanation is detail a newcomer skips and a Paradox
    /// player has to guess at.
    /// </summary>
    private void ShowRegionOverview()
    {
        if (_selectedState is null || _selectedNation is null) return;
        if (HideForeignRegionDetails()) return;
        _sideTitle.Text = _selectedState.Name;
        _regionControls.Visible = true;
        ClearDetail();

        var owner = _campaign.Nations.Find(nation => nation.MapIndex == _selectedState.NationMapIndex);
        var owned = _selectedState.NationMapIndex == _campaign.Player.MapIndex;
        _sideBody.Text = $"{_selectedState.Kind} of {owner?.Name ?? "an unknown state"}" +
                         (owned ? "" : " · foreign ground, read-only");

        DetailHeading("PEOPLE");
        DetailRow("Population", Fmt.People(_selectedState.Population), Fmt.PeopleExact(_selectedState.Population));
        DetailRow("Literacy", Fmt.Percent(_selectedState.Literacy),
            "How much of the region can read. Literacy raises productivity and assimilates minorities faster.");
        DetailRow("Standard of living", Fmt.Percent(_selectedState.StandardOfLiving),
            "How well people live here. Below the national average it feeds radicalism; above it, calm.");
        DetailRow("Urbanisation", Fmt.Percent(_selectedState.Urbanization),
            "Share living in towns. Urban regions industrialise more easily and riot more readily.");
        DetailRow("Radicalism", Fmt.Percent(_selectedState.Radicalism),
            "How angry the region is. Poverty, austerity and foreign rule all raise it.",
            _selectedState.Radicalism > 0.45f ? "state.negative" :
            _selectedState.Radicalism > 0.25f ? "state.warning" : "state.positive");

        var cultures = EconomySystem.Cultures(_selectedState);
        if (cultures.Count > 0)
            DetailRow("Largest people", DemographyCatalog.CultureName(cultures[0].Culture),
                $"{Fmt.PeopleExact(cultures[0].Size)} of {Fmt.PeopleExact(_selectedState.Population)}. " +
                "The whole table is under Population.");
        var faiths = EconomySystem.Religions(_selectedState);
        if (faiths.Count > 0)
            DetailRow("Largest faith", DemographyCatalog.ReligionName(faiths[0].Religion),
                $"{faiths[0].Size / (double)Math.Max(1, _selectedState.Population):P0} of the region.");

        var foreign = EconomySystem.ForeignShare(_selectedState, owner?.Tag ?? "");
        if (foreign > 0.001f)
        {
            var largest = cultures.FirstOrDefault(entry =>
                !DemographyCatalog.IsPrimary(owner?.Tag ?? "", entry.Culture));
            DetailRow("Minorities", Fmt.Percent(foreign),
                "People here who are not of a nation this state is for. They are more restive than " +
                "their neighbours and assimilate only slowly — the standing cost of a border that moved.",
                foreign > 0.5f ? "state.negative" : "state.warning");
            if (largest.Size > 0)
                DetailRow("Largest minority",
                    $"{DemographyCatalog.CultureName(largest.Culture)} {Fmt.People(largest.Size)}",
                    "The biggest people living under a flag that is not its own.");
        }

        DetailHeading("ECONOMY");
        var buildings = BuildingCatalog.All
            .Select(kind => (Kind: kind, Count: _selectedState.Buildings.GetValueOrDefault(kind)))
            .Where(entry => entry.Count > 0)
            .OrderByDescending(entry => entry.Count)
            .ToList();
        if (buildings.Count == 0) DetailRow("Buildings", "none", "Nothing has been built here yet.");
        foreach (var entry in buildings.Take(8))
        {
            var definition = BuildingCatalog.Get(entry.Kind);
            DetailRow(definition.Name, entry.Count.ToString("N0"), definition.Description);
        }

        var deposits = _selectedState.ResourceDeposits
            .Where(entry => entry.Value > 0.05f)
            .OrderByDescending(entry => entry.Value)
            .Take(5)
            .ToList();
        if (deposits.Count > 0)
        {
            DetailHeading("SURVEYED DEPOSITS");
            foreach (var entry in deposits)
                DetailRow(CommodityCatalog.Get(entry.Key).Name, Fmt.Number(entry.Value),
                    "Geological potential. Extraction buildings turn it into goods the market can price.");
        }

        var queue = _campaign.Construction
            .Where(project => project.StateMapIndex == _selectedState.MapIndex)
            .OrderBy(project => project.Id)
            .ToList();
        if (queue.Count > 0)
        {
            DetailHeading("UNDER CONSTRUCTION");
            foreach (var project in queue.Take(5))
                DetailRow(BuildingCatalog.Get(project.Building).Name, Fmt.Percent(project.Progress),
                    $"Costs {Fmt.Money(project.Cost)} over {project.DurationDays} days.");
        }

        var provinces = _campaign.ProvinceMap is null
            ? 0
            : PeaceConference.ProvincesOfState(_campaign.ProvinceMap, _selectedState.MapIndex).Count;
        var garrison = _campaign.Armies.Count(army => army.StateMapIndex == _selectedState.MapIndex);
        DetailHeading("GROUND");
        DetailRow("Provinces", provinces.ToString("N0"),
            "Tactical provinces this region is made of. A border drawn through them splits the region in two.");
        DetailRow("Divisions here", garrison.ToString("N0"),
            "Formations currently standing inside the region.");

        SetRegionalActionsEnabled(owned);
    }

    /// <summary>
    /// Who lives here: the peoples, their faiths, their trades and the classes they fall in.
    ///
    /// Four compositions rather than four lists of numbers. A region is a thing you are
    /// supposed to be able to recognise at a glance - one people or five, one faith or a
    /// fault line through the middle of it, a countryside or a city - and a column of
    /// percentages does not do that however accurate it is.
    /// </summary>
    private void ShowRegionPopulation()
    {
        if (_selectedState is null) return;
        if (HideForeignRegionDetails()) return;
        _sideTitle.Text = $"{_selectedState.Name} — people";
        _regionControls.Visible = true;
        ClearDetail();

        var owner = _campaign.Nations.Find(nation => nation.MapIndex == _selectedState.NationMapIndex);
        var total = Math.Max(1L, _selectedState.Population);
        _sideBody.Text = $"{Fmt.People(_selectedState.Population)} people under {owner?.Name ?? "an unknown flag"}";

        DetailHeading("THE REGION");
        DetailRow("Population", Fmt.People(_selectedState.Population), Fmt.PeopleExact(_selectedState.Population));
        DetailRow("Literacy", Fmt.Percent(_selectedState.Literacy),
            "How much of the region can read. Literacy raises productivity and assimilates minorities faster.");
        DetailRow("Standard of living", Fmt.Percent(_selectedState.StandardOfLiving),
            "How well people live here. Below the national average it feeds radicalism; above it, calm.");
        DetailRow("Urbanisation", Fmt.Percent(_selectedState.Urbanization),
            "Share living in towns — and what decides the trades below: a rural region is mostly " +
            "peasants, a city is mostly wage labour, counters and desks.");
        DetailRow("Radicalism", Fmt.Percent(_selectedState.Radicalism),
            "How angry the region is. Poverty, austerity and foreign rule all raise it.",
            _selectedState.Radicalism > 0.45f ? "state.negative" :
            _selectedState.Radicalism > 0.25f ? "state.warning" : "state.positive");
        var foreign = EconomySystem.ForeignShare(_selectedState, owner?.Tag ?? "");
        DetailRow("Minorities", Fmt.Percent(foreign),
            "People of no nation this state is for. A starred people below is one of its own.",
            foreign > 0.5f ? "state.negative" : foreign > 0.15f ? "state.warning" : "state.positive");

        DetailComposition("PEOPLES", CultureSlices(_selectedState, owner), total);
        DetailComposition("FAITHS", ReligionSlices(_selectedState), total, 6);
        DetailComposition("TRADES", ProfessionSlices(_selectedState), total, 9);

        var classes = Enum.GetValues<SocialClass>().Select(socialClass =>
        {
            var groups = _selectedState.PopulationGroups.Where(group => group.Class == socialClass).ToArray();
            var size = groups.Sum(group => group.Size);
            if (size <= 0) return new Slice(ClassName(socialClass), 0, Colors.Transparent, "");
            var employed = groups.Sum(group => group.Size * group.Employment) / size;
            var wealth = groups.Sum(group => group.Size * group.Wealth) / size;
            var living = groups.Sum(group => group.Size * group.StandardOfLiving) / size;
            return new Slice(ClassName(socialClass), size, Swatch(ClassColour(socialClass)),
                $"{Fmt.PeopleExact(size)} people\n" +
                $"Employed {employed:P0} · wealth {wealth:P0} · standard of living {living:P0}");
        }).ToList();
        DetailComposition("SOCIAL CLASSES", classes, total, 4);
    }

    /// <summary>The four strata the market prices, in the colours the economy screens use.</summary>
    private static string ClassColour(SocialClass socialClass) => socialClass switch
    {
        SocialClass.Farmers => "#7f9c4f",
        SocialClass.Workers => "#6f8fa8",
        SocialClass.MiddleClass => "#bf9f5f",
        _ => "#c06f5f"
    };

    /// <summary>One tactical province: who owns it, who holds it, and what it is made of.</summary>
    private void ShowProvinceOverview()
    {
        var topology = _campaign.ProvinceMap;
        if (topology is null || _selectedProvinceId <= 0 || _selectedProvinceId > topology.ProvinceCount)
        {
            _sideBody.Text = "The province map is still being prepared.";
            return;
        }
        _regionControls.Visible = true;
        ClearDetail();

        var owner = _campaign.Nations.Find(nation => nation.MapIndex == topology.ProvinceToNation[_selectedProvinceId]);
        var controllerIndex = _campaign.ProvinceController?[_selectedProvinceId] ?? topology.ProvinceToNation[_selectedProvinceId];
        var controller = _campaign.Nations.Find(nation => nation.MapIndex == controllerIndex);
        var occupation = _campaign.ProvinceOccupation?[_selectedProvinceId] ?? 0f;
        var formations = _campaign.Armies.Where(army => army.ProvinceId == _selectedProvinceId && CampaignIntel.PlayerCanSeeArmy(_campaign, army)).ToArray();
        var adjacent = topology.Neighbors[_selectedProvinceId];
        var crossings = adjacent.Count(province => topology.IsRiverCrossing(_selectedProvinceId, province));
        var centre = topology.CenterOf(_selectedProvinceId);

        _sideTitle.Text = $"Province #{_selectedProvinceId:N0}";
        _sideBody.Text = $"{_selectedState?.Name ?? "Unmapped ground"} · " +
                         $"{MathF.Abs(centre.Latitude):0.0}°{(centre.Latitude >= 0 ? "N" : "S")} " +
                         $"{MathF.Abs(centre.Longitude):0.0}°{(centre.Longitude >= 0 ? "E" : "W")}";

        DetailHeading("STANDING");
        DetailRow("Owner", owner?.Name ?? "unknown",
            "Whose ground this is in law. Only a peace or an annexation changes it.");
        DetailRow("Controller", controller?.Name ?? "unknown",
            "Who holds it right now. An army takes control; a treaty takes ownership.",
            controller == owner ? "text.bright" : "state.warning");
        if (controller != owner && controller is not null && CanInspectNation(controller.Id))
            DetailRow("Occupation", Fmt.Percent(occupation),
                "How firmly the occupier holds it. Above the secure threshold it can be kept at a peace.",
                occupation >= _campaign.Defines.OccupationSecureThreshold ? "state.positive" : "state.warning");

        DetailHeading("GROUND");
        DetailRow("Neighbours", adjacent.Length.ToString("N0"),
            "Provinces an army can march to from here in one step.");
        DetailRow("River crossings", crossings.ToString("N0"),
            crossings == 0
                ? "No river runs between this province and its neighbours."
                : "Attacking across a river costs the attacker dearly; a major one costs more.");
        DetailRow("Lake shore", topology.HasLakeAccess(_selectedProvinceId) ? "yes" : "no",
            "A navigable lake shore eases supply.");

        DetailHeading("FORMATIONS");
        if (formations.Length == 0)
            DetailRow("Visible divisions", "none", "No known formations. Foreign forces are hidden by fog of war.");
        foreach (var army in formations.Take(8))
            DetailRow(Shorten(army.Name, 18),
                $"{army.Divisions:0.0} div · {army.Organisation:P0}",
                CanInspectNation(army.NationId) ? DivisionTooltip(army) : "Observed foreign formation. Orders and route are unavailable.",
                army.CutOff ? "state.negative" : army.InBattle ? "state.warning" : "text.bright");
    }

    private void QueueRegionalBuilding(BuildingKind kind)
    {
        if (_selectedState is null || _selectedState.NationMapIndex != _campaign.Player.MapIndex)
        {
            _regionActionFeedback.Text = "Only an owned region can receive construction orders.";
            return;
        }
        var before = _campaign.Construction.Count;
        _campaign.Commands.Enqueue(new QueueConstructionCommand
        {
            IssuerId = _campaign.Player.Id,
            StateMapIndex = _selectedState.MapIndex,
            Building = kind
        });
        _campaign.Commands.ApplyAll(_campaign);
        _regionActionFeedback.Text = _campaign.Construction.Count > before
            ? $"{BuildingCatalog.Get(kind).Name} added to the {_selectedState.Name} queue."
            : "Could not queue the project: check treasury or the national construction limit.";
        ShowRegionOverview();
    }

    private void RaiseRegionalArmy()
    {
        if (_selectedState is null || _selectedState.NationMapIndex != _campaign.Player.MapIndex)
        {
            _regionActionFeedback.Text = "Recruitment is available only in an owned region.";
            return;
        }
        OpenRecruitment(_selectedState.MapIndex);
    }

    private void SetRegionalActionsEnabled(bool enabled)
    {
        if (_regionControls is null) return;
        foreach (var rowIndex in new[] { 2, 3 })
        {
            if (_regionControls.GetChildCount() <= rowIndex || _regionControls.GetChild(rowIndex) is not HBoxContainer actions) continue;
            foreach (var child in actions.GetChildren())
                if (child is Button button) button.Disabled = !enabled;
        }
    }

    private static string ClassName(SocialClass socialClass) => socialClass switch
    {
        SocialClass.MiddleClass => "Middle class",
        _ => socialClass.ToString()
    };
}
