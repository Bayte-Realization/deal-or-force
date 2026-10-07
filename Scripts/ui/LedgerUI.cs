using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _ledgerLayer = null!;
    private Label _ledgerTitle = null!;
    private Label _ledgerSummary = null!;
    private Label _ledgerPolicyLine = null!;
    // The ledger reads; it no longer governs. Everything a player can actually change now
    // lives in State Affairs, because a screen that is half report and half control desk
    // teaches nobody where to look for either.
    private StrategicLineChart _ledgerLineA = null!;
    private StrategicLineChart _ledgerLineB = null!;
    private StrategicBarChart _ledgerBarsA = null!;
    private StrategicBarChart _ledgerBarsB = null!;
    private string _ledgerTab = "Overview";
    private int _ledgerViewedNationId = -1;
    private readonly Dictionary<string, Button> _ledgerTabButtons = new(StringComparer.Ordinal);

    // Properties rather than fields: a mod's skin is layered in after this class is first
    // touched, and a `static readonly` would have frozen the shipped palette before then.
    private static Color Gold => Ui.Colour("series.gold");
    private static Color Blue => Ui.Colour("series.blue");
    private static Color Green => Ui.Colour("series.green");
    private static Color Red => Ui.Colour("series.red");
    private static Color Violet => Ui.Colour("series.violet");
    private static Color Orange => Ui.Colour("series.orange");

    private void BuildLedgerInterface()
    {
        _ledgerLayer = FullLayer("NationalLedger");
        _ledgerLayer.Visible = false;
        _hudLayer.AddChild(_ledgerLayer);

        var shade = new ColorRect
        {
            Color = new Color(0.015f, 0.026f, 0.041f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _ledgerLayer.AddChild(shade);

        var panel = TexturedPanel(_ledgerLayer);
        Ui.Place(panel, "modal.ledger");

        var rows = PanelRows(panel, 22, 16, 9);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        _ledgerTitle = Title("NATIONAL LEDGER", 25, Ui.Colour("text.accent"));
        _ledgerTitle.TooltipText = Ui.Tip(Tr("A record, for any country in the world. Nothing on this screen changes " +
                                   "anything: what you can decide is in State Affairs."));
        _ledgerTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_ledgerTitle);

        var playerView = ActionButton("Player nation", () =>
        {
            _ledgerViewedNationId = _campaign.Player.Id;
            RefreshLedger();
        }, 116, 32, true);
        playerView.TooltipText = Ui.Tip(Tr("Return every chart to the country you currently control."));
        header.AddChild(playerView);
        var selectedView = ActionButton("Map selection", () =>
        {
            _ledgerViewedNationId = (_selectedNation ?? _campaign.Player).Id;
            RefreshLedger();
        }, 124, 32);
        selectedView.TooltipText = Ui.Tip(Tr("Compare the country most recently selected on the map. Policies remain read-only for foreign countries."));
        header.AddChild(selectedView);
        header.AddChild(HandbookButton("reports"));
        header.AddChild(ActionButton("×", CloseLedger, 38, 32));

        var tabs = new HFlowContainer();
        tabs.AddThemeConstantOverride("h_separation", 6);
        tabs.AddThemeConstantOverride("v_separation", 6);
        rows.AddChild(tabs);
        foreach (var definition in CampaignUiCatalog.For("Ledger").Tabs)
        {
            var tab = definition.Id;
            var captured = tab;
            var button = ActionButton(definition.Label, () =>
            {
                _ledgerTab = captured;
                RefreshLedger();
            }, 116, 32, tab == "Overview");
            button.TooltipText = Ui.Tip(Tr(tab switch
            {
                "Overview" => "Who governs, what the country is doing, and what the week costs. Change any of it in State Affairs.",
                "Economy" => "Fiscal history and the construction base. The budget itself is set in State Affairs.",
                "Market" => "Prices, shortages, and what trade policy has done. Change it in State Affairs.",
                "Population" => "Population growth, living standards, classes and the largest states.",
                "Military" => "Field strength, reserves, formations and active-war performance.",
                "Chronicle" => "Recorded events in the world and this country.",
                "Vulnerabilities" => "Import dependence and concentration of named suppliers.",
                _ => "Compare this country with the largest powers in the current world."
            }));
            tabs.AddChild(button);
            _ledgerTabButtons[tab] = button;
        }

        _ledgerSummary = Title("", 13, Ui.Colour("text.body_soft"));
        _ledgerSummary.CustomMinimumSize = new Vector2(0, 34);
        rows.AddChild(_ledgerSummary);

        // The Market tab's own caption. It used to write into a label that lived in State
        // Affairs - the text was set in a window nobody was looking at. It lives here now,
        // under the charts it belongs to.
        _ledgerPolicyLine = Title("", 12, Ui.Colour("text.caption"));
        _ledgerPolicyLine.CustomMinimumSize = new Vector2(0, 20);
        rows.AddChild(_ledgerPolicyLine);

        var grid = new GridContainer
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        grid.AddThemeConstantOverride("h_separation", 9);
        grid.AddThemeConstantOverride("v_separation", 9);
        rows.AddChild(grid);
        _ledgerGrid = grid;
        BuildStrategicReports(rows);
        _ledgerLineA = new StrategicLineChart();
        _ledgerLineB = new StrategicLineChart();
        _ledgerBarsA = new StrategicBarChart();
        _ledgerBarsB = new StrategicBarChart();
        grid.AddChild(_ledgerLineA);
        grid.AddChild(_ledgerLineB);
        grid.AddChild(_ledgerBarsA);
        grid.AddChild(_ledgerBarsB);
    }

    /// <summary>
    /// A label for a horizontal row, which must not wrap.
    ///
    /// <see cref="Title"/> wraps by word, which is right in a column and catastrophic in a
    /// row: an HBoxContainer offers a label no width, so the label takes one character per
    /// line, becomes a thousand pixels tall, and drags the whole row down the screen with
    /// it. That is what the economy panel was doing - a sentence of hint text set
    /// vertically down the middle of the page, with the charts squeezed out below it.
    /// </summary>
    internal static Label RowLabel(string text, int size, Color colour, float minimumWidth = 0f,
        bool upper = false)
    {
        var label = Title(text, size, colour, upper);
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.VerticalAlignment = VerticalAlignment.Center;
        if (minimumWidth > 0f) label.CustomMinimumSize = new Vector2(minimumWidth, 0);
        return label;
    }

    /// <summary>A caption, a slider, and a live reading of what moving it would do.</summary>
    private static HBoxContainer SliderRow(string caption, HSlider slider, out Label reading)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 9);
        row.AddChild(RowLabel(caption, 12, Ui.Colour("text.caption"), 104, upper: true));
        slider.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        slider.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(slider);
        reading = RowLabel("", 12, Ui.Colour("text.body"), 172);
        reading.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(reading);
        return row;
    }






    private void OpenLedger()
    {
        _openScreen = "Ledger";
        _sidePanel.Visible = false;
        _ledgerViewedNationId = _ledgerViewedNationId < 0 ? _campaign.Player.Id : _ledgerViewedNationId;
        _ledgerLayer.Visible = true;
        // The ledger is a modal analysis workspace. It is authored before the
        // strategic outliner, so explicitly bring it above later HUD siblings.
        _ledgerLayer.MoveToFront();
        RefreshLedger();
    }

    private void CloseLedger()
    {
        _ledgerLayer.Visible = false;
        if (_openScreen == "Ledger") _openScreen = "";
    }









    private Nation ViewedLedgerNation() => _campaign?.NationById(_ledgerViewedNationId) ?? _campaign?.Player;

    /// <summary>Active national modifiers, newest first, with how long each has left.</summary>
    private string DescribeModifiers(Nation nation)
    {
        if (nation.Modifiers.Count == 0) return "";

        var parts = nation.Modifiers
            .OrderByDescending(active => active.GrantedDay)
            .Select(active =>
            {
                var definition = ModifierCatalog.Get(active.Id);
                var label = definition?.Name ?? active.Id;
                if (active.IsPermanent) return label;
                var days = active.DaysRemaining(_campaign.ElapsedDays);
                return $"{label} ({days}d)";
            });

        return "In effect:  " + string.Join("   ·   ", parts);
    }

    private void RefreshLedger()
    {
        if (_campaign?.Player is null || _ledgerLayer is null || !_ledgerLayer.Visible) return;
        if (!_ledgerTabButtons.ContainsKey(_ledgerTab))
            _ledgerTab = _ledgerTabButtons.Keys.FirstOrDefault() ?? "";
        if (_ledgerTab.Length == 0)
        {
            _ledgerGrid.Visible = _ledgerReportScroll.Visible = false;
            _ledgerSummary.Text = _ledgerPolicyLine.Text = "";
            return;
        }
        var nation = ViewedLedgerNation() ?? _campaign.Player;
        var known = CanInspectNation(nation.Id);
        var report = _ledgerTab is "Chronicle" or "Vulnerabilities";
        _ledgerGrid.Visible = known && !report;
        _ledgerReportScroll.Visible = known && report;
        _ledgerLineA.Visible = _ledgerLineB.Visible = _ledgerBarsA.Visible = _ledgerBarsB.Visible = known;
        if (!known)
        {
            _ledgerTitle.Text = Simulation.Loc.Format("NATIONAL LEDGER · {0}", nation.DisplayName);
            _ledgerSummary.Text = Tr("Fog of war — foreign internal statistics are unavailable. Choose Player nation.");
            _ledgerPolicyLine.Text = "";
            return;
        }
        if (report)
        {
            _ledgerTitle.Text = Simulation.Loc.Format("NATIONAL LEDGER · {0}", nation.DisplayName);
            _ledgerPolicyLine.Text = "";
            foreach (var (tab, button) in _ledgerTabButtons) ApplyLedgerButtonState(button, tab == _ledgerTab);
            RefreshStrategicReport(nation);
            return;
        }
        var samples = _campaign.History.ForNation(nation.Id);
        var days = samples.Select(sample => sample.Day).ToArray();
        var first = samples.Count > 0 ? samples[0] : default;
        var gdpTrend = samples.Count > 1 && MathF.Abs(first.Gdp) > 0.001f ? nation.Gdp / first.Gdp - 1f : 0f;
        var populationTrend = samples.Count > 1 && first.PopulationMillions > 0.001f
            ? nation.Population / 1_000_000f / first.PopulationMillions - 1f : 0f;
        _ledgerTitle.Text = Simulation.Loc.Format("NATIONAL LEDGER · {0}", nation.DisplayName.ToUpperInvariant());
        _ledgerSummary.Text = Simulation.Loc.Format(
            "{0}  ·  GDP {1}  ·  population {2}  ·  market access {3:P0}  ·  " +
            "treasury {4}  ·  {5} weekly observation(s)",
            _ledgerTab.ToUpperInvariant(), SignedPercent(gdpTrend), SignedPercent(populationTrend),
            nation.MarketAccess, Fmt.Money(nation.Treasury), samples.Count);

        // Named conditions belong where the player already reads the country's state -
        // a modifier nobody can see is a number changing for no visible reason.
        var spirits = DescribeModifiers(nation);
        if (!string.IsNullOrEmpty(spirits)) _ledgerSummary.Text += "\n" + spirits;

        foreach (var (tab, button) in _ledgerTabButtons)
            ApplyLedgerButtonState(button, tab == _ledgerTab);

        switch (_ledgerTab)
        {
            case "Economy": ShowLedgerEconomy(nation, samples, days); break;
            case "Market": ShowLedgerMarket(nation, samples, days); break;
            case "Population": ShowLedgerPopulation(nation, samples, days); break;
            case "Military": ShowLedgerMilitary(nation, samples, days); break;
            case "Rankings": ShowLedgerRankings(nation, samples, days); break;
            default: ShowLedgerOverview(nation, samples, days); break;
        }
    }

    private void ShowLedgerOverview(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Economic power", "GDP and treasury, billions", days, new[]
        {
            Series("GDP", Gold, samples, sample => sample.Gdp),
            Series("Treasury", Green, samples, sample => sample.Treasury)
        }, StrategicChartFormat.MoneyBillions);
        // What the country is doing to itself, which used to be the whole of a separate
        // tab. The population curve this replaces has a tab of its own and was the only
        // thing on two pages at once.
        _ledgerLineB.SetData("Social development", "Policy effects accumulate at each weekly economic tick", days, new[]
        {
            Series("Living standard", Gold, samples, sample => sample.StandardOfLiving),
            Series("Literacy", Blue, samples, sample => sample.Literacy),
            Series("Stability", Green, samples, sample => sample.Stability)
        }, StrategicChartFormat.Percent);
        _ledgerBarsA.SetData("National pulse", PoliticsSubtitle(nation), new[]
        {
            new StrategicBarEntry("Stability", nation.Stability, Green),
            new StrategicBarEntry("Living standard", nation.StandardOfLiving, Gold),
            new StrategicBarEntry("Literacy", nation.Literacy, Blue),
            new StrategicBarEntry("Market access", nation.MarketAccess, Violet),
            new StrategicBarEntry("War support", CurrentWarSupport(nation), Red)
        }, StrategicChartFormat.Percent);
        _ledgerBarsB.SetData("Weekly public balance", "Every value affects the treasury", new[]
        {
            new StrategicBarEntry("Tax revenue", nation.WeeklyTaxIncome, Green),
            new StrategicBarEntry("Tariffs", nation.WeeklyTariffIncome, Blue),
            new StrategicBarEntry("Trade balance", nation.WeeklyTradeBalance, Violet),
            new StrategicBarEntry("Expenses", -nation.WeeklyExpenses, Red),
            new StrategicBarEntry("Net balance", nation.WeeklyBalance, nation.WeeklyBalance >= 0 ? Gold : Red)
        }, StrategicChartFormat.MoneyBillions);
    }

    private void ShowLedgerEconomy(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Gross domestic product", "Policy, construction, war and shortages alter this curve", days,
            new[] { Series("GDP", Gold, samples, sample => sample.Gdp) }, StrategicChartFormat.MoneyBillions);
        _ledgerLineB.SetData("Weekly budget", "Revenue, expenses and resulting balance", days, new[]
        {
            Series("Income", Green, samples, sample => sample.WeeklyIncome),
            Series("Expenses", Red, samples, sample => sample.WeeklyExpenses),
            Series("Balance", Blue, samples, sample => sample.WeeklyBalance)
        }, StrategicChartFormat.MoneyBillions);
        _ledgerBarsA.SetData("Fiscal controls",
            Simulation.Loc.Format("Tax {0:P0} · military {1:P0}", nation.TaxRate, nation.MilitarySpending), new[]
        {
            new StrategicBarEntry("Tax rate", nation.TaxRate, Gold, "Raises public revenue; excessive taxation weighs on private growth."),
            new StrategicBarEntry("Military share", nation.MilitarySpending, Red, "Improves reinforcement and fleet readiness but increases expenditure."),
            new StrategicBarEntry("Stability", nation.Stability, Green),
            new StrategicBarEntry("Productivity", nation.Productivity / 1.5f, Blue,
                Simulation.Loc.Format("Raw productivity {0:0.00}", nation.Productivity))
        }, StrategicChartFormat.Percent);
        var states = _campaign.StatesOf(nation);
        _ledgerBarsB.SetData("National building base", "Completed buildings by type", Enum.GetValues<BuildingKind>()
            .Select(kind => new StrategicBarEntry(BuildingCatalog.Get(kind).Name,
                states.Sum(state => state.Buildings.GetValueOrDefault(kind)), BuildingColor(kind))).ToArray(),
            StrategicChartFormat.Number);
    }

    private void ShowLedgerMarket(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerPolicyLine.Text = Simulation.Loc.Format("Current: {0} · tariffs {1:P0}", TradePolicyRules.Name(nation.TradePolicy), TradePolicyRules.TariffRate(nation.TradePolicy));
        _ledgerLineA.SetData("Market health", "Access rises when domestic output and imports cover demand", days, new[]
        {
            Series("Access", Green, samples, sample => sample.MarketAccess),
            Series("Shortage", Red, samples, sample => sample.ShortageIndex)
        }, StrategicChartFormat.Percent);
        _ledgerLineB.SetData("Commodity price index", "100 equals each good's scenario base price", days,
            CommodityCatalog.All.Select(commodity => Series(CommodityCatalog.Get(commodity).Name,
                CommodityColor(commodity), samples, sample => sample.PriceIndex(commodity))).ToArray(), StrategicChartFormat.Index);
        _ledgerBarsA.SetData("Demand satisfaction", "Shortages reduce growth and living standards", CommodityCatalog.All.Select(commodity =>
        {
            var quote = nation.Market[commodity];
            return new StrategicBarEntry(CommodityCatalog.Get(commodity).Name, quote.Satisfaction,
                quote.IsShortage ? Red : Green,
                Simulation.Loc.Format("Production {0:0.0} · demand {1:0.0}", quote.Production, quote.Demand));
        }).ToArray(), StrategicChartFormat.Percent);
        _ledgerBarsB.SetData("Net commodity flow", "Positive is surplus; negative requires imports", CommodityCatalog.All.Select(commodity =>
        {
            var quote = nation.Market[commodity];
            return new StrategicBarEntry(CommodityCatalog.Get(commodity).Name, quote.Net,
                quote.Net >= 0f ? Green : Red,
                Simulation.Loc.Format("Imports {0:0.0} · exports {1:0.0}", quote.Imports, quote.Exports));
        }).ToArray(), StrategicChartFormat.Number);
    }

    private void ShowLedgerPopulation(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Population", "Millions of inhabitants", days,
            new[] { Series("Population", Blue, samples, sample => sample.PopulationMillions) }, StrategicChartFormat.Millions);
        _ledgerLineB.SetData("Social development", "Living standards, literacy and stability", days, new[]
        {
            Series("Living standard", Gold, samples, sample => sample.StandardOfLiving),
            Series("Literacy", Blue, samples, sample => sample.Literacy),
            Series("Stability", Green, samples, sample => sample.Stability)
        }, StrategicChartFormat.Percent);
        var states = _campaign.StatesOf(nation);
        _ledgerBarsA.SetData("Social classes", "Everyone the country has, by how well off they are",
            Enum.GetValues<SocialClass>()
                .Select((socialClass, index) => new StrategicBarEntry(SocialClassName(socialClass),
                    states.Sum(state => state.PopulationGroups
                        .Where(group => group.Class == socialClass).Sum(group => group.Size)) / 1_000_000f,
                    new[] { Blue, Gold, Orange }[index % 3])).ToArray(), StrategicChartFormat.Millions);
        _ledgerBarsB.SetData("Largest states", "Population centres determine construction and social weight", states
            .OrderByDescending(state => state.Population).Take(7)
            .Select(state => new StrategicBarEntry(state.DisplayName, state.Population / 1_000_000f, Blue,
                Simulation.Loc.Format("Living standard {0:P0} · literacy {1:P0}",
                    state.StandardOfLiving, state.Literacy))).ToArray(),
            StrategicChartFormat.Millions);
    }

    private void ShowLedgerMilitary(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Operational power", "Divisions weighted by strength, organisation and supply", days,
            new[] { Series("Power", Red, samples, sample => sample.MilitaryPower) }, StrategicChartFormat.Number);
        _ledgerLineB.SetData("Manpower reserve", "Millions available for reinforcement", days,
            new[] { Series("Reserve", Gold, samples, sample => sample.ManpowerMillions) }, StrategicChartFormat.Millions);
        var armies = _campaign.Armies.Where(army => army.NationId == nation.Id).OrderByDescending(army => army.Power).Take(8).ToArray();
        _ledgerBarsA.SetData("Field formations", "Current combat power, not raw division count", armies.Select(army =>
            new StrategicBarEntry(army.Name, army.Power, army.CutOff ? Red : Green,
                Simulation.Loc.Format("{0:0.0} divisions · organisation {1:P0} · supply {2:P0}",
                    army.Divisions, army.Organisation, army.SupplyLevel))).ToArray(),
            StrategicChartFormat.Number);
        var wars = _campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id)).ToArray();
        _ledgerBarsB.SetData("Active wars", "Signed score is shown from this country's perspective", wars.Select(war =>
        {
            var score = war.AttackerId == nation.Id ? war.WarScore : -war.WarScore;
            return new StrategicBarEntry(WarNames.Of(_campaign, war), score, score >= 0f ? Green : Red,
                Simulation.Loc.Format("Support {0:P0}",
                    war.AttackerId == nation.Id ? war.AttackerWarSupport : war.DefenderWarSupport));
        }).ToArray(), StrategicChartFormat.Number);
    }

    private void ShowLedgerRankings(Nation nation, IReadOnlyList<NationHistorySample> samples, IReadOnlyList<int> days)
    {
        _ledgerLineA.SetData("Selected-country GDP", "Use Map selection to compare another country", days,
            new[] { Series("GDP", Gold, samples, sample => sample.Gdp) }, StrategicChartFormat.MoneyBillions);
        _ledgerLineB.SetData("Selected-country population", "Historical trend for the viewed country", days,
            new[] { Series("Population", Blue, samples, sample => sample.PopulationMillions) }, StrategicChartFormat.Millions);
        _ledgerBarsA.SetData("Largest economies", "Current world GDP ranking", _campaign.Nations
            .Where(candidate => CanInspectNation(candidate.Id))
            .OrderByDescending(candidate => candidate.Gdp).Take(8)
            .Select(candidate => new StrategicBarEntry(candidate.Tag, candidate.Gdp,
                candidate.Id == nation.Id ? Gold : Blue, candidate.Name)).ToArray(), StrategicChartFormat.MoneyBillions);
        _ledgerBarsB.SetData("Largest populations", "Current world population ranking", _campaign.Nations
            .Where(candidate => CanInspectNation(candidate.Id))
            .OrderByDescending(candidate => candidate.Population).Take(8)
            .Select(candidate => new StrategicBarEntry(candidate.Tag, candidate.Population / 1_000_000f,
                candidate.Id == nation.Id ? Gold : Violet, candidate.Name)).ToArray(), StrategicChartFormat.Millions);
    }

    private static StrategicChartSeries Series(string name, Color color, IReadOnlyList<NationHistorySample> samples,
        Func<NationHistorySample, float> selector) => new(name, color, samples.Select(selector).ToArray());

    private float CurrentWarSupport(Nation nation)
    {
        var supports = _campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id))
            .Select(war => war.AttackerId == nation.Id ? war.AttackerWarSupport : war.DefenderWarSupport).ToArray();
        return supports.Length == 0 ? nation.WarSupport : supports.Min();
    }

    private static string SignedPercent(float value) => value >= 0f ? $"+{value:P1}" : $"{value:P1}";
    private static string SocialClassName(SocialClass socialClass) => socialClass switch
    {
        SocialClass.Lower => "Lower class",
        SocialClass.Middle => "Middle class",
        _ => "Upper class"
    };

    private static Color CommodityColor(Commodity commodity) => commodity switch
    {
        Commodity.Food => Green,
        Commodity.ConsumerGoods => Violet,
        Commodity.IndustrialMaterials => Ui.Colour("goods.industrial_materials"),
        Commodity.Energy => Orange,
        Commodity.Arms => Red,
        Commodity.Coal => Ui.Colour("goods.coal"),
        Commodity.Oil => Ui.Colour("goods.oil"),
        Commodity.IronOre => Ui.Colour("goods.iron_ore"),
        Commodity.RareEarths => Ui.Colour("goods.rare_earths"),
        Commodity.Machinery => Gold,
        _ => Blue
    };

    private static Color BuildingColor(BuildingKind kind) => kind switch
    {
        BuildingKind.CivilianIndustry => Blue,
        BuildingKind.Infrastructure => Ui.Colour("building.infrastructure"),
        BuildingKind.University => Violet,
        BuildingKind.Hospital => Green,
        BuildingKind.Agriculture => Orange,
        BuildingKind.Barracks => Red,
        BuildingKind.ExtractionIndustry => Ui.Colour("building.extraction"),
        BuildingKind.PowerPlant => Ui.Colour("building.power_plant"),
        BuildingKind.ArmsIndustry => Ui.Colour("building.arms_industry"),
        _ => Gold
    };

    internal static void ApplyLedgerButtonState(Button button, bool selected) => StyleButton(button, selected);

    /// <summary>
    /// Who governs and what they are for, under the bars that show what it has amounted to.
    ///
    /// The Overview is the page somebody opens to find out what a country *is*, and until
    /// the form of government existed it could answer that only in money and percentages.
    /// </summary>
    private string PoliticsSubtitle(Nation nation) => Simulation.Loc.Format("{0} · {1} · {2}",
        Tr(PoliticsCore.GovernmentOf(nation).Name),
        Tr(PoliticsCore.IdeologyOf(nation).Name),
        Tr(GovernmentFocusRules.Name(nation.GovernmentFocus)));
}
