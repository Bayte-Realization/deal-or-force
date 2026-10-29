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
        foreach (var kind in BuildingCatalog.All.OrderBy(kind => Tr(BuildingCatalog.Get(kind).Name), StringComparer.CurrentCulture))
        {
            var definition = BuildingCatalog.Get(kind);
            var built = state.Buildings.GetValueOrDefault(kind);
            var underway = _campaign.Construction.Count(project => project.StateMapIndex == state.MapIndex && project.Building == kind);

            // Why not, in the order a player would want to hear it.
            var refusal = Waterfront.Refusal(_campaign, state.MapIndex, kind);
            if (refusal.Length == 0 && running >= limit)
                refusal = Simulation.Loc.Format("The national construction limit is reached: {0} projects are under way.", running);
            if (refusal.Length == 0 && player.Treasury < MathF.Min(0.05f, ConstructionCost.Of(_campaign, player, kind)))
                refusal = Tr("The treasury cannot start it.");

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

            var tip = Tr(definition.Name) + "\n" + Simulation.Loc.Get(definition.Description) + "\n\n" +
                      Simulation.Loc.Format("Costs {0} and takes {1} days.", Fmt.Money(ConstructionCost.Of(_campaign, player, kind)), definition.Days) +
                      "\n" + Simulation.Loc.Format("In this region: {0} built, {1} under way.", built, underway);
            var effects = BuildingEffects(state, kind, definition);
            if (effects.Length > 0) tip += "\n\n" + effects;
            if (Waterfront.NeedsWater(kind))
                tip += "\n" + Tr("Needs a coast, a lake or a navigable river. On inland water it is worth less.");
            if (refusal.Length > 0) tip += "\n\n" + refusal;
            build.TooltipText = Ui.Tip(tip);
            name.TooltipText = build.TooltipText;
        }
    }

    private readonly List<Commodity> _buildingMakes = new();
    private readonly List<Commodity> _buildingUses = new();

    /// <summary>
    /// What one more of this building does in this region, in the lines events and decisions
    /// use (item 67): the numbers the simulation adds when it is finished, scaled by the water
    /// it stands on, then what it makes and what it burns, read out of the production chains.
    /// A mine names only what lies under this region, because that is all it will dig.
    /// </summary>
    private string BuildingEffects(ScenarioState state, BuildingKind kind, BuildingDefinition definition)
    {
        var defines = _campaign.Defines;
        var culture = CultureInfo.CurrentCulture;
        var yield = Waterfront.Yield(_campaign, state.MapIndex, kind);
        var lines = new List<string>();
        void Add(string label, string value) =>
            lines.Add("  " + Simulation.Loc.Format("{0}: {1}", Simulation.Loc.Get(label), value));
        string Points(float share) => (share * 100f).ToString("+0.#;-0.#", culture) + "%";

        if (definition.GdpGain > 0f) Add("GDP", "+" + Fmt.Money(definition.GdpGain * yield));
        if (definition.CapacityGain > 0f) Add("Construction capacity", (definition.CapacityGain * yield).ToString("+0.##;-0.##", culture));
        switch (kind)
        {
            case BuildingKind.Infrastructure:
            {
                Add("Supply throughput, divisions", defines.SupplyPerInfrastructure.ToString("+0.#;-0.#", culture));
                // Roads stop helping at a cap, so the next one may add less than the last.
                var level = state.Buildings.GetValueOrDefault(kind);
                var cap = MathF.Max(0f, defines.RoadSpeedCap);
                var now = M.Clamp(level * defines.RoadSpeedPerInfrastructure, 0f, cap);
                var next = M.Clamp((level + 1) * defines.RoadSpeedPerInfrastructure, 0f, cap);
                Add("March pace in this region", Points(next - now));
                break;
            }
            case BuildingKind.Barracks:
                Add("Supply throughput, divisions", defines.SupplyPerBarracks.ToString("+0.#;-0.#", culture));
                break;
            case BuildingKind.University:
                Add("Literacy in this region", Points(0.025f));
                Add("Literacy", Points(0.003f));
                Add("Productivity", Points(0.006f));
                break;
            case BuildingKind.Hospital:
                Add("Standard of living in this region", Points(0.018f));
                break;
            case BuildingKind.Shipyard:
                Add("Slipways", (defines.SlipwaysPerShipyard * yield).ToString("+0.#;-0.#", culture));
                break;
            case BuildingKind.Port:
            case BuildingKind.RailNetwork:
            {
                // How far the country's goods travel, which stops rising at half as far again.
                var reach = TradeNetwork.ReachOf(_campaign, _campaign.Player);
                var step = kind == BuildingKind.Port ? 0.05f * yield : 0.035f;
                Add("Trade reach", Points(MathF.Min(1.5f, reach + step) - reach));
                break;
            }
        }

        ProductionChains.RolesOf(kind, _buildingMakes, _buildingUses);
        var mine = kind == BuildingKind.ExtractionIndustry;
        if (mine)
        {
            // Almost every region holds a trace of almost everything - a twentieth of its country's
            // average, so a find is still possible; name what is worth digging, richest first, so
            // the order itself says what this mine would be for.
            _buildingMakes.RemoveAll(good => state.ResourceDeposits.GetValueOrDefault(good) < 0.5f);
            _buildingMakes.Sort((a, b) => state.ResourceDeposits.GetValueOrDefault(b).CompareTo(state.ResourceDeposits.GetValueOrDefault(a)));
        }

        var text = lines.Count > 0 ? Simulation.Loc.Get("Each one, once built:") + "\n" + string.Join("\n", lines) : "";
        string Goods(List<Commodity> goods) =>
            string.Join(", ", goods.Select(good => Tr(CommodityCatalog.Get(good).Name)));
        if (_buildingMakes.Count > 0)
            text += (text.Length > 0 ? "\n" : "") + Simulation.Loc.Format("Makes: {0}", Goods(_buildingMakes));
        else if (mine)
            text += (text.Length > 0 ? "\n" : "") + Simulation.Loc.Get("Nothing lies under this region to dig up.");
        if (_buildingUses.Count > 0)
            text += (text.Length > 0 ? "\n" : "") + Simulation.Loc.Format("Uses: {0}", Goods(_buildingUses));
        return text;
    }
}
