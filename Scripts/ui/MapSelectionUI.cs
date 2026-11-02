using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// What a left click on the map selects: a country, and the region and province under the
/// cursor - shown the way the genre shows them, as two panels rather than one.
///
/// The click used to open the dossier: one tall column titled with the region, holding the
/// country's diplomatic buttons, then the region's figures, then a button to go and talk to
/// the country somewhere else. For a foreign region most of that column was a line saying the
/// fog hid it. The two questions a click asks are different questions - who is this, and what
/// is this place - so they get a panel each: the country on the left with its flag, what can
/// be known about it and what can be done about it; the region and the province along the
/// bottom, where the map above them stays in view. The country is outlined on the map, the
/// same as a right click outlines it, so the panel and the map agree on what was picked.
///
/// The deep views stay where they were: the full diplomacy screen, and the region dossier
/// with its population tables, one button away from each panel.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _countryCard;
    private Control _countryCardFlag;
    private Label _countryCardName;
    private Label _countryCardCaption;
    private Label _countryCardStanding;
    private VBoxContainer _countryCardRows;
    private VBoxContainer _countryCardActionsHost;
    private ScrollContainer _countryFactsScroll;

    /// <summary>The flag editor's door on the player's own card; hidden on everybody else's.</summary>
    private Button _countryCardFlagEdit;
    private ScrollContainer _countryOrdersScroll;
    private HBoxContainer _countryCardLinks;

    private PanelContainer _regionCard;
    private Control _regionCardFlag;
    private Label _regionCardTitle;
    private Label _regionCardCaption;
    private VBoxContainer _regionCardRegionRows;
    private Label _regionCardProvinceTitle;
    private VBoxContainer _regionCardProvinceRows;
    private HBoxContainer _regionCardActions;
    private Button _regionCardMoveCapital;
    private Label _regionCardFeedback;

    /// <summary>Where the diplomatic action block lives when the dossier has it, and where in it.</summary>
    private Node _countryActionsHome;
    private int _countryActionsHomeIndex;

    private bool _mapSelectionOpen;
    private bool _mapSelectionHighlights;

    /// <summary>The country the panels are about - which the rest of the interface may move on from.</summary>
    private Nation _mapSelectionNation;
    private double _mapSelectionSinceRefresh;
    private long _mapSelectionRefreshedHour = long.MinValue;

    private bool MapSelectionShowing => _mapSelectionOpen && _countryCard is { Visible: true };

    // ------------------------------------------------------------------ building

    private void EnsureMapSelectionPanels()
    {
        if (_countryCard is not null || _hudLayer is null) return;

        // ---- the country, on the left; its configurable height contains a scrolling action list.
        _countryCard = TexturedPanel(_hudLayer, PanelSurface.Command);
        _countryCard.Name = "CountryCard";
        Ui.Place(_countryCard, "hud.country_card");
        _countryCard.Visible = false;
        var rows = PanelRows(_countryCard, 18, 14, 7);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 9);
        rows.AddChild(header);
        _countryCardFlag = FlagBadge("", 40);
        header.AddChild(_countryCardFlag);
        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        names.AddThemeConstantOverride("separation", 0);
        header.AddChild(names);
        _countryCardName = Title("", 20, Ui.Colour("text.bright"));
        _countryCardName.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _countryCardName.MaxLinesVisible = 3;
        names.AddChild(_countryCardName);
        _countryCardCaption = Title("", 12, Ui.Colour("text.caption"));
        _countryCardCaption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        names.AddChild(_countryCardCaption);
        // The player's own country may redesign its flag: a cosmetic choice, saved as a flag
        // description in the Editor workspace mod like any other (FlagEditorUI).
        _countryCardFlagEdit = ActionButton("Flag…", () =>
        {
            if (_campaign?.Player is { } player) OpenFlagEditor(player.Tag);
        }, 0, 32);
        _countryCardFlagEdit.TooltipText = Ui.Tip(Tr("Design your country's flag"));
        _countryCardFlagEdit.Visible = false;
        header.AddChild(_countryCardFlagEdit);
        header.AddChild(ActionButton("×", CloseMapSelection, 34, 32));

        _countryCardStanding = Title("", 14, Ui.Colour("text.accent"));
        _countryCardStanding.MouseFilter = Control.MouseFilterEnum.Stop;
        _countryCardStanding.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_countryCardStanding);
        BuildCountryDossierTabs(rows);

        // The card is a static top, a facts panel, and an actions panel, in that order.
        //
        // The top - flag, name, caption, standing - never scrolls. It says what the card is about,
        // and a reader who has scrolled down to the buttons should still be able to see whose
        // buttons they are.
        //
        // The actions are a panel with a scrollbar of their own rather than the tail of the facts.
        // They are what the card is open for, they are the part that grows - with the stance, with
        // a subject, with a war - and a list you have to scroll somebody's dossier past to reach is
        // a list in the wrong place.
        //
        // The facts take the height their own contents ask for, up to half the card. Not an
        // expanded share: two expanded panels split the card evenly, and the half the facts did not
        // need was a 240-pixel hole between "Full diplomacy" and the first action. And not a
        // measurement taken here either - Godot settles minimum sizes during the layout pass, so
        // asking at build time measures zero-height labels and the panel falls back to its cap.
        // SyncCountryFactsHeight keeps that height true every frame, which is the only moment the
        // answer exists.
        var countryFactsScroll = new ScrollContainer
        {
            Name = "CountryFactsScroll",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true
        };
        _countryFactsScroll = countryFactsScroll;
        rows.AddChild(countryFactsScroll);
        var facts = new VBoxContainer { Name = "CountryFacts", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        facts.AddThemeConstantOverride("separation", 7);
        countryFactsScroll.AddChild(facts);

        _countryCardRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _countryCardRows.AddThemeConstantOverride("separation", 2);
        facts.AddChild(_countryCardRows);

        _countryCardLinks = new HBoxContainer { Name = "CountryCardLinks" };
        _countryCardLinks.AddThemeConstantOverride("separation", 5);
        facts.AddChild(_countryCardLinks);

        // The floor keeps the actions panel from being squeezed out by a long dossier, and the
        // scrollbar is why `MouseForcePassScrollEvents` is off: over a list of buttons the wheel
        // should move the buttons rather than whatever is behind the card.
        var actionsScroll = new ScrollContainer
        {
            Name = "CountryActionsScroll",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 80),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true, MouseForcePassScrollEvents = false
        };
        _countryOrdersScroll = actionsScroll;
        rows.AddChild(actionsScroll);

        // Where the diplomatic buttons go while this card is open. They are one set of buttons
        // with one set of rules, and they move between here and the dossier rather than existing
        // twice and disagreeing.
        _countryCardActionsHost = new VBoxContainer { Name = "CountryActionsHost",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        actionsScroll.AddChild(_countryCardActionsHost);

        // ---- the region and the province, along the bottom.
        _regionCard = TexturedPanel(_hudLayer, PanelSurface.Command);
        _regionCard.Name = "RegionCard";
        Ui.Place(_regionCard, "hud.region_card");
        // Anchored to the bottom, so when it needs more room it takes it upwards rather than
        // hanging off the edge of the screen.
        _regionCard.GrowVertical = Control.GrowDirection.Begin;
        _regionCard.Visible = false;
        var regionRows = PanelRows(_regionCard, 18, 12, 6);

        var regionHeader = new HBoxContainer();
        regionHeader.AddThemeConstantOverride("separation", 8);
        regionRows.AddChild(regionHeader);
        _regionCardFlag = FlagBadge("", 20);
        regionHeader.AddChild(_regionCardFlag);
        _regionCardTitle = Title("", 17, Ui.Colour("text.bright"));
        _regionCardTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        regionHeader.AddChild(_regionCardTitle);
        _regionCardCaption = Title("", 12, Ui.Colour("text.caption"));
        _regionCardCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        _regionCardCaption.ClipText = true;
        _regionCardCaption.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _regionCardCaption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _regionCardCaption.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        regionHeader.AddChild(_regionCardCaption);
        regionHeader.AddChild(ActionButton("Details", OpenSelectedRegionDossier, 0, 28));
        regionHeader.AddChild(ActionButton("×", CloseRegionCard, 32, 28));

        var columns = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 16);
        var factsScroll = new ScrollContainer { Name = "RegionFactsScroll", SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        regionRows.AddChild(factsScroll);
        factsScroll.AddChild(columns);

        var region = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        region.AddThemeConstantOverride("separation", 2);
        columns.AddChild(region);
        region.AddChild(Title("REGION", 11, Ui.Colour("text.caption")));
        _regionCardRegionRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _regionCardRegionRows.AddThemeConstantOverride("separation", 1);
        region.AddChild(_regionCardRegionRows);

        columns.AddChild(new VSeparator());

        var province = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        province.AddThemeConstantOverride("separation", 2);
        columns.AddChild(province);
        _regionCardProvinceTitle = Title("PROVINCE", 11, Ui.Colour("text.caption"));
        province.AddChild(_regionCardProvinceTitle);
        _regionCardProvinceRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _regionCardProvinceRows.AddThemeConstantOverride("separation", 1);
        province.AddChild(_regionCardProvinceRows);

        // And for a region of one's own, everything it can build, in a column that scrolls.
        columns.AddChild(new VSeparator());
        BuildRegionBuildList(columns);

        // The orders a region of your own takes. Somebody else's takes none, so the row goes
        // rather than standing there greyed out.
        _regionCardActions = new HBoxContainer();
        _regionCardActions.AddThemeConstantOverride("separation", 5);
        regionRows.AddChild(_regionCardActions);
        var raise = ActionButton("Raise army", RaiseRegionalArmy, 0, 28);
        raise.TooltipText = Ui.Tip(Tr("Choose a template, training region and paid recruitment batch."));
        _regionCardActions.AddChild(raise);
        _regionCardMoveCapital = ActionButton("Move the capital here", RegionCardMoveCapital, 0, 28);
        _regionCardActions.AddChild(_regionCardMoveCapital);

        _regionCardFeedback = Title("", 12, Ui.Colour("text.accent_dim"));
        _regionCardFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        regionRows.AddChild(_regionCardFeedback);

        // Beside the dossier they replace in the drawing order, not on top of everything the
        // HUD holds: the full-screen pages that open over the map have to cover these too.
        if (_sidePanel is not null && _sidePanel.GetParent() == _hudLayer)
        {
            _hudLayer.MoveChild(_regionCard, _sidePanel.GetIndex() + 1);
            _hudLayer.MoveChild(_countryCard, _sidePanel.GetIndex() + 1);
        }
    }

    // ------------------------------------------------------------------ showing

    /// <summary>Select what a left click landed on: its country on the left, its ground along the bottom.</summary>
    private void ShowMapSelection(GlobePick pick)
    {
        if (_campaign?.Player is null || pick.Nation is null) return;
        if (_resourceAtlasPanel is not null) _resourceAtlasPanel.Visible = false;
        EnsureMapSelectionPanels();
        if (_countryCard is null) return;
        if (_fleetPanel is { Visible: true }) CloseFleetPanel();

        _selectedNation = pick.Nation;
        _selectedState = pick.State;
        _selectedProvinceId = pick.ProvinceId;
        _mapSelectionNation = pick.Nation;
        _mapSelectionOpen = true;
        _countryCardStanding.Text = "";
        _regionCardFeedback.Text = "";
        _countryActionFeedback.Text = "";
        _pendingWarTarget = -1;

        // Outlined on the map the way a right click outlines it: somebody else's country is
        // the thing being asked about, and your own is the ground you are standing on.
        var foreign = pick.Nation != _campaign.Player;
        _globe?.HighlightNation(foreign ? pick.Nation.MapIndex : -1);
        _mapSelectionHighlights = foreign;

        ShowMapSelectionContent();
    }

    private void ShowMapSelectionContent()
    {
        if (_countryCard is null || _selectedNation is null) return;
        _mapSelectionNation = _selectedNation;
        _countryCard.Visible = true;
        ShowCountryCard(_selectedNation);
        _regionCard.Visible = _selectedState is not null || _selectedProvinceId > 0;
        if (_regionCard.Visible) ShowRegionCard();
        _mapSelectionRefreshedHour = _campaign.ElapsedHours;
        _mapSelectionSinceRefresh = 0d;
    }

    /// <summary>
    /// Keep the panels true while time runs, and out of the way of anything that takes their
    /// place. Called every frame from the interface cadence; cheap unless it has work to do.
    /// </summary>
    private void KeepMapSelection(double delta)
    {
        if (!_mapSelectionOpen || _countryCard is null) return;
        // The dossier, a division or a fleet takes the same corner of the screen. Whichever
        // opened last is the one being read.
        // And a country picked somewhere else - a right click, a row on the diplomacy screen -
        // is a different question: the buttons act on the selected country, so a panel still
        // showing the old one would be a panel whose buttons do something it does not say.
        if (!_hudLayer.Visible || _sidePanel.Visible || _armyPanel is { Visible: true } ||
            _fleetPanel is { Visible: true } || _selectedNation != _mapSelectionNation ||
            !_campaign.Nations.Contains(_selectedNation))
        {
            HideMapSelection(_openScreen != "Diplomacy");
            return;
        }
        _mapSelectionSinceRefresh += delta;
        if (_mapSelectionSinceRefresh < 0.5d || _campaign.ElapsedHours == _mapSelectionRefreshedHour) return;
        ShowMapSelectionContent();
    }

    private void HideMapSelection(bool dropHighlight)
    {
        _mapSelectionOpen = false;
        if (_countryCard is not null) _countryCard.Visible = false;
        if (_regionCard is not null) _regionCard.Visible = false;
        if (dropHighlight && _mapSelectionHighlights) _globe?.HighlightNation(-1);
        _mapSelectionHighlights = false;
    }

    /// <summary>The selection is over: both panels, the outline and the map mode's subject.</summary>
    private void CloseMapSelection()
    {
        HideMapSelection(true);
        ClearRegionSelection();
        _globe?.SetMapModeSubject(null);
        ReturnCountryActions();
    }

    /// <summary>Drop the ground selection and its shader tint, not just the cards that describe it.</summary>
    private void ClearRegionSelection()
    {
        _selectedState = null;
        _selectedProvinceId = -1;
        _globe?.ClearSelection();
    }

    private void CloseRegionCard()
    {
        if (_regionCard is not null) _regionCard.Visible = false;
        ClearRegionSelection();
    }

    // ------------------------------------------------------------------ the country

    private void ShowCountryCard(Nation nation)
    {
        var player = _campaign.Player;
        var own = nation == player;
        SetFlag(_countryCardFlag, nation.Tag, 40);
        _countryCardName.Text = nation.DisplayName;
        _countryCardCaption.Text = Simulation.Loc.Format("{0} · {1} · {2}", nation.Tag,
            Tr(PoliticsCore.GovernmentOf(nation).Name), Tr(PoliticsCore.IdeologyOf(nation).Name));
        BeginCountryFactsUpdate();
        ClearChildren(_countryCardLinks);
        RefreshCountryDossierTabs(nation);
        _countryCardRows.AddChild(BuildGrandStrategyLeaderHeader(nation, compact: true));
        if (_countryDossierTab == "Government")
        {
        CardRow(_countryCardRows, "Form of government", Tr(PoliticsCore.GovernmentOf(nation).Name),
            Tr(PoliticsCore.GovernmentOf(nation).Description));
        CardRow(_countryCardRows, "Ideology", Tr(PoliticsCore.IdeologyOf(nation).Name),
            Tr(PoliticsCore.IdeologyOf(nation).Description));
            CardRow(_countryCardRows, "State culture", Simulation.StateIdentity.Caption(_campaign, nation, false));
            CardRow(_countryCardRows, "State religion", Simulation.StateIdentity.Caption(_campaign, nation, true));
            foreach (var portfolio in Rulers.AllPortfolios)
                if (Rulers.ActiveMinister(nation, portfolio.Id) is { } min)
                    CardRow(_countryCardRows, portfolio.Name,
                        $"{Rulers.MinisterPersonName(nation, portfolio.Id)} ({Simulation.Loc.Format("{0} yrs", Rulers.MinisterPersonAge(_campaign, nation, portfolio.Id))} · {Tr(min.Name)})");
            if (own || CanInspectNation(nation.Id)) AddSpiritRows(_countryCardRows, nation);
        }

        var divisions = 0f;
        foreach (var army in _campaign.Armies)
            if (army.NationId == nation.Id) divisions += army.Divisions;

        if (own)
        {
            _countryCardStanding.Text = Tr("Your country");
            _countryCardStanding.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
            _countryCardStanding.TooltipText = "";
            if (_countryDossierTab == "Overview")
            {
            CardRow(_countryCardRows, CountryNames.CapitalTitle(_campaign, nation), CountryNames.Capital(nation, _campaign));
            CardRow(_countryCardRows, "GDP", Fmt.Money(nation.Gdp));
            CardRow(_countryCardRows, "Treasury", Fmt.Money(nation.Treasury), null,
                nation.Treasury < 0f ? "state.negative" : "text.bright");
            CardRow(_countryCardRows, "Population", Fmt.People(nation.Population),
                DemographicCycle.Explain(_campaign, nation));
            var (ownBirth, ownDeath, _, ownVitalStatus) = HouseholdEconomy.NationalVitalSummary(_campaign, nation);
            CardRow(_countryCardRows, "Demography",
                Simulation.Loc.Format("{0} · {1} old", Tr(ownVitalStatus), Fmt.Percent(nation.OldShare)),
                DemographicCycle.Explain(_campaign, nation),
                ownDeath > ownBirth ? "state.warning" : "text.bright");
            CardRow(_countryCardRows, "Stability", Fmt.Percent(nation.Stability), null,
                nation.Stability < 0.35f ? "state.negative" : nation.Stability < 0.55f ? "state.warning" : "text.bright");
            var wars = _campaign.Wars.Count(war => !war.Ended && war.Involves(nation.Id));
            AddWarSupportRow(nation, wars);
            CardRow(_countryCardRows, "Divisions", divisions.ToString("0.#"));
            if (wars > 0) CardRow(_countryCardRows, "Wars", wars.ToString("N0"), null, "state.negative");
            }
            if (_countryDossierTab == "Government")
            { AddResearchRows(_countryCardRows); AddStaffRows(_countryCardRows); }
            if (_countryDossierTab == "Relations") AddCountryCompactRelations(nation);
            _countryCardLinks.AddChild(ActionButton("Nation", () => OpenScreen("Nation"), 0, 28));
            _countryCardLinks.AddChild(ActionButton("Economy", () => OpenScreen("Economy"), 0, 28));
            _countryCardLinks.AddChild(ActionButton("Subjects", () => OpenSubjectsModal(), 0, 28));
            _countryCardLinks.AddChild(ActionButton("Release", () => OpenReleaseCountryModal(_selectedState), 0, 28));
            _countryCardLinks.AddChild(ActionButton("Cabinet", OpenPoliticalAppointments, 0, 28));
            ReturnCountryActions();
            return;
        }

        // ---- where we stand: the stance, the opinion, and under the cursor what it is made of.
        var relation = CampaignDiplomacy.ReadOrNeutral(_campaign, player.Id, nation.Id);
        var war = _campaign.Wars.Find(candidate =>
            !candidate.Ended && candidate.Involves(player.Id) && candidate.Involves(nation.Id));
        _countryCardStanding.Text = war is not null
            ? Simulation.Loc.Format("At war · war score {0:+0.0;-0.0;0.0}",
                war.AttackerId == player.Id ? war.WarScore : -war.WarScore)
            : Simulation.Loc.Format("{0} · their opinion of us {1:+0;-0;0}",
                Tr(DiplomaticVocabulary.StanceName(relation.Stance)), relation.Opinion);
        _countryCardStanding.AddThemeColorOverride("font_color", Ui.Colour(
            war is not null || relation.Opinion < -20 ? "state.negative" :
            relation.Opinion > 20 ? "state.positive" : "text.accent"));
        _countryCardStanding.TooltipText = Ui.Tip(string.Join("\n", relation.OpinionDrivers.Entries
            .OrderByDescending(entry => Math.Abs(entry.Value))
            .Take(5)
            .Select(entry => $"{Tr(entry.Source)} {entry.Value:+0.0;-0.0;0.0}")));

        if (_countryDossierTab == "Overview")
        {
        CardRow(_countryCardRows, CountryNames.CapitalTitle(_campaign, nation), CountryNames.Capital(nation, _campaign));
        if (CanInspectNation(nation.Id))
        {
            CardRow(_countryCardRows, "GDP", Fmt.Money(nation.Gdp));
            CardRow(_countryCardRows, "Population", Fmt.People(nation.Population),
                DemographicCycle.Explain(_campaign, nation));
            var (forBirth, forDeath, _, forVitalStatus) = HouseholdEconomy.NationalVitalSummary(_campaign, nation);
            CardRow(_countryCardRows, "Demography",
                Simulation.Loc.Format("{0} · {1} old", Tr(forVitalStatus), Fmt.Percent(nation.OldShare)),
                DemographicCycle.Explain(_campaign, nation),
                forDeath > forBirth ? "state.warning" : "text.bright");
            CardRow(_countryCardRows, "Stability", Fmt.Percent(nation.Stability));
            CardRow(_countryCardRows, "Divisions", divisions.ToString("0.#"));
            if (TechnologyCatalog.All.Count > 0)
                CardRow(_countryCardRows, "Technologies",
                    Simulation.Loc.Format("{0} of {1} known", nation.Technologies.Count, TechnologyCatalog.All.Count),
                    nation.Technologies.Count == 0 ? Tr("None yet.") :
                        string.Join("\n", nation.Technologies.Select(id => Tr(TechnologyCatalog.Of(id)?.Name ?? id))));
        }
        else
            CardRow(_countryCardRows, "Intelligence", Tr("Unknown (fog of war)"),
                Tr("What their economy and their army amount to is hidden until you have eyes on them."),
                "text.muted");
        }

        if (_countryDossierTab == "Relations")
        {
        var affinity = PoliticsCore.Affinity(_campaign, player.Id, nation.Id);
        CardRow(_countryCardRows, "Politics", affinity >= 0f ? Tr("close to ours") : Tr("unlike ours"),
            Tr("How alike two governments are moves what they make of each other, every week, " +
               "whether or not anything has ever happened between them."),
            affinity >= 0f ? "state.positive" : "state.negative");

        // What they have signed, in one line each and only when there is something to say.
        if (relation.Stance == DiplomaticStance.Allied)
            CardRow(_countryCardRows, "Alliance", Tr("signed with you"), null, "state.positive");
        if (InternationalOrganizations.DefencePartners(_campaign, player.Id).Contains(nation.Id))
            CardRow(_countryCardRows, "Collective defence pact", Tr("signed with you"), null, "state.positive");
        if (relation.NonAggressionPact)
            CardRow(_countryCardRows, "Non-aggression pact", Tr("signed with you"));
        if (relation.GuaranteedBy(player.Id))
            CardRow(_countryCardRows, "Your guarantee", Tr("you have promised to fight for them"), null, "state.info");
        if (relation.GuaranteedBy(nation.Id))
            CardRow(_countryCardRows, "Guaranteed by them", Tr("they have promised to fight for you"), null, "state.positive");
        var allies = AllianceSystem.AlliesOf(_campaign, nation);
        if (allies.Count > 0)
            CardRow(_countryCardRows, "Allied to", string.Join(", ", allies.Take(5).Select(ally => ally.Tag)) +
                                                   (allies.Count > 5 ? $" +{allies.Count - 5:N0}" : ""));
        var through = player.TradePartners.GetValueOrDefault(nation.Id);
        if (through > 0.0001f)
            CardRow(_countryCardRows, "Trade through them", Simulation.Loc.Format("{0} a week · {1:P0} of all your trade",
                Fmt.Money(through), player.TradeDependenceOn(nation.Id)));

        if (relation.OpinionDrivers.Entries.Count > 0)
        {
            var topDrivers = relation.OpinionDrivers.Entries
                .OrderByDescending(entry => Math.Abs(entry.Value))
                .Take(2);
            foreach (var driver in topDrivers)
            {
                var val = driver.Value >= 0 ? $"+{driver.Value:0}" : $"{driver.Value:0}";
                CardRow(_countryCardRows, TranslateOpinionDriver(driver.Source), val, null, driver.Value >= 0 ? "state.positive" : "state.negative");
            }
        }
        AddCountryCompactRelations(nation);
        }

        var full = ActionButton("Full diplomacy", () =>
        {
            _selectedNation = nation;
            OpenScreen("Diplomacy");
        }, 0, 28, false, SpriteCatalog.TextureOf("icon_diplomacy", "res://game/gfx/interface/icons/icon_diplomacy.png"));
        full.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        full.TooltipText = Ui.Tip(Simulation.Loc.Format(
            "Everything between you and {0}: what their opinion is made of, what you have signed and what it would take.",
            nation.DisplayName));
        _countryCardLinks.AddChild(full);
        if (nation.AnswersTo(player.Id))
            _countryCardLinks.AddChild(ActionButton("Manage subject", () => OpenSubjectsModal(nation), 0, 28, true));

        HostCountryActions(_countryCardActionsHost);
        RefreshCountryActions(nation);
    }

    /// <summary>
    /// Keep the facts panel as tall as the facts, and no taller.
    ///
    /// Two expanded panels in a column split the space evenly, and the part the facts do not need
    /// becomes empty card - that was a 240-pixel hole between "Full diplomacy" and the first action.
    /// Sizing the panel once, when the card is built, does not work either: Godot settles minimum
    /// sizes during the layout pass, so the rows are still zero-height labels at that moment and the
    /// measurement comes back as nothing. Rather than guess which frame the layout lands on, the
    /// measurement is taken every frame and the answer is written only when it changes - a settled
    /// card therefore costs one walk of a small tree and no writes at all.
    /// </summary>
    private void SyncCountryFactsHeight()
    {
        if (_countryFactsScroll is null || !_countryFactsScroll.IsVisibleInTree()) return;
        // Facts and actions are separate pages, not two competing scroll panes.
        // A minimum based on all facts would force the whole card off a 720p screen.
        if (_countryFactsScroll.CustomMinimumSize != Vector2.Zero)
            _countryFactsScroll.CustomMinimumSize = Vector2.Zero;
    }

    /// <summary>
    /// Put the diplomatic buttons in the panel being read. There is one set of them, with one
    /// set of rules for what each may do; it moves between the dossier and this panel rather
    /// than existing twice and disagreeing.
    /// </summary>
    private void HostCountryActions(Node host)
    {
        if (_countryActions is null || host is null) return;
        var parent = _countryActions.GetParent();
        if (parent == host) return;
        if (_countryActionsHome is null)
        {
            _countryActionsHome = parent;
            _countryActionsHomeIndex = _countryActions.GetIndex();
        }
        _countryActions.Reparent(host, false);
        if (host == _countryActionsHome)
            host.MoveChild(_countryActions, Math.Min(_countryActionsHomeIndex, host.GetChildCount() - 1));
    }

    private void ReturnCountryActions()
    {
        if (_countryActionsHome is not null) HostCountryActions(_countryActionsHome);
    }

    /// <summary>After a diplomatic action: redraw whichever of the two views the player is reading.</summary>
    private void RefreshCountryViews(string location)
    {
        if (_selectedNation is null) return;
        if (MapSelectionShowing) ShowCountryCard(_selectedNation);
        else ShowCountryInteraction(_selectedNation, location);
    }

    // ------------------------------------------------------------------ the region and the province

    private void ShowRegionCard()
    {
        var player = _campaign.Player;
        var state = _selectedState;
        var topology = _campaign.ProvinceMap;
        ClearChildren(_regionCardRegionRows);
        ClearChildren(_regionCardProvinceRows);

        var owner = state is null ? null : _campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex);
        var owned = state is not null && (state.NationMapIndex == player.MapIndex ||
                    (state.HeldById == player.Id && HeldRegions.Administered(_campaign, state)));
        _regionCardFlag.Visible = owner is not null;
        if (owner is not null) SetFlag(_regionCardFlag, owner.Tag, 20);
        _regionCardTitle.Text = state?.DisplayName ?? Tr("Unmapped land");
        var isCapital = owner is not null && state.MapIndex == owner.CapitalStateMapIndex;
        _regionCardCaption.Text = state is null ? "" :
            RegionOfCaption(state, owner) + (isCapital ? " · " + Tr("the capital") : "");

        // ---- the region: the same fog rule as the dossier, so the two never disagree.
        var visible = state is not null && (owner is null || CanInspectNation(owner.Id) || PlayerHoldsMostOf(state));
        if (state is not null)
        {
            CardRow(_regionCardRegionRows, "Population", Fmt.People(state.Population), Fmt.PeopleExact(state.Population));
            CardRow(_regionCardRegionRows, "GDP (estimate)", Fmt.Money(RegionalStatistics.EstimatedGdp(_campaign, state)),
                Tr(RegionalStatistics.GdpExplanation));
            // A half-state: owned by one country, held by another by lease or by troops without a war.
            if (LandArrangementSystem.Covering(_campaign, state.MapIndex) is { } arranged)
            {
                var holder = _campaign.NationById(arranged.HolderId)?.DisplayName ?? "?";
                CardRow(_regionCardRegionRows, "Held by",
                    arranged.Kind == LandArrangementSystem.Lease
                        ? Simulation.Loc.Format("{0}, on lease", holder)
                        : Simulation.Loc.Format("{0} troops, no war", holder),
                    LandArrangementSystem.Describe(_campaign, state), "state.warning");
            }
            // Held ground worked by its holder (HeldRegions): who runs it and how well yet.
            else if (state.HeldById >= 0 && _campaign.NationById(state.HeldById) is { } administrator)
                CardRow(_regionCardRegionRows, "Administered by",
                    HeldRegions.Administered(_campaign, state)
                        ? Simulation.Loc.Format("{0} · {1} of its output", administrator.DisplayName, Fmt.Percent(HeldRegions.Efficiency(_campaign, state)))
                        : Simulation.Loc.Format("{0} · taking over", administrator.DisplayName),
                    Simulation.Loc.Format("Held by {0} since {1}: its output, workers and taxes go to the holder, at the efficiency of an administration that grows from occupation to integration over a year. The title stays with {2} until a treaty or a vote under the troops.",
                        administrator.DisplayName, LandArrangementSystem.DateOfDay(_campaign, state.HeldSinceDay), owner?.DisplayName ?? "?"),
                    "state.warning");
        }
        if (state is null) { }
        else if (!visible)
            CardRow(_regionCardRegionRows, "Intelligence", Tr("Unknown (fog of war)"),
                Tr("Fog of war hides internal conditions, industry and military intelligence, not public population and GDP estimates."),
                "text.muted");
        else
        {
            CardRow(_regionCardRegionRows, "Stability", Fmt.Percent(state.Stability), null,
                state.Stability < 0.35f ? "state.negative" : state.Stability < 0.55f ? "state.warning" : "state.positive");
            CardRow(_regionCardRegionRows, "Radicalism", Fmt.Percent(state.Radicalism), RadicalismTooltip(state),
                state.Radicalism > 0.45f ? "state.negative" : state.Radicalism > 0.25f ? "state.warning" : "state.positive");
            var cultures = EconomySystem.Cultures(state);
            if (cultures.Count > 0)
                CardRow(_regionCardRegionRows, "Largest people", DemographyCatalog.CultureName(cultures[0].Culture));
            var foreign = EconomySystem.ForeignShare(state, owner?.Tag ?? "");
            if (foreign > 0.001f)
                CardRow(_regionCardRegionRows, "Minorities", Fmt.Percent(foreign), null,
                    foreign > 0.5f ? "state.negative" : "state.warning");
            var buildings = BuildingCatalog.All
                .Select(kind => (Kind: kind, Count: state.Buildings.GetValueOrDefault(kind)))
                .Where(entry => entry.Count > 0)
                .OrderByDescending(entry => entry.Count)
                .Take(3)
                .Select(entry => $"{Tr(BuildingCatalog.Get(entry.Kind).Name)} {entry.Count:N0}")
                .ToList();
            CardRow(_regionCardRegionRows, "Buildings", buildings.Count == 0 ? Tr("none") : string.Join(" · ", buildings));
            var building = _campaign.Construction.Count(project => project.StateMapIndex == state.MapIndex);
            if (building > 0) CardRow(_regionCardRegionRows, "Under construction", building.ToString("N0"));
        }

        // ---- the province under the cursor.
        var province = _selectedProvinceId;
        if (topology is null || province <= 0 || province > topology.ProvinceCount)
        {
            _regionCardProvinceTitle.Text = Tr("PROVINCE");
            CardRow(_regionCardProvinceRows, "Province", Tr("The province map is still being prepared."));
        }
        else
        {
            _regionCardProvinceTitle.Text = Simulation.Loc.Format("PROVINCE #{0:N0}", province);
            var terrain = topology.TerrainAt(province);
            CardRow(_regionCardProvinceRows, "Terrain", Tr(terrain.Name),
                Simulation.Loc.Format("Movement {0:P0} · attack {1:P0} · defence {2:P0} · supply {3:P0}",
                    terrain.MovementSpeed, terrain.Attack, terrain.Defense, terrain.Supply));
            var provinceOwner = _campaign.Nations.Find(nation => nation.MapIndex == topology.ProvinceToNation[province]);
            var controllerIndex = _campaign.ProvinceController?[province] ?? topology.ProvinceToNation[province];
            var controller = _campaign.Nations.Find(nation => nation.MapIndex == controllerIndex);
            CardRow(_regionCardProvinceRows, "Controller", controller?.DisplayName ?? Tr("unknown"),
                Tr("Who holds it right now. An army takes control; a treaty takes ownership."),
                controller == provinceOwner ? "text.bright" : "state.warning");
            if (controller != provinceOwner && controller is not null && CanInspectNation(controller.Id))
            {
                CardRow(_regionCardProvinceRows, "Occupation", Fmt.Percent(_campaign.ProvinceOccupation?[province] ?? 0f),
                    Tr("How firmly the occupier holds it. Above the secure threshold it can be kept at a peace."));
                var slip = Simulation.OccupationResistance.WeeklySlip(_campaign, province);
                if (slip > 0f)
                    CardRow(_regionCardProvinceRows, "Resistance",
                        Simulation.Loc.Format("slipping {0:P1} of the hold a week",
                            slip / MathF.Max(0.0001f, _campaign.ProvinceOccupation?[province] ?? 0f)),
                        Tr("Partisans and quiet non-cooperation take the occupation down week by week. A garrison slows them; the occupier's own people in the region slow them further."));
            }
            var crossings = topology.Neighbors[province].Count(next => topology.IsRiverCrossing(province, next));
            CardRow(_regionCardProvinceRows, "River crossings", crossings.ToString("N0"),
                crossings == 0
                    ? Tr("No river runs between this province and its neighbours.")
                    : Tr("Attacking across a river costs the attacker dearly; a major one costs more."));
            if (topology.HasLakeAccess(province)) CardRow(_regionCardProvinceRows, "Lake shore", Tr("yes"));
            var seen = _campaign.Armies.Count(army => army.ProvinceId == province && CampaignIntel.PlayerCanSeeArmy(_campaign, army));
            CardRow(_regionCardProvinceRows, "Divisions here", seen.ToString("N0"),
                Tr("Formations you can see standing in this province. Foreign forces are hidden by fog of war."));
            // A count of counters is not what a player needs before ordering an attack: the
            // question is how much fight the foreign ones have left. Only formations this
            // country can actually see are averaged, and the player's own divisions are
            // left out of it, so a friendly garrison cannot make an enemy look healthy.
            var observable = Simulation.MilitaryConditionReadout.ForPlayerInProvince(
                _campaign, province, foreignOnly: true);
            if (observable.Known && observable.Formations > 0)
            {
                var observed = Tr("Division-weighted average of the foreign formations you can see here. Hidden reserves are not counted and no order or route is revealed.");
                // The same lines the roster and the battle card already call spent: a
                // formation below 35% organisation or 45% equipment is not holding much.
                CardRow(_regionCardProvinceRows, "Observed organisation",
                    Fmt.Percent(observable.Organisation), observed,
                    observable.Organisation < .35f ? "state.warning" : "text.bright");
                CardRow(_regionCardProvinceRows, "Observed equipment",
                    Fmt.Percent(observable.Equipment), observed,
                    observable.Equipment < .45f ? "state.warning" : "text.bright");
            }
        }
        // The movements with their headquarters or a following here, each a click from its panel.
        if (state is not null) AddMovementsToRegionCard(_regionCardProvinceRows, state);

        _regionCardActions.Visible = owned;
        if (owned) RefreshRegionCardCapitalButton();
        ShowRegionBuildList(state, owned);
        LayoutMapSelectionCards();
    }

    private void RefreshRegionCardCapitalButton()
    {
        if (_regionCardMoveCapital is null || _selectedState is null) return;
        var refusal = CapitalCore.Refusal(_campaign, _campaign.Player, _selectedState);
        _regionCardMoveCapital.Disabled = refusal.Length > 0;
        _regionCardMoveCapital.TooltipText = Ui.Tip(refusal.Length > 0
            ? Tr(refusal)
            : Simulation.Loc.Format("Move the government to {0}. It costs {1} from the treasury.",
                _selectedState.DisplayName, Fmt.Money(CapitalCore.Cost(_campaign, _campaign.Player))));
    }

    private void RegionCardBuild(BuildingKind kind, int count = 1)
    {
        QueueRegionalBuilding(kind, count);
        _regionCardFeedback.Text = _regionActionFeedback.Text;
    }

    private void RegionCardMoveCapital()
    {
        MoveCapitalHere();
        _regionCardFeedback.Text = _regionActionFeedback.Text;
    }

    /// <summary>After an order to a region: redraw whichever of the two views the player is reading.</summary>
    private void RefreshRegionViews()
    {
        if (MapSelectionShowing) ShowMapSelectionContent();
        else ShowRegionOverview();
    }

    /// <summary>The whole region dossier - people, economy, deposits, construction - in the side panel.</summary>
    private void OpenSelectedRegionDossier()
    {
        if (_selectedState is null || _selectedNation is null) return;
        ForgetDossierReturn();
        var owner = SelectedRegionOwner() ?? _selectedNation;
        HideMapSelection(true);
        ReturnCountryActions();
        _openScreen = "";
        _sidePanel.Visible = true;
        SetArmyPanelVisible(false);
        _countryActions.Visible = false;
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
        _battleDetailControls.Visible = false;
        _militarySpending.Visible = false;
        _regionControls.Visible = true;
        ShowSideFlag(owner.Tag);
        ShowRegionOverview();
    }

    // ------------------------------------------------------------------ rows

    /// <summary>
    /// The percent, then one fixed line of the facts already computed behind it.
    /// The line is clipped. The tooltip carries every sentence.
    /// </summary>
    private void AddWarSupportRow(Nation nation, int wars)
    {
        var notice = Simulation.WarWeariness.BesideWarSupport(_campaign, nation);
        var tip = EconomySystem.DescribeWarSupport(_campaign, nation);
        if (notice.Text.Length > 0)
            tip = tip.Length == 0 ? notice.Text : tip + "\n" + notice.Text;
        var tone = wars == 0 ? "text.muted"
            : nation.WarSupport < 0.25f ? "state.negative"
            : nation.WarSupport < 0.45f ? "state.warning" : "text.bright";
        if (notice.Level >= 2) tone = "state.negative";
        else if (notice.Level >= 1 && (tone == "text.bright" || tone == "text.muted")) tone = "state.warning";
        CardRow(_countryCardRows, "War support", Fmt.Percent(nation.WarSupport), tip, tone);
        if (notice.Text.Length == 0) return;
        CardRow(_countryCardRows, "Rear", notice.Text.Replace("\n", " · "), notice.Text,
            notice.Level >= 2 ? "state.negative" : "state.warning", true);
    }

    private void CardRow(VBoxContainer parent, string caption, string value, string tooltip = null,
        string valueToken = "text.bright", bool clipValue = false)
    {
        if (parent == _countryCardRows)
        { RepaintCountryFact(caption, value, tooltip, valueToken, clipValue); return; }
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        row.CustomMinimumSize = new Vector2(0, 21);
        if (!string.IsNullOrEmpty(tooltip))
        {
            row.TooltipText = Ui.Tip(tooltip);
            row.MouseFilter = Control.MouseFilterEnum.Stop;
        }
        parent.AddChild(row);
        var label = Title(caption, 13, Ui.Colour("text.body_soft"));
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.TooltipText = Tr(caption);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(label);
        // Room for a lone digit: a "0" is narrower than anything the layout audit accepts as shown.
        if (clipValue) label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        var figure = new Label
        {
            Text = value ?? "",
            HorizontalAlignment = clipValue ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            CustomMinimumSize = new Vector2(24, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = clipValue ? TextServer.AutowrapMode.Off : TextServer.AutowrapMode.WordSmart,
            ClipText = clipValue,
            TextOverrunBehavior = clipValue
                ? TextServer.OverrunBehavior.TrimEllipsis
                : TextServer.OverrunBehavior.NoTrimming,
            TooltipText = value ?? ""
        };
        figure.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.body", 13));
        figure.AddThemeColorOverride("font_color", Ui.Colour(valueToken));
        figure.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(figure);
    }

    private static void ClearChildren(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>
    /// "Oblast of Russia", "Province of China" - unless the country's own name already says the
    /// kind: a Russian republic that became a state was "Republic of Republic of Dagestan".
    /// Then it is a region of that country.
    /// </summary>
    private string RegionOfCaption(ScenarioState state, Nation owner)
    {
        var kind = Tr(state.Kind);
        var country = owner?.DisplayName ?? Tr("an unknown state");
        if (owner is not null && kind.Length > 0 && country.Contains(kind, StringComparison.OrdinalIgnoreCase))
            kind = Tr("Region");
        return Simulation.Loc.Format("{0} of {1}", kind, country);
    }
}
