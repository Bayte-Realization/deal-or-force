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

        // ---- the country, on the left, as tall as what it holds.
        _countryCard = TexturedPanel(_hudLayer, PanelSurface.Command);
        _countryCard.Name = "CountryCard";
        Ui.Place(_countryCard, "hud.country_card");
        _countryCard.Visible = false;
        var rows = PanelRows(_countryCard, 18, 14, 7);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 9);
        rows.AddChild(header);
        _countryCardFlag = FlagBadge("", 30);
        header.AddChild(_countryCardFlag);
        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        names.AddThemeConstantOverride("separation", 0);
        header.AddChild(names);
        _countryCardName = Title("", 20, Ui.Colour("text.bright"));
        _countryCardName.AutowrapMode = TextServer.AutowrapMode.Off;
        _countryCardName.ClipText = true;
        _countryCardName.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        names.AddChild(_countryCardName);
        _countryCardCaption = Title("", 12, Ui.Colour("text.caption"));
        _countryCardCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        _countryCardCaption.ClipText = true;
        _countryCardCaption.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        names.AddChild(_countryCardCaption);
        header.AddChild(ActionButton("×", CloseMapSelection, 34, 32));

        _countryCardStanding = Title("", 14, Ui.Colour("text.accent"));
        _countryCardStanding.MouseFilter = Control.MouseFilterEnum.Stop;
        rows.AddChild(_countryCardStanding);

        _countryCardRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _countryCardRows.AddThemeConstantOverride("separation", 2);
        rows.AddChild(_countryCardRows);

        _countryCardLinks = new HBoxContainer();
        _countryCardLinks.AddThemeConstantOverride("separation", 5);
        rows.AddChild(_countryCardLinks);

        _countryCardActionsHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rows.AddChild(_countryCardActionsHost);

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
        regionRows.AddChild(columns);

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
        _globe?.SetMapModeSubject(null);
        ReturnCountryActions();
    }

    private void CloseRegionCard()
    {
        if (_regionCard is not null) _regionCard.Visible = false;
    }

    // ------------------------------------------------------------------ the country

    private void ShowCountryCard(Nation nation)
    {
        var player = _campaign.Player;
        var own = nation == player;
        SetFlag(_countryCardFlag, nation.Tag, 30);
        _countryCardName.Text = nation.DisplayName;
        _countryCardCaption.Text = Simulation.Loc.Format("{0} · {1} · {2}", nation.Tag,
            Tr(PoliticsCore.GovernmentOf(nation).Name), Tr(PoliticsCore.IdeologyOf(nation).Name));
        ClearChildren(_countryCardRows);
        ClearChildren(_countryCardLinks);

        var divisions = 0f;
        foreach (var army in _campaign.Armies)
            if (army.NationId == nation.Id) divisions += army.Divisions;

        if (own)
        {
            _countryCardStanding.Text = Tr("Your country");
            _countryCardStanding.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
            _countryCardStanding.TooltipText = "";
            CardRow(_countryCardRows, "Capital", nation.CapitalName);
            CardRow(_countryCardRows, "GDP", Fmt.Money(nation.Gdp));
            CardRow(_countryCardRows, "Treasury", Fmt.Money(nation.Treasury), null,
                nation.Treasury < 0f ? "state.negative" : "text.bright");
            CardRow(_countryCardRows, "Population", Fmt.People(nation.Population));
            CardRow(_countryCardRows, "Stability", Fmt.Percent(nation.Stability), null,
                nation.Stability < 0.35f ? "state.negative" : nation.Stability < 0.55f ? "state.warning" : "text.bright");
            CardRow(_countryCardRows, "War support", Fmt.Percent(nation.WarSupport));
            CardRow(_countryCardRows, "Divisions", divisions.ToString("0.0"));
            var wars = _campaign.Wars.Count(war => !war.Ended && war.Involves(nation.Id));
            if (wars > 0) CardRow(_countryCardRows, "Wars", wars.ToString("N0"), null, "state.negative");
            AddResearchRows(_countryCardRows);
            AddStaffRows(_countryCardRows);
            _countryCardLinks.AddChild(ActionButton("Nation", () => OpenScreen("Nation"), 0, 28));
            _countryCardLinks.AddChild(ActionButton("Economy", () => OpenScreen("Economy"), 0, 28));
            _countryCardLinks.AddChild(ActionButton("Military", () => OpenScreen("Military"), 0, 28));
            ReturnCountryActions();
            return;
        }

        // ---- where we stand: the stance, the opinion, and under the cursor what it is made of.
        var relation = CampaignDiplomacy.Get(_campaign, player.Id, nation.Id);
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

        CardRow(_countryCardRows, "Capital", nation.CapitalName);
        if (CanInspectNation(nation.Id))
        {
            CardRow(_countryCardRows, "GDP", Fmt.Money(nation.Gdp));
            CardRow(_countryCardRows, "Population", Fmt.People(nation.Population));
            CardRow(_countryCardRows, "Stability", Fmt.Percent(nation.Stability));
            CardRow(_countryCardRows, "Divisions", divisions.ToString("0.0"));
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

        var affinity = PoliticsCore.Affinity(_campaign, player.Id, nation.Id);
        CardRow(_countryCardRows, "Politics", affinity >= 0f ? Tr("close to ours") : Tr("unlike ours"),
            Tr("How alike two governments are moves what they make of each other, every week, " +
               "whether or not anything has ever happened between them."),
            affinity >= 0f ? "state.positive" : "state.negative");

        // What they have signed, in one line each and only when there is something to say.
        if (relation.Stance == DiplomaticStance.Allied)
            CardRow(_countryCardRows, "Alliance", Tr("signed with you"), null, "state.positive");
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
        var overlord = SubjectSystem.OverlordOf(_campaign, nation);
        if (overlord is not null)
            CardRow(_countryCardRows, "Answers to", overlord.DisplayName);
        var through = player.TradePartners.GetValueOrDefault(nation.Id);
        if (through > 0.0001f)
            CardRow(_countryCardRows, "Trade through them", Simulation.Loc.Format("{0} a week · {1:P0} of all your trade",
                Fmt.Money(through), player.TradeDependenceOn(nation.Id)));

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

        HostCountryActions(_countryCardActionsHost);
        RefreshCountryActions(nation);
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
        var owned = state is not null && state.NationMapIndex == player.MapIndex;
        _regionCardFlag.Visible = owner is not null;
        if (owner is not null) SetFlag(_regionCardFlag, owner.Tag, 20);
        _regionCardTitle.Text = state?.DisplayName ?? Tr("Unmapped land");
        var isCapital = owner is not null && state.MapIndex == owner.CapitalStateMapIndex;
        _regionCardCaption.Text = state is null ? "" :
            Simulation.Loc.Format("{0} of {1}", Tr(state.Kind), owner?.DisplayName ?? Tr("an unknown state")) +
            (isCapital ? " · " + Tr("the capital") : "");

        // ---- the region: the same fog rule as the dossier, so the two never disagree.
        var visible = state is not null && (owner is null || CanInspectNation(owner.Id) || PlayerHoldsMostOf(state));
        if (state is null) { }
        else if (!visible)
            CardRow(_regionCardRegionRows, "Intelligence", Tr("Unknown (fog of war)"),
                Tr("Fog of war — regional population, industry and military intelligence are unavailable."),
                "text.muted");
        else
        {
            CardRow(_regionCardRegionRows, "Population", Fmt.People(state.Population), Fmt.PeopleExact(state.Population));
            CardRow(_regionCardRegionRows, "Stability", Fmt.Percent(state.Stability), null,
                state.Stability < 0.35f ? "state.negative" : state.Stability < 0.55f ? "state.warning" : "state.positive");
            CardRow(_regionCardRegionRows, "Radicalism", Fmt.Percent(state.Radicalism), null,
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
                CardRow(_regionCardProvinceRows, "Occupation", Fmt.Percent(_campaign.ProvinceOccupation?[province] ?? 0f),
                    Tr("How firmly the occupier holds it. Above the secure threshold it can be kept at a peace."));
            var crossings = topology.Neighbors[province].Count(next => topology.IsRiverCrossing(province, next));
            CardRow(_regionCardProvinceRows, "River crossings", crossings.ToString("N0"),
                crossings == 0
                    ? Tr("No river runs between this province and its neighbours.")
                    : Tr("Attacking across a river costs the attacker dearly; a major one costs more."));
            if (topology.HasLakeAccess(province)) CardRow(_regionCardProvinceRows, "Lake shore", Tr("yes"));
            var seen = _campaign.Armies.Count(army => army.ProvinceId == province && CampaignIntel.PlayerCanSeeArmy(_campaign, army));
            CardRow(_regionCardProvinceRows, "Divisions here", seen.ToString("N0"),
                Tr("Formations you can see standing in this province. Foreign forces are hidden by fog of war."));
        }

        _regionCardActions.Visible = owned;
        if (owned) RefreshRegionCardCapitalButton();
        ShowRegionBuildList(state, owned);
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

    private void RegionCardBuild(BuildingKind kind)
    {
        QueueRegionalBuilding(kind);
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

    private void CardRow(VBoxContainer parent, string caption, string value, string tooltip = null,
        string valueToken = "text.bright")
    {
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
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.AddChild(label);
        // Room for a lone digit: a "0" is narrower than anything the layout audit accepts as shown.
        var figure = new Label
        {
            Text = value ?? "", HorizontalAlignment = HorizontalAlignment.Right,
            CustomMinimumSize = new Vector2(24, 0)
        };
        figure.AddThemeFontSizeOverride("font_size", 13);
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
}
