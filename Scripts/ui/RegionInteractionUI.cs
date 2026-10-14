using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _regionControls = null!;
    private Label _regionActionFeedback = null!;
    private int _selectedProvinceId = -1;

    /// <summary>
    /// Everything that is only worth showing for a region the player owns: the heading, the
    /// two rows of orders and the line that answers them. Held as one node so that a foreign
    /// region hides all of it at once rather than greying six buttons out.
    /// </summary>
    private VBoxContainer _ownedRegionActions = null!;

    /// <summary>And what a foreign region offers instead, which is its owner.</summary>
    private Button _foreignRegionDiplomacy = null!;

    /// <summary>Move the government here. Its tooltip is the price, or why not.</summary>
    private Button _moveCapitalButton = null!;

    private void BuildRegionInteractionControls(VBoxContainer parent)
    {
        _regionControls = new VBoxContainer { Visible = false };
        _regionControls.AddThemeConstantOverride("separation", 6);
        parent.AddChild(_regionControls);

        // The one thing worth doing about somebody else's region: it has an owner, and the
        // owner can be talked to. Shown only when the region is not the player's, in the
        // space the six disabled buttons used to occupy.
        _foreignRegionDiplomacy = ActionButton("", OpenSelectedRegionDiplomacy, 0, 32, true,
            SpriteCatalog.TextureOf("icon_diplomacy", "res://game/gfx/interface/icons/icon_diplomacy.png"));
        _foreignRegionDiplomacy.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _foreignRegionDiplomacy.Visible = false;
        _regionControls.AddChild(_foreignRegionDiplomacy);

        _ownedRegionActions = new VBoxContainer();
        _ownedRegionActions.AddThemeConstantOverride("separation", 6);
        _regionControls.AddChild(_ownedRegionActions);
        _ownedRegionActions.AddChild(Title("REGION & PROVINCE", 12, Ui.Colour("text.caption")));

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 5);
        _ownedRegionActions.AddChild(actions);
        var develop = ActionButton("Develop", () => QueueRegionalBuilding(BuildingKind.Infrastructure), 92, 31, true);
        develop.TooltipText = Ui.Tip(Tr("Queue infrastructure in this region. Construction uses the national treasury, materials and construction capacity."));
        actions.AddChild(develop);
        var industry = ActionButton("Industry", () => QueueRegionalBuilding(BuildingKind.CivilianIndustry), 92, 31);
        industry.TooltipText = Ui.Tip(Tr("Queue civilian industry in this region."));
        actions.AddChild(industry);
        var raise = ActionButton("Raise army", RaiseRegionalArmy, 105, 31);
        raise.TooltipText = Ui.Tip(Tr("Choose a template, training region and paid recruitment batch."));
        actions.AddChild(raise);

        var strategic = new HBoxContainer();
        strategic.AddThemeConstantOverride("separation", 5);
        _ownedRegionActions.AddChild(strategic);
        strategic.AddChild(ActionButton("Extract", () => QueueRegionalBuilding(BuildingKind.ExtractionIndustry), 92, 31));
        strategic.AddChild(ActionButton("Power", () => QueueRegionalBuilding(BuildingKind.PowerPlant), 92, 31));
        strategic.AddChild(ActionButton("Arms", () => QueueRegionalBuilding(BuildingKind.ArmsIndustry), 105, 31));

        _moveCapitalButton = ActionButton("Move the capital here", MoveCapitalHere, 0, 31);
        _moveCapitalButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _ownedRegionActions.AddChild(_moveCapitalButton);

        _regionActionFeedback = Title("", 12, Ui.Colour("text.accent_dim"));
        _regionActionFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _ownedRegionActions.AddChild(_regionActionFeedback);
    }

    private void HideRegionInteractionControls()
    {
        if (_regionControls is not null) _regionControls.Visible = false;
    }

    /// <summary>
    /// Everything a region is, in rows a player can read at a glance and interrogate one at
    /// a time. The blob of text this replaced held most of the same numbers and none of the
    /// meaning: a figure with no explanation is detail a newcomer skips and a Paradox
    /// player has to guess at.
    /// </summary>
    private void ShowRegionOverview()
    {
        if (_selectedState is null || _selectedNation is null) return;
        PrepareDossierTabs("Region");
        _dossierTab = "Overview";
        ApplyDossierTab();
        if (HideForeignRegionDetails()) return;
        _sideTitle.Text = _selectedState.DisplayName;
        _regionControls.Visible = true;
        RefreshMoveCapitalButton();
        ClearDetail();

        var owner = _campaign.Nations.Find(nation => nation.MapIndex == _selectedState.NationMapIndex);
        var owned = _selectedState.NationMapIndex == _campaign.Player.MapIndex;
        _sideBody.Text = Simulation.Loc.Format("{0} of {1}",
                             Tr(_selectedState.Kind), owner?.DisplayName ?? Tr("an unknown state")) +
                         (owned ? "" : Tr(" · foreign ground, read-only"));

        DetailHeading("PEOPLE");
        DetailRow("Population", Fmt.People(_selectedState.Population), Fmt.PeopleExact(_selectedState.Population));
        DetailRow("Literacy", Fmt.Percent(_selectedState.Literacy),
            "How much of the region can read. Literacy raises productivity and assimilates minorities faster.");
        DetailRow("Standard of living", Fmt.Percent(_selectedState.StandardOfLiving),
            "How well people live here. Below the national average it feeds radicalism; above it, calm.");
        DetailRow("Urbanisation", Fmt.Percent(_selectedState.Urbanization),
            "Share living in towns. Urban regions industrialise more easily and riot more readily.");
        DetailRow("Radicalism", Fmt.Percent(_selectedState.Radicalism),
            RadicalismTooltip(_selectedState),
            _selectedState.Radicalism > 0.45f ? "state.negative" :
            _selectedState.Radicalism > 0.25f ? "state.warning" : "state.positive");

        // A region has a settledness of its own now, and the country's is what these add
        // up to - the capital weighing far above its size. It belongs next to the things
        // that decide it rather than only as a national figure on another screen.
        var isCapital = owner is not null && _selectedState.MapIndex == owner.CapitalStateMapIndex;
        DetailRow("Stability", Fmt.Percent(_selectedState.Stability),
            isCapital
                ? Tr("How settled this region is - and it is the capital, so it decides far more "
                     + "of the country's stability than its size alone would earn it.")
                : Tr("How settled this region is: what it lives on, what it thinks, who its "
                     + "people are, and whose soldiers are standing in it."),
            _selectedState.Stability < 0.35f ? "state.negative" :
            _selectedState.Stability < 0.55f ? "state.warning" : "state.positive");

        var cultures = EconomySystem.Cultures(_selectedState);
        if (cultures.Count > 0)
            DetailRow("Largest people", DemographyCatalog.CultureName(cultures[0].Culture),
                Simulation.Loc.Format("{0} of {1}.",
                    Fmt.PeopleExact(cultures[0].Size), Fmt.PeopleExact(_selectedState.Population)) + " " +
                Tr("The whole table is under Population."));
        var faiths = EconomySystem.Religions(_selectedState);
        if (faiths.Count > 0)
            DetailRow("Largest faith", DemographyCatalog.ReligionName(faiths[0].Religion),
                Simulation.Loc.Format("{0:P0} of the region.",
                    faiths[0].Size / (double)Math.Max(1, _selectedState.Population)));

        var foreign = EconomySystem.ForeignShare(_selectedState, owner?.Tag ?? "");
        if (foreign > 0.001f)
        {
            var largest = cultures.FirstOrDefault(entry =>
                !DemographyCatalog.IsPrimary(owner?.Tag ?? "", entry.Culture));
            DetailRow("Minorities", Fmt.Percent(foreign),
                "People here who are not of a nation this state is for. They are more restive than " +
                "their neighbours and assimilate only slowly — the standing cost of a border that moved.",
                foreign > 0.5f ? "state.negative" : "state.warning");
            if (largest.Size > 0)
                DetailRow("Largest minority",
                    $"{DemographyCatalog.CultureName(largest.Culture)} {Fmt.People(largest.Size)}",
                    "The biggest people living under a flag that is not its own.");
        }

        DetailHeading("ECONOMY");
        DetailRow("GDP (estimate)", Fmt.Money(RegionalStatistics.EstimatedGdp(_campaign, _selectedState)),
            Tr(RegionalStatistics.GdpExplanation));
        var buildings = BuildingCatalog.All
            .Select(kind => (Kind: kind, Count: _selectedState.Buildings.GetValueOrDefault(kind)))
            .Where(entry => entry.Count > 0)
            .OrderByDescending(entry => entry.Count)
            .ToList();
        if (buildings.Count == 0) DetailRow("Buildings", "none", "Nothing has been built here yet.");
        foreach (var entry in buildings.Take(8))
        {
            var definition = BuildingCatalog.Get(entry.Kind);
            DetailRow(definition.Name, entry.Count.ToString("N0"), definition.Description);
        }

        var deposits = _selectedState.ResourceDeposits
            .Where(entry => entry.Value > 0.05f)
            .OrderByDescending(entry => entry.Value)
            .Take(5)
            .ToList();
        if (deposits.Count > 0)
        {
            DetailHeading("SURVEYED DEPOSITS");
            foreach (var entry in deposits)
                DetailRow(CommodityCatalog.Get(entry.Key).Name, Fmt.Number(entry.Value),
                    "Geological potential. Extraction buildings turn it into goods the market can price.");
        }

        var queue = _campaign.Construction
            .Where(project => project.StateMapIndex == _selectedState.MapIndex)
            .OrderBy(project => project.Id)
            .ToList();
        if (queue.Count > 0)
        {
            DetailHeading("UNDER CONSTRUCTION");
            foreach (var project in queue.Take(5))
                DetailRow(BuildingCatalog.Get(project.Building).Name, Fmt.Percent(project.Progress),
                    Simulation.Loc.Format("Costs {0} over {1:N0} days.",
                        Fmt.Money(project.Cost), project.DurationDays));
        }

        var provinces = _campaign.ProvinceMap is null
            ? 0
            : PeaceConference.ProvincesOfState(_campaign.ProvinceMap, _selectedState.MapIndex).Count;
        var garrison = _campaign.Armies.Count(army => army.StateMapIndex == _selectedState.MapIndex);
        DetailHeading("GROUND");
        DetailRow("Provinces", provinces.ToString("N0"),
            "Tactical provinces this region is made of. A border drawn through them splits the region in two.");
        DetailRow("Divisions here", garrison.ToString("N0"),
            "Formations currently standing inside the region.");

        SetRegionalActionsEnabled(owned);
    }

    /// <summary>
    /// Who lives here: the peoples, their faiths, their trades and the classes they fall in.
    ///
    /// Four compositions rather than four lists of numbers. A region is a thing you are
    /// supposed to be able to recognise at a glance - one people or five, one faith or a
    /// fault line through the middle of it, a countryside or a city - and a column of
    /// percentages does not do that however accurate it is.
    /// </summary>
    private void ShowRegionPopulation()
    {
        if (_selectedState is null) return;
        PrepareDossierTabs("Region");
        _dossierTab = "Population";
        ApplyDossierTab();
        if (HideForeignRegionDetails()) return;
        _sideTitle.Text = Simulation.Loc.Format("{0} — people", _selectedState.DisplayName);
        _regionControls.Visible = true;
        ClearDetail();

        var owner = _campaign.Nations.Find(nation => nation.MapIndex == _selectedState.NationMapIndex);
        var total = Math.Max(1L, _selectedState.Population);
        _sideBody.Text = Simulation.Loc.Format("{0} people under {1}",
            Fmt.People(_selectedState.Population), owner?.DisplayName ?? Tr("an unknown flag"));

        DetailHeading("THE REGION");
        if (_selectedState.LastHouseholdReviewDay >= 0 && _campaign.Defines.HouseholdDynamicsEnabled)
        {
            DetailRow("Employment", Fmt.Percent(HouseholdEconomy.Employment(_selectedState)),
                "Employment follows available jobs gradually; factories need labour, not just construction money.");
            DetailRow("Basic needs covered", Fmt.Percent(_selectedState.BasicNeedsCoverage),
                "Food, energy and medicine satisfaction in the owner's market, weighted 60%, 25% and 15%. This is supply coverage, not income equality.");
            DetailRow("Livelihood stress", Fmt.Percent(_selectedState.LivelihoodStress),
                "Shortages, lack of jobs and local destruction put pressure on living standards and radicalism.");
            DetailRow("Births / deaths, last review", Loc.Format("{0} / {1}", Fmt.People(_selectedState.ReviewBirths), Fmt.People(_selectedState.ReviewDeaths)),
                "Literacy changes the birth rate; living conditions, medicine and war damage change the death rate.");
        }
        DetailRow("Population", Fmt.People(_selectedState.Population), Fmt.PeopleExact(_selectedState.Population));
        DetailRow("Literacy", Fmt.Percent(_selectedState.Literacy),
            "How much of the region can read. Literacy raises productivity and assimilates minorities faster.");
        DetailRow("Standard of living", Fmt.Percent(_selectedState.StandardOfLiving),
            "How well people live here. Below the national average it feeds radicalism; above it, calm.");
        DetailRow("Urbanisation", Fmt.Percent(_selectedState.Urbanization),
            "Share living in towns — and what decides the trades below: a rural region is mostly " +
            "peasants, a city is mostly wage labour, counters and desks.");
        DetailRow("Radicalism", Fmt.Percent(_selectedState.Radicalism),
            RadicalismTooltip(_selectedState),
            _selectedState.Radicalism > 0.45f ? "state.negative" :
            _selectedState.Radicalism > 0.25f ? "state.warning" : "state.positive");
        var foreign = EconomySystem.ForeignShare(_selectedState, owner?.Tag ?? "");
        DetailRow("Minorities", Fmt.Percent(foreign),
            "People of no nation this state is for. A starred people below is one of its own.",
            foreign > 0.5f ? "state.negative" : foreign > 0.15f ? "state.warning" : "state.positive");

        DetailComposition("PEOPLES", CultureSlices(_selectedState, owner), total);
        DetailComposition("FAITHS", ReligionSlices(_selectedState), total, 6);
        // Ten rows for ten trades. At nine the last one vanished into "and 1 more", and
        // the two summaries below stopped adding up to anything a reader could check -
        // which is exactly the complaint that led to splitting the axes in the first place.
        DetailComposition("TRADES", ProfessionSlices(_selectedState), total, 10);

        // The same people twice more. Neither list is a second census: both are the TRADES
        // above added up, once by how well off each trade is and once by what it lives off.
        // They are two lists because they are two questions - a landowner is the top of the
        // first and shares the second with a peasant - and a single list trying to be both
        // was the thing nobody could read.
        DetailComposition("SOCIAL CLASSES",
            Strata(Enum.GetValues<SocialClass>(), group => group.Class, ClassName, ClassColour),
            total, 3);
        DetailComposition("SECTORS",
            Strata(Enum.GetValues<Sector>(), group => group.Sector, SectorName, SectorColour),
            total, 4);
    }

    /// <summary>
    /// One cut through a region's people: a slice per value of some axis, with what the
    /// people on it earn and how they live.
    ///
    /// Written once and used for both cuts, because they are the same arithmetic over two
    /// different fields. A reader comparing the classes with the sectors should be comparing
    /// the population, not two implementations of the same sum.
    /// </summary>
    private IReadOnlyList<Slice> Strata<T>(IEnumerable<T> values, Func<PopulationGroup, T> axis,
        Func<T, string> name, Func<T, string> colour) where T : struct, Enum =>
        values.Select(value =>
        {
            var groups = _selectedState.PopulationGroups
                .Where(group => EqualityComparer<T>.Default.Equals(axis(group), value)).ToArray();
            var size = groups.Sum(group => group.Size);
            if (size <= 0) return new Slice(name(value), 0, Colors.Transparent, "");
            var employed = groups.Sum(group => group.Size * group.Employment) / size;
            var wealth = groups.Sum(group => group.Size * group.Wealth) / size;
            var living = groups.Sum(group => group.Size * group.StandardOfLiving) / size;
            return new Slice(name(value), size, Swatch(colour(value)),
                Simulation.Loc.Format("{0} people", Fmt.PeopleExact(size)) + "\n" +
                Simulation.Loc.Format("Employed {0:P0} · wealth {1:P0} · standard of living {2:P0}",
                    employed, wealth, living));
        }).ToList();

    /// <summary>The ladder, drawn as one: slate at the bottom, gold, then the owning red.</summary>
    private static string ClassColour(SocialClass socialClass) => socialClass switch
    {
        SocialClass.Lower => "#5f7f8f",
        SocialClass.Middle => "#bf9f5f",
        _ => "#c05f5f"
    };

    /// <summary>The sectors, each in the colour of the trade that stands for it.</summary>
    private static string SectorColour(Sector sector) => sector switch
    {
        Sector.Agriculture => "#7f9c4f",
        Sector.Industry => "#8f6f5f",
        Sector.Services => "#5f8fa8",
        _ => "#a06fa8"
    };

    /// <summary>One tactical province: who owns it, who holds it, and what it is made of.</summary>
    private void ShowProvinceOverview()
    {
        PrepareDossierTabs("Region");
        _dossierTab = "Province";
        ApplyDossierTab();
        var topology = _campaign.ProvinceMap;
        if (topology is null || _selectedProvinceId <= 0 || _selectedProvinceId > topology.ProvinceCount)
        {
            _sideBody.Text = Tr("The province map is still being prepared.");
            return;
        }
        _regionControls.Visible = true;
        ClearDetail();

        var owner = _campaign.Nations.Find(nation => nation.MapIndex == topology.ProvinceToNation[_selectedProvinceId]);
        var controllerIndex = _campaign.ProvinceController?[_selectedProvinceId] ?? topology.ProvinceToNation[_selectedProvinceId];
        var controller = _campaign.Nations.Find(nation => nation.MapIndex == controllerIndex);
        var occupation = _campaign.ProvinceOccupation?[_selectedProvinceId] ?? 0f;
        var formations = _campaign.Armies.Where(army => army.ProvinceId == _selectedProvinceId && CampaignIntel.PlayerCanSeeArmy(_campaign, army)).ToArray();
        var adjacent = topology.Neighbors[_selectedProvinceId];
        var crossings = adjacent.Count(province => topology.IsRiverCrossing(_selectedProvinceId, province));
        var centre = topology.CenterOf(_selectedProvinceId);

        _sideTitle.Text = Simulation.Loc.Format("Province #{0:N0}", _selectedProvinceId);
        _sideBody.Text = Simulation.Loc.Format("{0} · {1:0.0}°{2} {3:0.0}°{4}",
            _selectedState?.DisplayName ?? Tr("Unmapped ground"),
            MathF.Abs(centre.Latitude), centre.Latitude >= 0 ? Tr("N") : Tr("S"),
            MathF.Abs(centre.Longitude), centre.Longitude >= 0 ? Tr("E") : Tr("W"));

        DetailHeading("STANDING");
        DetailRow("Owner", owner?.DisplayName ?? Tr("unknown"),
            "Whose ground this is in law. Only a peace or an annexation changes it.");
        DetailRow("Controller", controller?.DisplayName ?? Tr("unknown"),
            "Who holds it right now. An army takes control; a treaty takes ownership.",
            controller == owner ? "text.bright" : "state.warning");
        if (controller != owner && controller is not null && CanInspectNation(controller.Id))
            DetailRow("Occupation", Fmt.Percent(occupation),
                "How firmly the occupier holds it. Above the secure threshold it can be kept at a peace.",
                occupation >= _campaign.Defines.OccupationSecureThreshold ? "state.positive" : "state.warning");

        DetailHeading("GROUND");
        DetailRow("Neighbours", adjacent.Length.ToString("N0"),
            "Provinces an army can march to from here in one step.");
        DetailRow("River crossings", crossings.ToString("N0"),
            crossings == 0
                ? "No river runs between this province and its neighbours."
                : "Attacking across a river costs the attacker dearly; a major one costs more.");
        DetailRow("Lake shore", topology.HasLakeAccess(_selectedProvinceId) ? "yes" : "no",
            "A navigable lake shore eases supply.");

        DetailHeading("FORMATIONS");
        if (formations.Length == 0)
            DetailRow("Visible divisions", "none", "No known formations. Foreign forces are hidden by fog of war.");
        foreach (var army in formations.Take(8))
            DetailRow(Shorten(army.Name, 18),
                Simulation.Loc.Format("{0:0.0} div · {1:P0}", army.Divisions, army.Organisation),
                CanInspectNation(army.NationId) ? DivisionTooltip(army) : "Observed foreign formation. Orders and route are unavailable.",
                army.CutOff ? "state.negative" : army.InBattle ? "state.warning" : "text.bright");
    }

    private void QueueRegionalBuilding(BuildingKind kind)
    {
        if (_selectedState is null || _selectedState.NationMapIndex != _campaign.Player.MapIndex)
        {
            _regionActionFeedback.Text = Tr("Only an owned region can receive construction orders.");
            return;
        }
        var before = _campaign.Construction.Count;
        _campaign.Commands.Enqueue(new QueueConstructionCommand
        {
            IssuerId = _campaign.Player.Id,
            StateMapIndex = _selectedState.MapIndex,
            Building = kind
        });
        _campaign.Commands.ApplyAll(_campaign);
        _regionActionFeedback.Text = _campaign.Construction.Count > before
            ? Simulation.Loc.Format("{0} added to the {1} queue.",
                Tr(BuildingCatalog.Get(kind).Name), _selectedState.DisplayName)
            : Tr("Could not queue the project: check treasury or the national construction limit.");
        RefreshRegionViews();
    }

    private void RefreshMoveCapitalButton()
    {
        if (_moveCapitalButton is null || _selectedState is null || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var refusal = CapitalCore.Refusal(_campaign, player, _selectedState);
        _moveCapitalButton.Disabled = refusal.Length > 0;
        _moveCapitalButton.TooltipText = Ui.Tip(refusal.Length > 0
            ? Tr(refusal)
            : Simulation.Loc.Format(
                "Move the government to {0}. It costs {1} from the treasury and {2} of the country's stability, " +
                "which fades the way any upheaval does, and the government cannot move again for {3} day(s). " +
                "The capital's region weighs far more in the country's stability than its size.",
                _selectedState.DisplayName, Fmt.Money(CapitalCore.Cost(_campaign, player)),
                _campaign.Defines.CapitalMoveStabilityCost.ToString("P0"),
                _campaign.Defines.CapitalMoveIntervalDays.ToString("N0")));
    }

    private void MoveCapitalHere()
    {
        if (_selectedState is null || _campaign?.Player is null) return;
        var command = new MoveCapitalCommand { IssuerId = _campaign.Player.Id, StateMapIndex = _selectedState.MapIndex };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _regionActionFeedback.Text = command.Moved
            ? Simulation.Loc.Format("The government has moved to {0}.", _selectedState.DisplayName)
            : Tr(CapitalCore.Refusal(_campaign, _campaign.Player, _selectedState));
        RefreshRegionViews();
    }

    private void RaiseRegionalArmy()
    {
        if (_selectedState is null || _selectedState.NationMapIndex != _campaign.Player.MapIndex)
        {
            _regionActionFeedback.Text = Tr("Recruitment is available only in an owned region.");
            return;
        }
        OpenRecruitment(_selectedState.MapIndex);
    }

    /// <summary>
    /// Show the orders for a region the player owns, or the way to its owner for one they
    /// do not. Never both, and never six buttons that cannot be pressed.
    /// </summary>
    private void SetRegionalActionsEnabled(bool owned)
    {
        if (_ownedRegionActions is null || _foreignRegionDiplomacy is null) return;
        _ownedRegionActions.Visible = owned;

        var owner = SelectedRegionOwner();
        var offer = !owned && owner is not null && owner.Id != _campaign.Player?.Id;
        _foreignRegionDiplomacy.Visible = offer;
        if (offer)
        {
            _foreignRegionDiplomacy.Text = Simulation.Loc.Format("Talk to {0}", owner.DisplayName);
            _foreignRegionDiplomacy.TooltipText = Ui.Tip(Simulation.Loc.Format(
                "Open the diplomacy screen with {0}, who this region belongs to.", owner.DisplayName));
        }
    }

    /// <summary>Whoever the selected region answers to, or null.</summary>
    private Simulation.Nation SelectedRegionOwner() =>
        _selectedState is null || _campaign is null
            ? null
            : _campaign.Nations.Find(nation => nation.MapIndex == _selectedState.NationMapIndex);

    /// <summary>Open the file on whoever owns the region being looked at.</summary>
    private void OpenSelectedRegionDiplomacy()
    {
        var owner = SelectedRegionOwner();
        if (owner is null) return;
        _selectedNation = owner;
        OpenScreen("Diplomacy");
    }

    private static string ClassName(SocialClass socialClass) => Simulation.Loc.Get(socialClass switch
    {
        SocialClass.Lower => "Lower class",
        SocialClass.Middle => "Middle class",
        _ => "Upper class"
    });

    /// <summary>
    /// The four halves of an economy, named. The enum's own spelling is the key, which is
    /// what every other data-declared name in the game uses too.
    /// </summary>
    private static string SectorName(Sector sector) => Simulation.Loc.Get(sector.ToString());
}
