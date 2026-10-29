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
    private readonly List<Button> _armyCaptionIconButtons = new();
    private int _lastRosterClickId = -1;
    private ulong _lastRosterClickMsec;
    private Button _withdrawSelectedButton;
    private VBoxContainer _armyAssaultReadiness;
    private Label _armyAssaultReadinessText;

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
        _armyCaptionIconButtons.Clear();
        _armyPanel = TexturedPanel(_hudLayer, PanelSurface.Command);
        Ui.Place(_armyPanel, "hud.army_panel");
        _armyPanel.Visible = false;

        // panel_command_frame.png has an ornamental bevel inside the StyleBox content
        // rectangle. Twenty-two semantic pixels keep text, scroll bars and tab controls
        // within the *visible* rail, rather than merely within the Control's outer box.
        var shell = PanelRows(_armyPanel, 22, 16, 6);
        shell.ClipContents = true;

        // ---- header: which army, who commands it, how much of it is selected ----------
        var header = new HBoxContainer { ClipContents = true };
        header.AddThemeConstantOverride("separation", 8);
        shell.AddChild(header);

        _armyPanelTitle = Title("Army", (int)Ui.Metric("ui.font.title", 20), Ui.Colour("text.accent"));
        _armyPanelTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _armyPanelTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        _armyPanelTitle.ClipText = true;
        header.AddChild(_armyPanelTitle);

        _armyPanelCount = Title("", (int)Ui.Metric("ui.font.heading", 16), Ui.Colour("text.bright"));
        // A count of divisions is a figure, so it is set in the tabular face: a right-aligned
        // count whose digits change width as the selection grows reads as a twitch.
        _armyPanelCount.AddThemeFontOverride("font", Ui.NumberFont);
        _armyPanelCount.HorizontalAlignment = HorizontalAlignment.Right;
        _armyPanelCount.AutowrapMode = TextServer.AutowrapMode.Off;
        _armyPanelCount.ClipText = true;
        _armyPanelCount.CustomMinimumSize = new Vector2(84, 0);
        header.AddChild(_armyPanelCount);
        header.AddChild(HandbookButton("orders"));
        header.AddChild(ActionButton("×", ClearArmySelectionAndClose, 32, 28));

        // The descriptive line gets the complete safe width instead of fighting the
        // selection count and close button in the header. Long commander names wrap
        // to a second line and are then clipped, never painted through the frame.
        _armyPanelSubtitle = Title("", (int)Ui.Metric("ui.font.body", 12), Ui.Colour("text.muted"));
        _armyPanelSubtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _armyPanelSubtitle.ClipText = true;
        _armyPanelSubtitle.MaxLinesVisible = 2;
        _armyPanelSubtitle.CustomMinimumSize = new Vector2(0, 18);
        shell.AddChild(_armyPanelSubtitle);

        // ---- composition: what the selection is made of, at a glance ------------------
        _armyComposition = new HBoxContainer { ClipContents = true };
        _armyComposition.AddThemeConstantOverride("separation", 12);
        shell.AddChild(_armyComposition);

        // ---- the roster ---------------------------------------------------------------
        var rosterFrame = new PanelContainer
        {
            Name = "ArmyRosterFrame",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 76),
            SizeFlagsStretchRatio = 0.75f
        };
        rosterFrame.SetMeta("atlas_section_ground", "surface.inset");
        rosterFrame.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(rosterFrame, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        shell.AddChild(rosterFrame);
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            ClipContents = true,
            FollowFocus = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        rosterFrame.AddChild(scroll);
        _armyRoster = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _armyRoster.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(_armyRoster);

        _armyRosterHint = Title(
            "Click a division to command it alone · Ctrl+click to add or remove · double-click to take every division of the same template",
            11, Ui.Colour("text.faint"));
        _armyRosterHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _armyRosterHint.ClipText = true;
        _armyRosterHint.MaxLinesVisible = 2;
        _armyRosterHint.CustomMinimumSize = new Vector2(0, 34);
        shell.AddChild(_armyRosterHint);

        // Commands are split into three short pages. The former single 430 px column
        // looked acceptable with seven divisions, then silently continued below the
        // panel frame with a large selection. Tabs keep every control inside a stable
        // surface and leave the roster the flexible part of the panel.
        var commandTabs = new TabContainer
        {
            Name = "ArmyCommandTabs",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 180),
            ClipContents = true
        };
        commandTabs.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button", 12));
        shell.AddChild(commandTabs);

        var plansPage = CommandPage(commandTabs, "ORDERS");
        var doctrinePage = CommandPage(commandTabs, "STANCE");
        var organisationPage = CommandPage(commandTabs, "ORGANISE");
        // The country's book of war belongs with the army rather than with the laws: it is paid
        // for in the experience the army's own formations earn, and it is the army that reads it.
        var warBookPage = CommandPage(commandTabs, "DOCTRINES");
        _armyCommandTabs = commandTabs;
        _armyDoctrineTab = commandTabs.GetTabIdxFromControl((Control)warBookPage.GetParent());
        _armyDoctrineRows = new VBoxContainer { Name = "DoctrineRows" };
        _armyDoctrineRows.AddThemeConstantOverride("separation", 8);
        warBookPage.AddChild(_armyDoctrineRows);
        commandTabs.TabChanged += _ => RefreshArmyPanel();

        _armyAssaultReadiness = new VBoxContainer { Name = "AssaultReadiness", Visible = false };
        _armyAssaultReadiness.AddThemeConstantOverride("separation", 2);
        plansPage.AddChild(_armyAssaultReadiness);
        _armyAssaultReadiness.AddChild(Title("AUTOMATIC ASSAULT", 11, Ui.Colour("text.caption")));
        _armyAssaultReadinessText = Title("", 12, Ui.Colour("text.body_soft"));
        _armyAssaultReadinessText.Name = "AssaultReadinessText";
        _armyAssaultReadinessText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _armyAssaultReadinessText.MouseFilter = MouseFilterEnum.Stop;
        _armyAssaultReadiness.AddChild(_armyAssaultReadinessText);

        // ---- battle plans -------------------------------------------------------------
        var plans = new HFlowContainer();
        plans.AddThemeConstantOverride("h_separation", 4);
        plans.AddThemeConstantOverride("v_separation", 4);
        plansPage.AddChild(plans);
        AddOrderButton(plans, "army_halt", ArmyOrder.Manual, "Halt",
            "H: stop movement or leave battle immediately. RMB on the formation's current province does the same. Alt+H cancels only queued post-battle orders.");
        var leaveBattle = ArmyIconButton("Leave battle", WithdrawSelectedDivisions, 110, 36, false, "army_withdraw");
        _withdrawSelectedButton = leaveBattle;
        leaveBattle.Name = "WithdrawSelectedDivisions";
        leaveBattle.TooltipText = Ui.Tip(Tr("Leave battle with the selected divisions. Other participants keep fighting. Losses are not undone."));
        plans.AddChild(leaveBattle);
        AddOrderButton(plans, "army_defend", ArmyOrder.DefendFront, "Front line",
            "Hold the assigned sector of the front. Entrench, recover, and repel what comes.");
        AddOrderButton(plans, "army_advance", ArmyOrder.AdvanceFront, "Offensive line",
            "Push the front forward across its whole width once the plan is ready.");
        AddOrderButton(plans, "army_spearhead", ArmyOrder.Spearhead, "Spearhead",
            "Drive at the last selected enemy region, or at the war goal if none was picked.");
        AddOrderButton(plans, "army_reserve", ArmyOrder.ReserveFront, "Reserve",
            "Stand behind the contact line, recover faster and reinforce defensive battles nearby.");

        var execution = new HFlowContainer();
        execution.AddThemeConstantOverride("h_separation", 4);
        execution.AddThemeConstantOverride("v_separation", 4);
        plansPage.AddChild(execution);
        var prepare = ArmyIconButton("Prepare", () => IssuePlanExecution(false), 102, 32, false, "plan_prepare");
        prepare.TooltipText = Ui.Tip(Tr("Hold offensive formations on their sector so they entrench and accumulate a plan."));
        execution.AddChild(prepare);
        var execute = ArmyIconButton("Execute", () => IssuePlanExecution(true), 104, 32, true, "plan_execute");
        execute.TooltipText = Ui.Tip(Tr("Activate the selected offensive plans. Front-line and reserve orders stay as they are."));
        execution.AddChild(execute);
        var autoFront = ArmyIconButton("Auto-deploy", AutoDeployFront, 118, 32, false, "auto_deploy");
        autoFront.TooltipText = Ui.Tip(Tr("Spread the selection across unique sectors of the active front."));
        execution.AddChild(autoFront);

        // ---- the brush ----------------------------------------------------------------
        // An order names a role; a drawn line says where. The three buttons pick which
        // kind of line the brush is drawing, and pressing the same one again commits it.
        plansPage.AddChild(Title("DRAW ON THE MAP", (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"), upper: true));
        // Two columns leave room for translated captions as well as the pictograms.
        var draw = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        draw.AddThemeConstantOverride("h_separation", 4);
        draw.AddThemeConstantOverride("v_separation", 4);
        plansPage.AddChild(draw);
        AddDrawButton(draw, "Front", "plan_front", FrontLineKind.Defence, "Front line",
            "Draw the line to hold. The selection spreads along its length and digs in.");
        AddDrawButton(draw, "Attack", "plan_attack", FrontLineKind.Offensive, "Offensive line",
            "Assign ONE front first. RMB-drag the destination line inside its target country; release to save. Shift+RMB adds a later phase (up to 24); Alt+RMB redraws the active phase. Prepare pauses locally; Execute advances through the phases and stops after the final objective.");
        AddDrawButton(draw, "Defend", "plan_fallback", FrontLineKind.Fallback, "Defensive line",
            "RMB-drag a connected line on controlled land. Release to distribute divisions evenly and hold immediately, in peace or war. Backtrack to shorten; Alt+RMB redraws the selected line; Esc cancels.");
        var dropLine = ArmyIconButton("Clear plans", ClearDrawnLine, 96, 30, false, "plan_clear");
        Tip(dropLine, "Delete the plans of the selected divisions and stop their planned movement. Other plans and ongoing battles remain.");
        draw.AddChild(dropLine);
        _planEraserButton = ArmyIconButton("Eraser", TogglePlanEraser, 90, 30, false, "plan_erase");
        Tip(_planEraserButton, "Click a plan on the map to erase it. An offensive arrow or destination removes only the objective and keeps its departure front. Esc cancels.");
        plansPage.AddChild(_planEraserButton);

        _drawFeedback = Title("Pick a line, then drag along the map. Right button rubs out.",
            11, Ui.Colour("text.muted"));
        _drawFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        plansPage.AddChild(_drawFeedback);
        var steps = Title("Defence: select divisions → Defend → right-drag on controlled land and release. Divisions occupy and hold the line immediately.\nOffensive: Front → Attack → Execute. Prepare pauses. Shift + right-drag adds a phase; Alt + right-drag redraws the active phase; Esc cancels drawing.",
            12, Ui.Colour("text.caption"));
        steps.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        plansPage.AddChild(steps);

        // ---- posture and reinforcement -------------------------------------------------
        doctrinePage.AddChild(Title("POSTURE", (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"), upper: true));
        var posture = new HFlowContainer();
        posture.AddThemeConstantOverride("h_separation", 4);
        posture.AddThemeConstantOverride("v_separation", 4);
        doctrinePage.AddChild(posture);
        AddStanceButton(posture, "Cautious", ArmyStance.Cautious,
            "Slower and more careful: better recovery, entrenchment and defence, fewer casualties.");
        AddStanceButton(posture, "Balanced", ArmyStance.Balanced,
            "No modifier either way. The right choice when supply or local superiority is uncertain.");
        AddStanceButton(posture, "Aggressive", ArmyStance.Aggressive,
            "Faster and harder-hitting, at the cost of recovery, defence and heavier casualties.");
        AddStanceButton(posture, "Last stand", ArmyStance.LastStand,
            "Hold this ground at any price: the strongest defence and the fastest digging, and the heaviest casualties of any posture.");
        AddStanceButton(posture, "Force attack", ArmyStance.ForceAttack,
            "Attack now rather than when ready: the heaviest attack, the worst defence, and the heaviest casualties.");

        doctrinePage.AddChild(Title("REINFORCEMENT PRIORITY", (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"), upper: true));
        var priority = new HBoxContainer();
        priority.AddThemeConstantOverride("separation", 4);
        doctrinePage.AddChild(priority);
        AddReinforcementPriorityButton(priority, "Low", ReinforcementPriority.Low,
            "0.45 weight. For quiet sectors and formations that are training rather than fighting.");
        AddReinforcementPriorityButton(priority, "Normal", ReinforcementPriority.Normal,
            "Standard weight. The sustainable allocation.", true);
        AddReinforcementPriorityButton(priority, "High", ReinforcementPriority.High,
            "1.90 weight. These recover first, and starve the lower priorities when reserves are short.");

        // ---- organisation --------------------------------------------------------------
        organisationPage.AddChild(Title("HEADQUARTERS", (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"), upper: true));
        // Two columns keep translated captions and icon-bearing buttons within the
        // narrow command frame. A single row could exceed the safe inner rail even
        // when its declared minimum widths appeared to fit.
        var grouping = new GridContainer
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        grouping.AddThemeConstantOverride("separation", 4);
        organisationPage.AddChild(grouping);
        var newGroup = ActionButton("New HQ", CreateGroupFromSelection, 96, 30, true, Icon("hq_new"));
        newGroup.TooltipText = Ui.Tip(Tr("Raise a new army headquarters and attach every selected division to it."));
        grouping.AddChild(newGroup);
        var nextGroup = ActionButton("Next HQ", AssignSelectionToNextGroup, 96, 30, false, Icon("hq_next"));
        nextGroup.TooltipText = Ui.Tip(Tr("Move the selection to the next existing army headquarters."));
        grouping.AddChild(nextGroup);
        var ungroup = ActionButton("Detach", () => AssignSelectionToGroup(-1), 90, 30, false, Icon("hq_detach"));
        ungroup.TooltipText = Ui.Tip(Tr("Remove the selection from its army while leaving it on the map."));
        grouping.AddChild(ungroup);
        var selectHq = ActionButton("Whole HQ", SelectCurrentArmyGroup, 104, 30, false, Icon("hq_whole"));
        selectHq.TooltipText = Ui.Tip(Tr("Select every division attached to the lead division's army headquarters."));
        grouping.AddChild(selectHq);

        var selection = new HBoxContainer();
        selection.AddThemeConstantOverride("separation", 4);
        organisationPage.AddChild(selection);
        var everything = ActionButton("Every division", SelectAllPlayerArmies, 124, 30, false, Icon("select_all"));
        everything.TooltipText = Ui.Tip(Tr("Take every field division your country has into the roster."));
        selection.AddChild(everything);
        var dropLead = ActionButton("Drop lead", RemoveLeadFromSelection, 104, 30, false, Icon("drop_lead"));
        dropLead.TooltipText = Ui.Tip(Tr("Remove the lead division from the selection without moving or disbanding it."));
        selection.AddChild(dropLead);
        var region = ActionButton("Region info", ShowSelectedArmyRegion, 102, 30, false, Icon("region"));
        region.TooltipText = Ui.Tip(Tr("Open the region dossier for the lead division's position. ← Orders in its header, or Escape, brings the orders back."));
        selection.AddChild(region);

        _armyCommandFeedback = Title("Right-click to march or attack · Shift+right-click queues a waypoint",
            11, Ui.Colour("text.body_soft"));
        _armyCommandFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        organisationPage.AddChild(_armyCommandFeedback);

        // The old order controls lived in the generic side panel; the panel is that
        // container now, so the visibility flags the rest of the HUD sets still land.
        _armyOrders = commandTabs;
        // Switching to the book of war must draw it. The page is built by the panel's own refresh,
        // which runs on a campaign day - and a player who opens the tab while time is paused would
        // otherwise look at an empty page until tomorrow.
        commandTabs.TabChanged += _ =>
        {
            _armyDoctrineDay = -1;
            RefreshArmyPanel();
        };
    }

    private static VBoxContainer CommandPage(TabContainer tabs, string name)
    {
        // A command deck is a workspace, not a fixed-height poster. Translations,
        // modded font sizes and the 720p layout must still expose the last action.
        var scroll = new ScrollContainer
        {
            Name = name,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FollowFocus = true,
            ClipContents = true
        };
        var page = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipContents = true
        };
        page.AddThemeConstantOverride("separation", 6);
        tabs.AddChild(scroll);
        scroll.AddChild(page);
        scroll.SetMeta("loc_tab", name);
        tabs.SetTabTitle(tabs.GetTabIdxFromControl(scroll), Tr(name));
        return page;
    }

    /// <summary>
    /// The icon a control asks for, named as a sprite rather than as a file, so a mod
    /// dropping its own `icon_army_advance.png` into its `gfx/` tree replaces it.
    /// </summary>
    private static string Icon(string name) =>
        Simulation.SpriteCatalog.TextureOf($"icon_{name}",
            $"res://game/gfx/interface/icons/icon_{name}.png");

    // ExpandIcon does not reserve the icon's full width in Godot's minimum size.
    // A translated caption can otherwise squeeze a perfectly loaded PNG to a dot.
    private Button ArmyIconButton(string caption, System.Action action, float width, float height,
        bool primary, string icon)
    {
        var button = ActionButton(caption, action, width, height, primary, Icon(icon));
        if (icon != "stability")
        {
            button.SetMeta("army_monochrome_icon", true);
            button.SetMeta("army_icon_sprite", "icon_" + icon);
            StyleButton(button, primary);
        }
        button.SetMeta("army_icon_min_width", width);
        _armyCaptionIconButtons.Add(button);
        ReserveArmyIconSpace(button);
        return button;
    }

    private static void ReserveArmyIconSpace(Button button)
    {
        var textWidth = button.GetThemeFont("font").GetStringSize(button.Text,
            fontSize: button.GetThemeFontSize("font_size")).X;
        var padding = button.GetThemeStylebox("normal").GetMinimumSize().X;
        var iconWidth = button.Icon is null ? 0 : button.GetThemeConstant("icon_max_width") +
            button.GetThemeConstant("h_separation");
        var minimum = button.GetMeta("army_icon_min_width").AsSingle();
        button.CustomMinimumSize = new Vector2(Mathf.Max(minimum, textWidth + padding + iconWidth + 2),
            button.CustomMinimumSize.Y);
    }

    private void AddDrawButton(Container parent, string label, string icon, FrontLineKind kind, string name, string help)
    {
        var button = ArmyIconButton(label, () => ToggleDrawing(kind), 96, 30, false, icon);
        ArmyCommandTip(button, name, help);
        _drawButtons[kind] = button;
        parent.AddChild(button);
    }

    private void AddOrderButton(Container parent, string icon, ArmyOrder order, string name, string help)
    {
        var button = ArmyIconButton("", () => IssueArmyOrder(order), 56, 36, false, icon);
        ArmyCommandTip(button, name, help);
        _armyOrderButtons[order] = button;
        parent.AddChild(button);
    }

    private void AddStanceButton(Container parent, string caption, ArmyStance stance, string help)
    {
        var icon = stance switch
        {
            ArmyStance.Cautious => "stance_cautious",
            ArmyStance.Aggressive => "stance_aggressive",
            ArmyStance.LastStand => "stance_cautious",
            ArmyStance.ForceAttack => "stance_aggressive",
            _ => "stability"
        };
        var button = ArmyIconButton(caption, () => IssueArmyStance(stance), 96, 32, false, icon);
        Tip(button, help);
        _armyStanceButtons[stance] = button;
        parent.AddChild(button);
    }

    private static void ArmyCommandTip(Button button, string heading, string help)
    {
        Tip(button, help);
        button.SetMeta("loc_tip_heading", heading);
        button.TooltipText = Ui.Tip(Tr(heading) + "\n" + Tr(help));
        button.SetMeta(LocTipAppliedMeta, button.TooltipText);
    }

    /// <summary>Show the panel, or hide it and let the generic side panel have the space.</summary>
    private void SetArmyPanelVisible(bool visible)
    {
        if (visible && _resourceAtlasPanel is not null) _resourceAtlasPanel.Visible = false;
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
        // Country switches, save loads or changed ownership must not leave an old
        // roster capable of selecting a division that is no longer ours.
        _selectedArmies.RemoveAll(army => !ArmyInteraction.CanSelect(_campaign, army));
        if (!ArmyInteraction.CanSelect(_campaign, _selectedArmy))
        {
            _selectedArmy = _selectedArmies.FirstOrDefault();
            _globe.SelectArmies(_selectedArmies.Select(army => army.Id));
        }
        var divisions = _selectedArmies.Count > 0
            ? _selectedArmies.Where(army => army is not null).ToList()
            : _selectedArmy is null ? new List<Army>() : new List<Army> { _selectedArmy };
        var readingDoctrine = _armyCommandTabs is not null && _armyCommandTabs.CurrentTab == _armyDoctrineTab;
        // The book is a country-level workspace. A hidden, expanding empty roster must
        // not reserve half the drawer above it, even when formations are selected.
        _armyRoster.GetParent<Control>().Visible = !readingDoctrine;
        _armyRosterHint.Visible = !readingDoctrine;
        _armyPanelCount.Visible = !readingDoctrine;
        if (divisions.Count == 0)
        {
            // A country with no divisions still has a book of war, and the Doctrines tab is where
            // it is read: the panel stays open for that tab alone.
            if (_armyCommandTabs is null || _armyCommandTabs.CurrentTab != _armyDoctrineTab)
            { SetArmyPanelVisible(false); return; }
        }

        // Daily refresh must not replace the roster row between mouse-down and
        // mouse-up, nor dismiss a command popup that the player is reading.
        if (InterfaceInteractionGuard.Busy(_armyPanel)) return;

        SetArmyPanelVisible(true);

        // Commands can change a country's doctrines and currencies on pause as well.
        var doctrineState = DoctrinePresentation(_campaign.Player);
        if (_armyDoctrineRows is not null &&
            (_armyDoctrineDay != _campaign.ElapsedDays || _armyDoctrineState != doctrineState))
        {
            _armyDoctrineDay = _campaign.ElapsedDays;
            _armyDoctrineState = doctrineState;
            ClearPolicyRows(_armyDoctrineRows);
            ShowDoctrineWorkspace(_campaign.Player, _armyDoctrineRows);
        }

        // A book of war belongs to the country, not to divisions[0]. A doctrine can
        // be read before recruitment or after the last selected formation is lost.
        _armyComposition.Visible = divisions.Count > 0 && !readingDoctrine;
        if (divisions.Count == 0)
        {
            _armyPanelTitle.Text = Tr("DOCTRINES");
            _armyPanelSubtitle.Text = _campaign.Player.DisplayName;
            _armyPanelCount.Text = Simulation.Loc.Format("{0:N0} selected", 0);
            _armyAssaultReadiness.Visible = false;
            return;
        }
        var owner = _campaign.NationById(divisions[0].NationId);
        var mine = owner == _campaign.Player;

        // Also follows language/font changes made while the campaign is open.
        foreach (var button in _armyCaptionIconButtons) ReserveArmyIconSpace(button);

        var group = _campaign.ArmyGroups.FirstOrDefault(candidate => candidate.Id == divisions[0].ArmyGroupId);
        _armyPanelTitle.Text = divisions.Count == 1
            ? divisions[0].Name
            : group?.Name ?? Simulation.Loc.Format("{0} field forces", owner?.Tag ?? "?");
        _armyPanelSubtitle.Text = divisions.Count == 1
            ? OneLineSummary(divisions[0])
            : Simulation.Loc.Format("{0} · {1:N0} men · {2:N0} headquarters",
                CommanderCaption(group),
                divisions.Sum(army => army.Personnel),
                divisions.Select(army => army.ArmyGroupId).Distinct().Count());
        _armyPanelSubtitle.TooltipText = Ui.Tip(CommanderCaption(group) + (group is null ? "" :
            "\n" + Tr(Simulation.Generals.Trait(group.CommanderTrait).Description) +
            "\n" + Tr("Every commander, and relieving one: Military → Generals.")));
        _armyPanelCount.Text = Simulation.Loc.Format("{0:N0} selected", divisions.Count);
        // Selection counts are localised; a fixed English-sized slot clips Russian.
        _armyPanelCount.CustomMinimumSize = new Vector2(Mathf.Max(84,
            _armyPanelCount.GetThemeFont("font").GetStringSize(_armyPanelCount.Text,
                fontSize: _armyPanelCount.GetThemeFontSize("font_size")).X + 2), 0);

        RefreshComposition(divisions);
        RefreshRoster(divisions, mine);
        RefreshAssaultReadiness(divisions);

        _armyRosterHint.Visible = mine && !readingDoctrine;
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
        RefreshDrawingUi();
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
            entry.TooltipText = Ui.Tip(Simulation.Loc.Format("{0} × {1}\nSoft attack {2:0.##} · hard attack {3:0.##}\nDefence {4:0.##} · breakthrough {5:0.##}\nArmour {6:0.##} · piercing {7:0.##}\nFrontage {8:0.#} of the 80 a province gives", group.Count(), template.Name, template.SoftAttack, template.HardAttack, template.Defense, template.Breakthrough, template.Armour, template.Piercing, template.CombatWidth));
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
        if (_withdrawSelectedButton is not null)
            _withdrawSelectedButton.Disabled = !mine || !divisions.Any(army => army.InBattle);
        foreach (var child in _armyRoster.GetChildren())
        {
            _armyRoster.RemoveChild(child);
            child.QueueFree();
        }
        _armyRosterRows.Clear();
        _armyRoster.AddChild(Title(Simulation.Loc.Get("Condition bars: organisation · strength · equipment"),
            11, Ui.Colour("text.muted")));

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
                TooltipText = Ui.Tip(DivisionTooltip(army)),
                Flat = true,
                Text = Simulation.Loc.Format(" {0}  {1}", CounterGlyph(template.CounterSymbol), Shorten(army.Name, 20))
            };
            row.AddThemeColorOverride("font_color", Ui.Colour("text.body"));
            row.AddThemeColorOverride("font_hover_color", Ui.Colour("button.text_hover"));
            row.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button", 12));
            var captured = army;
            row.Pressed += () => OnRosterRowPressed(captured);
            line.AddChild(row);
            _armyRosterRows[army.Id] = row;

            line.AddChild(Bar(army.Organisation, 54,
                army.Organisation < 0.35f ? "state.negative" :
                army.InBattle ? "state.warning" : "state.info",
                Simulation.Loc.Format("Organisation {0:P0} — how much fight is left in it",
                    army.Organisation)));
            line.AddChild(Bar(army.Strength, 40,
                army.Strength < 0.55f ? "state.warning" : "state.positive",
                Simulation.Loc.Format("Fighting strength {0:P0} — {1:N0} of {2:N0} men",
                    army.Strength, army.Personnel, army.MaxPersonnel)));
            var equipmentBar = Bar(army.Equipment, 40, "combat.equipment",
                Simulation.Loc.Format("Equipment: {0:P0}", army.Equipment) + "\n" + EquipmentLogistics.Description(_campaign, army), tooltipMeasure: 72);
            equipmentBar.Name = "EquipmentBar_" + army.Id;
            line.AddChild(equipmentBar);
            var reason = Simulation.StaffReasons.Explain(_campaign, army);
            var reasonMark = Title(Simulation.StaffReasons.Label(_campaign, army), 11, Ui.Colour("text.caption"));
            reasonMark.Name = "StaffReason_" + army.Id;
            reasonMark.ClipText = true;
            reasonMark.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            reasonMark.CustomMinimumSize = new Vector2(96, 16);
            reasonMark.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            reasonMark.TooltipText = Ui.Tip(reason);
            line.AddChild(reasonMark);
            line.AddChild(StatusPip(army));
        }

        if (divisions.Count > 120)
            _armyRoster.AddChild(Title(Simulation.Loc.Format("…and {0:N0} more", divisions.Count - 120),
                11, Ui.Colour("text.faint")));
    }

    /// <summary>
    /// A high-contrast filled bar. Organisation and strength read faster as bars than as
    /// "84 % / 61 %", and forty of them read faster still. The fill is anchored rather
    /// than sized, so the proportion is the layout instead of a number recomputed by hand.
    /// </summary>
    private Control Bar(float value, int width, string fillToken, string tooltip, int tooltipMeasure = 0)
    {
        var frame = new Control
        {
            CustomMinimumSize = new Vector2(width,
                Mathf.Clamp(Simulation.InterfaceSkin.Metric("combat.panel_bar_height", 12), 10, 20)),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = tooltipMeasure > 0 ? Ui.Tip(tooltip, Mathf.Min(Ui.TooltipMeasure, tooltipMeasure)) : Ui.Tip(tooltip)
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
            army.CutOff ? ("⊘", "state.negative", LogisticsSystem.DescribeSupply(_campaign, army)) :
            army.InBattle ? ("⚔", "state.warning", "In battle") :
            army.LastMoveResult == ArmyMoveResult.RouteInterrupted ? ("!", "state.warning", "Route interrupted by changed territorial control. Issue a new destination.") :
            army.SupplyCongestion < 0.999f ? ("△", "state.warning", LogisticsSystem.DescribeSupply(_campaign, army)) :
            army.SupplyLevel < 0.45f ? ("△", "state.warning", "Short of supply") :
            army.Organisation < 0.35f ? ("↓", "state.critical", "Organisation is spent") :
            army.IsMoving ? ("→", "state.info", "On the march") :
            IsOffensive(army) && army.PlanActive ? ("▶", "state.positive", "Plan executing") :
            IsOffensive(army) ? ("◷", "text.muted", "Plan preparing") :
            ("▪", "text.faint", "Holding");

        var pip = Title(glyph, 12, Ui.Colour(token));
        pip.Name = "SupplyStatus_" + army.Id;
        pip.CustomMinimumSize = new Vector2(16, 0);
        pip.HorizontalAlignment = HorizontalAlignment.Center;
        pip.TooltipText = Ui.Tip(help, Mathf.Min(Ui.TooltipMeasure, 72));
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
            army.Name + "\n" + Simulation.Loc.Get(template.Name) + "\n\n" +
            Simulation.Loc.Format("Organisation: {0:P0}", army.Organisation) + "\n" +
            Simulation.Loc.Format("Cohesion: {0:P0}", army.Cohesion) + "\n" +
            Simulation.Loc.Format("Fighting strength: {0:P0}", army.Strength) + "\n" +
            Simulation.Loc.Format("Manpower: {0:N0} of {1:N0}", army.Personnel, army.MaxPersonnel) + "\n" +
            Simulation.Loc.Format("Equipment: {0:P0} · experience: {1:P0}",
                army.Equipment, army.Experience) + "\n" +
            EquipmentLogistics.Description(_campaign, army) + "\n" +
            Simulation.Loc.Format("Supply: {0:P0}", army.SupplyLevel) + "\n" +
            LogisticsSystem.DescribeSupply(_campaign, army) + "\n" +
            Simulation.Loc.Format("Entrenchment: {0:P0}", army.Entrenchment) + "\n";
        if (IsOffensive(army))
            text += Simulation.Loc.Format("Plan preparation: {0:P0} (up to +{1:P0} attack)",
                army.Planning, _campaign.Defines.MaxPlanningAttackBonus) + "\n";
        if (IsOffensive(army) && !army.InBattle)
            text += ArmyAssaultPresentation.Description(_campaign, army) + "\n";
        text += "\n" + Simulation.Loc.Format("{0} · province #{1:N0}",
                    state is null ? Simulation.Loc.Get("Unmapped ground") : state.DisplayName,
                    army.ProvinceId) + "\n" +
                Simulation.Loc.Format("Order: {0} · posture: {1}",
                    OrderName(army.OrderKind), StanceName(army.Stance)) + "\n" +
                Simulation.ArmyVocabulary.Situation(_campaign, army) + "\n" +
                Simulation.StaffReasons.Explain(_campaign, army);
        if (army.LastMoveResult == ArmyMoveResult.RouteInterrupted)
            text += "\n" + Simulation.Loc.Get(
                "The route is cut. Choose a new destination.");
        return text;
    }

    private void RefreshAssaultReadiness(List<Army> divisions)
    {
        var offensive = divisions.Where(IsOffensive).ToArray();
        _armyAssaultReadiness.Visible = offensive.Length > 0;
        if (offensive.Length == 0) return;
        _armyAssaultReadinessText.Text = ArmyAssaultPresentation.Summary(_campaign, offensive);
        _armyAssaultReadinessText.TooltipText = Ui.Tip(
            _armyAssaultReadinessText.Text + "\n\n" + Simulation.Loc.Get(
                "These are minimum conditions for an automatic assault, not an estimate of victory. Orders, access, enemy strength and battle frontage still matter."));
    }

    private string OneLineSummary(Army army)
    {
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == army.StateMapIndex);
        return Simulation.Loc.Format("{0} · {1} · {2:N0} men",
            Simulation.Loc.Get(UnitTypeCatalog.Get(army.TemplateId).Name),
            state is null ? Simulation.Loc.Get("unmapped") : state.DisplayName,
            army.Personnel);
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
        if (!ArmyInteraction.CanSelect(_campaign, army)) return;
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
            SetArmyFeedback(Simulation.Loc.Format(
                Simulation.Plural.Key("{0:N0} division of {1} selected.", _selectedArmies.Count),
                _selectedArmies.Count, Simulation.Loc.Get(UnitTypeCatalog.Get(template).Name)));
        }
        else if (Input.IsKeyPressed(Key.Ctrl))
        {
            if (_selectedArmies.Contains(army)) _selectedArmies.Remove(army);
            else _selectedArmies.Add(army);
            if (_selectedArmies.Count == 0) { ClearArmySelectionAndClose(); return; }
            _selectedArmy = _selectedArmies[0];
            SetArmyFeedback(Simulation.Loc.Format(
                Simulation.Plural.Key("{0:N0} division selected.", _selectedArmies.Count),
                _selectedArmies.Count));
        }
        else
        {
            _selectedArmies.Clear();
            _selectedArmies.Add(army);
            _selectedArmy = army;
            _globe.FocusOn(army.Latitude, army.Longitude, 6.8f);
            SetArmyFeedback(Simulation.Loc.Format("{0} has the orders.", army.Name));
        }

        _globe.SelectArmies(_selectedArmies.Select(candidate => candidate.Id));
        RefreshArmyPanel();
    }

    /// <summary>The commander of a group as the panel names them: name, temperament, skill and record.</summary>
    private string CommanderCaption(ArmyGroup group) =>
        group is null || group.Commander == Simulation.Generals.Unassigned || group.CommanderTrait.Length == 0
            ? Tr(group?.Commander ?? "No commander assigned")
            : Simulation.Loc.Format("{0} ({1}, skill {2:0}%, {3} won / {4} lost)", group.Commander,
                Tr(Simulation.Generals.Trait(group.CommanderTrait).Name), group.StaffSkill * 100f, group.Victories, group.Defeats);
}
