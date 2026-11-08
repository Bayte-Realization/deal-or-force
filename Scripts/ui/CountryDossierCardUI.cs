using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string _countryDossierTab = "Overview";
    private readonly Dictionary<string, Button> _countryDossierTabs = new();
    private readonly Dictionary<string, int> _countryDossierScroll = new();
    private bool _countryDossierChangingPage;
    private readonly List<(HBoxContainer Row, Label Caption, Label Value)> _countryFactPool = new();
    private int _countryFactCursor;

    private void BeginCountryFactsUpdate()
    {
        _countryFactCursor = 0;
        foreach (var (row, _, _) in _countryFactPool) row.Visible = false;
        foreach (var child in _countryCardRows.GetChildren())
            if (!child.HasMeta("retained_country_fact"))
            { _countryCardRows.RemoveChild(child); child.QueueFree(); }
    }

    private void RepaintCountryFact(string caption, string value, string tooltip, string token, bool clipValue = false)
    {
        if (_countryFactCursor == _countryFactPool.Count)
        {
            var row = new StructuredFactRow { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 26),
                MouseFilter = MouseFilterEnum.Stop };
            row.AddThemeConstantOverride("separation", 10); row.SetMeta("retained_country_fact", true);
            var padLeft = new Control { CustomMinimumSize = new Vector2(6, 0), MouseFilter = MouseFilterEnum.Ignore };
            var name = Title("", 12, Ui.Colour("text.caption"));
            name.Name = "CountryFactCaption";
            // Godot's clipped + wrapped labels can report a one-pixel minimum height.
            name.ClipText = true; name.AutowrapMode = TextServer.AutowrapMode.Off;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill; name.MouseFilter = MouseFilterEnum.Ignore;
            var figure = Title("", 14, Ui.Colour("text.bright"));
            figure.Name = "CountryFactValue";
            figure.AddThemeFontOverride("font", Ui.NumberFont);
            figure.AutowrapMode = TextServer.AutowrapMode.WordSmart; figure.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            figure.HorizontalAlignment = HorizontalAlignment.Right; figure.MouseFilter = MouseFilterEnum.Ignore;
            var padRight = new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore };
            row.AddChild(padLeft); row.AddChild(name); row.AddChild(figure); row.AddChild(padRight); _countryCardRows.AddChild(row);
            _countryFactPool.Add((row, name, figure));
        }
        var index = _countryFactCursor;
        var item = _countryFactPool[_countryFactCursor++];
        if (item.Row is StructuredFactRow structured)
        {
            structured.Alternate = (index & 1) == 1;
            structured.AccentToken = token;
            structured.QueueRedraw();
        }
        item.Row.Visible = true;
        item.Caption.Text = Tr(caption); item.Value.Text = value ?? "";
        item.Caption.SetMeta(LocSourceMeta, caption); item.Caption.SetMeta(LocAppliedMeta, item.Caption.Text);
        item.Caption.TooltipText = Tr(caption);
        item.Caption.SizeFlagsHorizontal = clipValue ? SizeFlags.ShrinkBegin : SizeFlags.ExpandFill;
        item.Value.AutowrapMode = clipValue ? TextServer.AutowrapMode.Off : TextServer.AutowrapMode.WordSmart;
        item.Value.ClipText = clipValue;
        item.Value.TextOverrunBehavior = clipValue
            ? TextServer.OverrunBehavior.TrimEllipsis
            : TextServer.OverrunBehavior.NoTrimming;
        item.Value.HorizontalAlignment = clipValue ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        item.Value.AddThemeColorOverride("font_color", Ui.Colour(token));
        item.Row.TooltipText = Ui.Tip(string.IsNullOrEmpty(tooltip) ? Tr(caption) + ": " + value : tooltip);
        _countryCardRows.MoveChild(item.Row, _countryCardRows.GetChildCount() - 1);
    }

    private void BuildCountryDossierTabs(VBoxContainer rows)
    {
        var tabs = new HBoxContainer { Name = "CountryDossierTabs", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tabs.AddThemeConstantOverride("separation", 3);
        rows.AddChild(tabs);
        foreach (var page in new[] { "Overview", "Government", "Relations", "Actions" })
        {
            var captured = page;
            var button = ActionButton(page, () => SelectCountryDossierPage(captured), 0, 32);
            button.Name = "CountryPage" + page;
            button.SetMeta("atlas_tab_button", true);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", 12);
            button.ClipText = true;
            button.TooltipText = Tr(page);
            StyleButton(button, page == _countryDossierTab);
            tabs.AddChild(button); _countryDossierTabs.Add(page, button);
        }
    }

    private void SelectCountryDossierPage(string page)
    {
        if (!_countryDossierTabs.ContainsKey(page) || _countryDossierTab == page) return;
        var previous = _countryDossierTab == "Actions" ? _countryOrdersScroll : _countryFactsScroll;
        _countryDossierScroll[_countryDossierTab] = previous.ScrollVertical;
        _countryDossierTab = page;
        _countryDossierChangingPage = true;
        ShowCountryCard(_mapSelectionNation ?? _selectedNation);
        var scroll = page == "Actions" ? _countryOrdersScroll : _countryFactsScroll;
        scroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, _countryDossierScroll.GetValueOrDefault(page));
        _countryDossierChangingPage = false;
    }

    private void RefreshCountryDossierTabs(Nation nation)
    {
        foreach (var (page, button) in _countryDossierTabs)
        {
            button.Text = Tr(page); button.TooltipText = Tr(page);
            // Do not rebuild the tab strip on a tick: keyboard focus stays where it was.
            if (!button.HasMeta("country_page_active") || button.GetMeta("country_page_active").AsBool() != (page == _countryDossierTab))
            {
                button.SetMeta("country_page_active", page == _countryDossierTab);
                StyleButton(button, page == _countryDossierTab);
            }
        }
        _countryFactsScroll.Visible = _countryDossierTab != "Actions" || nation == _campaign.Player;
        if (_countryCardFlagEdit is not null)
            _countryCardFlagEdit.Visible = nation == _campaign.Player && CampaignCosmetics.CanEdit(_campaign, _campaign.Player, nation);
        _countryOrdersScroll.Visible = _countryDossierTab == "Actions" && nation != _campaign.Player;
        // Opening actions does not execute them. This is still the one shared action block.
        _countryCardCaption.TooltipText = Ui.Tip(nation.DisplayName + "\n" +
            Tr(PoliticsCore.GovernmentOf(nation).Name) + " · " + Tr(PoliticsCore.IdeologyOf(nation).Name) + "\n" +
            Loc.Format("Country code (saves and mods): {0}", nation.Tag));
        if (!_countryDossierChangingPage && _countryDossierTab != "Actions")
        {
            var y = _countryFactsScroll.ScrollVertical;
            _countryFactsScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, y);
        }
    }

    private void AddCountryCompactRelations(Nation nation)
    {
        var master = SubjectSystem.OverlordOf(_campaign, nation);
        if (master is not null)
        {
            CardRow(_countryCardRows, "Answers to", master.DisplayName);
            CardRow(_countryCardRows, "Subject status", Tr(SubjectCatalog.Of(nation.SubjectType)?.Name ?? nation.SubjectType));
            CardRow(_countryCardRows, "Liberty desire", Fmt.Percent(nation.LibertyDesire));
        }
        var subjects = SubjectSystem.SubjectsOf(_campaign, nation).ToArray();
        if (subjects.Length > 0)
            CardRow(_countryCardRows, "Subjects", string.Join(", ", subjects.Select(s => s.DisplayName)));
        foreach (var war in _campaign.Wars.Where(w => !w.Ended && w.Involves(nation.Id)))
            CardRow(_countryCardRows, "At war", WarNames.Of(_campaign, war));
        // The deterrent a country holds is the one fact that redraws every other row here:
        // what it may do, what may be done to it, and what its wars can end in.
        var deterrentTier = Deterrence.TierOf(_campaign, nation);
        if (deterrentTier > 0)
            CardRow(_countryCardRows, "Nuclear deterrent",
                deterrentTier >= 2 ? Loc.Format("Tier {0} · second strike", deterrentTier) : Loc.Get("Mutual assured destruction"),
                Loc.Get("A holder of the deterrent is not attacked directly by another holder, and its wars with other holders end in limited terms: liberation and money, never conquest. Acquisition is research, not decree."));
        if (_campaign.FeatureEnabled("organizations"))
            foreach (var organization in InternationalOrganizations.Memberships(_campaign, nation.Id))
                CardRow(_countryCardRows, "Organizations", InternationalOrganizations.DisplayName(_campaign, organization));
        if (_countryCardRows.GetChildren().OfType<Control>().All(c => !c.Visible))
            _countryCardRows.AddChild(CardLine("No active diplomatic ties.", "text.muted"));
    }
}
