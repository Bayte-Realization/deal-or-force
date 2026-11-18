using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Pure canvas-drawn proportional spending stack, revenue-vs-spending meter, and 6-month treasury
/// trajectory chart for ShellEconomyWorkspaceView.
/// </summary>
internal static class ShellEconomyCharts
{
    public static void PopulateSpendingStack(ShellContext ctx, HBoxContainer bar, List<(string Name, float Amount)> shares, float expTotal)
    {
        foreach (var ch in bar.GetChildren()) ch.QueueFree();
        if (expTotal <= 0.0001f) return;

        for (var i = 0; i < shares.Count; i++)
        {
            var (nm, amt) = shares[i];
            var pct = (int)MathF.Round((amt / expTotal) * 100f);
            if (pct < 2) continue;
            var seg = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = Math.Max(0.05f, amt / expTotal)
            };
            var segBox = new StyleBoxFlat
            {
                BgColor = Ui.Colour(i % 2 == 0 ? "shell.plate_top" : "shell.plate_bottom"),
                BorderColor = Ui.Colour("shell.engrave"),
                BorderWidthRight = 1,
                ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 3, ContentMarginBottom = 3
            };
            seg.AddThemeStyleboxOverride("panel", segBox);

            var sv = new VBoxContainer();
            sv.AddThemeConstantOverride("separation", 0);
            var sl1 = new Label { Text = nm, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
            sl1.AddThemeFontSizeOverride("font_size", 10);
            sl1.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            var sl2 = new Label { Text = $"{pct}%" };
            sl2.AddThemeFontOverride("font", ctx.Font("shell.sans", tabular: true));
            sl2.AddThemeFontSizeOverride("font_size", 12);
            sl2.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
            sv.AddChild(sl1);
            sv.AddChild(sl2);
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
        meter.DrawRect(new Rect2(0, 1, Math.Clamp(exp / denom, 0f, 1f) * w, h - 2), Ui.Colour("shell.paper_muted"));
        meter.DrawRect(new Rect2(Math.Clamp(rev / denom, 0f, 1f) * w - 1f, 0, 2f, h), Ui.Colour("shell.paper"));
    }

    public static void DrawTreasuryChart(Control chart, Campaign world, Nation n)
    {
        if (world is null || n is null) return;
        var w = chart.Size.X;
        var h = chart.Size.Y;
        if (w <= 60f || h <= 40f) return;

        const float padL = 36f, padR = 92f, padT = 10f, padB = 18f;
        var cw = w - padL - padR;
        var ch = h - padT - padB;

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

        for (var k = 0; k <= 4; k++)
        {
            var gy = padT + (k / 4f) * ch;
            chart.DrawLine(new Vector2(padL, gy), new Vector2(padL + cw, gy), Ui.Colour("shell.paper", 0.08f), 1f);
        }

        var yZero = Math.Clamp(MapY(0f), padT, padT + ch);
        chart.DrawLine(new Vector2(padL, yZero), new Vector2(padL + cw, yZero), Ui.Colour("shell.paper_faint"), 1.3f);

        var yLim = Math.Clamp(MapY(limit), padT, padT + ch);
        for (var x = padL; x < padL + cw - 4f; x += 10f)
            chart.DrawLine(new Vector2(x, yLim), new Vector2(MathF.Min(padL + cw, x + 6f), yLim), Ui.Colour("shell.state.negative"), 1.2f);

        var poly = new Vector2[7];
        for (var i = 0; i < 7; i++)
            poly[i] = new Vector2(MapX(i), Math.Clamp(MapY(ptsVal[i]), padT, padT + ch));
        chart.DrawPolyline(poly, Ui.Colour("shell.paper_muted"), 2.0f, antialiased: true);
        chart.DrawCircle(poly[0], 3f, Ui.Colour("shell.paper_muted"));
        chart.DrawCircle(poly[6], 3.5f, Ui.Colour("shell.paper"));
    }
}
