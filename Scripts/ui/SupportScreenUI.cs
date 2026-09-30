using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

// The long-range forces screen: what a country reaches with rather than stands on.
//
// HOI4 taught this page its shape. The left column is the establishment itself - every
// formation the country owns, grouped by the region it is based in, each with its
// readiness, its mission and the reach of its type. The right column is where the next
// one comes from: raise a formation into a region with a post free, watch it train, and
// when the war moves, rebase the ones whose ground was taken.
public partial class DealOrForceGame
{
    private Control _supportLayer = null!;
    private Label _supportSummary = null!;
    private ScrollContainer _supportScroll = null!;
    private VBoxContainer _supportRows = null!;
    private Label _supportFeedback = null!;
    private OptionButton _supportTypePicker = null!;
    private OptionButton _supportRegionPicker = null!;
    private Label _supportCostReadout = null!;
    private Label _supportTypeDetail = null!;
    private VBoxContainer _supportTrainingRows = null!;
    private VBoxContainer _supportRebaseBox = null!;
    private Label _supportRebaseHint = null!;
    private ScrollContainer _supportRebaseScroll = null!;
    private VBoxContainer _supportRebaseRows = null!;
    private bool _supportRowsBuilt;

    /// <summary>The formation being moved, or -1. Rebase is a two-click thought: pick the
    /// formation, then pick the ground - and the ground is a list, because the map is not
    /// where a player's eyes are when they are on this page.</summary>
    private int _supportRebasingId = -1;

    private sealed class SupportRow
    {
        public Button Row;
        public TextureRect Icon;
        public Label Name;
        public ColorRect StrengthFill;
        public Label Strength;
        public Label Range;
        public OptionButton Mission;
        public Button View;
        public Button Move;
        public Label Displaced;
        public SupportFormation Formation;
        public bool MissionBuilt;
    }

    private sealed class SupportTrainingRow
    {
        public Label Name;
        public ColorRect ProgressFill;
        public Label Progress;
        public Button Cancel;
        public SupportOrder Order;
    }

    private readonly Dictionary<int, SupportRow> _supportRowMap = new();
    private readonly Dictionary<int, SupportTrainingRow> _supportTrainingMap = new();
    private readonly Dictionary<int, VBoxContainer> _supportRegionSections = new();
    private readonly List<Control> _supportRebaseButtons = new();

    // ------------------------------------------------------------------ build

    private void BuildSupportScreen()
    {
        _supportLayer = FullLayer("Support");
        _supportLayer.Visible = false;
        _hudLayer.AddChild(_supportLayer);

        var shade = new ColorRect
        {
            Color = new Color(0.015f, 0.026f, 0.041f, 0.48f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _supportLayer.AddChild(shade);

        var panel = TexturedPanel(_supportLayer);
        Ui.Place(panel, "modal.support");

        var rows = PanelRows(panel, 22, 16, 9);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        var title = Title("SUPPORT", 25, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(ActionButton("×", CloseSupportScreen, 38, 32));

        _supportSummary = Title("", 13, Ui.Colour("text.body_soft"));
        _supportSummary.CustomMinimumSize = new Vector2(0, 22);
        _supportSummary.AutowrapMode = TextServer.AutowrapMode.Off;
        _supportSummary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        rows.AddChild(_supportSummary);

        var body = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        body.AddThemeConstantOverride("separation", 14);
        rows.AddChild(body);

        // ---- the establishment, on the left.
        var establishment = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        establishment.AddThemeConstantOverride("separation", 6);
        body.AddChild(establishment);

        StateSection(establishment, "THE LONG-RANGE ESTABLISHMENT",
            "Every formation the country owns, by the region it is based in. The reach is in real " +
            "kilometres from its base province, so where a formation stands decides what it can " +
            "touch. A region hosts two formations, and two more per barracks in it. Lose the " +
            "region and the formation goes silent until it is moved.");

        _supportScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true
        };
        establishment.AddChild(_supportScroll);
        _supportRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportRows.AddThemeConstantOverride("separation", 3);
        _supportScroll.AddChild(_supportRows);

        // ---- the right column: raising, training, and where a rebasing formation goes.
        var side = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(360, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        side.AddThemeConstantOverride("separation", 10);
        body.AddChild(side);

        _supportRebaseBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Visible = false
        };
        _supportRebaseBox.AddThemeConstantOverride("separation", 6);
        side.AddChild(_supportRebaseBox);
        var rebaseHeader = new HBoxContainer();
        rebaseHeader.AddThemeConstantOverride("separation", 6);
        _supportRebaseBox.AddChild(rebaseHeader);
        var rebaseTitle = Title("REBASE THE FORMATION", 12, Ui.Colour("text.caption"));
        rebaseTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rebaseHeader.AddChild(rebaseTitle);
        rebaseHeader.AddChild(ActionButton("×", CancelSupportRebase, 30, 24));
        _supportRebaseHint = Title("", 11, Ui.Colour("text.body_soft"));
        _supportRebaseHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportRebaseHint.CustomMinimumSize = new Vector2(1, 18);
        _supportRebaseBox.AddChild(_supportRebaseHint);
        _supportRebaseScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        _supportRebaseBox.AddChild(_supportRebaseScroll);
        _supportRebaseRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportRebaseRows.AddThemeConstantOverride("separation", 2);
        _supportRebaseScroll.AddChild(_supportRebaseRows);

        var raiseBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        raiseBox.AddThemeConstantOverride("separation", 6);
        _raiseBox = raiseBox;
        side.AddChild(raiseBox);

        StateSection(raiseBox, "RAISE A FORMATION",
            "A new formation is ordered into a region the country holds, costs money and " +
            "manpower up front like a division, and trains for the days its type states " +
            "before it exists.");

        var raiseGrid = new GridContainer
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        raiseGrid.AddThemeConstantOverride("h_separation", 8);
        raiseGrid.AddThemeConstantOverride("v_separation", 5);
        raiseBox.AddChild(raiseGrid);

        Label Field(string text)
        {
            var label = RowLabel(text, 11, Ui.Colour("text.caption"), 96);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.AutowrapMode = TextServer.AutowrapMode.Off;
            return label;
        }

        raiseGrid.AddChild(Field(Tr("FORMATION")));
        _supportTypePicker = new OptionButton
        {
            CustomMinimumSize = new Vector2(230, 28),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FitToLongestItem = false
        };
        raiseGrid.AddChild(_supportTypePicker);
        _supportTypePicker.ItemSelected += OnSupportTypePicked;

        raiseGrid.AddChild(Field(Tr("REGION")));
        _supportRegionPicker = new OptionButton
        {
            CustomMinimumSize = new Vector2(230, 28),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FitToLongestItem = false
        };
        raiseGrid.AddChild(_supportRegionPicker);
        _supportRegionPicker.ItemSelected += _ => RefreshSupportRaiseReadout();

        _supportTypeDetail = Title("", 11, Ui.Colour("text.body_soft"));
        _supportTypeDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportTypeDetail.CustomMinimumSize = new Vector2(1, 16);
        raiseBox.AddChild(_supportTypeDetail);

        _supportCostReadout = Title("", 12, Ui.Colour("text.accent_dim"));
        _supportCostReadout.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportCostReadout.CustomMinimumSize = new Vector2(1, 16);
        raiseBox.AddChild(_supportCostReadout);

        var raiseActions = new HBoxContainer();
        raiseActions.AddThemeConstantOverride("separation", 7);
        raiseBox.AddChild(raiseActions);
        var raiseButton = ActionButton("RAISE FORMATION", RaiseSupportFormation, 150, 30);
        raiseActions.AddChild(raiseButton);
        _supportFeedback = Title("", 11, Ui.Colour("state.negative"));
        _supportFeedback.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _supportFeedback.VerticalAlignment = VerticalAlignment.Center;
        _supportFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _supportFeedback.CustomMinimumSize = new Vector2(1, 18);
        raiseActions.AddChild(_supportFeedback);

        var trainingBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        trainingBox.AddThemeConstantOverride("separation", 6);
        side.AddChild(trainingBox);
        _supportTrainingBox = trainingBox;
        StateSection(trainingBox, "IN TRAINING",
            "Formations on order. Cancel returns most of what is left of the money and all " +
            "of the men.");
        _supportTrainingRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _supportTrainingRows.AddThemeConstantOverride("separation", 3);
        trainingBox.AddChild(_supportTrainingRows);
    }

    private Control _raiseBox = null!;
    private Control _supportTrainingBox = null!;

    // ------------------------------------------------------------------ open, close, refresh

    private void OpenSupportScreen()
    {
        _openScreen = "Support";
        _sidePanel.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_warLedgerLayer is not null) _warLedgerLayer.Visible = false;
        if (_stateAffairsLayer is not null) _stateAffairsLayer.Visible = false;
        if (_productionLayer is not null) _productionLayer.Visible = false;
        if (_diplomacyLayer is not null) _diplomacyLayer.Visible = false;
        if (_tradeScreenLayer is not null) _tradeScreenLayer.Visible = false;
        _supportLayer.Visible = true;
        _supportLayer.MoveToFront();
        RefreshSupportScreen();
        UpdateScreenButtons();
    }

    private void CloseSupportScreen()
    {
        _supportLayer.Visible = false;
        _supportRebasingId = -1;
        if (_openScreen == "Support") _openScreen = "";
        UpdateScreenButtons();
    }

    private void OnSupportPicked(SupportFormation formation)
    {
        if (_campaign?.Player is null || formation.NationId != _campaign.Player.Id) return;
        _globe.SelectSupportFormation(formation.Id);
        OpenSupportScreen();
        if (_supportRowMap.TryGetValue(formation.Id, out var row))
            _supportScroll.EnsureControlVisible(row.Row);
    }

    private void RefreshSupportScreen()
    {
        if (_supportLayer is null || !_supportLayer.Visible || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var owned = _campaign.SupportFormations.Where(f => f.NationId == player.Id).ToList();
        var training = _campaign.SupportOrders.Where(o => o.NationId == player.Id).ToList();

        _supportSummary.Text = Loc.Format("{0} · {1} · {2}",
            player.DisplayName,
            Loc.Format(Simulation.Plural.Key("{0} formation", owned.Count), owned.Count),
            Loc.Format(Simulation.Plural.Key("{0} in training", training.Count), training.Count));

        RefreshSupportEstablishment(player, owned);
        RefreshSupportTraining(training);
        RefreshSupportRebase(player);
        RefreshSupportRaise(player);
    }

    // ------------------------------------------------------------------ the establishment

    private void RefreshSupportEstablishment(Nation player, List<SupportFormation> owned)
    {
        // The rows are kept across refreshes and repainted, the way the trade matrix is:
        // a list a player is reading should not be torn out from under their eyes every
        // day the campaign steps.
        //
        // A section exists for every region that hosts formations of this country - the
        // held ones in name order, and after them the regions the war has taken, because a
        // formation standing on lost ground is exactly the one the player most needs to
        // find and move.
        var byState = owned.GroupBy(f => f.StateMapIndex)
            .Select(group => (MapIndex: group.Key,
                State: _campaign.States.Find(s => s.MapIndex == group.Key),
                Formations: group.ToList()))
            .Where(entry => entry.State is not null)
            .OrderByDescending(entry => entry.State.NationMapIndex == player.MapIndex)
            .ThenBy(entry => Loc.Get(entry.State.Name))
            .ToList();

        // Region sections in the order the player reads them, with the formations inside
        // each. Sections for regions that no longer exist are dropped; the rows inside keep
        // their scroll and their tooltips.
        var wanted = new HashSet<int>();
        foreach (var entry in byState)
        {
            var state = entry.State;
            var formations = entry.Formations;
            wanted.Add(state.MapIndex);
            if (!_supportRegionSections.TryGetValue(state.MapIndex, out var section))
            {
                section = BuildSupportRegionSection();
                _supportRegionSections[state.MapIndex] = section;
                _supportRows.AddChild(section);
            }
            section.GetNode<Label>("Caption").Text =
                $"{Loc.Get(state.Name)} · {Loc.Format("posts {0}/{1}",
                    SupportCore.PostsUsed(_campaign, state.MapIndex), SupportCore.CapacityOf(_campaign, state))}";
            var rowList = section.GetNode<VBoxContainer>("Formations");
            foreach (var formation in formations)
            {
                if (!_supportRowMap.TryGetValue(formation.Id, out var row))
                {
                    row = BuildSupportRow(player, formation);
                    _supportRowMap[formation.Id] = row;
                }
                // A rebased formation belongs to a new section; its row travels with it.
                if (row.Row.GetParent() != rowList)
                {
                    row.Row.GetParent()?.RemoveChild(row.Row);
                    rowList.AddChild(row.Row);
                }
                PaintSupportRow(row, formation, state);
            }
        }
        foreach (var (mapIndex, section) in _supportRegionSections.ToArray())
        {
            if (wanted.Contains(mapIndex)) continue;
            _supportRegionSections.Remove(mapIndex);
            section.QueueFree();
        }
        foreach (var (id, row) in _supportRowMap.ToArray())
        {
            var formation = owned.Find(f => f.Id == id);
            // Gone from the country, or freed with a section whose region has stopped
            // existing - both leave a row that would never be painted again.
            if (formation is null || row.Row.GetParent() is null || !wanted.Contains(formation.StateMapIndex))
            {
                _supportRowMap.Remove(id);
                row.Row.QueueFree();
            }
        }
    }

    private VBoxContainer BuildSupportRegionSection()
    {
        var section = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        section.AddThemeConstantOverride("separation", 2);
        section.Name = "Section";
        var caption = Title("", 12, Ui.Colour("text.caption"));
        caption.Name = "Caption";
        caption.AutowrapMode = TextServer.AutowrapMode.Off;
        caption.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        caption.CustomMinimumSize = new Vector2(1, 18);
        section.AddChild(caption);
        var formations = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        formations.Name = "Formations";
        formations.AddThemeConstantOverride("separation", 3);
        section.AddChild(formations);
        return section;
    }

    private SupportRow BuildSupportRow(Nation player, SupportFormation formation)
    {
        var row = new SupportRow { Formation = formation };

        // A Button is not a container: the row's contents need anchors of their own, or
        // they collapse to nothing - the same lesson every row in this shell has taught.
        var button = new Button
        {
            CustomMinimumSize = new Vector2(0, 40),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            ActionMode = BaseButton.ActionModeEnum.Press
        };
        button.AddThemeStyleboxOverride("normal", ShellRowStyle(false));
        button.AddThemeStyleboxOverride("hover", ShellRowStyle(true));
        button.AddThemeStyleboxOverride("pressed", ShellRowStyle(false));
        button.AddThemeStyleboxOverride("disabled", ShellRowStyle(false));
        button.MouseFilter = MouseFilterEnum.Ignore;
        row.Row = button;

        var inner = new HBoxContainer();
        inner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        inner.OffsetLeft = 9;
        inner.OffsetTop = 3;
        inner.OffsetRight = -9;
        inner.OffsetBottom = -3;
        inner.AddThemeConstantOverride("separation", 8);
        button.AddChild(inner);

        row.Icon = new TextureRect
        {
            CustomMinimumSize = new Vector2(26, 26),
            ExpandMode = Godot.TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = Godot.TextureRect.StretchModeEnum.KeepAspectCentered
        };
        row.Icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        inner.AddChild(row.Icon);

        var nameBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        nameBox.AddThemeConstantOverride("separation", 0);
        inner.AddChild(nameBox);

        row.Name = Title("", 12, Ui.Colour("text.bright"));
        row.Name.AutowrapMode = TextServer.AutowrapMode.Off;
        row.Name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        row.Name.CustomMinimumSize = new Vector2(1, 16);
        nameBox.AddChild(row.Name);

        row.Displaced = Title("", 10, Ui.Colour("state.negative"));
        row.Displaced.AutowrapMode = TextServer.AutowrapMode.Off;
        row.Displaced.CustomMinimumSize = new Vector2(1, 12);
        row.Displaced.Visible = false;
        nameBox.AddChild(row.Displaced);

        // The readiness bar: what share of the formation is crews, airframes and barrels
        // that work. Arms on the national market decide how fast it climbs back.
        var strengthBox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(78, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        inner.AddChild(strengthBox);
        var bar = new ColorRect
        {
            Color = Ui.Colour("state.negative"),
            CustomMinimumSize = new Vector2(78, 5)
        };
        strengthBox.AddChild(bar);
        var strength = Title("", 10, Ui.Colour("text.body_soft"));
        strength.AutowrapMode = TextServer.AutowrapMode.Off;
        strength.CustomMinimumSize = new Vector2(78, 12);
        strengthBox.AddChild(strength);
        row.Strength = strength;
        row.StrengthFill = bar;

        row.Range = Title("", 10, Ui.Colour("text.body_soft"));
        row.Range.AutowrapMode = TextServer.AutowrapMode.Off;
        row.Range.CustomMinimumSize = new Vector2(64, 14);
        row.Range.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        inner.AddChild(row.Range);

        row.Mission = new OptionButton
        {
            CustomMinimumSize = new Vector2(132, 26),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FitToLongestItem = false
        };
        inner.AddChild(row.Mission);
        row.Mission.ItemSelected += index =>
        {
            var mission = row.Mission.GetItemMetadata((int)index).AsString();
            if (row.Formation is not { } current) return;
            _campaign.Commands.Enqueue(new SetSupportMissionCommand
            {
                IssuerId = _campaign.Player.Id,
                FormationId = current.Id,
                Mission = mission
            });
            PlayStrategicCue("support_mission");
        };

        row.View = ActionButton("RANGE", () => ShowSupportRange(row.Formation.Id), 70, 26);
        row.View.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.View.TooltipText = Ui.Tip("Show this formation and its operational reach on the map.");
        inner.AddChild(row.View);

        row.Move = ActionButton("MOVE", () => BeginSupportRebase(row.Formation.Id), 62, 26);
        row.Move.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        inner.AddChild(row.Move);

        button.TooltipText = "";
        return row;
    }

    private void PaintSupportRow(SupportRow row, SupportFormation formation, ScenarioState state)
    {
        if (!SupportTypeCatalog.TryGet(formation.TypeId, out var type)) return;
        row.Formation = formation;
        row.View.Text = _globe.SelectedSupportId == formation.Id ? "VIEWING" : "RANGE";

        var nth = _campaign.SupportFormations
            .Count(f => f.NationId == formation.NationId && f.TypeId == formation.TypeId && f.Id <= formation.Id);
        row.Name.Text = $"{Loc.Get(type.Name)} {ToRomanOrNumber(nth)}";
        row.Icon.Texture = Art($"icon_{type.Icon}",
            $"res://game/gfx/interface/icons/icon_{type.Icon}.png");

        row.StrengthFill.AnchorRight = M.Clamp01(formation.Strength);
        row.StrengthFill.Color = Ui.Colour(
            formation.Strength > 0.75f ? "state.positive" :
            formation.Strength > 0.35f ? "state.warning" : "state.negative");
        row.Strength.Text = $"{Loc.Get("strength")} {formation.Strength:P0} · {Loc.Get("experience")} {formation.Experience:P0}";
        row.Range.Text = Loc.Format("{0} km", type.RangeKm);

        var displaced = SupportCore.Displaced(_campaign, formation);
        row.Displaced.Visible = displaced;
        row.Displaced.Text = Loc.Get("BASE LOST — MOVE IT OR IT STANDS SILENT");

        if (!row.MissionBuilt)
        {
            row.MissionBuilt = true;
            var written = new HashSet<string>();
            foreach (var mission in type.Missions)
            {
                if (!written.Add(mission)) continue;
                var index = row.Mission.ItemCount;
                row.Mission.AddItem(Loc.Get(SupportMissionName(mission)));
                row.Mission.SetItemMetadata(index, mission);
            }
        }
        // Metadata holds the mission ID itself, so modded catalogue rows are never
        // misread as a built-in mission merely because their index was unknown.
        var current = 0;
        for (var index = 0; index < row.Mission.ItemCount; index++)
            if (row.Mission.GetItemMetadata(index).AsString() == formation.Mission)
                current = index;
        row.Mission.Select(current);

        row.Row.TooltipText = Ui.Tip(
            $"{Loc.Get(type.Name)}\n" +
            Loc.Format("reach {0} km from {1}", type.RangeKm, Loc.Get(state.Name)) + "\n" +
            Loc.Format("fire {0:0.#} · air superiority {1:0.#} · interdiction {2:0.#} · air defence {3:0.#} · recon {4:0.#}",
                type.SupportFire, type.AirSuperiority, type.Interdiction, type.AirDefence, type.Recon));
    }

    private static string SupportMissionName(string mission) => mission switch
    {
        SupportCore.MissionStandby => "Standby",
        SupportCore.MissionCloseAirSupport => "Close support",
        SupportCore.MissionAirSuperiority => "Air superiority",
        SupportCore.MissionInterdiction => "Interdiction",
        _ => mission
    };

    private void ShowSupportRange(int formationId)
    {
        _globe.FocusSupportFormation(formationId);
        CloseSupportScreen();
    }

    private static string ToRomanOrNumber(int n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        _ => n.ToString()
    };

    // ------------------------------------------------------------------ in training

    private void RefreshSupportTraining(List<SupportOrder> training)
    {
        foreach (var order in training)
        {
            if (!_supportTrainingMap.TryGetValue(order.Id, out var row))
            {
                var box = new HBoxContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    CustomMinimumSize = new Vector2(0, 26)
                };
                box.AddThemeConstantOverride("separation", 7);
                var name = Title("", 11, Ui.Colour("text.bright"));
                name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                name.AutowrapMode = TextServer.AutowrapMode.Off;
                name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                box.AddChild(name);
                var progress = new ColorRect
                {
                    Color = Ui.Colour("state.positive"),
                    CustomMinimumSize = new Vector2(74, 5)
                };
                box.AddChild(progress);
                var percent = Title("", 10, Ui.Colour("text.body_soft"));
                percent.AutowrapMode = TextServer.AutowrapMode.Off;
                percent.CustomMinimumSize = new Vector2(38, 14);
                percent.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                box.AddChild(percent);
                var cancel = ActionButton("×", () => CancelSupportOrder(order.Id), 26, 24);
                box.AddChild(cancel);
                _supportTrainingRows.AddChild(box);
                row = new SupportTrainingRow
                {
                    Name = name, ProgressFill = progress, Progress = percent, Cancel = cancel, Order = order
                };
                _supportTrainingMap[order.Id] = row;
            }
            var state = _campaign.States.Find(s => s.MapIndex == order.StateMapIndex);
            var type = SupportTypeCatalog.Get(order.TypeId);
            row.Name.Text = $"{Loc.Get(type?.Name ?? order.TypeId)} · {Loc.Get(state?.Name ?? "")}";
            var fraction = M.Clamp01(order.TrainedDays / (float)Math.Max(1, order.DurationDays));
            row.ProgressFill.AnchorRight = fraction;
            row.ProgressFill.Color = Ui.Colour("state.positive");
            row.Progress.Text = $"{fraction:P0}";
            row.Order = order;
        }
        foreach (var (id, row) in _supportTrainingMap.ToArray())
        {
            if (training.Any(o => o.Id == id)) continue;
            _supportTrainingMap.Remove(id);
            row.Cancel.GetParent().QueueFree();
        }
    }

    private void CancelSupportOrder(int orderId)
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new CancelSupportOrderCommand
        {
            IssuerId = _campaign.Player.Id,
            OrderId = orderId
        });
        _supportFeedback.Text = "";
    }

    // ------------------------------------------------------------------ raising

    private void RefreshSupportRaise(Nation player)
    {
        if (!_supportRowsBuilt)
        {
            _supportRowsBuilt = true;
            _supportTypePicker.Clear();
            foreach (var type in SupportTypeCatalog.All.OrderBy(t => t.Kind).ThenBy(t => Loc.Get(t.Name)))
            {
                var index = _supportTypePicker.ItemCount;
                _supportTypePicker.AddItem(Loc.Get(type.Name));
                _supportTypePicker.SetItemMetadata(index, type.Id);
            }
            if (_supportTypePicker.ItemCount > 0) _supportTypePicker.Select(0);
        }

        // The region picker follows the map: regions are gained and lost, and a picker
        // that offered a region the country no longer holds would be a promise the
        // campaign refuses at the door.
        var selected = _supportRegionPicker.Selected >= 0
            ? _supportRegionPicker.GetItemMetadata(_supportRegionPicker.Selected).AsInt32() : -1;
        _supportRegionPicker.Clear();
        var option = 0;
        foreach (var state in _campaign.States
                     .Where(s => s.NationMapIndex == player.MapIndex)
                     .OrderBy(s => Loc.Get(s.Name)))
        {
            var free = SupportCore.CapacityOf(_campaign, state) -
                        SupportCore.PostsUsed(_campaign, state.MapIndex) -
                        _campaign.SupportOrders.Count(o => o.NationId == player.Id && o.StateMapIndex == state.MapIndex);
            _supportRegionPicker.AddItem($"{Loc.Get(state.Name)} ({free})");
            _supportRegionPicker.SetItemMetadata(option, state.MapIndex);
            if (state.MapIndex == selected) _supportRegionPicker.Select(option);
            option++;
        }
        if (_supportRegionPicker.Selected < 0 && _supportRegionPicker.ItemCount > 0) _supportRegionPicker.Select(0);
        if (_supportRegionPicker.ItemCount == 0) _supportRegionPicker.AddItem(Loc.Get("No regions held"));

        RefreshSupportRaiseReadout();
    }

    private void OnSupportTypePicked(long _) => RefreshSupportRaiseReadout();

    private (string TypeId, int StateMapIndex) SupportRaiseChoice()
    {
        var typeId = _supportTypePicker.Selected >= 0 &&
                      _supportTypePicker.GetItemMetadata(_supportTypePicker.Selected).VariantType == Variant.Type.String
            ? _supportTypePicker.GetItemMetadata(_supportTypePicker.Selected).AsString() : "";
        // The "No regions held" placeholder carries no metadata, and a NIL variant would
        // read as region zero - which is a real region somebody owns.
        var stateMapIndex = _supportRegionPicker.Selected >= 0 &&
                            _supportRegionPicker.GetItemMetadata(_supportRegionPicker.Selected).VariantType == Variant.Type.Int
            ? _supportRegionPicker.GetItemMetadata(_supportRegionPicker.Selected).AsInt32() : -1;
        return (typeId, stateMapIndex);
    }

    private void RefreshSupportRaiseReadout()
    {
        if (_campaign?.Player is null) return;
        var player = _campaign.Player;
        var (typeId, _) = SupportRaiseChoice();
        if (!SupportTypeCatalog.TryGet(typeId, out var type)) return;

        _supportTypeDetail.Text =
            Loc.Format("fire {0:0.#} · reach {1:0} km · recon {2:0.#}",
                type.SupportFire, type.RangeKm, type.Recon) +
            (type.AirSuperiority > 0 ? " · " + Loc.Format("air superiority {0:0.#}", type.AirSuperiority) : "") +
            (type.Interdiction > 0 ? " · " + Loc.Format("interdiction {0:0.#}", type.Interdiction) : "") +
            (type.AirDefence > 0 ? " · " + Loc.Format("air defence {0:0.#}", type.AirDefence) : "");

        var cost = SupportCore.CostOf(player, type);
        var days = SupportCore.BuildDaysOf(player, type);
        var affordable = player.Treasury >= cost && player.ManpowerReserve >= type.Personnel;
        _supportCostReadout.Text = Loc.Format("{0} · {1} · {2} days",
            Fmt.Money(cost), Fmt.People(type.Personnel), days);
        _supportCostReadout.AddThemeColorOverride("font_color",
            Ui.Colour(affordable ? "text.accent_dim" : "state.negative"));
    }

    private void RaiseSupportFormation()
    {
        if (_campaign?.Player is null) return;
        var (typeId, stateMapIndex) = SupportRaiseChoice();
        var command = new RaiseSupportCommand
        {
            IssuerId = _campaign.Player.Id,
            StateMapIndex = stateMapIndex,
            TypeId = typeId
        };
        // Validate before the wire: a refusal the player can read now beats one that
        // arrives with the next campaign step.
        var refusal = SupportCore.Validate(_campaign, command.IssuerId, stateMapIndex, typeId);
        _supportFeedback.Text = refusal.Length > 0 ? Loc.Get(refusal) : "";
        if (refusal.Length > 0) return;
        _campaign.Commands.Enqueue(command);
        PlayStrategicCue("support_order");
    }

    // ------------------------------------------------------------------ rebasing

    private void BeginSupportRebase(int formationId)
    {
        _supportRebasingId = formationId;
        RefreshSupportScreen();
    }

    private void CancelSupportRebase()
    {
        _supportRebasingId = -1;
        RefreshSupportScreen();
    }

    private void RefreshSupportRebase(Nation player)
    {
        var formation = _campaign.SupportFormations.Find(f => f.Id == _supportRebasingId && f.NationId == player.Id);
        _supportRebaseBox.Visible = formation is not null;
        _raiseBox.Visible = formation is null;
        _supportTrainingBox.Visible = formation is null;
        if (formation is null) return;

        var type = SupportTypeCatalog.Get(formation.TypeId);
        _supportRebaseHint.Text = Loc.Format("{0} · reach {1:0} km from its base", Loc.Get(type?.Name ?? ""), type?.RangeKm ?? 0);

        foreach (var button in _supportRebaseButtons) button.QueueFree();
        _supportRebaseButtons.Clear();
        foreach (var state in _campaign.States
                     .Where(s => s.NationMapIndex == player.MapIndex)
                     .OrderBy(s => Loc.Get(s.Name)))
        {
            var free = SupportCore.CapacityOf(_campaign, state) -
                        SupportCore.PostsUsed(_campaign, state.MapIndex, formation.Id);
            var captured = state.MapIndex;
            var button = ActionButton(
                $"{Loc.Get(state.Name)} · {Loc.Format("posts {0}/{1}", SupportCore.PostsUsed(_campaign, state.MapIndex, formation.Id), SupportCore.CapacityOf(_campaign, state))}",
                () => MoveSupportFormation(captured), 0, 26);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Disabled = free <= 0 && state.MapIndex != formation.StateMapIndex;
            _supportRebaseRows.AddChild(button);
            _supportRebaseButtons.Add(button);
        }
    }

    private void MoveSupportFormation(int stateMapIndex)
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new RebaseSupportCommand
        {
            IssuerId = _campaign.Player.Id,
            FormationId = _supportRebasingId,
            StateMapIndex = stateMapIndex
        });
        _supportRebasingId = -1;
    }
}
