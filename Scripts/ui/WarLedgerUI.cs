using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>One line in the list of the world's wars, kept so it can be refreshed in place.</summary>
    private sealed class WarLedgerRow
    {
        public Button Button;
        public ColorRect AttackerColour;
        public ColorRect DefenderColour;
        public Label Attacker;
        public Label Defender;
        public Control BarField;
        public ColorRect BarFill;
        public Label Score;
        public Label Casualties;
        public Label Front;
        public Label Age;
        public int WarId = -1;
    }

    private Control _warLedgerLayer = null!;
    private Label _warLedgerSummary = null!;
    private VBoxContainer _warListRows = null!;
    private VBoxContainer _warDetailRows = null!;
    private ScrollContainer _warListScroll = null!;
    private ScrollContainer _warDetailScroll = null!;
    private Button _warFilterAll = null!;
    private Button _warFilterMine = null!;
    private Button _warBackButton = null!;
    private HBoxContainer _warListHeader = null!;
    private readonly List<WarLedgerRow> _warLedgerRows = new();
    private readonly List<StrategicWar> _warLedgerOrder = new();
    private VBoxContainer _warDetailActions = null!;
    private bool _warLedgerMineOnly;
    private int _warLedgerOpenWarId = -1;
    private int _warDetailDay = int.MinValue;
    private int _warDetailBuiltFor = -1;

    /// <summary>How lopsided a war has to be before its bar is drawn full.</summary>
    private const float WarLedgerBarScale = 40f;

    /// <summary>
    /// The world's wars, as a list somebody can read.
    ///
    /// There were a hundred and forty of them and the screen wrote a paragraph about each
    /// one into the side panel: name, belligerents, war goal, doctrines, front size, four
    /// score components, losses, support and the latest contact - nine lines a war, in one
    /// column, unsorted. Everything about every war and nothing you could find.
    ///
    /// So it splits the way Hearts of Iron and Age of History split it. A list of one-line
    /// rows that can be scanned - who, which way it is going, what it has cost, how long it
    /// has run - and then one war at a time on its own page, where the nine lines belong.
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

        var panel = TexturedPanel(_warLedgerLayer);
        Ui.Place(panel, "modal.war_ledger");

        var rows = PanelRows(panel, 22, 16, 9);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        var title = Title("WARS OF THE WORLD", 25, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);

        _warBackButton = ActionButton("← All wars", ShowWarLedgerList, 118, 30);
        _warBackButton.Visible = false;
        header.AddChild(_warBackButton);

        _warFilterAll = ActionButton("Every war", () => SetWarLedgerFilter(false), 108, 30, true);
        header.AddChild(_warFilterAll);
        _warFilterMine = ActionButton("Mine", () => SetWarLedgerFilter(true), 78, 30);
        header.AddChild(_warFilterMine);
        header.AddChild(ActionButton("×", CloseWarLedger, 34, 30));

        _warLedgerSummary = Title("", 13, Ui.Colour("text.body_soft"));
        _warLedgerSummary.CustomMinimumSize = new Vector2(0, 24);
        rows.AddChild(_warLedgerSummary);

        // ------------------------------------------------------------------ the list
        _warListScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        // The captions sit outside the scroll so they stay put while the list moves.
        _warListHeader = new HBoxContainer();
        _warListHeader.AddThemeConstantOverride("separation", 10);
        rows.AddChild(_warListHeader);
        Label Caption(string text, int width, HorizontalAlignment alignment = HorizontalAlignment.Left)
        {
            var label = RowLabel(text, 11, Ui.Colour("text.caption"), width);
            label.HorizontalAlignment = alignment;
            return label;
        }
        _warListHeader.AddChild(Caption("", 5));
        var attackerCaption = Caption("ATTACKER", 0);
        attackerCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _warListHeader.AddChild(attackerCaption);
        _warListHeader.AddChild(Caption("", 24));
        _warListHeader.AddChild(Caption("", 5));
        var defenderCaption = Caption("DEFENDER", 0);
        defenderCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _warListHeader.AddChild(defenderCaption);
        _warListHeader.AddChild(Caption("WHO IS WINNING", 140, HorizontalAlignment.Center));
        _warListHeader.AddChild(Caption("SCORE", 62, HorizontalAlignment.Right));
        _warListHeader.AddChild(Caption("DEAD", 104, HorizontalAlignment.Right));
        _warListHeader.AddChild(Caption("FRONT", 96, HorizontalAlignment.Right));
        _warListHeader.AddChild(Caption("LENGTH", 86, HorizontalAlignment.Right));

        rows.AddChild(_warListScroll);
        _warListRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _warListRows.AddThemeConstantOverride("separation", 3);
        _warListScroll.AddChild(_warListRows);

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
        detail.AddThemeConstantOverride("separation", 6);
        _warDetailScroll.AddChild(detail);

        _warDetailRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _warDetailRows.AddThemeConstantOverride("separation", 6);
        detail.AddChild(_warDetailRows);

        // The buttons that end a war are built once per war and left alone. The figures
        // above them are rebuilt as the war moves, and a control that is torn down and
        // remade between a mouse going down and coming up is a control that cannot be
        // clicked - at speed five that is every fifth of a second.
        _warDetailActions = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _warDetailActions.AddThemeConstantOverride("separation", 6);
        detail.AddChild(_warDetailActions);
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
        _warListHeader.Visible = true;
        _warListScroll.Visible = true;
        _warDetailScroll.Visible = false;
        RefreshWarLedger();
    }

    private void ShowWarLedgerDetail(int warId)
    {
        _warLedgerOpenWarId = warId;
        _warDetailDay = int.MinValue;
        _warDetailBuiltFor = -1;
        _warBackButton.Visible = true;
        _warListHeader.Visible = false;
        _warListScroll.Visible = false;
        _warDetailScroll.Visible = true;
        RefreshWarLedger();
    }

    /// <summary>
    /// The whole screen, whichever half of it is showing.
    ///
    /// Rows are kept and refilled rather than rebuilt: this runs whenever the campaign
    /// changes, and throwing away a hundred and forty buttons a frame to make the same
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

        var worldwide = 0f;
        foreach (var war in _campaign.Wars)
            if (!war.Ended) worldwide += war.AttackerLosses + war.DefenderLosses;
        var fighting = _campaign.Wars.Count(war => !war.Ended);
        _warLedgerSummary.Text = _warLedgerOrder.Count == 0
            ? _warLedgerMineOnly ? "You are at peace." : "The world is at peace."
            : $"{_warLedgerOrder.Count:N0} of {fighting:N0} war(s) shown  ·  " +
              $"{Fmt.People(worldwide)} dead in the wars still being fought  ·  " +
              "click a war for its own page";

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
    }

    private WarLedgerRow CreateWarLedgerRow()
    {
        var row = new WarLedgerRow();
        row.Button = new Button
        {
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true
        };
        row.Button.Pressed += () =>
        {
            if (row.WarId >= 0) ShowWarLedgerDetail(row.WarId);
        };
        _warListRows.AddChild(row.Button);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 0);
        margin.AddThemeConstantOverride("margin_right", 0);
        row.Button.AddChild(margin);

        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 10);
        margin.AddChild(line);

        Label Cell(string text, int width, HorizontalAlignment alignment = HorizontalAlignment.Left)
        {
            var label = RowLabel(text, 13, Ui.Colour("text.body"), width);
            label.MouseFilter = MouseFilterEnum.Ignore;
            label.ClipText = true;
            label.HorizontalAlignment = alignment;
            return label;
        }

        ColorRect Swatch()
        {
            var swatch = new ColorRect
            {
                CustomMinimumSize = new Vector2(5, 18),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore
            };
            line.AddChild(swatch);
            return swatch;
        }

        row.AttackerColour = Swatch();
        row.Attacker = Cell("", 0);
        row.Attacker.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(row.Attacker);

        line.AddChild(Cell("vs", 24, HorizontalAlignment.Center));

        row.DefenderColour = Swatch();
        row.Defender = Cell("", 0);
        row.Defender.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(row.Defender);

        // The bar is drawn by hand rather than laid out: it grows from the middle towards
        // whoever is winning, which no container arrangement says as directly.
        row.BarField = new Control
        {
            CustomMinimumSize = new Vector2(140, 12),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        line.AddChild(row.BarField);
        var track = new ColorRect { Color = Ui.Colour("surface.inset"), MouseFilter = MouseFilterEnum.Ignore };
        track.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.BarField.AddChild(track);
        row.BarFill = new ColorRect { MouseFilter = MouseFilterEnum.Ignore };
        row.BarField.AddChild(row.BarFill);
        var centre = new ColorRect
        {
            Color = Ui.Colour("surface.line"),
            Position = new Vector2(69, 0),
            Size = new Vector2(2, 12),
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.BarField.AddChild(centre);

        row.Score = Cell("", 62, HorizontalAlignment.Right);
        line.AddChild(row.Score);
        row.Casualties = Cell("", 104, HorizontalAlignment.Right);
        line.AddChild(row.Casualties);
        row.Front = Cell("", 96, HorizontalAlignment.Right);
        line.AddChild(row.Front);
        row.Age = Cell("", 86, HorizontalAlignment.Right);
        line.AddChild(row.Age);
        return row;
    }

    private void FillWarLedgerRow(WarLedgerRow row, StrategicWar war)
    {
        var attacker = _campaign.NationById(war.AttackerId);
        var defender = _campaign.NationById(war.DefenderId);
        var mine = _campaign.Player is not null && war.Involves(_campaign.Player.Id);

        row.WarId = war.Id;
        row.AttackerColour.Color = NationColour(attacker);
        row.DefenderColour.Color = NationColour(defender);
        row.Attacker.Text = attacker?.Name ?? "?";
        row.Defender.Text = defender?.Name ?? "?";
        row.Attacker.AddThemeColorOverride("font_color",
            mine ? Ui.Colour("text.accent") : Ui.Colour("text.body"));
        row.Defender.AddThemeColorOverride("font_color",
            mine ? Ui.Colour("text.accent") : Ui.Colour("text.body"));

        // The bar grows from the middle towards whoever is ahead, in that country's own
        // colour - the same colour as the swatch beside their name. Red and green would
        // have to mean something, and in a list of other people's wars they mean nothing.
        var lean = Mathf.Clamp(war.WarScore / WarLedgerBarScale, -1f, 1f);
        var half = 69f;
        var width = Mathf.Abs(lean) * half;
        var leader = lean >= 0f ? row.AttackerColour.Color : row.DefenderColour.Color;
        row.BarFill.Position = new Vector2(lean >= 0f ? half + 1f : half + 1f - width, 1f);
        row.BarFill.Size = new Vector2(Mathf.Max(1f, width), 10f);
        row.BarFill.Color = leader;

        row.Score.Text = war.WarScore.ToString("+0.0;-0.0;0.0");
        row.Score.AddThemeColorOverride("font_color",
            Mathf.Abs(war.WarScore) < 0.05f ? Ui.Colour("text.muted") : leader);
        row.Casualties.Text = Fmt.People(war.AttackerLosses + war.DefenderLosses);
        row.Front.Text = Simulation.Loc.Format("{0:N0} front", war.AttackerFront.Count + war.DefenderFront.Count);
        var days = Math.Max(0, _campaign.ElapsedDays - war.StartedDay);
        row.Age.Text = days >= 730 ? $"{days / 365f:0.#} yr" : $"{days:N0} d";
    }

    private static Color NationColour(Nation nation) =>
        nation is null ? new Color(0.5f, 0.5f, 0.5f) : new Color(nation.Color);

    /// <summary>
    /// One war on its own page: who, what it is for, how it is going and what it has cost.
    ///
    /// This is where the nine lines the list refuses to carry belong, and where the peace
    /// conference is reached from - for this war, rather than for whichever war the player
    /// happened to enter first.
    /// </summary>
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
        RefreshWarLedger();
    }

    private void RefreshWarLedgerDetail()
    {
        var war = _campaign.Wars.Find(candidate => candidate.Id == _warLedgerOpenWarId);
        if (war is null)
        {
            _warLedgerSummary.Text = Tr("That war is over.");
            return;
        }
        // Nothing on this page changes inside a day.
        if (_warDetailDay == _campaign.ElapsedDays && _warDetailBuiltFor == war.Id) return;
        _warDetailDay = _campaign.ElapsedDays;
        foreach (var child in _warDetailRows.GetChildren()) child.QueueFree();

        var attacker = _campaign.NationById(war.AttackerId);
        var defender = _campaign.NationById(war.DefenderId);
        var player = _campaign.Player;
        var mine = player is not null && war.Involves(player.Id);
        var days = Math.Max(0, _campaign.ElapsedDays - war.StartedDay);
        _warLedgerSummary.Text = war.Name + "  ·  " +
                                 (war.Ended
                                     ? Tr("settled")
                                     : Simulation.Loc.Format("{0:N0} day(s) of fighting", days)) +
                                 (mine ? Tr("  ·  you are a belligerent") : "");

        void Heading(string text) =>
            _warDetailRows.AddChild(RowLabel(text, 12, Ui.Colour("text.caption")));

        void Line(string text, Color? colour = null)
        {
            var label = Title(text, 14, colour ?? Ui.Colour("text.body"));
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _warDetailRows.AddChild(label);
        }

        Heading("BELLIGERENTS");
        // Both sides in full. A war used to be a pair of countries and an alliance answering
        // one produced a war per member; now it is one war with two sides, and the ledger is
        // where that has to be visible or it may as well not be true.
        string SideNames(bool attacking) => string.Join(", ", war.Side(attacking)
            .Select(id => _campaign.NationById(id)?.Name)
            .Where(name => !string.IsNullOrEmpty(name)));
        Line($"{SideNames(true)}   against   {SideNames(false)}");

        var objective = _campaign.States.FirstOrDefault(state => state.MapIndex == war.ClaimedStateMapIndex);
        Line($"War goal: {objective?.Name ?? "strategic supremacy"}");
        if (attacker is not null && defender is not null)
            Line($"Operational profiles: {AiStrategyCatalog.Resolve(_campaign, war, attacker).Id} " +
                 $"against {AiStrategyCatalog.Resolve(_campaign, war, defender).Id}");

        Heading("HOW IT IS GOING");
        Line($"War score {war.WarScore:+0.0;-0.0;0.0} " +
             $"towards {(war.WarScore >= 0f ? attacker?.Name : defender?.Name) ?? "nobody"}",
             NationColour(war.WarScore >= 0f ? attacker : defender));
        Line($"    occupation {war.OccupationScore:+0.0;-0.0;0.0}  ·  " +
             $"battles {war.BattleScore:+0.0;-0.0;0.0}  ·  " +
             $"objective {war.ObjectiveScore:+0.0;-0.0;0.0}  ·  " +
             $"home support {war.SupportScore:+0.0;-0.0;0.0}");
        Line($"Front: {war.AttackerFront.Count:N0} attacking province(s) against " +
             $"{war.DefenderFront.Count:N0} defending");

        var committed = new Dictionary<int, int>();
        foreach (var army in _campaign.Armies)
            if (army.AssignedWarId == war.Id)
                committed[army.NationId] = committed.GetValueOrDefault(army.NationId) + 1;
        var attackingDivisions = war.Side(true).Sum(id => committed.GetValueOrDefault(id));
        var defendingDivisions = war.Side(false).Sum(id => committed.GetValueOrDefault(id));
        Line($"Divisions committed: {attackingDivisions:N0} against {defendingDivisions:N0}");

        Heading("WHAT IT HAS COST");
        Line($"Dead: {Fmt.People(war.AttackerLosses)} against {Fmt.People(war.DefenderLosses)}");
        Line($"War support at home: {war.AttackerWarSupport:P0} against {war.DefenderWarSupport:P0}");

        var battles = _campaign.Battles
            .Where(battle => battle.WarId == war.Id && !battle.Ended)
            .OrderByDescending(battle => battle.AttackerLosses + battle.DefenderLosses)
            .Take(8)
            .ToList();
        Heading($"ENGAGEMENTS  ·  {_campaign.Battles.Count(battle => battle.WarId == war.Id && !battle.Ended):N0} being fought");
        if (battles.Count == 0) Line("Nothing is in contact.");
        foreach (var battle in battles)
        {
            var place = _campaign.ProvinceMap is null
                ? $"province {battle.ProvinceId:N0}"
                : StateNameOfProvince(battle.ProvinceId);
            Line($"{place}  ·  day {_campaign.ElapsedDays - battle.StartedDay + 1:N0}  ·  " +
                 $"{Fmt.People(battle.AttackerLosses + battle.DefenderLosses)} dead  ·  {battle.Outcome}");
        }

        if (war.LastNegotiation.Length > 0)
        {
            Heading("LAST PROPOSAL");
            Line(war.LastNegotiation);
        }

        if (_warDetailBuiltFor == war.Id) return;
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
                var button = ActionButton(friend.Name, () => CallAllyToWar(war, captured), 150, 30);
                button.Disabled = blocked.Length > 0;
                button.TooltipText = Ui.Tip(blocked.Length > 0
                    ? blocked
                    : Simulation.Loc.Format(
                        "Ask {0} to join this war. They are not obliged to - an offensive call is a favour, and refusing one costs goodwill rather than the alliance.",
                        friend.Name));
                calls.AddChild(button);
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
                     PeaceTerms.EnforceWarGoal, PeaceTerms.EnforceDemands, PeaceTerms.Capitulate
                 })
        {
            var captured = terms;
            var blocked = PeaceNegotiation.BlockReason(_campaign, war, player.Id, terms);
            var acceptance = PeaceNegotiation.Acceptance(_campaign, war, player.Id, terms);
            var button = ActionButton(PeaceTermsName(terms), () => ProposePeace(war, captured), 150, 30);
            button.Disabled = blocked.Length > 0;
            button.TooltipText = Ui.Tip(blocked.Length > 0
                ? blocked
                : $"{PeaceNegotiation.Description(terms)}\nAcceptance {acceptance:0}%.");
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
            return $"province {provinceId:N0}";
        var stateIndex = topology.ProvinceToState[provinceId];
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == stateIndex);
        return state?.Name ?? $"province {provinceId:N0}";
    }
}
