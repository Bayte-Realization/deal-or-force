using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// Single-war detail view inside the War Ledger: framed flag-only coalition clusters,
    /// full-scale WarBar tug, responsive 3-column / 2+1-column breakdown (How it is going,
    /// What it has cost, clickable Engagements), and wrapping HFlowContainer diplomacy/peace controls.
    /// </summary>
    private void RefreshWarLedgerDetail()
    {
        var war = _campaign.Wars.Find(candidate => candidate.Id == _warLedgerOpenWarId);
        if (war is null)
        {
            _warLedgerSummary.Text = Tr("That war is over.");
            return;
        }
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

        _warLedgerTitle.Text = WarNames.Of(_campaign, war) is { Length: > 0 } title ? title.ToUpperInvariant() : Tr("WARS OF THE WORLD");
        _warLedgerSummary.Text =
            (objective is null ? Tr("no claim on ground") : Simulation.Loc.Format("for {0}", objective.DisplayName)) + "  ·  " +
            (war.Ended ? Tr("settled") : Simulation.Loc.Format("{0:N0} day(s) of fighting", days)) +
            (mine ? Tr("  ·  you are a belligerent") : "") +
            (war.CivilConflict ? "  ·  " + Simulation.Loc.Format("Civil conflict · escalation {0}", war.EscalationStage) : "");
        _warLedgerSummary.TooltipText = war.CivilConflict && !war.Ended
            ? Ui.Tip(Simulation.Loc.Format("Talks can open in {0} days. Total defeat can still end the war; reserve mobilisation requires people and funds.",
                Math.Max(0, war.AiNegotiationUnlockDay - _campaign.ElapsedDays))) : "";

        Label Caption(string text) => RowLabel(text, 11, Ui.Colour("text.caption"));

        Label Body(string text, int size = 13, Color? colour = null, bool wrap = false, bool tabular = false)
        {
            var label = RowLabel("", size, colour ?? Ui.Colour("text.body"));
            if (tabular) label.AddThemeFontOverride("font", PresentationFonts.Tabular());
            label.Text = text;
            if (wrap) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            return label;
        }

        Label Yielding(Label label)
        {
            label.ClipText = true;
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }

        WarBar Bar(float height, float width = 0f) => new()
        {
            CustomMinimumSize = new Vector2(width, height),
            SizeFlagsHorizontal = width > 0f ? SizeFlags.ShrinkCenter : SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Track = Ui.Colour("war.track"),
            MarkerColour = Ui.Colour("war.front_line"),
            TickColour = Ui.Colour("shell.paper", 0.45f)
        };

        // ------------------------------------------------------------------ the two sides + flag-only coalition clusters
        var banner = new PanelContainer { ClipContents = true };
        banner.AddThemeStyleboxOverride("panel", WarInsetStyle(18, 12));
        _warDetailRows.AddChild(banner);
        var bannerRows = new VBoxContainer { ClipContents = true };
        bannerRows.AddThemeConstantOverride("separation", 10);
        banner.AddChild(bannerRows);

        var faces = new HBoxContainer { ClipContents = true };
        faces.AddThemeConstantOverride("separation", 14);
        bannerRows.AddChild(faces);

        Control Side(Nation nation, bool attacking)
        {
            var box = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            box.AddThemeConstantOverride("separation", 10);
            var flag = FlagBadge(nation?.Tag ?? "", 44, nation is not null ? WarMemberTooltip(war, nation, attacking) : "");
            TintBlankFlag(flag, nation);
            var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, ClipContents = true };
            words.AddThemeConstantOverride("separation", 1);
            var name = Yielding(Body(nation?.DisplayName ?? "?", 20, mine ? Ui.Colour("text.accent") : Ui.Colour("text.bright")));
            var role = Yielding(Body(WarSideLine(war, attacking), 11, Ui.Colour("text.caption")));
            var support = Yielding(Body(Simulation.Loc.Format("war support at home {0}",
                (attacking ? war.AttackerWarSupport : war.DefenderWarSupport).ToString("P0")), 11, Ui.Colour("text.muted"), tabular: true));
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
            SizeFlagsStretchRatio = 1.35f,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ClipContents = true
        };
        front.AddThemeConstantOverride("separation", 5);
        var standing = Body(WarStanding(war, attacker, defender), 14,
            Mathf.Abs(war.WarScore) < 0.5f ? Ui.Colour("text.body_soft") : NationColour(war.WarScore >= 0f ? attacker : defender), tabular: true);
        standing.HorizontalAlignment = HorizontalAlignment.Center;
        front.AddChild(standing);
        var tug = Bar(24);
        tug.Name = "WarLedgerDetailScoreBar";
        tug.Tug(Mathf.Clamp(war.WarScore / WarLedgerBarScale, -1f, 1f), attackerColour, defenderColour);
        front.AddChild(tug);
        var scale = Body(Simulation.Loc.Format("the front line is drawn at the war score; the end of the bar is {0}",
            WarLedgerBarScale.ToString("0")), 11, Ui.Colour("text.faint"), tabular: true);
        scale.Name = "WarLedgerScaleCaption";
        scale.HorizontalAlignment = HorizontalAlignment.Center;
        front.AddChild(scale);
        faces.AddChild(front);
        faces.AddChild(Side(defender, false));

        // Framed FLAG-ONLY coalition clusters (full name, role, divisions & losses in hover tooltip!)
        if (war.Side(true).Count() + war.Side(false).Count() > 2)
        {
            var coalitions = new HBoxContainer { ClipContents = true };
            coalitions.AddThemeConstantOverride("separation", 20);
            bannerRows.AddChild(coalitions);

            VBoxContainer CoalitionGrid(bool attacking)
            {
                var wrap = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
                wrap.AddThemeConstantOverride("separation", 4);
                var hdr = Caption(Tr(attacking ? "shell.ws.war.coalition_attackers" : "shell.ws.war.coalition_defenders"));
                if (!attacking) hdr.HorizontalAlignment = HorizontalAlignment.Right;
                wrap.AddChild(hdr);

                var flow = new HFlowContainer
                {
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    ClipContents = true,
                    Alignment = attacking ? FlowContainer.AlignmentMode.Begin : FlowContainer.AlignmentMode.End
                };
                flow.AddThemeConstantOverride("h_separation", 5);
                flow.AddThemeConstantOverride("v_separation", 5);
                foreach (var id in war.Side(attacking))
                {
                    var member = _campaign.NationById(id);
                    if (member is null) continue;
                    var flag = FlagBadge(member.Tag, 20, WarMemberTooltip(war, member, attacking));
                    TintBlankFlag(flag, member);
                    flow.AddChild(flag);
                }
                wrap.AddChild(flow);
                return wrap;
            }
            coalitions.AddChild(CoalitionGrid(true));
            coalitions.AddChild(CoalitionGrid(false));
        }

        // ------------------------------------------------------------------ responsive columns (3-col on 1080p, 2+1 on 720p)
        var narrow = GetViewport().GetVisibleRect().Size.X < 1440f;
        var topCols = new HBoxContainer { ClipContents = true };
        topCols.AddThemeConstantOverride("separation", 12);
        _warDetailRows.AddChild(topCols);

        VBoxContainer Column(Container host, string caption, float ratio)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = ratio, ClipContents = true };
            panel.AddThemeStyleboxOverride("panel", WarInsetStyle(13, 11));
            host.AddChild(panel);
            var stack = new VBoxContainer { ClipContents = true };
            stack.AddThemeConstantOverride("separation", 6);
            panel.AddChild(stack);
            stack.AddChild(Caption(caption));
            return stack;
        }

        // 1. How it is going
        var going = Column(topCols, "HOW IT IS GOING", 1f);
        foreach (var (format, value) in WarScoring.Parts(war))
        {
            var part = new HBoxContainer { ClipContents = true };
            part.AddThemeConstantOverride("separation", 8);
            part.AddChild(Yielding(Body(Simulation.Loc.Format(format, "").Trim(), 12, Ui.Colour("text.body_soft"))));
            var bar = Bar(8, narrow ? 88f : 110f);
            bar.Lean(value / WarLedgerPartScale, attackerColour, defenderColour);
            part.AddChild(bar);
            var figure = Body(value.ToString("+0.0;-0.0;0.0"), 12,
                Mathf.Abs(value) < 0.05f ? Ui.Colour("text.muted") : NationColour(value >= 0f ? attacker : defender), tabular: true);
            figure.HorizontalAlignment = HorizontalAlignment.Right;
            figure.CustomMinimumSize = new Vector2(46, 0);
            part.AddChild(figure);
            going.AddChild(part);
        }

        void Split(VBoxContainer column, string caption, float left, float right, string reading)
        {
            column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 3) });
            var head = new HBoxContainer { ClipContents = true };
            head.AddChild(Yielding(Caption(caption)));
            var figures = Body(reading, 12, Ui.Colour("text.body"), tabular: true);
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

        var advancing = 0;
        var holding = 0;
        var reserve = 0;
        foreach (var army in _campaign.Armies)
        {
            if (army.AssignedWarId != war.Id) continue;
            if (army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead) advancing++;
            else if (army.OrderKind == ArmyOrder.ReserveFront) reserve++;
            else holding++;
        }
        going.AddChild(Yielding(Body(Simulation.Loc.Format(
            "{0:N0} ordered to advance · {1:N0} holding · {2:N0} in reserve",
            advancing, holding, reserve), 11,
            advancing > 0 ? Ui.Colour("text.body_soft") : Ui.Colour("state.warning"), tabular: true)));
        if (attacker is not null && defender is not null)
        {
            going.AddChild(Yielding(Body(Simulation.Loc.Format("Operational profiles: {0} against {1}",
                AiStrategyCatalog.Resolve(_campaign, war, attacker).Id,
                AiStrategyCatalog.Resolve(_campaign, war, defender).Id), 11, Ui.Colour("text.faint"))));
        }

        // 2. What it has cost
        var cost = Column(topCols, "WHAT IT HAS COST", 1f);
        var killedColour = Ui.Colour("war.killed");
        var woundedColour = Ui.Colour("war.wounded");
        var capturedColour = Ui.Colour("war.captured");
        var heaviest = Mathf.Max(1f, Mathf.Max(war.AttackerLosses, war.DefenderLosses));

        void SideCost(Nation leader, float losses, float killed, float wounded, float captured)
        {
            var head = new HBoxContainer { ClipContents = true };
            var coalition = leader is not null && WarLosses.ByCountry(war, war.IsAttacking(leader.Id)).Countries.Any(entry => entry.NationId != leader.Id);
            head.AddChild(Yielding(Body(leader is null ? Tr("nobody")
                : coalition ? Simulation.Loc.Format("{0} and allies", leader.DisplayName) : leader.DisplayName, 13, Ui.Colour("text.bright"))));
            var total = Body(Fmt.People(losses), 14, Ui.Colour("text.bright"), tabular: true);
            total.HorizontalAlignment = HorizontalAlignment.Right;
            head.AddChild(total);
            cost.AddChild(head);
            var bar = Bar(10);
            if (killed + wounded + captured >= 1f)
                bar.Shares((killed / heaviest, killedColour), (wounded / heaviest, woundedColour), (captured / heaviest, capturedColour));
            else
                bar.Shares((losses / heaviest, Ui.Colour("text.muted")));
            cost.AddChild(bar);
            if (killed + wounded + captured >= 1f)
                cost.AddChild(Yielding(Body(Simulation.Loc.Format("{0} killed · {1} wounded · {2} captured",
                    Fmt.People(killed), Fmt.People(wounded), Fmt.People(captured)), 11, Ui.Colour("text.muted"), tabular: true)));
        }

        void CountryCosts(bool attacking)
        {
            var (countries, unattributed) = WarLosses.ByCountry(war, attacking);
            var alone = countries.Count == 1 && countries[0].NationId == (attacking ? war.AttackerId : war.DefenderId);
            if (countries.Count == 0 || (alone && unattributed < 1f)) return;
            void Line(Control mark, string name, float men, string tip, (float Killed, float Wounded, float Captured)? parts)
            {
                var line = new HBoxContainer { TooltipText = Ui.Tip(tip), MouseFilter = MouseFilterEnum.Stop, ClipContents = true };
                line.AddThemeConstantOverride("separation", 6);
                line.AddChild(mark);
                line.AddChild(Yielding(Body(name, 11, Ui.Colour("text.body_soft"))));
                var bar = Bar(6, 62);
                if (parts is { } p)
                    bar.Shares((p.Killed / heaviest, killedColour), (p.Wounded / heaviest, woundedColour), (p.Captured / heaviest, capturedColour));
                else
                    bar.Shares((men / heaviest, Ui.Colour("text.muted")));
                line.AddChild(bar);
                var figure = Body(Fmt.People(men), 11, Ui.Colour("text.bright"), tabular: true);
                figure.HorizontalAlignment = HorizontalAlignment.Right;
                figure.CustomMinimumSize = new Vector2(54, 0);
                line.AddChild(figure);
                cost.AddChild(line);
            }
            foreach (var (id, toll) in countries)
            {
                var member = _campaign.NationById(id);
                var name = member?.DisplayName ?? Tr("a country that is gone");
                Line(member is null ? new Control { CustomMinimumSize = new Vector2(20, 12) } : FlagBadge(member.Tag, 12, name),
                    name, toll.Total,
                    name + "\n" + Simulation.Loc.Format("{0} killed · {1} wounded · {2} captured",
                        Fmt.People(toll.Killed), Fmt.People(toll.Wounded), Fmt.People(toll.Captured)),
                    (toll.Killed, toll.Wounded, toll.Captured));
            }
            if (unattributed >= 1f)
                Line(new Control { CustomMinimumSize = new Vector2(20, 12) }, Tr("Earlier, not recorded by country"), unattributed,
                    Tr("Lost before this campaign began keeping each country's losses apart. They are in the side's total."), null);
        }

        SideCost(attacker, war.AttackerLosses, war.AttackerKilled, war.AttackerWounded, war.AttackerCaptured);
        CountryCosts(true);
        SideCost(defender, war.DefenderLosses, war.DefenderKilled, war.DefenderWounded, war.DefenderCaptured);
        CountryCosts(false);

        var key = new HBoxContainer { ClipContents = true };
        key.AddThemeConstantOverride("separation", 10);
        foreach (var (colour, word) in new[] { (killedColour, "killed"), (woundedColour, "wounded"), (capturedColour, "captured") })
        {
            key.AddChild(new ColorRect { Color = colour, CustomMinimumSize = new Vector2(9, 9), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            key.AddChild(RowLabel(word, 11, Ui.Colour("text.caption")));
        }
        cost.AddChild(key);

        var heldFromAttackers = war.Side(true).Sum(id => WarLosses.HeldFrom(war, id));
        var heldFromDefenders = war.Side(false).Sum(id => WarLosses.HeldFrom(war, id));
        if (heldFromAttackers + heldFromDefenders >= 1f)
            Split(cost, "PRISONERS STILL HELD", heldFromAttackers, heldFromDefenders,
                Simulation.Loc.Format("{0} against {1}", Fmt.People(heldFromAttackers), Fmt.People(heldFromDefenders)));

        // 3. Engagements (3rd column on 1080p+, full-width bottom card on 720p)
        var fought = _campaign.Battles.Count(battle => battle.WarId == war.Id && !battle.Ended);
        var whereHost = narrow ? (Container)_warDetailRows : topCols;
        var where = Column(whereHost, Tr("ENGAGEMENTS") + "  ·  " + Simulation.Loc.Format("{0} being fought", fought.ToString("N0")), 1f);
        var battles = _campaign.Battles
            .Where(battle => battle.WarId == war.Id && !battle.Ended)
            .OrderByDescending(battle => battle.AttackerLosses + battle.DefenderLosses)
            .Take(8)
            .ToList();
        if (battles.Count == 0)
        {
            where.AddChild(Body(Tr("Nothing is in contact."), 12, Ui.Colour("text.muted")));
        }
        else
        {
            foreach (var battle in battles)
            {
                var capturedProv = battle.ProvinceId;
                var place = _campaign.ProvinceMap is null
                    ? Simulation.Loc.Format("province {0}", capturedProv.ToString("N0"))
                    : BattlePlaceName(capturedProv);
                var sub = Simulation.Loc.Format("day {0}", (_campaign.ElapsedDays - battle.StartedDay + 1).ToString("N0")) + "  ·  " +
                          Simulation.Loc.Format("{0} casualties", Fmt.People(battle.AttackerLosses + battle.DefenderLosses)) + "  ·  " +
                          Simulation.Loc.Get(battle.Outcome);

                var bBtn = new Button
                {
                    CustomMinimumSize = new Vector2(0, 42),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    ClipContents = true,
                    TooltipText = Ui.Tip(Tr("shell.ws.war.battle_jump_tip")),
                    MouseDefaultCursorShape = CursorShape.PointingHand,
                    FocusMode = FocusModeEnum.None
                };
                bBtn.AddThemeStyleboxOverride("normal", WarCardStyle(mine: false, hover: false));
                bBtn.AddThemeStyleboxOverride("hover", WarCardStyle(mine: false, hover: true));
                bBtn.Pressed += () => { CloseWarLedger(); FocusProvince(capturedProv, 6.8f); };

                var entry = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, ClipContents = true };
                entry.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                entry.OffsetLeft = 8; entry.OffsetRight = -8; entry.OffsetTop = 4; entry.OffsetBottom = -4;
                entry.AddThemeConstantOverride("separation", 1);
                entry.AddChild(Yielding(Body(place, 12, Ui.Colour("text.bright"))));
                entry.AddChild(Yielding(Body(sub, 11, Ui.Colour("text.muted"), tabular: true)));
                bBtn.AddChild(entry);
                where.AddChild(bBtn);
            }
        }

        if (war.LastNegotiation.Length > 0)
        {
            var proposal = new PanelContainer { ClipContents = true };
            proposal.AddThemeStyleboxOverride("panel", WarInsetStyle(14, 10));
            var stack = new VBoxContainer { ClipContents = true };
            stack.AddThemeConstantOverride("separation", 4);
            stack.AddChild(Caption("LAST PROPOSAL"));
            stack.AddChild(Body(war.LastNegotiation, 12, Ui.Colour("text.body"), true));
            proposal.AddChild(stack);
            _warDetailRows.AddChild(proposal);
        }

        if (_warDetailBuiltFor != war.Id) BuildWarDetailActions(war, player, mine);
        FitWarLedgerDetail();
        GetTree().CreateTimer(0.05).Timeout += FitWarLedgerDetail;
    }

    private void FitWarLedgerDetail()
    {
        if (_warLedgerOpenWarId < 0 || !IsInstanceValid(_warLedgerLayer) || !_warLedgerLayer.Visible) return;
        var actions = LiveHeight(_warDetailActions);
        FitWarLedger(LiveHeight(_warDetailRows) + (actions > 0f ? actions + 12f : 0f) + 8f);
    }

    private void BuildWarDetailActions(StrategicWar war, Nation player, bool mine)
    {
        _warDetailBuiltFor = war.Id;
        foreach (var child in _warDetailActions.GetChildren()) child.QueueFree();
        if (!mine || war.Ended) return;

        // Calling allies — wrapping HFlowContainer so narrow screens never overflow
        var callable = Simulation.Statecraft.CallableAllies(_campaign, war, player.Id);
        if (callable.Count > 0)
        {
            _warDetailActions.AddChild(RowLabel("CALLING FOR HELP", 12, Ui.Colour("text.caption")));
            var calls = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            calls.AddThemeConstantOverride("h_separation", 6);
            calls.AddThemeConstantOverride("v_separation", 6);
            _warDetailActions.AddChild(calls);
            foreach (var allyId in callable.Take(6))
            {
                var friend = _campaign.NationById(allyId);
                if (friend is null) continue;
                var captured = allyId;
                var blocked = Simulation.Statecraft.CallRefusal(_campaign, war, player.Id, allyId);
                var button = ActionButton(friend.DisplayName, () => CallAllyToWar(war, captured), 140, 30);
                button.Disabled = blocked.Length > 0;
                button.TooltipText = Ui.Tip(blocked.Length > 0
                    ? blocked
                    : Simulation.Loc.Format(
                        "Ask {0} to join this war. They are not obliged to - an offensive call is a favour, and refusing one costs goodwill rather than the alliance.",
                        friend.DisplayName));
                calls.AddChild(button);
            }
        }

        // Prisoner exchanges — wrapping HFlowContainer with concise caption + rich tooltip
        var holders = war.EnemiesOf(player.Id)
            .Where(id => WarLosses.HeldFrom(war, id) >= 1f)
            .Take(4)
            .ToList();
        if (holders.Count > 0 || WarLosses.HeldFrom(war, player.Id) >= 1f)
        {
            _warDetailActions.AddChild(RowLabel("PRISONERS", 12, Ui.Colour("text.caption")));
            var exchanges = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            exchanges.AddThemeConstantOverride("h_separation", 6);
            exchanges.AddThemeConstantOverride("v_separation", 6);
            _warDetailActions.AddChild(exchanges);
            foreach (var enemyId in holders.Count > 0 ? holders : war.EnemiesOf(player.Id).Take(1).ToList())
            {
                var enemy = _campaign.NationById(enemyId);
                if (enemy is null) continue;
                var captured = enemyId;
                var refusal = WarLosses.ExchangeRefusal(_campaign, war, player.Id, enemyId);
                var button = ActionButton(Simulation.Loc.Format("shell.ws.war.exchange_short_fmt", enemy.DisplayName),
                    () => ExchangePrisoners(war, captured), 172, 30);
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

        BuildSeparatePeaceActions(war, player);
        _warDetailActions.AddChild(RowLabel("ENDING IT", 12, Ui.Colour("text.caption")));

        var headRow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        headRow.AddThemeConstantOverride("h_separation", 8);
        headRow.AddThemeConstantOverride("v_separation", 6);
        _warDetailActions.AddChild(headRow);

        var conference = ActionButton("Open the peace conference", () => OpenPeaceConference(war), 280, 34, true);
        conference.TooltipText = Ui.Tip(Tr("Draw the settlement on the map: claim the ground you mean to keep, " +
                                 "priced against what the war is actually worth."));
        headRow.AddChild(conference);

        var jumpBtn = ActionButton(Tr("shell.ws.war.jump_btn"), () => JumpToWarFront(war.Id), 110, 34);
        jumpBtn.TooltipText = Ui.Tip(Tr("shell.ws.war.jump_tip"));
        headRow.AddChild(jumpBtn);

        // Wrapping HFlowContainer for all 7 quick peace-term buttons so they NEVER overflow at 720p/1080p
        var quick = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        quick.AddThemeConstantOverride("h_separation", 6);
        quick.AddThemeConstantOverride("v_separation", 6);
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
            var button = ActionButton(PeaceTermsName(terms), () => ProposePeace(war, captured), 138, 30);
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

    private string StateNameOfProvince(int provinceId)
    {
        var topology = _campaign.ProvinceMap;
        if (topology is null || provinceId <= 0 || provinceId > topology.ProvinceCount)
            return Simulation.Loc.Format("province {0}", provinceId.ToString("N0"));
        var stateIndex = topology.ProvinceToState[provinceId];
        var state = _campaign.States.FirstOrDefault(candidate => candidate.MapIndex == stateIndex);
        return state?.DisplayName ?? Simulation.Loc.Format("province {0}", provinceId.ToString("N0"));
    }

    private string BattlePlaceName(int provinceId) =>
        Simulation.CityCatalog.NearestTown(_campaign.ProvinceMap, provinceId)?.DisplayName ??
        StateNameOfProvince(provinceId);
}
