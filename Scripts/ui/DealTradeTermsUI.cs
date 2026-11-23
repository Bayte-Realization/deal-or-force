using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The trade agreement on the deal table: one strip under both columns, because it is the pair's
/// term and not a side's (TradeTerms). Closed it is one tick; open it is presets in a row, then the
/// numbers the presets fill in - each side's tariff cut, the goods, the term, a supply guarantee -
/// each a slider with one-press values and, where it matters, "the most they would accept". Under it,
/// what the terms would change, in the same numbers the other government counts them by.
/// </summary>
public partial class DealOrForceGame
{
    private CheckBox _dealTradeOn;
    private VBoxContainer _dealTradeBox;
    private HSlider _dealOurCut, _dealTheirCut, _dealYears, _dealShare;
    private Label _dealOurCutLabel, _dealTheirCutLabel, _dealYearsLabel, _dealShareLabel, _dealTradeEffect, _dealTradeNote;
    private OptionButton _dealGuarantee, _dealGuaranteeGood;
    private HFlowContainer _dealOurGoodsRow, _dealTheirGoodsRow, _dealPresetRow;
    private readonly HashSet<string> _dealOurGoods = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dealTheirGoods = new(StringComparer.OrdinalIgnoreCase);
    private bool _dealOurGoodsAll = true, _dealTheirGoodsAll = true, _dealOurGoodsMore, _dealTheirGoodsMore;
    private bool _dealTradeIndefinite = true, _dealTradeRenewal, _dealTradeSync;
    private string _dealTradePreset = "ordinary";
    private Commodity[] _dealGuaranteeGoods = Array.Empty<Commodity>();
    private const int DealChipHeight = 30;

    /// <summary>The trade terms the strip describes, or null when the agreement is not on the table.</summary>
    private TradeTerms DealTradeDraft()
    {
        if (_dealTradeOn is null || !_dealTradeOn.ButtonPressed) return null;
        var player = _campaign.Player;
        var target = _campaign.NationById(_dealTarget);
        if (player is null || target is null) return null;
        var t = new TradeTerms
        {
            Preset = _dealTradePreset ?? "", Renewal = _dealTradeRenewal,
            Years = _dealTradeIndefinite ? 0 : (int)_dealYears.Value
        };
        t.SetHalf(player.Id, target.Id, (float)_dealOurCut.Value / 100f, _dealOurGoodsAll ? Array.Empty<string>() : _dealOurGoods.OrderBy(g => g).ToArray());
        t.SetHalf(target.Id, player.Id, (float)_dealTheirCut.Value / 100f, _dealTheirGoodsAll ? Array.Empty<string>() : _dealTheirGoods.OrderBy(g => g).ToArray());
        var direction = _dealGuarantee.Selected;
        if (direction > 0 && _dealGuaranteeGood.Selected >= 0 && _dealGuaranteeGood.Selected < _dealGuaranteeGoods.Length)
        {
            t.GuarantorId = direction == 1 ? target.Id : player.Id;
            t.GuaranteeGood = CommodityCatalog.IdOf(_dealGuaranteeGoods[_dealGuaranteeGood.Selected]);
            t.GuaranteeShare = (float)_dealShare.Value / 100f;
        }
        return t;
    }

    private void BuildDealTradeStrip(VBoxContainer rows, Nation player, Nation target, DiplomaticRelation relation, TradeTerms start)
    {
        var frame = new PanelContainer { Name = "DealSharedPanel", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        frame.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10));
        rows.AddChild(frame);
        var strip = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        strip.AddThemeConstantOverride("separation", 6);
        frame.AddChild(strip);
        strip.AddChild(Title(Tr("SHARED"), 13, Ui.Colour("text.accent")));
        var inForce = relation?.TradeAgreement == true;
        _dealTradeRenewal = start?.Renewal == true && inForce;
        _dealTradeOn = new CheckBox
        {
            Name = "DealTradeAgreement", CustomMinimumSize = new(0, DealChipHeight),
            Text = _dealTradeRenewal ? Tr("Revise our trade agreement") : Tr("Trade agreement"),
            Disabled = inForce && !_dealTradeRenewal,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = Ui.Tip(Tr("A term of both countries, not of one side: each importer cuts its own tariff on the other's goods. Both count the same terms from their own seat."))
        };
        strip.AddChild(_dealTradeOn);
        if (_dealTradeOn.Disabled) strip.AddChild(DealRefusal("A trade agreement is already in force. Revise it from the country page."));
        _dealTradeBox = new VBoxContainer { Name = "DealTradeTerms", Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        _dealTradeBox.AddThemeConstantOverride("separation", 6);
        strip.AddChild(_dealTradeBox);

        _dealPresetRow = new HFlowContainer { Name = "DealTradePresets", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        _dealPresetRow.AddThemeConstantOverride("h_separation", 6);
        _dealPresetRow.AddThemeConstantOverride("v_separation", 4);
        _dealTradeBox.AddChild(_dealPresetRow);
        foreach (var preset in TradeTermsCatalog.All.Where(p => p.Id != "concession"))
        {
            var id = preset.Id;
            var chip = ActionButton(PresetCaption(preset, player, target), () => ApplyDealPreset(id), 0, DealChipHeight);
            chip.Name = "DealTradePreset_" + id;
            _dealPresetRow.AddChild(chip);
        }

        (HSlider, Label) CutRow(string caption, string name, Action<float> set, bool theirs)
        {
            _dealTradeBox.AddChild(Title(caption, 12, Ui.Colour("text.muted")));
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 6);
            _dealTradeBox.AddChild(line);
            var slider = new HSlider { Name = name, MinValue = 0, MaxValue = 100, Step = 25, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new(160, DealChipHeight) };
            line.AddChild(slider);
            var value = Title("", 13, Ui.Colour("text.body"));
            value.CustomMinimumSize = new(52, 0);
            value.AddThemeFontOverride("font", PresentationFonts.Tabular());
            line.AddChild(value);
            foreach (var quick in new[] { 0, 50, 100 })
            {
                var q = quick;
                line.AddChild(ActionButton(q + "%", () => set(q), 56, DealChipHeight));
            }
            if (theirs)
            {
                var most = ActionButton(Tr("The most they'd accept"), DealTheirCutMost, 0, DealChipHeight);
                most.Name = "DealTradeTheirCutMost";
                most.TooltipText = Ui.Tip(Tr("The largest cut of their tariff at which they would still sign the whole table."));
                line.AddChild(most);
            }
            slider.ValueChanged += _ => { if (!_dealTradeSync) { _dealTradePreset = ""; RefreshDealComposer(); } };
            return (slider, value);
        }
        (_dealOurCut, _dealOurCutLabel) = CutRow(Tr("Our tariff cut on their goods"), "DealTradeOurCut", v => _dealOurCut.Value = v, false);
        _dealOurGoodsRow = GoodsRow("DealTradeOurGoods");
        (_dealTheirCut, _dealTheirCutLabel) = CutRow(Tr("Their tariff cut on our goods"), "DealTradeTheirCut", v => _dealTheirCut.Value = v, true);
        _dealTheirGoodsRow = GoodsRow("DealTradeTheirGoods");

        _dealTradeBox.AddChild(Title(Tr("Term"), 12, Ui.Colour("text.muted")));
        var years = new HBoxContainer();
        years.AddThemeConstantOverride("separation", 6);
        _dealTradeBox.AddChild(years);
        _dealYears = new HSlider { Name = "DealTradeYears", MinValue = 1, MaxValue = 10, Step = 1, Value = 5,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(160, DealChipHeight) };
        years.AddChild(_dealYears);
        _dealYearsLabel = Title("", 13, Ui.Colour("text.body"));
        _dealYearsLabel.CustomMinimumSize = new(72, 0);
        _dealYearsLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        years.AddChild(_dealYearsLabel);
        foreach (var quick in new[] { 2, 5, 10, 0 })
        {
            var q = quick;
            years.AddChild(ActionButton(q == 0 ? "∞" : q.ToString(), () =>
            {
                _dealTradeIndefinite = q == 0;
                if (q > 0) { _dealTradeSync = true; _dealYears.Value = q; _dealTradeSync = false; }
                _dealTradePreset = "";
                RefreshDealComposer();
            }, 56, DealChipHeight));
        }
        _dealYears.ValueChanged += _ => { if (_dealTradeSync) return; _dealTradeIndefinite = false; _dealTradePreset = ""; RefreshDealComposer(); };

        _dealTradeBox.AddChild(Title(Tr("Supply guarantee"), 12, Ui.Colour("text.muted")));
        var guarantee = new HBoxContainer();
        guarantee.AddThemeConstantOverride("separation", 6);
        _dealTradeBox.AddChild(guarantee);
        _dealGuarantee = new OptionButton { Name = "DealTradeGuarantee", CustomMinimumSize = new(140, DealChipHeight) };
        _dealGuarantee.AddItem(Tr("No guarantee"));
        _dealGuarantee.AddItem(Tr("They guarantee us"));
        _dealGuarantee.AddItem(Tr("We guarantee them"));
        guarantee.AddChild(_dealGuarantee);
        _dealGuaranteeGood = new OptionButton { Name = "DealTradeGuaranteeGood", CustomMinimumSize = new(140, DealChipHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        guarantee.AddChild(_dealGuaranteeGood);
        _dealGuarantee.ItemSelected += _ => { FillGuaranteeGoods(); _dealTradePreset = ""; RefreshDealComposer(); };
        _dealGuaranteeGood.ItemSelected += _ => { _dealTradePreset = ""; RefreshDealComposer(); };
        var share = new HBoxContainer();
        share.AddThemeConstantOverride("separation", 6);
        _dealTradeBox.AddChild(share);
        _dealShare = new HSlider { Name = "DealTradeShare", MinValue = 5, MaxValue = 50, Step = 5, Value = 25,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(160, DealChipHeight) };
        share.AddChild(_dealShare);
        _dealShareLabel = Title("", 13, Ui.Colour("text.body"));
        _dealShareLabel.CustomMinimumSize = new(52, 0);
        _dealShareLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        share.AddChild(_dealShareLabel);
        var mostShare = ActionButton(Tr("The most they'd give"), DealShareMost, 0, DealChipHeight);
        mostShare.Name = "DealTradeShareMost";
        mostShare.TooltipText = Ui.Tip(Tr("The largest share of their spare output they would still promise and sign."));
        share.AddChild(mostShare);
        _dealShare.ValueChanged += _ => { if (!_dealTradeSync) { _dealTradePreset = ""; RefreshDealComposer(); } };

        _dealTradeEffect = WrappedUltimatumText("", 13);
        _dealTradeEffect.Name = "DealTradeEffect";
        _dealTradeBox.AddChild(_dealTradeEffect);
        _dealTradeNote = WrappedUltimatumText("", 12);
        _dealTradeBox.AddChild(_dealTradeNote);

        _dealTradeOn.Toggled += on => { _dealTradeBox.Visible = on; RefreshDealComposer(); };
        FillGuaranteeGoods();
        ApplyTradeTermsToControls(start ?? (inForce ? TradeTerms.Of(relation) : TradeTermsCatalog.Ordinary(_campaign, player, target)));
        if (start is not null && !_dealTradeOn.Disabled)
        {
            _dealTradeOn.SetPressedNoSignal(true);
            _dealTradeBox.Visible = true;
        }
    }

    /// <summary>"Goods for goods" names the pair's real goods: "Oil for Grain".</summary>
    private string PresetCaption(TradePreset preset, Nation player, Nation target)
    {
        if (preset.Id == "goods_for_goods")
        {
            var ours = TradeTermsCatalog.Best(_campaign, player, target, 1);
            var theirs = TradeTermsCatalog.Best(_campaign, target, player, 1);
            if (ours.Length > 0 && theirs.Length > 0)
                return Loc.Format("{0} for {1}", TradeTermsText.GoodsList(theirs), TradeTermsText.GoodsList(ours));
        }
        return Loc.Get(preset.Name);
    }

    private HFlowContainer GoodsRow(string name)
    {
        var row = new HFlowContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        row.AddThemeConstantOverride("h_separation", 6);
        row.AddThemeConstantOverride("v_separation", 4);
        _dealTradeBox.AddChild(row);
        return row;
    }

    private void FillGuaranteeGoods()
    {
        var player = _campaign.Player;
        var target = _campaign.NationById(_dealTarget);
        if (player is null || target is null || _dealGuaranteeGood is null) return;
        var keep = _dealGuaranteeGood.Selected >= 0 && _dealGuaranteeGood.Selected < _dealGuaranteeGoods.Length
            ? _dealGuaranteeGoods[_dealGuaranteeGood.Selected] : (Commodity?)null;
        var toUs = _dealGuarantee.Selected != 2;
        var receiver = toUs ? player : target;
        var giver = toUs ? target : player;
        // Most needed first: what the receiver lacks against what the giver can spare.
        _dealGuaranteeGoods = CommodityCatalog.All
            .OrderByDescending(g => MathF.Min(TradeTermsCatalog.Shortfall(receiver, g), TradeTermsCatalog.Spare(giver, g)) *
                                    CommodityCatalog.Get(g).BasePrice)
            .ThenBy(g => CommodityCatalog.IdOf(g), StringComparer.Ordinal).ToArray();
        _dealGuaranteeGood.Clear();
        foreach (var g in _dealGuaranteeGoods) _dealGuaranteeGood.AddItem(Loc.Get(CommodityCatalog.Get(g).Name));
        var index = keep is { } k ? Array.IndexOf(_dealGuaranteeGoods, k) : 0;
        if (_dealGuaranteeGoods.Length > 0) _dealGuaranteeGood.Select(Math.Max(0, index));
    }

    private void ApplyDealPreset(string id)
    {
        var player = _campaign.Player;
        var target = _campaign.NationById(_dealTarget);
        var built = TradeTermsCatalog.Build(_campaign, TradeTermsCatalog.Find(id), player, target);
        if (built is null)
        {
            _dealTradeNote.Text = Tr("This preset does not fit the two countries: nothing to name in it.");
            return;
        }
        built.Renewal = _dealTradeRenewal;
        ApplyTradeTermsToControls(built);
        RefreshDealComposer();
    }

    private void ApplyTradeTermsToControls(TradeTerms t)
    {
        if (t is null || _dealOurCut is null) return;
        var player = _campaign.Player;
        var target = _campaign.NationById(_dealTarget);
        _dealTradeSync = true;
        _dealTradePreset = t.Preset ?? "";
        _dealOurCut.Value = t.CutOf(player.Id, target.Id) * 100f;
        _dealTheirCut.Value = t.CutOf(target.Id, player.Id) * 100f;
        _dealOurGoods.Clear(); _dealTheirGoods.Clear();
        foreach (var g in t.GoodsOf(player.Id, target.Id)) _dealOurGoods.Add(g);
        foreach (var g in t.GoodsOf(target.Id, player.Id)) _dealTheirGoods.Add(g);
        _dealOurGoodsAll = _dealOurGoods.Count == 0;
        _dealTheirGoodsAll = _dealTheirGoods.Count == 0;
        _dealTradeIndefinite = t.Years <= 0;
        if (t.Years > 0) _dealYears.Value = Math.Clamp(t.Years, 1, 10);
        _dealGuarantee.Select(!t.HasGuarantee ? 0 : t.GuarantorId == target.Id ? 1 : 2);
        FillGuaranteeGoods();
        if (t.TryGuaranteeGood(out var good)) _dealGuaranteeGood.Select(Math.Max(0, Array.IndexOf(_dealGuaranteeGoods, good)));
        if (t.HasGuarantee) _dealShare.Value = t.GuaranteeShare * 100f;
        _dealTradeSync = false;
    }

    /// <summary>The goods chips of one half: "All", then the goods sorted by how well they fit, six at a time.</summary>
    private void FillGoodsChips(HFlowContainer row, HashSet<string> chosen, bool all, bool more, Nation importer, Nation partner,
        Action<bool> setAll, Action<bool> setMore)
    {
        foreach (var child in row.GetChildren()) { row.RemoveChild(child); child.QueueFree(); }
        var allChip = new Button { Text = Tr("All goods"), ToggleMode = true, CustomMinimumSize = new(0, DealChipHeight) };
        allChip.SetPressedNoSignal(all);
        allChip.Toggled += on => { setAll(on); if (!on && chosen.Count == 0) foreach (var g in TradeTermsCatalog.Best(_campaign, importer, partner, 1)) chosen.Add(g); _dealTradePreset = ""; RefreshDealComposer(); };
        row.AddChild(allChip);
        if (all) return;
        var ranked = TradeTermsCatalog.Best(_campaign, importer, partner, int.MaxValue).ToList();
        foreach (var g in CommodityCatalog.All.Select(CommodityCatalog.IdOf).OrderBy(id => id, StringComparer.Ordinal))
            if (!ranked.Contains(g, StringComparer.OrdinalIgnoreCase)) ranked.Add(g);
        var shown = more ? ranked : ranked.Take(6).Union(chosen, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var id in shown)
        {
            var good = id;
            var chip = new Button { ToggleMode = true, CustomMinimumSize = new(0, DealChipHeight),
                Text = CommodityCatalog.TryParse(id, out var c) ? Loc.Get(CommodityCatalog.Get(c).Name) : id };
            chip.SetPressedNoSignal(chosen.Contains(id));
            chip.Toggled += on => { if (on) chosen.Add(good); else chosen.Remove(good); _dealTradePreset = ""; RefreshDealComposer(); };
            row.AddChild(chip);
        }
        var toggle = ActionButton(more ? Tr("Fewer") : Tr("More goods…"), () => { setMore(!more); RefreshDealComposer(); }, 0, DealChipHeight);
        row.AddChild(toggle);
    }

    /// <summary>The strip's numbers and the effect readout, refreshed with the rest of the table.</summary>
    private void RefreshDealTradeStrip(Nation player, Nation target)
    {
        if (_dealTradeOn is null || player is null || target is null) return;
        _dealOurCutLabel.Text = Fmt.Percent((float)_dealOurCut.Value / 100f);
        _dealTheirCutLabel.Text = Fmt.Percent((float)_dealTheirCut.Value / 100f);
        _dealYearsLabel.Text = _dealTradeIndefinite ? Tr("No end") : Loc.Format("{0} year(s)", (int)_dealYears.Value);
        _dealShareLabel.Text = Fmt.Percent((float)_dealShare.Value / 100f);
        _dealShare.Editable = _dealGuarantee.Selected > 0;
        _dealGuaranteeGood.Disabled = _dealGuarantee.Selected == 0;
        foreach (var chip in _dealPresetRow.GetChildren().OfType<Button>())
            chip.Modulate = chip.Name == "DealTradePreset_" + _dealTradePreset ? Ui.Colour("text.accent") : Ui.Colour("text.bright");
        if (!_dealTradeOn.ButtonPressed) return;
        FillGoodsChips(_dealOurGoodsRow, _dealOurGoods, _dealOurGoodsAll, _dealOurGoodsMore, player, target,
            v => _dealOurGoodsAll = v, v => _dealOurGoodsMore = v);
        FillGoodsChips(_dealTheirGoodsRow, _dealTheirGoods, _dealTheirGoodsAll, _dealTheirGoodsMore, target, player,
            v => _dealTheirGoodsAll = v, v => _dealTheirGoodsMore = v);
        var draft = DealTradeDraft();
        if (draft is null) { _dealTradeEffect.Text = ""; return; }
        var view = TradeTermsValue.Read(_campaign, player, target);
        var lines = TradeTermsValue.Effects(view, draft).ToList();
        _dealTradeEffect.Text = lines.Count == 0
            ? Tr("On this week's market these terms move little between you.")
            : Tr("What changes for us") + "\n" + string.Join("\n", lines);
        var why = new Simulation.Breakdown();
        why.Begin("", "");
        TradeTermsValue.Value(view, draft, why);
        _dealTradeNote.Text = Tr("How we count it") + ": " + string.Join(" · ", why.Entries
            .Where(e => MathF.Abs(e.Value) >= 0.05f).OrderByDescending(e => MathF.Abs(e.Value)).Take(5)
            .Select(e => $"{Tr(e.Source)} {e.Value:+0.0;-0.0}"));
    }

    /// <summary>The largest cut of their tariff at which they would still sign the whole table.</summary>
    private void DealTheirCutMost()
    {
        var keep = _dealTheirCut.Value;
        for (var cut = 100; cut >= 0; cut -= 25)
        {
            _dealTradeSync = true; _dealTheirCut.Value = cut; _dealTradeSync = false;
            if (Ultimatum.AssessNegotiatedDeal(_campaign, _campaign.Player, _campaign.NationById(_dealTarget), ComposedDeal()).Acceptable)
            {
                _dealTradePreset = "";
                RefreshDealComposer();
                return;
            }
        }
        _dealTradeSync = true; _dealTheirCut.Value = keep; _dealTradeSync = false;
        _dealTradeNote.Text = Tr("They would not sign at any cut of their tariff. Pay, or give more.");
    }

    /// <summary>The largest guarantee they would still give and sign.</summary>
    private void DealShareMost()
    {
        if (_dealGuarantee.Selected != 1) _dealGuarantee.Select(1);
        FillGuaranteeGoods();
        var keep = _dealShare.Value;
        for (var share = 50; share >= 5; share -= 5)
        {
            _dealTradeSync = true; _dealShare.Value = share; _dealTradeSync = false;
            if (Ultimatum.AssessNegotiatedDeal(_campaign, _campaign.Player, _campaign.NationById(_dealTarget), ComposedDeal()).Acceptable)
            {
                _dealTradePreset = "";
                RefreshDealComposer();
                return;
            }
        }
        _dealTradeSync = true; _dealShare.Value = keep; _dealTradeSync = false;
        _dealTradeNote.Text = Tr("They would not promise any share of it on these terms.");
    }
}
