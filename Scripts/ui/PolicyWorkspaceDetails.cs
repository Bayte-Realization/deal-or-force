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
        if (_policyTab == "Crises") { ShowDomesticCrisisWorkspace(); return; }
        ClearPolicyRows(_policyOptions);
        _policyChoices.Clear();
        _policyConfirmation = "";
        if (_policyTab == "Course")
        {
            ShowNationalCourse(_campaign.Player, _policyOptions);
            RefreshPolicyPreview(); return;
        }
        var choices = WorkspacePolicies().Where(p => p.Category == _policyCategory && PolicyMatchesSearch(p)).ToArray();
        if (choices.Length == 0)
        {
            PolicyText(_policyOptions, "Choose another section or clear your search.");
            _policySelected = ""; RefreshPolicyPreview(); return;
        }
        var current = PolicyIsAgenda ? PoliticalPolicyCatalog.Find(_campaign.Player.Agenda?.Id, true) :
            PoliticalPolicies.Effective(_campaign.Player, choices[0], _lawTarget, _lawRegion);
        if (!choices.Any(p => p.Id == _policySelected)) _policySelected =
            choices.FirstOrDefault(p => p.Id == current?.Id)?.Id ?? choices[0].Id;
        SectionRule(_policyOptions, _policyCategory, choices.Length.ToString());
        foreach (var policy in choices)
        {
            var button = ActionButton("", () => SelectWorkspacePolicy(policy.Id), 0, 76, policy.Id == _policySelected);
            button.Name = "SelectPolicy_" + policy.Id;
            button.TooltipText = Ui.Tip(Tr(policy.Name) + "\n\n" + Tr(policy.Description));
            _policyOptions.AddChild(button);
            var inset = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            inset.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            foreach (var side in new[] { "left", "right" }) inset.AddThemeConstantOverride("margin_" + side, 12);
            foreach (var side in new[] { "top", "bottom" }) inset.AddThemeConstantOverride("margin_" + side, 8);
            button.AddChild(inset);
            var rows = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; rows.AddThemeConstantOverride("separation", 3); inset.AddChild(rows);
            var title = Title(policy.Name, 14, Ui.Colour("text.bright"));
            title.MaxLinesVisible = 2; title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            title.MouseFilter = MouseFilterEnum.Ignore; rows.AddChild(title);
            var status = Title("", 11, Ui.Colour("text.caption"));
            status.MouseFilter = MouseFilterEnum.Ignore; status.MaxLinesVisible = 1;
            status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; rows.AddChild(status);
            _policyChoices[policy.Id] = (button, status);
        }
        PaintPolicyChoices(current);
        RefreshPolicyPreview();
    }

    private bool PolicyMatchesSearch(PoliticalPolicy policy)
    {
        var query = _policySearch.Text.Trim();
        return query.Length == 0 || Tr(policy.Category).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Tr(policy.Name).Contains(query, StringComparison.OrdinalIgnoreCase) || policy.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectWorkspacePolicy(string id)
    {
        if (_policySelected == id) return;
        _policySelected = id; _policyConfirmation = ""; _policyWorkspaceFeedback.Visible = false;
        var p = PoliticalPolicyCatalog.Find(id, PolicyIsAgenda);
        PaintPolicyChoices(PolicyIsAgenda ? PoliticalPolicyCatalog.Find(_campaign.Player.Agenda?.Id, true) :
            PoliticalPolicies.Effective(_campaign.Player, p, _lawTarget, _lawRegion));
        _policyPreviewScroll.ScrollVertical = 0; RefreshPolicyPreview();
    }

    private void PaintPolicyChoices(PoliticalPolicy current)
    {
        foreach (var (id, row) in _policyChoices)
        {
            var selected = id == _policySelected;
            StyleButton(row.Button, selected);
            row.Status.Text = Tr(current?.Id == id ? "Currently in force" : selected ? "Selected for review" : "Alternative policy");
            row.Status.AddThemeColorOverride("font_color", Ui.Colour(current?.Id == id ? "state.positive" : selected ? "text.accent" : "text.caption"));
        }
    }

    private void RefreshPolicyPreview()
    {
        if (_policyPreview is null || _campaign?.Player is not { } n) return;
        if (_policyTab == "Crises") return;
        var scroll = _policyPreviewScroll.ScrollVertical;
        ClearPolicyRows(_policyPreview);
        if (_policyTab == "Doctrines")
        {
            ShowDoctrinePreview(n);
            FitPolicyTypography();
            return;
        }
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
                PolicyText(_policyPreview, Simulation.Loc.Format("In the lead for {0} days.", memory.ChallengerDays));
            }
            PolicyText(_policyPreview, "Laws, living conditions and great events shape it over the years.");
            FitPolicyTypography();
            return;
        }
        if (PolicyIsAgenda) ShowPromiseProgress(n);
        var p = PoliticalPolicyCatalog.Find(_policySelected, PolicyIsAgenda);
        if (p is null) { PolicyText(_policyPreview, "No policy selected"); RefreshPolicyReformBar(true); FitPolicyTypography(); return; }
        SectionRule(_policyPreview, "SELECTED POLICY");
        _policyPreview.AddChild(Title(p.Name, 22, Ui.Colour("text.bright")));
        PolicyText(_policyPreview, p.Description);
        var currentLaw = PolicyIsAgenda ? null : PoliticalPolicies.Effective(n, p, _lawTarget, _lawRegion);
        BuildPolicyComparison(n, currentLaw, p);
        if (PolicyIsAgenda)
        {
            PolicyStat(_policyPreview, "Term progress", Simulation.Loc.Format("{0} days", p.DurationDays), "agenda");
            PolicyText(_policyPreview, Tr(p.Goal switch { "living" => "Living standard target", "debt_ratio" => "Debt / GDP ceiling",
                "peace" => "Remain at peace", _ => "Stability target" }) + (p.Goal == "peace" ? "" : ": " + Fmt.Percent(p.GoalValue)));
            PolicyText(_policyPreview, "The goal must be met at the end and for at least a third of the term.");
        }
        _policyPreviewScroll.ScrollVertical = scroll;
        RefreshPolicyReformBar(true);
        FitPolicyTypography();
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
        {
            _policyWorkspaceFeedback.Text = Tr(refusal);
            _policyWorkspaceFeedback.Visible = true;
            PlayStrategicCue("action_denied");
            RefreshPolicyPreview();
            return;
        }
        if (_policyConfirmation != p.Id)
        {
            _policyConfirmation = p.Id;
            _policyWorkspaceFeedback.Text = Simulation.Loc.Format("Confirm {0}: {1} will be paid from the treasury.", Tr(p.Name), Fmt.Money(n.Gdp * p.CostShare));
            _policyWorkspaceFeedback.Visible = false; // The persistent footer carries the confirmation and exact price.
            RefreshPolicyPreview(); _policyEnact.GrabFocus(); return;
        }
        // Normal authority-checked command path: local UI, multiplayer and mods agree.
        _campaign.Commands.Enqueue(new AdoptPoliticalPolicyCommand { IssuerId = n.Id, PolicyId = p.Id,
            TargetTag = PolicyScope == "foreign" ? _lawTarget : "", Region = PolicyScope == "region" ? _lawRegion : 0, Agenda = PolicyIsAgenda });
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("policy_changed");
        _policyWorkspaceFeedback.Text = Simulation.Loc.Format("Adopted: {0}.", Tr(p.Name));
        _policyWorkspaceFeedback.Visible = true;
        ShowPolicyOptions(); _policyBuiltDay = -1; RefreshPolicyWorkspace();
    }

    private void PolicyHeading(VBoxContainer rows, string caption)
    {
        // A section head, in one place for every policy screen: tracked micro-caps with a rule
        // under them. It used to be a 13-pixel upper-case caption floating over a list, which is
        // what made a page of decisions read as one long form rather than as sections.
        var block = new VBoxContainer { Name = "PolicyHeading_" + caption };
        block.AddThemeConstantOverride("separation", 4);
        rows.AddChild(block);
        block.AddChild(Title(caption, (int)Ui.Metric("ui.font.caption", 11), Ui.Colour("text.accent"), true));
        block.AddChild(new HSeparator { Name = "PolicyHeadingRule" });
    }
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
        var val = Title(value, 15, Ui.Colour("text.bright"));
        val.AddThemeFontOverride("font", PresentationFonts.Tabular());
        text.AddChild(val);
    }

}
