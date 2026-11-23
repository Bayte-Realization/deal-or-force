using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>One war in the list of the world's wars, kept so it can be refreshed in place.</summary>
    private sealed class WarLedgerRow
    {
        public PanelContainer Card;
        public Button MainButton;
        public Control AttackerFlag;
        public Control DefenderFlag;
        public HBoxContainer AttackerCluster;
        public HBoxContainer DefenderCluster;
        public Label Attacker;
        public Label AttackerSide;
        public Label Defender;
        public Label DefenderSide;
        public Label Heading;
        public Label ScoreBadge;
        public WarBar Bar;
        public Label Standing;
        public Label Losses;
        public Label Divisions;
        public Label Support;
        public Label Age;
        public Button JumpBtn;
        public Button PeaceBtn;
        public int WarId = -1;
        public int Styled = -1;
        public string ClusterSig = "";
    }

    private Control _warLedgerLayer = null!;
    private PanelContainer _warLedgerPanel = null!;
    private float _warLedgerPlacedBottom;
    private Label _warLedgerTitle = null!;
    private Label _warLedgerSummary = null!;
    private PanelContainer _warReadouts = null!;
    private readonly Dictionary<string, Label> _warReadoutValues = new();
    private Label _warLedgerEmpty = null!;
    private VBoxContainer _warListRows = null!;
    private VBoxContainer _warDetailRows = null!;
    private ScrollContainer _warListScroll = null!;
    private ScrollContainer _warDetailScroll = null!;
    private Button _warFilterAll = null!;
    private Button _warFilterMine = null!;
    private Button _warBackButton = null!;
    private readonly List<WarLedgerRow> _warLedgerRows = new();
    private readonly List<StrategicWar> _warLedgerOrder = new();
    private VBoxContainer _warDetailActions = null!;
    private bool _warLedgerMineOnly;
    private int _warLedgerOpenWarId = -1;
    private int _warDetailDay = int.MinValue;
    private int _warDetailBuiltFor = -1;

    /// <summary>How lopsided a war has to be before its front line reaches the end of the bar.</summary>
    private static float WarLedgerBarScale => Ui.Metric("war_ledger.bar_scale", 100f);

    /// <summary>The same for one part of a war's score, which is a fraction of the whole.</summary>
    private static float WarLedgerPartScale => Ui.Metric("war_ledger.part_scale", 20f);

    private static float WarCardHeight => Ui.Metric("war_ledger.card_height", 92f);

    /// <summary>
    /// The world's wars, as a scannable war room board: framed coalition flag clusters,
    /// centered high-contrast war-score tug bar, tabular casualties/divisions/exhaustion KPIs,
    /// and direct Front-jump and Peace Conference actions.
    /// </summary>
    private void BuildWarLedger()
    {
        _warLedgerLayer = FullLayer("WarLedger");
        _warLedgerLayer.Visible = false;
        _hudLayer.AddChild(_warLedgerLayer);

        var shade = new ColorRect
        {
            Color = Ui.Colour("surface.overlay"),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _warLedgerLayer.AddChild(shade);

        _warLedgerPanel = TexturedPanel(_warLedgerLayer);
        _warLedgerPanel.ClipContents = true;
        ChromePlateArt.AttachSurfaceTexture(_warLedgerPanel, "blueprint");
        Ui.Place(_warLedgerPanel, "modal.war_ledger");
        _warLedgerPlacedBottom = _warLedgerPanel.OffsetBottom;

        var rows = PanelRows(_warLedgerPanel, 20, 14, 9);
        rows.ClipContents = true;

        _warBackButton = ActionButton("← All wars", ShowWarLedgerList, 118, 30);
        _warBackButton.Visible = false;
        _warFilterAll = ActionButton("Every war", () => SetWarLedgerFilter(false), 108, 30, true);
        _warFilterAll.SetMeta("atlas_tab_button", true);
        StyleButton(_warFilterAll, true);
        _warFilterMine = ActionButton("Mine", () => SetWarLedgerFilter(true), 78, 30);
        _warFilterMine.SetMeta("atlas_tab_button", true);
        StyleButton(_warFilterMine, false);
        var toConflicts = ActionButton("Conflicts", () => { CloseWarLedger(); OpenConflictsScreen(); }, 0, 30,
            iconPath: "res://game/gfx/interface/icons/icon_unrest.png");
        var head = ScreenHeader(rows, "WARS OF THE WORLD",
            "Active theatres, belligerents, front-line war score, casualties and peace settlements.",
            false, _warBackButton, _warFilterAll, _warFilterMine, toConflicts, HandbookButton("wars"), ActionButton("×", CloseWarLedger, 34, 30));
        _warLedgerTitle = HeaderTitle(head);

        // ------------------------------------------------------------------ the world KPIs
        _warReadouts = new PanelContainer { ClipContents = true };
        _warReadouts.AddThemeStyleboxOverride("panel", WarInsetStyle(14, 8));
        rows.AddChild(_warReadouts);
        var readouts = new HBoxContainer { ClipContents = true };
        readouts.AddThemeConstantOverride("separation", Ui.Gap(16));
        _warReadouts.AddChild(readouts);
        foreach (var (key, caption) in new[]
                 {
                     ("wars", "WARS BEING FOUGHT"), ("nations", "COUNTRIES AT WAR"),
                     ("battles", "ENGAGEMENTS"), ("losses", "LOSSES"), ("mine", "YOUR WARS")
                 })
        {
            var cell = NumberCell(caption, "—");
            cell.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cell.ClipContents = true;
            var valLbl = (Label)cell.GetNode("FigureValue");
            valLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            _warReadoutValues[key] = valLbl;
            readouts.AddChild(cell);
        }

        _warLedgerSummary = RowLabel("", 12, Ui.Colour("text.body_soft"));
        _warLedgerSummary.ClipText = true;
        _warLedgerSummary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        rows.AddChild(_warLedgerSummary);

        // ------------------------------------------------------------------ the list (with >=16px scrollbar clearance)
        _warListScroll = new ScrollContainer
        {
            Name = "WarLedgerListScroll",
            ClipContents = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        rows.AddChild(_warListScroll);

        var listMargin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        listMargin.AddThemeConstantOverride("margin_right", 16);
        _warListScroll.AddChild(listMargin);

        _warListRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        _warListRows.AddThemeConstantOverride("separation", 7);
        listMargin.AddChild(_warListRows);

        _warLedgerEmpty = Title("", 15, Ui.Colour("text.muted"));
        _warLedgerEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _warLedgerEmpty.CustomMinimumSize = new Vector2(0, 70);
        _warLedgerEmpty.VerticalAlignment = VerticalAlignment.Center;
        _warListRows.AddChild(_warLedgerEmpty);

        // ------------------------------------------------------------------ one war detail (with >=16px scrollbar clearance)
        _warDetailScroll = new ScrollContainer
        {
            Name = "WarLedgerDetailScroll",
            Visible = false,
            ClipContents = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        rows.AddChild(_warDetailScroll);

        var detailMargin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        detailMargin.AddThemeConstantOverride("margin_right", 16);
        _warDetailScroll.AddChild(detailMargin);

        var detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        detail.AddThemeConstantOverride("separation", 12);
        detailMargin.AddChild(detail);

        _warDetailRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        _warDetailRows.AddThemeConstantOverride("separation", 10);
        detail.AddChild(_warDetailRows);

        _warDetailActions = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        _warDetailActions.AddThemeConstantOverride("separation", 6);
        detail.AddChild(_warDetailActions);
    }

    private static StyleBoxFlat WarInsetStyle(int horizontal, int vertical)
    {
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("war.inset"),
            BorderColor = Ui.Colour("war.inset_border"),
            ShadowColor = Ui.Colour("shell.border", 0.36f),
            ShadowSize = 5,
            ShadowOffset = new Vector2(0, 2),
            ContentMarginLeft = horizontal, ContentMarginRight = horizontal,
            ContentMarginTop = vertical, ContentMarginBottom = vertical
        };
        style.SetBorderWidthAll(1);
        style.BorderWidthTop = 2;
        style.SetCornerRadiusAll(4);
        return style;
    }

    private static StyleBoxFlat WarCardStyle(bool mine, bool hover)
    {
        var style = new StyleBoxFlat
        {
            BgColor = hover ? Ui.Colour("war.card_hover") : Ui.Colour("war.card"),
            BorderColor = mine
                ? Ui.Colour("war.card_mine")
                : hover ? Ui.Colour("war.card_border_hover") : Ui.Colour("war.card_border"),
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6
        };
        style.SetBorderWidthAll(1);
        if (mine) style.BorderWidthLeft = 4;
        style.SetCornerRadiusAll(4);
        return style;
    }

    private void OpenWarLedger()
    {
        EnsureScreen("war_ledger", BuildWarLedger);
        _openScreen = "Wars";
        _sidePanel.Visible = false;
        RaiseWorkspaceLayer(_warLedgerLayer);
        FitWorkspaceNavigationInset(_warLedgerLayer);
        ShowWarLedgerList();
    }

    private void CloseWarLedger()
    {
        if (_warLedgerLayer is null) return;
        _warLedgerLayer.Visible = false;
        if (_openScreen == "Wars") _openScreen = "";
        UpdateScreenButtons();
    }

    private void SetWarLedgerFilter(bool mineOnly)
    {
        _warLedgerMineOnly = mineOnly;
        ShowWarLedgerList();
    }

    private void ShowWarLedgerList()
    {
        _warLedgerOpenWarId = -1;
        _warBackButton.Visible = false;
        _warFilterAll.Visible = true;
        _warFilterMine.Visible = true;
        _warReadouts.Visible = true;
        _warListScroll.Visible = true;
        _warDetailScroll.Visible = false;
        _warLedgerTitle.Text = Tr("WARS OF THE WORLD");
        FitWorkspaceNavigationInset(_warLedgerLayer);
        RefreshWarLedger();
    }

    private void ShowWarLedgerDetail(int warId)
    {
        _warLedgerOpenWarId = warId;
        _warDetailDay = int.MinValue;
        _warDetailBuiltFor = -1;
        _warBackButton.Visible = true;
        _warFilterAll.Visible = false;
        _warFilterMine.Visible = false;
        _warReadouts.Visible = false;
        _warListScroll.Visible = false;
        _warDetailScroll.Visible = true;
        FitWorkspaceNavigationInset(_warLedgerLayer);
        RefreshWarLedger();
    }

    private void FitWarLedger(float content)
    {
        if (IsNewShellActive || _warLedgerPanel.AnchorBottom <= _warLedgerPanel.AnchorTop)
            return;
        if (Ui.Metric("war_ledger.fit_content", 1f) <= 0f || _warLedgerLayer.Size.Y <= 0f)
        {
            _warLedgerPanel.OffsetBottom = _warLedgerPlacedBottom;
            return;
        }
        var needed = 2f * 14f + 6f + 34f + 22f + 2f * 9f +
                     (_warReadouts.Visible ? _warReadouts.GetCombinedMinimumSize().Y + 10f : 0f) + content;
        var full = _warLedgerLayer.Size.Y * (_warLedgerPanel.AnchorBottom - _warLedgerPanel.AnchorTop) +
                   _warLedgerPlacedBottom - _warLedgerPanel.OffsetTop;
        _warLedgerPanel.OffsetBottom = _warLedgerPlacedBottom - Mathf.Max(0f, full - needed);
    }

    private static float LiveHeight(BoxContainer box)
    {
        var height = 0f;
        var count = 0;
        foreach (var child in box.GetChildren())
        {
            if (child is not Control { Visible: true } control || control.IsQueuedForDeletion()) continue;
            height += control.GetCombinedMinimumSize().Y;
            count++;
        }
        return height + Mathf.Max(0, count - 1) * box.GetThemeConstant("separation");
    }

    private void RefreshWarLedger()
    {
        if (_campaign is null || !IsInstanceValid(_warLedgerLayer) || !_warLedgerLayer.Visible) return;

        ApplyLedgerButtonState(_warFilterAll, !_warLedgerMineOnly);
        ApplyLedgerButtonState(_warFilterMine, _warLedgerMineOnly);

        if (_warLedgerOpenWarId >= 0)
        {
            RefreshWarLedgerDetail();
            return;
        }

        _warLedgerOrder.Clear();
        var player = _campaign.Player;
        foreach (var war in _campaign.Wars)
        {
            if (war.Ended) continue;
            if (_warLedgerMineOnly && (player is null || !war.Involves(player.Id))) continue;
            _warLedgerOrder.Add(war);
        }

        _warLedgerOrder.Sort((left, right) =>
        {
            var mine = player is null ? 0 : right.Involves(player.Id).CompareTo(left.Involves(player.Id));
            if (mine != 0) return mine;
            return (right.AttackerLosses + right.DefenderLosses)
                .CompareTo(left.AttackerLosses + left.DefenderLosses);
        });

        var worldwide = 0f;
        var fighting = 0;
        var mineCount = 0;
        var belligerents = new HashSet<int>();
        foreach (var war in _campaign.Wars)
        {
            if (war.Ended) continue;
            fighting++;
            worldwide += war.AttackerLosses + war.DefenderLosses;
            foreach (var id in war.Side(true)) belligerents.Add(id);
            foreach (var id in war.Side(false)) belligerents.Add(id);
            if (player is not null && war.Involves(player.Id)) mineCount++;
        }
        var engagements = _campaign.Battles.Count(battle => !battle.Ended);
        _warReadoutValues["wars"].Text = fighting.ToString("N0");
        _warReadoutValues["nations"].Text = belligerents.Count.ToString("N0");
        _warReadoutValues["battles"].Text = engagements.ToString("N0");
        _warReadoutValues["losses"].Text = Fmt.People(worldwide);
        _warReadoutValues["mine"].Text = mineCount == 0 ? "—" : mineCount.ToString("N0");
        _warReadoutValues["mine"].AddThemeColorOverride("font_color",
            mineCount > 0 ? Ui.Colour("text.accent") : Ui.Colour("text.muted"));

        _warLedgerSummary.Text = _warLedgerOrder.Count == 0
            ? ""
            : Simulation.Loc.Format("{0} of {1} war(s) shown", _warLedgerOrder.Count.ToString("N0"), fighting.ToString("N0")) +
              "  ·  " + Tr("the player's wars first, then the costliest") + "  ·  " + Tr("click a war for its own page");
        _warLedgerEmpty.Visible = _warLedgerOrder.Count == 0;
        _warLedgerEmpty.Text = _warLedgerMineOnly ? Tr("You are at peace.") : Tr("The world is at peace.");

        while (_warLedgerRows.Count < _warLedgerOrder.Count) _warLedgerRows.Add(CreateWarLedgerRow());
        for (var index = 0; index < _warLedgerRows.Count; index++)
        {
            var row = _warLedgerRows[index];
            if (index >= _warLedgerOrder.Count)
            {
                row.Card.Visible = false;
                continue;
            }
            row.Card.Visible = true;
            FillWarLedgerRow(row, _warLedgerOrder[index]);
        }
        FitWarLedger(_warLedgerOrder.Count == 0
            ? _warLedgerEmpty.CustomMinimumSize.Y
            : _warLedgerOrder.Count * (WarCardHeight + 7f));
    }

    private WarLedgerRow CreateWarLedgerRow()
    {
        var row = new WarLedgerRow();
        row.Card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, WarCardHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipContents = true
        };
        _warListRows.AddChild(row.Card);

        var outer = new HBoxContainer { ClipContents = true };
        outer.AddThemeConstantOverride("separation", 8);
        row.Card.AddChild(outer);

        row.MainButton = new Button
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        var empty = new StyleBoxEmpty();
        var mainHover = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.05f) };
        mainHover.SetCornerRadiusAll(3);
        row.MainButton.AddThemeStyleboxOverride("normal", empty);
        row.MainButton.AddThemeStyleboxOverride("hover", mainHover);
        row.MainButton.AddThemeStyleboxOverride("pressed", mainHover);
        row.MainButton.Pressed += () =>
        {
            if (row.WarId >= 0) ShowWarLedgerDetail(row.WarId);
        };
        outer.AddChild(row.MainButton);

        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, ClipContents = true };
        line.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        line.OffsetLeft = 4; line.OffsetRight = -4; line.OffsetTop = 2; line.OffsetBottom = -2;
        line.AddThemeConstantOverride("separation", 10);
        row.MainButton.AddChild(line);

        Label Text(int size, string colour, HorizontalAlignment alignment = HorizontalAlignment.Left, bool tabular = false)
        {
            var label = RowLabel("", size, Ui.Colour(colour));
            if (tabular) label.AddThemeFontOverride("font", PresentationFonts.Tabular());
            label.MouseFilter = MouseFilterEnum.Ignore;
            label.ClipText = true;
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.HorizontalAlignment = alignment;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }

        Control FlagSlot(out Control flag)
        {
            var slot = new CenterContainer
            {
                CustomMinimumSize = new Vector2(50, 0),
                MouseFilter = MouseFilterEnum.Ignore
            };
            flag = FlagBadge("", 28);
            flag.MouseFilter = MouseFilterEnum.Ignore;
            slot.AddChild(flag);
            return slot;
        }

        VBoxContainer Stack(float ratio, int separation = 2)
        {
            var stack = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = ratio,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Ignore
            };
            stack.AddThemeConstantOverride("separation", separation);
            return stack;
        }

        // Attacker leader + framed flag cluster
        line.AddChild(FlagSlot(out row.AttackerFlag));
        var attacker = Stack(0.95f);
        row.Attacker = Text(15, "text.bright");
        row.AttackerSide = Text(10, "text.caption");
        row.AttackerCluster = new HBoxContainer { ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        row.AttackerCluster.AddThemeConstantOverride("separation", 3);
        attacker.AddChild(row.Attacker);
        attacker.AddChild(row.AttackerSide);
        attacker.AddChild(row.AttackerCluster);
        line.AddChild(attacker);

        // Center: War title + high-contrast WarBar tug + tabular score badge + crisp front/battles line
        var middle = Stack(1.45f, 4);
        row.Heading = Text(11, "text.caption", HorizontalAlignment.Center);
        middle.AddChild(row.Heading);

        var barRow = new HBoxContainer { ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        barRow.AddThemeConstantOverride("separation", 6);
        row.Bar = new WarBar
        {
            CustomMinimumSize = new Vector2(150, 18),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Track = Ui.Colour("war.track"),
            MarkerColour = Ui.Colour("war.front_line"),
            TickColour = Ui.Colour("shell.paper", 0.45f)
        };
        barRow.AddChild(row.Bar);
        row.ScoreBadge = Text(12, "text.bright", HorizontalAlignment.Right, tabular: true);
        row.ScoreBadge.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        row.ScoreBadge.CustomMinimumSize = new Vector2(46, 0);
        barRow.AddChild(row.ScoreBadge);
        middle.AddChild(barRow);

        row.Standing = Text(11, "text.body_soft", HorizontalAlignment.Center, tabular: true);
        middle.AddChild(row.Standing);
        line.AddChild(middle);

        // Defender leader + framed flag cluster
        var defender = Stack(0.95f);
        row.Defender = Text(15, "text.bright", HorizontalAlignment.Right);
        row.DefenderSide = Text(10, "text.caption", HorizontalAlignment.Right);
        row.DefenderCluster = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        row.DefenderCluster.AddThemeConstantOverride("separation", 3);
        defender.AddChild(row.Defender);
        defender.AddChild(row.DefenderSide);
        defender.AddChild(row.DefenderCluster);
        line.AddChild(defender);
        line.AddChild(FlagSlot(out row.DefenderFlag));

        line.AddChild(new ColorRect
        {
            Color = Ui.Colour("war.inset_border"),
            CustomMinimumSize = new Vector2(1, 0),
            MouseFilter = MouseFilterEnum.Ignore
        });

        // Right KPI column: Casualties, Divisions, War Support, Age
        var kpis = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(126, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        kpis.AddThemeConstantOverride("separation", 1);
        row.Losses = Text(15, "text.bright", HorizontalAlignment.Right, tabular: true);
        row.Divisions = Text(11, "text.body_soft", HorizontalAlignment.Right, tabular: true);
        row.Support = Text(10, "text.caption", HorizontalAlignment.Right, tabular: true);
        row.Age = Text(10, "text.muted", HorizontalAlignment.Right, tabular: true);
        kpis.AddChild(row.Losses);
        kpis.AddChild(row.Divisions);
        kpis.AddChild(row.Support);
        kpis.AddChild(row.Age);
        line.AddChild(kpis);

        // Quick action rail: Jump to Front + Peace Conference (when player's war)
        var actRail = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(68, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ClipContents = true
        };
        actRail.AddThemeConstantOverride("separation", 4);
        row.JumpBtn = ActionButton(Tr("shell.ws.war.jump_btn"), () => JumpToWarFront(row.WarId), 66, 26);
        row.JumpBtn.TooltipText = Ui.Tip(Tr("shell.ws.war.jump_tip"));
        actRail.AddChild(row.JumpBtn);

        row.PeaceBtn = ActionButton(Tr("shell.ws.war.peace_btn"), () => OpenWarPeaceFromCard(row.WarId), 66, 26, primary: true);
        row.PeaceBtn.TooltipText = Ui.Tip(Tr("shell.ws.war.peace_tip"));
        row.PeaceBtn.Visible = false;
        actRail.AddChild(row.PeaceBtn);
        outer.AddChild(actRail);

        return row;
    }

    private void FillWarLedgerRow(WarLedgerRow row, StrategicWar war)
    {
        var attacker = _campaign.NationById(war.AttackerId);
        var defender = _campaign.NationById(war.DefenderId);
        var mine = _campaign.Player is not null && war.Involves(_campaign.Player.Id);

        row.WarId = war.Id;
        row.Bar.Name = "WarLedgerScoreBar" + war.Id;
        var styled = mine ? 1 : 0;
        if (row.Styled != styled)
        {
            row.Styled = styled;
            row.Card.AddThemeStyleboxOverride("panel", WarCardStyle(mine, false));
        }

        var reason = WarReason(war);
        var tip = WarNames.Of(_campaign, war) +
                  (reason.Length > 0 ? "\n" + reason : "") +
                  "\n" + Tr("click a war for its own page");
        row.MainButton.TooltipText = Ui.Tip(tip);

        SetFlag(row.AttackerFlag, attacker?.Tag ?? "", 28);
        SetFlag(row.DefenderFlag, defender?.Tag ?? "", 28);
        TintBlankFlag(row.AttackerFlag, attacker);
        TintBlankFlag(row.DefenderFlag, defender);
        row.Attacker.Text = attacker?.DisplayName ?? "?";
        row.Defender.Text = defender?.DisplayName ?? "?";
        var nameColour = mine ? Ui.Colour("text.accent") : Ui.Colour("text.bright");
        row.Attacker.AddThemeColorOverride("font_color", nameColour);
        row.Defender.AddThemeColorOverride("font_color", nameColour);
        row.AttackerSide.Text = WarSideLine(war, true);
        row.DefenderSide.Text = WarSideLine(war, false);

        // Rebuild compact flag-only coalition clusters only when membership changes
        var atkAllies = war.Side(true).Where(id => id != war.AttackerId).ToArray();
        var defAllies = war.Side(false).Where(id => id != war.DefenderId).ToArray();
        var clusterSig = war.Id + "|" + string.Join(",", atkAllies) + "|" + string.Join(",", defAllies);
        if (row.ClusterSig != clusterSig)
        {
            row.ClusterSig = clusterSig;
            PopulateCardFlagCluster(row.AttackerCluster, war, atkAllies, attacking: true);
            PopulateCardFlagCluster(row.DefenderCluster, war, defAllies, attacking: false);
        }

        var objective = WarObjective(war);
        row.Heading.Text = WarNames.Of(_campaign, war).ToUpperInvariant() + "  ·  " +
                           (objective is null ? Tr("no claim on ground") : Simulation.Loc.Format("for {0}", objective.DisplayName));

        var attackerColour = WarSideColour(attacker);
        var defenderColour = WarSideColour(defender);
        row.Bar.Tug(Mathf.Clamp(war.WarScore / WarLedgerBarScale, -1f, 1f), attackerColour, defenderColour);

        row.ScoreBadge.Text = war.WarScore.ToString("+0.0;-0.0;0.0");
        row.ScoreBadge.AddThemeColorOverride("font_color",
            Mathf.Abs(war.WarScore) < 0.5f
                ? Ui.Colour("text.muted")
                : war.WarScore > 0f ? Ui.Colour("state.positive") : Ui.Colour("state.negative"));

        var battles = 0;
        foreach (var battle in _campaign.Battles)
            if (battle.WarId == war.Id && !battle.Ended) battles++;
        var frontProvs = war.AttackerFront.Count + war.DefenderFront.Count;
        row.Standing.Text = battles > 0
            ? Simulation.Loc.Format("shell.ws.war.front_battles_fmt", frontProvs.ToString("N0"), battles.ToString("N0"))
            : Simulation.Loc.Format("shell.ws.war.front_only_fmt", frontProvs.ToString("N0"));

        var atkDivs = 0;
        var defDivs = 0;
        foreach (var army in _campaign.Armies)
        {
            if (army.AssignedWarId != war.Id) continue;
            if (war.IsAttacking(army.NationId)) atkDivs++;
            else defDivs++;
        }

        row.Losses.Text = Fmt.People(war.AttackerLosses + war.DefenderLosses);
        row.Divisions.Text = Simulation.Loc.Format("shell.ws.war.divs_fmt", atkDivs.ToString("N0"), defDivs.ToString("N0"));
        row.Support.Text = Simulation.Loc.Format("shell.ws.war.exhaustion_fmt",
            war.AttackerWarSupport.ToString("P0"), war.DefenderWarSupport.ToString("P0"));
        row.Age.Text = WarLength(Math.Max(0, _campaign.ElapsedDays - war.StartedDay));

        row.PeaceBtn.Visible = mine && !war.Ended && !_campaign.ObserverMode;
    }

    private void PopulateCardFlagCluster(HBoxContainer host, StrategicWar war, int[] allyIds, bool attacking)
    {
        foreach (var ch in host.GetChildren())
        {
            host.RemoveChild(ch);
            ch.QueueFree();
        }
        host.Visible = allyIds.Length > 0;
        if (allyIds.Length == 0) return;

        const int maxShown = 4;
        for (var i = 0; i < Math.Min(maxShown, allyIds.Length); i++)
        {
            var member = _campaign.NationById(allyIds[i]);
            if (member is null) continue;
            var flag = FlagBadge(member.Tag, 14, WarMemberTooltip(war, member, attacking));
            TintBlankFlag(flag, member);
            host.AddChild(flag);
        }
        if (allyIds.Length > maxShown)
        {
            var more = RowLabel(Simulation.Loc.Format("shell.ws.war.ally_more_fmt", allyIds.Length - maxShown), 10, Ui.Colour("text.caption"));
            more.AddThemeFontOverride("font", PresentationFonts.Tabular());
            host.AddChild(more);
        }
    }

    private string WarMemberTooltip(StrategicWar war, Nation member, bool attacking)
    {
        var roleKey = member.Id == (attacking ? war.AttackerId : war.DefenderId)
            ? "shell.ws.war.role_leader"
            : attacking ? "shell.ws.war.role_attacker" : "shell.ws.war.role_defender";
        var divs = _campaign.Armies.Count(a => a.AssignedWarId == war.Id && a.NationId == member.Id);
        var losses = WarLosses.ByCountry(war, attacking).Countries
            .Where(c => c.NationId == member.Id)
            .Select(c => c.Toll.Total)
            .FirstOrDefault();
        return Simulation.Loc.Format("shell.ws.war.flag_tip_fmt",
            member.DisplayName, Tr(roleKey), divs.ToString("N0"), Fmt.People(losses));
    }

    private void JumpToWarFront(int warId)
    {
        var war = _campaign?.Wars.Find(w => w.Id == warId);
        if (war is null) return;
        var prov = (_campaign.Player is not null && war.Involves(_campaign.Player.Id))
            ? war.FriendlyFront(_campaign.Player.Id).FirstOrDefault()
            : 0;
        if (prov <= 0) prov = war.AttackerFront.FirstOrDefault();
        if (prov <= 0) prov = war.DefenderFront.FirstOrDefault();
        CloseWarLedger();
        if (prov > 0)
            FocusProvince(prov, 7.0f);
        else if (_campaign.NationById(war.AttackerId) is { } atk)
            FocusNation(atk);
    }

    private void OpenWarPeaceFromCard(int warId)
    {
        var war = _campaign?.Wars.Find(w => w.Id == warId);
        if (war is null || war.Ended) return;
        OpenPeaceConference(war);
    }

    private static void TintBlankFlag(Control badge, Nation nation)
    {
        if (badge?.GetThemeStylebox("panel") is not StyleBoxFlat frame) return;
        var blank = badge.GetChildCount() == 0 || badge.GetChild(0) is not TextureRect { Texture: not null };
        frame.BgColor = blank && nation is not null ? WarSideColour(nation) : Ui.Colour("surface.inset");
    }

    private string WarSideLine(StrategicWar war, bool attacking)
    {
        var allies = Math.Max(0, war.Side(attacking).Count() - 1);
        return attacking
            ? allies == 0 ? Tr("attacking, alone") : Simulation.Loc.Format("attacking, with {0} ally(ies)", allies.ToString("N0"))
            : allies == 0 ? Tr("defending, alone") : Simulation.Loc.Format("defending, with {0} ally(ies)", allies.ToString("N0"));
    }

    private string WarStanding(StrategicWar war, Nation attacker, Nation defender)
    {
        var score = war.WarScore.ToString("+0.0;-0.0;0.0");
        if (war.Collapsed != 0)
            return Simulation.Loc.Format("{0} has collapsed: the war is decided.",
                (war.Collapsed > 0 ? defender?.DisplayName : attacker?.DisplayName) ?? Tr("nobody"));
        if (Mathf.Abs(war.WarScore) < 0.5f) return Simulation.Loc.Format("{0} · neither side ahead", score);
        return Simulation.Loc.Format("{0} · {1} ahead", score,
            (war.WarScore > 0f ? attacker?.DisplayName : defender?.DisplayName) ?? Tr("nobody"));
    }

    private string WarReason(StrategicWar war) =>
        Tr(Simulation.CasusBelliNames.Of(war.Justification));

    private ScenarioState WarObjective(StrategicWar war) =>
        war.ClaimedStateMapIndex < 0
            ? null
            : _campaign.States.FirstOrDefault(state => state.MapIndex == war.ClaimedStateMapIndex);

    private static string WarLength(int days) =>
        days >= 730 ? Simulation.Loc.Format("{0} yr", (days / 365f).ToString("0.#"))
        : days >= 90 ? Simulation.Loc.Format("{0} mo", ((int)(days / 30.44f)).ToString("N0"))
        : Simulation.Loc.Format("{0} d", days.ToString("N0"));

    private static Color NationColour(Nation nation)
    {
        if (nation is null || string.IsNullOrWhiteSpace(nation.Color))
            return Ui.Colour("text.muted");
        try { return Color.FromHtml(nation.Color.Trim()); }
        catch { return Ui.Colour("text.muted"); }
    }

    private static Color WarSideColour(Nation nation)
    {
        var colour = NationColour(nation);
        colour.A = 1f;
        return colour.Darkened(Ui.Metric("war_ledger.colour_depth", 0.12f));
    }

    private void CallAllyToWar(StrategicWar war, int allyId)
    {
        if (_campaign?.Player is null || war is null) return;
        _campaign.Commands.Enqueue(new CallAllyToWarCommand
        {
            IssuerId = _campaign.Player.Id, WarId = war.Id, AllyId = allyId
        });
        _campaign.Commands.ApplyAll(_campaign);
        _warDetailBuiltFor = -1;
        _warDetailDay = int.MinValue;
        RefreshWarLedger();
    }

    private void ExchangePrisoners(StrategicWar war, int counterpartId)
    {
        if (_campaign?.Player is null || war is null) return;
        _campaign.Commands.Enqueue(new ExchangePrisonersCommand
        {
            IssuerId = _campaign.Player.Id, WarId = war.Id, CounterpartId = counterpartId
        });
        _campaign.Commands.ApplyAll(_campaign);
        _warDetailBuiltFor = -1;
        _warDetailDay = int.MinValue;
        RefreshWarLedger();
    }
}
