using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Division roster rows, stack readiness KPIs, template composition pills, condition bars,
/// clickable active-battle status pips, and division tooltips for <see cref="ArmyPanelUI"/>.
/// </summary>
public partial class DealOrForceGame
{
    private static string CounterGlyph(string counterSymbol) => counterSymbol switch
    {
        "armor" => "▬",
        "mechanized" => "◈",
        "motorized" => "⊙",
        "artillery" => "●",
        "cavalry" => "╱",
        _ => "✕"
    };

    private string SeaLiftCaption(Simulation.Army army)
    {
        if (Simulation.SeaLift.Of(_campaign, army) is not { } lift) return "";
        string Region(int province)
        {
            var map = _campaign.ProvinceMap;
            if (map is null || province <= 0 || province > map.ProvinceCount) return "?";
            return _campaign.States.Find(s => s.MapIndex == map.ProvinceToState[province])?.DisplayName ?? "?";
        }
        var fleet = _campaign.Fleets.Find(f => f.Id == lift.FleetId);
        var fleetName = fleet is null ? "?" : Simulation.Loc.Get(fleet.Name);
        if (army.EmbarkedFleetId >= 0) return Simulation.Loc.Format("⚓ aboard {0}, landing at {1}", fleetName, Region(lift.LandingProvinceId));
        return army.ProvinceId == lift.PortProvinceId
            ? Simulation.Loc.Format("⚓ waiting at {0} for {1}", Region(lift.PortProvinceId), fleetName)
            : Simulation.Loc.Format("⚓ marching to {0} to board {1}", Region(lift.PortProvinceId), fleetName);
    }

    private void RefreshArmyReadinessStrip(List<Army> divisions)
    {
        if (divisions.Count == 0 || _armyKpiValues[0] is null) return;
        var str = divisions.Average(a => a.Strength);
        var org = divisions.Average(a => a.Organisation);
        var sup = divisions.Average(a => a.SupplyLevel);
        var offensive = divisions.Any(IsOffensive);
        var prep = offensive ? divisions.Average(a => a.Planning) : divisions.Average(a => a.Entrenchment);

        var metrics = new (float Val, string Tone)[]
        {
            (str, str < 0.45f ? "state.negative" : str < 0.70f ? "state.warning" : "combat.strength"),
            (org, org < 0.35f ? "state.negative" : org < 0.60f ? "state.warning" : "combat.organisation"),
            (sup, sup < 0.45f ? "state.negative" : sup < 0.75f ? "state.warning" : "combat.supply"),
            (prep, offensive ? "state.info" : "state.positive")
        };

        for (var i = 0; i < 4; i++)
        {
            var v = Math.Clamp(metrics[i].Val, 0f, 1f);
            _armyKpiValues[i].Text = $"{v:P0}";
            _armyKpiValues[i].AddThemeColorOverride("font_color", Ui.Colour(metrics[i].Tone));
            _armyKpiFills[i].Color = Ui.Colour(metrics[i].Tone);
            _armyKpiFills[i].AnchorRight = Math.Max(0.02f, v);
        }
    }

    private void RefreshComposition(List<Army> divisions)
    {
        foreach (var child in _armyComposition.GetChildren()) { _armyComposition.RemoveChild(child); child.QueueFree(); }

        foreach (var group in divisions.GroupBy(army => army.TemplateId).OrderByDescending(group => group.Count()))
        {
            var template = UnitTypeCatalog.Get(group.Key);
            var pill = new PanelContainer
            {
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Stop,
                TooltipText = Ui.Tip(Simulation.Loc.Format("{0} × {1}\nSoft attack {2:0.##} · hard attack {3:0.##}\nDefence {4:0.##} · breakthrough {5:0.##}\nArmour {6:0.##} · piercing {7:0.##}\nFrontage {8:0.#} of the 80 a province gives",
                    group.Count(), template.Name, template.SoftAttack, template.HardAttack, template.Defense, template.Breakthrough, template.Armour, template.Piercing, template.CombatWidth))
            };
            var sb = new StyleBoxFlat
            {
                BgColor = Ui.Colour("surface.inset"), BorderColor = Ui.Colour("surface.line"),
                ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 2, ContentMarginBottom = 2
            };
            sb.SetBorderWidthAll(1);
            sb.SetCornerRadiusAll(3);
            pill.AddThemeStyleboxOverride("panel", sb);

            var entry = Title($"{CounterGlyph(template.CounterSymbol)} {group.Count()}", 12, Ui.Colour("text.body_soft"));
            entry.AddThemeFontOverride("font", PresentationFonts.Tabular());
            entry.MouseFilter = MouseFilterEnum.Ignore;
            entry.AutowrapMode = TextServer.AutowrapMode.Off;
            pill.AddChild(entry);
            _armyComposition.AddChild(pill);
        }
    }

    private void RefreshRoster(List<Army> divisions, bool mine)
    {
        if (_withdrawSelectedButton is not null)
            _withdrawSelectedButton.Disabled = !mine || !divisions.Any(army => army.InBattle);
        foreach (var child in _armyRoster.GetChildren()) { _armyRoster.RemoveChild(child); child.QueueFree(); }
        _armyRosterRows.Clear();

        // Column-aligned micro-header matching exact right-hand bar/status widths
        var colHead = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        colHead.AddThemeConstantOverride("separation", 4);
        var divHdr = Title(Tr("shell.ws.army.col_div"), 9, Ui.Colour("text.caption"));
        divHdr.SizeFlagsHorizontal = SizeFlags.ExpandFill; divHdr.AutowrapMode = TextServer.AutowrapMode.Off;
        colHead.AddChild(divHdr);
        foreach (var (key, w) in new[] { ("shell.ws.army.col_org", 32), ("shell.ws.army.col_str", 26), ("shell.ws.army.col_eqp", 26), ("shell.ws.army.col_state", 70) })
        {
            var h = Title(Tr(key), 9, Ui.Colour("text.caption"));
            h.CustomMinimumSize = new Vector2(w, 0);
            h.HorizontalAlignment = HorizontalAlignment.Center;
            h.AutowrapMode = TextServer.AutowrapMode.Off;
            colHead.AddChild(h);
        }
        _armyRoster.AddChild(colHead);

        var leadArmyId = divisions.Count > 0 ? divisions[0].Id : -1;
        foreach (var army in divisions.OrderBy(army => army.Name, StringComparer.Ordinal).Take(120))
        {
            var template = UnitTypeCatalog.Get(army.TemplateId);
            var isLead = army.Id == leadArmyId && divisions.Count > 1;

            var line = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            line.AddThemeConstantOverride("separation", 4);
            _armyRoster.AddChild(line);

            StyleBox rowNormal = isLead
                ? new StyleBoxFlat
                {
                    BgColor = Ui.Colour("button.primary_fill", 0.72f),
                    BorderColor = Ui.Colour("button.primary_border"),
                    BorderWidthLeft = 3,
                    ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2
                }
                : new StyleBoxEmpty { ContentMarginLeft = 2, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2 };
            var rowHover = new StyleBoxFlat
            {
                BgColor = Ui.Colour("button.fill_hover", 0.55f),
                ContentMarginLeft = isLead ? 4 : 2, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2
            };
            rowHover.SetCornerRadiusAll(2);

            var row = new Button
            {
                CustomMinimumSize = new Vector2(64, 26),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = 1f,
                Alignment = HorizontalAlignment.Left,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = Ui.Tip((isLead ? Tr("shell.ws.army.lead_tip") + "\n" : "") + DivisionTooltip(army)),
                Flat = !isLead,
                Text = Simulation.Loc.Format(" {0}  {1}", CounterGlyph(template.CounterSymbol), army.Name)
            };
            row.AddThemeStyleboxOverride("normal", rowNormal);
            row.AddThemeStyleboxOverride("pressed", rowNormal);
            row.AddThemeStyleboxOverride("focus", rowNormal);
            row.AddThemeStyleboxOverride("hover", rowHover);
            row.AddThemeColorOverride("font_color", Ui.Colour(isLead ? "text.accent" : "text.body"));
            row.AddThemeColorOverride("font_hover_color", Ui.Colour("button.text_hover"));
            row.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button", 12));
            var captured = army;
            row.Pressed += () => OnRosterRowPressed(captured);
            line.AddChild(row);
            _armyRosterRows[army.Id] = row;

            line.AddChild(Bar(army.Organisation, 32,
                army.Organisation < 0.35f ? "state.negative" : army.InBattle ? "state.warning" : "state.info",
                Simulation.Loc.Format("Organisation {0:P0} — how much fight is left in it", army.Organisation)));
            line.AddChild(Bar(army.Strength, 26,
                army.Strength < 0.55f ? "state.warning" : "state.positive",
                Simulation.Loc.Format("Fighting strength {0:P0} — {1:N0} of {2:N0} men", army.Strength, army.Personnel, army.MaxPersonnel)));
            var equipmentBar = Bar(army.Equipment, 26, "combat.equipment",
                Simulation.Loc.Format("Equipment: {0:P0}", army.Equipment) + "\n" + EquipmentLogistics.Description(_campaign, army), tooltipMeasure: 72);
            equipmentBar.Name = "EquipmentBar_" + army.Id;
            line.AddChild(equipmentBar);

            var reason = Simulation.StaffReasons.Explain(_campaign, army);
            var reasonMark = Fixed(Title(Simulation.StaffReasons.Label(_campaign, army), 10, Ui.Colour("text.caption")), 52);
            reasonMark.Name = "StaffReason_" + army.Id;
            reasonMark.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            reasonMark.CustomMinimumSize = new Vector2(52, 16);
            reasonMark.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            reasonMark.TooltipText = Ui.Tip(reason);
            line.AddChild(reasonMark);
            line.AddChild(StatusPip(army));
        }

        if (divisions.Count > 120)
            _armyRoster.AddChild(Title(Simulation.Loc.Format("…and {0:N0} more", divisions.Count - 120), 11, Ui.Colour("text.faint")));
    }

    private Control Bar(float value, int width, string fillToken, string tooltip, int tooltipMeasure = 0)
    {
        var frame = new Control
        {
            CustomMinimumSize = new Vector2(width, Mathf.Clamp(Simulation.InterfaceSkin.Metric("combat.panel_bar_height", 12), 10, 20)),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = tooltipMeasure > 0 ? Ui.Tip(tooltip, Mathf.Min(Ui.TooltipMeasure, tooltipMeasure)) : Ui.Tip(tooltip)
        };
        var back = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        back.AddThemeStyleboxOverride("panel", InstrumentMaterials.Meter("surface.inset", inset: true));
        back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddChild(back);

        var fill = new Panel { MouseFilter = MouseFilterEnum.Ignore, Visible = value > 0f };
        fill.AddThemeStyleboxOverride("panel", InstrumentMaterials.Meter(fillToken));
        fill.AnchorLeft = 0f; fill.AnchorTop = 0f; fill.AnchorBottom = 1f;
        fill.AnchorRight = Mathf.Clamp(value, 0f, 1f);
        fill.OffsetLeft = 0f; fill.OffsetTop = 0f; fill.OffsetRight = 0f; fill.OffsetBottom = 0f;
        frame.AddChild(fill);
        return frame;
    }

    private Control StatusPip(Army army)
    {
        var stores = Simulation.StrandedGarrisons.StoresBadge(_campaign, army);
        var (glyph, token, help) =
            army.CutOff ? (stores.Text.Length > 0 ? "⊘ " + stores.Text : "⊘",
                stores.Severity == Simulation.StrandedGarrisons.BadgeNegative ? "state.negative" : "state.warning",
                LogisticsSystem.DescribeSupply(_campaign, army)) :
            army.InBattle ? ("⚔", "state.warning", Tr("In battle") + "\n" + Tr("shell.ws.battle.jump_battle_tip")) :
            army.LastMoveResult == ArmyMoveResult.RouteInterrupted ? ("!", "state.warning", "Route interrupted by changed territorial control. Issue a new destination.") :
            army.LivingOffLand ? ("≈", "state.info", LogisticsSystem.DescribeSupply(_campaign, army)) :
            army.SupplyCongestion < 0.999f ? ("△", "state.warning", LogisticsSystem.DescribeSupply(_campaign, army)) :
            army.SupplyLevel < 0.45f ? ("△", "state.warning", "Short of supply") :
            army.Organisation < 0.35f ? ("↓", "state.critical", "Organisation is spent") :
            LandCrossings.WaitingAt(_campaign, army) is { } shutFerry ? ("!", "state.warning", LandCrossings.WaitingSentence(shutFerry)) :
            army.IsMoving ? ("→", "state.info", "On the march") :
            IsOffensive(army) && army.PlanActive ? ("▶", "state.positive", "Plan executing") :
            IsOffensive(army) ? ("◷", "text.muted", "Plan preparing") :
            ("▪", "text.faint", "Holding");

        if (army.InBattle && army.BattleId >= 0)
        {
            var battleId = army.BattleId;
            var btn = new Button
            {
                Name = "SupplyStatus_" + army.Id,
                Text = glyph,
                Flat = true,
                CustomMinimumSize = new Vector2(20, 20),
                FocusMode = FocusModeEnum.None,
                MouseDefaultCursorShape = CursorShape.PointingHand,
                TooltipText = Ui.Tip(help, Mathf.Min(Ui.TooltipMeasure, 72))
            };
            btn.AddThemeColorOverride("font_color", Ui.Colour(token));
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.Pressed += () =>
            {
                var b = _campaign?.Battles.FirstOrDefault(x => x.Id == battleId);
                if (b is not null) ShowBattleDetails(b);
            };
            return btn;
        }

        var pip = Title(glyph, 12, Ui.Colour(token));
        pip.AddThemeFontOverride("font", PresentationFonts.Tabular());
        pip.Name = "SupplyStatus_" + army.Id;
        pip.CustomMinimumSize = new Vector2(army.CutOff && stores.Text.Length > 0 ? 28 : 16, 0);
        pip.HorizontalAlignment = HorizontalAlignment.Center;
        pip.TooltipText = Ui.Tip(help, Mathf.Min(Ui.TooltipMeasure, 72));
        pip.MouseFilter = MouseFilterEnum.Stop;
        return pip;
    }

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
            Simulation.Loc.Format("Equipment: {0:P0} · experience: {1:P0}", army.Equipment, army.Experience) + "\n" +
            EquipmentLogistics.Description(_campaign, army) + "\n" +
            Simulation.Loc.Format("Supply: {0:P0}", army.SupplyLevel) + "\n" +
            LogisticsSystem.DescribeSupply(_campaign, army) + "\n" +
            Simulation.Loc.Format("Entrenchment: {0:P0}", army.Entrenchment) + "\n";
        if (IsOffensive(army))
            text += Simulation.Loc.Format("Plan preparation: {0:P0} (up to +{1:P0} attack)", army.Planning, _campaign.Defines.MaxPlanningAttackBonus) + "\n";
        if (IsOffensive(army) && !army.InBattle)
            text += ArmyAssaultPresentation.Description(_campaign, army) + "\n";
        text += "\n" + Simulation.Loc.Format("{0} · province #{1:N0}",
                    state is null ? Simulation.Loc.Get("Unmapped ground") : state.DisplayName, army.ProvinceId) + "\n" +
                Simulation.Loc.Format("Order: {0} · posture: {1}", OrderName(army.OrderKind), StanceName(army.Stance)) + "\n" +
                Simulation.ArmyVocabulary.Situation(_campaign, army) + "\n" +
                Simulation.StaffReasons.Explain(_campaign, army);
        if (army.LastMoveResult == ArmyMoveResult.RouteInterrupted)
            text += "\n" + Simulation.Loc.Get("The route is cut. Choose a new destination.");
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
                _campaign?.Player is { } player && candidate.NationId == player.Id && candidate.TemplateId == template));
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
                Simulation.Plural.Key("{0:N0} division selected.", _selectedArmies.Count), _selectedArmies.Count));
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

    private string CommanderCaption(ArmyGroup group) =>
        group is null || group.Commander == Simulation.Generals.Unassigned || group.CommanderTrait.Length == 0
            ? Tr(group?.Commander ?? "No commander assigned")
            : Simulation.Loc.Format("{0} ({1}, skill {2:0}%, {3} won / {4} lost)", group.Commander,
                Tr(Simulation.Generals.Trait(group.CommanderTrait).Name), group.StaffSkill * 100f, group.Victories, group.Defeats);
}
