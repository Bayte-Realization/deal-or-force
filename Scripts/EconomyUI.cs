using System;
using System.Linq;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

public partial class GlobalityGame
{
    private VBoxContainer _economyControls = null!;
    private Label _constructionStatus = null!;
    private Label _marketStatus = null!;
    private ScenarioState _selectedState;

    private void BuildEconomyControls(VBoxContainer parent)
    {
        _economyControls = new VBoxContainer { Visible = false };
        _economyControls.AddThemeConstantOverride("separation", 6);
        parent.AddChild(_economyControls);
        _economyControls.AddChild(Title("CONSTRUCTION", 12, new Color("#8e9daf")));

        var rowA = new HBoxContainer();
        var rowB = new HBoxContainer();
        var rowC = new HBoxContainer();
        var rowD = new HBoxContainer();
        var rowE = new HBoxContainer();
        rowA.AddThemeConstantOverride("separation", 5);
        rowB.AddThemeConstantOverride("separation", 5);
        rowC.AddThemeConstantOverride("separation", 5);
        rowD.AddThemeConstantOverride("separation", 5);
        rowE.AddThemeConstantOverride("separation", 5);
        _economyControls.AddChild(rowA);
        _economyControls.AddChild(rowB);
        _economyControls.AddChild(rowC);
        _economyControls.AddChild(rowD);
        _economyControls.AddChild(rowE);
        AddBuildButton(rowA, BuildingKind.CivilianIndustry, "Industry");
        AddBuildButton(rowA, BuildingKind.Infrastructure, "Infrastructure");
        AddBuildButton(rowB, BuildingKind.University, "University");
        AddBuildButton(rowB, BuildingKind.Hospital, "Hospital");
        AddBuildButton(rowC, BuildingKind.Agriculture, "Agriculture");
        AddBuildButton(rowC, BuildingKind.Barracks, "Barracks");
        AddBuildButton(rowD, BuildingKind.ExtractionIndustry, "Extraction");
        AddBuildButton(rowD, BuildingKind.PowerPlant, "Power plant");
        AddBuildButton(rowE, BuildingKind.ArmsIndustry, "Arms industry");

        _constructionStatus = Title("No active construction.", 12, new Color("#a8b5c4"));
        _constructionStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _economyControls.AddChild(_constructionStatus);

        _economyControls.AddChild(Title("NATIONAL MARKET", 12, new Color("#8e9daf")));
        _marketStatus = Title("Market data is recalculated every economic week.", 12, new Color("#cbd4df"));
        _marketStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _marketStatus.TooltipText = "Output + imports − demand − exports. Satisfaction below 100% is a real shortage that affects prices, GDP and living standards.";
        _economyControls.AddChild(_marketStatus);
    }

    private void AddBuildButton(Container row, BuildingKind kind, string label)
    {
        var definition = BuildingCatalog.Get(kind);
        var button = ActionButton(label, () => QueueBuilding(kind), 154, 32, kind == BuildingKind.CivilianIndustry);
        button.TooltipText = $"{definition.Name}: ${definition.Cost:0.00}B, {definition.Days} days. {definition.Description}";
        row.AddChild(button);
    }

    private void QueueBuilding(BuildingKind kind)
    {
        var player = _campaign.Player;
        if (_selectedState is null || _selectedState.NationMapIndex != player.MapIndex)
            _selectedState = _campaign.States.FirstOrDefault(state => state.NationMapIndex == player.MapIndex);
        if (_selectedState is null) return;
        _campaign.Commands.Enqueue(new QueueConstructionCommand
        {
            IssuerId = player.Id,
            StateMapIndex = _selectedState.MapIndex,
            Building = kind
        });
        _campaign.Commands.ApplyAll(_campaign);
        ShowEconomyScreen();
    }

    private void ShowEconomyScreen()
    {
        var player = _campaign.Player;
        if (_selectedState is null || _selectedState.NationMapIndex != player.MapIndex)
            _selectedState = _campaign.States.FirstOrDefault(state => state.NationMapIndex == player.MapIndex);
        var state = _selectedState;
        var queue = _campaign.Construction.Where(project => project.NationId == player.Id).OrderBy(project => project.Id).ToArray();
        _sideBody.Text =
            $"{player.Name} economy\n\nGDP: ${player.Gdp:0.00}B\nTreasury: ${player.Treasury:0.00}B\n" +
            $"Weekly taxes: ${player.WeeklyTaxIncome:0.000}B · tariffs: ${player.WeeklyTariffIncome:0.000}B\n" +
            $"Weekly expenses: ${player.WeeklyExpenses:0.000}B\n" +
            $"Balance: {SignedMoney(player.WeeklyBalance)}\nTax rate: {player.TaxRate:P0}\n" +
            $"Productivity: {player.Productivity:0.00} · stability: {player.Stability:P0}\n\n" +
            (state is null ? "Select an owned state on the map." :
                $"SELECTED STATE — {state.Name}\nPopulation: {state.Population:N0}\n" +
                $"Literacy: {state.Literacy:P0} · standard of living: {state.StandardOfLiving:P0}\n" +
                $"Urbanisation: {state.Urbanization:P0} · radicalism: {state.Radicalism:P0}\n" +
                $"Workers: {GroupSize(state, SocialClass.Workers):N0} · farmers: {GroupSize(state, SocialClass.Farmers):N0}\n" +
                $"Middle class: {GroupSize(state, SocialClass.MiddleClass):N0} · owners: {GroupSize(state, SocialClass.Owners):N0}");

        _constructionStatus.Text = queue.Length == 0
            ? "No active construction. Click a project above to add it to the queue."
            : string.Join("\n", queue.Take(3).Select((project, index) =>
            {
                var target = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == project.StateMapIndex);
                return $"{index + 1}. {BuildingCatalog.Get(project.Building).Name} — {target?.Name} · {project.Progress:P0}";
            })) + (queue.Length > 3 ? $"\n+{queue.Length - 3} queued" : "");

        var marketLines = player.Market.Values
            .OrderByDescending(quote => quote.IsShortage)
            .ThenBy(quote => quote.Commodity)
            .Select(quote =>
            {
                var definition = CommodityCatalog.Get(quote.Commodity);
                var flow = quote.Net >= 0f ? $"+{quote.Net:0.0}" : $"{quote.Net:0.0}";
                var condition = quote.IsShortage ? "SHORTAGE" : quote.Exports > 0.01f ? "SURPLUS" : "BALANCED";
                return $"{definition.Icon} {definition.Name}: {quote.Satisfaction:P0} · {condition} · net {flow} · ${quote.Price:0.000}B";
            });
        _marketStatus.Text = string.Join("\n", marketLines) +
            $"\nMarket access: {player.MarketAccess:P0} · trade: {SignedMoney(player.WeeklyTradeBalance)}\n" +
            $"Policy: {TradePolicyRules.Name(player.TradePolicy)} · tariff {TradePolicyRules.TariffRate(player.TradePolicy):P0}";
    }

    private static long GroupSize(ScenarioState state, SocialClass socialClass) =>
        state.PopulationGroups.Where(group => group.Class == socialClass).Sum(group => group.Size);

    private static string SignedMoney(float value) => value >= 0f ? $"+${value:0.000}B" : $"-${Math.Abs(value):0.000}B";
}
