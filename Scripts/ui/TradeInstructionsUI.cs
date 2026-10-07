using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The controls a country has over its own trade.
///
/// This screen used to be seven identical grey sliders in one column - EXPORT RESERVE 0 %,
/// IMPORT CEILING 100 %, PRICE FLOOR 0.4x - with two dropdowns above them and a count of
/// instructions at the bottom. Every figure on it was true and none of them was usable. A
/// player could not tell which of the seven were doing anything, could not tell where the
/// good's rules ended and the country's began, and could not tell what "0.4x" would do to
/// their week. Four instructions were in force and the screen's only way of saying so was
/// the number four.
///
/// Rebuilt around three ideas, all of which are ordinary genre practice and none of which
/// cost a single extra pixel of height:
///
///   - TWO CARDS, not one list. What this country has decided about a GOOD and what it has
///     decided about a COUNTRY are different decisions, so they are different panels, each
///     with its own selector, its own mark, and its own count of what is in force in it.
///   - A ROW SAYS WHAT IT IS DOING. Under each control is the sentence the catalogue
///     declares for the value it is currently at - "we will not sell below two fifths of
///     what it is worth" - so the decision is readable without arithmetic.
///   - A ROW SAYS WHETHER IT IS DOING IT. A control sitting at its declared default is
///     dimmed and marked with a hollow dot; one that has been moved is bright, marked with a
///     filled one, and carries the only reset button on its row. "Four instructions in
///     force" is now four bright rows, which is a thing you can see rather than count.
///
/// Everything drawn here is still declared in `common/trade` - the bounds, the step, the
/// names and now the sentences - so a mod that adds a control gets all of this for free.
/// </summary>
public partial class DealOrForceGame
{
    /// <summary>One control: its slider or switch, its reading, and the sentence under it.</summary>
    private sealed class TradeControl
    {
        public VBoxContainer Row;
        public Label Dot;
        public Label Caption;
        public Label Value;
        public HSlider Slider;
        public CheckButton Switch;
        public Label Effect;
        public Button Reset;
    }

    private VBoxContainer _tradeInstructionRows;
    private OptionButton _tradeGoodPicker;
    private OptionButton _tradePartnerPicker;
    private TextureRect _tradeGoodMark;
    private Control _tradePartnerFlag;
    private CheckButton _tradePartnerThisGoodOnly;
    private Label _tradeGoodCount;
    private Label _tradePartnerCount;
    private Label _tradeGoodBalance;
    private ColorRect _tradeGoodMeterFill;
    private Control _tradeGoodMeter;
    private Label _tradeGoodFlow;
    private Label _tradeInstructionSummary;
    private VBoxContainer _tradePartnerControls;
    private CheckButton _tradeDelegated;
    private Control _tradeReviewCard;
    private VBoxContainer _tradeReviewRows;
    private Label _tradeReviewEmpty;
    private readonly List<Commodity> _tradeGoodOrder = new();
    private readonly List<string> _tradePartnerOrder = new();
    private readonly Dictionary<string, TradeControl> _tradeControls = new(StringComparer.Ordinal);
    private bool _updatingTradeInstructions;

    private void BuildTradeInstructions(VBoxContainer rows)
    {
        StateSection(rows, "TRADE INSTRUCTIONS",
            "What this country has told its traders to do - first about a good, then about another " +
            "country. A row that has been moved is lit and in force; a row still at its default is " +
            "dimmed and doing nothing. A change takes effect at the next weekly clearing.");

        _tradeInstructionRows = new VBoxContainer();
        _tradeInstructionRows.AddThemeConstantOverride("separation", 10);
        rows.AddChild(_tradeInstructionRows);

        BuildTradeGoodCard(_tradeInstructionRows);
        BuildTradePartnerCard(_tradeInstructionRows);

        BuildTradeReviewCard(_tradeInstructionRows);

        // ---- who is running this desk ----
        //
        // Placed under the cards rather than above them, because it is the answer to a
        // question a player only has once they have seen how many knobs there are.
        _tradeDelegated = new CheckButton { Text = Tr("Let the trade desk run itself") };
        _tradeDelegated.TooltipText = Ui.Tip(Tr(
            "Hand the trade desk to your own civil service. Once a month it buys what the country " +
            "is short of first, holds back what it cannot spare, sells a real surplus, and drops " +
            "the instruction again when a good stops being a problem. Everything it does shows up " +
            "in the list above beside your own decisions, and you can take the desk back at any " +
            "time - the instructions it left stay until you change them."));
        _tradeDelegated.Toggled += OnTradeDelegationToggled;
        _tradeInstructionRows.AddChild(_tradeDelegated);

        // ---- and the way out ----
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 9);
        _tradeInstructionSummary = Title("", 11, Ui.Colour("text.accent_dim"));
        _tradeInstructionSummary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _tradeInstructionSummary.VerticalAlignment = VerticalAlignment.Center;
        _tradeInstructionSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        footer.AddChild(_tradeInstructionSummary);
        var clear = ActionButton("Clear every instruction", ClearTradeInstructions, 190, 30);
        clear.TooltipText = Ui.Tip(Tr("Forget everything this country has decided about its trade and " +
                                      "let every good be traded on its merits again."));
        footer.AddChild(clear);
        _tradeInstructionRows.AddChild(footer);
    }

    // ------------------------------------------------------------ everything in force ---

    /// <summary>
    /// Every instruction this country has given, in one list.
    ///
    /// The two cards above are for DECIDING - one good, one country, with room to explain
    /// what each control does. This is for REVIEWING, which is a different question and was
    /// unanswerable: a player who had set fifteen things over a decade could only find them
    /// by clicking through twenty goods and two hundred and sixty countries.
    ///
    /// It matters more now that the desk can run itself. A delegate whose work you cannot
    /// see is a delegate nobody switches on, so what the civil service decided is listed
    /// here in the same place and the same shape as what you decided.
    /// </summary>
    private void BuildTradeReviewCard(VBoxContainer parent)
    {
        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", TradeCardStyle());
        parent.AddChild(frame);
        _tradeReviewCard = frame;
        var margin = Margin(frame, 10, 8);
        var card = new VBoxContainer();
        card.AddThemeConstantOverride("separation", 5);
        margin.AddChild(card);

        var heading = new HBoxContainer();
        heading.AddThemeConstantOverride("separation", 8);
        card.AddChild(heading);
        var caption = Fixed(Title("EVERYTHING IN FORCE", 12, Ui.Colour("text.caption"), upper: true));
        caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        heading.AddChild(caption);

        _tradeReviewRows = new VBoxContainer();
        _tradeReviewRows.AddThemeConstantOverride("separation", 3);
        card.AddChild(_tradeReviewRows);

        _tradeReviewEmpty = Title("", 11, Ui.Colour("text.hint"));
        _tradeReviewEmpty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        card.AddChild(_tradeReviewEmpty);
    }

    /// <summary>Re-list what is in force. Cheap: a country has tens of these, not thousands.</summary>
    private void RefreshTradeReview(Nation nation)
    {
        if (_tradeReviewRows is null) return;
        foreach (var child in _tradeReviewRows.GetChildren()) child.QueueFree();

        var listed = 0;
        foreach (var (key, value) in nation.TradeSettings.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var setting = TradeDesk.SettingOf(key);
            var definition = setting.Length == 0 ? null : TradeDesk.Of(setting);
            if (definition is null) continue;

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            _tradeReviewRows.AddChild(row);

            // The sentence a row's own control writes under itself - "Our claim weighs 2.25x
            // when the world is short of it." - is the wrong shape for a list. It is written
            // in data, a mod can make it any length at all, and a list column is a column:
            // set it right-aligned in a fixed width and a long sentence is clipped from its
            // own left edge, which reads as a caption that begins mid-word. So the column
            // carries the figure, the way every other value column in the game does, and the
            // sentence is one hover away on either half of the row.
            var said = TradeDesk.Reading(definition, value, TradeValueText(definition, value));

            var what = Fixed(Title(TradeDesk.Describe(nation, key), 11, Ui.Colour("text.body_soft")));
            what.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            what.MouseFilter = MouseFilterEnum.Stop;
            what.TooltipText = Ui.Tip(said);
            row.AddChild(what);

            var reads = Fixed(Title(TradeValueText(definition, value), 11,
                Ui.Colour("text.bright")), 120);
            reads.HorizontalAlignment = HorizontalAlignment.Right;
            reads.MouseFilter = MouseFilterEnum.Stop;
            reads.TooltipText = Ui.Tip(said);
            row.AddChild(reads);

            var captured = key;
            var drop = ActionButton("×", () =>
            {
                nation.TradeSettings.Remove(captured);
                RefreshTradeInstructions();
            }, 24, 20);
            drop.TooltipText = Ui.Tip(Tr("Withdraw this instruction."));
            row.AddChild(drop);
            listed++;
        }

        _tradeReviewEmpty.Text = listed > 0
            ? ""
            : Tr("Nothing is in force. Every good and every country is traded on its merits.");
        _tradeReviewEmpty.Visible = listed == 0;
    }

    private void OnTradeDelegationToggled(bool delegated)
    {
        if (_updatingTradeInstructions || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetDelegationCommand
        {
            IssuerId = _campaign.Player.Id,
            Portfolio = Delegation.Trade,
            Delegated = delegated
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshTradeInstructions();
    }

    // ---------------------------------------------------------------- what about a good ---

    private void BuildTradeGoodCard(VBoxContainer parent)
    {
        var card = TradeCard(parent, "ABOUT ONE GOOD", out var heading, out _tradeGoodCount);

        _tradeGoodMark = Symbol("good_food", 20, "icon.commodity");
        if (_tradeGoodMark is not null) heading.AddChild(_tradeGoodMark);
        _tradeGoodPicker = new OptionButton { CustomMinimumSize = new Vector2(190, 32),
            FitToLongestItem = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _tradeGoodPicker.ItemSelected += _ => RefreshTradeInstructions();
        heading.AddChild(_tradeGoodPicker);

        // What the good is actually doing, which is the context every decision below is made
        // against. A meter rather than five figures in a row: whether we make enough of this
        // is a shape, and the figures are underneath it for whoever wants them.
        _tradeGoodBalance = Title("", 12, Ui.Colour("text.body_soft"));
        _tradeGoodBalance.AutowrapMode = TextServer.AutowrapMode.Off;
        _tradeGoodBalance.ClipText = true;
        card.AddChild(_tradeGoodBalance);

        _tradeGoodMeter = new Control
        {
            CustomMinimumSize = new Vector2(0, 8),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        var back = new ColorRect { Color = Ui.Colour("surface.inset"), MouseFilter = MouseFilterEnum.Ignore };
        back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _tradeGoodMeter.AddChild(back);
        _tradeGoodMeterFill = new ColorRect { MouseFilter = MouseFilterEnum.Ignore };
        _tradeGoodMeterFill.AnchorTop = 0f;
        _tradeGoodMeterFill.AnchorBottom = 1f;
        _tradeGoodMeterFill.AnchorLeft = 0f;
        _tradeGoodMeter.AddChild(_tradeGoodMeterFill);
        card.AddChild(_tradeGoodMeter);

        _tradeGoodFlow = Title("", 11, Ui.Colour("text.caption"));
        _tradeGoodFlow.AutowrapMode = TextServer.AutowrapMode.Off;
        _tradeGoodFlow.ClipText = true;
        card.AddChild(_tradeGoodFlow);

        foreach (var (id, definition) in Ordered(TradeDesk.GoodScope))
            AddTradeControl(card, id, definition);
    }

    // ---------------------------------------------------------------- and about a country -

    private void BuildTradePartnerCard(VBoxContainer parent)
    {
        var card = TradeCard(parent, "ABOUT ONE COUNTRY", out var heading, out _tradePartnerCount);

        _tradePartnerFlag = FlagBadge("", 20);
        heading.AddChild(_tradePartnerFlag);
        _tradePartnerPicker = new OptionButton { CustomMinimumSize = new Vector2(190, 32),
            FitToLongestItem = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _tradePartnerPicker.ItemSelected += _ => RefreshTradeInstructions();
        heading.AddChild(_tradePartnerPicker);

        // A rule about a country, or a rule about a country and one good. The same control
        // serves both, because "no oil from them" and "nothing from them" are the same
        // decision at two widths and a player should not have to find two screens for it.
        _tradePartnerThisGoodOnly = new CheckButton { Text = Tr("for the good above only") };
        _tradePartnerThisGoodOnly.TooltipText = Ui.Tip(Tr(
            "Apply the rules below to the good selected above rather than to everything. " +
            "A rule naming a good is read first, and what the country said in general is the fallback."));
        _tradePartnerThisGoodOnly.Toggled += _ => RefreshTradeInstructions();
        card.AddChild(_tradePartnerThisGoodOnly);

        _tradePartnerControls = card;
        foreach (var (id, definition) in Ordered(TradeDesk.PartnerScope))
            AddTradeControl(card, id, definition);
    }

    /// <summary>
    /// A card inside a window: the inset ground the rest of the interface uses for a recessed
    /// area, a hairline of the same brass the dossier frame is ruled in, and nothing else. Two
    /// of these are what turn one list of seven sliders into two decisions.
    /// </summary>
    private static StyleBoxFlat TradeCardStyle()
    {
        var box = new StyleBoxFlat
        {
            BgColor = Ui.Colour("surface.inset"),
            BorderColor = new Color(Ui.Colour("button.primary_border"), 0.30f),
            CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2,
            CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
            ContentMarginLeft = 0, ContentMarginRight = 0,
            ContentMarginTop = 0, ContentMarginBottom = 0
        };
        box.SetBorderWidthAll(1);
        return box;
    }

    /// <summary>One panel with a heading, a mark, a selector and a count of what is in force.</summary>
    private VBoxContainer TradeCard(VBoxContainer parent, string title,
        out HBoxContainer heading, out Label count)
    {
        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", TradeCardStyle());
        parent.AddChild(frame);
        var margin = Margin(frame, 10, 8);
        var card = new VBoxContainer();
        card.AddThemeConstantOverride("separation", 5);
        margin.AddChild(card);

        // Selectors must not steal the heading's width. Keep the title/count above them;
        // counts retain their natural translated width instead of a clipped 120 px slot.
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 8);
        card.AddChild(titleRow);
        var caption = Title(title, 12, Ui.Colour("text.caption"), upper: true);
        caption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        titleRow.AddChild(caption);
        count = Title("", 11, Ui.Colour("text.accent"));
        count.AutowrapMode = TextServer.AutowrapMode.Off;
        count.HorizontalAlignment = HorizontalAlignment.Right;
        titleRow.AddChild(count);
        heading = new HBoxContainer();
        heading.AddThemeConstantOverride("separation", 8);
        card.AddChild(heading);
        return card;
    }

    /// <summary>The catalogue's settings for one scope, in a stable order.</summary>
    private static IEnumerable<(string Id, TradeSettingDefinition Definition)> Ordered(string scope) =>
        TradeDesk.All.Where(entry => entry.Value.Scope == scope)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value));

    /// <summary>
    /// One instruction: a dot saying whether it is in force, its name, its value, the control,
    /// and underneath, in words, what it is currently telling the traders to do.
    /// </summary>
    private void AddTradeControl(VBoxContainer parent, string id, TradeSettingDefinition definition)
    {
        var control = new TradeControl();
        control.Row = new VBoxContainer();
        control.Row.AddThemeConstantOverride("separation", 1);
        control.Row.TooltipText = Ui.Tip(Tr(definition.Description));
        parent.AddChild(control.Row);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 7);
        control.Row.AddChild(head);

        // Hollow when the control is resting, filled when it has been moved. One glyph, and
        // the answer to the only question this screen could not previously answer.
        // Built empty and filled on refresh: a glyph is not a caption, and putting one
        // through the caption helper asks the localisation check to index a circle.
        control.Dot = Title("", 12, Ui.Colour("text.faint"));
        control.Dot.Text = "○";
        control.Dot.CustomMinimumSize = new Vector2(12, 0);
        control.Dot.VerticalAlignment = VerticalAlignment.Center;
        head.AddChild(control.Dot);

        control.Caption = Fixed(Title(definition.Name, 12, Ui.Colour("text.body_soft")));
        control.Caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(control.Caption);

        control.Value = Fixed(Title("", 12, Ui.Colour("text.bright")), 130);
        control.Value.HorizontalAlignment = HorizontalAlignment.Right;
        head.AddChild(control.Value);

        // Only on a row that has something to undo, so the screen is not seven crosses.
        control.Reset = ActionButton("×", () => ResetTradeSetting(id), 24, 22);
        control.Reset.TooltipText = Ui.Tip(Tr("Put this back to what it was before this country decided anything."));
        control.Reset.Visible = false;
        head.AddChild(control.Reset);

        var captured = id;
        if (string.Equals(definition.Shape, "flag", StringComparison.OrdinalIgnoreCase))
        {
            // A yes-or-no is not a slider, and drawing it as one is how "FORBIDDEN 0 %"
            // happens. The name is already in the row above, so the switch carries none.
            control.Switch = new CheckButton { Text = "" };
            control.Switch.Toggled += pressed => OnTradeInstructionChanged(captured,
                pressed ? definition.Maximum : definition.Minimum);
            head.AddChild(control.Switch);
        }
        else
        {
            control.Slider = new HSlider
            {
                MinValue = definition.Minimum,
                MaxValue = definition.Maximum,
                Step = definition.Step > 0f ? definition.Step : 0.01f,
                CustomMinimumSize = new Vector2(0, 20),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                // Same reason as the budget: these sit in a scrolling panel, and a wheel that
                // moved the control under the pointer would rewrite a policy on the way past.
                Scrollable = false
            };
            control.Slider.ValueChanged += value => OnTradeInstructionChanged(captured, value);
            control.Row.AddChild(control.Slider);
        }

        control.Effect = Title("", 11, Ui.Colour("text.caption"));
        control.Effect.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        control.Row.AddChild(control.Effect);

        _tradeControls[id] = control;
    }

    private Commodity SelectedTradeGood()
    {
        var index = _tradeGoodPicker?.Selected ?? -1;
        return index >= 0 && index < _tradeGoodOrder.Count ? _tradeGoodOrder[index] : default;
    }

    private string SelectedTradePartner()
    {
        var index = _tradePartnerPicker?.Selected ?? -1;
        return index >= 0 && index < _tradePartnerOrder.Count ? _tradePartnerOrder[index] : "";
    }

    private void OnTradeInstructionChanged(string setting, double value)
    {
        if (_updatingTradeInstructions || _campaign?.Player is null) return;
        var definition = TradeDesk.Of(setting);
        if (definition is null) return;

        var partner = definition.Scope == TradeDesk.PartnerScope ? SelectedTradePartner() : "";
        if (definition.Scope == TradeDesk.PartnerScope && partner.Length == 0) return;
        var namesGood = definition.Scope == TradeDesk.GoodScope ||
                        (_tradePartnerThisGoodOnly?.ButtonPressed ?? false);

        _campaign.Commands.Enqueue(new SetTradeInstructionCommand
        {
            IssuerId = _campaign.Player.Id,
            GoodId = namesGood ? CommodityCatalog.IdOf(SelectedTradeGood()) : "",
            PartnerTag = partner,
            Setting = setting,
            Value = (float)value
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshTradeInstructions();
    }

    /// <summary>Put one control back to its declared default, which is what "not decided" is.</summary>
    private void ResetTradeSetting(string setting)
    {
        var definition = TradeDesk.Of(setting);
        if (definition is null) return;
        OnTradeInstructionChanged(setting, definition.Default);
    }

    private void ClearTradeInstructions()
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new ClearTradeInstructionsCommand { IssuerId = _campaign.Player.Id });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshTradeInstructions();
    }

    /// <summary>
    /// Put the controls where the country actually is.
    ///
    /// Set without firing their signals, for the reason the budget sliders are: a control
    /// that reported its own value back as a command would queue an order every time the
    /// screen refreshed, which at speed five is several orders a second.
    /// </summary>
    private void RefreshTradeInstructions()
    {
        if (_tradeInstructionRows is null || _campaign?.Player is null) return;
        var player = _campaign.Player;
        _updatingTradeInstructions = true;
        try
        {
            // Set here rather than only when the row was built. A label written once at
            // construction is a label in whatever language the game was in at construction,
            // and this screen outlives a change of language.
            _tradePartnerThisGoodOnly.Text = Tr("for the good above only");

            // Who is running the desk, and everything the desk - or the player - has decided.
            if (_tradeDelegated is not null)
            {
                _tradeDelegated.ButtonPressed = Delegation.Holds(player, Delegation.Trade);
                _tradeDelegated.Text = Tr(Delegation.Holds(player, Delegation.Trade)
                    ? "The trade desk is running itself"
                    : "Let the trade desk run itself");
            }
            RefreshTradeReview(player);

            FillGoodPicker();
            FillPartnerPicker(player);

            var commodity = SelectedTradeGood();
            var partner = SelectedTradePartner();
            var goodInForce = 0;
            var partnerInForce = 0;

            if (_tradeGoodMark is not null)
            {
                _tradeGoodMark.Texture = Art("icon_" + GoodIcon(commodity),
                    $"res://game/gfx/interface/icons/icon_{GoodIcon(commodity)}.png");
                _tradeGoodMark.Modulate = ArtColour("icon_" + GoodIcon(commodity), "icon.commodity");
            }
            SetFlag(_tradePartnerFlag, partner, 20);

            foreach (var (id, definition) in TradeDesk.All)
            {
                if (!_tradeControls.TryGetValue(id, out var control)) continue;
                var aboutPartner = definition.Scope == TradeDesk.PartnerScope;
                var value = aboutPartner
                    ? TradeDesk.ForPartner(player, partner, commodity, id)
                    : TradeDesk.ForGood(player, commodity, id);
                var live = !aboutPartner || partner.Length > 0;
                var inForce = live && TradeDesk.InForce(definition, value);
                if (inForce) { if (aboutPartner) partnerInForce++; else goodInForce++; }

                if (control.Slider is not null)
                {
                    control.Slider.MinValue = definition.Minimum;
                    control.Slider.MaxValue = definition.Maximum;
                    control.Slider.Step = definition.Step > 0f ? definition.Step : 0.01f;
                    control.Slider.Value = value;
                    control.Slider.Editable = live;
                }
                if (control.Switch is not null)
                {
                    control.Switch.ButtonPressed = value >= (definition.Minimum + definition.Maximum) * 0.5f;
                    control.Switch.Disabled = !live;
                }

                var reading = TradeValueText(definition, value);
                control.Caption.Text = Tr(definition.Name);
                control.Value.Text = reading;
                control.Effect.Text = TradeDesk.Reading(definition, value, reading);
                control.Reset.Visible = inForce;

                // Lit and filled when the traders have been told something; dim and hollow
                // when the row is only describing what everybody does anyway.
                control.Dot.Text = inForce ? "●" : "○";
                control.Dot.AddThemeColorOverride("font_color",
                    Ui.Colour(inForce ? "text.accent" : "text.faint"));
                control.Caption.AddThemeColorOverride("font_color",
                    Ui.Colour(inForce ? "text.bright" : "text.body_soft"));
                control.Value.AddThemeColorOverride("font_color",
                    Ui.Colour(inForce ? "text.accent" : "text.faint"));
                control.Row.Modulate = new Color(1, 1, 1, live ? 1f : 0.45f);
            }

            RefreshTradeGoodReading(player, commodity);

            _tradeGoodCount.Text = CountBadge(goodInForce);
            _tradePartnerCount.Text = partner.Length == 0
                ? Tr("no partner selected")
                : CountBadge(partnerInForce);

            var count = player.TradeSettings.Count;
            _tradeInstructionSummary.Text = count == 0
                ? Tr("Nothing has been decided; every good is traded on its merits.")
                : Loc.Format(Simulation.Plural.Key("{0:N0} instruction in force across every good and partner.",
                    count), count);
        }
        finally { _updatingTradeInstructions = false; }
    }

    private static string CountBadge(int inForce) => inForce == 0
        ? Tr("nothing decided here")
        : Loc.Format(Simulation.Plural.Key("{0:N0} in force", inForce), inForce);

    /// <summary>
    /// What the good is doing, as a shape first and figures second.
    ///
    /// Whether a country makes enough of something is the single fact every decision on this
    /// screen answers to, and it was previously the tail of a five-part sentence in small
    /// grey text. The meter fills to what is produced against what is wanted and turns red
    /// when the country is short.
    /// </summary>
    private void RefreshTradeGoodReading(Nation player, Commodity commodity)
    {
        if (!player.Market.TryGetValue(commodity, out var quote))
        {
            _tradeGoodBalance.Text = "";
            _tradeGoodFlow.Text = "";
            _tradeGoodMeterFill.AnchorRight = 0f;
            return;
        }

        var covered = quote.Demand > 0.0001f
            ? Mathf.Clamp(quote.Production / quote.Demand, 0f, 1f)
            : 1f;
        _tradeGoodMeterFill.AnchorRight = covered;
        _tradeGoodMeterFill.Color = Ui.Colour(
            quote.IsShortage ? "state.negative" : covered >= 0.999f ? "state.positive" : "state.warning");

        _tradeGoodBalance.Text = Loc.Format("We make {0} of the {1} we want",
            Fmt.Number(quote.Production), Fmt.Number(quote.Demand));
        _tradeGoodFlow.Text = Loc.Format("Bought in {0} · sold abroad {1} · price {2}",
            Fmt.Number(quote.Imports), Fmt.Number(quote.Exports), Fmt.Money(quote.Price));
    }

    /// <summary>A setting's value, printed the way its declared shape asks for.</summary>
    private static string TradeValueText(TradeSettingDefinition definition, float value) =>
        definition.Shape switch
        {
            "share" => Fmt.Percent(value),
            "flag" => value >= (definition.Minimum + definition.Maximum) * 0.5f ? Tr("yes") : Tr("no"),
            // Nought is the off position for the price limits, and "0.0x" reads as "refuse
            // everything" when it means the opposite.
            "multiplier" => value <= 0.0001f && definition.Default <= 0.0001f
                ? Loc.Get("no limit")
                : Loc.Format("{0}×", value.ToString("0.##")),
            _ => value.ToString("0.##")
        };

    private void FillGoodPicker()
    {
        // Already built: the list is right, but its words may not be. A dropdown item carries
        // no key for the language refresh to find, so the text is re-set here instead.
        if (_tradeGoodPicker.ItemCount == CommodityCatalog.All.Count() && _tradeGoodOrder.Count > 0)
        {
            for (var index = 0; index < _tradeGoodOrder.Count; index++)
            {
                var wanted = Tr(CommodityCatalog.Get(_tradeGoodOrder[index]).Name);
                if (_tradeGoodPicker.GetItemText(index) != wanted)
                    _tradeGoodPicker.SetItemText(index, wanted);
            }
            return;
        }
        var chosen = _tradeGoodPicker.Selected;
        _tradeGoodPicker.Clear();
        _tradeGoodOrder.Clear();
        foreach (var commodity in CommodityCatalog.All)
        {
            _tradeGoodOrder.Add(commodity);
            _tradeGoodPicker.AddItem(Tr(CommodityCatalog.Get(commodity).Name));
        }
        if (_tradeGoodPicker.ItemCount > 0)
            _tradeGoodPicker.Selected = Math.Clamp(chosen, 0, _tradeGoodPicker.ItemCount - 1);
    }

    /// <summary>
    /// Whoever this country actually trades with, largest first, so the list is the list a
    /// player would reach for. A country it has already said something about stays on it
    /// even when the trade has stopped - which is exactly what forbidding one does.
    /// </summary>
    private void FillPartnerPicker(Nation player)
    {
        var wanted = new List<(string Tag, float Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (partnerId, value) in player.TradePartners)
        {
            var partner = _campaign.NationById(partnerId);
            if (partner is null || !seen.Add(partner.Tag)) continue;
            wanted.Add((partner.Tag, value));
        }
        foreach (var key in player.TradeSettings.Keys)
        {
            if (!key.StartsWith("partner/", StringComparison.Ordinal)) continue;
            var tag = key.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
            if (tag is null || !seen.Add(tag)) continue;
            wanted.Add((tag, 0f));
        }
        wanted.Sort((left, right) =>
        {
            var order = right.Value.CompareTo(left.Value);
            return order != 0 ? order : string.CompareOrdinal(left.Tag, right.Tag);
        });

        if (_tradePartnerOrder.SequenceEqual(wanted.Select(entry => entry.Tag))) return;
        var chosen = _tradePartnerPicker.Selected;
        _tradePartnerPicker.Clear();
        _tradePartnerOrder.Clear();
        foreach (var (tag, _) in wanted)
        {
            _tradePartnerOrder.Add(tag);
            var partner = _campaign.Nations.Find(candidate => candidate.Tag == tag);
            _tradePartnerPicker.AddItem(partner is null ? tag : $"{tag}  {partner.DisplayName}");
        }
        if (_tradePartnerPicker.ItemCount > 0)
            _tradePartnerPicker.Selected = Math.Clamp(chosen, 0, _tradePartnerPicker.ItemCount - 1);
    }
}
