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
        public Button Button;
        public Control AttackerFlag;
        public Control DefenderFlag;
        public Label Attacker;
        public Label AttackerSide;
        public Label Defender;
        public Label DefenderSide;
        public Label Heading;
        public WarBar Bar;
        public Label Standing;
        public Label Losses;
        public Label Age;
        public int WarId = -1;
        public int Styled = -1;
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
    private static float WarLedgerBarScale => Ui.Metric("war_ledger.bar_scale", 40f);

    /// <summary>The same for one part of a war's score, which is a fraction of the whole.</summary>
    private static float WarLedgerPartScale => Ui.Metric("war_ledger.part_scale", 20f);

    private static float WarCardHeight => Ui.Metric("war_ledger.card_height", 76f);

    /// <summary>
    /// The world's wars, as a board somebody can read.
    ///
    /// There were a hundred and forty of them and the screen once wrote a paragraph about each
    /// one into the side panel. Then it became a spreadsheet: one line a war across a window
    /// the size of the screen, so that three wars were three lines at the top of a great deal
    /// of navy. Now each war is a card - the two flags facing each other, the ground between
    /// them as a bar with the front line drawn where the war score puts it, what it has cost
    /// and how long it has run - and the window is as tall as the wars in it. The figures for
    /// the whole world sit above them, read first, the way a situation room reads.
    /// </summary>
    private void BuildWarLedger()
    {
        _warLedgerLayer = FullLayer("WarLedger");
        _warLedgerLayer.Visible = false;
        _hudLayer.AddChild(_warLedgerLayer);

        var shade = new ColorRect
        {
            Color = new Color(0.015f, 0.026f, 0.041f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _warLedgerLayer.AddChild(shade);

        _warLedgerPanel = TexturedPanel(_warLedgerLayer);
        Ui.Place(_warLedgerPanel, "modal.war_ledger");
        _warLedgerPlacedBottom = _warLedgerPanel.OffsetBottom;

        var rows = PanelRows(_warLedgerPanel, 22, 16, 10);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        _warLedgerTitle = Title("WARS OF THE WORLD", 25, Ui.Colour("text.accent"));
        _warLedgerTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _warLedgerTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        _warLedgerTitle.ClipText = true;
        header.AddChild(_warLedgerTitle);

        _warBackButton = ActionButton("← All wars", ShowWarLedgerList, 118, 30);
        _warBackButton.Visible = false;
        header.AddChild(_warBackButton);

        _warFilterAll = ActionButton("Every war", () => SetWarLedgerFilter(false), 108, 30, true);
        header.AddChild(_warFilterAll);
        _warFilterMine = ActionButton("Mine", () => SetWarLedgerFilter(true), 78, 30);
        header.AddChild(_warFilterMine);
        header.AddChild(ActionButton("×", CloseWarLedger, 34, 30));

        // ------------------------------------------------------------------ the world
        _warReadouts = new PanelContainer();
        _warReadouts.AddThemeStyleboxOverride("panel", WarInsetStyle(16, 9));
        rows.AddChild(_warReadouts);
        var readouts = new HBoxContainer();
        readouts.AddThemeConstantOverride("separation", 18);
        _warReadouts.AddChild(readouts);
        foreach (var (key, caption) in new[]
                 {
                     ("wars", "WARS BEING FOUGHT"), ("nations", "COUNTRIES AT WAR"),
                     ("battles", "ENGAGEMENTS"), ("losses", "LOSSES"), ("mine", "YOUR WARS")
                 })
        {
            if (readouts.GetChildCount() > 0)
                readouts.AddChild(new ColorRect
                {
                    Color = Ui.Colour("war.inset_border"),
                    CustomMinimumSize = new Vector2(1, 0),
                    MouseFilter = MouseFilterEnum.Ignore
                });
            var readout = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            readout.AddThemeConstantOverride("separation", 0);
            readout.AddChild(RowLabel(caption, 10, Ui.Colour("text.caption")));
            var value = RowLabel("—", 21, Ui.Colour("text.bright"));
            readout.AddChild(value);
            _warReadoutValues[key] = value;
            readouts.AddChild(readout);
        }

        _warLedgerSummary = RowLabel("", 12, Ui.Colour("text.body_soft"));
        _warLedgerSummary.ClipText = true;
        rows.AddChild(_warLedgerSummary);

        // ------------------------------------------------------------------ the list
        _warListScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        rows.AddChild(_warListScroll);
        _warListRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _warListRows.AddThemeConstantOverride("separation", 6);
        _warListScroll.AddChild(_warListRows);

        _warLedgerEmpty = Title("", 15, Ui.Colour("text.muted"));
        _warLedgerEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _warLedgerEmpty.CustomMinimumSize = new Vector2(0, 70);
        _warLedgerEmpty.VerticalAlignment = VerticalAlignment.Center;
        _warListRows.AddChild(_warLedgerEmpty);

        // ------------------------------------------------------------------ one war
        _warDetailScroll = new ScrollContainer
        {
            Visible = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        rows.AddChild(_warDetailScroll);
        var detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        detail.AddThemeConstantOverride("separation", 12);
        _warDetailScroll.AddChild(detail);

        _warDetailRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _warDetailRows.AddThemeConstantOverride("separation", 12);
        detail.AddChild(_warDetailRows);

        // The buttons that end a war are built once per war and left alone. The figures
        // above them are rebuilt as the war moves, and a control that is torn down and
        // remade between a mouse going down and coming up is a control that cannot be
        // clicked - at speed five that is every fifth of a second.
        _warDetailActions = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _warDetailActions.AddThemeConstantOverride("separation", 6);
        detail.AddChild(_warDetailActions);
    }

    /// <summary>A recessed field on the screen: the world's figures, a column of one war.</summary>
    private static StyleBoxFlat WarInsetStyle(int horizontal, int vertical)
    {
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("war.inset"),
            BorderColor = Ui.Colour("war.inset_border"),
            ContentMarginLeft = horizontal, ContentMarginRight = horizontal,
            ContentMarginTop = vertical, ContentMarginBottom = vertical
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(3);
        return style;
    }

    /// <summary>
    /// A war's card, in its three states. The player's own wars carry a brass edge - the one
    /// ornament on the board, so that "mine" is found before anything is read.
    /// </summary>
    private static StyleBoxFlat WarCardStyle(bool mine, bool hover)
    {
        var style = new StyleBoxFlat
        {
            BgColor = hover ? Ui.Colour("war.card_hover") : Ui.Colour("war.card"),
            BorderColor = mine
                ? Ui.Colour("war.card_mine")
                : hover ? Ui.Colour("war.card_border_hover") : Ui.Colour("war.card_border"),
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8
        };
        style.SetBorderWidthAll(1);
        if (mine) style.BorderWidthLeft = 4;
        style.SetCornerRadiusAll(4);
        return style;
    }

    private void OpenWarLedger()
    {
        _openScreen = "Wars";
        _sidePanel.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        _warLedgerLayer.Visible = true;
        _warLedgerLayer.MoveToFront();
        ShowWarLedgerList();
    }

    private void CloseWarLedger()
    {
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
        RefreshWarLedger();
    }

    /// <summary>
    /// The window as tall as the wars in it, up to the rectangle the layout gives it.
    ///
    /// The rectangle is the most it may take, and is still the layout's to move; what is
    /// taken off is taken from the bottom, so the title stays where the eye expects it.
    /// `war_ledger.fit_content` 0 turns it off for a skin that wants a fixed window.
    /// </summary>
    private void FitWarLedger(float content)
    {
        if (Ui.Metric("war_ledger.fit_content", 1f) <= 0f || _warLedgerLayer.Size.Y <= 0f)
        {
            _warLedgerPanel.OffsetBottom = _warLedgerPlacedBottom;
            return;
        }
        // Margins and frame, the title and the line under it, the world's figures when they are
        // showing, and the gaps between them - then whatever the page itself holds.
        var needed = 2f * 16f + 6f + 34f + 22f + 2f * 10f +
                     (_warReadouts.Visible ? _warReadouts.GetCombinedMinimumSize().Y + 10f : 0f) + content;
        var full = _warLedgerLayer.Size.Y * (_warLedgerPanel.AnchorBottom - _warLedgerPanel.AnchorTop) +
                   _warLedgerPlacedBottom - _warLedgerPanel.OffsetTop;
        _warLedgerPanel.OffsetBottom = _warLedgerPlacedBottom - Mathf.Max(0f, full - needed);
    }

    /// <summary>
    /// How tall a column of controls is as built, not counting what is on its way out - a page
    /// rebuilt this frame still has last frame's children in it until the frame ends.
    /// </summary>
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

    /// <summary>
    /// The whole screen, whichever half of it is showing.
    ///
    /// Cards are kept and refilled rather than rebuilt: this runs whenever the campaign
    /// changes, and throwing away a hundred and forty cards a frame to make the same
    /// hundred and forty is how a ledger becomes a stutter.
    /// </summary>
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

        // The player's own wars first, then the ones that have cost the most - which is
        // the closest thing to "the wars that matter" that does not need an opinion.
        _warLedgerOrder.Sort((left, right) =>
        {
            var mine = player is null ? 0 : right.Involves(player.Id).CompareTo(left.Involves(player.Id));
            if (mine != 0) return mine;
            return (right.AttackerLosses + right.DefenderLosses)
                .CompareTo(left.AttackerLosses + left.DefenderLosses);
        });

        // The world's figures, which do not depend on the filter: "mine" narrows the list,
        // not the world.
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
                row.Button.Visible = false;
                continue;
            }
            row.Button.Visible = true;
            FillWarLedgerRow(row, _warLedgerOrder[index]);
        }
        FitWarLedger(_warLedgerOrder.Count == 0
            ? _warLedgerEmpty.CustomMinimumSize.Y
            : _warLedgerOrder.Count * (WarCardHeight + 6f));
    }

    private WarLedgerRow CreateWarLedgerRow()
    {
        var row = new WarLedgerRow();
        row.Button = new Button
        {
            CustomMinimumSize = new Vector2(0, WarCardHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipContents = true
        };
        row.Button.Pressed += () =>
        {
            if (row.WarId >= 0) ShowWarLedgerDetail(row.WarId);
        };
        _warListRows.AddChild(row.Button);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        row.Button.AddChild(margin);

        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 14);
        margin.AddChild(line);

        Label Text(int size, string colour, HorizontalAlignment alignment = HorizontalAlignment.Left)
        {
            var label = RowLabel("", size, Ui.Colour(colour));
            label.MouseFilter = MouseFilterEnum.Ignore;
            label.ClipText = true;
            label.HorizontalAlignment = alignment;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }

        // A flag in a slot of its own width, so the names beside flags of every shape line up.
        Control FlagSlot(out Control flag)
        {
            var slot = new CenterContainer
            {
                CustomMinimumSize = new Vector2(56, 0),
                MouseFilter = MouseFilterEnum.Ignore
            };
            flag = FlagBadge("", 30);
            flag.MouseFilter = MouseFilterEnum.Ignore;
            slot.AddChild(flag);
            return slot;
        }

        VBoxContainer Stack(float ratio, int separation = 1)
        {
            var stack = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = ratio,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore
            };
            stack.AddThemeConstantOverride("separation", separation);
            return stack;
        }

        // The attacker, facing right.
        line.AddChild(FlagSlot(out row.AttackerFlag));
        var attacker = Stack(1f);
        row.Attacker = Text(16, "text.bright");
        row.AttackerSide = Text(11, "text.caption");
        attacker.AddChild(row.Attacker);
        attacker.AddChild(row.AttackerSide);
        line.AddChild(attacker);

        // The ground between them, and the front line on it.
        var middle = Stack(1.5f, 4);
        row.Heading = Text(11, "text.caption", HorizontalAlignment.Center);
        middle.AddChild(row.Heading);
        row.Bar = new WarBar
        {
            CustomMinimumSize = new Vector2(220, 16),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Track = Ui.Colour("war.track"),
            MarkerColour = Ui.Colour("war.front_line")
        };
        middle.AddChild(row.Bar);
        row.Standing = Text(11, "text.body_soft", HorizontalAlignment.Center);
        middle.AddChild(row.Standing);
        line.AddChild(middle);

        // The defender, facing left.
        var defender = Stack(1f);
        row.Defender = Text(16, "text.bright", HorizontalAlignment.Right);
        row.DefenderSide = Text(11, "text.caption", HorizontalAlignment.Right);
        defender.AddChild(row.Defender);
        defender.AddChild(row.DefenderSide);
        line.AddChild(defender);
        line.AddChild(FlagSlot(out row.DefenderFlag));

        line.AddChild(new ColorRect
        {
            Color = Ui.Colour("war.inset_border"),
            CustomMinimumSize = new Vector2(1, 0),
            MouseFilter = MouseFilterEnum.Ignore
        });

        // What it has cost and how long it has run, read down the right edge of every card.
        var cost = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(112, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        cost.AddThemeConstantOverride("separation", 0);
        var caption = RowLabel("LOSSES", 10, Ui.Colour("text.caption"));
        caption.HorizontalAlignment = HorizontalAlignment.Right;
        caption.MouseFilter = MouseFilterEnum.Ignore;
        cost.AddChild(caption);
        row.Losses = Text(18, "text.bright", HorizontalAlignment.Right);
        cost.AddChild(row.Losses);
        row.Age = Text(11, "text.muted", HorizontalAlignment.Right);
        cost.AddChild(row.Age);
        line.AddChild(cost);
        return row;
    }

    private void FillWarLedgerRow(WarLedgerRow row, StrategicWar war)
    {
        var attacker = _campaign.NationById(war.AttackerId);
        var defender = _campaign.NationById(war.DefenderId);
        var mine = _campaign.Player is not null && war.Involves(_campaign.Player.Id);

        row.WarId = war.Id;
        var styled = mine ? 1 : 0;
        if (row.Styled != styled)
        {
            row.Styled = styled;
            row.Button.AddThemeStyleboxOverride("normal", WarCardStyle(mine, false));
            row.Button.AddThemeStyleboxOverride("hover", WarCardStyle(mine, true));
            row.Button.AddThemeStyleboxOverride("pressed", WarCardStyle(mine, true));
            row.Button.AddThemeStyleboxOverride("focus", WarCardStyle(mine, true));
        }
        row.Button.TooltipText = Ui.Tip(war.Name + "\n" + Tr("click a war for its own page"));

        SetFlag(row.AttackerFlag, attacker?.Tag ?? "", 30);
        SetFlag(row.DefenderFlag, defender?.Tag ?? "", 30);
        TintBlankFlag(row.AttackerFlag, attacker);
        TintBlankFlag(row.DefenderFlag, defender);
        row.Attacker.Text = attacker?.DisplayName ?? "?";
        row.Defender.Text = defender?.DisplayName ?? "?";
        var nameColour = mine ? Ui.Colour("text.accent") : Ui.Colour("text.bright");
        row.Attacker.AddThemeColorOverride("font_color", nameColour);
        row.Defender.AddThemeColorOverride("font_color", nameColour);
        row.AttackerSide.Text = WarSideLine(war, true);
        row.DefenderSide.Text = WarSideLine(war, false);

        var objective = WarObjective(war);
        row.Heading.Text = (war.Name.Length > 0 ? war.Name.ToUpperInvariant() : Tr("WAR")) + "  ·  " +
                           (objective is null ? Tr("no claim on ground") : Simulation.Loc.Format("for {0}", objective.DisplayName));

        // The ground between them in each side's own colour, and the front line where the war
        // score puts it. Red and green would have to mean something, and in a list of other
        // people's wars they mean nothing; the colours here are the ones on the map.
        var attackerColour = WarSideColour(attacker);
        var defenderColour = WarSideColour(defender);
        row.Bar.Tug(Mathf.Clamp(war.WarScore / WarLedgerBarScale, -1f, 1f), attackerColour, defenderColour);

        var battles = 0;
        foreach (var battle in _campaign.Battles)
            if (battle.WarId == war.Id && !battle.Ended) battles++;
        var standing = new List<string> { WarStanding(war, attacker, defender) };
        standing.Add(Simulation.Loc.Format("{0} province(s) of front", (war.AttackerFront.Count + war.DefenderFront.Count).ToString("N0")));
        if (battles > 0) standing.Add(Simulation.Loc.Format("{0} battle(s)", battles.ToString("N0")));
        row.Standing.Text = string.Join("  ·  ", standing);

        row.Losses.Text = Fmt.People(war.AttackerLosses + war.DefenderLosses);
        row.Age.Text = WarLength(Math.Max(0, _campaign.ElapsedDays - war.StartedDay));
    }

    /// <summary>
    /// A badge with no picture in it shows the country's colour rather than an empty frame -
    /// a country with no flag file and no world to draw a provisional one from still has a
    /// colour on the map, and that is what the player knows it by.
    /// </summary>
    private static void TintBlankFlag(Control badge, Nation nation)
    {
        if (badge?.GetThemeStylebox("panel") is not StyleBoxFlat frame) return;
        var blank = badge.GetChildCount() == 0 || badge.GetChild(0) is not TextureRect { Texture: not null };
        frame.BgColor = blank && nation is not null ? WarSideColour(nation) : Ui.Colour("surface.inset");
    }

    /// <summary>"attacking, with 2 allies" - the role, and whether anybody came.</summary>
    private string WarSideLine(StrategicWar war, bool attacking)
    {
        var allies = Math.Max(0, war.Side(attacking).Count() - 1);
        return attacking
            ? allies == 0 ? Tr("attacking, alone") : Simulation.Loc.Format("attacking, with {0} ally(ies)", allies.ToString("N0"))
            : allies == 0 ? Tr("defending, alone") : Simulation.Loc.Format("defending, with {0} ally(ies)", allies.ToString("N0"));
    }

    /// <summary>Who is ahead, in words, with the score that says so.</summary>
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

    private ScenarioState WarObjective(StrategicWar war) =>
        war.ClaimedStateMapIndex < 0
            ? null
            : _campaign.States.FirstOrDefault(state => state.MapIndex == war.ClaimedStateMapIndex);

    /// <summary>How long, in the unit a person would use for it.</summary>
    private static string WarLength(int days) =>
        days >= 730 ? Simulation.Loc.Format("{0} yr", (days / 365f).ToString("0.#"))
        : days >= 90 ? Simulation.Loc.Format("{0} mo", ((int)(days / 30.44f)).ToString("N0"))
        : Simulation.Loc.Format("{0} d", days.ToString("N0"));

    private static Color NationColour(Nation nation) =>
        nation is null ? new Color(0.5f, 0.5f, 0.5f) : new Color(nation.Color);

    /// <summary>
    /// A country's colour as a field on this screen: its map colour, a shade deeper, so a
    /// pale country does not glare out of a navy panel and a bar of two still reads as two.
    /// </summary>
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
        // The answer lands on the war, and the panel is rebuilt so the side list shows it.
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

    /// <summary>
    /// One war on its own page: who, what it is for, how it is going and what it has cost.
    ///
    /// The two sides face each other across the top, as on the card, larger; under them three
    /// columns - how it is going, what it has cost, where it is being fought - so that the
    /// page is read across as well as down, and a figure is next to the figure it is weighed
    /// against rather than nine lines away from it. This is also where the peace conference
    /// is reached from - for this war, rather than for whichever war the player happened to
    /// enter first.
    /// </summary>
    private void RefreshWarLedgerDetail()
    {
        var war = _campaign.Wars.Find(candidate => candidate.Id == _warLedgerOpenWarId);
        if (war is null)
        {
            _warLedgerSummary.Text = Tr("That war is over.");
            return;
        }
        // Nothing on this page changes inside a day.
        if (_warDetailDay == _campaign.ElapsedDays && _warDetailBuiltFor == war.Id)
        {
            FitWarLedgerDetail();
            return;
        }
        _warDetailDay = _campaign.ElapsedDays;
        foreach (var child in _warDetailRows.GetChildren()) child.QueueFree();

        var attacker = _campaign.NationById(war.AttackerId);
        var defender = _campaign.NationById(war.DefenderId);
        var player = _campaign.Player;
        var mine = player is not null && war.Involves(player.Id);
        var days = Math.Max(0, _campaign.ElapsedDays - war.StartedDay);
        var objective = WarObjective(war);
        var attackerColour = WarSideColour(attacker);
        var defenderColour = WarSideColour(defender);

        _warLedgerTitle.Text = war.Name.Length > 0 ? war.Name.ToUpperInvariant() : Tr("WARS OF THE WORLD");
        _warLedgerSummary.Text =
            (objective is null ? Tr("no claim on ground") : Simulation.Loc.Format("for {0}", objective.DisplayName)) + "  ·  " +
            (war.Ended ? Tr("settled") : Simulation.Loc.Format("{0:N0} day(s) of fighting", days)) +
            (mine ? Tr("  ·  you are a belligerent") : "");

        Label Caption(string text) => RowLabel(text, 11, Ui.Colour("text.caption"));

        // A label keeps its own width unless it is one that is told to give way (a name beside
        // a figure): a clipped label in a box that grants it nothing is a label of no letters.
        Label Body(string text, int size = 13, Color? colour = null, bool wrap = false)
        {
            var label = RowLabel("", size, colour ?? Ui.Colour("text.body"));
            label.Text = text;
            if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            return label;
        }

        Label Yielding(Label label)
        {
            label.ClipText = true;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }

        WarBar Bar(float height, float width = 0f) => new()
        {
            CustomMinimumSize = new Vector2(width, height),
            SizeFlagsHorizontal = width > 0f ? SizeFlags.ShrinkCenter : SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Track = Ui.Colour("war.track"),
            MarkerColour = Ui.Colour("war.front_line")
        };

        // ------------------------------------------------------------------ the two sides
        var banner = new PanelContainer();
        banner.AddThemeStyleboxOverride("panel", WarInsetStyle(20, 14));
        _warDetailRows.AddChild(banner);
        var bannerRows = new VBoxContainer();
        bannerRows.AddThemeConstantOverride("separation", 10);
        banner.AddChild(bannerRows);

        var faces = new HBoxContainer();
        faces.AddThemeConstantOverride("separation", 16);
        bannerRows.AddChild(faces);

        Control Side(Nation nation, bool attacking)
        {
            var box = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 12);
            var flag = FlagBadge(nation?.Tag ?? "", 46);
            TintBlankFlag(flag, nation);
            var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            words.AddThemeConstantOverride("separation", 0);
            var name = Yielding(Body(nation?.DisplayName ?? "?", 22, mine ? Ui.Colour("text.accent") : Ui.Colour("text.bright")));
            var role = Body(WarSideLine(war, attacking), 12, Ui.Colour("text.caption"));
            var support = Body(Simulation.Loc.Format("war support at home {0}",
                (attacking ? war.AttackerWarSupport : war.DefenderWarSupport).ToString("P0")), 12, Ui.Colour("text.muted"));
            if (!attacking)
            {
                name.HorizontalAlignment = HorizontalAlignment.Right;
                role.HorizontalAlignment = HorizontalAlignment.Right;
                support.HorizontalAlignment = HorizontalAlignment.Right;
            }
            words.AddChild(name);
            words.AddChild(role);
            words.AddChild(support);
            if (attacking) { box.AddChild(flag); box.AddChild(words); }
            else { box.AddChild(words); box.AddChild(flag); }
            return box;
        }

        faces.AddChild(Side(attacker, true));
        var front = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.3f,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        front.AddThemeConstantOverride("separation", 6);
        var standing = Body(WarStanding(war, attacker, defender), 14,
            Mathf.Abs(war.WarScore) < 0.5f ? Ui.Colour("text.body_soft") : NationColour(war.WarScore >= 0f ? attacker : defender));
        standing.HorizontalAlignment = HorizontalAlignment.Center;
        front.AddChild(standing);
        var tug = Bar(24);
        tug.Tug(Mathf.Clamp(war.WarScore / WarLedgerBarScale, -1f, 1f), attackerColour, defenderColour);
        front.AddChild(tug);
        var scale = Body(Simulation.Loc.Format("the front line is drawn at the war score; the end of the bar is {0}",
            WarLedgerBarScale.ToString("0")), 11, Ui.Colour("text.faint"));
        scale.HorizontalAlignment = HorizontalAlignment.Center;
        front.AddChild(scale);
        faces.AddChild(front);
        faces.AddChild(Side(defender, false));

        // Every country on each side, by flag: a war is two sides now, not a pair, and the
        // allies who came are the first thing a player looks for.
        if (war.Side(true).Count() + war.Side(false).Count() > 2)
        {
            var coalitions = new HBoxContainer();
            coalitions.AddThemeConstantOverride("separation", 24);
            bannerRows.AddChild(coalitions);
            HFlowContainer Coalition(bool attacking)
            {
                var flow = new HFlowContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    Alignment = attacking ? FlowContainer.AlignmentMode.Begin : FlowContainer.AlignmentMode.End
                };
                flow.AddThemeConstantOverride("h_separation", 10);
                flow.AddThemeConstantOverride("v_separation", 4);
                foreach (var id in war.Side(attacking))
                {
                    var member = _campaign.NationById(id);
                    if (member is null) continue;
                    var chip = new HBoxContainer();
                    chip.AddThemeConstantOverride("separation", 5);
                    chip.AddChild(FlagBadge(member.Tag, 14, member.DisplayName));
                    chip.AddChild(Body(member.DisplayName, 12, Ui.Colour("text.body_soft")));
                    flow.AddChild(chip);
                }
                return flow;
            }
            coalitions.AddChild(Coalition(true));
            coalitions.AddChild(Coalition(false));
        }

        // ------------------------------------------------------------------ three columns
        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 12);
        _warDetailRows.AddChild(columns);

        VBoxContainer Column(string caption, float ratio)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = ratio };
            panel.AddThemeStyleboxOverride("panel", WarInsetStyle(14, 12));
            columns.AddChild(panel);
            var stack = new VBoxContainer();
            stack.AddThemeConstantOverride("separation", 7);
            panel.AddChild(stack);
            stack.AddChild(Caption(caption));
            return stack;
        }

        // How it is going: what the score is made of, and the two sides' weight at the front.
        var going = Column("HOW IT IS GOING", 1f);
        foreach (var (format, value) in WarScoring.Parts(war))
        {
            var part = new HBoxContainer();
            part.AddThemeConstantOverride("separation", 8);
            part.AddChild(Yielding(Body(Simulation.Loc.Format(format, "").Trim(), 12, Ui.Colour("text.body_soft"))));
            var bar = Bar(8, 110);
            bar.Lean(value / WarLedgerPartScale, attackerColour, defenderColour);
            part.AddChild(bar);
            var figure = Body(value.ToString("+0.0;-0.0;0.0"), 12,
                Mathf.Abs(value) < 0.05f ? Ui.Colour("text.muted") : NationColour(value >= 0f ? attacker : defender));
            figure.HorizontalAlignment = HorizontalAlignment.Right;
            figure.CustomMinimumSize = new Vector2(46, 0);
            part.AddChild(figure);
            going.AddChild(part);
        }

        void Split(VBoxContainer column, string caption, float left, float right, string reading)
        {
            column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
            var head = new HBoxContainer();
            head.AddChild(Yielding(Caption(caption)));
            var figures = Body(reading, 12, Ui.Colour("text.body"));
            figures.HorizontalAlignment = HorizontalAlignment.Right;
            head.AddChild(figures);
            column.AddChild(head);
            var bar = Bar(10);
            var whole = left + right;
            if (whole > 0f) bar.Shares((left / whole, attackerColour), (right / whole, defenderColour));
            column.AddChild(bar);
        }

        Split(going, "PROVINCES ON THE FRONT", war.AttackerFront.Count, war.DefenderFront.Count,
            Simulation.Loc.Format("{0} against {1}", war.AttackerFront.Count.ToString("N0"), war.DefenderFront.Count.ToString("N0")));
        var committed = new Dictionary<int, int>();
        foreach (var army in _campaign.Armies)
            if (army.AssignedWarId == war.Id)
                committed[army.NationId] = committed.GetValueOrDefault(army.NationId) + 1;
        var attackingDivisions = war.Side(true).Sum(id => committed.GetValueOrDefault(id));
        var defendingDivisions = war.Side(false).Sum(id => committed.GetValueOrDefault(id));
        Split(going, "DIVISIONS COMMITTED", attackingDivisions, defendingDivisions,
            Simulation.Loc.Format("{0} against {1}", attackingDivisions.ToString("N0"), defendingDivisions.ToString("N0")));
        if (attacker is not null && defender is not null)
        {
            going.AddChild(new Control { CustomMinimumSize = new Vector2(0, 2) });
            going.AddChild(Yielding(Body(Simulation.Loc.Format("Operational profiles: {0} against {1}",
                AiStrategyCatalog.Resolve(_campaign, war, attacker).Id,
                AiStrategyCatalog.Resolve(_campaign, war, defender).Id), 11, Ui.Colour("text.faint"))));
        }

        // What it has cost: each side's losses as a bar of the killed, the wounded and the
        // captured, drawn to one scale so that the side that has lost more has the longer bar.
        var cost = Column("WHAT IT HAS COST", 1f);
        var killedColour = Ui.Colour("war.killed");
        var woundedColour = Ui.Colour("war.wounded");
        var capturedColour = Ui.Colour("war.captured");
        var heaviest = Mathf.Max(1f, Mathf.Max(war.AttackerLosses, war.DefenderLosses));
        void SideCost(Nation leader, float losses, float killed, float wounded, float captured)
        {
            var head = new HBoxContainer();
            head.AddChild(Yielding(Body(leader?.DisplayName ?? Tr("nobody"), 13, Ui.Colour("text.bright"))));
            var total = Body(Fmt.People(losses), 15, Ui.Colour("text.bright"));
            total.HorizontalAlignment = HorizontalAlignment.Right;
            head.AddChild(total);
            cost.AddChild(head);
            var bar = Bar(10);
            // A war carried over from a save made before losses were broken down has a total
            // and nothing under it; it is drawn as one undivided length rather than as nobody.
            if (killed + wounded + captured >= 1f)
                bar.Shares((killed / heaviest, killedColour), (wounded / heaviest, woundedColour), (captured / heaviest, capturedColour));
            else
                bar.Shares((losses / heaviest, Ui.Colour("text.muted")));
            cost.AddChild(bar);
            if (killed + wounded + captured >= 1f)
                cost.AddChild(Body(Simulation.Loc.Format("{0} killed · {1} wounded · {2} captured",
                    Fmt.People(killed), Fmt.People(wounded), Fmt.People(captured)), 11, Ui.Colour("text.muted")));
            cost.AddChild(new Control { CustomMinimumSize = new Vector2(0, 2) });
        }
        SideCost(attacker, war.AttackerLosses, war.AttackerKilled, war.AttackerWounded, war.AttackerCaptured);
        SideCost(defender, war.DefenderLosses, war.DefenderKilled, war.DefenderWounded, war.DefenderCaptured);

        var key = new HBoxContainer();
        key.AddThemeConstantOverride("separation", 12);
        foreach (var (colour, word) in new[] { (killedColour, "killed"), (woundedColour, "wounded"), (capturedColour, "captured") })
        {
            var swatch = new ColorRect { Color = colour, CustomMinimumSize = new Vector2(9, 9), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            key.AddChild(swatch);
            key.AddChild(RowLabel(word, 11, Ui.Colour("text.caption")));
        }
        cost.AddChild(key);

        var heldFromAttackers = war.Side(true).Sum(id => WarLosses.HeldFrom(war, id));
        var heldFromDefenders = war.Side(false).Sum(id => WarLosses.HeldFrom(war, id));
        if (heldFromAttackers + heldFromDefenders >= 1f)
            Split(cost, "PRISONERS STILL HELD", heldFromAttackers, heldFromDefenders,
                Simulation.Loc.Format("{0} against {1}", Fmt.People(heldFromAttackers), Fmt.People(heldFromDefenders)));

        // Where it is being fought.
        var fought = _campaign.Battles.Count(battle => battle.WarId == war.Id && !battle.Ended);
        var where = Column(Tr("ENGAGEMENTS") + "  ·  " + Simulation.Loc.Format("{0} being fought", fought.ToString("N0")), 1f);
        var battles = _campaign.Battles
            .Where(battle => battle.WarId == war.Id && !battle.Ended)
            .OrderByDescending(battle => battle.AttackerLosses + battle.DefenderLosses)
            .Take(8)
            .ToList();
        if (battles.Count == 0) where.AddChild(Body(Tr("Nothing is in contact."), 13, Ui.Colour("text.muted")));
        foreach (var battle in battles)
        {
            var place = _campaign.ProvinceMap is null
                ? Simulation.Loc.Format("province {0}", battle.ProvinceId.ToString("N0"))
                : BattlePlaceName(battle.ProvinceId);
            var entry = new VBoxContainer();
            entry.AddThemeConstantOverride("separation", 0);
            entry.AddChild(Body(place, 13, Ui.Colour("text.bright")));
            entry.AddChild(Body(
                Simulation.Loc.Format("day {0}", (_campaign.ElapsedDays - battle.StartedDay + 1).ToString("N0")) + "  ·  " +
                Simulation.Loc.Format("{0} casualties", Fmt.People(battle.AttackerLosses + battle.DefenderLosses)) + "  ·  " +
                Simulation.Loc.Get(battle.Outcome), 11, Ui.Colour("text.muted")));
            where.AddChild(entry);
        }

        if (war.LastNegotiation.Length > 0)
        {
            var proposal = new PanelContainer();
            proposal.AddThemeStyleboxOverride("panel", WarInsetStyle(14, 10));
            var stack = new VBoxContainer();
            stack.AddThemeConstantOverride("separation", 4);
            stack.AddChild(Caption("LAST PROPOSAL"));
            stack.AddChild(Body(war.LastNegotiation, 13, Ui.Colour("text.body"), true));
            proposal.AddChild(stack);
            _warDetailRows.AddChild(proposal);
        }

        if (_warDetailBuiltFor != war.Id) BuildWarDetailActions(war, player, mine);
        FitWarLedgerDetail();
        // A paragraph that wraps knows its height only once it has been given its width, which
        // is after this frame's layout: measure again when that has happened.
        GetTree().CreateTimer(0.05).Timeout += FitWarLedgerDetail;
    }

    /// <summary>
    /// The page as tall as what is on it, up to the window: a war nobody is asking the player
    /// to act in is read in half the screen, and the map stays in view under it.
    /// </summary>
    private void FitWarLedgerDetail()
    {
        if (_warLedgerOpenWarId < 0 || !IsInstanceValid(_warLedgerLayer) || !_warLedgerLayer.Visible) return;
        var actions = LiveHeight(_warDetailActions);
        FitWarLedger(LiveHeight(_warDetailRows) + (actions > 0f ? actions + 12f : 0f) + 8f);
    }

    /// <summary>The buttons that act on a war - built once a war, so they can be clicked.</summary>
    private void BuildWarDetailActions(StrategicWar war, Nation player, bool mine)
    {
        _warDetailBuiltFor = war.Id;
        foreach (var child in _warDetailActions.GetChildren()) child.QueueFree();
        if (!mine || war.Ended) return;

        // Asking for help. The automatic call happens the moment war is declared and is
        // over in the same breath; this is the case a player actually wants, which is a war
        // that has gone badly and an ally who was never obliged to be in it.
        var callable = Simulation.Statecraft.CallableAllies(_campaign, war, player.Id);
        if (callable.Count > 0)
        {
            _warDetailActions.AddChild(RowLabel("CALLING FOR HELP", 12, Ui.Colour("text.caption")));
            var calls = new HBoxContainer();
            calls.AddThemeConstantOverride("separation", 6);
            _warDetailActions.AddChild(calls);
            foreach (var allyId in callable.Take(6))
            {
                var friend = _campaign.NationById(allyId);
                if (friend is null) continue;
                var captured = allyId;
                var blocked = Simulation.Statecraft.CallRefusal(_campaign, war, player.Id, allyId);
                var button = ActionButton(friend.DisplayName, () => CallAllyToWar(war, captured), 150, 30);
                button.Disabled = blocked.Length > 0;
                button.TooltipText = Ui.Tip(blocked.Length > 0
                    ? blocked
                    : Simulation.Loc.Format(
                        "Ask {0} to join this war. They are not obliged to - an offensive call is a favour, and refusing one costs goodwill rather than the alliance.",
                        friend.DisplayName));
                calls.AddChild(button);
            }
        }

        // Prisoners. One for one, as many as the smaller side holds - which is why governments
        // that agree on nothing else still agree on this - and at most once a month.
        var holders = war.EnemiesOf(player.Id)
            .Where(id => WarLosses.HeldFrom(war, id) >= 1f)
            .Take(4)
            .ToList();
        if (holders.Count > 0 || WarLosses.HeldFrom(war, player.Id) >= 1f)
        {
            _warDetailActions.AddChild(RowLabel("PRISONERS", 12, Ui.Colour("text.caption")));
            var exchanges = new HBoxContainer();
            exchanges.AddThemeConstantOverride("separation", 6);
            _warDetailActions.AddChild(exchanges);
            foreach (var enemyId in holders.Count > 0 ? holders : war.EnemiesOf(player.Id).Take(1).ToList())
            {
                var enemy = _campaign.NationById(enemyId);
                if (enemy is null) continue;
                var captured = enemyId;
                var refusal = WarLosses.ExchangeRefusal(_campaign, war, player.Id, enemyId);
                var button = ActionButton(Simulation.Loc.Format("Exchange with {0}", enemy.DisplayName),
                    () => ExchangePrisoners(war, captured), 220, 30);
                button.Disabled = refusal.Length > 0;
                var men = MathF.Min(WarLosses.HeldFrom(war, player.Id), WarLosses.HeldFrom(war, enemyId));
                button.TooltipText = Ui.Tip(refusal.Length > 0
                    ? Tr(refusal)
                    : Simulation.Loc.Format(
                        "{0} of our men for {0} of theirs. They go back into the reserve, less the {1} who did not survive captivity.",
                        Fmt.People(men), (1f - _campaign.Defines.PrisonersRepatriatedShare).ToString("P0")));
                exchanges.AddChild(button);
            }
        }

        _warDetailActions.AddChild(RowLabel("ENDING IT", 12, Ui.Colour("text.caption")));
        var conference = ActionButton("Open the peace conference", () => OpenPeaceConference(war), 320, 34, true);
        conference.TooltipText = Ui.Tip(Tr("Draw the settlement on the map: claim the ground you mean to keep, " +
                                 "priced against what the war is actually worth."));
        _warDetailActions.AddChild(conference);

        var quick = new HBoxContainer();
        quick.AddThemeConstantOverride("separation", 6);
        _warDetailActions.AddChild(quick);
        foreach (var terms in new[]
                 {
                     PeaceTerms.Ceasefire, PeaceTerms.WhitePeace, PeaceTerms.WarReparations,
                     PeaceTerms.StatusQuo, PeaceTerms.EnforceWarGoal, PeaceTerms.EnforceDemands, PeaceTerms.Capitulate
                 })
        {
            var captured = terms;
            var blocked = PeaceNegotiation.BlockReason(_campaign, war, player.Id, terms);
            var acceptance = PeaceNegotiation.Acceptance(_campaign, war, player.Id, terms);
            var button = ActionButton(PeaceTermsName(terms), () => ProposePeace(war, captured), 150, 30);
            button.Disabled = blocked.Length > 0;
            button.TooltipText = Ui.Tip(blocked.Length > 0
                ? blocked
                : PeaceNegotiation.Description(terms) + "\n" +
                  Simulation.Loc.Format("Acceptance {0}%.", acceptance.ToString("0")));
            quick.AddChild(button);
        }
    }

    private static string PeaceTermsName(PeaceTerms terms) => terms switch
    {
        PeaceTerms.Ceasefire => "Ceasefire",
        PeaceTerms.WhitePeace => "White peace",
        PeaceTerms.WarReparations => "Reparations",
        PeaceTerms.EnforceWarGoal => "War goal",
        PeaceTerms.EnforceDemands => "Hold occupied",
        PeaceTerms.StatusQuo => "Status quo",
        PeaceTerms.Capitulate => "Capitulate",
        PeaceTerms.TradeConcessions => "Trade concessions",
        PeaceTerms.DemilitarizedSettlement => "Demilitarize",
        _ => terms.ToString()
    };

    /// <summary>Where a province is, in words a player recognises.</summary>
    private string StateNameOfProvince(int provinceId)
    {
        var topology = _campaign.ProvinceMap;
        if (topology is null || provinceId <= 0 || provinceId > topology.ProvinceCount)
            return Simulation.Loc.Format("province {0}", provinceId.ToString("N0"));
        var stateIndex = topology.ProvinceToState[provinceId];
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == stateIndex);
        return state?.DisplayName ?? Simulation.Loc.Format("province {0}", provinceId.ToString("N0"));
    }

    /// <summary>
    /// Where a fight is, as a place: the town it is at when one of the region's towns is near,
    /// and the region when none is. Several battles in one region are then several places.
    /// </summary>
    private string BattlePlaceName(int provinceId) =>
        Simulation.CityCatalog.NearestTown(_campaign.ProvinceMap, provinceId)?.DisplayName ??
        StateNameOfProvince(provinceId);
}
