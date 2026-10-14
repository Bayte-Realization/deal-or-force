using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private int _selectedOrganization = -1;
    private string _organizationConfirmation = "";
    private Button OrganizationHelp()
    {
        var help = HandbookButton("organizations");
        help.Text = Tr("How organizations work");
        help.CustomMinimumSize = new(0, 32);
        return help;
    }
    private void ListOrganizations()
    {
        _conflictsList.AddChild(Caption("INTERNATIONAL ORGANIZATIONS"));
        var listed = InternationalOrganizations.Active(_campaign)
            .OrderByDescending(o => o.Members.Contains(_campaign.Player.Id))
            .ThenByDescending(o => o.Members.Count).ThenBy(o => o.Name).ToList();
        // Something is always open on the right: ours, or the largest.
        if (listed.Count > 0 && listed.All(o => o.Id != _selectedOrganization)) _selectedOrganization = listed[0].Id;
        foreach (var o in listed)
        {
            var id = o.Id;
            var ours = o.Members.Contains(_campaign.Player.Id);
            var kind = InternationalOrganizations.Charter(o.CharterId);
            var row = ActionButton(Loc.Format("{0} · {1} members", InternationalOrganizations.DisplayName(_campaign, o), o.Members.Count) +
                (ours ? "  ·  " + Tr("we are a member") : ""), () => {
                _selectedOrganization = id; _organizationConfirmation = ""; RefreshConflictsScreen(true);
            }, 0, 38, iconPath: OrganizationIcon(o));
            row.Name = "Organization_" + id; row.ClipText = true;
            row.TooltipText = Ui.Tip(InternationalOrganizations.DisplayName(_campaign, o) + "\n" + Tr(kind?.Name ?? "") + "\n" + Tr(kind?.Description ?? ""));
            StyleButton(row, id == _selectedOrganization); _conflictsList.AddChild(row);
            // Who is in it, at a glance: the members' flags under the name.
            var flags = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
            flags.AddThemeConstantOverride("h_separation", 3);
            flags.AddThemeConstantOverride("v_separation", 3);
            var shown = 0;
            foreach (var member in o.Members.Select(m => _campaign.NationById(m)).Where(n => n is not null)
                         .OrderByDescending(n => n.Gdp).Take(18))
            {
                var flag = FlagBadge(member.Tag, 14, member.DisplayName);
                flag.MouseFilter = MouseFilterEnum.Pass;
                flags.AddChild(flag);
                shown++;
            }
            if (o.Members.Count > shown)
                flags.AddChild(Title(Loc.Format("+{0} more", o.Members.Count - shown), 11, Ui.Colour("text.muted")));
            _conflictsList.AddChild(flags);
        }
        _conflictsList.AddChild(new HSeparator()); _conflictsList.AddChild(Caption("FOUND YOUR OWN ORGANIZATION"));
        var name = Placeholder(new LineEdit { Name = "OrganizationName", MaxLength = 80 }, "Organization name...");
        _conflictsList.AddChild(name);
        var charters = InternationalOrganizations.Charters.OrderBy(d => d.Id).ToArray();
        var charter = new OptionButton { Name = "OrganizationCharter", CustomMinimumSize = new(0, 36) };
        foreach (var d in charters) charter.AddItem(Tr(d.Name));
        _conflictsList.AddChild(charter);
        var explanation = WrappedUltimatumText("", 14); _conflictsList.AddChild(explanation);
        void Explain() => explanation.Text = charters.Length == 0 ? Tr("No organization charters are available.") :
            Tr(charters[charter.Selected].Description) + "\n" + Loc.Format("Founding deposit into common fund: {0}.",
                Fmt.Money(Math.Max(0, _campaign.Defines.OrganizationFoundingDeposit)));
        charter.ItemSelected += _ => Explain(); Explain();
        var create = ActionButton("Found organization", () => {
            if (charters.Length == 0) return;
            var cmd = new OrganizationCommand { IssuerId = _campaign.Player.Id, Action = "create", Name = name.Text, CharterId = charters[charter.Selected].Id };
            OrganizationAction(cmd, "found:" + name.Text + ":" + cmd.CharterId);
        }, 0, 36, iconPath: "res://game/gfx/interface/icons/icon_decisions.png");
        create.Name = "CreateOrganization"; _conflictsList.AddChild(create);
    }

    /// <summary>A defence bloc carries a shield, an economic union the industry mark, a council the handshake.</summary>
    private static string OrganizationIcon(InternationalOrganization o) =>
        InternationalOrganizations.Charter(o.CharterId) switch
        {
            { Defence: true } => "res://game/gfx/interface/icons/icon_alliance.png",
            { Id: "development" } => "res://game/gfx/interface/icons/icon_industry.png",
            _ => "res://game/gfx/interface/icons/icon_diplomacy.png"
        };

    private void OrganizationAction(OrganizationCommand command, string confirmation = "")
    {
        var refusal = InternationalOrganizations.Refusal(_campaign, command);
        if (refusal.Length > 0) { _organizationConfirmation = ""; _conflictsFeedback.Text = refusal; return; }
        if (confirmation.Length > 0 && _organizationConfirmation != confirmation)
        { _organizationConfirmation = confirmation; _conflictsFeedback.Text = Tr("Review the deposit or motion, then press the same action again to confirm. Nothing has changed yet."); return; }
        _organizationConfirmation = ""; _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        if (command.Action == "create" && command.Changed) _selectedOrganization = _campaign.Organizations.Last().Id;
        RefreshConflictsScreen(true);
        _conflictsFeedback.Text = Tr(command.Changed ? "Organization decision recorded." : "The host will validate this organization decision.");
    }

    private void ShowOrganizationPage()
    {
        var o = InternationalOrganizations.Active(_campaign).FirstOrDefault(o => o.Id == _selectedOrganization);
        if (o is null)
        {
            _conflictsPage.AddChild(Title("INTERNATIONAL ORGANIZATIONS", 21, Ui.Colour("text.accent")));
            _conflictsPage.AddChild(WrappedUltimatumText("Choose an organization on the left, or found one. Each has a common fund its members pay into, votes on who joins and who is helped, and - for a defence bloc - an obligation to answer an attack on any member.", 16));
            _conflictsPage.AddChild(OrganizationHelp()); return;
        }
        var charter = InternationalOrganizations.Charter(o.CharterId);
        _conflictsPage.AddChild(Title(InternationalOrganizations.DisplayName(_campaign, o), 24, Ui.Colour("text.accent")));
        _conflictsPage.AddChild(WrappedUltimatumText(Tr(charter.Name) + "\n" + Tr(charter.Description), 15));
        _conflictsPage.AddChild(OrganizationHelp());
        _conflictsPage.AddChild(WrappedUltimatumText(Loc.Format("Common fund: {0} · institutional cohesion: {1}%", Fmt.Money(o.Budget), (o.Cohesion * 100).ToString("0")), 16));
        _conflictsPage.AddChild(Gauge(o.Cohesion * 100, Ui.Colour("text.accent"), 200));
        _conflictsPage.AddChild(WrappedUltimatumText("Members pay dues every week. Arrears erode cohesion, and below 25% the collective defence no longer holds.", 13));
        _conflictsPage.AddChild(Caption(Loc.Format("MEMBERS ({0})", o.Members.Count)));
        _conflictsPage.AddChild(DiplomacyChips(o.Members.Select(m => _campaign.NationById(m)).Where(n => n is not null)
            .OrderByDescending(n => n.Gdp), Tr("Nobody.")));
        var members = new VBoxContainer { Name = "OrganizationMembers", Visible = false };
        _conflictsPage.AddChild(ActionButton(Loc.Format("Weekly dues and arrears ({0})", o.Members.Count), () => members.Visible = !members.Visible, 0, 34));
        _conflictsPage.AddChild(members);
        foreach (var id in o.Members)
        {
            var n = _campaign.NationById(id); if (n is null) continue;
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 9); row.AddChild(FlagBadge(n.Tag, 20));
            row.AddChild(Title(Loc.Format("{0} · {1}/week · {2} unpaid weeks", n.DisplayName,
                Fmt.Money(InternationalOrganizations.Dues(_campaign, o, n)), o.UnpaidWeeks.GetValueOrDefault(id)), 13, Ui.Colour("text.body")));
            members.AddChild(row);
        }
        var player = _campaign.Player;
        if (o.Invitations.Contains(player.Id))
        {
            _conflictsPage.AddChild(WrappedUltimatumText("The assembly has invited your country. Accession requires your consent and future dues.", 15));
            foreach (var action in new[] { "join", "decline" })
            {
                var captured = action;
                _conflictsPage.AddChild(ActionButton(action == "join" ? "Accept membership" : "Decline invitation", () =>
                    OrganizationAction(new OrganizationCommand { IssuerId = player.Id, OrganizationId = o.Id, Action = captured }, captured + o.Id), 0, 34));
            }
        }
        if (o.LastOutcome.Length > 0) _conflictsPage.AddChild(WrappedUltimatumText(Loc.Format("Last vote: {0} of {1} yes · {2}", o.LastYes, o.LastVoters,
            Tr(o.LastOutcome.EndsWith("_passed", StringComparison.Ordinal) ? "Motion passed." : "Motion did not pass.")), 13));
        if (!o.Members.Contains(player.Id)) return;
        foreach (var war in _campaign.Wars.Where(w => !w.Ended && !w.Involves(player.Id) && o.Members.Any(w.IsDefending)))
        {
            var command = new OrganizationCommand { IssuerId = player.Id, OrganizationId = o.Id, Action = "defend", WarId = war.Id };
            var refusal = InternationalOrganizations.Refusal(_campaign, command);
            var defend = ActionButton(Loc.Format("Answer defensive call: {0}", WarNames.Of(_campaign, war)), () => OrganizationAction(command, "defend:" + war.Id), 0, 36,
                iconPath: Icon("war"));
            defend.Disabled = refusal.Length > 0; defend.TooltipText = Ui.Tip(refusal); _conflictsPage.AddChild(defend);
        }
        _conflictsPage.AddChild(new HSeparator()); _conflictsPage.AddChild(Caption("THE ASSEMBLY"));
        if (o.Motion is { } m)
        {
            var target = _campaign.NationById(m.TargetId);
            _conflictsPage.AddChild(WrappedUltimatumText(Loc.Format("{0} · {1}\nVoting closes in {2} days. {3} of {4} have voted yes.",
                Tr(m.Kind == "admission" ? "Admission vote" : "Common-fund grant"), target?.DisplayName ?? "?",
                Math.Max(0, m.EndsDay - _campaign.ElapsedDays), m.Votes.Count(v => v.Value), o.Members.Count), 15));
            if (m.Kind == "grant") _conflictsPage.AddChild(Title(Fmt.Money(m.Amount), 16, Ui.Colour("text.accent")));
            foreach (var yes in new[] { true, false })
            {
                var captured = yes;
                _conflictsPage.AddChild(ActionButton(yes ? "Vote yes" : "Vote no", () => OrganizationAction(new OrganizationCommand {
                    IssuerId = player.Id, OrganizationId = o.Id, Action = "vote", MotionId = m.Id, Yes = captured }), 0, 34));
            }
        }
        else
        {
            _conflictsPage.AddChild(WrappedUltimatumText(charter.Unanimity ? "Every member must vote yes; abstention is not consent." : "A majority of all members must vote yes; abstention is not consent.", 14));
            var countries = _campaign.Nations.Where(n => InternationalOrganizations.Eligible(_campaign, n)).OrderBy(n => n.DisplayName).ToArray();
            var targets = new OptionButton { Name = "OrganizationMotionTarget", CustomMinimumSize = new(0, 36) };
            foreach (var n in countries) targets.AddItem(n.DisplayName); _conflictsPage.AddChild(targets);
            _conflictsPage.AddChild(Caption("Proposed grant · billions of dollars"));
            var amount = new SpinBox { Name = "OrganizationGrantAmount", MinValue = 0, MaxValue = o.Budget, Step = .01, CustomMinimumSize = new(0, 36),
                TooltipText = Ui.Tip(Tr("Grant in billions of dollars. It can only be paid from the existing common fund after the vote.")) };
            _conflictsPage.AddChild(amount);
            foreach (var kind in new[] { "admission", "grant" })
            {
                var captured = kind;
                _conflictsPage.AddChild(ActionButton(kind == "admission" ? "Propose admission" : "Propose funded grant", () => {
                    if (countries.Length == 0) return;
                    OrganizationAction(new OrganizationCommand { IssuerId = player.Id, OrganizationId = o.Id, Action = "propose", Kind = captured,
                        TargetId = countries[targets.Selected].Id, Amount = (float)amount.Value }, captured + o.Id + countries[targets.Selected].Id + ":" + amount.Value);
                }, 0, 34, iconPath: "res://game/gfx/interface/icons/icon_decisions.png"));
            }
        }
        _conflictsPage.AddChild(ActionButton("Leave organization", () => OrganizationAction(new OrganizationCommand {
            IssuerId = player.Id, OrganizationId = o.Id, Action = "leave" }, "leave" + o.Id), 0, 34));
    }
}
