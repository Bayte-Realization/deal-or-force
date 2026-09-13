using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
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
        _economyControls.AddChild(Title("CONSTRUCTION", 12, Ui.Colour("text.caption")));

        var rowA = new HBoxContainer();
        var rowB = new HBoxContainer();
        var rowC = new HBoxContainer();
        var rowD = new HBoxContainer();
        var rowE = new HBoxContainer();
        var rowF = new HBoxContainer();
        var rowG = new HBoxContainer();
        var rowH = new HBoxContainer();
        var rowI = new HBoxContainer();
        rowA.AddThemeConstantOverride("separation", 5);
        rowB.AddThemeConstantOverride("separation", 5);
        rowC.AddThemeConstantOverride("separation", 5);
        rowD.AddThemeConstantOverride("separation", 5);
        rowE.AddThemeConstantOverride("separation", 5);
        rowF.AddThemeConstantOverride("separation", 5);
        rowG.AddThemeConstantOverride("separation", 5);
        rowH.AddThemeConstantOverride("separation", 5);
        rowI.AddThemeConstantOverride("separation", 5);
        _economyControls.AddChild(rowA);
        _economyControls.AddChild(rowB);
        _economyControls.AddChild(rowC);
        _economyControls.AddChild(rowD);
        _economyControls.AddChild(rowE);
        _economyControls.AddChild(rowF);
        _economyControls.AddChild(rowG);
        _economyControls.AddChild(rowH);
        _economyControls.AddChild(rowI);
        AddBuildButton(rowA, BuildingKind.CivilianIndustry, "Industry");
        AddBuildButton(rowA, BuildingKind.Infrastructure, "Infrastructure");
        AddBuildButton(rowB, BuildingKind.University, "University");
        AddBuildButton(rowB, BuildingKind.Hospital, "Hospital");
        AddBuildButton(rowC, BuildingKind.Agriculture, "Agriculture");
        AddBuildButton(rowC, BuildingKind.Barracks, "Barracks");
        AddBuildButton(rowD, BuildingKind.ExtractionIndustry, "Extraction");
        AddBuildButton(rowD, BuildingKind.PowerPlant, "Power plant");
        AddBuildButton(rowE, BuildingKind.ArmsIndustry, "Arms industry");
        AddBuildButton(rowE, BuildingKind.Refinery, "Refinery");
        AddBuildButton(rowF, BuildingKind.ChemicalPlant, "Chemical plant");
        AddBuildButton(rowF, BuildingKind.Smelter, "Smelter");
        AddBuildButton(rowG, BuildingKind.ElectronicsPlant, "Electronics");
        AddBuildButton(rowG, BuildingKind.ResearchLab, "Research lab");
        AddBuildButton(rowH, BuildingKind.Port, "Port");
        AddBuildButton(rowH, BuildingKind.RailNetwork, "Rail network");
        AddBuildButton(rowI, BuildingKind.Shipyard, "Shipyard");

        _constructionStatus = Title("No active construction.", 12, Ui.Colour("text.muted"));
        _constructionStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _economyControls.AddChild(_constructionStatus);

        _economyControls.AddChild(Title("NATIONAL MARKET", 12, Ui.Colour("text.caption")));
        _marketStatus = Title("Market data is recalculated every economic week.", 12, Ui.Colour("text.body_soft"));
        _marketStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _marketStatus.TooltipText = Ui.Tip(Tr("Output + imports − demand − exports. Satisfaction below 100% is a real shortage that affects prices, GDP and living standards."));
        _economyControls.AddChild(_marketStatus);
    }

    private void AddBuildButton(Container row, BuildingKind kind, string label)
    {
        var definition = BuildingCatalog.Get(kind);
        var button = ActionButton(label, () => QueueBuilding(kind), 154, 32, kind == BuildingKind.CivilianIndustry);
        button.TooltipText = Ui.Tip(Simulation.Loc.Format("{0}: {1}, {2} days. {3}", definition.Name, Fmt.Money(definition.Cost), definition.Days, definition.Description));
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

    /// <summary>
    /// The budget, in rows that say where the money comes from and where it goes. Every
    /// line names what moves it, so a player who has never played one of these can learn
    /// the economy by hovering rather than by losing a campaign to it.
    /// </summary>
    private void ShowEconomyScreen()
    {
        var player = _campaign.Player;
        if (_selectedState is null || _selectedState.NationMapIndex != player.MapIndex)
            _selectedState = _campaign.States.FirstOrDefault(state => state.NationMapIndex == player.MapIndex);
        var state = _selectedState;
        var queue = _campaign.Construction.Where(project => project.NationId == player.Id).OrderBy(project => project.Id).ToArray();

        _sideBody.Text = Simulation.Loc.Format("{0} · {1}", player.DisplayName,
            Tr(TradePolicyRules.Name(player.TradePolicy)));
        ClearDetail();

        // Before the first economic week closes every one of these is genuinely zero, and
        // three zeros on the screen a player opens first reads as a broken game rather than
        // as a week that has not happened yet.
        var settled = _campaign.ElapsedDays >= _campaign.Defines.EconomyTickDays;
        string Weekly(float value) => settled ? Fmt.Money(value) : "—";

        DetailHeading("THE WEEK");
        DetailRow("Taxes", Weekly(player.WeeklyTaxIncome),
            $"What the tax rate of {player.TaxRate:P0} raises from the economy each week. " +
            "Raising it fills the treasury and empties the public's patience." +
            (settled ? "" : " The first economic week has not closed yet."));
        DetailRow("Tariffs", Weekly(player.WeeklyTariffIncome),
            "Duty on goods crossing the border. Trade policy sets the rate.");
        DetailRow("Expenses", Weekly(player.WeeklyExpenses),
            "Divisions, fleets, buildings and the social budget, all of it weekly.");
        DetailRow("Balance", settled ? SignedMoney(player.WeeklyBalance) : "—",
            "What the week actually leaves behind. Negative for long enough and the treasury runs out, " +
            "and a bankrupt state loses stability.",
            player.WeeklyBalance >= 0f ? "state.positive" : "state.negative",
            () => player.Budget);
        DetailRow("Treasury", Fmt.Money(player.Treasury), Fmt.MoneyExact(player.Treasury),
            player.Treasury < 0f ? "state.negative" : "text.bright");

        DetailHeading("OUTPUT");
        DetailRow("GDP", Fmt.Money(player.Gdp),
            "Annual output. It is not a dial - it emerges from what your buildings actually produce " +
            "and what the world market pays for it.");
        DetailRow("Productivity", Fmt.Number(player.Productivity),
            "How much each worker makes. Literacy raises it; shortages, war and heavy taxation lower it.");
        DetailRow("Market access", Fmt.Percent(player.MarketAccess),
            "How much of the world market you can actually reach. Blockade and distance cut it.");
        DetailRow("Trade balance", settled ? SignedMoney(player.WeeklyTradeBalance) : "—",
            "Exports against imports for the week.",
            player.WeeklyTradeBalance >= 0f ? "state.positive" : "state.warning");
        DetailRow("Stability", Fmt.Percent(player.Stability),
            "How settled the country is. Low stability slows everything and invites events you will not enjoy.",
            player.Stability < 0.35f ? "state.negative" :
            player.Stability < 0.55f ? "state.warning" : "state.positive");

        if (state is not null)
        {
            DetailHeading(Simulation.Loc.Format("REGION — {0}", state.DisplayName.ToUpperInvariant()));
            DetailRow("Population", Fmt.People(state.Population), Fmt.PeopleExact(state.Population));
            DetailRow("Literacy", Fmt.Percent(state.Literacy), "Schooling here, against the national average.");
            DetailRow("Standard of living", Fmt.Percent(state.StandardOfLiving),
                "How well this region lives. It drifts toward the national figure over time.");
            DetailRow("Urbanisation", Fmt.Percent(state.Urbanization), "Share living in towns.");
            DetailRow("Radicalism", Fmt.Percent(state.Radicalism),
                "Anger here. Poverty, austerity and foreign rule all feed it.",
                state.Radicalism > 0.45f ? "state.negative" :
                state.Radicalism > 0.25f ? "state.warning" : "state.positive");
            foreach (var socialClass in Enum.GetValues<SocialClass>())
            {
                var size = GroupSize(state, socialClass);
                if (size <= 0) continue;
                DetailRow(ClassName(socialClass), Fmt.People(size),
                    $"{Fmt.PeopleExact(size)} people of this class live in {state.DisplayName}.");
            }
        }

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
                return $"{definition.Icon} {definition.Name}: {quote.Satisfaction:P0} · {condition} · net {flow} · {Fmt.Money(quote.Price)}";
            });
        _marketStatus.Text = string.Join("\n", marketLines) +
            $"\nMarket access: {player.MarketAccess:P0} · trade: {SignedMoney(player.WeeklyTradeBalance)}\n" +
            $"Policy: {TradePolicyRules.Name(player.TradePolicy)} · tariff {TradePolicyRules.TariffRate(player.TradePolicy):P0}";
    }

    private static long GroupSize(ScenarioState state, SocialClass socialClass) =>
        state.PopulationGroups.Where(group => group.Class == socialClass).Sum(group => group.Size);

    private static string SignedMoney(float value) => (value >= 0f ? "+" : "-") + Fmt.Money(Math.Abs(value));
}
