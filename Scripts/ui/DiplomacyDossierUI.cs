using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>Persistent identity and navigation above the scrollable diplomatic document.</summary>
public partial class DealOrForceGame
{
    private string _diplomacyDossierPage = "Overview";
    private ScrollContainer _diplomacyDossierScroll;
    private Control _diplomacyDossierFlag;
    private VBoxContainer _diplomacyLeaderPortraitSlot;
    private Label _diplomacyDossierName, _diplomacyDossierIdentity, _diplomacyDossierFigures;
    private HBoxContainer _diplomacyDossierOpinion;
    private readonly Dictionary<string, Button> _diplomacyDossierTabs = new();
    private readonly Dictionary<string, int> _diplomacyDossierOffsets = new();

    private void BuildDiplomacyDossierChrome(VBoxContainer parent)
    {
        var banner = new PanelContainer { Name = "DiplomacyCountryBanner", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        banner.AddThemeStyleboxOverride("panel", WarInsetStyle(14, 12));
        AtlasSurfaceSkin.Attach(banner, AtlasSurfaceSkin.Level.Raised, "war.card");
        parent.AddChild(banner);
        var rows = new VBoxContainer(); rows.AddThemeConstantOverride("separation", 7); banner.AddChild(rows);
        var identity = new HBoxContainer(); identity.AddThemeConstantOverride("separation", 12); rows.AddChild(identity);
        _diplomacyDossierFlag = FlagBadge("", 44); identity.AddChild(_diplomacyDossierFlag);
        _diplomacyLeaderPortraitSlot = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        identity.AddChild(_diplomacyLeaderPortraitSlot);
        var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; identity.AddChild(words);
        _diplomacyDossierName = Title("", 23, Ui.Colour("text.bright"));
        _diplomacyDossierName.Name = "DiplomacyDossierName";
        _diplomacyDossierName.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        words.AddChild(_diplomacyDossierName);
        _diplomacyDossierIdentity = CardLine("", "text.caption"); words.AddChild(_diplomacyDossierIdentity);
        _diplomacyDossierFigures = CardLine("", "text.body_soft"); rows.AddChild(_diplomacyDossierFigures);
        // Opinion lives on its own line. A fixed 160px aside squeezed long names and
        // government descriptions out of the old banner at 720p, especially in Russian.
        _diplomacyDossierOpinion = new HBoxContainer { Name = "DiplomacyOpinionStrip" };
        _diplomacyDossierOpinion.AddThemeConstantOverride("separation", 8); rows.AddChild(_diplomacyDossierOpinion);
        var tabs = new HBoxContainer { Name = "DiplomacyDossierTabs" }; tabs.AddThemeConstantOverride("separation", 4); parent.AddChild(tabs);
        foreach (var page in new[] { "Overview", "Government", "Relations", "Comparison" })
        {
            var captured = page;
            var button = ActionButton(page, () => SelectDiplomacyDossierPage(captured), 0, 34);
            button.Name = "DiplomacyPage" + page; button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.SetMeta("atlas_tab_button", true);
            button.AddThemeFontSizeOverride("font_size", 12); button.ClipText = true;
            StyleButton(button, page == _diplomacyDossierPage);
            tabs.AddChild(button); _diplomacyDossierTabs.Add(page, button);
        }
    }

    private void SelectDiplomacyDossierPage(string page)
    {
        if (!_diplomacyDossierTabs.ContainsKey(page) || _diplomacyDossierPage == page) return;
        _diplomacyDossierOffsets[_diplomacyDossierPage] = _diplomacyDossierScroll.ScrollVertical;
        _diplomacyDossierPage = page;
        ForceDiplomacyRefresh();
        _diplomacyDossierScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical,
            _diplomacyDossierOffsets.GetValueOrDefault(page));
    }

    private void RefreshDiplomacyDossierChrome(Nation nation, bool canSee)
    {
        SetFlag(_diplomacyDossierFlag, nation.Tag, 44); TintBlankFlag(_diplomacyDossierFlag, nation);
        if (_diplomacyLeaderPortraitSlot is not null)
        {
            ClearRuntimeChildren(_diplomacyLeaderPortraitSlot);
            var hos = Rulers.HeadOfState(_campaign, nation);
            var badge = BuildLeaderPortraitBadge(hos, nation.Color, 48, 58);
            badge.TooltipText = Ui.Tip($"{Tr(hos.Title)}: {hos.Name} ({Simulation.Loc.Format("{0} yrs", hos.Age)})\n{Tr("Trait")}: {Tr(hos.TraitName)}");
            _diplomacyLeaderPortraitSlot.AddChild(badge);
        }
        _diplomacyDossierName.Text = nation.DisplayName;
        var ruler = Rulers.HeadOfState(_campaign, nation);
        _diplomacyDossierIdentity.Text = Loc.Format("{0} · {1} · {2} · {3}: {4}", nation.Tag,
            Tr(PoliticsCore.GovernmentOf(nation).Name), Tr(PoliticsCore.IdeologyOf(nation).Name),
            Tr(ruler.Title), ruler.Name);
        _diplomacyDossierFigures.Text = canSee
            ? Loc.Format("{0} people · GDP {1} · stability {2}", Fmt.People(nation.Population), Fmt.Money(nation.Gdp), Fmt.Percent(nation.Stability))
            : Tr("What they have is not known to us.");
        foreach (var (page, button) in _diplomacyDossierTabs)
        {
            button.Text = Tr(page); button.TooltipText = Tr(page);
            if (!button.HasMeta("dossier_page_active") || button.GetMeta("dossier_page_active").AsBool() != (page == _diplomacyDossierPage))
            { button.SetMeta("dossier_page_active", page == _diplomacyDossierPage); StyleButton(button, page == _diplomacyDossierPage); }
        }
    }
}
