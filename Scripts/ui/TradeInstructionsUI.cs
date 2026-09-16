using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The controls a country has over its own trade, under the trade policy that frames them.
///
/// Drawn from `common/trade` rather than from a list written here: every slider on this
/// screen is a setting the catalogue declares, with the catalogue's own name, explanation,
/// bounds and step. A mod that adds a control gets one here without this file being told,
/// and a mod that narrows the import ceiling to eight tenths narrows the slider.
///
/// One good at a time and one country at a time, because the alternative is a hundred
/// sliders on one screen. The pair of selectors is the whole of the navigation, and what is
/// under them is the current state of that good or that partner.
/// </summary>
public partial class DealOrForceGame
{
    private VBoxContainer _tradeInstructionRows;
    private OptionButton _tradeGoodPicker;
    private OptionButton _tradePartnerPicker;
    private CheckButton _tradePartnerThisGoodOnly;
    private Label _tradeGoodReading;
    private Label _tradeInstructionSummary;
    private readonly List<Commodity> _tradeGoodOrder = new();
    private readonly List<string> _tradePartnerOrder = new();
    private readonly Dictionary<string, HSlider> _tradeSliders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Label> _tradeReadings = new(StringComparer.Ordinal);
    private bool _updatingTradeInstructions;

    private void BuildTradeInstructions(VBoxContainer rows)
    {
        StateSection(rows, "TRADE INSTRUCTIONS",
            "What this country has told its traders to do, good by good and country by country. " +
            "Every control here is declared in common/trade, so a mod can change the limits, the " +
            "defaults and the names of all of them. A change takes effect at the next weekly clearing.");

        _tradeInstructionRows = new VBoxContainer();
        _tradeInstructionRows.AddThemeConstantOverride("separation", 6);
        rows.AddChild(_tradeInstructionRows);

        // ---- which good ----
        var goodRow = new HBoxContainer();
        goodRow.AddThemeConstantOverride("separation", 9);
        goodRow.AddChild(RowLabel(Tr("Per good"), 12, Ui.Colour("text.caption"), 104, upper: true));
        _tradeGoodPicker = new OptionButton { CustomMinimumSize = new Vector2(190, 32) };
        _tradeGoodPicker.ItemSelected += _ => RefreshTradeInstructions();
        goodRow.AddChild(_tradeGoodPicker);
        _tradeGoodReading = Title("", 11, Ui.Colour("text.caption"));
        _tradeGoodReading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _tradeGoodReading.VerticalAlignment = VerticalAlignment.Center;
        goodRow.AddChild(_tradeGoodReading);
        _tradeInstructionRows.AddChild(goodRow);

        foreach (var (id, definition) in Ordered(TradeDesk.GoodScope))
            AddTradeSlider(_tradeInstructionRows, id, definition);

        // ---- which country ----
        var partnerRow = new HBoxContainer();
        partnerRow.AddThemeConstantOverride("separation", 9);
        partnerRow.AddChild(RowLabel(Tr("Per country"), 12, Ui.Colour("text.caption"), 104, upper: true));
        _tradePartnerPicker = new OptionButton { CustomMinimumSize = new Vector2(190, 32) };
        _tradePartnerPicker.ItemSelected += _ => RefreshTradeInstructions();
        partnerRow.AddChild(_tradePartnerPicker);
        // A rule about a country, or a rule about a country and one good. The same control
        // serves both, because "no oil from them" and "nothing from them" are the same
        // decision at two widths and a player should not have to find two screens for it.
        _tradePartnerThisGoodOnly = new CheckButton { Text = Tr("for the good above only") };
        _tradePartnerThisGoodOnly.TooltipText = Ui.Tip(Tr(
            "Apply the rules below to the good selected above rather than to everything. " +
            "A rule naming a good is read first, and what the country said in general is the fallback."));
        _tradePartnerThisGoodOnly.Toggled += _ => RefreshTradeInstructions();
        partnerRow.AddChild(_tradePartnerThisGoodOnly);
        _tradeInstructionRows.AddChild(partnerRow);

        foreach (var (id, definition) in Ordered(TradeDesk.PartnerScope))
            AddTradeSlider(_tradeInstructionRows, id, definition);

        // ---- and the way out ----
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 9);
        _tradeInstructionSummary = Title("", 11, Ui.Colour("text.accent_dim"));
        _tradeInstructionSummary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _tradeInstructionSummary.VerticalAlignment = VerticalAlignment.Center;
        footer.AddChild(_tradeInstructionSummary);
        var clear = ActionButton("Clear every instruction", ClearTradeInstructions, 190, 30);
        clear.TooltipText = Ui.Tip(Tr("Forget everything this country has decided about its trade and " +
                                      "let every good be traded on its merits again."));
        footer.AddChild(clear);
        _tradeInstructionRows.AddChild(footer);
    }

    /// <summary>The catalogue's settings for one scope, in a stable order.</summary>
    private static IEnumerable<(string Id, TradeSettingDefinition Definition)> Ordered(string scope) =>
        TradeDesk.All.Where(entry => entry.Value.Scope == scope)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value));

    private void AddTradeSlider(VBoxContainer parent, string id, TradeSettingDefinition definition)
    {
        var slider = new HSlider
        {
            MinValue = definition.Minimum,
            MaxValue = definition.Maximum,
            Step = definition.Step > 0f ? definition.Step : 0.01f,
            CustomMinimumSize = new Vector2(190, 26),
            // Same reason as the budget: these sit in a scrolling panel, and a wheel that
            // moved the control under the pointer would rewrite a policy on the way past.
            Scrollable = false
        };
        var captured = id;
        slider.ValueChanged += value => OnTradeInstructionChanged(captured, value);
        var row = SliderRow(Tr(definition.Name), slider, out var reading);
        row.TooltipText = Ui.Tip(Tr(definition.Description));
        parent.AddChild(row);
        _tradeSliders[id] = slider;
        _tradeReadings[id] = reading;
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
    /// screen refreshed, which at speed five is five orders a second.
    /// </summary>
    private void RefreshTradeInstructions()
    {
        if (_tradeInstructionRows is null || _campaign?.Player is null) return;
        var player = _campaign.Player;
        _updatingTradeInstructions = true;
        try
        {
            // Set here rather than only when the row was built. A label written once at
            // construction is a label that is whatever language the game was in at
            // construction, and this screen outlives a change of language; the sliders are
            // refreshed anyway, so the two words beside them cost nothing to refresh too.
            _tradePartnerThisGoodOnly.Text = Tr("for the good above only");

            FillGoodPicker();
            FillPartnerPicker(player);

            var commodity = SelectedTradeGood();
            var partner = SelectedTradePartner();
            var namesGood = _tradePartnerThisGoodOnly?.ButtonPressed ?? false;

            foreach (var (id, definition) in TradeDesk.All)
            {
                if (!_tradeSliders.TryGetValue(id, out var slider)) continue;
                slider.MinValue = definition.Minimum;
                slider.MaxValue = definition.Maximum;
                slider.Step = definition.Step > 0f ? definition.Step : 0.01f;

                var value = definition.Scope == TradeDesk.PartnerScope
                    ? TradeDesk.ForPartner(player, partner, commodity, id)
                    : TradeDesk.ForGood(player, commodity, id);
                slider.Value = value;
                slider.Editable = definition.Scope != TradeDesk.PartnerScope || partner.Length > 0;
                if (_tradeReadings.TryGetValue(id, out var reading))
                    reading.Text = Reading(definition, value);
            }

            // What the good is actually doing, which is the context every one of these
            // decisions is made against.
            if (player.Market.TryGetValue(commodity, out var quote))
                _tradeGoodReading.Text = Loc.Format("{0} {1} · {2} {3} · {4} {5} · {6} {7} · {8} {9}",
                    Tr("Production"), Fmt.Number(quote.Production),
                    Tr("Demand"), Fmt.Number(quote.Demand),
                    Tr("Imported"), Fmt.Number(quote.Imports),
                    Tr("Exported"), Fmt.Number(quote.Exports),
                    Tr("Price"), Fmt.Money(quote.Price));
            else _tradeGoodReading.Text = "";

            var count = player.TradeSettings.Count;
            _tradeInstructionSummary.Text = count == 0
                ? Tr("Nothing has been decided; every good is traded on its merits.")
                : Loc.Format("{0}: {1}", Tr("Instructions in force"), count);
        }
        finally { _updatingTradeInstructions = false; }
    }

    private static string Reading(TradeSettingDefinition definition, float value) => definition.Shape switch
    {
        "share" => Fmt.Percent(value),
        "flag" => value >= 0.5f ? "✓" : "—",
        // Nought is the off position for the price limits, and "0.0×" reads as "refuse
        // everything" when it means the opposite.
        "multiplier" => value <= 0.0001f && definition.Default <= 0.0001f
            ? Loc.Get("no limit")
            : Loc.Format("{0}×", value.ToString("0.##")),
        _ => value.ToString("0.##")
    };

    private void FillGoodPicker()
    {
        if (_tradeGoodPicker.ItemCount == CommodityCatalog.All.Count() && _tradeGoodOrder.Count > 0) return;
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
