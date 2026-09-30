using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The trade screen: the country's whole week of trade, at a glance.
///
/// Trade policy used to sit at the bottom of State Affairs and the instructions under it
/// in the same column as twelve tax sliders, which made the country's dealings with the
/// world a sub-paragraph of its dealings with itself. And nothing anywhere answered the
/// questions anybody opens a trade screen with: what are we short of, what do we make of
/// it, who is it crossing the border with, and what is any of it worth.
///
/// So this screen is the market the way Hearts of Iron shows it, which is the shape every
/// player of these games already knows:
///
///   - a MATRIX of the goods: made, wanted, bought in, sold, net, price, and how well
///     supplied the country actually is, one row per good, deficits lit;
///   - the PARTNERS: who the trade actually crosses the border with, what it is worth,
///     and which way it flows - the same named routes an embargo cuts;
///   - the POLICY, and the instructions that were the old screen, moved here whole: they
///     are the controls, and the matrix and the partners are what the controls are for.
/// </summary>
public partial class DealOrForceGame
{
    private Control _tradeScreenLayer = null!;
    private Label _tradeScreenSummary = null!;
    private Label _tradePolicyFeedback = null!;
    private VBoxContainer _tradeGoodRows = null!;
    private VBoxContainer _tradePartnerRows = null!;
    private Label _tradePartnersShown = null!;
    private readonly Dictionary<TradePolicy, Button> _tradePolicyButtons = new();

    /// <summary>One good's row in the matrix, kept open so the week can move under it.</summary>
    private sealed class TradeScreenRow
    {
        public Button Select;
        public TextureRect Icon;
        public Label Name;
        public Label Made;
        public Label Wanted;
        public Label Bought;
        public Label Sold;
        public Label Net;
        public Label Price;
        public ColorRect SupplyFill;
        public Label Supply;
        public Commodity Good;
    }

    /// <summary>One partner's row, and which way the trade flows through them.</summary>
    private sealed class TradePartnerRow
    {
        public Button Select;
        public Control Flag;
        public Label Name;
        public Label Value;
        public Label Flow;
        public Nation Nation;
    }

    private readonly List<TradeScreenRow> _tradeScreenRows = new();
    private readonly List<TradePartnerRow> _tradePartnerRowList = new();
    private readonly List<Commodity> _tradeScreenOrder = new();
    private bool _tradeRowsBuilt;

    private void BuildTradeScreen()
    {
        _tradeScreenLayer = FullLayer("Trade");
        _tradeScreenLayer.Visible = false;
        _hudLayer.AddChild(_tradeScreenLayer);

        var shade = new ColorRect
        {
            Color = new Color(0.015f, 0.026f, 0.041f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _tradeScreenLayer.AddChild(shade);

        var panel = TexturedPanel(_tradeScreenLayer);
        Ui.Place(panel, "modal.trade");

        var rows = PanelRows(panel, 22, 16, 9);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        var title = Title("TRADE", 25, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(ActionButton("×", CloseTradeScreen, 38, 32));

        _tradeScreenSummary = Title("", 13, Ui.Colour("text.body_soft"));
        _tradeScreenSummary.CustomMinimumSize = new Vector2(0, 22);
        rows.AddChild(_tradeScreenSummary);

        // ------------------------------------------------------------------ the policy
        // The same three stances, moved here from State Affairs: they are trade, and a
        // player looking for them should not have to open the tax screen to find them.
        var policyRow = new HBoxContainer();
        policyRow.AddThemeConstantOverride("separation", 7);
        rows.AddChild(policyRow);
        var policyCaption = Title("TRADE POLICY", 12, Ui.Colour("text.caption"));
        policyCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        policyCaption.CustomMinimumSize = new Vector2(98, 0);
        policyCaption.VerticalAlignment = VerticalAlignment.Center;
        policyRow.AddChild(policyCaption);
        foreach (var policy in Simulation.TradePolicyCatalog.All)
        {
            var captured = policy;
            var button = ActionButton(TradePolicyRules.Name(captured), () => SetStateTradePolicy(captured), 114, 30,
                captured == TradePolicy.BalancedTrade);
            button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            button.TooltipText = Ui.Tip(TradePolicyRules.Description(captured));
            policyRow.AddChild(button);
            _tradePolicyButtons[captured] = button;
        }
        _tradePolicyFeedback = Title("", 11, Ui.Colour("text.accent_dim"));
        _tradePolicyFeedback.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _tradePolicyFeedback.VerticalAlignment = VerticalAlignment.Center;
        policyRow.AddChild(_tradePolicyFeedback);

        // ------------------------------------------------------------------ the body
        var body = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        body.AddThemeConstantOverride("separation", 14);
        rows.AddChild(body);

        // ---- the matrix and the partners, stacked on the left.
        var market = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        market.AddThemeConstantOverride("separation", 6);
        body.AddChild(market);

        StateSection(market, "THE GOODS",
            "One row per good, the week that has just closed. A deficit is what the country is " +
            "short of; the price is what the world market paid; the supply bar is how much of the " +
            "want the country actually met, imports included. Click a row to open its instructions.");

        var captions = new HBoxContainer();
        captions.AddThemeConstantOverride("separation", 8);
        market.AddChild(captions);
        Label Caption(string text, float width, HorizontalAlignment alignment = HorizontalAlignment.Left)
        {
            var label = RowLabel(text, 11, Ui.Colour("text.caption"), width);
            label.HorizontalAlignment = alignment;
            return label;
        }
        captions.AddChild(Caption(Tr("GOOD"), 190));
        captions.AddChild(Caption(Tr("MADE"), 74, HorizontalAlignment.Right));
        captions.AddChild(Caption(Tr("WANTED"), 74, HorizontalAlignment.Right));
        captions.AddChild(Caption(Tr("BOUGHT IN"), 76, HorizontalAlignment.Right));
        captions.AddChild(Caption(Tr("SOLD"), 70, HorizontalAlignment.Right));
        captions.AddChild(Caption(Tr("NET"), 74, HorizontalAlignment.Right));
        captions.AddChild(Caption(Tr("PRICE"), 78, HorizontalAlignment.Right));
        captions.AddChild(Caption(Tr("SUPPLY"), 0));
        (captions.GetChild(captions.GetChildCount() - 1) as Control).SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var matrixScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        market.AddChild(matrixScroll);
        _tradeGoodRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _tradeGoodRows.AddThemeConstantOverride("separation", 2);
        matrixScroll.AddChild(_tradeGoodRows);

        // ---- the partners, under the matrix: the trade the goods travel in.
        var partners = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 230)
        };
        partners.AddThemeConstantOverride("separation", 5);
        market.AddChild(partners);
        StateSection(partners, "THE PARTNERS",
            "Who the week's trade actually crossed the border with, and what it was worth. These " +
            "are the same named routes an embargo cuts and a blockade interrupts.");
        var partnerCaptions = new HBoxContainer();
        partnerCaptions.AddThemeConstantOverride("separation", 8);
        partners.AddChild(partnerCaptions);
        var whoCaption = RowLabel(Tr("COUNTRY"), 11, Ui.Colour("text.caption"), 230);
        partnerCaptions.AddChild(whoCaption);
        var boughtCaption = RowLabel(Tr("SOLD TO THEM"), 11, Ui.Colour("text.caption"), 120);
        boughtCaption.HorizontalAlignment = HorizontalAlignment.Right;
        partnerCaptions.AddChild(boughtCaption);
        var soldCaption = RowLabel(Tr("BOUGHT FROM THEM"), 11, Ui.Colour("text.caption"), 140);
        soldCaption.HorizontalAlignment = HorizontalAlignment.Right;
        partnerCaptions.AddChild(soldCaption);
        _tradePartnersShown = RowLabel("", 11, Ui.Colour("text.caption"), 150);
        _tradePartnersShown.HorizontalAlignment = HorizontalAlignment.Right;
        _tradePartnersShown.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        partnerCaptions.AddChild(_tradePartnersShown);
        var partnerScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        partners.AddChild(partnerScroll);
        _tradePartnerRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _tradePartnerRows.AddThemeConstantOverride("separation", 2);
        partnerScroll.AddChild(_tradePartnerRows);

        // ---- the instructions, whole, on the right: they are the controls this screen reads.
        var instructionsScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(560, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        body.AddChild(instructionsScroll);
        var instructionRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        instructionRows.AddThemeConstantOverride("separation", 9);
        instructionsScroll.AddChild(instructionRows);
        BuildTradeInstructions(instructionRows);
    }

    private void OpenTradeScreen()
    {
        _openScreen = "Trade";
        _sidePanel.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_warLedgerLayer is not null) _warLedgerLayer.Visible = false;
        if (_stateAffairsLayer is not null) _stateAffairsLayer.Visible = false;
        if (_productionLayer is not null) _productionLayer.Visible = false;
        if (_diplomacyLayer is not null) _diplomacyLayer.Visible = false;
        _tradeScreenLayer.Visible = true;
        _tradeScreenLayer.MoveToFront();
        RefreshTradeScreen();
        UpdateScreenButtons();
    }

    private void CloseTradeScreen()
    {
        _tradeScreenLayer.Visible = false;
        if (_openScreen == "Trade") _openScreen = "";
        UpdateScreenButtons();
    }

    private void RefreshTradeScreen()
    {
        if (_tradeScreenLayer is null || !_tradeScreenLayer.Visible || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var settled = _campaign.ElapsedDays >= _campaign.Defines.EconomyTickDays;

        _tradeScreenSummary.Text = Simulation.Loc.Format("{0} · {1}", player.DisplayName,
            Tr(TradePolicyRules.Name(player.TradePolicy))) +
            (settled
                ? " · " + Simulation.Loc.Format("bought in {0} · sold {1} · balance {2} · market access {3:P0}",
                    Fmt.Money(player.ImportValue), Fmt.Money(player.ExportValue),
                    player.WeeklyTradeBalance >= 0 ? "+" + Fmt.Money(player.WeeklyTradeBalance) : "-" + Fmt.Money(-player.WeeklyTradeBalance),
                    player.MarketAccess)
                : " · " + Tr("The first economic week has not closed yet."));

        foreach (var (policy, button) in _tradePolicyButtons)
            ApplyLedgerButtonState(button, policy == player.TradePolicy);

        RefreshTradeMatrix(settled);
        RefreshTradePartners();
        RefreshTradeInstructions();
    }

    // ------------------------------------------------------------------ the matrix

    private void RefreshTradeMatrix(bool settled)
    {
        if (_campaign?.Player is null) return;
        var player = _campaign.Player;

        // The rows are built once and repainted for the rest of the campaign: the order is
        // the catalogue's own, and a list torn down every week is a list whose tooltip a
        // player can never finish reading.
        if (!_tradeRowsBuilt)
        {
            _tradeRowsBuilt = true;
            _tradeScreenOrder.Clear();
            foreach (var good in CommodityCatalog.All)
                _tradeScreenOrder.Add(good);
            foreach (var good in _tradeScreenOrder)
            {
                var row = BuildTradeMatrixRow(good);
                _tradeScreenRows.Add(row);
                _tradeGoodRows.AddChild(row.Select);
            }
        }

        foreach (var row in _tradeScreenRows)
        {
            if (!player.Market.TryGetValue(row.Good, out var quote)) continue;
            var definition = CommodityCatalog.Get(row.Good);
            row.Name.Text = Simulation.Loc.Get(definition.Name);
            row.Made.Text = quote.Production.ToString("0.0");
            row.Wanted.Text = quote.Demand.ToString("0.0");
            row.Bought.Text = quote.Imports.ToString("0.0");
            row.Sold.Text = quote.Exports.ToString("0.0");
            row.Net.Text = (quote.Net >= 0 ? "+" : "") + quote.Net.ToString("0.0");
            row.Net.AddThemeColorOverride("font_color",
                quote.Net >= 0 ? Ui.Colour("state.positive") : Ui.Colour("state.negative"));
            row.Price.Text = settled ? Fmt.Money(quote.Price) : "—";
            var supplied = quote.Demand <= 0.001f ? 1f : quote.Satisfaction;
            row.SupplyFill.AnchorRight = supplied;
            row.SupplyFill.Color = Ui.Colour(
                supplied > 0.985f ? "state.positive" :
                supplied > 0.8f ? "state.warning" : "state.negative");
            row.Supply.Text = supplied.ToString("P0");
            row.Select.TooltipText = Ui.Tip(Simulation.Loc.Get(definition.Description));
            row.Select.Modulate = supplied < 0.985f
                ? new Color(1.25f, 0.85f, 0.85f)
                : Colors.White;
        }
    }

    private TradeScreenRow BuildTradeMatrixRow(Commodity good)
    {
        var row = new TradeScreenRow { Good = good };
        // A Button is not a container: a child laid out inside it goes by its anchors, and
        // a child with no anchors goes nowhere - it sits at the top-left corner at its own
        // minimum size, so the row reads as a one-pixel smear. Anchor the inner box to the
        // full rectangle, inset by the row style's own padding, and give the button the
        // height the row needs, which a plain Button does not take from its children.
        row.Select = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 28) };
        row.Select.AddThemeStyleboxOverride("normal", ShellRowStyle(false));
        row.Select.AddThemeStyleboxOverride("hover", ShellRowStyle(true));
        row.Select.AddThemeStyleboxOverride("pressed", ShellRowStyle(true));
        row.Select.AddThemeStyleboxOverride("focus", ShellRowStyle(false));

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 8);
        inner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        inner.OffsetLeft = 9;
        inner.OffsetRight = -9;
        inner.OffsetTop = 3;
        inner.OffsetBottom = -3;
        row.Select.AddChild(inner);

        row.Icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = Ui.Colour("text.accent_dim")
        };
        row.Icon.Texture = Art("icon_" + GoodIcon(good),
            $"res://game/gfx/interface/icons/icon_{GoodIcon(good)}.png");
        inner.AddChild(row.Icon);

        Label Figure(float width)
        {
            var label = Title("", 12, Ui.Colour("text.body_soft"));
            Fixed(label, width);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            return label;
        }

        row.Name = Title("", 13, Ui.Colour("text.body"));
        Fixed(row.Name, 174);
        inner.AddChild(row.Name);
        row.Made = Figure(74);
        inner.AddChild(row.Made);
        row.Wanted = Figure(74);
        inner.AddChild(row.Wanted);
        row.Bought = Figure(76);
        inner.AddChild(row.Bought);
        row.Sold = Figure(70);
        inner.AddChild(row.Sold);
        row.Net = Figure(74);
        inner.AddChild(row.Net);
        row.Price = Figure(78);
        inner.AddChild(row.Price);

        var supplyBox = new Control
        {
            CustomMinimumSize = new Vector2(90, 10),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        var back = new ColorRect { Color = Ui.Colour("surface.inset"), MouseFilter = MouseFilterEnum.Ignore };
        back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        supplyBox.AddChild(back);
        row.SupplyFill = new ColorRect
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Color = Ui.Colour("state.positive")
        };
        row.SupplyFill.SetAnchorsPreset(LayoutPreset.LeftWide);
        supplyBox.AddChild(row.SupplyFill);
        inner.AddChild(supplyBox);
        row.Supply = Figure(44);
        inner.AddChild(row.Supply);

        // Clicking a row selects the same good in the instructions card beside it, so a
        // shortage seen here becomes an instruction written there without a dropdown.
        row.Select.Pressed += () =>
        {
            for (var index = 0; index < _tradeGoodPicker.ItemCount; index++)
                if (_tradeGoodPicker.GetItemText(index) == Simulation.Loc.Get(CommodityCatalog.Get(row.Good).Name))
                {
                    _tradeGoodPicker.Selected = index;
                    break;
                }
            RefreshTradeInstructions();
        };
        return row;
    }

    // ------------------------------------------------------------------ the partners

    private void RefreshTradePartners()
    {
        if (_campaign?.Player is null) return;
        var player = _campaign.Player;

        // What flowed each way with each partner, from this week's named routes. Routes
        // are not saved - they are a consequence of the week, worked out again here.
        var soldTo = new Dictionary<int, float>();
        var boughtFrom = new Dictionary<int, float>();
        var byGood = new Dictionary<int, List<TradeRoute>>();
        foreach (var route in _campaign.TradeRoutes)
        {
            if (route.SellerId == player.Id)
            {
                soldTo[route.BuyerId] = soldTo.GetValueOrDefault(route.BuyerId) + route.Value;
                AddPartnerRoute(byGood, route.BuyerId, route);
            }
            else if (route.BuyerId == player.Id)
            {
                boughtFrom[route.SellerId] = boughtFrom.GetValueOrDefault(route.SellerId) + route.Value;
                AddPartnerRoute(byGood, route.SellerId, route);
            }
        }

        var partnerIds = soldTo.Keys.Union(boughtFrom.Keys)
            .OrderByDescending(id => soldTo.GetValueOrDefault(id) + boughtFrom.GetValueOrDefault(id))
            .Take(10)
            .ToArray();
        // Named routes are a consequence of the week - they are not saved, so a campaign
        // straight out of a save has none of them until the market next closes. The totals
        // each partner is worth ARE saved, so rather than an empty page the list falls
        // back on those, marked as what they are.
        var fromTotals = partnerIds.Length == 0;
        if (fromTotals)
            partnerIds = player.TradePartners
                .Where(entry => entry.Value > 0f)
                .OrderByDescending(entry => entry.Value)
                .Select(entry => entry.Key).Take(10).ToArray();

        while (_tradePartnerRowList.Count > partnerIds.Length)
        {
            var spare = _tradePartnerRowList[^1];
            _tradePartnerRowList.RemoveAt(_tradePartnerRowList.Count - 1);
            if (GodotObject.IsInstanceValid(spare.Select)) spare.Select.QueueFree();
        }
        for (var index = 0; index < partnerIds.Length; index++)
        {
            if (index >= _tradePartnerRowList.Count) _tradePartnerRowList.Add(BuildTradePartnerRow());
            var row = _tradePartnerRowList[index];
            var partner = _campaign.Nations.Find(candidate => candidate.Id == partnerIds[index]);
            if (partner is null) continue;
            row.Nation = partner;
            SetFlag(row.Flag, partner.Tag, 16);
            row.Name.Text = partner.DisplayName;
            var sold = soldTo.GetValueOrDefault(partner.Id);
            var bought = boughtFrom.GetValueOrDefault(partner.Id);
            if (fromTotals)
            {
                var saved = player.TradePartners.GetValueOrDefault(partner.Id);
                row.Value.Text = Fmt.Money(saved);
                row.Value.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
                row.Flow.Text = Tr("totals of the last closed week");
            }
            else
            {
                row.Value.Text = sold >= bought
                    ? Simulation.Loc.Format("+{0}", Fmt.Money(sold))
                    : "-" + Fmt.Money(bought);
                row.Value.AddThemeColorOverride("font_color",
                    sold >= bought ? Ui.Colour("state.positive") : Ui.Colour("state.negative"));
                row.Flow.Text = Simulation.Loc.Format("{0} · {1}", Fmt.Money(sold), Fmt.Money(bought));
            }
            var routes = byGood.GetValueOrDefault(partner.Id);
            row.Select.TooltipText = Ui.Tip(routes is null || routes.Count == 0
                ? partner.DisplayName
                : partner.DisplayName + "\n" + string.Join("\n", routes
                    .GroupBy(route => route.Commodity)
                    .Select(group => (Commodity: group.Key, Value: group.Sum(route => route.Value)))
                    .OrderByDescending(entry => entry.Value)
                    .Take(4)
                    .Select(entry => Simulation.Loc.Format("{0} · {1}", Simulation.Loc.Get(CommodityCatalog.Get(entry.Commodity).Name), Fmt.Money(entry.Value)))));
        }

        var total = player.TradePartners.Count;
        _tradePartnersShown.Text = Simulation.Loc.Format("{0:N0} of {1:N0} partners shown", (float)partnerIds.Length, (float)total);
    }

    private static void AddPartnerRoute(Dictionary<int, List<TradeRoute>> table, int partnerId, TradeRoute route)
    {
        if (!table.TryGetValue(partnerId, out var routes))
            table[partnerId] = routes = new List<TradeRoute>();
        routes.Add(route);
    }

    private TradePartnerRow BuildTradePartnerRow()
    {
        var row = new TradePartnerRow();
        // A Button is not a container: a child laid out inside it goes by its anchors, and
        // a child with no anchors goes nowhere - it sits at the top-left corner at its own
        // minimum size, so the row reads as a one-pixel smear. Anchor the inner box to the
        // full rectangle, inset by the row style's own padding, and give the button the
        // height the row needs, which a plain Button does not take from its children.
        row.Select = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 26) };
        row.Select.AddThemeStyleboxOverride("normal", ShellRowStyle(true));
        row.Select.AddThemeStyleboxOverride("hover", ShellRowStyle(false));
        row.Select.AddThemeStyleboxOverride("pressed", ShellRowStyle(false));
        row.Select.AddThemeStyleboxOverride("focus", ShellRowStyle(true));

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 8);
        inner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        inner.OffsetLeft = 9;
        inner.OffsetRight = -9;
        inner.OffsetTop = 3;
        inner.OffsetBottom = -3;
        row.Select.AddChild(inner);

        row.Flag = FlagBadge("", 16);
        inner.AddChild(row.Flag);
        row.Name = Title("", 12, Ui.Colour("text.body"));
        Fixed(row.Name, 0);
        row.Name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.Name.ClipText = true;
        row.Name.AutowrapMode = TextServer.AutowrapMode.Off;
        inner.AddChild(row.Name);
        row.Value = Title("", 12, Ui.Colour("text.bright"));
        Fixed(row.Value, 100);
        row.Value.HorizontalAlignment = HorizontalAlignment.Right;
        inner.AddChild(row.Value);
        row.Flow = Title("", 11, Ui.Colour("text.caption"));
        Fixed(row.Flow, 0);
        row.Flow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.Flow.HorizontalAlignment = HorizontalAlignment.Right;
        row.Flow.ClipText = true;
        row.Flow.AutowrapMode = TextServer.AutowrapMode.Off;
        inner.AddChild(row.Flow);

        // Attached once, at construction: the list repaints its figures on every refresh,
        // and a handler re-attached per repaint is a handler that fires twice, then three
        // times, for one click.
        row.Select.Pressed += () =>
        {
            if (row.Nation is null) return;
            _globe.SetMapModeSubject(row.Nation);
            _globe.FocusOn(row.Nation.CapitalLatitude, row.Nation.CapitalLongitude, 6.8f);
        };
        _tradePartnerRows.AddChild(row.Select);
        return row;
    }
}
