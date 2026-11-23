using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Selected-army command drawer: header with split &amp; combine-HQ shortcuts, inlaid 4-metric
/// stack readiness summary (Strength, Organisation, Supply, Entrenchment/Planning), framed
/// template composition badges, scrollable division roster (in <see cref="ArmyRosterUI"/>) with
/// 16px scrollbar clearance, and tabbed Orders / Stance / Organise / Doctrines controls.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _armyPanel = null!;
    private Label _armyPanelTitle = null!;
    private Label _armyPanelSubtitle = null!;
    private Label _armyPanelCount = null!;
    private Button _armyMergeHqButton = null!;
    private HBoxContainer _armyReadinessStrip = null!;
    private readonly Label[] _armyKpiValues = new Label[4];
    private readonly ColorRect[] _armyKpiFills = new ColorRect[4];
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
    private Button _armyDisbandButton;
    private ulong _disbandArmedUntil;

    private void DisbandSelectedDivisions()
    {
        var mine = SelectedPlayerDivisions();
        if (mine.Length == 0) return;
        var now = Time.GetTicksMsec();
        if (now > _disbandArmedUntil)
        {
            _disbandArmedUntil = now + 4000;
            var men = mine.Sum(army => (long)(army.Personnel * army.Strength));
            SetArmyFeedback(Simulation.Loc.Format("Disband {0:N0} division(s), {1:N0} men? Press Disband (or Delete) again to confirm.", mine.Length, men));
            if (IsInstanceValid(_armyDisbandButton)) _armyDisbandButton.Text = Tr("Confirm");
            return;
        }
        _disbandArmedUntil = 0;
        if (IsInstanceValid(_armyDisbandButton)) _armyDisbandButton.Text = Tr("Disband");
        var order = new Simulation.DisbandArmiesCommand { IssuerId = _campaign.Player.Id, ArmyIds = mine.Select(army => army.Id).ToArray() };
        _campaign.Commands.Enqueue(order);
        _campaign.Commands.ApplyAll(_campaign);
        var feedback = Simulation.Loc.Format("{0:N0} division(s) sent home.", order.StoodDown) +
                       (order.Refusal.Length > 0 ? " " + order.Refusal : "");
        if (_selectedArmies.All(army => !_campaign.Armies.Contains(army))) ClearArmySelectionAndClose();
        else { _selectedArmies.RemoveAll(army => !_campaign.Armies.Contains(army)); ShowSelectedArmies(); }
        SetArmyFeedback(feedback);
    }

    private void BuildArmyPanel()
    {
        _armyCaptionIconButtons.Clear();
        _armyPanel = TexturedPanel(_hudLayer, PanelSurface.Command);
        _armyPanel.ClipContents = true;
        Ui.Place(_armyPanel, "hud.army_panel");
        _armyPanel.Visible = false;

        var shell = PanelRows(_armyPanel, 20, 14, 6);
        shell.ClipContents = true;

        // ---- header: army title, selection count, quick HQ merge & split, close ----
        var header = new HBoxContainer { ClipContents = true };
        header.AddThemeConstantOverride("separation", 6);
        shell.AddChild(header);

        _armyPanelTitle = Title("Army", (int)Ui.Metric("ui.font.title", 20), Ui.Colour("text.accent"));
        _armyPanelTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _armyPanelTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        _armyPanelTitle.ClipText = true;
        _armyPanelTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        header.AddChild(_armyPanelTitle);

        _armyPanelCount = Title("", (int)Ui.Metric("ui.font.heading", 15), Ui.Colour("text.bright"));
        _armyPanelCount.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _armyPanelCount.HorizontalAlignment = HorizontalAlignment.Right;
        _armyPanelCount.AutowrapMode = TextServer.AutowrapMode.Off;
        _armyPanelCount.ClipText = true;
        _armyPanelCount.CustomMinimumSize = new Vector2(74, 0);
        header.AddChild(_armyPanelCount);

        _armyMergeHqButton = ActionButton(Tr("shell.ws.army.merge_hq_btn"), CreateGroupFromSelection, 46, 28);
        _armyMergeHqButton.Name = "ArmyQuickMergeHq";
        _armyMergeHqButton.TooltipText = Ui.Tip(Tr("shell.ws.army.merge_hq_tip"));
        header.AddChild(_armyMergeHqButton);

        var halve = ActionButton("½", SplitSelectionInHalf, 30, 28);
        halve.TooltipText = Ui.Tip(Tr("Split in half (Ctrl+X): of several formations half stay selected for the next order and the rest stay where they stand; one formation of two or more divisions is cut in two."));
        header.AddChild(halve);
        header.AddChild(HandbookButton("orders"));
        header.AddChild(ActionButton("×", ClearArmySelectionAndClose, 30, 28));

        _armyPanelSubtitle = Title("", (int)Ui.Metric("ui.font.body", 12), Ui.Colour("text.muted"));
        _armyPanelSubtitle.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _armyPanelSubtitle.ClipText = true;
        _armyPanelSubtitle.MaxLinesVisible = 2;
        _armyPanelSubtitle.CustomMinimumSize = new Vector2(0, 18);
        shell.AddChild(_armyPanelSubtitle);

        // ---- inlaid 4-metric stack readiness strip ----
        BuildArmyReadinessStrip(shell);

        // ---- framed template composition pills ----
        _armyComposition = new HBoxContainer { ClipContents = true };
        _armyComposition.AddThemeConstantOverride("separation", 6);
        shell.AddChild(_armyComposition);

        // ---- division roster with explicit >=16px right scrollbar clearance ----
        var rosterFrame = new PanelContainer
        {
            Name = "ArmyRosterFrame",
            ClipContents = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 76),
            SizeFlagsStretchRatio = 0.78f
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

        var rosterMargin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rosterMargin.AddThemeConstantOverride("margin_right", 16);
        scroll.AddChild(rosterMargin);

        _armyRoster = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        _armyRoster.AddThemeConstantOverride("separation", 3);
        rosterMargin.AddChild(_armyRoster);

        _armyRosterHint = Title(
            "Click a division to command it alone · Ctrl+click to add or remove · double-click to take every division of the same template",
            11, Ui.Colour("text.faint"));
        _armyRosterHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _armyRosterHint.ClipText = true;
        _armyRosterHint.MaxLinesVisible = 2;
        _armyRosterHint.CustomMinimumSize = new Vector2(0, 30);
        shell.AddChild(_armyRosterHint);

        // ---- tabbed command deck ----
        var commandTabs = new TabContainer
        {
            Name = "ArmyCommandTabs",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 176),
            ClipContents = true
        };
        commandTabs.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button", 12));
        shell.AddChild(commandTabs);

        var plansPage = CommandPage(commandTabs, "ORDERS");
        var doctrinePage = CommandPage(commandTabs, "STANCE");
        var organisationPage = CommandPage(commandTabs, "ORGANISE");
        var warBookPage = CommandPage(commandTabs, "DOCTRINES");
        _armyCommandTabs = commandTabs;
        _armyDoctrineTab = commandTabs.GetTabIdxFromControl((Control)warBookPage.GetParent().GetParent());
        _armyDoctrineRows = new VBoxContainer { Name = "DoctrineRows", ClipContents = true };
        _armyDoctrineRows.AddThemeConstantOverride("separation", 8);
        warBookPage.AddChild(_armyDoctrineRows);
        commandTabs.TabChanged += _ => { _armyDoctrineDay = -1; RefreshArmyPanel(); };

        _armyAssaultReadiness = new VBoxContainer { Name = "AssaultReadiness", Visible = false, ClipContents = true };
        _armyAssaultReadiness.AddThemeConstantOverride("separation", 2);
        plansPage.AddChild(_armyAssaultReadiness);
        _armyAssaultReadiness.AddChild(Title("AUTOMATIC ASSAULT", 11, Ui.Colour("text.caption")));
        _armyAssaultReadinessText = Title("", 12, Ui.Colour("text.body_soft"));
        _armyAssaultReadinessText.Name = "AssaultReadinessText";
        _armyAssaultReadinessText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _armyAssaultReadinessText.MouseFilter = MouseFilterEnum.Stop;
        _armyAssaultReadiness.AddChild(_armyAssaultReadinessText);

        var plans = new HFlowContainer { ClipContents = true };
        plans.AddThemeConstantOverride("h_separation", 4);
        plans.AddThemeConstantOverride("v_separation", 4);
        plansPage.AddChild(plans);
        AddOrderButton(plans, "army_halt", ArmyOrder.Manual, "Halt",
            "H: stop movement or leave battle immediately. RMB on the formation's current province stops a march or breaks off an attack, and holds a defence. Alt+H cancels only queued post-battle orders.");
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

        var autoFront = ArmyIconButton("", AutoDeployFront, 56, 36, false, "auto_deploy");
        ArmyCommandTip(autoFront, "Auto-deploy", "Spread the selection across unique sectors of the active front.");
        plans.AddChild(autoFront);

        // ---- posture & reinforcement priority ----
        doctrinePage.AddChild(Title("POSTURE", (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"), upper: true));
        var posture = new HFlowContainer { ClipContents = true };
        posture.AddThemeConstantOverride("h_separation", 4);
        posture.AddThemeConstantOverride("v_separation", 4);
        doctrinePage.AddChild(posture);
        AddStanceButton(posture, "Cautious", ArmyStance.Cautious, "Slower and more careful: better recovery, entrenchment and defence, fewer casualties.");
        AddStanceButton(posture, "Balanced", ArmyStance.Balanced, "No modifier either way. The right choice when supply or local superiority is uncertain.");
        AddStanceButton(posture, "Aggressive", ArmyStance.Aggressive, "Faster and harder-hitting, at the cost of recovery, defence and heavier casualties.");
        AddStanceButton(posture, "Last stand", ArmyStance.LastStand, "Hold this ground at any price: the strongest defence and the fastest digging, and the heaviest casualties of any posture.");
        AddStanceButton(posture, "Force attack", ArmyStance.ForceAttack, "Attack now rather than when ready: the heaviest attack, the worst defence, and the heaviest casualties.");

        doctrinePage.AddChild(Title("REINFORCEMENT PRIORITY", (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"), upper: true));
        var priority = new HBoxContainer { ClipContents = true };
        priority.AddThemeConstantOverride("separation", 4);
        doctrinePage.AddChild(priority);
        AddReinforcementPriorityButton(priority, "Low", ReinforcementPriority.Low, "0.45 weight. For quiet sectors and formations that are training rather than fighting.");
        AddReinforcementPriorityButton(priority, "Normal", ReinforcementPriority.Normal, "Standard weight. The sustainable allocation.", true);
        AddReinforcementPriorityButton(priority, "High", ReinforcementPriority.High, "1.90 weight. These recover first, and starve the lower priorities when reserves are short.");

        // ---- organisation ----
        organisationPage.AddChild(Title("HEADQUARTERS", (int)Ui.Metric("ui.font.micro", 10), Ui.Colour("text.caption"), upper: true));
        var grouping = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
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

        var selection = new HFlowContainer { ClipContents = true };
        selection.AddThemeConstantOverride("h_separation", 4);
        selection.AddThemeConstantOverride("v_separation", 4);
        organisationPage.AddChild(selection);
        var everything = ActionButton("Every division", SelectAllPlayerArmies, 120, 30, false, Icon("select_all"));
        everything.TooltipText = Ui.Tip(Tr("Take every field division your country has into the roster."));
        selection.AddChild(everything);
        var dropLead = ActionButton("Drop lead", RemoveLeadFromSelection, 100, 30, false, Icon("drop_lead"));
        dropLead.TooltipText = Ui.Tip(Tr("Remove the lead division from the selection without moving or disbanding it."));
        selection.AddChild(dropLead);
        var disband = ActionButton("Disband", () => DisbandSelectedDivisions(), 92, 30);
        disband.TooltipText = Ui.Tip(Tr("Disband (Delete)\nSend the selected divisions home: their men go back to the manpower reserve. Not in battle or aboard a ship. Press twice to confirm."));
        selection.AddChild(disband);
        _armyDisbandButton = disband;
        var region = ActionButton("Region info", ShowSelectedArmyRegion, 98, 30, false, Icon("region"));
        region.TooltipText = Ui.Tip(Tr("Open the region dossier for the lead division's position. ← Orders in its header, or Escape, brings the orders back."));
        selection.AddChild(region);

        _armyCommandFeedback = Title(
            Platform.PlatformProfile.Current.IsTouchPrimary
                ? "mobile.override.army_orders"
                : "Right-click to march or attack · Shift+right-click queues a waypoint",
            11, Ui.Colour("text.body_soft"));
        _armyCommandFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        organisationPage.AddChild(_armyCommandFeedback);

        _armyOrders = commandTabs;
    }

    private void BuildArmyReadinessStrip(VBoxContainer parent)
    {
        _armyReadinessStrip = new HBoxContainer { Name = "ArmyStackReadinessStrip", ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _armyReadinessStrip.AddThemeConstantOverride("separation", 5);
        parent.AddChild(_armyReadinessStrip);

        var specs = new[]
        {
            ("shell.ws.army.kpi_str", "combat.strength"),
            ("shell.ws.army.kpi_org", "combat.organisation"),
            ("shell.ws.army.kpi_sup", "combat.supply"),
            ("shell.ws.army.kpi_plan", "state.info")
        };

        for (var i = 0; i < specs.Length; i++)
        {
            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            var sb = new StyleBoxFlat
            {
                BgColor = Ui.Colour("surface.inset"), BorderColor = Ui.Colour("surface.line"),
                ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 3, ContentMarginBottom = 4
            };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(3);
            card.AddThemeStyleboxOverride("panel", sb);
            _armyReadinessStrip.AddChild(card);

            var col = new VBoxContainer { ClipContents = true };
            col.AddThemeConstantOverride("separation", 2);
            card.AddChild(col);

            var top = new HBoxContainer { ClipContents = true };
            var cap = Title(Tr(specs[i].Item1), 9, Ui.Colour("text.caption"));
            cap.AutowrapMode = TextServer.AutowrapMode.Off; cap.ClipText = true; cap.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            top.AddChild(cap);

            var val = Title("—", 11, Ui.Colour("text.bright"));
            val.AddThemeFontOverride("font", PresentationFonts.Tabular());
            val.AutowrapMode = TextServer.AutowrapMode.Off; val.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            top.AddChild(val);
            col.AddChild(top);
            _armyKpiValues[i] = val;

            var track = new ColorRect { Color = Ui.Colour("surface.base"), CustomMinimumSize = new Vector2(0, 4), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var fill = new ColorRect { Color = Ui.Colour(specs[i].Item2), AnchorRight = 0.5f, AnchorBottom = 1f };
            track.AddChild(fill);
            col.AddChild(track);
            _armyKpiFills[i] = fill;
        }
    }

    private static VBoxContainer CommandPage(TabContainer tabs, string name)
    {
        var scroll = new ScrollContainer
        {
            Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            FollowFocus = true, ClipContents = true
        };
        var margin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_right", 16);
        scroll.AddChild(margin);

        var page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        page.AddThemeConstantOverride("separation", 6);
        margin.AddChild(page);
        tabs.AddChild(scroll);
        scroll.SetMeta("loc_tab", name);
        tabs.SetTabTitle(tabs.GetTabIdxFromControl(scroll), Tr(name));
        return page;
    }

    private static string Icon(string name) =>
        Simulation.SpriteCatalog.TextureOf($"icon_{name}", $"res://game/gfx/interface/icons/icon_{name}.png");

    private Button ArmyIconButton(string caption, System.Action action, float width, float height, bool primary, string icon)
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
        var textWidth = button.GetThemeFont("font").GetStringSize(button.Text, fontSize: button.GetThemeFontSize("font_size")).X;
        var padding = button.GetThemeStylebox("normal").GetMinimumSize().X;
        var iconWidth = button.Icon is null ? 0 : button.GetThemeConstant("icon_max_width") + button.GetThemeConstant("h_separation");
        var minimum = button.GetMeta("army_icon_min_width").AsSingle();
        button.CustomMinimumSize = new Vector2(Mathf.Max(minimum, textWidth + padding + iconWidth + 2), button.CustomMinimumSize.Y);
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

    private void SetArmyPanelVisible(bool visible)
    {
        if (visible && _resourceAtlasPanel is not null) _resourceAtlasPanel.Visible = false;
        if (!visible) EndDrawing();
        if (_armyPanel is null) return;
        _armyPanel.Visible = visible;
        if (!visible) _armyRosterRows.Clear();
    }

    private void RefreshArmyPanel()
    {
        if (_armyPanel is null || _campaign?.Player is null) return;
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
        _armyRoster.GetParent<Control>().GetParent<Control>().Visible = !readingDoctrine;
        _armyRosterHint.Visible = !readingDoctrine;
        _armyPanelCount.Visible = !readingDoctrine;
        _armyReadinessStrip.Visible = divisions.Count > 0 && !readingDoctrine;
        if (divisions.Count == 0 && (_armyCommandTabs is null || _armyCommandTabs.CurrentTab != _armyDoctrineTab))
        {
            SetArmyPanelVisible(false);
            return;
        }
        if (InterfaceInteractionGuard.Busy(_armyPanel)) return;

        SetArmyPanelVisible(true);

        var doctrineState = DoctrinePresentation(_campaign.Player);
        if (_armyDoctrineRows is not null && (_armyDoctrineDay != _campaign.ElapsedDays || _armyDoctrineState != doctrineState))
        {
            _armyDoctrineDay = _campaign.ElapsedDays;
            _armyDoctrineState = doctrineState;
            ClearPolicyRows(_armyDoctrineRows);
            ShowDoctrineWorkspace(_campaign.Player, _armyDoctrineRows);
        }

        _armyComposition.Visible = divisions.Count > 0 && !readingDoctrine;
        if (divisions.Count == 0)
        {
            _armyPanelTitle.Text = Tr("DOCTRINES");
            _armyPanelSubtitle.Text = _campaign.Player.DisplayName;
            _armyPanelCount.Text = Simulation.Loc.Format("{0:N0} selected", 0);
            _armyAssaultReadiness.Visible = false;
            _armyMergeHqButton.Disabled = true;
            return;
        }
        var owner = _campaign.NationById(divisions[0].NationId);
        var mine = owner == _campaign.Player;
        var canMergeHq = mine && divisions.Count >= 2;
        _armyMergeHqButton.Disabled = !canMergeHq;
        _armyMergeHqButton.TooltipText = Ui.Tip(Tr(canMergeHq ? "shell.ws.army.merge_hq_tip" : "shell.ws.army.merge_hq_refusal"));

        foreach (var button in _armyCaptionIconButtons) ReserveArmyIconSpace(button);

        var group = _campaign.ArmyGroups.FirstOrDefault(candidate => candidate.Id == divisions[0].ArmyGroupId);
        _armyPanelTitle.Text = divisions.Count == 1 ? divisions[0].Name : group?.Name ?? Simulation.Loc.Format("{0} field forces", owner?.Tag ?? "?");
        _armyPanelSubtitle.Text = divisions.Count == 1
            ? OneLineSummary(divisions[0])
            : Simulation.Loc.Format("{0} · {1:N0} men · {2:N0} headquarters",
                CommanderCaption(group), divisions.Sum(army => army.Personnel), divisions.Select(army => army.ArmyGroupId).Distinct().Count());
        if (SeaLiftCaption(divisions[0]) is { Length: > 0 } crossing) _armyPanelSubtitle.Text = crossing + " · " + _armyPanelSubtitle.Text;
        if (Simulation.StrandedGarrisons.SelectionLine(_campaign, divisions) is { Length: > 0 } pocket) _armyPanelSubtitle.Text = pocket + " · " + _armyPanelSubtitle.Text;
        _armyPanelSubtitle.TooltipText = Ui.Tip(CommanderCaption(group) + (group is null ? "" :
            "\n" + Tr(Simulation.Generals.Trait(group.CommanderTrait).Description) + "\n" + Tr("Every commander, and relieving one: Military → Generals.")));
        _armyPanelCount.Text = Simulation.Loc.Format("{0:N0} selected", divisions.Count);
        _armyPanelCount.CustomMinimumSize = new Vector2(Mathf.Max(74,
            _armyPanelCount.GetThemeFont("font").GetStringSize(_armyPanelCount.Text, fontSize: _armyPanelCount.GetThemeFontSize("font_size")).X + 4), 0);

        RefreshArmyReadinessStrip(divisions);
        RefreshComposition(divisions);
        RefreshRoster(divisions, mine);
        RefreshAssaultReadiness(divisions);

        _armyRosterHint.Visible = mine && !readingDoctrine;
        foreach (var (order, button) in _armyOrderButtons) { button.Disabled = !mine; StyleButton(button, mine && divisions.All(army => army.OrderKind == order)); }
        foreach (var (stance, button) in _armyStanceButtons) { button.Disabled = !mine; StyleButton(button, mine && divisions.All(army => army.Stance == stance)); }
        ApplyArmyPriorityButtonStates();
        RefreshDrawingUi();
    }
}
