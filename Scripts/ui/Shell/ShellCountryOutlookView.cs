using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Renders Capital city navigation and Domestic Stability Outlook (with expandable factor breakdown)
/// inside <see cref="ShellCountryDossierView"/>. Read-only presentation reusing canonical
/// <see cref="StabilityOutlook"/>, <see cref="CapitalCore"/>, and <see cref="CountryNames"/> models.
/// Zero raw Color literals; all styling routes through <see cref="Ui.Colour"/> tokens.
/// </summary>
internal static class ShellCountryOutlookView
{
    public static void AddCapitalRow(
        ShellContext ctx, VBoxContainer parent, Campaign world, Nation nation)
    {
        var capital = CountryNames.Capital(nation, world);
        if (string.IsNullOrWhiteSpace(capital)) return;

        var title = Loc.Get(CountryNames.CapitalTitle(world, nation));
        if (CapitalCore.HasSeat(world, nation) && ctx.FocusLatLon is not null)
        {
            var isTouch = ctx.IsTouchMode?.Invoke() ?? (Platform.PlatformProfile.Current?.IsTouchPrimary == true);
            var capturedLat = CapitalCore.EffectiveLatitude(world, nation);
            var capturedLon = CapitalCore.EffectiveLongitude(world, nation);

            var btn = new Button
            {
                Name = "ShellOverviewCapitalRow",
                CustomMinimumSize = new Vector2(0, isTouch ? 34 : 26),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipContents = true,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = Ui.Tip($"{title}: {capital}")
            };
            btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 2, 6, 2));
            btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 2, 6, 2));
            btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(underlineBottom: true));
            btn.Pressed += () =>
            {
                ctx.PlayCue?.Invoke("ui_select");
                ctx.FocusLatLon(capturedLat, capturedLon, 8f);
            };
            parent.AddChild(btn);

            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            row.OffsetLeft = 6;
            row.OffsetRight = -6;
            row.AddThemeConstantOverride("separation", 6);
            btn.AddChild(row);

            if (ctx.Icon("icon_shell_capital") is { } capIcon)
            {
                var ico = new TextureRect
                {
                    Texture = capIcon,
                    CustomMinimumSize = new Vector2(14, 14),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                ShellContext.ApplyIconRelief(ico, Ui.Colour("shell.brass_hi"), 0.55f);
                row.AddChild(ico);
            }

            row.AddChild(ShellCountryDossierView.MakeLabel(ctx, title, "shell.sans", 11, "shell.paper_muted", clip: false, expand: false));
            var capLbl = ShellCountryDossierView.MakeLabel(ctx, capital, "shell.serif_semibold", 11, "shell.brass_hi", clip: true, expand: true);
            capLbl.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(capLbl);
            return;
        }

        var kv = new HBoxContainer { Name = "ShellOverviewCapitalRow", CustomMinimumSize = new Vector2(0, 20), ClipContents = true };
        kv.AddThemeConstantOverride("separation", 6);
        kv.AddChild(ShellCountryDossierView.MakeLabel(ctx, title, "shell.sans", 11, "shell.paper_muted", clip: false, expand: false));
        var val = ShellCountryDossierView.MakeLabel(ctx, capital, "shell.sans", 11, "shell.paper", clip: true, expand: true);
        val.HorizontalAlignment = HorizontalAlignment.Right;
        kv.AddChild(val);
        parent.AddChild(kv);
    }

    public static void AddOverviewStabilityDragRow(
        ShellContext ctx, VBoxContainer parent, Campaign world, Nation nation, bool canInspect)
    {
        if (!canInspect) return;
        var outlook = StabilityOutlook.Read(world, nation);
        if (outlook?.MainDrag is not { } drag) return;

        var row = new HBoxContainer
        {
            Name = "ShellOverviewMainDragRow",
            CustomMinimumSize = new Vector2(0, 20),
            ClipContents = true,
            TooltipText = Ui.Tip(BuildTooltip(outlook))
        };
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(ShellCountryDossierView.MakeLabel(ctx, Loc.Get("Domestic outlook"), "shell.sans", 11, "shell.paper_muted", clip: false));
        var val = ShellCountryDossierView.MakeLabel(ctx, Loc.Format("Main drag: {0}", Loc.Get(drag.Source)),
            "shell.sans", 11, "shell.state.warning_text", clip: true, expand: true);
        val.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(val);
        parent.AddChild(row);
    }

    public static void AddStabilityOutlookCard(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation nation)
    {
        var isOwn = world.Player?.Id == nation.Id;
        var observing = ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        if (!isOwn && !observing && !CampaignIntel.PlayerCanInspect(world, nation.Id)) return;

        var outlook = StabilityOutlook.Read(world, nation);
        if (outlook is null) return;

        var toneTok = MapToneToken(outlook.Tone);
        var cardCol = ShellCountryDossierView.MakeSectionCard(
            ctx, root, "ShellSocietyStabilityOutlook", "icon_stability",
            Loc.Get("Domestic outlook"), Loc.Get(outlook.Direction), toneTok);

        if (cardCol.GetParent<Control>() is { } card)
            card.TooltipText = Ui.Tip(BuildTooltip(outlook));

        var kpiGrid = new GridContainer
        {
            Name = "ShellStabilitySummaryGrid",
            Columns = 2,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        kpiGrid.AddThemeConstantOverride("h_separation", 6);
        kpiGrid.AddThemeConstantOverride("v_separation", 4);
        cardCol.AddChild(kpiGrid);

        AddMiniFigure(ctx, kpiGrid, Loc.Get("Current stability"), Fmt.Percent(outlook.Current), "shell.paper");
        var targetTok = outlook.Target < outlook.Current - 0.005f
            ? "shell.state.warning_text"
            : outlook.Target > outlook.Current + 0.005f
                ? "shell.state.positive"
                : "shell.paper";
        AddMiniFigure(ctx, kpiGrid, Loc.Get("Under current conditions"), Fmt.Percent(outlook.Target), targetTok);

        if (outlook.MainDrag is { } drag)
        {
            var dragBox = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            dragBox.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.state.warning_text", 1, 2, 6, 3, 6, 3));
            var dragLbl = WrapLabel(ctx, Loc.Format("Main drag: {0}", Loc.Get(drag.Source)), 11, "shell.state.warning_text", "ShellStabilityMainDrag");
            dragBox.AddChild(dragLbl);
            cardCol.AddChild(dragBox);
        }

        AddCalculationExpander(ctx, cardCol, nation, outlook);
    }

    private static void AddCalculationExpander(
        ShellContext ctx, VBoxContainer cardCol, Nation nation, StabilityOutlook outlook)
    {
        var isTouch = ctx.IsTouchMode?.Invoke() ?? (Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        var nationId = nation.Id;
        var details = new VBoxContainer
        {
            Name = "ShellStabilityDrivers",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        details.AddThemeConstantOverride("separation", 4);

        var toggle = new Button
        {
            Name = "ShellStabilityCalcToggle",
            CustomMinimumSize = new Vector2(0, isTouch ? 32 : 25),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("Read the stability calculation without hovering. This does not change policy."))
        };
        var normalStyle = ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 2, 6, 2);
        var hoverStyle = ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 6, 2, 6, 2);
        toggle.AddThemeStyleboxOverride("normal", normalStyle);
        toggle.AddThemeStyleboxOverride("hover", hoverStyle);
        toggle.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(underlineBottom: true));
        toggle.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        toggle.AddThemeFontSizeOverride("font_size", 11);
        toggle.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        toggle.AddThemeColorOverride("font_hover_color", Ui.Colour("shell.brass_hi"));

        void ApplyOpenState(bool open)
        {
            details.Visible = open;
            if (open) ctx.CollapsedStabilityBreakdownNations.Remove(nationId);
            else ctx.CollapsedStabilityBreakdownNations.Add(nationId);
            toggle.Text = Loc.Get(open ? "Hide calculation" : "Show calculation");
        }

        toggle.Pressed += () =>
        {
            ctx.PlayCue?.Invoke("ui_select");
            ApplyOpenState(!details.Visible);
        };
        cardCol.AddChild(toggle);
        cardCol.AddChild(details);
        ApplyOpenState(!ctx.CollapsedStabilityBreakdownNations.Contains(nationId));

        details.AddChild(WrapLabel(ctx, Loc.Get("How the target is calculated"), 11, "shell.brass_hi", "ShellStabilityCalcHeading"));

        foreach (var entry in outlook.Drivers.Entries)
        {
            var row = new HBoxContainer { Name = "ShellStabilityDriverRow", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            row.AddThemeConstantOverride("separation", 8);

            var caption = WrapLabel(ctx, Loc.Get(entry.Source), 11, "shell.paper_muted", null);
            var valTok = entry.Value < 0f ? "shell.state.negative_text" : "shell.state.positive";
            var amount = ShellCountryDossierView.MakeLabel(
                ctx, Loc.Format("{0:+0.0;-0.0;0.0} pp", entry.Value * 100f),
                "shell.sans", 11, valTok, tabular: true);
            amount.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            amount.HorizontalAlignment = HorizontalAlignment.Right;

            row.AddChild(caption);
            row.AddChild(amount);
            details.AddChild(row);

            if (IsOverstretch(entry) && outlook.OverstretchNote.Length > 0)
                details.AddChild(WrapLabel(ctx, outlook.OverstretchNote, 10, "shell.paper_faint", "ShellStabilityOverstretchNote"));
        }

        details.AddChild(WrapLabel(ctx, Loc.Format("Target under current conditions: {0}", Fmt.Percent(outlook.Target)),
            11, "shell.paper", "ShellStabilityCalcTotal"));
        details.AddChild(WrapLabel(ctx, Loc.Get("Conditions, not a prediction: stability moves gradually towards this level at economic reviews. War, living standards and government can change it."),
            10, "shell.paper_faint", "ShellStabilityCalcNote"));
    }

    private static void AddMiniFigure(ShellContext ctx, GridContainer grid, string caption, string value, string valueTok)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        box.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 6, 4, 6, 4));
        grid.AddChild(box);

        var col = new VBoxContainer { ClipContents = true };
        col.AddThemeConstantOverride("separation", 1);
        box.AddChild(col);
        col.AddChild(ShellCountryDossierView.MakeLabel(ctx, caption, "shell.sans", 10, "shell.paper_faint", clip: true));
        col.AddChild(ShellCountryDossierView.MakeLabel(ctx, value, "shell.sans", 14, valueTok, clip: true, tabular: true));
    }

    private static Label WrapLabel(ShellContext ctx, string text, int size, string colorTok, string name)
    {
        var lbl = new Label
        {
            Text = text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        if (!string.IsNullOrEmpty(name)) lbl.Name = name;
        lbl.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        lbl.AddThemeFontSizeOverride("font_size", size);
        lbl.AddThemeColorOverride("font_color", Ui.Colour(colorTok));
        return lbl;
    }

    private static string BuildTooltip(StabilityOutlook outlook)
    {
        var explanation = Loc.Get("Conditions, not a prediction: stability moves gradually towards this level at economic reviews. War, living standards and government can change it.");
        var lines = string.Join("\n", outlook.Drivers.Entries.Select(entry =>
            Loc.Format("{0}: {1:+0.0;-0.0;0.0} pp", Loc.Get(entry.Source), entry.Value * 100f) +
            (IsOverstretch(entry) && outlook.OverstretchNote.Length > 0 ? "\n    " + outlook.OverstretchNote : "")));
        return explanation + "\n\n" + lines;
    }

    private static bool IsOverstretch(Breakdown.Entry entry) =>
        entry.Source == "Imperial overstretch" || entry.Source == Loc.Get("Imperial overstretch");

    private static string MapToneToken(string simulationTone) => simulationTone switch
    {
        "state.positive" => "shell.state.positive",
        "state.warning" => "shell.state.warning_text",
        _ => "shell.brass_hi"
    };
}
