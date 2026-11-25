using System;
using System.Text;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Read-only compact formatting, trend sampling, and rich tooltip builders for
/// <see cref="ShellCornerView"/>. Extracted to keep the L-corner view concise and crisp.
/// </summary>
internal static class ShellCornerTelemetry
{
    public static (string Fig, string Unit) SplitMoney(float billions)
    {
        var culture = Loc.Culture;
        var abs = MathF.Abs(billions);
        var sign = billions < -0.0001f ? "−" : "";
        if (abs >= 1000f) return ($"{sign}${(abs / 1000f).ToString("0.0", culture)}", Loc.Get("shell.unit_trillion"));
        if (abs >= 1f) return ($"{sign}${abs.ToString("0.00", culture)}", Loc.Get("shell.unit_billion"));
        return ($"{sign}${(abs * 1000f).ToString("0", culture)}", Loc.Get("shell.unit_million"));
    }

    public static (string Fig, string Unit) SplitPeople(long count)
    {
        var culture = Loc.Culture;
        if (count < 0) count = 0;
        if (count >= 1_000_000_000)
            return ((count / 1_000_000_000.0).ToString("0.00", culture), Loc.Get("shell.aaa.unit_billion_people"));
        if (count >= 1_000_000)
            return ((count / 1_000_000.0).ToString("0.0", culture), Loc.Get("shell.unit_million"));
        if (count >= 1_000)
            return ((count / 1_000.0).ToString("0.#", culture), Loc.Get("shell.unit_thousand"));
        return (count.ToString(culture), "");
    }

    public static string FormatGdpPerCapita(float gdpBillions, long population)
    {
        if (gdpBillions <= 0f || population <= 0) return "$0";
        var usd = (gdpBillions * 1_000_000_000.0) / population;
        if (usd >= 1000.0) return Fmt.Money(gdpBillions / population);
        return $"${Math.Round(usd).ToString("N0", Loc.Culture)}";
    }

    [ThreadStatic] private static StringBuilder t_sb;

    private static StringBuilder BorrowSb()
    {
        var sb = t_sb ??= new StringBuilder(256);
        sb.Clear();
        return sb;
    }

    public static int BreakdownHash(Breakdown breakdown)
    {
        if (breakdown is null || !breakdown.HasEntries) return 0;
        var entries = breakdown.Entries;
        var hc = new HashCode();
        hc.Add(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            hc.Add(e.Source, StringComparer.Ordinal);
            hc.Add((int)MathF.Round(e.Value * 1000f));
        }
        return hc.ToHashCode();
    }

    public static string FormatTopDrivers(Breakdown breakdown, bool asPercent)
    {
        if (breakdown is null || !breakdown.HasEntries) return "";
        var entries = breakdown.Entries;
        Span<int> topIdx = stackalloc int[4];
        Span<float> topAbs = stackalloc float[4];
        var count = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var abs = MathF.Abs(entries[i].Value);
            if (abs < 0.0005f) continue;
            var pos = count;
            if (pos < 4) count++;
            else if (abs <= topAbs[3]) continue;
            else pos = 3;

            while (pos > 0 && abs > topAbs[pos - 1])
            {
                topIdx[pos] = topIdx[pos - 1];
                topAbs[pos] = topAbs[pos - 1];
                pos--;
            }
            topIdx[pos] = i;
            topAbs[pos] = abs;
        }
        if (count == 0) return "";

        var sb = BorrowSb();
        sb.Append('\n').Append(Loc.Get("shell.clarity.drivers_header"));
        for (var i = 0; i < count; i++)
        {
            var e = entries[topIdx[i]];
            var valStr = asPercent
                ? $"{(e.Value * 100f):+0.0;-0.0;0.0}%"
                : e.Value.ToString("+0.0;-0.0;0.0", Loc.Culture);
            sb.Append("\n  ").Append(valStr).Append("  ").Append(Loc.Get(e.Source));
        }
        return sb.ToString();
    }

    public static float SampleGdpDeltaPct(
        ref (Campaign World, int NationId, int SampleDay, float SampleGdp, float GrowthPct) sample,
        Campaign world, Nation player)
    {
        if (!ReferenceEquals(sample.World, world) || sample.NationId != player.Id)
        {
            sample = (world, player.Id, world.ElapsedDays, player.Gdp, 0f);
            return 0f;
        }
        var days = world.ElapsedDays - sample.SampleDay;
        if (days >= 7 && sample.SampleGdp > 0.01f)
        {
            var annualized = ((player.Gdp - sample.SampleGdp) / sample.SampleGdp) * (365f / days) * 100f;
            sample = (world, player.Id, world.ElapsedDays, player.Gdp, annualized);
        }
        return sample.GrowthPct;
    }

    public static string BuildPopulationTooltip(Campaign world, Nation player, int regionCount)
    {
        if (player is null) return "";
        var perCapita = FormatGdpPerCapita(player.Gdp, player.Population);
        var sb = BorrowSb();
        sb.Append(Loc.Get("Population")).Append(": ").Append(Fmt.PeopleExact(player.Population));
        sb.Append('\n').Append(Loc.Format("shell.aaa.tip_regions_gdp_pc_fmt", regionCount, perCapita));
        sb.Append('\n').Append(Loc.Format("shell.aaa.tip_pop_vital_fmt",
            Fmt.Percent(player.Literacy), Fmt.Percent(player.StandardOfLiving)));
        sb.Append('\n').Append(Loc.Get("shell.aaa.tip_click_country"));
        return sb.ToString();
    }

    public static string BuildGdpTooltip(Nation player, float growthPct)
    {
        if (player is null) return "";
        var perCapita = FormatGdpPerCapita(player.Gdp, player.Population);
        var sb = BorrowSb();
        sb.Append(Loc.Get("GDP")).Append(": ").Append(Fmt.MoneyExact(player.Gdp));
        sb.Append('\n').Append(Loc.Get("GDP per head")).Append(": ").Append(perCapita);
        sb.Append('\n').Append(Loc.Format("shell.aaa.tip_weekly_output_fmt", Fmt.Money(player.Gdp / 52f)));
        if (MathF.Abs(growthPct) >= 0.05f)
            sb.Append('\n').Append(Loc.Format("shell.aaa.tip_gdp_growth_fmt", $"{growthPct:+0.0;-0.0;0.0}%"));
        return sb.ToString();
    }

    public static string BuildManpowerTooltip(Campaign world, Nation player)
    {
        if (player is null) return "";
        var sb = BorrowSb();
        sb.Append(Loc.Get("Manpower")).Append(": ").Append(Fmt.PeopleExact(player.ManpowerReserve));
        if (player.ReinforcementCapacity > 0)
            sb.Append('\n').Append(Loc.Format("shell.clarity.reinf_rate_fmt", Fmt.People(player.ReinforcementCapacity)));
        var share = player.Population > 0
            ? Math.Clamp(player.ManpowerReserve / (float)player.Population, 0f, 1f)
            : 0f;
        sb.Append('\n').Append(Loc.Format("shell.aaa.tip_manpower_share_fmt", Fmt.Percent(share)));
        if (world is not null)
        {
            var divs = 0f;
            for (var i = 0; i < world.Armies.Count; i++)
                if (world.Armies[i].NationId == player.Id) divs += world.Armies[i].Divisions;
            var training = 0;
            for (var i = 0; i < world.Recruitment.Count; i++)
                if (world.Recruitment[i].NationId == player.Id) training++;
            sb.Append('\n').Append(Loc.Format("shell.aaa.tip_manpower_forces_fmt",
                (int)MathF.Ceiling(divs), training));
        }
        return sb.ToString();
    }

    public static string BuildFlagTooltip(Campaign world, Nation player, int overallRank)
    {
        if (player is null) return "";
        var rankText = overallRank > 0 ? overallRank.ToString(Loc.Culture) : "—";
        var gov = Loc.Get(PoliticsCore.GovernmentOf(player).Name);
        var ideo = Loc.Get(PoliticsCore.IdeologyOf(player).Name);
        var ruler = world is not null ? Rulers.HeadOfState(world, player) : default;
        var sb = BorrowSb();
        sb.Append(Loc.Format("shell.aaa.flag_tip_rank_fmt", player.DisplayName, player.Tag, rankText));
        sb.Append('\n').Append(gov).Append(" · ").Append(ideo);
        if (!string.IsNullOrWhiteSpace(ruler.Name))
            sb.Append('\n').Append(Loc.Get(ruler.Title)).Append(' ').Append(ruler.Name);
        sb.Append('\n').Append(Loc.Format("shell.aaa.flag_tip_stats_fmt",
            Fmt.People(player.Population), Fmt.Money(player.Gdp)));
        sb.Append('\n').Append(Loc.Format("ui.prestige.flag_tip_fmt",
            MathF.Round(player.Prestige).ToString("N0", Loc.Culture)));
        sb.Append('\n').Append(Loc.Get("shell.aaa.tip_flag_open"));
        return sb.ToString();
    }

    public static Vector2[] CloseRing(Vector2[] pts)
    {
        var res = new Vector2[pts.Length + 1];
        Array.Copy(pts, res, pts.Length);
        res[^1] = pts[0];
        return res;
    }

    public static Vector2[] InsetPolygon(Vector2[] pts, float dist)
    {
        var len = pts.Length;
        var res = new Vector2[len];
        for (var i = 0; i < len; i++)
        {
            var prev = pts[(i - 1 + len) % len];
            var cur = pts[i];
            var next = pts[(i + 1) % len];
            var e1 = (cur - prev).Normalized();
            var e2 = (next - cur).Normalized();
            var n1 = new Vector2(-e1.Y, e1.X);
            var n2 = new Vector2(-e2.Y, e2.X);
            var bis = n1 + n2;
            if (bis.LengthSquared() < 1e-4f) res[i] = cur + n1 * dist;
            else
            {
                bis = bis.Normalized();
                res[i] = cur + bis * (dist / Math.Max(0.35f, bis.Dot(n1)));
            }
        }
        return res;
    }

    public static void DrawSeamlessLCorner(Control backdrop, ShellLayoutProfile p)
    {
        if (backdrop is null || p.ViewportWidth <= 0f) return;
        var compact = p.Compact;
        var ox = p.TopPlate.X;
        var oy = p.TopPlate.Y;
        var w = p.TopPlate.Right;
        var h = p.TopPlate.Bottom;
        var rw = p.LeftRail.Right;
        var b = p.LeftRail.Bottom;
        var s = p.CornerNode.Right;
        var n = p.CornerNode.Bottom;
        var ch = compact ? 8f : 12f;
        var nc = compact ? 6f : 8f;

        Vector2[] poly =
        {
            new(ox, oy), new(w, oy), new(w, h - ch), new(w - ch, h), new(s, h),
            new(s, n - nc), new(s - nc, n), new(rw, n), new(rw, b - ch), new(rw - ch, b), new(ox, b)
        };

        var shadowPoly = new Vector2[poly.Length];
        for (var i = 0; i < poly.Length; i++) shadowPoly[i] = poly[i] + new Vector2(1.5f, 4f);
        backdrop.DrawColoredPolygon(shadowPoly, Ui.Colour("shell.border", 0.52f));

        var topCol = Ui.Colour("shell.plate_top");
        var botCol = Ui.Colour("shell.plate_bottom");
        var colors = new Color[poly.Length];
        for (var i = 0; i < poly.Length; i++)
            colors[i] = topCol.Lerp(botCol, Math.Clamp(poly[i].Y / Math.Max(1f, b), 0f, 1f));
        backdrop.DrawPolygon(poly, colors);

        backdrop.DrawPolyline(CloseRing(poly), Ui.Colour("shell.border"), 1.2f, antialiased: true);
        backdrop.DrawPolyline(CloseRing(InsetPolygon(poly, compact ? 1.8f : 2.2f)), Ui.Colour("shell.brass_hi"), compact ? 2.0f : 2.6f, antialiased: true);
        backdrop.DrawPolyline(CloseRing(InsetPolygon(poly, compact ? 3.5f : 4.2f)), Ui.Colour("shell.paper", 0.14f), 1.0f, antialiased: true);
    }
}
