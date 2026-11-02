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
    private string _organizationDraftName = "", _organizationDraftCharter = "";
    private bool _organizationMembersExpanded;
    private bool _organizationIndexReady;
    private int _organizationIndexKey;

    // Membership rows and the unsent form do not depend on dues or the clock.
    // Rebuild only when their displayed identity/order or selection actually changes.
    private void RefreshOrganizationIndex()
    {
        var hash = new HashCode();
        hash.Add(Loc.Language); hash.Add(_campaign.Player.Id); hash.Add(_selectedOrganization);
        hash.Add(_campaign.Defines.OrganizationFoundingDeposit);
        var politicalCost = InternationalOrganizations.FoundingPoliticalCost(_campaign);
        hash.Add(politicalCost); hash.Add(_observerMode);
        hash.Add(_campaign.Player.PoliticalPoints >= politicalCost);
        hash.Add(_campaign.Player.Treasury >= Math.Max(0, _campaign.Defines.OrganizationFoundingDeposit));
        foreach (var o in InternationalOrganizations.Active(_campaign).OrderBy(o => o.Id))
        {
            hash.Add(o.Id); hash.Add(InternationalOrganizations.DisplayName(_campaign, o)); hash.Add(o.CharterId);
            foreach (var id in o.Members.OrderByDescending(id => _campaign.NationById(id)?.Gdp ?? 0))
            {
                var n = _campaign.NationById(id); hash.Add(id); hash.Add(n?.Tag); hash.Add(n?.DisplayName);
            }
        }
        foreach (var charter in InternationalOrganizations.Charters.OrderBy(c => c.Id))
        { hash.Add(charter.Id); hash.Add(charter.Name); hash.Add(charter.Description); }
        var key = hash.ToHashCode();
        if (_organizationIndexReady && _organizationIndexKey == key) return;
        _organizationIndexKey = key; _organizationIndexReady = true;
        ClearRuntimeChildren(_conflictsList); ListOrganizations();
    }
    private Button OrganizationHelp()
    {
        var help = HandbookButton("organizations");
        help.Text = Tr("How organizations work");
        help.CustomMinimumSize = new(0, 32);
        return help;
    }
    private void ListOrganizations()
    {
        var newOrganization = ActionButton("New organization…", () =>
        {
            if (_conflictsList.FindChild("OrganizationName", true, false) is LineEdit input)
            { _conflictsListScroll.EnsureControlVisible(input); input.GrabFocus(); }
        }, 0, 34, iconPath: Icon("decisions"));
        newOrganization.Name = "BeginOrganizationFounding";
        newOrganization.Disabled = _observerMode;
        _conflictsList.AddChild(newOrganization);
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
        var name = Placeholder(new LineEdit { Name = "OrganizationName", MaxLength = 80, Text = _organizationDraftName }, "Organization name...");
        name.TextChanged += value => _organizationDraftName = value;
        _conflictsList.AddChild(name);
        var charters = InternationalOrganizations.Charters.OrderBy(d => d.Id).ToArray();
        var charter = new OptionButton { Name = "OrganizationCharter", CustomMinimumSize = new(0, 36) };
        foreach (var d in charters) charter.AddItem(Tr(d.Name));
        var draftIndex = Array.FindIndex(charters, c => c.Id == _organizationDraftCharter);
        if (draftIndex >= 0) charter.Select(draftIndex);
        _conflictsList.AddChild(charter);
        var explanation = WrappedUltimatumText("", 14); _conflictsList.AddChild(explanation);
        var create = ActionButton("Found organization", () => {
            if (charters.Length == 0) return;
            var cmd = new OrganizationCommand { IssuerId = _campaign.Player.Id, Action = "create", Name = name.Text, CharterId = charters[charter.Selected].Id };
            OrganizationAction(cmd, "found:" + name.Text + ":" + cmd.CharterId);
        }, 0, 36, iconPath: "res://game/gfx/interface/icons/icon_decisions.png");
        create.Name = "CreateOrganization"; _conflictsList.AddChild(create);
        void Explain()
        {
            explanation.Text = charters.Length == 0 ? Tr("No organization charters are available.") :
                Tr(charters[charter.Selected].Description) + "\n" + Loc.Format(
                    "Founding cost: {0} into the common fund and {1:0.#} political capital.",
                    Fmt.Money(Math.Max(0, _campaign.Defines.OrganizationFoundingDeposit)),
                    InternationalOrganizations.FoundingPoliticalCost(_campaign));
            var refusal = charters.Length == 0 ? Tr("No organization charters are available.") :
                InternationalOrganizations.Refusal(_campaign, new OrganizationCommand { IssuerId = _campaign.Player.Id,
                    Action = "create", Name = name.Text, CharterId = charters[charter.Selected].Id });
            if (_observerMode) refusal = Tr("Observer mode cannot issue diplomatic orders.");
            create.Disabled = refusal.Length > 0;
            create.TooltipText = Ui.Tip(refusal.Length > 0 ? refusal : Tr("Press again to confirm."));
        }
        name.TextChanged += _ => Explain();
        charter.ItemSelected += _ => { _organizationDraftCharter = charters[charter.Selected].Id; Explain(); };
        Explain();
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
        if (_observerMode) { _conflictsFeedback.Text = Tr("Observer mode cannot issue diplomatic orders."); return; }
        var refusal = InternationalOrganizations.Refusal(_campaign, command);
        if (refusal.Length > 0) { _organizationConfirmation = ""; _conflictsFeedback.Text = refusal; return; }
        if (confirmation.Length > 0 && _organizationConfirmation != confirmation)
        { _organizationConfirmation = confirmation; _conflictsFeedback.Text = Tr("Press again to confirm."); return; }
        _organizationConfirmation = ""; _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        if (command.Action == "create" && command.Changed)
        { _selectedOrganization = _campaign.Organizations.Last().Id; _organizationDraftName = ""; }
        RefreshConflictsScreen(true);
        _conflictsFeedback.Text = Tr(command.Changed ? "Organization decision recorded." : "Sent to the host.");
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
        if (InternationalOrganizations.Leader(_campaign, o) is { } leader)
        {
            var challenger = InternationalOrganizations.Challenger(_campaign, o);
            var leadLine = challenger is not null
                ? Loc.Format("Leading power: {0} · contested by {1}", leader.DisplayName, challenger.DisplayName)
                : Loc.Format("Leading power: {0}", leader.DisplayName);
            _conflictsPage.AddChild(WrappedUltimatumText(leadLine, 14));
        }
        var activeDoc = InternationalOrganizations.Doctrine(o.DoctrineId);
        _conflictsPage.AddChild(WrappedUltimatumText(activeDoc is not null
            ? Loc.Format("Bloc doctrine: {0} — {1}", Tr(activeDoc.Name), Tr(activeDoc.Description))
            : Tr("Bloc doctrine: none adopted yet."), 14));
        _conflictsPage.AddChild(Caption(Loc.Format("MEMBERS ({0})", o.Members.Count)));
        _conflictsPage.AddChild(DiplomacyChips(o.Members.Select(m => _campaign.NationById(m)).Where(n => n is not null)
            .OrderByDescending(n => n.Gdp), Tr("Nobody.")));
        // Suspended seats (OrganizationDiscipline): out of the vote and every benefit until given back.
        if (o.Suspended.Count > 0)
        {
            _conflictsPage.AddChild(Caption(Loc.Format("SUSPENDED ({0})", o.Suspended.Count)));
            _conflictsPage.AddChild(DiplomacyChips(o.Suspended.Keys.Select(m => _campaign.NationById(m)).Where(n => n is not null)
                .OrderByDescending(n => n.Gdp), Tr("Nobody.")));
        }
        var members = new VBoxContainer { Name = "OrganizationMembers", Visible = _organizationMembersExpanded };
        _conflictsPage.AddChild(ActionButton(Loc.Format("Weekly dues and arrears ({0})", o.Members.Count), () =>
        {
            _organizationMembersExpanded = !_organizationMembersExpanded;
            RefreshConflictsScreen(true);
        }, 0, 34));
        _conflictsPage.AddChild(members);
        foreach (var id in _organizationMembersExpanded ? o.Members : Enumerable.Empty<int>())
        {
            var n = _campaign.NationById(id); if (n is null) continue;
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 9); row.AddChild(FlagBadge(n.Tag, 20));
            row.AddChild(Title(Loc.Format("{0} · {1}/week · {2} unpaid weeks", n.DisplayName,
                Fmt.Money(InternationalOrganizations.Dues(_campaign, o, n)), o.UnpaidWeeks.GetValueOrDefault(id)), 13, Ui.Colour("text.body")));
            members.AddChild(row);
            var engagement = OrganizationDynamics.Belonging(_campaign,o,n);
            var rift = OrganizationDynamics.LeadershipFriction(_campaign,o,n,InternationalOrganizations.Leader(_campaign,o));
            members.AddChild(WrappedUltimatumText(Loc.Format("Belonging: {0:0}% · leadership dispute: −{1:0} points",
                engagement*100,rift*100),12));
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
        if (o.Suspended.ContainsKey(player.Id))
        {
            _conflictsPage.AddChild(WrappedUltimatumText("Your country's seat is suspended: no vote, no customs union, no common defence, no grants. The assembly may give it back once the cause is gone, or vote to expel it while the cause stands.", 14));
            _conflictsPage.AddChild(ActionButton("Give up the seat", () => OrganizationAction(new OrganizationCommand {
                IssuerId = player.Id, OrganizationId = o.Id, Action = "leave" }, "leave" + o.Id), 0, 34));
            return;
        }
        if (!o.Members.Contains(player.Id))
        {
            if (!o.Invitations.Contains(player.Id))
            {
                var pending = o.Motion is { Kind: "admission" } application &&
                    application.ProposerId == player.Id && application.TargetId == player.Id;
                _conflictsPage.AddChild(WrappedUltimatumText(pending
                    ? Loc.Format("Your membership application is awaiting the assembly. Voting closes in {0} days.", Math.Max(0, o.Motion.EndsDay - _campaign.ElapsedDays))
                    : Tr("Apply for membership to start an assembly vote. Approval gives an invitation, not automatic membership; you can then accept or decline."), 14));
                var command = new OrganizationCommand { IssuerId = player.Id, OrganizationId = o.Id,
                    Action = pending ? "withdraw_application" : "request_membership" };
                var refusal = _observerMode ? Tr("Observer mode cannot issue diplomatic orders.") : InternationalOrganizations.Refusal(_campaign, command);
                var apply = ActionButton(pending ? "Withdraw application" : "Apply for membership", () =>
                    OrganizationAction(command, command.Action + ":" + o.Id), 0, 36, iconPath: Icon("diplomacy"));
                apply.Name = pending ? "WithdrawOrganizationApplication" : "RequestOrganizationMembership";
                apply.Disabled = refusal.Length > 0; apply.TooltipText = Ui.Tip(refusal);
                _conflictsPage.AddChild(apply);
                if (refusal.Length > 0) _conflictsPage.AddChild(WrappedUltimatumText(refusal, 13));
            }
            return;
        }
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
            var subjectText = m.Kind == "doctrine"
                ? Tr(InternationalOrganizations.Doctrine(m.Subject)?.Name ?? m.Subject)
                : target?.DisplayName ?? "?";
            _conflictsPage.AddChild(WrappedUltimatumText(Loc.Format("{0} · {1}\nVoting closes in {2} days. {3} of {4} have voted yes.",
                Tr(m.Kind == "admission" ? "Admission vote" : m.Kind == "sanction" ? "Sanctions vote" :
                   m.Kind == "lift_sanction" ? "Vote to lift sanctions" : m.Kind == "doctrine" ? "Doctrine vote" :
                   m.Kind == "suspend" ? "Suspension vote" : m.Kind == "expel" ? "Expulsion vote" :
                   m.Kind == "readmit" ? "Vote to restore a seat" : "Common-fund grant"), subjectText,
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
            _conflictsPage.AddChild(WrappedUltimatumText("Suspension and expulsion: the accused has no vote; suspension and restoring a seat need a majority of the rest, expulsion two thirds (a unanimity charter needs all of them).", 13));
            var countries = _campaign.Nations.Where(n => InternationalOrganizations.Eligible(_campaign, n)).OrderBy(n => n.DisplayName).ToArray();
            var targets = new OptionButton { Name = "OrganizationMotionTarget", CustomMinimumSize = new(0, 36) };
            foreach (var n in countries) targets.AddItem(n.DisplayName); _conflictsPage.AddChild(targets);
            _conflictsPage.AddChild(Caption("Proposed grant · billions of dollars"));
            var amount = new SpinBox { Name = "OrganizationGrantAmount", MinValue = 0, MaxValue = o.Budget, Step = .01, CustomMinimumSize = new(0, 36),
                TooltipText = Ui.Tip(Tr("Grant in billions of dollars. It can only be paid from the existing common fund after the vote.")) };
            _conflictsPage.AddChild(amount);
            foreach (var kind in new[] { "admission", "grant", "sanction", "lift_sanction", "suspend", "expel", "readmit" })
            {
                var captured = kind;
                _conflictsPage.AddChild(ActionButton(kind == "admission" ? "Propose admission" :
                    kind == "grant" ? "Propose funded grant" : kind == "sanction" ? "Propose sanctions" :
                    kind == "suspend" ? "Propose suspension" : kind == "expel" ? "Propose expulsion" :
                    kind == "readmit" ? "Propose restoring the seat" : "Propose lifting sanctions", () => {
                    if (countries.Length == 0) return;
                    OrganizationAction(new OrganizationCommand { IssuerId = player.Id, OrganizationId = o.Id, Action = "propose", Kind = captured,
                        TargetId = countries[targets.Selected].Id, Amount = (float)amount.Value }, captured + o.Id + countries[targets.Selected].Id + ":" + amount.Value);
                }, 0, 34, iconPath: "res://game/gfx/interface/icons/icon_decisions.png"));
            }
            var doctrines = InternationalOrganizations.Doctrines.OrderBy(d => d.Id).ToArray();
            if (doctrines.Length > 0)
            {
                _conflictsPage.AddChild(Caption("Bloc doctrine"));
                var docSelect = new OptionButton { Name = "OrganizationDoctrineSelect", CustomMinimumSize = new(0, 36) };
                foreach (var d in doctrines) docSelect.AddItem(Tr(d.Name));
                _conflictsPage.AddChild(docSelect);
                _conflictsPage.AddChild(ActionButton("Propose doctrine", () => {
                    var chosen = doctrines[docSelect.Selected].Id;
                    OrganizationAction(new OrganizationCommand { IssuerId = player.Id, OrganizationId = o.Id, Action = "propose", Kind = "doctrine", Subject = chosen },
                        "doctrine:" + o.Id + ":" + chosen);
                }, 0, 34, iconPath: "res://game/gfx/interface/icons/icon_decisions.png"));
            }
        }
        // A membership crisis: who is bargaining and until when, and the two levers - demanding
        // better terms for ourselves, or buying the bargaining member back (OrganizationCrises).
        if (OrganizationCrises.Active(_campaign))
        {
            if (o.CrisisMemberId >= 0 && _campaign.NationById(o.CrisisMemberId) is { } bargaining)
                _conflictsPage.AddChild(Caption(Loc.Format("Membership crisis: {0} demands better terms; the vote on leaving is in {1} days.",
                    bargaining.DisplayName, Math.Max(0, o.CrisisSinceDay + _campaign.Defines.OrganizationCrisisDays - _campaign.ElapsedDays))));
            var demand = new OrganizationCommand { IssuerId = player.Id, OrganizationId = o.Id, Action = "demand_terms" };
            var demandWhy = InternationalOrganizations.Refusal(_campaign, demand);
            var demandButton = ActionButton("Demand better terms", () => OrganizationAction(demand, "demand" + o.Id), 0, 34,
                iconPath: "res://game/gfx/interface/icons/icon_decisions.png");
            demandButton.Disabled = demandWhy.Length > 0;
            demandButton.TooltipText = Ui.Tip(demandWhy.Length > 0 ? demandWhy : Tr(
                "Open a membership crisis: for half a year the others may buy your country back with a concession paid to your treasury. If nobody does, nothing forces you out - leaving stays your choice."));
            _conflictsPage.AddChild(demandButton);
            if (o.CrisisMemberId >= 0 && o.CrisisMemberId != player.Id)
            {
                var concede = new OrganizationCommand { IssuerId = player.Id, OrganizationId = o.Id, Action = "concede" };
                var concedeWhy = InternationalOrganizations.Refusal(_campaign, concede);
                var cost = OrganizationCrises.ConcessionCost(_campaign, _campaign.NationById(o.CrisisMemberId));
                var concedeButton = ActionButton(Loc.Format("Make a concession ({0})", Fmt.Money(cost)),
                    () => OrganizationAction(concede, "concede" + o.Id + ":" + o.CrisisMemberId), 0, 34,
                    iconPath: "res://game/gfx/interface/icons/icon_decisions.png");
                concedeButton.Disabled = concedeWhy.Length > 0;
                concedeButton.TooltipText = Ui.Tip(concedeWhy.Length > 0 ? concedeWhy : Tr(
                    "Pay the bargaining member to stay: its sense of belonging rises for years and it thinks better of your country. The crisis ends."));
                _conflictsPage.AddChild(concedeButton);
            }
        }
        _conflictsPage.AddChild(ActionButton("Leave organization", () => OrganizationAction(new OrganizationCommand {
            IssuerId = player.Id, OrganizationId = o.Id, Action = "leave" }, "leave" + o.Id), 0, 34));
    }
}
