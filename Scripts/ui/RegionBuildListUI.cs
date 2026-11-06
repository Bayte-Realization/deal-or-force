using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Everything a region can build, in the region card itself.
///
/// The card offered five buttons - Develop, Industry, Extract, Power, Arms - for a catalogue of
/// seventeen buildings, so a refinery, a port or a research laboratory could only be ordered from
/// the national screen, one dropdown at a time, away from the map the player was looking at. Now
/// the card lists every building in a column of its own that scrolls: what the region already
/// has, what is being built, a button to add one, and - under the cursor - what it costs, how
/// long it takes, and why it cannot be started when it cannot.
/// </summary>
public partial class DealOrForceGame
{
    private VBoxContainer _regionBuildColumn;
    private VBoxContainer _regionBuildRows;

    /// <summary>
    /// The ruins, among the region's other figures: how much of it the war has wrecked, what
    /// that costs it, and how fast it comes back (WarDamage). Nothing for an untouched region,
    /// and nothing the fog of war hides - the same rule as the rest of the card.
    /// </summary>
    private void ShowWarDamage(ScenarioState state)
    {
        if (state is null || !IsInstanceValid(_regionCardRegionRows)) return;
        var owner = _campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex);
        if (owner is not null && !CanInspectNation(owner.Id) && !PlayerHoldsMostOf(state)) return;

        // What the enemy stands on makes nothing for the owner - whoever the owner is.
        var occupied = owner is null ? 0f : StabilityCore.OccupiedShare(_campaign, state, owner);
        if (occupied > 0.001f)
            CardRow(_regionCardRegionRows, "Under occupation", Fmt.Percent(occupied),
                Simulation.Loc.Format("{0} of the region is held by the enemy. What is made there does not reach {1}'s market.",
                    Fmt.Percent(occupied), owner.DisplayName) + "\n" +
                Simulation.Loc.Format("Output for its owner: -{0}", Fmt.Percent(1f - WarDamage.OutputFactor(_campaign, owner, state))),
                occupied > 0.5f ? "state.negative" : "state.warning");

        if (state.WarDamage < 0.005f && state.Displaced.Count == 0) return;
        var tip = string.Join("\n",
            Simulation.Loc.Format("{0} of the region lies in ruins.", Fmt.Percent(state.WarDamage)),
            Simulation.Loc.Format("Output: -{0}", Fmt.Percent(1f - WarDamage.OutputFactor(_campaign, state))),
            Simulation.Loc.Format("Leaving: about {0} people a week", Fmt.People(WarDamage.Leaving(_campaign, state))),
            Simulation.Loc.Format("Rebuilt: about {0} of the damage a week",
                Fmt.Percent(WarDamage.RebuiltPerWeek(_campaign, owner, state))),
            Tr("Infrastructure and a reconstruction programme rebuild it faster."));
        var displaced = WarDamage.DisplacedTotal(state);
        if (displaced > 0)
        {
            tip += "\n\n" + Simulation.Loc.Format("Displaced elsewhere in the country: about {0}", Fmt.People(displaced));
            var returning = WarDamage.Returning(_campaign, state);
            tip += "\n" + (returning > 0
                ? Simulation.Loc.Format("Coming home: about {0} people a week", Fmt.People(returning))
                : Tr("They come home once the region is rebuilt and safe from the fighting."));
        }
        CardRow(_regionCardRegionRows, "War damage", Fmt.Percent(state.WarDamage), tip,
            state.WarDamage > 0.5f ? "state.negative" : state.WarDamage > 0.2f ? "state.warning" : "text.bright");
        if (state.NationMapIndex == _campaign.Player.MapIndex && state.WarDamage >= _campaign.Defines.WarDamageRebuildMinimum)
            ShowRebuild(state);
    }

    /// <summary>
    /// The order to rebuild a region of one's own, under its war damage: what it costs, what it
    /// buys, and - when it cannot be given - why. While a reconstruction runs, how long it has
    /// left, and whether the fighting has stopped it.
    /// </summary>
    private void ShowRebuild(ScenarioState state)
    {
        var player = _campaign.Player;
        var defines = _campaign.Defines;
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        row.CustomMinimumSize = new Vector2(0, 23);
        _regionCardRegionRows.AddChild(row);

        var underway = _campaign.ElapsedDays <= state.RebuildingUntilDay;
        var status = !underway ? Tr("Reconstruction") :
            WarDamage.Safe(_campaign, state)
                ? Simulation.Loc.Format("Rebuilding: {0} days left", state.RebuildingUntilDay - _campaign.ElapsedDays)
                : Tr("Rebuilding stopped by the fighting");
        var label = Title(status, 13, Ui.Colour(underway && !WarDamage.Safe(_campaign, state) ? "state.warning" :
            underway ? "text.accent" : "text.body_soft"));
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(label);

        var refusal = WarDamage.RebuildRefusal(_campaign, player, state);
        var rebuild = ActionButton("Rebuild", () =>
        {
            _campaign.Commands.Enqueue(new RebuildRegionCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex });
            _campaign.Commands.ApplyAll(_campaign);
            ShowRegionCard();
        }, 96, 21);
        rebuild.Disabled = refusal.Length > 0;
        var tip = Simulation.Loc.Format(
            "For {0} days the region is rebuilt {1} times as fast, while no battle is fought in it and as far as the market has industrial materials for it.",
            defines.WarDamageRebuildDays, (1f + defines.WarDamageRebuildBoost).ToString("0.#", CultureInfo.InvariantCulture)) +
                  "\n" + Simulation.Loc.Format("Costs {0}.", Fmt.Money(WarDamage.RebuildCost(_campaign, player, state))) +
                  "\n" + Simulation.Loc.Format("Industrial materials: {0} of what the market wants.", Fmt.Percent(WarDamage.Materials(player)));
        if (refusal.Length > 0) tip += "\n\n" + refusal;
        rebuild.TooltipText = Ui.Tip(tip);
        label.TooltipText = rebuild.TooltipText;
        label.MouseFilter = Control.MouseFilterEnum.Pass;
        row.AddChild(rebuild);
    }

    /// <summary>The build column, beside the region and the province.</summary>
    private void BuildRegionBuildList(HBoxContainer columns)
    {
        _regionBuildColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _regionBuildColumn.AddThemeConstantOverride("separation", 2);
        columns.AddChild(_regionBuildColumn);
        _regionBuildColumn.AddChild(Title("BUILD", 11, Ui.Colour("text.caption")));

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(250, 150),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        _regionBuildColumn.AddChild(scroll);
        _regionBuildRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _regionBuildRows.AddThemeConstantOverride("separation", 0);
        scroll.AddChild(_regionBuildRows);
    }

    /// <summary>One row per building, for a region of one's own; the column hides for anybody else's.</summary>
    private void ShowRegionBuildList(ScenarioState state, bool owned)
    {
        ShowWarDamage(state);
        if (_regionBuildColumn is null || !IsInstanceValid(_regionBuildRows)) return;
        _regionBuildColumn.Visible = owned && state is not null;
        if (!_regionBuildColumn.Visible) return;
        ClearChildren(_regionBuildRows);

        var player = _campaign.Player;
        var running = _campaign.Construction.Count(project => project.NationId == player.Id);
        var limit = ConstructionCost.Limit(_campaign, player, _campaign.Defines.MaxConstructionProjects);
        var outputFactor = MarketSystem.RegionOutputFactor(_campaign, player, state);
        foreach (var kind in BuildingCatalog.All.OrderBy(kind => Tr(BuildingCatalog.Get(kind).Name), StringComparer.CurrentCulture))
        {
            var definition = BuildingCatalog.Get(kind);
            var built = state.Buildings.GetValueOrDefault(kind);
            var underway = _campaign.Construction.Count(project => project.StateMapIndex == state.MapIndex && project.Building == kind);

            // Why not, in the order a player would want to hear it.
            var refusal = ConstructionAdmission.Refusal(_campaign, player, state, kind);

            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 6);
            _regionBuildRows.AddChild(row);

            var name = Title(Tr(definition.Name), 12, Ui.Colour(refusal.Length > 0 ? "text.muted" : "text.body_soft"));
            name.AutowrapMode = TextServer.AutowrapMode.Off;
            name.ClipText = true;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            name.CustomMinimumSize = new Vector2(120, 0);
            name.MouseFilter = Control.MouseFilterEnum.Pass;
            row.AddChild(name);

            var count = Title(underway > 0 ? $"{built:N0} +{underway:N0}" : built.ToString("N0"), 12,
                Ui.Colour(underway > 0 ? "text.accent" : "text.bright"));
            count.AutowrapMode = TextServer.AutowrapMode.Off;
            count.HorizontalAlignment = HorizontalAlignment.Right;
            count.CustomMinimumSize = new Vector2(44, 0);
            row.AddChild(count);

            var captured = kind;
            var build = ActionButton("+", () => RegionCardBuild(captured), 26, 21);
            build.Disabled = refusal.Length > 0;
            row.AddChild(build);
            // Several at once: a programme, not a click per factory. Stops where money or the
            // national limit runs out, so it never queues what cannot start.
            var room = Math.Max(0, limit - running);
            var batch = ActionButton("+5", () => RegionCardBuild(captured, 5), 34, 21);
            batch.Disabled = refusal.Length > 0 || room < 2;
            batch.TooltipText = Ui.Tip(Simulation.Loc.Format("Queue up to {0} at once; the national limit leaves room for {1}.", 5, room));
            row.AddChild(batch);

            var tip = Tr(definition.Name) + "\n" + Simulation.Loc.Get(definition.Description) + "\n\n" +
                      Simulation.Loc.Format("Costs {0} and takes {1} days.", Fmt.Money(ConstructionCost.Of(_campaign, player, kind)), ConstructionCost.ExpectedDays(_campaign, player, kind)) +
                      "\n" + Simulation.Loc.Format("In this region: {0} built, {1} under way.", built, underway);
            var effects = BuildingEffects(state, kind, outputFactor).Text;
            if (effects.Length > 0) tip += "\n\n" + effects;
            if (Waterfront.NeedsWater(kind))
                tip += "\n" + Tr("Needs a coast, a lake or a navigable river. On inland water it is worth less.");
            if (refusal.Length > 0) tip += "\n\n" + refusal;
            build.TooltipText = Ui.Tip(tip);
            name.TooltipText = build.TooltipText;
        }
    }

    private readonly List<(Commodity Good, float Amount)> _buildingMakes = new();
    private readonly List<(Commodity Good, float Amount)> _buildingUses = new();
    private readonly List<Commodity> _buildingNeeds = new();

    /// <summary>What one more of a building does: the tooltip's lines, and a one-line summary for a card.</summary>
    private readonly record struct BuildingOutlookText(string Text, string Summary);

    /// <summary>
    /// What one more of this building does in this region, in short lines a player can act on.
    ///
    /// The player's complaint was fair: the list said "GDP +1.65" and "construction capacity
    /// +0.25" and named the goods a building touches, and none of it said how much, whether it
    /// would make money, or why a refinery and a smelter were different things. Now every line
    /// is a number the simulation itself uses, asked of the code that uses it rather than copied:
    ///
    ///   - goods made and burned a week, from ProductionChains.Marginal - every recipe worked out
    ///     with and without the building, at this week's staffing, prices and supply, so a mine
    ///     names what lies under this region and a smelter with no power makes less;
    ///   - the goods it cannot work without but does not buy (a recipe's needs);
    ///   - the jobs it asks for (BuildingDefinition.Jobs, which MarketSystem staffs the plant from);
    ///   - what its output is worth a year at this country's GDP scale, against its inputs;
    ///   - its special effect - supply, recruitment, research, deaths, trade reach, slipways -
    ///     from the same constants and helpers the systems that apply them read;
    ///   - and construction capacity, said for what it is: the speed every site of the country
    ///     works at (ConstructionCost.Pace), with diminishing returns. There is no GDP line of its
    ///     own: GDP is what the country sells, so the output line is the building's GDP.
    /// </summary>
    private BuildingOutlookText BuildingEffects(ScenarioState state, BuildingKind kind, float? outputFactor = null)
    {
        var player = _campaign?.Player;
        if (player is null || state is null) return new BuildingOutlookText("", "");
        var definition = BuildingCatalog.Get(kind);
        var defines = _campaign.Defines;
        var culture = CultureInfo.CurrentCulture;
        var yield = Waterfront.Yield(_campaign, state.MapIndex, kind);
        var factor = outputFactor ?? MarketSystem.RegionOutputFactor(_campaign, player, state);
        var lines = new List<string>();
        var summary = new List<string>();
        void Add(string label, string value) =>
            lines.Add("  " + Simulation.Loc.Format("{0}: {1}", Simulation.Loc.Get(label), value));
        void Say(string sentence) => lines.Add("  " + Simulation.Loc.Get(sentence));
        string Points(float share) => (share * 100f).ToString("+0.#;-0.#;0", culture) + "%";
        string Amount(float value) => Fmt.Number(value, value >= 10f ? "0" : value >= 1f ? "0.#" : "0.##");
        string Goods(IEnumerable<(Commodity Good, float Amount)> goods, int most) =>
            string.Join(", ", goods.Take(most).Select(entry => Tr(CommodityCatalog.Get(entry.Good).Name) + " " + Amount(entry.Amount)));

        // ---- goods ------------------------------------------------------------------------
        ProductionChains.Marginal(player, state, kind, factor, _buildingMakes, _buildingUses, _buildingNeeds);
        if (_buildingMakes.Count > 0)
        {
            Add("Makes a week", Goods(_buildingMakes, 6));
            summary.Add(string.Join(", ", _buildingMakes.Take(3).Select(entry => Tr(CommodityCatalog.Get(entry.Good).Name))));
        }
        else if (kind == BuildingKind.ExtractionIndustry)
            Say("Nothing lies under this region to dig up.");
        if (_buildingUses.Count > 0) Add("Uses a week", Goods(_buildingUses, 6));
        if (_buildingNeeds.Count > 0)
            Add("Slows without", string.Join(", ", _buildingNeeds.Select(good => Tr(CommodityCatalog.Get(good).Name))));

        // ---- people and money -------------------------------------------------------------
        if (definition.Jobs > 0f)
        {
            Add("Jobs", Fmt.People(definition.Jobs * 1_000_000f));
            summary.Add(Simulation.Loc.Format("{0} jobs", Fmt.People(definition.Jobs * 1_000_000f)));
        }
        else Add("Jobs", Simulation.Loc.Get("none"));
        if (player.EmployedShare < 0.995f && definition.Jobs > 0f)
            Add("Jobs the country can fill", Fmt.Percent(player.EmployedShare));
        // At this country's GDP scale: the market value of a week's output, annualised and
        // calibrated the way EconomySystem turns the market into GDP.
        var scale = player.OutputScale > 0f ? player.OutputScale : 1f;
        var worth = 0f;
        foreach (var (good, amount) in _buildingMakes)
            if (player.Market.TryGetValue(good, out var quote)) worth += amount * MathF.Max(0f, quote.Price);
        var cost = 0f;
        foreach (var (good, amount) in _buildingUses)
            if (player.Market.TryGetValue(good, out var quote)) cost += amount * MathF.Max(0f, quote.Price);
        // GDP is the market value of what the country sells (MarketSystem's gross output), so
        // this is the building's lasting share of GDP while its goods find buyers - there is no
        // separate GDP step on completion any more.
        if (worth > 0f)
            Add("Output a year, counted in GDP", cost > 0f
                ? Simulation.Loc.Format("{0}, inputs {1}", Fmt.Money(worth * 52f * scale), Fmt.Money(cost * 52f * scale))
                : Fmt.Money(worth * 52f * scale));

        // ---- what it does besides goods --------------------------------------------------
        switch (kind)
        {
            case BuildingKind.Infrastructure:
            {
                Add("Army supply here, divisions", defines.SupplyPerInfrastructure.ToString("+0.#;-0.#", culture));
                // Roads stop helping at a cap, so the next one may add less than the last.
                var level = state.Buildings.GetValueOrDefault(kind);
                var cap = MathF.Max(0f, defines.RoadSpeedCap);
                var now = M.Clamp(level * defines.RoadSpeedPerInfrastructure, 0f, cap);
                var next = M.Clamp((level + 1) * defines.RoadSpeedPerInfrastructure, 0f, cap);
                Add("March pace in this region", Points(next - now));
                Add("War damage rebuilt here", Points(defines.WarDamageRecoveryPerInfrastructure));
                var regions = Math.Max(1, _campaign.StatesOf(player).Length);
                AddReach(TradeNetwork.ReachPerInfrastructure / regions);
                summary.Add(Simulation.Loc.Get("army supply"));
                break;
            }
            case BuildingKind.Barracks:
                Add("Army supply here, divisions", defines.SupplyPerBarracks.ToString("+0.#;-0.#", culture));
                Add("Reinforcements a day", "+" + Fmt.People(LogisticsSystem.ReinforcementPerBarracks *
                    LogisticsSystem.ReinforcementBudget(_campaign, player) * NationalModifiers.Factor(player, "reinforcement_rate")));
                Add("Divisions per recruitment order", Simulation.Loc.Format("+{0} here, +{1} elsewhere",
                    RecruitmentSystem.BatchPerLocalBarracks, RecruitmentSystem.BatchPerBarracks.ToString("0.#", culture)));
                Add("Recruitment queue", Simulation.Loc.Format("+{0}, +{1} at war",
                    RecruitmentSystem.QueuePerBarracks, RecruitmentSystem.QueuePerBarracksAtWar));
                Add("Support formations here", "+" + SupportCore.PostsPerBarracks);
                summary.Add(Simulation.Loc.Get("reinforcements"));
                break;
            case BuildingKind.University:
                Add("Literacy in this region", Points(BuildingCatalog.UniversityRegionalLiteracy));
                Add("Literacy", Points(BuildingCatalog.UniversityNationalLiteracy));
                Add("Productivity", Points(BuildingCatalog.UniversityProductivity));
                AddResearch();
                Say("Keeps educated people from emigrating.");
                summary.Add(Simulation.Loc.Get("literacy, research"));
                break;
            case BuildingKind.Hospital:
            {
                // The lasting part: the share of this region's deaths it prevents, while there is
                // medicine to give - the same cover HouseholdEconomy.VitalRates reads.
                var medicine = HouseholdEconomy.MedicineSupply(player);
                var hospitals = state.Buildings.GetValueOrDefault(kind);
                var saved = (HouseholdEconomy.HospitalCover(state, hospitals + 1, medicine) -
                             HouseholdEconomy.HospitalCover(state, hospitals, medicine)) * HouseholdEconomy.HospitalDeathRelief;
                Add("Deaths in this region", Points(-saved));
                if (medicine < 0.9f) Add("Medicine on hand", Fmt.Percent(medicine));
                Add("Standard of living here, fading", Points(BuildingCatalog.HospitalLiving));
                summary.Add(Simulation.Loc.Get("fewer deaths"));
                break;
            }
            case BuildingKind.ResearchLab:
                AddResearch();
                Say("Keeps educated people from emigrating.");
                summary.Add(Simulation.Loc.Get("research"));
                break;
            case BuildingKind.Shipyard:
                Add("Slipways", (defines.SlipwaysPerShipyard * yield).ToString("+0.#;-0.#", culture));
                summary.Add(Simulation.Loc.Get("slipways"));
                break;
            case BuildingKind.Port:
                AddReach(TradeNetwork.ReachPerPort * yield);
                Add("Slipways", (Shipbuilding.SlipwaysPerPort * yield).ToString("+0.##;-0.##", culture));
                summary.Add(Simulation.Loc.Get("trade reach"));
                break;
            case BuildingKind.RailNetwork:
                AddReach(TradeNetwork.ReachPerRail);
                // LogisticsSystem.BuildingThroughput: the trunk line to the hub, less than roads.
                Add("Army supply here, divisions", defines.SupplyPerRailNetwork.ToString("+0.#;-0.#", culture));
                summary.Add(Simulation.Loc.Get("trade reach"));
                summary.Add(Simulation.Loc.Get("army supply"));
                break;
        }

        // ---- construction capacity, for what it is --------------------------------------
        // The speed every one of the country's sites works at (ConstructionCost.Pace): a day of
        // work a day at capacity one, faster above it with diminishing returns up to a ceiling.
        // The share is of the pace itself, so "+2%" means every site finishes about 2% sooner.
        var pace = ConstructionCost.Pace(defines, player.ConstructionCapacity);
        var paceNext = ConstructionCost.Pace(defines, player.ConstructionCapacity + definition.CapacityGain * yield);
        if (paceNext > pace + 0.0005f) Add("Construction speed, every site", Points(paceNext / pace - 1f));

        var text = lines.Count > 0 ? Simulation.Loc.Get("Each one, once built:") + "\n" + string.Join("\n", lines) : "";
        return new BuildingOutlookText(text, string.Join(" · ", summary));

        void AddReach(float step)
        {
            // How far the country's goods travel, which stops rising at the ceiling.
            var reach = TradeNetwork.ReachOf(_campaign, player);
            Add("Trade reach", Points(MathF.Min(TradeNetwork.ReachCeiling, reach + step) - MathF.Min(TradeNetwork.ReachCeiling, reach)));
        }

        void AddResearch()
        {
            var now = ResearchSystem.InstitutionFactor(_campaign, player, null);
            var next = ResearchSystem.InstitutionFactor(_campaign, player, kind);
            Add("Research speed", now > 0f ? Points(next / now - 1f) : "0%");
        }
    }
}
