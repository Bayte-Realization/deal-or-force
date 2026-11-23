using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private const string BattleBalanceExplanation =
        "Endurance balance, not victory probability. Compares time left before organisation breaks at the recent loss rate. Reinforcements, supply and orders can change it.";
    private int _battleDossierFingerprint = int.MinValue;
    private string _battleWithdrawalFeedback = "";

    private void BuildBattleDetailControls(VBoxContainer parent)
    {
        _battleDetailControls = new VBoxContainer
        {
            Name = "BattleDossier", Visible = false, ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _battleDetailControls.AddThemeConstantOverride("separation", 7);
        parent.AddChild(_battleDetailControls);
        _battleActions = new VBoxContainer
        {
            Name = "BattleActions", Visible = false, ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _battleActions.AddThemeConstantOverride("separation", 5);
        _dossierScroll.GetParent().AddChild(_battleActions);
        _battleDetailControls.VisibilityChanged += () =>
        {
            _battleActions.Visible = _battleDetailControls.Visible;
            LayoutCampaignDossier();
        };
    }

    private LandBattle SelectedBattle() => _campaign?.Battles.FirstOrDefault(battle => battle.Id == _selectedBattleId);

    private void ShowBattleDetails(LandBattle battle)
    {
        if (battle is null || !_hudLayer.Visible || !CampaignIntel.PlayerCanSeeBattle(_campaign, battle)) return;
        if (_supportLayer is { Visible: true }) CloseSupportScreen();
        CloseFleetPanel();
        PrepareDossierTabs("Battle");
        ApplyDossierTab();
        ClearDetail();
        _selectedBattleId = battle.Id;
        _battleWithdrawalFeedback = "";
        _openScreen = "";
        _sidePanel.Visible = true;
        SetArmyPanelVisible(false);
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = _decisionControls.Visible = _militarySpending.Visible = false;
        _battleDetailControls.Visible = true;
        _battleDossierFingerprint = int.MinValue;
        RefreshBattleDossier();
        FocusProvince(battle.ProvinceId, 6.5f);
    }

    private void RefreshBattleDossier()
    {
        if (_battleDetailControls is not { Visible: true } || !_sidePanel.Visible) return;
        var battle = SelectedBattle();
        if (battle is null || !CampaignIntel.PlayerCanSeeBattle(_campaign, battle))
        {
            _battleDetailControls.Visible = false;
            _sideBody.Text = Tr("Battle report is no longer available.");
            return;
        }
        var hash = HashCode.Combine(battle.Id, battle.Rounds.Count, battle.Ended,
            battle.AttackerLosses, battle.DefenderLosses, _campaign.ElapsedDays, Loc.Language);
        hash = HashCode.Combine(hash, battle.Attackers.Count, battle.Defenders.Count,
            _campaign.ElapsedHours, _campaign.Player.Id, _campaign.Player.IsHuman, (int)battle.LastPhase, battle.LastAttackDirections);
        var commandEnabled = !_campaign.Defines.FeatureCommands.TryGetValue(nameof(WithdrawArmyFromBattleCommand), out var feature) ||
            string.IsNullOrWhiteSpace(feature) || _campaign.FeatureEnabled(feature);
        hash = HashCode.Combine(hash, _campaign.MapRevision, _campaign.WarRevision,
            GetWindow().Size, SettingsCatalog.Number("interface_scale"), _battleRosterExpanded, commandEnabled,
            BattlePixelScale());
        if (hash == _battleDossierFingerprint) return;
        _battleDossierFingerprint = hash;

        foreach (var child in _battleDetailControls.GetChildren()) { _battleDetailControls.RemoveChild(child); child.QueueFree(); }
        foreach (var child in _battleActions.GetChildren()) { _battleActions.RemoveChild(child); child.QueueFree(); }
        LayoutCampaignDossier();

        var telemetry = BattleTelemetry.Build(_campaign, battle);
        _sideTitle.Text = Tr(battle.Ended ? "BATTLE REPORT" : "BATTLE IN PROGRESS");
        _sideBody.Text = Loc.Format("{0} · day {1}", BattlePlaceName(battle.ProvinceId), Math.Max(1, telemetry.DaysFought));

        // Explicit >=14px right scrollbar clearance so cards/buttons never scrape _dossierScroll's bar
        var contentMargin = new MarginContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        contentMargin.AddThemeConstantOverride("margin_right", 14);
        _battleDetailControls.AddChild(contentMargin);
        var body = new VBoxContainer { ClipContents = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 7);
        contentMargin.AddChild(body);

        var attacker = _campaign.NationById(battle.AttackerId);
        var defender = _campaign.NationById(battle.DefenderId);
        var armies = _campaign.Armies.ToDictionary(army => army.Id);
        var ownAttack = battle.Attackers.Any(id => armies.TryGetValue(id, out var unit) && unit.NationId == _campaign.Player.Id);
        var ownDefence = battle.Defenders.Any(id => armies.TryGetValue(id, out var unit) && unit.NationId == _campaign.Player.Id);
        var detailed = !_campaign.Player.IsHuman || battle.AttackerId == _campaign.Player.Id ||
                       battle.DefenderId == _campaign.Player.Id || ownAttack || ownDefence;

        if (!detailed)
        {
            body.AddChild(BuildBattleVsStrip(telemetry, attacker, defender, battle));
            body.AddChild(BattleText(Tr("Observed battle between foreign countries. Detailed combat reports unavailable."), 12, "text.muted"));
            if (!battle.Ended)
            {
                var observed = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
                observed.AddThemeConstantOverride("separation", 10);
                body.AddChild(observed);
                AddObservedBattleSide(observed, true, attacker, ResolveBattleSide(battle.Attackers, armies));
                AddObservedBattleSide(observed, false, defender, ResolveBattleSide(battle.Defenders, armies));
            }
            AddBattleNavigation(battle);
            FitBattleReading();
            return;
        }

        var assessment = BattleReadout.Assess(battle, _campaign.Defines.BattleBreakOrganisation);
        var playerAttacks = !_campaign.Player.IsHuman || battle.AttackerId == _campaign.Player.Id || ownAttack;
        var resisting = !battle.Ended && battle.Rounds.LastOrDefault()?.Note == PocketResistance.BattleNote;
        var share = assessment.ShareFor(playerAttacks);
        var tone = resisting ? "state.warning" : !assessment.Known ? "text.muted" : share > .55f ? "state.positive" :
            share < .45f ? "state.negative" : "state.warning";

        // ---- Compact Hero Header: Attacker vs Defender flags + Phase Badge + Progress & Endurance ----
        var heroPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        heroPanel.SetMeta("atlas_section_ground", "surface.inset");
        heroPanel.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(heroPanel, AtlasSurfaceSkin.Level.Raised, "surface.inset");
        body.AddChild(heroPanel);

        var heroBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        heroBox.AddThemeConstantOverride("separation", 4);
        heroPanel.AddChild(heroBox);

        heroBox.AddChild(BuildBattleVsStrip(telemetry, attacker, defender, battle));

        var hero = new HBoxContainer { ClipContents = true };
        hero.AddThemeConstantOverride("separation", 6);
        heroBox.AddChild(hero);
        var icon = Symbol("battle", 20, tone);
        if (icon is not null) hero.AddChild(icon);
        var headline = BattleText(battle.Ended ? Loc.Format("Winner: {0}",
            _campaign.NationById(battle.WinnerId)?.DisplayName ?? Tr("Unknown")) :
            Tr(resisting ? "Encircled defenders are holding" : assessment.StatusFor(playerAttacks)), 16, tone);
        headline.Name = "BattleStatus";
        hero.AddChild(headline);

        if (!battle.Ended)
        {
            var metaRow = new HBoxContainer { ClipContents = true };
            metaRow.AddThemeConstantOverride("separation", 6);
            heroBox.AddChild(metaRow);

            var caption = BattleText(resisting
                ? Tr("The last combat round continued despite spent organisation. Losses and supply still matter.")
                : assessment.Known
                ? Loc.Format(_campaign.Player.IsHuman ? "Our endurance balance: {0:P0}" : "Attacker endurance balance: {0:P0}", share)
                : Tr("The first combat round has not resolved yet."),
                12, "text.body_soft", tabular: true);
            caption.Name = "BattleAssessment";
            caption.TooltipText = Ui.Tip(Tr(BattleBalanceExplanation));
            caption.MouseFilter = MouseFilterEnum.Stop;
            metaRow.AddChild(caption);

            if (!resisting && telemetry.EstimatedDaysToResolve > 0.05f)
            {
                var etaTxt = telemetry.EstimatedDaysToResolve < 1f
                    ? Loc.Format("shell.ws.battle.eta_hours_fmt", MathF.Max(1f, telemetry.EstimatedDaysToResolve * 24f))
                    : Loc.Format("shell.ws.battle.eta_days_fmt", telemetry.EstimatedDaysToResolve);
                var momentum = telemetry.Momentum > 0.08f ? "▲ " : telemetry.Momentum < -0.08f ? "▼ " : "═ ";
                var etaLbl = BattleText(momentum + etaTxt, 11, tone, tabular: true);
                etaLbl.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
                etaLbl.AutowrapMode = TextServer.AutowrapMode.Off;
                metaRow.AddChild(etaLbl);
            }

            if (!resisting)
            {
                var balance = BattleGauge(assessment.Known ? share : .5f, tone, "BattleEndurance");
                balance.TooltipText = caption.TooltipText;
                heroBox.AddChild(balance);
            }

            // Live Battle Progress Tug bar from BattleTelemetry (AttackerProgress 0..1)
            var tug = new WarBar
            {
                Name = "BattleProgressTug",
                CustomMinimumSize = new Vector2(0, 10),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Track = Ui.Colour("war.track"),
                MarkerColour = Ui.Colour("war.front_line"),
                TickColour = Ui.Colour("shell.paper", 0.45f),
                TooltipText = Ui.Tip(Loc.Format("shell.ws.battle.progress_fmt", telemetry.AttackerProgress))
            };
            tug.Tug(Mathf.Clamp((telemetry.AttackerProgress - 0.5f) * 2f, -1f, 1f), WarSideColour(attacker), WarSideColour(defender));
            heroBox.AddChild(tug);
        }

        // ---- Split Attacker vs Defender Side Columns (with Engaged vs Reserve Formation Cards) ----
        var sideRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        sideRow.AddThemeConstantOverride("separation", 8);
        body.AddChild(sideRow);

        var attacking = ResolveBattleSide(battle.Attackers, armies);
        var defending = ResolveBattleSide(battle.Defenders, armies);
        if (!battle.Ended)
            defending = defending.Where(army => army.ProvinceId == battle.ProvinceId).ToList();
        AddBattleSide(sideRow, battle, telemetry.Attacker, true, attacker, attacking);
        AddBattleSide(sideRow, battle, telemetry.Defender, false, defender, defending);

        if (battle.WithdrawnAttackers.Count + battle.WithdrawnDefenders.Count > 0)
            body.AddChild(FactRow(Tr("Withdrawn formations"),
                $"{battle.WithdrawnAttackers.Count}/{battle.WithdrawnDefenders.Count}", "surface.line_faint", "text.muted"));

        // ---- Visual Combat Modifier Chips + Factor Breakdown ----
        AddBattleModifierChips(body, telemetry, battle, playerAttacks);
        AddBattleFactors(body, battle, playerAttacks);

        // ---- Ground, Frontage Width Bar, Tactics & Combat Log ----
        SectionRule(body, "Ground and fire");
        AddCombatWidthRow(body, telemetry, attacker, defender);

        var attackerTactic = Simulation.CombatTactics.Find(battle.AttackerTactic);
        var defenderTactic = Simulation.CombatTactics.Find(battle.DefenderTactic);
        if (attackerTactic is not null || defenderTactic is not null)
        {
            var nameOf = new Func<Simulation.CombatTactic, string>(t => t is null ? Tr("No manoeuvre") : Tr(t.Name));
            var tacticTone = attackerTactic is not null && attackerTactic.Counters(defenderTactic) ? "state.positive"
                : defenderTactic is not null && defenderTactic.Counters(attackerTactic) ? "state.warning" : "text.body_soft";
            var manoeuvres = FactRow(Tr("Manoeuvres"), $"{nameOf(attackerTactic)} vs {nameOf(defenderTactic)}", tacticTone, tacticTone);
            manoeuvres.TooltipText = Ui.Tip(
                (attackerTactic is null ? "" : Tr(attackerTactic.Name) + " — " + Tr(attackerTactic.Description)) +
                (attackerTactic is not null && defenderTactic is not null ? "\n" : "") +
                (defenderTactic is null ? "" : Tr(defenderTactic.Name) + " — " + Tr(defenderTactic.Description)));
            body.AddChild(manoeuvres);
        }

        var latest = battle.Rounds.LastOrDefault();
        if (latest is not null)
        {
            SectionRule(body, "Combat log", Loc.Format("{0} d.", battle.Rounds.Count));
            var combatNote = BattleText(Tr(latest.Note), 12, "text.accent_dim");
            if (latest.Note == PocketResistance.BattleNote)
            {
                var pixelScale = (GetViewport().GetStretchTransform() *
                    _battleDetailControls.GetGlobalTransformWithCanvas()).Scale.Abs();
                var fontSize = DecisionReadability.FontSize(12, MathF.Min(pixelScale.X, pixelScale.Y),
                    Math.Clamp(SettingsCatalog.Number("interface_scale"), .5f, 2.5f),
                    Ui.Metric("decision.minimum_font_pixels", 14));
                combatNote.AddThemeFontSizeOverride("font_size", fontSize);
            }
            body.AddChild(combatNote);
            var history = string.Join("\n", battle.Rounds.TakeLast(3).Reverse().Select(round =>
                Loc.Format("Day {0}: losses {1:N0}/{2:N0} · organisation {3:P0}/{4:P0}",
                    round.Day - battle.StartedDay + 1, round.AttackerLosses, round.DefenderLosses,
                    round.AttackerOrganisation, round.DefenderOrganisation)));
            body.AddChild(BattleText(history, 11, "text.muted", tabular: true));
        }
        else body.AddChild(BattleText(Tr("Assessing battle"), 12, "text.muted"));
        if (battle.Ended) body.AddChild(BattleText(Tr(battle.Outcome), 13, tone));
        AddBattleNavigation(battle);
        if (_battleWithdrawalFeedback.Length > 0)
            body.AddChild(BattleText(_battleWithdrawalFeedback, 12, "state.warning"));
        FitBattleReading();
    }

    private Control BuildBattleVsStrip(BattleTelemetrySnapshot telemetry, Nation attacker, Nation defender, LandBattle battle)
    {
        var strip = new HBoxContainer { Name = "BattleVsHeader", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        strip.AddThemeConstantOverride("separation", 6);

        var atkFlag = FlagBadge(attacker?.Tag ?? "", 18, attacker?.DisplayName ?? "");
        TintBlankFlag(atkFlag, attacker);
        strip.AddChild(atkFlag);

        var atkCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        atkCol.AddThemeConstantOverride("separation", 0);
        var atkNm = BattleText(attacker?.DisplayName ?? "?", 12, "text.accent");
        atkNm.AutowrapMode = TextServer.AutowrapMode.Off; atkNm.ClipText = true; atkNm.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        atkCol.AddChild(atkNm);
        var atkCnt = BattleText(Loc.Format("shell.ws.battle.divs_men_fmt", telemetry.Attacker.TotalDivisions, Fmt.People(telemetry.Attacker.TotalPersonnel)), 10, "text.muted", tabular: true);
        atkCnt.AutowrapMode = TextServer.AutowrapMode.Off; atkCnt.ClipText = true;
        atkCol.AddChild(atkCnt);
        strip.AddChild(atkCol);

        // Centered Active Battle Tactical Phase Badge
        var phaseTone = telemetry.Phase switch
        {
            BattlePhase.PocketCollapse => "state.negative",
            BattlePhase.ArmouredBreakthrough or BattlePhase.FlankingEnvelopment => "state.positive",
            BattlePhase.ArtilleryBarrage or BattlePhase.UrbanGrind or BattlePhase.DesperateCounterattack => "state.warning",
            _ => "state.info"
        };
        var phaseBadge = new PanelContainer
        {
            Name = "BattlePhaseBadge",
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ClipContents = true,
            TooltipText = Ui.Tip(Tr("shell.ws.battle.phase_header") + ": " + Tr(telemetry.PhaseLabel) + "\n" + Tr(telemetry.PhaseDescription)),
            MouseFilter = MouseFilterEnum.Stop
        };
        var pbStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("surface.base"),
            BorderColor = Ui.Colour(phaseTone),
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2
        };
        pbStyle.SetBorderWidthAll(1);
        pbStyle.SetCornerRadiusAll(3);
        phaseBadge.AddThemeStyleboxOverride("panel", pbStyle);
        var phaseLbl = BattleText(Tr(telemetry.PhaseLabel), 11, phaseTone);
        phaseLbl.AutowrapMode = TextServer.AutowrapMode.Off;
        phaseLbl.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        phaseLbl.MouseFilter = MouseFilterEnum.Ignore;
        phaseBadge.AddChild(phaseLbl);
        strip.AddChild(phaseBadge);

        var defCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        defCol.AddThemeConstantOverride("separation", 0);
        var defNm = BattleText(defender?.DisplayName ?? "?", 12, "text.accent");
        defNm.HorizontalAlignment = HorizontalAlignment.Right;
        defNm.AutowrapMode = TextServer.AutowrapMode.Off; defNm.ClipText = true; defNm.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        defCol.AddChild(defNm);
        var defCnt = BattleText(Loc.Format("shell.ws.battle.divs_men_fmt", telemetry.Defender.TotalDivisions, Fmt.People(telemetry.Defender.TotalPersonnel)), 10, "text.muted", tabular: true);
        defCnt.HorizontalAlignment = HorizontalAlignment.Right;
        defCnt.AutowrapMode = TextServer.AutowrapMode.Off; defCnt.ClipText = true;
        defCol.AddChild(defCnt);
        strip.AddChild(defCol);

        var defFlag = FlagBadge(defender?.Tag ?? "", 18, defender?.DisplayName ?? "");
        TintBlankFlag(defFlag, defender);
        strip.AddChild(defFlag);
        return strip;
    }

    private void AddCombatWidthRow(VBoxContainer parent, BattleTelemetrySnapshot telemetry, Nation attacker, Nation defender)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        box.AddThemeConstantOverride("separation", 2);
        var txt = Loc.Format("shell.ws.battle.width_fmt",
            telemetry.Attacker.WidthUsed, telemetry.Attacker.WidthLimit,
            telemetry.Attacker.InLineCount, telemetry.Attacker.TotalFormations) + "  ·  " +
            $"{telemetry.Defender.InLineCount}/{telemetry.Defender.TotalFormations}";
        box.AddChild(BattleText(txt, 11, "text.body_soft", tabular: true));

        var bar = new WarBar
        {
            CustomMinimumSize = new Vector2(0, 7),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Track = Ui.Colour("war.track"),
            MarkerColour = Ui.Colour("war.front_line"),
            TickColour = Ui.Colour("shell.paper", 0.35f)
        };
        var atkShare = Math.Clamp(telemetry.Attacker.WidthUsed / MathF.Max(1f, telemetry.Attacker.WidthLimit), 0f, 1f) * 0.5f;
        var defShare = Math.Clamp(telemetry.Defender.WidthUsed / MathF.Max(1f, telemetry.Defender.WidthLimit), 0f, 1f) * 0.5f;
        bar.Shares((atkShare, WarSideColour(attacker)), (defShare, WarSideColour(defender)));
        box.AddChild(bar);
        parent.AddChild(box);
    }

    private static List<Army> ResolveBattleSide(IEnumerable<int> ids, Dictionary<int, Army> armies)
    {
        var result = new List<Army>();
        foreach (var id in ids) if (armies.TryGetValue(id, out var army) && army.Divisions >= .1f) result.Add(army);
        return result;
    }

    private void AddBattleSide(HBoxContainer parent, LandBattle battle, BattleSideSnapshot sideSnap, bool attacking, Nation nation, List<Army> armies)
    {
        var sidePanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1,
            CustomMinimumSize = Vector2.Zero, ClipContents = true
        };
        sidePanel.SetMeta("atlas_section_ground", "surface.inset");
        sidePanel.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(sidePanel, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        parent.AddChild(sidePanel);

        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        column.AddThemeConstantOverride("separation", 4);
        sidePanel.AddChild(column);

        var roleRow = new HBoxContainer { ClipContents = true };
        roleRow.AddChild(BattleText(Tr(attacking ? "ATTACKER" : "DEFENDER"), 11, "text.caption"));
        var lossLbl = BattleText(Loc.Format("Losses: {0:N0}", attacking ? battle.AttackerLosses : battle.DefenderLosses), 11, "state.warning", tabular: true);
        lossLbl.SizeFlagsHorizontal = SizeFlags.ShrinkEnd; lossLbl.AutowrapMode = TextServer.AutowrapMode.Off;
        roleRow.AddChild(lossLbl);
        column.AddChild(roleRow);

        var state = BattleReadout.Summarise(armies);
        var last = battle.Rounds.LastOrDefault();
        var org = battle.Ended && last is not null ? (attacking ? last.AttackerOrganisation : last.DefenderOrganisation) : state.Organisation;
        AddBattleMeter(column, "Organisation", org, "combat.organisation", "organisation");
        if (!battle.Ended)
        {
            AddBattleMeter(column, "Fighting strength", state.Strength, "combat.strength", "strength");
            AddBattleMeter(column, "Equipment", state.Equipment, "combat.equipment", "equipment");
            AddBattleMeter(column, "Supply", state.Supply, "combat.supply", "supply");
        }
        if (battle.Ended || !_battleRosterExpanded) return;

        // Map formation telemetry snapshots by ArmyId for Frontline / Reserve / Retreating partition
        var snapById = sideSnap.Formations.ToDictionary(f => f.ArmyId);
        var ordered = armies.OrderBy(a => snapById.TryGetValue(a.Id, out var f) ? (int)f.Role : 0)
            .ThenByDescending(a => a.Organisation)
            .ToList();
        var limit = Math.Clamp((int)InterfaceSkin.Metric("combat.roster_rows", 6), 1, 30);
        BattleFormationRole? lastRole = null;

        foreach (var army in ordered.Take(limit))
        {
            var hasSnap = snapById.TryGetValue(army.Id, out var fSnap);
            var role = hasSnap ? fSnap.Role
                : ((attacking ? battle.LastAttackerLine : battle.LastDefenderLine).Contains(army.Id) ? BattleFormationRole.Frontline : BattleFormationRole.Reserve);
            if (lastRole != role)
            {
                lastRole = role;
                var hdrKey = role == BattleFormationRole.Frontline ? "shell.ws.battle.frontline_hdr"
                    : role == BattleFormationRole.Retreating ? "shell.ws.battle.retreating_hdr" : "shell.ws.battle.reserve_hdr";
                var hdrTone = role == BattleFormationRole.Frontline ? "text.accent" : role == BattleFormationRole.Retreating ? "state.negative" : "text.muted";
                column.AddChild(BattleText(Tr(hdrKey), 10, hdrTone));
            }

            if (army.PostBattleWaypoints.Count > 0)
                column.AddChild(BattleText(Loc.Format("After battle: {0} waypoint(s). Alt+H cancels the queue; H leaves battle.",
                    army.PostBattleWaypoints.Count), 10, "state.warning"));

            var cardFrame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            var cardStyle = new StyleBoxFlat
            {
                BgColor = Ui.Colour("surface.base"),
                BorderColor = Ui.Colour(role == BattleFormationRole.Frontline ? "button.primary_border" : "surface.line"),
                ContentMarginLeft = 5, ContentMarginRight = 5, ContentMarginTop = 3, ContentMarginBottom = 3
            };
            cardStyle.SetBorderWidthAll(1);
            cardStyle.SetCornerRadiusAll(2);
            cardFrame.AddThemeStyleboxOverride("panel", cardStyle);
            column.AddChild(cardFrame);

            var card = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            card.AddThemeConstantOverride("separation", 2);
            cardFrame.AddChild(card);

            var template = UnitTypeCatalog.Get(army.TemplateId);
            var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            header.AddThemeConstantOverride("separation", 4);
            var caption = BattleText($"{CounterGlyph(template.CounterSymbol)} {army.Name}", 11);
            caption.MaxLinesVisible = 1; caption.AutowrapMode = TextServer.AutowrapMode.Off;
            caption.ClipText = true; caption.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            caption.TooltipText = Ui.Tip(CanInspectNation(army.NationId) ? DivisionTooltip(army) :
                Loc.Format("Organisation {0:P0} · equipment {1:P0}",
                    BattleReadout.Unit(army.Organisation), BattleReadout.Unit(army.Equipment)) + "\n" +
                Tr("Foreign formation — orders and route are not available."));
            caption.MouseFilter = MouseFilterEnum.Stop;
            header.AddChild(caption);

            var roleTag = BattleText(Tr(role == BattleFormationRole.Frontline ? "shell.ws.battle.role_frontline"
                : role == BattleFormationRole.Retreating ? "shell.ws.battle.role_retreating" : "shell.ws.battle.role_reserve"),
                10, role == BattleFormationRole.Frontline ? "state.positive" : role == BattleFormationRole.Retreating ? "state.negative" : "text.muted");
            roleTag.SizeFlagsHorizontal = SizeFlags.ShrinkEnd; roleTag.AutowrapMode = TextServer.AutowrapMode.Off;
            header.AddChild(roleTag);
            card.AddChild(header);

            if (!battle.Ended && army.NationId == _campaign.Player.Id && _campaign.Player.IsHuman)
            {
                var actions = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
                actions.AddThemeConstantOverride("separation", 4);
                card.AddChild(actions);
                var armyId = army.Id;
                var quote = BattleWithdrawalPreview.Read(_campaign, _campaign.Player.Id, army.Id);
                var withdrawBtn = ActionButton(quote.Attacking ? "Stop attack" : "Retreat", () => WithdrawBattleFormation(armyId, 1f), 0, 22);
                withdrawBtn.Name = "WithdrawFormation_" + army.Id;
                withdrawBtn.TooltipText = Ui.Tip(WithdrawalTip(quote));
                withdrawBtn.Disabled = !quote.Available;
                withdrawBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                actions.AddChild(withdrawBtn);
                if (army.Divisions >= 2f)
                {
                    var half = ActionButton("Withdraw half", () => WithdrawBattleFormation(armyId, .5f), 0, 22);
                    half.Name = "WithdrawHalf_" + army.Id;
                    var halfQuote = BattleWithdrawalPreview.Read(_campaign, _campaign.Player.Id, army.Id, .5f);
                    half.TooltipText = Ui.Tip(WithdrawalTip(halfQuote));
                    half.Disabled = !halfQuote.Available;
                    half.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                    actions.AddChild(half);
                }
            }

            var bars = new HBoxContainer { ClipContents = true };
            bars.AddThemeConstantOverride("separation", 3);
            card.AddChild(bars);
            bars.AddChild(BattleGauge(army.Strength, "combat.strength"));
            bars.AddChild(BattleGauge(army.Organisation, "combat.organisation"));
            bars.AddChild(BattleGauge(army.Equipment, "combat.equipment"));
            bars.TooltipText = Ui.Tip(Loc.Format("Organisation {0:P0} · equipment {1:P0}", army.Organisation, army.Equipment) +
                (army.NationId == _campaign.Player.Id ? "\n" + EquipmentLogistics.Description(_campaign, army) : ""));
            bars.MouseFilter = MouseFilterEnum.Stop;

            var numbers = BattleText(Loc.Format("Organisation {0:P0} · equipment {1:P0}",
                BattleReadout.Unit(army.Organisation), BattleReadout.Unit(army.Equipment)), 11, "text.body_soft", tabular: true);
            numbers.Name = "FormationReadiness_" + army.Id;
            card.AddChild(numbers);

            if (army.Entrenchment > 0.05f || army.CutOff || army.SupplyLevel < .45f || army.Organisation < .35f)
            {
                var statusParts = new List<string>();
                if (army.Entrenchment > 0.05f) statusParts.Add(Loc.Format("shell.ws.battle.trench_pip_fmt", army.Entrenchment));
                if (hasSnap && fSnap.InPocketCollapse) statusParts.Add(Tr("shell.ws.battle.pocket_chip"));
                else if (army.CutOff) statusParts.Add(Tr("CUT OFF"));
                else if (army.Organisation < .35f) statusParts.Add(Tr("Organisation is spent"));
                else if (army.SupplyLevel < .45f) statusParts.Add(Tr("Short of supply"));
                var warnTone = army.CutOff || army.Organisation < .35f || army.SupplyLevel < .45f ? "state.negative" : "text.muted";
                card.AddChild(BattleText(string.Join(" · ", statusParts), 10, warnTone, tabular: true));
            }
        }
        if (armies.Count > limit) column.AddChild(BattleText(Loc.Format("…and {0:N0} more", armies.Count - limit), 11, tabular: true));
    }

    private void AddObservedBattleSide(HBoxContainer parent, bool attacking, Nation nation, List<Army> armies)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1, CustomMinimumSize = Vector2.Zero, ClipContents = true };
        column.AddThemeConstantOverride("separation", 5);
        parent.AddChild(column);
        column.AddChild(BattleText(Tr(attacking ? "ATTACKER" : "DEFENDER"), 11, "text.caption"));
        column.AddChild(BattleText(nation?.DisplayName ?? "?", 15, "text.accent"));
        var condition = MilitaryConditionReadout.ForPlayer(_campaign, armies);
        if (!condition.Known)
        {
            column.AddChild(BattleText(Tr("No formations currently observed."), 12, "text.muted"));
            return;
        }
        column.AddChild(BattleText(Tr("Observed combat readiness"), 11, "text.caption"));
        AddBattleMeter(column, "Organisation", condition.Organisation, "combat.organisation", "organisation");
        AddBattleMeter(column, "Equipment", condition.Equipment, "combat.equipment", "equipment");
        column.AddChild(BattleText(Tr("Observed formations only; unseen reinforcements are not included."), 11, "text.muted"));
        if (!_battleRosterExpanded) return;
        var limit = Math.Clamp((int)InterfaceSkin.Metric("combat.roster_rows", 6), 1, 30);
        var shown = 0;
        foreach (var army in armies)
        {
            if (!MilitaryConditionReadout.CanRead(_campaign, _campaign.Player.Id, army)) continue;
            if (shown++ >= limit) continue;
            column.AddChild(BattleText(army.Name, 11));
            var numbers = BattleText(Loc.Format("Organisation {0:P0} · equipment {1:P0}",
                BattleReadout.Unit(army.Organisation), BattleReadout.Unit(army.Equipment)), 11, "text.body_soft", tabular: true);
            numbers.Name = "ObservedFormationReadiness_" + army.Id;
            column.AddChild(numbers);
        }
        if (shown > limit) column.AddChild(BattleText(Loc.Format("…and {0:N0} more", shown - limit), 11, tabular: true));
    }

    private void WithdrawBattleFormation(int armyId, float share)
        => ConfirmLastDefenderWithdrawal(new[] { armyId }, share, () => ApplyBattleWithdrawal(armyId, share));

    private void ApplyBattleWithdrawal(int armyId, float share)
    {
        var army = _campaign.Armies.Find(a => a.Id == armyId);
        if (army is null || army.NationId != _campaign.Player.Id) return;
        var command = new WithdrawArmyFromBattleCommand { IssuerId = _campaign.Player.Id, ArmyId = armyId, Share = share };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _battleWithdrawalFeedback = !command.Applied ? Tr("Order sent to the host. Awaiting confirmation.") :
            command.Withdrawn ? Loc.Format("Withdrawn: {0}", army.Name) :
            Loc.Format("Cannot retreat: {0}", Tr(command.Result));
        _battleDossierFingerprint = int.MinValue;
        RefreshBattleDossier();
    }

    private void AddBattleMeter(VBoxContainer parent, string title, float value, string tone, string iconName)
    {
        var row = new HBoxContainer { ClipContents = true };
        var icon = Symbol(iconName, 13, tone);
        if (icon is not null) row.AddChild(icon);
        var label = BattleText(Tr(title), 11, "text.body_soft");
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        var amount = BattleText($"{BattleReadout.Unit(value):P0}", 11, tabular: true);
        amount.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        amount.AutowrapMode = TextServer.AutowrapMode.Off;
        row.AddChild(amount);
        parent.AddChild(row);
        parent.AddChild(BattleGauge(value, tone));
    }

    private Label BattleText(string text, int size = 12, string tone = "text.body", bool tabular = false)
    {
        var lbl = new Label
        {
            Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, Theme = null
        }.WithBattleStyle(size, Ui.Colour(tone));
        if (tabular) lbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
        return lbl;
    }

    private ProgressBar BattleGauge(float value, string tone, string name = "")
    {
        var bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Value = BattleReadout.Unit(value),
            ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        if (name.Length > 0) bar.Name = name;
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.inset"), CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2 });
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour(tone), CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2 });
        return bar;
    }

    private void AddBattleNavigation(LandBattle battle)
    {
        if (!battle.Ended && _campaign.Player.IsHuman &&
            _campaign.Armies.Any(army => army.BattleId == battle.Id && army.NationId == _campaign.Player.Id))
        {
            var leave = ActionButton("Leave this battle", () =>
            {
                var targets = _campaign.Armies.Where(army => army.BattleId == battle.Id &&
                    army.NationId == _campaign.Player.Id).Select(army => army.Id).ToArray();
                ConfirmLastDefenderWithdrawal(targets, 1f, () =>
                {
                    var commands = targets.Select(id => new WithdrawArmyFromBattleCommand { IssuerId = _campaign.Player.Id, ArmyId = id }).ToArray();
                    foreach (var command in commands) _campaign.Commands.Enqueue(command);
                    _campaign.Commands.ApplyAll(_campaign);
                    _battleWithdrawalFeedback = commands.Any(command => !command.Applied)
                        ? Tr("Order sent to the host. Awaiting confirmation.")
                        : Loc.Format("Left battle: {0} · stopped: {1} · no retreat route: {2}",
                            commands.Count(command => command.Withdrawn), 0, commands.Count(command => !command.Withdrawn));
                    _battleDossierFingerprint = int.MinValue;
                    RefreshBattleDossier();
                });
            }, 160, 30);
            leave.Name = "WithdrawBattleSide";
            var quotes = _campaign.Armies.Where(army => army.BattleId == battle.Id &&
                army.NationId == _campaign.Player.Id).Select(army => BattleWithdrawalPreview.Read(_campaign, _campaign.Player.Id, army.Id)).ToArray();
            leave.Disabled = !quotes.Any(quote => quote.Available);
            leave.TooltipText = Ui.Tip(Tr("Withdraw our participants only. Defenders without a friendly retreat route remain in combat.") +
                (leave.Disabled ? "\n" + Tr(quotes.FirstOrDefault().Refusal ?? "Not in battle") : ""));
            _battleActions.AddChild(leave);
            if (leave.Disabled)
            {
                var refusal = BattleText(Tr(quotes.FirstOrDefault().Refusal ?? "Not in battle"), 12, "state.warning");
                refusal.Name = "BattleWithdrawalRefusal";
                _battleActions.AddChild(refusal);
            }
        }
        var row = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        row.AddThemeConstantOverride("h_separation", 5);
        row.AddThemeConstantOverride("v_separation", 5);
        _battleActions.AddChild(row);
        row.AddChild(ActionButton("Focus battle", () => FocusProvince(battle.ProvinceId, 6.5f), 124, 28, true));
        row.AddChild(ActionButton("War dossier", () => OpenScreen("Wars"), 124, 28));
        if (!battle.Ended)
        {
            var roster = ActionButton(_battleRosterExpanded ? "Hide formations" : "Show formations", ToggleBattleRoster, 144, 28);
            roster.Name = "BattleRosterToggle";
            row.AddChild(roster);
        }
    }
}

internal static class BattleLabelStyle
{
    internal static Label WithBattleStyle(this Label label, int size, Color color)
    {
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
