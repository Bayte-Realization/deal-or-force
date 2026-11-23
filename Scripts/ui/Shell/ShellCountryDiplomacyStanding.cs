using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Flag-Only World Standing relation grids and strictly bounded Organizations/Blocs pills for
/// <see cref="ShellCountryDiplomacyView"/>. Every country-relation group renders compact, framed,
/// clickable 38x26 flag tiles (zero text overflow, full details in hover tooltips).
/// </summary>
internal static class ShellCountryDiplomacyStanding
{
    private const int FlagTileW = 38;
    private const int FlagTileH = 26;

    internal readonly record struct FlagTileSpec(Nation Target, string Detail, string CornerTag, string AccentTok);

    public static void PopulateStandoffs(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation nation, Action<Nation> openCountry)
    {
        var standoffs = Escalation.Hottest(world, 25f)
            .Where(r => r.A == nation.Id || r.B == nation.Id)
            .Take(10)
            .Select(r => (
                Other: world.NationById(r.Other(nation.Id)),
                Stage: (TensionStage)r.TensionStage,
                Tension: (int)MathF.Round(r.Tension)))
            .Where(x => x.Other is not null)
            .Select(x => new FlagTileSpec(
                x.Other,
                Detail: $"{Loc.Get(StageLabel(x.Stage))} ({x.Tension}%)",
                CornerTag: x.Stage >= TensionStage.Clashes ? "!" : "",
                AccentTok: x.Stage >= TensionStage.Clashes ? "shell.state.negative" : "shell.state.warning"))
            .ToList();
        if (standoffs.Count > 0)
            AddFlagTileSection(ctx, root, "ShellDipGroup_Standoffs", "icon_shell_warning",
                Loc.Get("shell.dip.standoffs"), "shell.state.warning_text", standoffs, world, nation, openCountry);
    }

    public static void PopulateWorldStanding(
        ShellContext ctx, VBoxContainer root, Campaign world, Nation nation, Action<Nation> openCountry)
    {
        var anyGroup = false;

        // International Organizations / Blocs (strictly bounded compact pills with full tooltip)
        if (world.FeatureEnabled("organizations"))
        {
            var orgs = InternationalOrganizations.Memberships(world, nation.Id).ToList();
            if (orgs.Count > 0)
            {
                anyGroup = true;
                var card = ShellCountryDiplomacyView.MakeSectionCard(ctx, root, "ShellDipGroup_Blocs", "icon_diplomacy",
                    Loc.Get("shell.dip.blocs"), orgs.Count.ToString(CultureInfo.InvariantCulture), "shell.state.info");
                var flow = ShellCountryDiplomacyView.MakeChipFlow(card);
                foreach (var org in orgs)
                {
                    var orgName = InternationalOrganizations.DisplayName(world, org);
                    var charterName = Loc.Get(InternationalOrganizations.Charter(org.CharterId)?.Name ?? "");
                    var shortLabel = CompactOrgLabel(orgName);
                    var tip = $"{orgName}\n{Loc.Format("{0} · {1} members", charterName, org.Members.Count)}";
                    flow.AddChild(MakeBoundedOrgPill(ctx, shortLabel, "shell.state.info", tip,
                        () => ctx.OpenOrganizations?.Invoke()));
                }
            }
        }

        // Overlord & Subjects (Flag-Only tiles)
        if (nation.IsSubject && world.NationById(nation.OverlordId) is { } overlord)
        {
            anyGroup = true;
            var sType = Loc.Get(SubjectCatalog.Of(nation.SubjectType)?.Name ?? nation.SubjectType);
            AddFlagTileSection(ctx, root, "ShellDipGroup_Overlord", "icon_subject",
                Loc.Get("shell.dip.overlord"), "shell.state.warning_text",
                new List<FlagTileSpec> { new(overlord, sType, "", "shell.state.warning") }, world, nation, openCountry);
        }

        var subjects = SubjectSystem.SubjectsOf(world, nation);
        if (subjects.Count > 0)
        {
            anyGroup = true;
            var items = subjects.Select(s => new FlagTileSpec(
                s, Loc.Get(SubjectCatalog.Of(s.SubjectType)?.Name ?? s.SubjectType), "", "shell.brass_hi")).ToList();
            AddFlagTileSection(ctx, root, "ShellDipGroup_Subjects", "icon_subject",
                Loc.Get("shell.dip.subjects"), "shell.brass_hi", items, world, nation, openCountry);
        }

        // Allies (Flag-Only tiles)
        var allies = AllianceSystem.AlliesOf(world, nation);
        if (allies.Count > 0)
        {
            anyGroup = true;
            var items = allies.Select(a => new FlagTileSpec(a, Loc.Get("Allied"), "", "shell.state.positive")).ToList();
            AddFlagTileSection(ctx, root, "ShellDipGroup_Allies", "icon_alliance",
                Loc.Get("shell.dip.allies"), "shell.state.positive", items, world, nation, openCountry);
        }

        // Single read-only pass over living nations for pairwise diplomatic ties
        var rivals = new List<FlagTileSpec>();
        var truces = new List<FlagTileSpec>();
        var trades = new List<FlagTileSpec>();
        var naps = new List<FlagTileSpec>();
        var guarGiven = new List<FlagTileSpec>();
        var guarRecv = new List<FlagTileSpec>();
        var accessGiven = new List<FlagTileSpec>();
        var accessRecv = new List<FlagTileSpec>();
        var embImposed = new List<FlagTileSpec>();
        var embUnder = new List<FlagTileSpec>();

        for (var i = 0; i < world.Nations.Count; i++)
        {
            var other = world.Nations[i];
            if (other.Id == nation.Id || other.Population <= 0) continue;
            var tie = CampaignDiplomacy.Peek(world, nation.Id, other.Id);
            if (tie is null) continue;

            if (tie.Stance == DiplomaticStance.Rival)
                rivals.Add(new(other, Loc.Get("Rival"), "", "shell.state.negative"));
            if (tie.TruceUntil > world.ElapsedDays)
            {
                var days = tie.TruceUntil - world.ElapsedDays;
                truces.Add(new(other, Loc.Format("shell.dip.truce_days_fmt", days), $"{days}d", "shell.paper_muted"));
            }
            if (tie.TradeAgreement)
                trades.Add(new(other, Loc.Get("shell.dip.badge_trade"), "", "shell.state.info"));
            if (tie.NonAggressionPact)
                naps.Add(new(other, Loc.Get("shell.dip.badge_nap"), "", "shell.paper_muted"));
            if (tie.GuaranteedBy(nation.Id))
                guarGiven.Add(new(other, Loc.Get("shell.dip.guarantees_given"), "", "shell.state.positive"));
            if (tie.GuaranteedBy(other.Id))
                guarRecv.Add(new(other, Loc.Get("shell.dip.guarantees_received"), "", "shell.state.info"));
            if (tie.GrantsMilitaryAccess(nation.Id))
                accessGiven.Add(new(other, Loc.Get("shell.dip.access_granted"), "", "shell.paper_muted"));
            if (tie.GrantsMilitaryAccess(other.Id))
                accessRecv.Add(new(other, Loc.Get("shell.dip.access_received"), "", "shell.paper_muted"));
            if (tie.EmbargoBy(nation.Id))
                embImposed.Add(new(other, Loc.Get("shell.dip.embargoes_imposed"), "", "shell.state.warning"));
            if (tie.EmbargoBy(other.Id))
                embUnder.Add(new(other, Loc.Get("shell.dip.embargoes_under"), "", "shell.state.negative"));
        }

        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_Rivals", "icon_unrest", Loc.Get("shell.dip.rivals"), "shell.state.negative_text", rivals, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_Truces", "icon_treaty", Loc.Get("shell.dip.truces"), "shell.paper_muted", truces, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_Trade", "icon_trade", Loc.Get("shell.dip.trade_pacts"), "shell.state.info", trades, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_Naps", "icon_shell_treaty", Loc.Get("shell.dip.naps"), "shell.paper_muted", naps, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_GuarGiven", "icon_alliance", Loc.Get("shell.dip.guarantees_given"), "shell.state.positive", guarGiven, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_GuarRecv", "icon_alliance", Loc.Get("shell.dip.guarantees_received"), "shell.state.info", guarRecv, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_AccessGiven", "icon_military", Loc.Get("shell.dip.access_granted"), "shell.paper_muted", accessGiven, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_AccessRecv", "icon_military", Loc.Get("shell.dip.access_received"), "shell.paper_muted", accessRecv, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_EmbImposed", "icon_shell_warning", Loc.Get("shell.dip.embargoes_imposed"), "shell.state.warning_text", embImposed, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_EmbUnder", "icon_shell_warning", Loc.Get("shell.dip.embargoes_under"), "shell.state.negative_text", embUnder, world, nation, openCountry);

        // Proxy War Sponsorships (Flag-Only tiles with L1/L2/L3 micro-badge)
        var backers = new List<FlagTileSpec>();
        var clients = new List<FlagTileSpec>();
        for (var i = 0; i < world.Sponsorships.Count; i++)
        {
            var s = world.Sponsorships[i];
            if (s.Ended) continue;
            if (s.ClientId == nation.Id && world.NationById(s.SponsorId) is { } sp)
                backers.Add(new(sp, Loc.Format("level {0}", s.Level), $"L{s.Level}", "shell.state.warning"));
            else if (s.SponsorId == nation.Id && world.NationById(s.ClientId) is { } cl)
                clients.Add(new(cl, Loc.Format("level {0}", s.Level), $"L{s.Level}", "shell.brass_hi"));
        }
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_Backers", "icon_equipment", Loc.Get("shell.dip.proxy_backers"), "shell.state.warning_text", backers, world, nation, openCountry);
        anyGroup |= TryAddFlagSection(ctx, root, "ShellDipGroup_Clients", "icon_equipment", Loc.Get("shell.dip.proxy_clients"), "shell.brass_hi", clients, world, nation, openCountry);

        // Territorial claims summary badge
        if (world.ProvinceMap is not null && nation.Movement is null)
        {
            var claimCount = 0;
            for (var i = 0; i < world.States.Count; i++)
            {
                var st = world.States[i];
                if (st.NationMapIndex != nation.MapIndex && st.Population > 0 && !st.Unclaimed &&
                    TerritorialClaims.TitleOf(world, nation, st) != TerritorialTitle.None)
                    claimCount++;
            }
            if (claimCount > 0)
            {
                anyGroup = true;
                ShellCountryDiplomacyView.AddNoticeStrip(ctx, root, "icon_region",
                    Loc.Format("shell.dip.claims_summary_fmt", claimCount), "shell.state.warning_text");
            }
        }

        if (!anyGroup)
            ShellCountryDiplomacyView.AddNoticeStrip(ctx, root, "icon_diplomacy",
                Loc.Get("shell.dip.no_foreign_ties"), "shell.paper_faint");
    }

    private static bool TryAddFlagSection(
        ShellContext ctx, VBoxContainer root, string cardName, string iconName, string title, string accentTok,
        List<FlagTileSpec> items, Campaign world, Nation subject, Action<Nation> openCountry)
    {
        if (items.Count == 0) return false;
        AddFlagTileSection(ctx, root, cardName, iconName, title, accentTok, items, world, subject, openCountry);
        return true;
    }

    private static void AddFlagTileSection(
        ShellContext ctx, VBoxContainer root, string cardName, string iconName, string title, string accentTok,
        List<FlagTileSpec> items, Campaign world, Nation subject, Action<Nation> openCountry)
    {
        var card = ShellCountryDiplomacyView.MakeSectionCard(ctx, root, cardName, iconName, title,
            items.Count.ToString(CultureInfo.InvariantCulture), accentTok);
        var flow = ShellCountryDiplomacyView.MakeChipFlow(card);
        var cap = Math.Min(items.Count, 24);
        for (var i = 0; i < cap; i++)
            flow.AddChild(BuildFlagTile(ctx, items[i], world, subject, openCountry));
        if (items.Count > cap)
            flow.AddChild(ShellCountryDiplomacyView.MakeStatusPill(ctx, $"+{items.Count - cap}", "shell.paper_faint"));
    }

    /// <summary>
    /// Compact, framed, clickable FLAG-ONLY tile (38x26px) with zero overflowing text,
    /// subtle semantic corner badge (e.g. L2 / 90d / !), and rich hover tooltip.
    /// </summary>
    private static Button BuildFlagTile(
        ShellContext ctx, FlagTileSpec spec, Campaign world, Nation subject, Action<Nation> openCountry)
    {
        var target = spec.Target;
        var op = (int)MathF.Round(CampaignDiplomacy.ReadOrNeutral(world, subject.Id, target.Id).Opinion);
        var tip = $"{target.DisplayName} ({target.Tag})\n" +
                  (string.IsNullOrEmpty(spec.Detail) ? "" : $"{spec.Detail} · ") +
                  $"{Loc.Get("Opinion")}: {op:+0;-0;0}\n{Loc.Get("shell.dip.flag_tile_tip")}";

        var tile = new Button
        {
            Name = "ShellDipFlag_" + target.Tag,
            CustomMinimumSize = new Vector2(FlagTileW, FlagTileH),
            ClipContents = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(tip)
        };
        tile.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", "shell.engrave", 1, 2, 0, 0, 0, 0));
        tile.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", spec.AccentTok, 1, 2, 0, 0, 0, 0));
        tile.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle());
        tile.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); openCountry?.Invoke(target); };

        var flagRect = new TextureRect
        {
            Name = "FlagImg",
            Material = FlagClothPresentation.Material,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Texture = ctx.FlagTexture(target.Tag),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        flagRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        flagRect.OffsetLeft = 4f;
        flagRect.OffsetRight = -4f;
        flagRect.OffsetTop = 3f;
        flagRect.OffsetBottom = -3f;
        tile.AddChild(flagRect);

        if (!string.IsNullOrEmpty(spec.CornerTag))
        {
            var badge = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            badge.AnchorLeft = 1f; badge.AnchorRight = 1f;
            badge.AnchorTop = 1f; badge.AnchorBottom = 1f;
            badge.OffsetLeft = -22f; badge.OffsetRight = -1f;
            badge.OffsetTop = -12f; badge.OffsetBottom = -1f;
            badge.AddThemeStyleboxOverride("panel", ctx.BorderedBox("shell.well", spec.AccentTok, 1, 1, 2, 0, 2, 0));
            var bLbl = ShellCountryDiplomacyView.MakeLabel(ctx, spec.CornerTag, "shell.sans", 8, spec.AccentTok, tabular: true);
            bLbl.HorizontalAlignment = HorizontalAlignment.Center;
            badge.AddChild(bLbl);
            tile.AddChild(badge);
        }
        return tile;
    }

    private static Button MakeBoundedOrgPill(ShellContext ctx, string text, string colorTok, string tip, Action onClick)
    {
        var estW = Math.Clamp(text.Length * 6.8f + 16f, 44f, 152f);
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(estW, 24),
            ClipText = true,
            ClipContents = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = string.IsNullOrEmpty(tip) ? "" : Ui.Tip(tip)
        };
        btn.AddThemeStyleboxOverride("normal", ctx.BorderedBox("shell.well_bottom", colorTok, 1, 2, 7, 2, 7, 2));
        btn.AddThemeStyleboxOverride("hover", ctx.BorderedBox("shell.plate_hover", "shell.paper", 1, 2, 7, 2, 7, 2));
        btn.AddThemeStyleboxOverride("pressed", ctx.PressedWellStyle(padX: 7, padY: 2));
        btn.AddThemeFontOverride("font", ctx.Font("shell.sans"));
        btn.AddThemeFontSizeOverride("font_size", 11);
        btn.AddThemeColorOverride("font_color", Ui.Colour(colorTok));
        btn.Pressed += () => { ctx.PlayCue?.Invoke("ui_select"); onClick?.Invoke(); };
        return btn;
    }

    private static string CompactOrgLabel(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length <= 18) return name ?? "";
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3)
        {
            var sb = new System.Text.StringBuilder(words.Length);
            for (var i = 0; i < words.Length; i++)
            {
                var w = words[i];
                if (w.Length <= 2 && i > 0) continue;
                sb.Append(char.ToUpperInvariant(w[0]));
            }
            if (sb.Length >= 2 && sb.Length <= 6) return sb.ToString();
        }
        return name[..17] + "…";
    }

    private static string StageLabel(TensionStage stage) => stage switch
    {
        TensionStage.Friction => "Friction",
        TensionStage.Crisis => "Crisis",
        TensionStage.Clashes => "Clashes",
        TensionStage.War => "At war",
        _ => "Calm"
    };
}
