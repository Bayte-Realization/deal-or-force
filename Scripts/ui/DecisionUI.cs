using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _decisionControls = null!;
    private VBoxContainer _agendaHost;
    private LineEdit _agendaSearch;
    private OptionButton _agendaSectionPicker, _agendaStatusPicker;
    private ItemList _agendaList;
    private Label _agendaResults, _agendaTitle, _agendaDescription, _agendaCost, _agendaEffects, _agendaReason;
    private Button _agendaApply;
    private readonly List<AgendaWorkspace.Entry> _agendaPageRows = new();
    private readonly HashSet<string> _goalsSeenComplete = new();
    private AgendaWorkspace.Section _agendaSection;
    private AgendaWorkspace.Filter _agendaFilter;
    private string _agendaQuery = "", _agendaSelected = "";
    private Campaign _agendaWorld;
    private int _agendaNation = -1;

    private void BuildDecisionControls(VBoxContainer parent)
    {
        _decisionControls = new VBoxContainer { Visible = false };
        _decisionControls.AddThemeConstantOverride("separation", 8);
        parent.AddChild(_decisionControls);
        var window = GetWindow();
        window.SizeChanged += RefreshAgendaViewport;
        TreeExiting += () => window.SizeChanged -= RefreshAgendaViewport;
    }

    private void ShowDecisionsScreen()
    {
        if (_dossierTab == "Opportunities") { ShowOpportunityAtlas(); return; }
        if (_dossierTab == "Formables") { ShowFormableNations(); return; }
        _formationConfirmation = "";
        if (!ReferenceEquals(_agendaWorld, _campaign) || _agendaNation != _campaign.Player.Id)
        {
            _agendaWorld = _campaign; _agendaNation = _campaign.Player.Id;
            _agendaSection = AgendaWorkspace.Section.Decisions; _agendaFilter = AgendaWorkspace.Filter.All;
            _agendaQuery = _agendaSelected = "";
            _goalsSeenComplete.Clear();
            if (GodotObject.IsInstanceValid(_agendaHost) && _agendaHost.GetParent() == _decisionControls)
            { _agendaSearch.Text = ""; _agendaSectionPicker.Select(0); _agendaStatusPicker.Select(0); }
        }
        EnsureAgendaControls();
        RefreshAgendaControls();
    }

    private void EnsureAgendaControls()
    {
        // Formables/atlas own this same dossier slot. Rebuild only when returning
        // from another tab, never because a campaign day advanced.
        if (GodotObject.IsInstanceValid(_agendaHost) && !_agendaHost.IsQueuedForDeletion() &&
            _agendaHost.GetParent() == _decisionControls) return;
        ClearRuntimeChildren(_decisionControls);
        _agendaHost = new VBoxContainer { Name = "AgendaWorkspace" };
        _agendaHost.AddThemeConstantOverride("separation", 7);
        _decisionControls.AddChild(_agendaHost);
        _agendaHost.AddChild(CardLine("Review initiatives without changing the world. Only the action button issues an order.", "text.muted"));
        var selectors = new HBoxContainer();
        _agendaHost.AddChild(selectors);
        _agendaSectionPicker = new OptionButton { Name = "AgendaSection", FitToLongestItem = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var caption in new[] { "Decisions", "Promises", "Goals" }) _agendaSectionPicker.AddItem(Tr(caption));
        _agendaSectionPicker.Select((int)_agendaSection);
        _agendaSectionPicker.AccessibilityName = Tr("Agenda section");
        selectors.AddChild(_agendaSectionPicker);
        _agendaStatusPicker = new OptionButton { Name = "AgendaStatus", FitToLongestItem = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var caption in new[] { "All statuses", "Ready", "Active", "Completed" }) _agendaStatusPicker.AddItem(Tr(caption));
        _agendaStatusPicker.Select((int)_agendaFilter);
        _agendaStatusPicker.AccessibilityName = Tr("Agenda status filter");
        selectors.AddChild(_agendaStatusPicker);
        _agendaSectionPicker.ItemSelected += index =>
        { _agendaSection = (AgendaWorkspace.Section)index; _agendaSelected = ""; RefreshAgendaControls(); };
        _agendaStatusPicker.ItemSelected += index =>
        { _agendaFilter = (AgendaWorkspace.Filter)index; RefreshAgendaControls(); };

        _agendaSearch = Placeholder(new LineEdit { Name = "AgendaSearch", ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, Text = _agendaQuery }, "Find an initiative or ID...");
        _agendaSearch.AccessibilityName = Tr("Agenda search");
        _agendaHost.AddChild(_agendaSearch);
        _agendaSearch.TextChanged += text => { _agendaQuery = text; RefreshAgendaControls(); };
        _agendaResults = CardLine("", "text.caption"); _agendaResults.Name = "AgendaResults";
        _agendaHost.AddChild(_agendaResults);

        _agendaList = new ItemList
        {
            Name = "AgendaEntries",
            SelectMode = ItemList.SelectModeEnum.Single,
            AllowReselect = true,
            AutoHeight = false,
            SameColumnWidth = true,
            FixedColumnWidth = 0,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, AgendaListHeight()),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseForcePassScrollEvents = false,
            AccessibilityName = Tr("All matching initiatives")
        };
        _agendaList.AddThemeColorOverride("font_color", Ui.Colour("text.body"));
        _agendaList.AddThemeColorOverride("font_selected_color", Ui.Colour("text.bright"));
        _agendaList.AddThemeColorOverride("font_hovered_color", Ui.Colour("text.accent"));
        _agendaList.AddThemeConstantOverride("v_separation", 5);
        _agendaList.ItemSelected += index =>
        {
            if (index >= 0 && index < _agendaPageRows.Count)
            {
                _agendaSelected = _agendaPageRows[(int)index].Id;
                RefreshAgendaDetail(_agendaPageRows[(int)index]);
            }
        };
        _agendaHost.AddChild(_agendaList);

        var detail = WorkbenchCard(_agendaHost);
        detail.Name = "AgendaSelectedDetail";
        _agendaTitle = CardLine("", "text.accent"); _agendaTitle.Name = "AgendaTitle";
        _agendaTitle.AddThemeFontSizeOverride("font_size", 17);
        _agendaDescription = CardLine("", "text.body_soft");
        _agendaCost = CardLine("", "text.accent_dim");
        _agendaCost.Name = "AgendaCost";
        _agendaEffects = CardLine("", "text.body"); _agendaEffects.Name = "AgendaEffects";
        _agendaReason = CardLine("", "state.warning"); _agendaReason.Name = "AgendaReason";
        _agendaApply = ActionButton("Enact decision", ExecuteSelectedAgenda, 0, 34);
        foreach (var control in new Control[] { _agendaTitle, _agendaDescription, _agendaCost, _agendaEffects, _agendaReason, _agendaApply })
            detail.AddChild(control);
    }

    private float AgendaListHeight()
    {
        var height = GetTree()?.Root?.Size.Y ?? (int)GetViewportRect().Size.Y;
        return height < Ui.Metric("agenda.compact_height", 800)
            ? Ui.Metric("agenda.list_height_compact", 156) : Ui.Metric("agenda.list_height", 260);
    }

    private void RefreshAgendaControls()
    {
        if (!GodotObject.IsInstanceValid(_agendaHost) || _agendaHost.IsQueuedForDeletion() ||
            _agendaHost.GetParent() != _decisionControls || _campaign?.Player is null) return;
        var nation = _campaign.Player;
        _sideTitle.Text = Tr("Decisions");
        _sideBody.Text = Simulation.Loc.Format("National agenda · treasury {0}", Fmt.Money(nation.Treasury));
        var wantedHeight = AgendaListHeight();
        if (Math.Abs(_agendaList.CustomMinimumSize.Y - wantedHeight) > 0.5f)
        {
            _agendaList.CustomMinimumSize = new Vector2(0, wantedHeight);
            _agendaList.Size = new Vector2(_agendaList.Size.X, wantedHeight);
        }

        var entries = AgendaWorkspace.Entries(_campaign, nation, _agendaSection, _agendaQuery, _agendaFilter);
        _agendaPageRows.Clear();
        _agendaPageRows.AddRange(entries);
        if (!_agendaPageRows.Any(row => row.Id == _agendaSelected))
            _agendaSelected = _agendaPageRows.FirstOrDefault().Id ?? "";

        var disabled = _agendaSection == AgendaWorkspace.Section.Promises && !_campaign.FeatureEnabled("government_promises") ||
            _agendaSection == AgendaWorkspace.Section.Goals && !_campaign.FeatureEnabled("strategic_goals");
        var readyCount = _agendaPageRows.Count(r => r.State == AgendaWorkspace.Status.Ready);
        _agendaResults.Text = disabled ? Tr("This mechanic is disabled in this scenario.") : entries.Count == 0 ? Tr("Nothing matches these filters.") :
            Simulation.Loc.Format("{0} initiatives · {1} ready", entries.Count, readyCount);

        var scroll = _agendaList.GetVScrollBar();
        var prevScroll = scroll?.Value ?? 0;

        while (_agendaList.ItemCount > _agendaPageRows.Count)
            _agendaList.RemoveItem(_agendaList.ItemCount - 1);

        var selectedIdx = -1;
        for (var i = 0; i < _agendaPageRows.Count; i++)
        {
            var row = _agendaPageRows[i];
            var badge = row.State switch
            {
                AgendaWorkspace.Status.Ready => "● ",
                AgendaWorkspace.Status.Active => "◆ ",
                AgendaWorkspace.Status.Completed => "✓ ",
                _ => "· "
            };
            var text = badge + Tr(row.Name) + " · " + Tr("Agenda status: " + row.State);
            if (i >= _agendaList.ItemCount) _agendaList.AddItem(text);
            else _agendaList.SetItemText(i, text);
            _agendaList.SetItemMetadata(i, row.Id);
            _agendaList.SetItemTooltip(i, Ui.Tip(text + (row.Reason.Length == 0 ? "" : "\n" + Tr(row.Reason))));
            _agendaList.SetItemCustomFgColor(i, row.State switch
            {
                AgendaWorkspace.Status.Ready => Ui.Colour("state.positive"),
                AgendaWorkspace.Status.Active => Ui.Colour("text.accent"),
                AgendaWorkspace.Status.Completed => Ui.Colour("state.info"),
                _ => Ui.Colour("text.body_soft")
            });
            if (row.Id == _agendaSelected) selectedIdx = i;
        }

        if (selectedIdx >= 0) _agendaList.Select(selectedIdx);
        else _agendaList.DeselectAll();
        if (scroll is not null && prevScroll > 0 && _agendaList.ItemCount > 0)
            scroll.Value = prevScroll;

        RefreshAgendaDetail(_agendaPageRows.FirstOrDefault(row => row.Id == _agendaSelected));
        if (_campaign.FeatureEnabled("strategic_goals"))
            foreach (var goal in nation.Goals)
                if (goal.CompletedDay >= 0 && _goalsSeenComplete.Add(goal.Id)) PlayStrategicCue("goal_complete");
                else if (goal.CompletedDay < 0) _goalsSeenComplete.Remove(goal.Id);
    }

    private void RefreshAgendaViewport()
    {
        if (_agendaHost is not null && GodotObject.IsInstanceValid(_agendaHost) &&
            _agendaHost.IsVisibleInTree() && _dossierScreen == "Decisions" && _dossierTab == "Decisions")
            RefreshAgendaControls();
    }

    private void RefreshAgendaLanguage()
    {
        if (!GodotObject.IsInstanceValid(_agendaHost) || _agendaHost.IsQueuedForDeletion() ||
            _agendaHost.GetParent() != _decisionControls) return;
        var sections = new[] { "Decisions", "Promises", "Goals" };
        var statuses = new[] { "All statuses", "Ready", "Active", "Completed" };
        for (var i = 0; i < sections.Length; i++) _agendaSectionPicker.SetItemText(i, Tr(sections[i]));
        for (var i = 0; i < statuses.Length; i++) _agendaStatusPicker.SetItemText(i, Tr(statuses[i]));
        _agendaSearch.PlaceholderText = Tr("Find an initiative or ID...");
        RefreshAgendaControls();
    }

    private void RefreshAgendaDetail(AgendaWorkspace.Entry row)
    {
        var has = !string.IsNullOrEmpty(row.Id);
        _agendaTitle.Text = has ? Tr(row.Name) : Tr("No initiative selected");
        _agendaDescription.Text = has ? Tr(row.Description) : Tr("Change the search or status filter to see other initiatives.");
        _agendaCost.Text = _agendaEffects.Text = _agendaReason.Text = "";
        _agendaApply.Visible = has;
        if (!has) return;
        var nation = _campaign.Player;
        var lines = new List<string>();
        var action = "Enact decision";
        var enabled = row.State == AgendaWorkspace.Status.Ready;
        if (_agendaSection == AgendaWorkspace.Section.Decisions)
        {
            var d = DecisionCatalog.All.FirstOrDefault(d => d.Id == row.Id);
            if (d is { Id.Length: > 0 })
            {
                var price = DecisionCatalog.Cost(nation, d);
                _agendaCost.Text = Simulation.Loc.Format("Cost {0} · cooldown {1} days", Fmt.Money(price), d.CooldownDays);
                var resources = DecisionResources.PriceText(nation, d);
                if (resources.Length > 0) _agendaCost.Text += "\n" + resources;
                lines.AddRange(ScriptedEffects.PreviewForNation(_campaign, nation, d.Effect, price));
            }
        }
        else if (_agendaSection == AgendaWorkspace.Section.Promises && PromiseCatalog.Find(row.Id) is { } promise)
        {
            action = row.State == AgendaWorkspace.Status.Active ? "Active promise" : "Make promise";
            var active = nation.Promises.FirstOrDefault(p => p.Id == row.Id);
            _agendaCost.Text = active is null ? Simulation.Loc.Format("Deadline {0} days", promise.DurationDays) :
                Simulation.Loc.Format("Days left: {0}", Math.Max(0, active.DueDay - _campaign.ElapsedDays));
            lines.Add(Tr("If kept:"));
            lines.AddRange(ScriptedEffects.PreviewForNation(_campaign, nation, promise.Kept));
            lines.Add(Tr("If missed:"));
            lines.AddRange(ScriptedEffects.PreviewForNation(_campaign, nation, promise.Broken));
        }
        else if (_agendaSection == AgendaWorkspace.Section.Goals && GoalCatalog.Find(row.Id) is { } goal)
        {
            var active = nation.Goals.FirstOrDefault(g => g.Id == row.Id);
            action = row.State == AgendaWorkspace.Status.Completed ? "Completed" :
                active is null ? "Pin goal" : "Abandon goal";
            enabled = row.State is AgendaWorkspace.Status.Ready or AgendaWorkspace.Status.Active;
            var progress = StrategicGoals.Progress(_campaign, nation, goal);
            for (var i = 0; i < goal.Milestones.Count; i++)
                lines.Add((progress[i] ? "✓ " : "· ") + Tr(goal.Milestones[i].Text));
            _agendaCost.Text = Tr("A goal is private: nothing in the world changes when it is pinned, dropped or met. The record is the point.");
        }
        _agendaEffects.Text = lines.Count == 0 ? "" : Tr("Result") + ":\n" + string.Join("\n", lines);
        _agendaReason.Text = Tr(row.Reason);
        _agendaApply.Text = Tr(action); _agendaApply.Disabled = !enabled;
        _agendaApply.Name = (_agendaSection == AgendaWorkspace.Section.Decisions ? "Decision_" :
            _agendaSection == AgendaWorkspace.Section.Promises ? "Promise_" : "Goal_") + row.Id;
        SetAgendaHighlight(_agendaApply, enabled);
    }

    private void SetAgendaHighlight(Button button, bool highlighted)
    {
        if (button.HasMeta("agenda_highlight") && button.GetMeta("agenda_highlight").AsBool() == highlighted) return;
        button.SetMeta("agenda_highlight", highlighted);
        StyleButton(button, highlighted);
    }

    private void SelectAgendaEntry(AgendaWorkspace.Section section, string id)
    {
        _agendaSection = section; _agendaFilter = AgendaWorkspace.Filter.All; _agendaQuery = ""; _agendaSelected = id;
        EnsureAgendaControls();
        _agendaSectionPicker.Select((int)section); _agendaStatusPicker.Select(0); _agendaSearch.Text = "";
        RefreshAgendaControls();
        if (_agendaList is not null && _agendaList.GetSelectedItems().Length > 0)
            _agendaList.EnsureCurrentIsVisible();
    }

    private void ExecuteSelectedAgenda()
    {
        // Revalidate against the live world; displayed state is not command authority.
        var row = AgendaWorkspace.Entries(_campaign, _campaign.Player, _agendaSection)
            .FirstOrDefault(row => row.Id == _agendaSelected);
        if (string.IsNullOrEmpty(row.Id)) { RefreshAgendaControls(); return; }
        if (_agendaSection == AgendaWorkspace.Section.Goals && row.State == AgendaWorkspace.Status.Active) AbandonGoal(row.Id);
        else if (row.State != AgendaWorkspace.Status.Ready) RefreshAgendaControls();
        else if (_agendaSection == AgendaWorkspace.Section.Decisions) TakeDecision(row.Id);
        else if (_agendaSection == AgendaWorkspace.Section.Promises) TakePromise(row.Id);
        else AdoptGoal(row.Id);
    }

    private void TakeDecision(string id) => ApplyAgendaCommand(new TakeDecisionCommand { IssuerId = _campaign.Player.Id, DecisionId = id }, "save_complete");
    private void TakePromise(string id) => ApplyAgendaCommand(new MakePromiseCommand { IssuerId = _campaign.Player.Id, PromiseId = id }, "promise_seal");
    private void AdoptGoal(string id) => ApplyAgendaCommand(new AdoptGoalCommand { IssuerId = _campaign.Player.Id, GoalId = id }, "goal_pinned");
    private void AbandonGoal(string id) => ApplyAgendaCommand(new AbandonGoalCommand { IssuerId = _campaign.Player.Id, GoalId = id }, "");

    private void ApplyAgendaCommand(ICampaignCommand command, string cue)
    {
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        if (cue.Length > 0) PlayStrategicCue(cue);
        ShowDecisionsScreen();
        Refresh();
    }
}
