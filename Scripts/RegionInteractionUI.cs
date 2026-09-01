using System;
using System.Collections.Generic;
using System.Linq;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

public partial class GlobalityGame
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
        raise.TooltipText = "Raise one line division here. Costs $0.18B and 15,000 available personnel.";
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

    private void ShowRegionOverview()
    {
        if (_selectedState is null || _selectedNation is null) return;
        _sideTitle.Text = _selectedState.Name;
        _regionControls.Visible = true;
        var buildings = BuildingCatalog.All
            .Select(kind => $"{BuildingCatalog.Get(kind).Name}: {_selectedState.Buildings.GetValueOrDefault(kind)}")
            .ToArray();
        var queue = _campaign.Construction
            .Where(project => project.StateMapIndex == _selectedState.MapIndex)
            .OrderBy(project => project.Id)
            .Select(project => $"{BuildingCatalog.Get(project.Building).Name} {project.Progress:P0}")
            .ToArray();
        var deposits = _selectedState.ResourceDeposits
            .Where(entry => entry.Value > 0.05f && entry.Key is Commodity.Coal or Commodity.Oil or Commodity.IronOre or Commodity.RareEarths)
            .OrderByDescending(entry => entry.Value)
            .Select(entry => $"{CommodityCatalog.Get(entry.Key).Name}: potential {entry.Value:0.00}")
            .ToArray();
        var owned = _selectedState.NationMapIndex == _campaign.Player.MapIndex;
        _sideBody.Text =
            $"{_selectedState.Name} · {_selectedState.Kind}\n{_selectedNation.Name} ({_selectedNation.Tag})\n" +
            $"Administrative ID: {_selectedState.Id} · map #{_selectedState.MapIndex:N0}\n\n" +
            $"Population: {_selectedState.Population:N0}\n" +
            $"Literacy: {_selectedState.Literacy:P0} · standard of living: {_selectedState.StandardOfLiving:P0}\n" +
            $"Urbanisation: {_selectedState.Urbanization:P0} · radicalism: {_selectedState.Radicalism:P0}\n\n" +
            "BUILDINGS\n" + string.Join("\n", buildings) + "\n\n" +
            "STRATEGIC RESOURCES\n" + (deposits.Length == 0 ? "No significant surveyed deposits." : string.Join("\n", deposits)) + "\n\n" +
            "CONSTRUCTION\n" + (queue.Length == 0 ? "No active regional projects." : string.Join("\n", queue)) +
            (owned ? "\n\nOwned region: development and recruitment actions are available below." :
                "\n\nForeign region: economic and recruitment actions are read-only.");
        SetRegionalActionsEnabled(owned);
    }

    private void ShowRegionPopulation()
    {
        if (_selectedState is null) return;
        _sideTitle.Text = $"{_selectedState.Name} — Population";
        _regionControls.Visible = true;
        var classes = Enum.GetValues<SocialClass>()
            .Select(socialClass =>
            {
                var groups = _selectedState.PopulationGroups.Where(group => group.Class == socialClass).ToArray();
                var size = groups.Sum(group => group.Size);
                var employed = size <= 0 ? 0f : groups.Sum(group => group.Size * group.Employment) / size;
                var wealth = size <= 0 ? 0f : groups.Sum(group => group.Size * group.Wealth) / size;
                return $"{ClassName(socialClass)}: {size:N0} · employed {employed:P0} · wealth {wealth:P0}";
            });
        var nationalities = _selectedState.PopulationGroups
            .GroupBy(group => group.Nationality)
            .Select(group => (Tag: group.Key, Size: group.Sum(item => item.Size)))
            .OrderByDescending(group => group.Size)
            .Select(group => $"{group.Tag}: {group.Size:N0} ({group.Size / (double)Math.Max(1, _selectedState.Population):P1})");
        _sideBody.Text =
            $"{_selectedState.Name} demographic register\n\nSOCIAL CLASSES\n" +
            string.Join("\n", classes) + "\n\nNATIONALITIES\n" + string.Join("\n", nationalities) +
            $"\n\nLiteracy {_selectedState.Literacy:P1} · SOL {_selectedState.StandardOfLiving:P1} · radicalism {_selectedState.Radicalism:P1}";
    }

    private void ShowProvinceOverview()
    {
        var topology = _campaign.ProvinceMap;
        if (topology is null || _selectedProvinceId <= 0 || _selectedProvinceId > topology.ProvinceCount)
        {
            _sideBody.Text = "Province topology is still being prepared.";
            return;
        }
        _regionControls.Visible = true;
        var originalOwner = _campaign.Nations.FirstOrDefault(nation => nation.MapIndex == topology.ProvinceToNation[_selectedProvinceId]);
        var controllerIndex = _campaign.ProvinceController?[_selectedProvinceId] ?? topology.ProvinceToNation[_selectedProvinceId];
        var controller = _campaign.Nations.FirstOrDefault(nation => nation.MapIndex == controllerIndex);
        var occupation = _campaign.ProvinceOccupation?[_selectedProvinceId] ?? 0f;
        var formations = _campaign.Armies.Where(army => army.ProvinceId == _selectedProvinceId).ToArray();
        var adjacent = topology.Neighbors[_selectedProvinceId];
        var riverCrossings = adjacent.Count(province => topology.IsRiverCrossing(_selectedProvinceId, province));
        var waterProfile = riverCrossings == 0 && !topology.HasLakeAccess(_selectedProvinceId)
            ? "No mapped major inland water"
            : $"River crossings: {riverCrossings}" +
              (topology.HasLakeAccess(_selectedProvinceId) ? " · navigable lake shore" : "");
        _sideTitle.Text = $"Province #{_selectedProvinceId:N0}";
        _sideBody.Text =
            $"TACTICAL PROVINCE #{_selectedProvinceId:N0}\n{_selectedState?.Name ?? "Unmapped region"}\n\n" +
            $"Legal owner: {originalOwner?.Name ?? "Unknown"}\nController: {controller?.Name ?? "Unknown"}\n" +
            $"Occupation progress: {occupation:P0}\nAdjacent provinces: {adjacent.Length}\nHydrography: {waterProfile}\n\n" +
            $"Formations present: {formations.Length}\n" +
            (formations.Length == 0 ? "No field formation is stationed here." : string.Join("\n", formations.Select(army =>
                $"{army.Name}: {army.Divisions:0.0} divisions · org {army.Organisation:P0} · supply {army.SupplyLevel:P0}"))) +
            "\n\nRight-click issues a movement order without replacing this inspection selection.";
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
        var before = _campaign.Armies.Count;
        _campaign.Commands.Enqueue(new RaiseArmyCommand
        {
            IssuerId = _campaign.Player.Id,
            StateMapIndex = _selectedState.MapIndex,
            Divisions = 1f,
            TemplateId = "line_infantry"
        });
        _campaign.Commands.ApplyAll(_campaign);
        _regionActionFeedback.Text = _campaign.Armies.Count > before
            ? $"A new line division was raised in {_selectedState.Name}."
            : "Recruitment failed: $0.18B, 15,000 personnel and an owned province are required.";
        ShowRegionOverview();
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
