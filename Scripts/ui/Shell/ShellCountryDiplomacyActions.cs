using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Sculpted Direct Diplomatic Actions 2-column card for the Country Dossier Diplomacy tab
/// (<see cref="ShellCountryDiplomacyView"/>). Every button routes strictly through existing
/// <see cref="ICampaignCommand"/> paths and displays cost, acceptance indicator, or refusal reasons.
/// </summary>
internal static class ShellCountryDiplomacyActions
{
    public static void Populate(
        ShellContext ctx,
        VBoxContainer root,
        Label feedbackLabel,
        Campaign world,
        Nation player,
        Nation nation,
        bool observing,
        Action onMutated)
    {
        var card = new PanelContainer
        {
            Name = "ShellDipActionsCard",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        card.AddThemeStyleboxOverride("panel", ChromePlateArt.BuildCardWrapperStyle(8, 6, 8, 6) ??
            ctx.BorderedBox("shell.plate_bottom", "shell.engrave", 1, 3, 8, 6, 8, 6));
        root.AddChild(card);

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        col.AddThemeConstantOverride("separation", 5);
        card.AddChild(col);

        var head = new HBoxContainer { ClipContents = true };
        head.AddThemeConstantOverride("separation", 6);
        col.AddChild(head);
        if (ctx.Icon("icon_shell_treaty") is { } hIco)
        {
            var tr = new TextureRect
            {
                Texture = hIco, CustomMinimumSize = new Vector2(14, 14),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            ShellContext.ApplyIconRelief(tr, Ui.Colour("shell.icon"), 0.55f);
            head.AddChild(tr);
        }
        var hLbl = new Label { Text = Loc.Get("shell.dip.actions_header"), ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hLbl.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        hLbl.AddThemeFontSizeOverride("font_size", 12);
        hLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        head.AddChild(hLbl);
        col.AddChild(ShellContext.CreateRuledLine(91, "shell.rule", 1.0f, 3f));

        var rel = CampaignDiplomacy.ReadOrNeutral(world, player.Id, nation.Id);
        var atWar = CampaignDiplomacy.AtWar(world, player.Id, nation.Id) || rel.Stance == DiplomaticStance.AtWar;

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 5);
        col.AddChild(grid);

        void ShowFeedback(string msg)
        {
            if (feedbackLabel is null || string.IsNullOrWhiteSpace(msg)) return;
            feedbackLabel.Text = msg;
            feedbackLabel.Visible = true;
        }

        // 1. Improve Relations / Recall Envoy
        var hasMission = DiplomaticOutreach.Of(rel, player.Id) is not null;
        var missionRefusal = observing ? Loc.Get("shell.obs.readonly_banner")
            : DiplomaticOutreach.Refusal(world, player.Id, nation.Id, active: !hasMission);
        AddActionBtn(ctx, grid, "ShellDipAct_Mission", "icon_diplomacy",
            Loc.Get(hasMission ? "shell.dip.act_recall_envoy" : "shell.dip.act_send_envoy"),
            disabled: missionRefusal.Length > 0,
            tip: AppendRefusal(DiplomaticOutreach.Description(world, player.Id, nation.Id), missionRefusal),
            primary: false, willingHint: null,
            () =>
            {
                var fb = ctx.SendOrRecallMission?.Invoke(nation) ?? "";
                ShowFeedback(fb);
                onMutated?.Invoke();
            });

        // 2. Alliance (Propose / Accept / Break)
        var isAllied = rel.Stance == DiplomaticStance.Allied;
        var hasAllianceOffer = !isAllied && AllianceSystem.OfferedTo(world, player)?.Id == nation.Id;
        var allyRefusal = observing ? Loc.Get("shell.obs.readonly_banner") : "";
        var allyTip = "";
        bool? allyWilling = null;
        if (!observing && !isAllied && !hasAllianceOffer)
        {
            if (!AllianceSystem.CanPropose(world, player, nation, out allyRefusal, preview: true))
                allyTip = allyRefusal;
            else
            {
                var w = AllianceSystem.Willingness(world, nation, player, why: null, refreshAssessment: false);
                allyWilling = w >= 0f;
                allyTip = FormatWillingness(w);
            }
        }
        AddActionBtn(ctx, grid, "ShellDipAct_Alliance", "icon_alliance",
            Loc.Get(isAllied ? "shell.dip.act_break_alliance" : hasAllianceOffer ? "shell.dip.act_accept_alliance" : "shell.dip.act_propose_alliance"),
            disabled: allyRefusal.Length > 0,
            tip: AppendRefusal(allyTip, allyRefusal),
            primary: hasAllianceOffer, willingHint: allyWilling,
            () =>
            {
                if (isAllied)
                    RunCommand(world, new BreakAllianceCommand { IssuerId = player.Id, TargetId = nation.Id });
                else if (hasAllianceOffer)
                    RunCommand(world, new AnswerAllianceCommand { IssuerId = player.Id, Accept = true });
                else
                    RunCommand(world, new ProposeAllianceCommand { IssuerId = player.Id, TargetId = nation.Id });
                onMutated?.Invoke();
            });

        // 3. Trade Agreement
        AddAgreementActionBtn(ctx, grid, world, player, nation, rel, AgreementKind.TradeAgreement,
            "ShellDipAct_Trade", "icon_trade", "shell.dip.act_cancel_trade", "shell.dip.act_propose_trade", observing, ShowFeedback, onMutated);

        // 4. Non-Aggression Pact
        AddAgreementActionBtn(ctx, grid, world, player, nation, rel, AgreementKind.NonAggressionPact,
            "ShellDipAct_Nap", "icon_treaty", "shell.dip.act_cancel_nap", "shell.dip.act_propose_nap", observing, ShowFeedback, onMutated);

        // 5. Guarantee Independence
        var weGuarantee = rel.GuaranteedBy(player.Id);
        var guarRefusal = observing ? Loc.Get("shell.obs.readonly_banner") : (atWar ? Loc.Get("Already at war.") : "");
        AddActionBtn(ctx, grid, "ShellDipAct_Guarantee", "icon_alliance",
            Loc.Get(weGuarantee ? "shell.dip.act_revoke_guarantee" : "shell.dip.act_guarantee"),
            disabled: guarRefusal.Length > 0,
            tip: AppendRefusal(Loc.Get("Guarantee their independence; an attack on them is an attack on us."), guarRefusal),
            primary: false, willingHint: null,
            () =>
            {
                RunCommand(world, new GuaranteeCommand { IssuerId = player.Id, TargetId = nation.Id, Guarantee = !weGuarantee });
                onMutated?.Invoke();
            });

        // 6. Military Access
        var theyGrantUs = rel.GrantsMilitaryAccess(nation.Id);
        var accessRefusal = observing ? Loc.Get("shell.obs.readonly_banner")
            : (theyGrantUs ? "" : MilitaryAccess.Refusal(world, player, nation));
        var accessTip = "";
        bool? accessWilling = null;
        if (!observing && !theyGrantUs && accessRefusal.Length == 0)
        {
            var w = MilitaryAccess.Willingness(world, player, nation);
            accessWilling = w >= 0f;
            accessTip = FormatWillingness(w);
        }
        AddActionBtn(ctx, grid, "ShellDipAct_Access", "icon_military",
            Loc.Get(theyGrantUs ? "shell.dip.act_revoke_access" : "shell.dip.act_request_access"),
            disabled: accessRefusal.Length > 0,
            tip: AppendRefusal(accessTip, accessRefusal),
            primary: false, willingHint: accessWilling,
            () =>
            {
                var cmd = new MilitaryAccessCommand
                {
                    IssuerId = player.Id, TargetId = nation.Id,
                    Action = theyGrantUs ? MilitaryAccessAction.Renounce : MilitaryAccessAction.Request
                };
                RunCommand(world, cmd);
                ShowFeedback(cmd.Result);
                onMutated?.Invoke();
            });

        // 7. Embargo / Lift Embargo
        var weEmbargo = rel.EmbargoBy(player.Id);
        var embRefusal = observing ? Loc.Get("shell.obs.readonly_banner") : "";
        AddActionBtn(ctx, grid, "ShellDipAct_Embargo", "icon_shell_warning",
            Loc.Get(weEmbargo ? "shell.dip.act_lift_embargo" : "shell.dip.act_embargo"),
            disabled: embRefusal.Length > 0,
            tip: AppendRefusal(Loc.Get("Impose a trade embargo."), embRefusal),
            primary: false, willingHint: null,
            () =>
            {
                RunCommand(world, new SetEmbargoCommand { IssuerId = player.Id, TargetId = nation.Id, Embargo = !weEmbargo });
                onMutated?.Invoke();
            });

        // 8. State Visit
        var visitRefusal = observing ? Loc.Get("shell.obs.readonly_banner")
            : DiplomaticGestures.Refusal(world, player.Id, nation.Id, "state_visit");
        AddActionBtn(ctx, grid, "ShellDipAct_Visit", "icon_shell_treaty",
            Loc.Get("shell.dip.act_state_visit"),
            disabled: visitRefusal.Length > 0,
            tip: AppendRefusal(Loc.Format("State visit: {0:0.##} political credit and {1}.",
                world.Defines.StateVisitPoliticalCost, Fmt.Money(DiplomaticGestures.Cash(world, player, "state_visit"))), visitRefusal),
            primary: false, willingHint: null,
            () =>
            {
                var cmd = new DiplomaticGestureCommand { IssuerId = player.Id, TargetId = nation.Id, Gesture = "state_visit" };
                RunCommand(world, cmd);
                ShowFeedback(cmd.Done ? Loc.Get("shell.dip.act_state_visit") + " ✓" : cmd.Refusal);
                onMutated?.Invoke();
            });

        // 9. War / Peace + Full Diplomacy
        if (atWar)
        {
            AddActionBtn(ctx, grid, "ShellDipAct_Peace", "icon_treaty",
                Loc.Get("shell.dip.act_negotiate_peace"),
                disabled: observing,
                tip: observing ? Loc.Get("shell.obs.readonly_banner") : Loc.Get("Open the peace conference."),
                primary: !observing, willingHint: null,
                () => ctx.OpenPeaceConferenceFor?.Invoke(nation));
        }
        else
        {
            var warReason = "";
            var canWar = !observing && CampaignDiplomacy.CanDeclareWar(world, player.Id, nation.Id, out warReason);
            var warRefusal = observing ? Loc.Get("shell.obs.readonly_banner") : (canWar ? "" : warReason);
            AddActionBtn(ctx, grid, "ShellDipAct_War", "icon_wars",
                Loc.Get("shell.dip.act_declare_war"),
                disabled: !canWar,
                tip: AppendRefusal(Loc.Get("Begin an immediate land war."), warRefusal),
                primary: false, willingHint: null,
                () => ctx.OpenWarDeclarationFor?.Invoke(nation));
        }

        AddActionBtn(ctx, grid, "ShellDipAct_Full", "icon_diplomacy",
            Loc.Get("shell.dip.open_full"),
            disabled: false,
            tip: Loc.Get("shell.sub_dip"),
            primary: false, willingHint: null,
            () => ctx.OpenDiplomacyFor?.Invoke(nation));
    }

    private static void AddAgreementActionBtn(
        ShellContext ctx, GridContainer grid, Campaign world, Nation player, Nation nation, DiplomaticRelation rel,
        AgreementKind kind, string nodeName, string iconName, string cancelKey, string proposeKey, bool observing,
        Action<string> showFeedback, Action onMutated)
    {
        var signed = AgreementSystem.Signed(rel, kind);
        var refusal = observing ? Loc.Get("shell.obs.readonly_banner") : "";
        var tip = "";
        bool? willing = null;
        if (!observing && !signed)
        {
            if (!AgreementSystem.CanPropose(world, player, nation, kind, out refusal, preview: true))
                tip = refusal;
            else
            {
                var w = AgreementSystem.Willingness(world, nation, player, kind, why: null, preview: true);
                willing = w >= 0f;
                tip = FormatWillingness(w);
            }
        }
        AddActionBtn(ctx, grid, nodeName, iconName, Loc.Get(signed ? cancelKey : proposeKey),
            disabled: refusal.Length > 0, tip: AppendRefusal(tip, refusal), primary: false, willingHint: willing,
            () =>
            {
                if (signed)
                    RunCommand(world, new CancelAgreementCommand { IssuerId = player.Id, TargetId = nation.Id, Kind = kind });
                else
                {
                    var cmd = new ProposeAgreementCommand { IssuerId = player.Id, TargetId = nation.Id, Kind = kind };
                    RunCommand(world, cmd);
                    if (!string.IsNullOrEmpty(cmd.Reason)) showFeedback(cmd.Reason);
                }
                onMutated?.Invoke();
            });
    }

    private static void AddActionBtn(
        ShellContext ctx, GridContainer grid, string name, string iconName, string text, bool disabled,
        string tip, bool primary, bool? willingHint, Action onClick)
    {
        var btn = new Button
        {
            Name = name,
            Disabled = disabled,
            CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            MouseDefaultCursorShape = disabled ? Control.CursorShape.Arrow : Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = string.IsNullOrEmpty(tip) ? "" : Ui.Tip(tip)
        };
        var normal = primary
            ? ctx.NineSlice("button_brass_cta_9s", 8, 6, 3, 6, 3, "shell.plate_top", "shell.brass_hi", 2)
            : ctx.BorderedBox(disabled ? "shell.well_bottom" : "shell.plate_top", "shell.engrave", 1, 2, 6, 3, 6, 3);
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 3, 6, 3));
        btn.AddThemeStyleboxOverride("disabled", ctx.BorderedBox("shell.well_bottom", "shell.rule", 1, 2, 6, 3, 6, 3));
        btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 6, padY: 3));
        btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); onClick?.Invoke(); };

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 6; row.OffsetRight = -6;
        row.AddThemeConstantOverride("separation", 5);
        btn.AddChild(row);

        if (ctx.Icon(iconName) is { } ico)
        {
            var tr = new TextureRect
            {
                Texture = ico, CustomMinimumSize = new Vector2(13, 13),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Modulate = Ui.Colour(disabled ? "shell.paper_ghost" : "shell.icon"),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            row.AddChild(tr);
        }

        var lbl = new Label
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        lbl.AddThemeFontOverride("font", ctx.Font(primary ? "shell.serif_semibold" : "shell.sans"));
        lbl.AddThemeFontSizeOverride("font_size", 11);
        lbl.AddThemeColorOverride("font_color", Ui.Colour(disabled ? "shell.paper_ghost" : "shell.paper"));
        row.AddChild(lbl);

        if (willingHint.HasValue && !disabled)
        {
            var pip = new Label
            {
                Text = willingHint.Value ? "●" : "▲",
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            pip.AddThemeFontSizeOverride("font_size", 9);
            pip.AddThemeColorOverride("font_color", Ui.Colour(willingHint.Value ? "shell.state.positive" : "shell.state.negative_text"));
            row.AddChild(pip);
        }

        grid.AddChild(btn);
    }

    private static void RunCommand(Campaign world, ICampaignCommand command)
    {
        world.Commands.Enqueue(command);
        world.Commands.ApplyAll(world);
    }

    private static string FormatWillingness(float w)
    {
        var score = (int)MathF.Round(w * 100f);
        return Loc.Format(score >= 0 ? "shell.dip.willing_yes_fmt" : "shell.dip.willing_no_fmt", score);
    }

    private static string AppendRefusal(string baseTip, string refusal)
    {
        if (string.IsNullOrEmpty(refusal)) return baseTip;
        return string.IsNullOrEmpty(baseTip) ? refusal : $"{baseTip}\n\n{refusal}";
    }
}
