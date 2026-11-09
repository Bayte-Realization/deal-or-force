using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

// The Relations page of the country card: every tie a country has, the countries in it shown by
// their flags (Paradox, Age of History), each flag one click from that country's own card.
public partial class DealOrForceGame
{
    /// <summary>A caption and the flags of the countries in it. Nothing is added when there are none.</summary>
    private void CardFlags(string caption, IEnumerable<Nation> members, Func<Nation, string> note = null, string token = "text.body_soft")
    {
        var list = members.Where(m => m is not null).Distinct().ToList();
        if (list.Count == 0 || _countryCardRows is null) return;
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 26) };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new Control { CustomMinimumSize = new Vector2(6, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        var label = Title(caption, 12, Ui.Colour(token));
        label.CustomMinimumSize = new Vector2(118, 0);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(label);
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 4);
        flow.AddThemeConstantOverride("v_separation", 4);
        row.AddChild(flow);
        var limit = (int)Ui.Metric("country_card.flags_shown", 18f);
        foreach (var member in list.Take(limit)) flow.AddChild(CardFlag(member, note?.Invoke(member)));
        if (list.Count > limit)
        {
            var more = RowLabel(Simulation.Loc.Format("+{0}", (list.Count - limit).ToString("N0")), 12, Ui.Colour("text.muted"));
            more.TooltipText = Ui.Tip(string.Join(", ", list.Skip(limit).Select(m => m.DisplayName)));
            more.MouseFilter = Control.MouseFilterEnum.Stop;
            flow.AddChild(more);
        }
        _countryCardRows.AddChild(row);
    }

    /// <summary>One flag, with the country's name and the tie in its tooltip; a click opens that country's card.</summary>
    private Button CardFlag(Nation member, string note)
    {
        var flag = new Button
        {
            Icon = FlagTexture(member.Tag),
            ExpandIcon = true,
            CustomMinimumSize = new Vector2(30, 20),
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(member.DisplayName + (string.IsNullOrEmpty(note) ? "" : "\n" + note) + "\n" + Tr("Click to open its card."))
        };
        StyleBoxFlat Style(bool hover)
        {
            var style = new StyleBoxFlat
            {
                BgColor = Ui.Colour(hover ? "war.card_hover" : "war.card"),
                BorderColor = Ui.Colour(hover ? "text.accent" : "war.card_border"),
                ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 2, ContentMarginBottom = 2
            };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(2);
            return style;
        }
        flag.AddThemeStyleboxOverride("normal", Style(false));
        flag.AddThemeStyleboxOverride("hover", Style(true));
        flag.AddThemeStyleboxOverride("pressed", Style(true));
        flag.AddThemeStyleboxOverride("focus", Style(false));
        var id = member.Id;
        flag.Pressed += () =>
        {
            var target = _campaign?.NationById(id);
            if (target is null) return;
            _selectedNation = target;
            ShowMapSelectionContent();
        };
        return flag;
    }

    /// <summary>
    /// Every tie between this country and the rest of the world, as flags: who it answers to and who
    /// answers to it, its allies, each war and who it is fighting, promises given and received, pacts,
    /// trade agreements, military access both ways, embargoes, arms, rivals and truces.
    /// </summary>
    private void AddCountryRelationFlags(Nation nation)
    {
        var c = _campaign;
        if (SubjectSystem.OverlordOf(c, nation) is { } master)
            CardFlags("Answers to", new[] { master }, _ => Tr(SubjectCatalog.Of(nation.SubjectType)?.Name ?? nation.SubjectType));
        CardFlags("Subjects", SubjectSystem.SubjectsOf(c, nation), s => Tr(SubjectCatalog.Of(s.SubjectType)?.Name ?? s.SubjectType));
        CardFlags("Allied to", AllianceSystem.AlliesOf(c, nation), null, "state.positive");
        if (c.FeatureEnabled("organizations"))
            CardFlags("Defence pact with", InternationalOrganizations.DefencePartners(c, nation.Id).Select(c.NationById), null, "state.positive");
        foreach (var war in c.Wars.Where(w => !w.Ended && w.Involves(nation.Id)))
        {
            var name = WarNames.Of(c, war);
            CardFlags(Simulation.Loc.Format("At war · {0}", name), war.EnemiesOf(nation.Id).Select(c.NationById),
                e => SeparatePeace.IsLeader(war, e.Id) ? Tr("leads the other side") : Tr("fights on the other side"), "state.negative");
            CardFlags(Simulation.Loc.Format("Beside them · {0}", name),
                war.Side(war.IsAttacking(nation.Id)).Where(id => id != nation.Id).Select(c.NationById), null, "state.positive");
        }

        var ties = c.Relations.Values.Where(r => r.A == nation.Id || r.B == nation.Id).ToList();
        IEnumerable<Nation> Others(Func<DiplomaticRelation, int, bool> test) =>
            ties.Where(r => test(r, r.Other(nation.Id))).Select(r => c.NationById(r.Other(nation.Id)))
                .Where(n => n is { Population: > 0 }).OrderByDescending(n => n.Population);
        CardFlags("Guaranteed by", Others((r, o) => r.GuaranteedBy(o)), null, "state.positive");
        CardFlags("Guarantees", Others((r, o) => r.GuaranteedBy(nation.Id)));
        CardFlags("Non-aggression pacts", Others((r, o) => r.NonAggressionPact));
        CardFlags("Trade agreements", Others((r, o) => r.TradeAgreement));
        CardFlags("Lets troops through", Others((r, o) => r.GrantsMilitaryAccess(nation.Id)),
            _ => Tr("may march across this country's ground"));
        CardFlags("Troops may cross", Others((r, o) => r.GrantsMilitaryAccess(o)),
            _ => Tr("this country may march across their ground"));
        CardFlags("Embargoes", Others((r, o) => r.EmbargoBy(nation.Id)), null, "state.warning");
        CardFlags("Embargoed by", Others((r, o) => r.EmbargoBy(o)), null, "state.warning");
        CardFlags("Rivals", Others((r, o) => r.Stance == DiplomaticStance.Rival), null, "state.negative");
        CardFlags("Truce with", Others((r, o) => r.Stance == DiplomaticStance.Truce || r.TruceUntil > c.ElapsedDays),
            o => Tr("no war between them until the truce runs out"));

        var sponsorships = c.Sponsorships.Where(s => !s.Ended).ToList();
        CardFlags("Armed by", sponsorships.Where(s => s.ClientId == nation.Id).Select(s => c.NationById(s.SponsorId)),
            n => Simulation.Loc.Format("level {0}", sponsorships.First(s => s.SponsorId == n.Id && s.ClientId == nation.Id).Level));
        CardFlags("Arms", sponsorships.Where(s => s.SponsorId == nation.Id).Select(s => c.NationById(s.ClientId)),
            n => Simulation.Loc.Format("level {0}", sponsorships.First(s => s.ClientId == n.Id && s.SponsorId == nation.Id).Level));
    }
}
