using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The army panel, built the way Hearts of Iron builds one.
///
/// The commands were all here before - orders, posture, reinforcement priority, army
/// groups, plan execution - but they were eighteen identical word-buttons stacked in a
/// generic side panel, above a paragraph of statistics about a selection the player could
/// not see. A player commanding forty divisions could not tell which ones they had, which
/// of them was broken, or which one was about to be overrun.
///
/// So the panel is a roster. Every selected division is a row with its counter, its name,
/// how much fight it has left and how many men, and the row itself is how you select,
/// inspect and re-select. Orders are a compact bar of marks along the bottom, the way a
/// battle-plan toolbar reads, rather than a wall of words - and every one of them still
/// says in full what it does when you rest on it, because a familiar interface should not
/// also be a guessing game.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _armyPanel = null!;
    private Label _armyPanelTitle = null!;
    private Label _armyPanelSubtitle = null!;
    private Label _armyPanelCount = null!;
    private HBoxContainer _armyComposition = null!;
    private VBoxContainer _armyRoster = null!;
    private Label _armyRosterHint = null!;
    private readonly Dictionary<int, Button> _armyRosterRows = new();
    private readonly Dictionary<ArmyOrder, Button> _armyOrderButtons = new();
    private readonly Dictionary<ArmyStance, Button> _armyStanceButtons = new();
    private int _lastRosterClickId = -1;
    private ulong _lastRosterClickMsec;

    /// <summary>
    /// NATO-ish counters. A player who has played a Paradox game reads these before they
    /// read the name next to them, which is the whole point of having them.
    /// </summary>
    private static string CounterGlyph(string counterSymbol) => counterSymbol switch
    {
        "armor" => "▬",
        "mechanized" => "◈",
        "motorized" => "⊙",
        "artillery" => "●",
        "cavalry" => "╱",
        _ => "✕"
    };

    private void BuildArmyPanel()
    {
        _armyPanel = TexturedPanel(_hudLayer, PanelSurface.Command);
        Ui.Place(_armyPanel, "hud.army_panel");
        _armyPanel.Visible = false;

        var shell = PanelRows(_armyPanel, 16, 12, 6);

        // ---- header: which army, who commands it, how much of it is selected ----------
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        shell.AddChild(header);

        var identity = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        identity.AddThemeConstantOverride("separation", 1);
        header.AddChild(identity);
        _armyPanelTitle = Title("Army", 19, Ui.Colour("text.accent"));
        _armyPanelTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        identity.AddChild(_armyPanelTitle);
        _armyPanelSubtitle = Title("", 12, Ui.Colour("text.muted"));
        _armyPanelSubtitle.AutowrapMode = TextServer.AutowrapMode.Off;
        identity.AddChild(_armyPanelSubtitle);

        _armyPanelCount = Title("", 15, Ui.Colour("text.bright"));
        _armyPanelCount.HorizontalAlignment = HorizontalAlignment.Right;
        _armyPanelCount.AutowrapMode = TextServer.AutowrapMode.Off;
        _armyPanelCount.CustomMinimumSize = new Vector2(96, 0);
        header.AddChild(_armyPanelCount);
        header.AddChild(ActionButton("×", ClearArmySelectionAndClose, 32, 28));

        // ---- composition: what the selection is made of, at a glance ------------------
        _armyComposition = new HBoxContainer();
        _armyComposition.AddThemeConstantOverride("separation", 12);
        shell.AddChild(_armyComposition);

        // ---- the roster ---------------------------------------------------------------
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 150),
            SizeFlagsStretchRatio = 0.75f
        };
        shell.AddChild(scroll);
        _armyRoster = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _armyRoster.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_armyRoster);

        _armyRosterHint = Title(
            "Click a division to command it alone · Ctrl+click to add or remove · double-click to take every division of the same template",
            11, Ui.Colour("text.faint"));
        _armyRosterHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        shell.AddChild(_armyRosterHint);

        // The command deck has many useful controls, but it must never dictate the
        // panel's screen-space height. Keeping it in its own scroll viewport lets the
        // data-driven HUD rectangle remain authoritative at every supported resolution.
        // The roster remains independently scrollable, so selecting seventy divisions
        // does not push the battle-plan buttons below the map controls.
        var commandScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.45f,
            CustomMinimumSize = new Vector2(0, 250),
            FollowFocus = true
        };
        shell.AddChild(commandScroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 6);
        commandScroll.AddChild(rows);

        // ---- battle plans -------------------------------------------------------------
        rows.AddChild(Title("BATTLE PLANS", 11, Ui.Colour("text.caption")));
        var plans = new HBoxContainer();
        plans.AddThemeConstantOverride("separation", 4);
        rows.AddChild(plans);
        AddOrderButton(plans, "✋", ArmyOrder.Manual, "Halt",
            "Cancel the route or plan and hold the present province.");
        AddOrderButton(plans, "▬", ArmyOrder.DefendFront, "Front line",
            "Hold the assigned sector of the front. Entrench, recover, and repel what comes.");
        AddOrderButton(plans, "➤", ArmyOrder.AdvanceFront, "Offensive line",
            "Push the front forward across its whole width once the plan is ready.");
        AddOrderButton(plans, "➤➤", ArmyOrder.Spearhead, "Spearhead",
            "Drive at the last selected enemy region, or at the war goal if none was picked.");
        AddOrderButton(plans, "⌂", ArmyOrder.ReserveFront, "Reserve",
            "Stand behind the contact line, recover faster and reinforce defensive battles nearby.");

        var execution = new HBoxContainer();
        execution.AddThemeConstantOverride("separation", 4);
        rows.AddChild(execution);
        var prepare = ActionButton("Prepare", () => IssuePlanExecution(false), 96, 30);
        prepare.TooltipText = "Hold offensive formations on their sector so they entrench and accumulate a plan.";
        execution.AddChild(prepare);
        var execute = ActionButton("Execute ▶", () => IssuePlanExecution(true), 110, 30, true);
        execute.TooltipText = "Activate the selected offensive plans. Front-line and reserve orders stay as they are.";
        execution.AddChild(execute);
        var autoFront = ActionButton("Auto-deploy", AutoDeployFront, 110, 30);
        autoFront.TooltipText = "Spread the selection across unique sectors of the active front.";
        execution.AddChild(autoFront);

        // ---- the brush ----------------------------------------------------------------
        // An order names a role; a drawn line says where. The three buttons pick which
        // kind of line the brush is drawing, and pressing the same one again commits it.
        rows.AddChild(Title("DRAW ON THE MAP", 11, Ui.Colour("text.caption")));
        var draw = new HBoxContainer();
        draw.AddThemeConstantOverride("separation", 4);
        rows.AddChild(draw);
        AddDrawButton(draw, "▬ Front", FrontLineKind.Defence, "Front line",
            "Draw the line to hold. The selection spreads along its length and digs in.");
        AddDrawButton(draw, "➤ Attack", FrontLineKind.Offensive, "Offensive line",
            "Draw a line across enemy ground. The selection attacks towards it once executed.");
        AddDrawButton(draw, "⌂ Fall back", FrontLineKind.Fallback, "Fallback line",
            "Draw the line to hold if the one in front gives way.");
        var dropLine = ActionButton("Erase", ClearDrawnLine, 74, 30);
        dropLine.TooltipText = "Take the selection off its drawn line and return it to the war's own front.";
        draw.AddChild(dropLine);

        _drawFeedback = Title("Pick a line, then drag along the map. Right button rubs out.",
            11, Ui.Colour("text.muted"));
        _drawFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_drawFeedback);

        // ---- posture and reinforcement -------------------------------------------------
        rows.AddChild(Title("POSTURE", 11, Ui.Colour("text.caption")));
        var posture = new HBoxContainer();
        posture.AddThemeConstantOverride("separation", 4);
        rows.AddChild(posture);
        AddStanceButton(posture, "Cautious", ArmyStance.Cautious,
            "Slower and more careful: better recovery, entrenchment and defence, fewer casualties.");
        AddStanceButton(posture, "Balanced", ArmyStance.Balanced,
            "No modifier either way. The right choice when supply or local superiority is uncertain.");
        AddStanceButton(posture, "Aggressive", ArmyStance.Aggressive,
            "Faster and harder-hitting, at the cost of recovery, defence and heavier casualties.");

        rows.AddChild(Title("REINFORCEMENT PRIORITY", 11, Ui.Colour("text.caption")));
        var priority = new HBoxContainer();
        priority.AddThemeConstantOverride("separation", 4);
        rows.AddChild(priority);
        AddReinforcementPriorityButton(priority, "Low", ReinforcementPriority.Low,
            "0.45 weight. For quiet sectors and formations that are training rather than fighting.");
        AddReinforcementPriorityButton(priority, "Normal", ReinforcementPriority.Normal,
            "Standard weight. The sustainable allocation.", true);
        AddReinforcementPriorityButton(priority, "High", ReinforcementPriority.High,
            "1.90 weight. These recover first, and starve the lower priorities when reserves are short.");

        // ---- organisation --------------------------------------------------------------
        rows.AddChild(Title("ARMY", 11, Ui.Colour("text.caption")));
        var grouping = new HBoxContainer();
        grouping.AddThemeConstantOverride("separation", 4);
        rows.AddChild(grouping);
        var newGroup = ActionButton("New HQ", CreateGroupFromSelection, 84, 28, true);
        newGroup.TooltipText = "Raise a new army headquarters and attach every selected division to it.";
        grouping.AddChild(newGroup);
        var nextGroup = ActionButton("Next HQ", AssignSelectionToNextGroup, 84, 28);
        nextGroup.TooltipText = "Move the selection to the next existing army headquarters.";
        grouping.AddChild(nextGroup);
        var ungroup = ActionButton("Detach", () => AssignSelectionToGroup(-1), 78, 28);
        ungroup.TooltipText = "Remove the selection from its army while leaving it on the map.";
        grouping.AddChild(ungroup);
        var selectHq = ActionButton("Whole HQ", SelectCurrentArmyGroup, 88, 28);
        selectHq.TooltipText = "Select every division attached to the lead division's army headquarters.";
        grouping.AddChild(selectHq);

        var selection = new HBoxContainer();
        selection.AddThemeConstantOverride("separation", 4);
        rows.AddChild(selection);
        var everything = ActionButton("Every division", SelectAllPlayerArmies, 118, 28);
        everything.TooltipText = "Take every field division your country has into the roster.";
        selection.AddChild(everything);
        var dropLead = ActionButton("Drop lead", RemoveLeadFromSelection, 96, 28);
        dropLead.TooltipText = "Remove the lead division from the selection without moving or disbanding it.";
        selection.AddChild(dropLead);
        var region = ActionButton("Region", ShowSelectedArmyRegion, 96, 28);
        region.TooltipText = "Open the demographic and economic inspector for the ground this division is on.";
        selection.AddChild(region);

        _armyCommandFeedback = Title("Right-click to march or attack · Shift+right-click queues a waypoint",
            11, Ui.Colour("text.body_soft"));
        _armyCommandFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_armyCommandFeedback);

        // The old order controls lived in the generic side panel; the panel is that
        // container now, so the visibility flags the rest of the HUD sets still land.
        _armyOrders = rows;
    }

    private void AddDrawButton(Container parent, string label, FrontLineKind kind, string name, string help)
    {
        var button = ActionButton(label, () => ToggleDrawing(kind), 92, 30);
        button.TooltipText = $"{name}\n{help}";
        _drawButtons[kind] = button;
        parent.AddChild(button);
    }

    private void AddOrderButton(Container parent, string glyph, ArmyOrder order, string name, string help)
    {
        var button = ActionButton(glyph, () => IssueArmyOrder(order), 56, 34);
        button.TooltipText = $"{name}\n{help}";
        _armyOrderButtons[order] = button;
        parent.AddChild(button);
    }

    private void AddStanceButton(Container parent, string caption, ArmyStance stance, string help)
    {
        var button = ActionButton(caption, () => IssueArmyStance(stance), 96, 28);
        button.TooltipText = help;
        _armyStanceButtons[stance] = button;
        parent.AddChild(button);
    }

    /// <summary>Show the panel, or hide it and let the generic side panel have the space.</summary>
    private void SetArmyPanelVisible(bool visible)
    {
        if (!visible) EndDrawing();
        if (_armyPanel is null) return;
        _armyPanel.Visible = visible;
        if (!visible) _armyRosterRows.Clear();
    }

    /// <summary>
    /// Rebuild the roster for the current selection. Called on every campaign refresh, so
    /// the bars a player is watching move while the battle they are watching is fought.
    /// </summary>
    private void RefreshArmyPanel()
    {
        if (_armyPanel is null || _campaign?.Player is null) return;
        var divisions = _selectedArmies.Count > 0
            ? _selectedArmies.Where(army => army is not null).ToList()
            : _selectedArmy is null ? new List<Army>() : new List<Army> { _selectedArmy };
        if (divisions.Count == 0) { SetArmyPanelVisible(false); return; }

        var owner = _campaign.NationById(divisions[0].NationId);
        var mine = owner == _campaign.Player;
        SetArmyPanelVisible(true);

        var group = _campaign.ArmyGroups.FirstOrDefault(candidate => candidate.Id == divisions[0].ArmyGroupId);
        _armyPanelTitle.Text = divisions.Count == 1
            ? divisions[0].Name
            : group?.Name ?? $"{owner?.Tag ?? "?"} field forces";
        _armyPanelSubtitle.Text = divisions.Count == 1
            ? OneLineSummary(divisions[0])
            : $"{group?.Commander ?? "No commander assigned"} · " +
              $"{divisions.Sum(army => army.Personnel):N0} men · " +
              $"{divisions.Select(army => army.ArmyGroupId).Distinct().Count()} headquarters";
        _armyPanelCount.Text = divisions.Count == 1 ? "1 selected" : $"{divisions.Count} selected";

        RefreshComposition(divisions);
        RefreshRoster(divisions, mine);

        _armyRosterHint.Visible = mine;
        foreach (var (order, button) in _armyOrderButtons)
        {
            button.Disabled = !mine;
            StyleButton(button, mine && divisions.All(army => army.OrderKind == order));
        }
        foreach (var (stance, button) in _armyStanceButtons)
        {
            button.Disabled = !mine;
            StyleButton(button, mine && divisions.All(army => army.Stance == stance));
        }
        ApplyArmyPriorityButtonStates();
    }

    private void RefreshComposition(List<Army> divisions)
    {
        foreach (var child in _armyComposition.GetChildren())
        {
            _armyComposition.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var group in divisions
                     .GroupBy(army => army.TemplateId)
                     .OrderByDescending(group => group.Count()))
        {
            var template = UnitTypeCatalog.Get(group.Key);
            var entry = Title($"{CounterGlyph(template.CounterSymbol)} {group.Count()}", 13, Ui.Colour("text.body_soft"));
            entry.TooltipText = $"{group.Count()} × {template.Name}\n" +
                                $"Soft attack {template.SoftAttack:0.##} · hard attack {template.HardAttack:0.##}\n" +
                                $"Defence {template.Defense:0.##} · breakthrough {template.Breakthrough:0.##}\n" +
                                $"Armour {template.Armour:0.##} · piercing {template.Piercing:0.##}\n" +
                                $"Frontage {template.CombatWidth:0.#} of the 80 a province gives";
            entry.MouseFilter = MouseFilterEnum.Stop;
            entry.AutowrapMode = TextServer.AutowrapMode.Off;
            _armyComposition.AddChild(entry);
        }
    }

    /// <summary>
    /// One row per division: counter, name, organisation, strength. The bars are the point -
    /// forty rows of numbers is a spreadsheet, forty rows of bars is a situation.
    /// </summary>
    private void RefreshRoster(List<Army> divisions, bool mine)
    {
        foreach (var child in _armyRoster.GetChildren())
        {
            _armyRoster.RemoveChild(child);
            child.QueueFree();
        }
        _armyRosterRows.Clear();

        foreach (var army in divisions.OrderBy(army => army.Name, System.StringComparer.Ordinal).Take(120))
        {
            var template = UnitTypeCatalog.Get(army.TemplateId);

            // The row is a container, not a button with things floating on top of it: the
            // name is the button, the bars and the mark are its siblings. Anchoring the
            // bars inside the button pushed the row wider than the panel and clipped the
            // right-hand bar off the edge.
            var line = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            line.AddThemeConstantOverride("separation", 6);
            _armyRoster.AddChild(line);

            var row = new Button
            {
                // The name gives way, not the bars: a division whose name is cut short is
                // still readable, a strength bar clipped off the panel edge is not.
                CustomMinimumSize = new Vector2(120, 26),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = 1f,
                Alignment = HorizontalAlignment.Left,
                ClipText = true,
                TooltipText = DivisionTooltip(army),
                Flat = true,
                Text = $" {CounterGlyph(template.CounterSymbol)}  {Shorten(army.Name, 20)}"
            };
            row.AddThemeColorOverride("font_color", Ui.Colour("text.body"));
            row.AddThemeColorOverride("font_hover_color", Ui.Colour("button.text_hover"));
            row.AddThemeFontSizeOverride("font_size", 12);
            var captured = army;
            row.Pressed += () => OnRosterRowPressed(captured);
            line.AddChild(row);
            _armyRosterRows[army.Id] = row;

            line.AddChild(Bar(army.Organisation, 54,
                army.Organisation < 0.35f ? "state.negative" :
                army.InBattle ? "state.warning" : "state.info",
                $"Organisation {army.Organisation:P0} — how much fight is left in it"));
            line.AddChild(Bar(army.Strength, 40,
                army.Strength < 0.55f ? "state.warning" : "state.positive",
                $"Fighting strength {army.Strength:P0} — {army.Personnel:N0} of {army.MaxPersonnel:N0} men"));
            line.AddChild(StatusPip(army));
        }

        if (divisions.Count > 120)
            _armyRoster.AddChild(Title($"…and {divisions.Count - 120} more", 11, Ui.Colour("text.faint")));
    }

    /// <summary>
    /// A thin filled bar. Organisation and strength read faster as two bars than as
    /// "84 % / 61 %", and forty of them read faster still. The fill is anchored rather
    /// than sized, so the proportion is the layout instead of a number recomputed by hand.
    /// </summary>
    private Control Bar(float value, int width, string fillToken, string tooltip)
    {
        var frame = new Control
        {
            CustomMinimumSize = new Vector2(width, 8),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = tooltip
        };

        var back = new ColorRect { Color = Ui.Colour("surface.inset"), MouseFilter = MouseFilterEnum.Ignore };
        back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddChild(back);

        var fill = new ColorRect { Color = Ui.Colour(fillToken), MouseFilter = MouseFilterEnum.Ignore };
        fill.AnchorLeft = 0f;
        fill.AnchorTop = 0f;
        fill.AnchorBottom = 1f;
        fill.AnchorRight = Mathf.Clamp(value, 0f, 1f);
        fill.OffsetLeft = 0f;
        fill.OffsetTop = 0f;
        fill.OffsetRight = 0f;
        fill.OffsetBottom = 0f;
        frame.AddChild(fill);
        return frame;
    }

    /// <summary>One mark that says what is wrong with this division, if anything is.</summary>
    private Control StatusPip(Army army)
    {
        var (glyph, token, help) =
            army.CutOff ? ("⊘", "state.negative", "Cut off from supply") :
            army.InBattle ? ("⚔", "state.warning", "In battle") :
            army.SupplyLevel < 0.45f ? ("△", "state.warning", "Short of supply") :
            army.Organisation < 0.35f ? ("↓", "state.critical", "Organisation is spent") :
            army.IsMoving ? ("→", "state.info", "On the march") :
            IsOffensive(army) && army.PlanActive ? ("▶", "state.positive", "Plan executing") :
            IsOffensive(army) ? ("◷", "text.muted", "Plan preparing") :
            ("▪", "text.faint", "Holding");

        var pip = Title(glyph, 12, Ui.Colour(token));
        pip.CustomMinimumSize = new Vector2(16, 0);
        pip.HorizontalAlignment = HorizontalAlignment.Center;
        pip.TooltipText = help;
        pip.MouseFilter = MouseFilterEnum.Stop;
        return pip;
    }

    /// <summary>
    /// The fields Hearts of Iron puts in a division tooltip, because they are the ones a
    /// player actually decides on: what it can still take, how many men are left, whether
    /// it is being fed, and how ready its plan is.
    /// </summary>
    private string DivisionTooltip(Army army)
    {
        var template = UnitTypeCatalog.Get(army.TemplateId);
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == army.StateMapIndex);
        var text =
            $"{army.Name}\n{template.Name}\n\n" +
            $"Organisation: {army.Organisation:P0} of {template.Organisation:P0}\n" +
            $"Fighting strength: {army.Strength:P0}\n" +
            $"Manpower: {army.Personnel:N0} of {army.MaxPersonnel:N0}\n" +
            $"Equipment: {army.Equipment:P0} · experience: {army.Experience:P0}\n" +
            $"Supply: {army.SupplyLevel:P0}" + (army.CutOff ? " — CUT OFF\n" : "\n") +
            $"Entrenchment: {army.Entrenchment:P0}\n";
        if (IsOffensive(army))
            text += $"Plan preparation: {army.Planning:P0} " +
                    $"(up to +{_campaign.Defines.MaxPlanningAttackBonus:P0} attack)\n";
        text += $"\n{state?.Name ?? "Unmapped ground"} · province #{army.ProvinceId:N0}\n" +
                $"Order: {OrderName(army.OrderKind)} · posture: {StanceName(army.Stance)}";
        if (army.InBattle) text += $"\nEngaged in battle #{army.BattleId}";
        return text;
    }

    private string OneLineSummary(Army army)
    {
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == army.StateMapIndex);
        return $"{UnitTypeCatalog.Get(army.TemplateId).Name} · {state?.Name ?? "unmapped"} · " +
               $"{army.Personnel:N0} men";
    }

    private static string Shorten(string text, int limit) =>
        string.IsNullOrEmpty(text) || text.Length <= limit ? text ?? "" : text[..(limit - 1)] + "…";

    /// <summary>
    /// Roster click semantics, matching what a Paradox player's hands already do: a click
    /// commands that division alone, Ctrl adds or removes it, and a double-click takes
    /// every division built to the same template - which is how you find all your armour.
    /// </summary>
    private void OnRosterRowPressed(Army army)
    {
        if (army is null || _campaign?.Player is null) return;
        var now = Time.GetTicksMsec();
        var doubleClick = _lastRosterClickId == army.Id && now - _lastRosterClickMsec < 400;
        _lastRosterClickId = army.Id;
        _lastRosterClickMsec = now;

        if (doubleClick)
        {
            var template = army.TemplateId;
            _selectedArmies.Clear();
            _selectedArmies.AddRange(_campaign.Armies.Where(candidate =>
                candidate.NationId == _campaign.Player.Id && candidate.TemplateId == template));
            _selectedArmy = _selectedArmies.FirstOrDefault();
            SetArmyFeedback($"{_selectedArmies.Count} division(s) of {UnitTypeCatalog.Get(template).Name} selected.");
        }
        else if (Input.IsKeyPressed(Key.Ctrl))
        {
            if (_selectedArmies.Contains(army)) _selectedArmies.Remove(army);
            else _selectedArmies.Add(army);
            if (_selectedArmies.Count == 0) { ClearArmySelectionAndClose(); return; }
            _selectedArmy = _selectedArmies[0];
            SetArmyFeedback($"{_selectedArmies.Count} division(s) selected.");
        }
        else
        {
            _selectedArmies.Clear();
            _selectedArmies.Add(army);
            _selectedArmy = army;
            _globe.FocusOn(army.Latitude, army.Longitude, 6.8f);
            SetArmyFeedback($"{army.Name} has the orders.");
        }

        _globe.SelectArmies(_selectedArmies.Select(candidate => candidate.Id));
        RefreshArmyPanel();
    }
}
