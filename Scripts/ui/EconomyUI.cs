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

    private void BuildEconomyControls(VBoxContainer parent) => BuildEconomyWorkbench(parent);

    private void QueueBuilding(BuildingKind kind)
    {
        var player = _campaign.Player;
        if (_selectedState is null || _selectedState.NationMapIndex != player.MapIndex) return;
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

        // Regional demographics live in the Region dossier, not the national budget.

        _constructionStatus.Text = queue.Length == 0
            ? Tr("No active construction. Choose a building below.")
            : string.Join("\n", queue.Take(3).Select((project, index) =>
            {
                var target = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == project.StateMapIndex);
                return $"{index + 1}. {Tr(BuildingCatalog.Get(project.Building).Name)} — {target?.Name} · {project.Progress:P0}";
            })) + (queue.Length > 3 ? "\n" + Simulation.Loc.Format("+{0} queued", queue.Length - 3) : "");

        RefreshEconomyWorkbench();
        ApplyDossierTab();
    }

    private static long GroupSize(ScenarioState state, SocialClass socialClass) =>
        state.PopulationGroups.Where(group => group.Class == socialClass).Sum(group => group.Size);

    private static string SignedMoney(float value) => (value >= 0f ? "+" : "-") + Fmt.Money(Math.Abs(value));
}
