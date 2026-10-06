using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string _policyConfirmation = "";

    private void ShowPolicyOptions()
    {
        ClearPolicyRows(_policyOptions);
        _policyConfirmation = "";
        if (_policyTab == "Course")
        {
            ShowNationalCourse(_campaign.Player, _policyOptions);
            RefreshPolicyPreview(); return;
        }
        var choices = WorkspacePolicies().Where(p => p.Category == _policyCategory).ToArray();
        if (choices.Length == 0)
        {
            PolicyText(_policyOptions, "Choose another section or clear your search.");
            _policySelected = ""; RefreshPolicyPreview(); return;
        }
        var current = PolicyIsAgenda ? PoliticalPolicyCatalog.Find(_campaign.Player.Agenda?.Id, true) :
            PoliticalPolicies.Effective(_campaign.Player, choices[0], _lawTarget, _lawRegion);
        if (!choices.Any(p => p.Id == _policySelected)) _policySelected =
            choices.FirstOrDefault(p => p.Id == current?.Id)?.Id ?? choices[0].Id;
        PolicyHeading(_policyOptions, _policyCategory);
        PolicyText(_policyOptions, PolicyIsAgenda ? "A government promise is a measurable commitment, not an instant change of national character." :
            "Compare alternatives before reforming. Only one law in each category applies to the selected target.");
        foreach (var policy in choices)
        {
            var card = new PanelContainer { Name = "Policy_" + policy.Id };
            card.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10)); _policyOptions.AddChild(card);
            var rows = new VBoxContainer(); rows.AddThemeConstantOverride("separation", 6); card.AddChild(rows);
            var button = ActionButton(policy.Name, () => { _policySelected = policy.Id; ShowPolicyOptions(); }, 0, 38,
                policy.Id == _policySelected, "res://game/gfx/interface/icons/icon_" + policy.Icon + ".png");
            button.Name = "SelectPolicy_" + policy.Id; button.ClipText = true;
            button.TooltipText = Ui.Tip(Tr(policy.Name)); rows.AddChild(button);
            PolicyText(rows, policy.Id == current?.Id ? "Currently in force" : policy.Default ? "Institutional baseline" : "Alternative policy",
                policy.Id == current?.Id ? "state.positive" : "text.caption");
            PolicyText(rows, policy.Description);
        }
        RefreshPolicyPreview();
    }

    private void RefreshPolicyPreview()
    {
        if (_policyPreview is null || _campaign?.Player is not { } n) return;
        ClearPolicyRows(_policyPreview); _policyEnact = null;
        if (_policyTab == "Course")
        {
            PolicyHeading(_policyPreview, "WHY THIS COURSE?");
            var memory = n.NationalCourse;
            PolicyStat(_policyPreview, "Established course", Tr(NationalCourseCatalog.Find(memory.Dominant)?.Name ?? memory.Dominant), "agenda");
            PolicyStat(_policyPreview, "Time under this course", Simulation.Loc.Format("{0} days", memory.AgeDays), "history");
            PolicyStat(_policyPreview, "Institutional inertia", Fmt.Percent(memory.Entrenchment), "government");
            PolicyStat(_policyPreview, "Pressure for change", Fmt.Percent(memory.Shock), "stability");
            if (memory.ShockReason.Length > 0 && memory.Shock > .01f)
                PolicyStat(_policyPreview, "Last significant shock", Tr(memory.ShockReason), "agenda");
            if (memory.Challenger.Length > 0)
            {
                PolicyStat(_policyPreview, "Emerging course", Tr(NationalCourseCatalog.Find(memory.Challenger)?.Name ?? memory.Challenger), "agenda");
                PolicyText(_policyPreview, Simulation.Loc.Format("Sustained lead: {0} days. A temporary lead is not enough to change the national agenda.", memory.ChallengerDays));
            }
            PolicyText(_policyPreview, "You cannot replace this course by clicking a button. Laws, institutions, living conditions and major events gradually shape it.");
            return;
        }
        if (PolicyIsAgenda) ShowPromiseProgress(n);
        var p = PoliticalPolicyCatalog.Find(_policySelected, PolicyIsAgenda);
        if (p is null) { PolicyText(_policyPreview, "No policy selected"); return; }
        PolicyHeading(_policyPreview, "SELECTED POLICY");
        PolicyStat(_policyPreview, p.Name, Tr(p.Category), p.Icon);
        PolicyText(_policyPreview, p.Description);
        PolicyHeading(_policyPreview, "EFFECTS");
        var lines = NationalModifiers.Lines(p.Modifiers).ToList();
        if (p.ForeignTrust != 0) lines.Add(Simulation.Loc.Format("Sustainable alliance trust: {0:P0}", p.ForeignTrust));
        PolicyText(_policyPreview, string.Join("\n", lines.Count > 0 ? lines : new[] { Tr("Neutral institutional baseline") }));
        PolicyHeading(_policyPreview, "REFORM TERMS");
        PolicyStat(_policyPreview, "Reform cost", Fmt.Money(n.Gdp * p.CostShare), "economy");
        PolicyStat(_policyPreview, "Cooling-off period", Simulation.Loc.Format("{0} days", p.CooldownDays), "history");
        if (PolicyScope == "region") PolicyStat(_policyPreview, "Governed region",
            _campaign.StatesOf(n).FirstOrDefault(s => s.MapIndex == _lawRegion)?.DisplayName ?? Tr("No governed regions"), "nation");
        if (PolicyScope == "foreign") PolicyStat(_policyPreview, "Country or movement",
            _campaign.NationByTag(_lawTarget)?.DisplayName ?? "—", "diplomacy");
        if (PolicyIsAgenda)
        {
            PolicyStat(_policyPreview, "Term progress", Simulation.Loc.Format("{0} days", p.DurationDays), "agenda");
            PolicyText(_policyPreview, Tr(p.Goal switch { "living" => "Living standard target", "debt_ratio" => "Debt / GDP ceiling",
                "peace" => "Remain at peace", _ => "Stability target" }) + (p.Goal == "peace" ? "" : ": " + Fmt.Percent(p.GoalValue)));
            PolicyText(_policyPreview, "Sustained achievement is required; reaching the goal only on the final day is not enough.");
        }
        var may = PoliticalPolicies.CanAdopt(_campaign, n, p, _lawTarget, _lawRegion, PolicyIsAgenda, out var refusal);
        var ready = n.PolicyReadyOnDay.GetValueOrDefault(PolicyIsAgenda ? "agenda" : PoliticalPolicies.Slot(p, _lawTarget, _lawRegion));
        PolicyText(_policyPreview, may ? "Ready for consideration" : Tr(refusal), may ? "state.positive" : "state.warning");
        if (ready > _campaign.ElapsedDays) PolicyText(_policyPreview, Simulation.Loc.Format("Next reform in {0} days.", ready - _campaign.ElapsedDays));
        _policyEnact = ActionButton(_policyConfirmation == p.Id ? "Confirm reform" : PolicyIsAgenda ? "Announce promise" : "Enact law",
            EnactWorkspacePolicy, 0, 38, true, "res://game/gfx/interface/icons/icon_" + p.Icon + ".png");
        _policyEnact.Name = "PolicyEnact"; _policyEnact.Disabled = !may;
        _policyEnact.TooltipText = Ui.Tip(may ? Tr("Review the reform, then click again to confirm payment and adoption.") : Tr(refusal));
        _policyPreview.AddChild(_policyEnact);
    }

    private void ShowPromiseProgress(Nation n)
    {
        PolicyHeading(_policyPreview, "GOVERNMENT PROMISE");
        if (n.Agenda is not { } promise) { PolicyText(_policyPreview, "No government promise in progress"); return; }
        PolicyStat(_policyPreview, "Political promise", Tr(PoliticalPolicyCatalog.Find(promise.Id, true)?.Name ?? promise.Id), "agenda");
        PolicyStat(_policyPreview, "Term progress", $"{promise.ElapsedDays} / {promise.DurationDays}", "history");
        PolicyStat(_policyPreview, "Days meeting the goal", promise.SatisfiedDays.ToString(), "agenda");
        PolicyText(_policyPreview, promise.Completed ? promise.Succeeded ? "Promise fulfilled" : "Promise broken" :
            PoliticalPolicies.GoalMet(_campaign, n, promise) ? "Goal currently met" : "Goal not yet met",
            promise.Completed && !promise.Succeeded ? "state.warning" : "state.positive");
    }

    private void EnactWorkspacePolicy()
    {
        var n = _campaign.Player;
        var p = PoliticalPolicyCatalog.Find(_policySelected, PolicyIsAgenda);
        if (!PoliticalPolicies.CanAdopt(_campaign, n, p, _lawTarget, _lawRegion, PolicyIsAgenda, out var refusal))
        { _policyWorkspaceFeedback.Text = Tr(refusal); RefreshPolicyPreview(); return; }
        if (_policyConfirmation != p.Id)
        {
            _policyConfirmation = p.Id;
            _policyWorkspaceFeedback.Text = Simulation.Loc.Format("Confirm {0}: {1} will be paid from the treasury.", Tr(p.Name), Fmt.Money(n.Gdp * p.CostShare));
            RefreshPolicyPreview(); _policyEnact.GrabFocus(); return;
        }
        // Normal authority-checked command path: local UI, multiplayer and mods agree.
        _campaign.Commands.Enqueue(new AdoptPoliticalPolicyCommand { IssuerId = n.Id, PolicyId = p.Id,
            TargetTag = PolicyScope == "foreign" ? _lawTarget : "", Region = PolicyScope == "region" ? _lawRegion : 0, Agenda = PolicyIsAgenda });
        _campaign.Commands.ApplyAll(_campaign);
        _policyWorkspaceFeedback.Text = Simulation.Loc.Format("Adopted: {0}.", Tr(p.Name));
        ShowPolicyOptions(); _policyBuiltDay = -1; RefreshPolicyWorkspace();
    }

    private void PolicyHeading(VBoxContainer rows, string caption) => rows.AddChild(Title(caption, 13, Ui.Colour("text.accent"), true));
    private void PolicyText(VBoxContainer rows, string text, string color = "text.body_soft")
    {
        if (!string.IsNullOrWhiteSpace(text)) rows.AddChild(Title(text, 13, Ui.Colour(color)));
    }

    private void PolicyStat(VBoxContainer rows, string caption, string value, string icon)
    {
        if (icon == "history") icon = "ledger";
        var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", WarInsetStyle(10, 8)); rows.AddChild(panel);
        var line = new HBoxContainer(); line.AddThemeConstantOverride("separation", 8); panel.AddChild(line);
        line.AddChild(new TextureRect { Texture = Art("icon_" + icon, "res://game/gfx/interface/icons/icon_" + icon + ".png"),
            CustomMinimumSize = new Vector2(24, 24), SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = ArtColour("icon_" + icon, "text.accent") });
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; line.AddChild(text);
        text.AddChild(Title(caption, 12, Ui.Colour("text.caption")));
        text.AddChild(Title(value, 15, Ui.Colour("text.bright")));
    }
}
