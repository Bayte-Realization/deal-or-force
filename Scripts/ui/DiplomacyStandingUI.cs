using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Where a country stands in the world, on its diplomacy page: everything it is in, at a
/// glance and by flag.
///
/// The page used to answer one question - what is between it and us - and a player who
/// opened Kosovo or Transnistria learned their opinion of us and the balance of power, and
/// nothing of the wars they were fighting, the bloc they were in or who was paying for
/// their army. Those are the facts a foreign ministry reads first. Each kind of tie is a row
/// with a caption on the left and the countries in it on the right; a row with nothing in
/// it is not shown, except the wars, where "at peace" is itself the news.
/// </summary>
public partial class DealOrForceGame
{
    private void AddWorldStanding(Nation nation, Func<string, string, Color?, VBoxContainer> field,
        Func<string, int, Color, Label> text, Func<float, float, WarBar> bar)
    {
        var c = _campaign;
        var wars = c.Wars.Where(w => !w.Ended && w.Involves(nation.Id)).ToList();
        var box = field("IN THE WORLD", wars.Count == 0 ? Tr("at peace")
            : Simulation.Loc.Format("{0} war(s)", wars.Count), wars.Count == 0 ? Ui.Colour("text.muted") : Ui.Colour("state.negative"));

        HBoxContainer Row(string caption)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var label = RowLabel(caption, 11, Ui.Colour("text.caption"), upper: true);
            label.CustomMinimumSize = new Vector2(150, 0);
            label.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            row.AddChild(label);
            box.AddChild(row);
            return row;
        }
        void Chips(string caption, IEnumerable<Nation> members, Func<Nation, string> suffix = null)
        {
            var list = members.Where(n => n is not null).Distinct().ToList();
            if (list.Count == 0) return;
            var chips = DiplomacyChips(list, "", suffix);
            chips.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            Row(caption).AddChild(chips);
        }
        void Pills(string caption, IEnumerable<(string Text, string Tip)> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", 6);
            flow.AddThemeConstantOverride("v_separation", 6);
            foreach (var (words, tip) in list)
            {
                var pill = new PanelContainer { TooltipText = tip.Length > 0 ? Ui.Tip(tip) : "", MouseFilter = MouseFilterEnum.Pass };
                pill.AddThemeStyleboxOverride("panel", WarInsetStyle(8, 3));
                pill.AddChild(text(words, 12, Ui.Colour("text.bright")));
                flow.AddChild(pill);
            }
            Row(caption).AddChild(flow);
        }

        // ---- wars: the name, who is on the other side, and how it is going for them.
        foreach (var war in wars)
        {
            var row = Row(war.CivilConflict ? "Civil war" : "At war");
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddThemeConstantOverride("separation", 4);
            row.AddChild(column);
            var head = new HBoxContainer();
            head.AddThemeConstantOverride("separation", 8);
            column.AddChild(head);
            var name = text(WarNames.Of(c, war), 13, Ui.Colour("text.bright"));
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            head.AddChild(name);
            var attacking = war.IsAttacking(nation.Id);
            var score = attacking ? war.WarScore : -war.WarScore;
            var enemies = war.EnemiesOf(nation.Id).Select(id => c.NationById(id)).Where(n => n is not null).ToList();
            var tug = bar(10, 140);
            tug.Tug(Mathf.Clamp(score / WarLedgerBarScale, -1f, 1f), WarSideColour(nation),
                WarSideColour(enemies.FirstOrDefault() ?? nation));
            head.AddChild(tug);
            var figure = text(score.ToString("+0;-0;0"), 12,
                Mathf.Abs(score) < 0.5f ? Ui.Colour("text.muted") : score > 0f ? Ui.Colour("state.positive") : Ui.Colour("state.negative"));
            figure.CustomMinimumSize = new Vector2(36, 0);
            figure.HorizontalAlignment = HorizontalAlignment.Right;
            head.AddChild(figure);
            var warId = war.Id;
            head.AddChild(ActionButton("Open", () => OpenWarPage(warId), 64, 24));
            var against = new HBoxContainer();
            against.AddThemeConstantOverride("separation", 8);
            against.AddChild(text(Tr("against"), 11, Ui.Colour("text.caption")));
            var chips = DiplomacyChips(enemies, "");
            chips.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            against.AddChild(chips);
            column.AddChild(against);
        }

        // ---- the standoffs that could become the next war.
        var standoffs = Escalation.Hottest(c, 25f).Where(r => r.A == nation.Id || r.B == nation.Id).Take(4).ToList();
        if (standoffs.Count > 0)
        {
            var row = Row("Standoffs");
            var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            flow.AddThemeConstantOverride("h_separation", 6);
            flow.AddThemeConstantOverride("v_separation", 6);
            row.AddChild(flow);
            foreach (var relation in standoffs)
            {
                var other = c.NationById(relation.A == nation.Id ? relation.B : relation.A);
                if (other is null) continue;
                var stage = (TensionStage)relation.TensionStage;
                flow.AddChild(DiplomacyChip(other, "  " + Tr(StageName(stage))));
            }
        }

        // ---- who arms whom.
        var backings = c.Sponsorships.Where(s => !s.Ended).ToList();
        string Level(Sponsorship s) => "  " + Simulation.Loc.Format("level {0}", s.Level);
        var backers = backings.Where(s => s.ClientId == nation.Id).ToList();
        Chips("Armed by", backers.Select(s => c.NationById(s.SponsorId)),
            n => Level(backers.First(s => s.SponsorId == n.Id)));
        var clients = backings.Where(s => s.SponsorId == nation.Id).ToList();
        Chips("Arms", clients.Select(s => c.NationById(s.ClientId)),
            n => Level(clients.First(s => s.ClientId == n.Id)));

        // ---- the blocs and bonds.
        if (c.FeatureEnabled("organizations"))
            Pills("Organizations", InternationalOrganizations.Memberships(c, nation.Id).Select(o =>
                (InternationalOrganizations.DisplayName(c, o),
                 Simulation.Loc.Format("{0} · {1} members", Tr(InternationalOrganizations.Charter(o.CharterId)?.Name ?? ""), o.Members.Count))));
        Chips("Allies", AllianceSystem.AlliesOf(c, nation));
        if (nation.IsSubject && c.NationById(nation.OverlordId) is { } overlord)
            Chips("Answers to", new[] { overlord }, _ => "  " + Tr(SubjectCatalog.Of(nation.SubjectType)?.Name ?? nation.SubjectType));
        Chips("Subjects", SubjectSystem.SubjectsOf(c, nation));
        var others = c.Nations.Where(o => o.Id != nation.Id && o.Population > 0).ToList();
        DiplomaticRelation Tie(Nation other) => CampaignDiplomacy.Peek(c, nation.Id, other.Id);
        Chips("Defended by", others.Where(o => Tie(o) is { } t && t.GuaranteedBy(o.Id)));
        Chips("Defends", others.Where(o => Tie(o) is { } t && t.GuaranteedBy(nation.Id)));
        Chips("Under embargo by", others.Where(o => Tie(o) is { } t && t.EmbargoBy(o.Id)));
        Chips("Embargoes", others.Where(o => Tie(o) is { } t && t.EmbargoBy(nation.Id)));

        // ---- unfulfilled war claims (grudges from past wars)
        Chips("Unfinished claims against", others.Where(o => Tie(o) is { } t && OpinionSystem.Has(t, "unfinished_war_claim")));
        Chips("Unfinished claims on us", others.Where(o => CampaignDiplomacy.Peek(c, o.Id, nation.Id) is { } t && OpinionSystem.Has(t, "unfinished_war_claim")));

        // ---- active war plans (visible to observer or with sufficient intel)
        if (_observerMode || CampaignIntel.PlayerCanInspect(c, nation.Id))
        {
            if (nation.WarPlanTargetId >= 0 && c.NationById(nation.WarPlanTargetId) is { } target)
                Chips("Planning war against", new[] { target }, _ => "  " + Tr("active plan"));
        }

        // ---- land: what it says is its own and others hold, and who says the same of its land.
        if (c.ProvinceMap is not null && nation.Movement is null)
        {
            var claims = c.States.Where(s => s.NationMapIndex != nation.MapIndex && s.Population > 0 && !s.Unclaimed &&
                                             TerritorialClaims.TitleOf(c, nation, s) != TerritorialTitle.None)
                .Select(s => (Region: s, Holder: c.Nations.Find(n => n.MapIndex == s.NationMapIndex)))
                .Where(x => x.Holder is not null && x.Holder.Id != nation.Id && !x.Holder.AnswersTo(nation.Id))
                .OrderByDescending(x => x.Region.Population).ToList();
            Pills("Claims", claims.Take(6).Select(x => (Simulation.Loc.Format("{0} · {1}", x.Region.DisplayName, x.Holder.Tag),
                Simulation.Loc.Format("Held by {0}", x.Holder.DisplayName)))
                .Concat(claims.Count > 6 ? new[] { (Simulation.Loc.Format("+{0} more", claims.Count - 6), "") } : Array.Empty<(string, string)>()));
            var own = c.States.Where(s => s.NationMapIndex == nation.MapIndex && s.Population > 0).ToList();
            Chips("Claimed by", others.Where(o => o.Movement is null && !o.AnswersTo(nation.Id) && !nation.AnswersTo(o.Id) &&
                                                 own.Any(s => TerritorialClaims.TitleOf(c, o, s) != TerritorialTitle.None)));
            // Movements with a following inside its borders.
            var ownIds = own.Select(s => s.MapIndex).ToHashSet();
            Chips("Movements inside", c.Nations.Where(m => m.Movement is { Dissolved: false } movement &&
                movement.Support.Any(pair => pair.Value > 0.05f && ownIds.Contains(pair.Key))));
        }
    }
}
