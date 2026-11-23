using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Holds the live controls of the Top Fiscal Summary Ribbon inside ShellEconomyWorkspaceView.
/// </summary>
internal sealed class EconomyFiscalRibbonNodes
{
    public Label TreasuryVal, TreasurySub;
    public Label FlowVal, FlowSub;
    public ColorRect FlowFill;
    public Control FlowRest;
    public Label DebtVal, DebtSub;
    public ColorRect DebtFill;
    public Control DebtRest;
    public Label InflVal, InflSub;
    public ColorRect InflFill;
    public Control InflRest;
}

internal sealed class EconomyTaxEfficiencyNodes
{
    public PanelContainer Root;
    public TextureRect Icon;
    public Label TitleLabel;
    public Label PercentLabel;
    public Label BodyLabel;
    public ColorRect BarFill;
    public Control BarRest;
}

/// <summary>
/// Top fiscal summary ribbon, Grand Projects CTA card, canvas-drawn spending stack,
/// revenue-vs-spending meter, labeled 6-month treasury trajectory chart, and domestic
/// supply-shortage highlights for ShellEconomyWorkspaceView.
/// </summary>
internal static class ShellEconomyCharts
{
    private static readonly string[] StackAccentTokens =
    {
        "shell.brass_hi", "shell.state.info", "shell.state.positive",
        "shell.state.warning", "shell.state.crisis", "shell.paper_muted"
    };

    public static EconomyFiscalRibbonNodes BuildFiscalSummaryRibbon(ShellContext ctx, VBoxContainer parent)
    {
        var nodes = new EconomyFiscalRibbonNodes();
        var ribbon = new GridContainer { Name = "ShellEconomyFiscalRibbon", Columns = 4, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        ribbon.AddThemeConstantOverride("h_separation", 8);
        parent.AddChild(ribbon);

        CreateRibbonCard(ctx, ribbon, Loc.Get("shell.ws.eco.ribbon_treasury"), out nodes.TreasuryVal, out nodes.TreasurySub);

        var c2 = CreateRibbonCard(ctx, ribbon, Loc.Get("shell.ws.eco.ribbon_flow"), out nodes.FlowVal, out nodes.FlowSub);
        c2.AddChild(BuildMiniGauge(ctx, 0.5f, Ui.Colour("shell.state.positive"), out nodes.FlowFill, out nodes.FlowRest));

        var c3 = CreateRibbonCard(ctx, ribbon, Loc.Get("shell.ws.eco.ribbon_debt"), out nodes.DebtVal, out nodes.DebtSub);
        c3.AddChild(BuildMiniGauge(ctx, 0.2f, Ui.Colour("shell.state.warning"), out nodes.DebtFill, out nodes.DebtRest));

        var c4 = CreateRibbonCard(ctx, ribbon, Loc.Get("shell.ws.eco.ribbon_inflation"), out nodes.InflVal, out nodes.InflSub);
        c4.AddChild(BuildMiniGauge(ctx, 0.1f, Ui.Colour("shell.state.info"), out nodes.InflFill, out nodes.InflRest));

        return nodes;
    }

    public static void RefreshFiscalSummaryRibbon(
        EconomyFiscalRibbonNodes r, Nation n, SovereignBalanceSheet sheet,
        float revTotal, float expTotal, float bal, float debtPct, float inflPct)
    {
        r.TreasuryVal.Text = Fmt.Money(n.Treasury);
        r.TreasuryVal.AddThemeColorOverride("font_color", Ui.Colour(n.Treasury < 0f ? "shell.state.negative_text" : "shell.paper"));
        r.TreasurySub.Text = Loc.Format("shell.ws.eco.credit_headroom_fmt", Fmt.Money(sheet.NewCredit));

        r.FlowVal.Text = Fmt.MoneyDelta(bal);
        r.FlowVal.AddThemeColorOverride("font_color", Ui.Colour(bal < -0.0005f ? "shell.state.negative_text" : "shell.state.positive"));
        r.FlowSub.Text = Loc.Format("shell.ws.eco.rev_exp_fmt", Fmt.Money(revTotal), Fmt.Money(expTotal));
        UpdateMiniGauge(r.FlowFill, r.FlowRest, revTotal / MathF.Max(0.001f, revTotal + expTotal),
            Ui.Colour(bal < -0.0005f ? "shell.state.negative" : "shell.state.positive"));

        r.DebtVal.Text = $"{debtPct:0.1}%";
        r.DebtVal.AddThemeColorOverride("font_color",
            Ui.Colour(debtPct > 90f ? "shell.state.negative_text" : debtPct > 55f ? "shell.state.warning_text" : "shell.paper"));
        r.DebtSub.Text = Fmt.Money(sheet.Principal);
        UpdateMiniGauge(r.DebtFill, r.DebtRest, debtPct / 140f,
            Ui.Colour(debtPct > 90f ? "shell.state.negative" : debtPct > 55f ? "shell.state.warning" : "shell.state.info"));

        r.InflVal.Text = $"{inflPct:0.1}%";
        r.InflVal.AddThemeColorOverride("font_color",
            Ui.Colour(inflPct > 9f ? "shell.state.negative_text" : inflPct > 4.5f ? "shell.state.warning_text" : "shell.state.positive"));
        r.InflSub.Text = Loc.Get(inflPct > 9f ? "shell.ws.eco.inflation_hot" : inflPct > 4.5f ? "shell.ws.eco.inflation_warm" : "shell.ws.eco.inflation_cool");
        UpdateMiniGauge(r.InflFill, r.InflRest, inflPct / 15f,
            Ui.Colour(inflPct > 9f ? "shell.state.negative" : inflPct > 4.5f ? "shell.state.warning" : "shell.state.positive"));
    }

    public static Label BuildGrandProjectsCtaCard(ShellContext ctx, VBoxContainer rightCol)
    {
        var card = new PanelContainer { Name = "ShellEconomyGrandProjectsCard", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.plate_top", "shell.brass_hi", 1, 3, 9, 7, 9, 7));
        rightCol.AddChild(card);

        var col = new VBoxContainer { ClipContents = true };
        col.AddThemeConstantOverride("separation", 5);
        card.AddChild(col);

        var top = new HBoxContainer { ClipContents = true };
        top.AddThemeConstantOverride("separation", 6);
        col.AddChild(top);
        if (ctx.Icon("icon_industry") is { } gpIco)
        {
            top.AddChild(new TextureRect
            {
                Texture = gpIco, CustomMinimumSize = new Vector2(18, 18),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Modulate = Ui.Colour("shell.brass_hi")
            });
        }

        var hdr = new Label { Text = Loc.Get("shell.ws.eco.gp_cta_title"), ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hdr.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        hdr.AddThemeFontSizeOverride("font_size", 13);
        hdr.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        top.AddChild(hdr);

        var statusLbl = new Label { Text = Loc.Get("shell.ws.eco.gp_cta_none"), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        statusLbl.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        statusLbl.AddThemeFontSizeOverride("font_size", 11);
        statusLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        col.AddChild(statusLbl);

        var openBtn = new Button
        {
            Name = "ShellEconomyOpenGrandProjectsCta", Text = Loc.Get("shell.ws.eco.gp_cta_open"),
            CustomMinimumSize = new Vector2(0, 30), ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(Loc.Get("gp.ui.subtitle"))
        };
        openBtn.AddThemeStyleboxOverride("normal", ctx.NineSlice("button_brass_cta_9s", 8, 10, 4, 10, 4, "shell.plate_top", "shell.brass_hi", 3));
        openBtn.AddThemeStyleboxOverride("hover", ctx.NineSlice("button_brass_cta_9s", 8, 10, 4, 10, 4, "shell.plate_hover", "shell.paper", 3));
        openBtn.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        openBtn.AddThemeFontSizeOverride("font_size", 12);
        openBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        openBtn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); ctx.OpenGrandProjects?.Invoke(); };
        col.AddChild(openBtn);
        return statusLbl;
    }

    private static VBoxContainer CreateRibbonCard(ShellContext ctx, GridContainer ribbon, string caption, out Label valueLbl, out Label subLbl)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        card.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 3, 9, 5, 9, 5));
        ribbon.AddChild(card);

        var col = new VBoxContainer { ClipContents = true };
        col.AddThemeConstantOverride("separation", 2);
        card.AddChild(col);

        var cap = new Label { Text = caption, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        cap.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        cap.AddThemeFontSizeOverride("font_size", 10);
        cap.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        col.AddChild(cap);

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 6);
        col.AddChild(row);

        valueLbl = new Label { Text = "—" };
        valueLbl.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        valueLbl.AddThemeFontSizeOverride("font_size", 16);
        valueLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(valueLbl);

        subLbl = new Label
        {
            Text = "", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        subLbl.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        subLbl.AddThemeFontSizeOverride("font_size", 10);
        subLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        row.AddChild(subLbl);
        return col;
    }

    public static void PopulateSpendingStack(ShellContext ctx, HBoxContainer bar, List<(string Name, float Amount)> shares, float expTotal)
    {
        foreach (var ch in bar.GetChildren()) { bar.RemoveChild(ch); ch.QueueFree(); }
        if (expTotal <= 0.0001f) return;

        var ordered = shares.OrderByDescending(s => s.Amount).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (nm, amt) = ordered[i];
            var frac = amt / expTotal;
            var pct = (int)MathF.Round(frac * 100f);
            if (pct < 1) continue;

            var accent = Ui.Colour(StackAccentTokens[i % StackAccentTokens.Length]);
            var seg = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = Math.Max(0.03f, frac),
                ClipContents = true, TooltipText = Ui.Tip(Loc.Format("shell.ws.eco.stack_tip_fmt", nm, Fmt.Money(amt), pct))
            };
            var segBox = new StyleBoxFlat
            {
                BgColor = Ui.Colour(i % 2 == 0 ? "shell.plate_top" : "shell.plate_bottom"),
                BorderColor = accent, BorderWidthRight = 1, BorderWidthBottom = 1, BorderWidthTop = 3,
                ContentMarginLeft = 5, ContentMarginRight = 5, ContentMarginTop = 3, ContentMarginBottom = 3
            };
            seg.AddThemeStyleboxOverride("panel", segBox);

            var sv = new VBoxContainer { ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            sv.AddThemeConstantOverride("separation", 0);
            if (frac >= 0.11f)
            {
                var sl1 = new Label { Text = nm, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = Control.MouseFilterEnum.Ignore };
                sl1.AddThemeFontOverride("font", ctx.Font("shell.sans"));
                sl1.AddThemeFontSizeOverride("font_size", 10);
                sl1.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
                sv.AddChild(sl1);
            }
            if (frac >= 0.06f)
            {
                var sl2 = new Label { Text = $"{pct}%", ClipText = true, MouseFilter = Control.MouseFilterEnum.Ignore };
                sl2.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
                sl2.AddThemeFontSizeOverride("font_size", 11);
                sl2.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
                sv.AddChild(sl2);
            }
            seg.AddChild(sv);
            bar.AddChild(seg);
        }
    }

    public static void DrawBalanceMeter(Control meter, Nation n)
    {
        if (n is null) return;
        var w = meter.Size.X;
        var h = meter.Size.Y;
        if (w <= 4f) return;

        meter.DrawRect(new Rect2(0, 1, w, h - 2), Ui.Colour("shell.well_top"));
        var rev = MathF.Max(0.001f, n.WeeklyTaxIncome + n.WeeklyTariffIncome);
        var exp = MathF.Max(0.001f, n.WeeklyExpenses);
        var denom = MathF.Max(rev, exp);
        var fillCol = rev >= exp ? Ui.Colour("shell.state.positive", 0.72f) : Ui.Colour("shell.state.negative", 0.78f);
        meter.DrawRect(new Rect2(0, 1, Math.Clamp(exp / denom, 0f, 1f) * w, h - 2), fillCol);
        meter.DrawRect(new Rect2(Math.Clamp(rev / denom * w - 1f, 0f, w - 2f), 0, 2f, h), Ui.Colour("shell.paper"));
    }

    public static void DrawTreasuryChart(Control chart, ShellContext ctx, Campaign world, Nation n)
    {
        if (world is null || n is null) return;
        var w = chart.Size.X;
        var h = chart.Size.Y;
        if (w <= 80f || h <= 48f) return;

        const float padL = 54f, padR = 72f, padT = 12f, padB = 20f;
        var cw = w - padL - padR;
        var ch = h - padT - padB;
        if (cw <= 24f || ch <= 20f) return;

        var sheet = SovereignBalanceSheet.Read(world, n);
        var limit = -MathF.Max(0.2f, sheet.NewCredit);
        var cur = n.Treasury;
        var bal = n.WeeklyBalance;

        var ptsVal = new float[7];
        for (var i = 0; i < 7; i++) ptsVal[i] = cur - bal * (6 - i) * 4.3f;

        var hi = MathF.Max(MathF.Max(1f, ptsVal.Max() * 1.15f), cur + 0.5f);
        var lo = MathF.Min(limit * 1.15f, ptsVal.Min() - 0.3f);
        float MapY(float v) => padT + (hi - v) / MathF.Max(0.01f, hi - lo) * ch;
        float MapX(int idx) => padL + (idx / 6f) * cw;

        chart.DrawRect(new Rect2(padL, padT, cw, ch), Ui.Colour("shell.well", 0.45f));
        for (var k = 0; k <= 4; k++)
        {
            var gy = padT + (k / 4f) * ch;
            chart.DrawLine(new Vector2(padL, gy), new Vector2(padL + cw, gy), Ui.Colour("shell.paper", 0.08f), 1f);
        }

        var yZero = Math.Clamp(MapY(0f), padT, padT + ch);
        chart.DrawLine(new Vector2(padL, yZero), new Vector2(padL + cw, yZero), Ui.Colour("shell.paper_faint", 0.8f), 1.2f);

        var yLim = Math.Clamp(MapY(limit), padT, padT + ch);
        for (var x = padL; x < padL + cw - 4f; x += 10f)
            chart.DrawLine(new Vector2(x, yLim), new Vector2(MathF.Min(padL + cw, x + 6f), yLim), Ui.Colour("shell.state.negative"), 1.2f);

        var poly = new Vector2[7];
        for (var i = 0; i < 7; i++) poly[i] = new Vector2(MapX(i), Math.Clamp(MapY(ptsVal[i]), padT, padT + ch));

        var fillPoly = new Vector2[9];
        for (var i = 0; i < 7; i++) fillPoly[i] = poly[i];
        fillPoly[7] = new Vector2(poly[6].X, yZero);
        fillPoly[8] = new Vector2(poly[0].X, yZero);
        var trendCol = bal < -0.0005f ? Ui.Colour("shell.state.negative_text") : Ui.Colour("shell.state.positive");
        chart.DrawColoredPolygon(fillPoly, bal < -0.0005f ? Ui.Colour("shell.state.negative", 0.14f) : Ui.Colour("shell.state.positive", 0.14f));
        chart.DrawPolyline(poly, trendCol, 2.2f, antialiased: true);
        chart.DrawCircle(poly[0], 3f, Ui.Colour("shell.paper_muted"));
        chart.DrawCircle(poly[6], 3.8f, Ui.Colour("shell.paper"));

        var tabFont = ctx.Font("shell.sans", tabular: true);
        var faint = Ui.Colour("shell.paper_faint");
        var bright = Ui.Colour("shell.paper");
        chart.DrawString(tabFont, new Vector2(2f, padT + 9f), Fmt.Money(hi), HorizontalAlignment.Right, padL - 6f, 10, faint);
        chart.DrawString(tabFont, new Vector2(2f, Math.Clamp(yZero + 4f, padT + 18f, padT + ch - 2f)), "$0", HorizontalAlignment.Right, padL - 6f, 10, faint);
        chart.DrawString(tabFont, new Vector2(padL + cw + 6f, Math.Clamp(poly[6].Y + 4f, padT + 10f, padT + ch)), Fmt.Money(cur), HorizontalAlignment.Left, padR - 8f, 11, bright);
        if (MathF.Abs(yLim - poly[6].Y) > 13f)
            chart.DrawString(tabFont, new Vector2(padL + cw + 6f, Math.Clamp(yLim + 4f, padT + 10f, padT + ch)), Loc.Get("shell.ws.eco.chart_credit"), HorizontalAlignment.Left, padR - 8f, 10, Ui.Colour("shell.state.negative_text"));

        var xY = padT + ch + 13f;
        chart.DrawString(tabFont, new Vector2(padL, xY), Loc.Get("shell.ws.eco.chart_6m"), HorizontalAlignment.Left, 48f, 10, faint);
        chart.DrawString(tabFont, new Vector2(padL + cw * 0.5f - 20f, xY), Loc.Get("shell.ws.eco.chart_3m"), HorizontalAlignment.Center, 40f, 10, faint);
        chart.DrawString(tabFont, new Vector2(padL + cw - 44f, xY), Loc.Get("shell.ws.eco.chart_now"), HorizontalAlignment.Right, 44f, 10, bright);
    }

    public static PanelContainer BuildMiniGauge(ShellContext ctx, float fraction01, Color fillColor, out ColorRect fillRect, out Control restCtrl)
    {
        var wrap = new PanelContainer { CustomMinimumSize = new Vector2(0, 6), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        wrap.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", "shell.engrave", 1, 2, 1, 1, 1, 1));
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        wrap.AddChild(row);
        var clamped = Math.Clamp(fraction01, 0.01f, 1f);
        fillRect = new ColorRect { Color = fillColor, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = clamped, MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(fillRect);
        restCtrl = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = Math.Max(0.001f, 1f - clamped), MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(restCtrl);
        return wrap;
    }

    public static void UpdateMiniGauge(ColorRect fillRect, Control restCtrl, float fraction01, Color fillColor)
    {
        var clamped = Math.Clamp(fraction01, 0.01f, 1f);
        fillRect.Color = fillColor;
        fillRect.SizeFlagsStretchRatio = clamped;
        restCtrl.SizeFlagsStretchRatio = Math.Max(0.001f, 1f - clamped);
    }

    public static void PopulateSupplyShortages(ShellContext ctx, VBoxContainer host, Nation nation, Action openMarket)
    {
        foreach (var ch in host.GetChildren()) { host.RemoveChild(ch); ch.QueueFree(); }
        if (nation is null) return;

        var shortages = nation.Market.Values
            .Where(q => q.Demand > 0.05f && q.Satisfaction < 0.90f)
            .OrderByDescending(q => (1f - q.Satisfaction) * q.Demand * MathF.Max(0.01f, q.Price))
            .Take(4)
            .ToArray();

        if (shortages.Length == 0)
        {
            var okCard = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
            okCard.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 7, 4, 7, 4));
            var okLbl = new Label { Text = Loc.Get("shell.ws.eco.shortages_none"), ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
            okLbl.AddThemeFontOverride("font", ctx.Font("shell.sans"));
            okLbl.AddThemeFontSizeOverride("font_size", 11);
            okLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.state.positive"));
            okCard.AddChild(okLbl);
            host.AddChild(okCard);
            return;
        }

        foreach (var q in shortages)
        {
            var def = CommodityCatalog.Get(q.Commodity);
            var goodName = Loc.Get(def.Name);
            var satPct = (int)MathF.Round(Math.Clamp(q.Satisfaction, 0f, 1f) * 100f);
            var severe = q.Satisfaction < 0.55f;

            var btn = new Button
            {
                CustomMinimumSize = new Vector2(0, 26), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true,
                TooltipText = Ui.Tip(Loc.Format("shell.ws.eco.shortage_tip_fmt", goodName, satPct)),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None
            };
            btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", severe ? "shell.state.negative" : "shell.state.warning", 1, 2, 6, 3, 6, 3));
            btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.paper", 1, 2, 6, 3, 6, 3));
            btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); openMarket?.Invoke(); };

            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            row.AddThemeConstantOverride("separation", 6);
            row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            row.OffsetLeft = 6; row.OffsetRight = -6;
            btn.AddChild(row);

            var gIco = ctx.Icon($"icon_good_{q.Commodity.ToString().ToLowerInvariant()}") ?? ctx.Icon("icon_trade");
            if (gIco is not null)
                row.AddChild(new TextureRect { Texture = gIco, CustomMinimumSize = new Vector2(14, 14), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore });

            var nmLbl = new Label { Text = goodName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = Control.MouseFilterEnum.Ignore };
            nmLbl.AddThemeFontOverride("font", ctx.Font("shell.sans"));
            nmLbl.AddThemeFontSizeOverride("font_size", 11);
            nmLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
            row.AddChild(nmLbl);

            var satLbl = new Label { Text = Loc.Format("shell.ws.eco.shortage_sat_fmt", satPct), VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            satLbl.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
            satLbl.AddThemeFontSizeOverride("font_size", 11);
            satLbl.AddThemeColorOverride("font_color", Ui.Colour(severe ? "shell.state.negative_text" : "shell.state.warning_text"));
            row.AddChild(satLbl);
            host.AddChild(btn);
        }
    }

    public static EconomyTaxEfficiencyNodes BuildTaxEfficiencyBanner(ShellContext ctx, VBoxContainer parent)
    {
        var nodes = new EconomyTaxEfficiencyNodes();
        var root = new PanelContainer
        {
            Name = "ShellEconomyTaxEfficiencyWarning",
            Visible = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        root.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", "shell.state.warning_text", 1, 2, 8, 6, 8, 6));
        parent.AddChild(root);

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        col.AddThemeConstantOverride("separation", 4);
        root.AddChild(col);

        var top = new HBoxContainer { ClipContents = true };
        top.AddThemeConstantOverride("separation", 6);
        col.AddChild(top);

        if (ctx.Icon("icon_shell_warning") is { } warnIco)
        {
            nodes.Icon = new TextureRect
            {
                Texture = warnIco,
                CustomMinimumSize = new Vector2(14, 14),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                Modulate = Ui.Colour("shell.state.warning_text")
            };
            top.AddChild(nodes.Icon);
        }

        nodes.TitleLabel = new Label
        {
            Text = Loc.Get("Tax efficiency"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        nodes.TitleLabel.AddThemeFontOverride("font", ctx.Font("shell.serif_semibold"));
        nodes.TitleLabel.AddThemeFontSizeOverride("font_size", 12);
        nodes.TitleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.state.warning_text"));
        top.AddChild(nodes.TitleLabel);

        nodes.PercentLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        nodes.PercentLabel.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
        nodes.PercentLabel.AddThemeFontSizeOverride("font_size", 12);
        nodes.PercentLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.state.warning_text"));
        top.AddChild(nodes.PercentLabel);

        col.AddChild(BuildMiniGauge(ctx, 1f, Ui.Colour("shell.state.warning"), out nodes.BarFill, out nodes.BarRest));

        nodes.BodyLabel = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        nodes.BodyLabel.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        nodes.BodyLabel.AddThemeFontSizeOverride("font_size", 11);
        nodes.BodyLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        col.AddChild(nodes.BodyLabel);

        nodes.Root = root;
        return nodes;
    }

    public static void RefreshTaxEfficiencyBanner(
        ShellContext ctx, EconomyTaxEfficiencyNodes nodes, Nation n, float weeklyOut, bool atWar)
    {
        if (nodes?.Root is null || n is null) return;
        var compliance = BudgetCore.ComplianceAt(n, weeklyOut, atWar);
        var visible = compliance < 0.999f;
        nodes.Root.Visible = visible;
        if (!visible) return;

        var severe = compliance < 0.70f;
        var textTok = severe ? "shell.state.negative_text" : "shell.state.warning_text";
        var barTok = severe ? "shell.state.negative" : "shell.state.warning";
        var toneCol = Ui.Colour(textTok);

        nodes.Root.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well_bottom", textTok, 1, 2, 8, 6, 8, 6));
        if (nodes.Icon is not null) nodes.Icon.Modulate = toneCol;
        nodes.TitleLabel.Text = Loc.Get("Tax efficiency");
        nodes.TitleLabel.AddThemeColorOverride("font_color", toneCol);
        nodes.PercentLabel.Text = $"{compliance:P0}";
        nodes.PercentLabel.AddThemeColorOverride("font_color", toneCol);
        UpdateMiniGauge(nodes.BarFill, nodes.BarRest, compliance, Ui.Colour(barTok));

        var msg = Loc.Format(
            "Every levy is charged against the same week's output: {0:P0} of what is asked for is actually collected. A further tax on top of these raises less than the first one did.",
            compliance);
        nodes.BodyLabel.Text = msg;
        nodes.Root.TooltipText = Ui.Tip(msg);
    }
}
