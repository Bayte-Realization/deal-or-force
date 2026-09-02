using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The peace conference: a map you draw the settlement on, and a bill that adds up.
///
/// The fixed terms are a menu, and a menu can only offer the peaces somebody thought of in
/// advance. This is the other half - you paint the ground you mean to keep, straight onto
/// the map, with the same drag a battle plan uses. Every province you claim is priced
/// against the war score the war is actually worth, and the map redraws in your colours as
/// you go, so what you are looking at is the border you are proposing rather than a list
/// describing one.
///
/// Clicking works as well as dragging, and a region in the ledger takes or gives back the
/// whole region at once, because painting twenty provinces one at a time is not a feature.
/// </summary>
public partial class DealOrForceGame
{
    private Control _peaceLayer = null!;
    private Label _peaceTitle = null!;
    private Label _peaceBudget = null!;
    private Label _peaceVerdict = null!;
    private VBoxContainer _peaceLedgerRows = null!;
    private Label _peaceHint = null!;
    private readonly Dictionary<string, Button> _peaceTermButtons = new();
    private PeaceDemand _peaceDemand;
    private PeaceLedger _peaceLedgerModel;
    private StrategicWar _peaceWar;

    private void BuildPeaceConference()
    {
        _peaceLayer = FullLayer("PeaceConference");
        _peaceLayer.Visible = false;
        _peaceLayer.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_peaceLayer);

        var panel = TexturedPanel(_peaceLayer);
        panel.AnchorLeft = 1;
        panel.AnchorRight = 1;
        panel.AnchorTop = 0;
        panel.AnchorBottom = 1;
        panel.OffsetLeft = -470;
        panel.OffsetRight = -18;
        panel.OffsetTop = 100;
        panel.OffsetBottom = -100;

        var rows = PanelRows(panel, 20, 16, 8);
        _peaceTitle = Title("PEACE CONFERENCE", 22, Ui.Colour("text.accent"));
        rows.AddChild(_peaceTitle);

        _peaceBudget = Title("", 14, Ui.Colour("text.bright"));
        _peaceBudget.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_peaceBudget);

        _peaceVerdict = Title("", 13, Ui.Colour("state.warning"));
        _peaceVerdict.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_peaceVerdict);

        _peaceHint = Title(
            "Drag the left button across the map to claim ground · drag the right button to give it back · " +
            "click a region below to take or release all of it",
            12, Ui.Colour("text.faint"));
        _peaceHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_peaceHint);

        rows.AddChild(Title("TERRITORY DEMANDED", 11, Ui.Colour("text.caption")));
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200)
        };
        rows.AddChild(scroll);
        _peaceLedgerRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _peaceLedgerRows.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_peaceLedgerRows);

        rows.AddChild(Title("OTHER TERMS", 11, Ui.Colour("text.caption")));
        var terms = new VBoxContainer();
        terms.AddThemeConstantOverride("separation", 3);
        rows.AddChild(terms);
        AddPeaceTerm(terms, "reparations", "Reparations",
            "An indemnity scaled to what their economy can still bear.");
        AddPeaceTerm(terms, "trade", "Trade concessions",
            "A bilateral trade agreement they did not want to sign.");
        AddPeaceTerm(terms, "demilitarise", "Demilitarise them",
            "Their surviving formations give up equipment and cohesion.");
        AddPeaceTerm(terms, "humiliate", "Humiliate them",
            "Read the terms out in public. Three years of lost war support, stability and quiet anger.");
        AddPeaceTerm(terms, "truce", "Ten-year truce",
            "Neither side may declare on the other for a decade instead of five years.");

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 6);
        rows.AddChild(actions);
        var clear = ActionButton("Clear", ClearPeaceDemand, 92, 34);
        clear.TooltipText = "Drop every claim and start the settlement again.";
        actions.AddChild(clear);
        var occupied = ActionButton("Claim held", ClaimOccupiedGround, 118, 34);
        occupied.TooltipText = "Claim every province you currently occupy. The cheapest demand there is.";
        actions.AddChild(occupied);
        var propose = ActionButton("Propose", ProposeWrittenPeace, 108, 34, true);
        propose.TooltipText = "Put the settlement to them.";
        actions.AddChild(propose);
        var cancel = ActionButton("Leave", ClosePeaceConference, 92, 34);
        cancel.TooltipText = "Walk away from the table. The war continues.";
        actions.AddChild(cancel);
    }

    private void AddPeaceTerm(Container parent, string key, string caption, string help)
    {
        var button = ActionButton(caption, () => TogglePeaceTerm(key), 300, 30);
        button.TooltipText = help;
        _peaceTermButtons[key] = button;
        parent.AddChild(button);
    }

    /// <summary>Open the table for a war the player is fighting.</summary>
    private void OpenPeaceConference(StrategicWar war)
    {
        if (war is null || war.Ended || _campaign?.Player is null || !war.Involves(_campaign.Player.Id)) return;
        _peaceWar = war;
        _peaceDemand = new PeaceDemand { WarId = war.Id, ProposerId = _campaign.Player.Id };
        _peaceLedgerModel = new PeaceLedger(_campaign, war, _campaign.Player.Id);
        _peaceLayer.Visible = true;
        _sidePanel.Visible = false;
        SetArmyPanelVisible(false);
        _globe.PaintMode = true;
        _globe.ProvincePainted -= OnProvincePainted;
        _globe.ProvincePainted += OnProvincePainted;
        _campaign.IsPaused = true;
        StyleButton(_pause, true);
        RefreshPeaceConference();
    }

    private void ClosePeaceConference()
    {
        if (_peaceLayer is null) return;
        _peaceLayer.Visible = false;
        _globe.PaintMode = false;
        _globe.ProvincePainted -= OnProvincePainted;
        _globe.ClearPeacePreview();
        _peaceDemand = null;
        _peaceLedgerModel = null;
        _peaceWar = null;
    }

    /// <summary>One brush stroke: claim the province, or hand it back.</summary>
    private void OnProvincePainted(GlobePick pick, bool claiming)
    {
        if (_peaceDemand is null || _peaceLedgerModel is null || pick.ProvinceId <= 0) return;
        var loser = _peaceLedgerModel.Loser;
        if (loser is null) return;

        // Only the counterparty's ground is on the table. Painting your own is a no-op
        // rather than an error, so a stroke across a border does the sensible thing.
        if (_campaign.ProvinceMap.ProvinceToNation[pick.ProvinceId] != (ushort)loser.MapIndex) return;

        var changed = claiming
            ? _peaceDemand.Provinces.Add(pick.ProvinceId)
            : _peaceDemand.Provinces.Remove(pick.ProvinceId);
        if (changed) RefreshPeaceConference();
    }

    private void TogglePeaceTerm(string key)
    {
        if (_peaceDemand is null) return;
        switch (key)
        {
            case "reparations": _peaceDemand.Reparations = !_peaceDemand.Reparations; break;
            case "trade": _peaceDemand.TradeConcessions = !_peaceDemand.TradeConcessions; break;
            case "demilitarise": _peaceDemand.Demilitarise = !_peaceDemand.Demilitarise; break;
            case "humiliate": _peaceDemand.Humiliate = !_peaceDemand.Humiliate; break;
            case "truce": _peaceDemand.LongTruce = !_peaceDemand.LongTruce; break;
        }
        RefreshPeaceConference();
    }

    private void ClearPeaceDemand()
    {
        if (_peaceDemand is null) return;
        _peaceDemand = new PeaceDemand { WarId = _peaceDemand.WarId, ProposerId = _peaceDemand.ProposerId };
        RefreshPeaceConference();
    }

    /// <summary>
    /// The obvious opening bid, one click: everything you are standing on. It is also the
    /// cheapest ground there is, so it is where a settlement usually starts.
    /// </summary>
    private void ClaimOccupiedGround()
    {
        if (_peaceDemand is null || _peaceLedgerModel is null || _peaceWar is null) return;
        var player = _campaign.Player;
        foreach (var province in PeaceConference.Claimable(_campaign, _peaceWar, player.Id))
            if (_campaign.ProvinceController[province] == (ushort)player.MapIndex)
                _peaceDemand.Provinces.Add(province);
        RefreshPeaceConference();
    }

    /// <summary>Take or give back a whole region from the ledger.</summary>
    private void TogglePeaceRegion(int stateMapIndex)
    {
        if (_peaceDemand is null || _peaceLedgerModel is null) return;
        var provinces = PeaceConference.ProvincesOfState(_campaign.ProvinceMap, stateMapIndex)
            .Where(province => _campaign.ProvinceMap.ProvinceToNation[province] ==
                               (ushort)(_peaceLedgerModel.Loser?.MapIndex ?? -1))
            .ToList();
        if (provinces.Count == 0) return;

        var whole = provinces.All(province => _peaceDemand.Provinces.Contains(province));
        foreach (var province in provinces)
            if (whole) _peaceDemand.Provinces.Remove(province);
            else _peaceDemand.Provinces.Add(province);
        RefreshPeaceConference();
    }

    private void ProposeWrittenPeace()
    {
        if (_peaceDemand is null || _peaceWar is null) return;
        if (PeaceConference.Resolve(_campaign, _peaceWar, _peaceDemand))
        {
            var summary = _peaceWar.PeaceSummary;
            ClosePeaceConference();
            SetArmyFeedback(summary);
            Refresh();
            return;
        }
        RefreshPeaceConference();
    }

    /// <summary>Re-price the demand, redraw the ledger, and repaint the border on the map.</summary>
    private void RefreshPeaceConference()
    {
        if (_peaceLayer is null || !_peaceLayer.Visible || _peaceDemand is null ||
            _peaceLedgerModel is null || _peaceWar is null) return;

        var player = _campaign.Player;
        var loser = _peaceLedgerModel.Loser;
        _peaceTitle.Text = $"PEACE WITH {loser?.Name?.ToUpperInvariant() ?? "?"}";

        var budget = _peaceLedgerModel.Budget;
        var cost = _peaceLedgerModel.Cost(_peaceDemand);
        _peaceBudget.Text = $"This war is worth {budget:0.0} · you are asking for {cost:0.0}";
        _peaceBudget.AddThemeColorOverride("font_color",
            cost > budget ? Ui.Colour("state.negative") : Ui.Colour("state.positive"));

        var block = PeaceConference.BlockReason(_campaign, _peaceWar, _peaceDemand);
        _peaceVerdict.Text = block.Length == 0
            ? $"{loser?.Name} will sign this."
            : block;
        _peaceVerdict.AddThemeColorOverride("font_color",
            block.Length == 0 ? Ui.Colour("state.positive") : Ui.Colour("state.warning"));

        foreach (var (key, button) in _peaceTermButtons)
            StyleButton(button, key switch
            {
                "reparations" => _peaceDemand.Reparations,
                "trade" => _peaceDemand.TradeConcessions,
                "demilitarise" => _peaceDemand.Demilitarise,
                "humiliate" => _peaceDemand.Humiliate,
                "truce" => _peaceDemand.LongTruce,
                _ => false
            });

        RefreshPeaceLedgerRows();
        _globe.SetPeacePreview(_peaceDemand.Provinces, player.MapIndex);
    }

    private void RefreshPeaceLedgerRows()
    {
        foreach (var child in _peaceLedgerRows.GetChildren())
        {
            _peaceLedgerRows.RemoveChild(child);
            child.QueueFree();
        }

        if (_peaceDemand.Provinces.Count == 0)
        {
            _peaceLedgerRows.AddChild(Title("Nothing claimed yet.", 12, Ui.Colour("text.faint")));
            return;
        }

        var topology = _campaign.ProvinceMap;
        var groups = _peaceDemand.Provinces
            .GroupBy(province => (int)topology.ProvinceToState[province])
            .Select(group => new
            {
                State = _campaign.States.Find(state => state.MapIndex == group.Key),
                MapIndex = group.Key,
                Claimed = group.Count(),
                Cost = group.Sum(province => _peaceLedgerModel.Price(province).Cost),
                Total = _peaceLedgerModel.ProvincesPerState.GetValueOrDefault(group.Key)
            })
            .OrderByDescending(entry => entry.Cost)
            .ToList();

        foreach (var entry in groups.Take(40))
        {
            var name = entry.State?.Name ?? $"region {entry.MapIndex}";
            var whole = entry.Total > 0 && entry.Claimed >= entry.Total;
            var row = ActionButton(
                $"{(whole ? "▣" : "▤")} {name}  {entry.Claimed}/{entry.Total}  ·  {entry.Cost:0.0}",
                () => TogglePeaceRegion(entry.MapIndex), 0, 26);
            row.Alignment = HorizontalAlignment.Left;
            row.ClipText = true;
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.TooltipText = whole
                ? $"All {entry.Total} provinces of {name} are on the table. Click to give the region back."
                : $"{entry.Claimed} of {entry.Total} provinces of {name}. Click to demand the whole region.";
            _peaceLedgerRows.AddChild(row);
        }

        if (groups.Count > 40)
            _peaceLedgerRows.AddChild(Title($"…and {groups.Count - 40} more regions",
                11, Ui.Colour("text.faint")));
    }
}
