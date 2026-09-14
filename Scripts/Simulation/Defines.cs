using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Every tunable constant the simulation reads, in one place, so balance is a data
/// change rather than a code change. Mods override individual fields; anything they
/// do not mention keeps the value below.
/// </summary>
public sealed class Defines
{
	/// <summary>
	/// How long a formation is left to reach the post it was sent to before the front
	/// planner may send it somewhere else.
	///
	/// Without this the planner re-tasks a marching division every time the line shifts,
	/// which on an active front is daily - so the division marches for ever and arrives
	/// nowhere. Ten days is long enough to cross a few provinces and short enough that a
	/// division is not left walking towards a sector that has genuinely moved on.
	/// </summary>
	public int OrderSettleDays { get; set; } = 10;

	public int BorderVisionDepth { get; set; } = 3;
	public int ArmyVisionDepth { get; set; } = 1;
	public float LandingVisionRangeDegrees { get; set; } = 1f;

	/// <summary>
	/// How far in the map has to be zoomed before unseen ground is tinted at all, and how
	/// far out before the tint is gone entirely.
	///
	/// What fog is FOR is hiding things - formations, their strength, what is happening in
	/// a province - and none of that is the tint. That is decided by the vision model and
	/// applies at every zoom, whatever the map looks like. The tint only says "you are not
	/// being shown this", which is worth saying when a player is reading one country and is
	/// a grey blanket over half the world when they are looking at the globe.
	///
	/// So it fades with distance: full at <see cref="FogTintFullDistance"/> and nothing
	/// beyond <see cref="FogTintClearDistance"/>. The camera runs from 5.25 to 24.
	/// </summary>
	public float FogTintFullDistance { get; set; } = 7f;

	public float FogTintClearDistance { get; set; } = 12f;

	/// <summary>
	/// How dark unseen ground goes at the closest zoom, and how much colour it keeps. One
	/// is untouched; the darkness multiplies the colour and the saturation mixes it back
	/// towards grey.
	/// </summary>
	public float FogTintDarkness { get; set; } = 0.74f;

	public float FogTintSaturation { get; set; } = 0.65f;
	// --- calendar ---

	public int StartYear { get; set; } = 2024;
	public int StartMonth { get; set; } = 1;
	public int StartDay { get; set; } = 1;

	/// <summary>Days between full economic recalculations.</summary>
	public int EconomyTickDays { get; set; } = 7;

	/// <summary>Days between population growth and social mobility passes.</summary>
	public int PopulationTickDays { get; set; } = 30;

	/// <summary>In-game days between automatic saves to SaveSystem.QuickSlot, or 0 to turn
	/// autosave off entirely. Monthly by default - often enough that losing progress to a
	/// crash never costs much, rarely enough that it never shows up as a hitch worth
	/// noticing next to the (much heavier) weekly economy tick it can land alongside.</summary>
	public int AutosaveIntervalDays { get; set; } = 30;

	/// <summary>Real seconds per in-game day at speed 1.</summary>
	public float SecondsPerDay { get; set; } = 0.55f;

	/// <summary>
	/// Hours of campaign time one step of the simulation covers.
	///
	/// A day was both the smallest thing that could happen and the step the whole world
	/// advanced in; those are two different decisions. Six hours makes armies march in four
	/// smaller pieces a day instead of one, which is what "the armies feel slow" was partly
	/// about - the same distance, arriving in view rather than in jumps. Seventy-two runs
	/// three days in one step, which is a real fast-forward for a quiet decade.
	///
	/// Anything that accumulates at a rate per day takes its share of the step. Anything
	/// that happens *on* a day - the weekly clearing, an event roll - runs once for each
	/// whole day the step crossed, so the answer does not change with the step size.
	/// Clamped to 1..240.
	/// </summary>
	public int HoursPerTick { get; set; } = 24;

	// --- how fast an army is ---

	/// <summary>
	/// How much of a province a division crosses in a day at speed 1, before terrain,
	/// supply, rivers and stance have their say.
	///
	/// 0.22 is four and a half days to cross one province - and there are thirty-eight
	/// thousand of them, so a province is a district rather than a country. An army that
	/// takes most of a week to walk across a district is an army the player watches rather
	/// than commands, and that is a fair complaint.
	///
	/// It is left at 0.22 all the same, because the 2024 front turns out to be chaotically
	/// sensitive to it: at 0.27 Ukraine keeps 56 % of its ground after two years of nobody
	/// playing, at 0.32 it keeps 75 %, and the bench's stalemate check fails at both. The
	/// number that makes armies feel right is not a number - it is a pass over entrenchment,
	/// defence and how a front absorbs a breakthrough, and raising this alone just decides
	/// the war for everybody. Raise it in a mod and see for yourself; that is what it is a
	/// define for.
	/// </summary>
	public float MarchProgressPerDay { get; set; } = 0.22f;

	/// <summary>Organisation a formation out of battle recovers in a day.</summary>
	public float OrganisationRecoveryPerDay { get; set; } = 0.06f;

	/// <summary>Strength a formation replaces from its own depots in a day.</summary>
	public float StrengthRecoveryPerDay { get; set; } = 0.012f;

	/// <summary>Organisation a formation in battle spends in a day.</summary>
	public float OrganisationBurnPerDay { get; set; } = 0.16f;

	/// <summary>
	/// Milliseconds of a frame the simulation may spend catching up on days. One day is
	/// always run so time never stops; beyond that the budget decides. Lower means a
	/// steadier frame rate and slower time, higher the reverse.
	/// </summary>
	public float FrameSimulationBudgetMs { get; set; } = 9f;

	/// <summary>Days a single frame may advance however cheap they turn out to be.</summary>
	public int MaxDaysPerFrame { get; set; } = 12;

	/// <summary>Multipliers applied to <see cref="SecondsPerDay"/> for speeds 1..5.</summary>
	public List<float> SpeedFactors { get; set; } = new() { 1f, 0.55f, 0.3f, 0.15f, 0.06f };

	// --- the globe ---

	/// <summary>Radius of the rendered planet, in world units.</summary>
	public float GlobeRadius { get; set; } = 2600f;

	/// <summary>How far land stands above sea level on the globe, in world units.</summary>
	public float LandRelief { get; set; } = 26f;

	/// <summary>Extra relief a terrain's own height contributes, as a multiplier.</summary>
	public float ReliefScale { get; set; } = 1.6f;

	/// <summary>
	/// Whether the textured relief shell over the water is built at all. Off for now -
	/// the crosshatch read as busy at a glance, closer to graph paper than open water,
	/// and a flat, deliberately-graded ocean colour reads calmer at every zoom level.
	/// The texture stays on disk and this is the one flag that brings it back.
	/// </summary>
	public bool OceanReliefEnabled { get; set; } = false;

	/// <summary>
	/// Texture repeated across the ocean shell, when the relief above is on.
	///
	/// A sprite name rather than a path, the way every other picture in the game is named,
	/// so a mod changes the water by dropping `globe_ocean_relief.png` into its own gfx/.
	/// It was a path - `ui/textures/ocean_vibrant.png` - and that path had not existed for
	/// some time: the file it named lived beside the source code in `Assets/`, in a folder
	/// nothing shipped and nothing could reach.
	/// </summary>
	public string OceanTexture { get; set; } = "globe_ocean_relief";

	/// <summary>Number of horizontal texture repeats around the planet.</summary>
	public float OceanTilesX { get; set; } = 48f;

	/// <summary>Number of vertical texture repeats from pole to pole.</summary>
	public float OceanTilesY { get; set; } = 24f;

	/// <summary>Height of the textured water shell above the political surface.</summary>
	public float OceanSurfaceOffset { get; set; } = 2.5f;

	/// <summary>Multiplier on every land colour. Below 1 darkens the whole map.</summary>
	public float MapBrightness { get; set; } = 0.92f;

	/// <summary>Colour saturation of the map. Above 1 pushes countries further apart.</summary>
	public float MapSaturation { get; set; } = 1.35f;

	/// <summary>Strength of the dark seam drawn along national borders.</summary>
	public float MapCountryBorder { get; set; } = 0.66f;

	/// <summary>The same for internal state borders, which should read fainter.</summary>
	public float MapStateBorder { get; set; } = 0.30f;

	/// <summary>People one unit of habitable land area can support at full development.</summary>
	public float PopulationPerAreaUnit { get; set; } = 320000f;

	// --- attrition ---

	/// <summary>Strength below which a formation has ceased to exist rather than be losing.</summary>
	public float DivisionBrokenStrength { get; set; } = 0.06f;

	/// <summary>Strength an encircled formation loses each day it cannot be supplied.</summary>
	public float EncircledStrengthLoss { get; set; } = 0.022f;

	/// <summary>Organisation it loses on the same day.</summary>
	public float EncircledOrganisationLoss { get; set; } = 0.035f;

	/// <summary>How much faster a pocket collapses while it is also being attacked.</summary>
	public float EncircledUnderAttack { get; set; } = 1.8f;

	/// <summary>How often a country below its establishment raises another formation.</summary>
	public int ArmyRebuildIntervalDays { get; set; } = 24;

	/// <summary>What raising one costs the treasury, matching the player's own recruiting.</summary>
	public float ArmyRebuildCost { get; set; } = 0.18f;

	/// <summary>How much of its establishment a country rebuilds towards.</summary>
	public float ArmyRebuildCeiling { get; set; } = 1.0f;

	// --- statecraft ---

	/// <summary>Notoriety earned by declaring a war nobody accepts a reason for.</summary>
	public float NotorietyPerUnjustifiedWar { get; set; } = 18f;

	/// <summary>Notoriety earned per million people annexed by force.</summary>
	public float NotorietyPerMillionTaken { get; set; } = 0.85f;

	/// <summary>Stability lost at home for a war declared with no reason given.</summary>
	public float UnjustifiedWarStabilityCost { get; set; } = 0.06f;

	/// <summary>War support lost for a war declared with no reason given.</summary>
	public float UnjustifiedWarSupportCost { get; set; } = 0.10f;

	/// <summary>Stability lost by a government that refuses to honour its own alliance.</summary>
	public float BrokenPromiseStabilityCost { get; set; } = 0.08f;

	// --- market ---

	/// <summary>How fast prices chase their target. 0 freezes them, 1 snaps instantly.</summary>
	public float PriceAdjustSpeed { get; set; } = 0.25f;

	/// <summary>Share of a shortfall a country can cover by importing from the world market.</summary>
	public float WorldMarketAccess { get; set; } = 0.80f;

	/// <summary>Markup paid on imports from the world market.</summary>
	public float ImportMarkup { get; set; } = 1.06f;

	/// <summary>Share of cross-border goods value reaching the public treasury each week.</summary>
	public float MarketTreasuryTradeShare { get; set; } = 0.08f;

	/// <summary>Annualised GDP growth lost at a complete weighted market shortage.</summary>
	public float ShortageAnnualGrowthPenalty { get; set; } = 0.08f;

    /// <summary>
    /// How much two countries' politics count towards what they make of each other.
    ///
    /// Multiplied by how close their ideologies stand: identical politics are worth
    /// about a third of this in goodwill and opposite ones rather more than that in
    /// suspicion. Zero turns ideology into scenery.
    /// </summary>
    public float IdeologyOpinionWeight { get; set; } = 30f;

    /// <summary>
    /// How fast GDP converges on the market value of what the country actually produced.
    /// Low on purpose: output is recomputed every economic week, and an economy that
    /// repriced itself completely each week would read as noise instead of as a trend a
    /// player can act on. 0 freezes GDP at its calibrated starting value.
    /// </summary>
    public float GdpConvergenceRate { get; set; } = 0.10f;

	/// <summary>Weekly standard-of-living pressure at a complete weighted shortage.</summary>
	public float ShortageSolWeeklyPenalty { get; set; } = 0.004f;

	/// <summary>Weekly interest on the national debt.</summary>
	public float DebtInterest { get; set; } = 0.0008f;

	/// <summary>Share of spare cash put toward the debt each week.</summary>
	public float DebtRepayment { get; set; } = 0.15f;

	/// <summary>Unrest added while the debt is past what lenders will bear.</summary>
	public float BankruptcyUnrest { get; set; } = 0.02f;

	// --- production and wages ---

	/// <summary>Share of a building's revenue that becomes wages rather than profit.</summary>
	public float WageShare { get; set; } = 0.62f;

	/// <summary>Minimum wage per worker per economic tick, paid even at a loss.</summary>
	public float SubsistenceWage { get; set; } = 60f;

	/// <summary>Output a pop produces for itself when it holds no job.</summary>
	public float SubsistenceIncome { get; set; } = 25f;

	// --- pops ---

	/// <summary>Standard of living a pop reaches on a fully satisfied needs basket.</summary>
	public float MaxStandardOfLiving { get; set; } = 28f;

	/// <summary>Monthly growth at the ideal standard of living.</summary>
	public float MaxMonthlyGrowth { get; set; } = 0.006f;

	/// <summary>Monthly decline when starving.</summary>
	public float MaxMonthlyDecline { get; set; } = 0.004f;

	/// <summary>Share of a pop that may change profession in one population tick.</summary>
	public float MobilityRate { get; set; } = 0.035f;

	/// <summary>Radicalism a fully foreign-populated region gains each week.</summary>
	public float MinorityRadicalismPerWeek { get; set; } = 0.0012f;

	/// <summary>Share of a minority that assimilates each week under ideal conditions.</summary>
	public float AssimilationPerWeek { get; set; } = 0.0009f;

	public float RadicalismGain { get; set; } = 0.05f;
	public float RadicalismDecay { get; set; } = 0.03f;

	// --- country ---

	public float StartingTreasury { get; set; } = 5_000_000_000f;
	public float DefaultTaxRate { get; set; } = 0.21f;

	/// <summary>Pounds of upkeep per building level per economic tick.</summary>
	public float BuildingUpkeep { get; set; } = 18000f;

	/// <summary>Pounds of construction funded per day per point of country wealth.</summary>
	public float ConstructionRate { get; set; } = 0.008f;

	// --- military and war ---

	/// <summary>Share of a population that could serve.</summary>
	public float MilitaryEligibleShare { get; set; } = 0.42f;

	/// <summary>
	/// Share of a state's people old enough to work at all - children and the very old
	/// are counted in Population but are not part of the labour force. Without this,
	/// Workforce equalled the whole population and an unemployment rate measured against
	/// it was not really measuring anything.
	/// </summary>
	public float WorkingAgeShare { get; set; } = 0.63f;

	/// <summary>Cost of keeping one division in the field, per economic tick.</summary>
	public float DivisionUpkeep { get; set; } = 2_600_000f;

	/// <summary>
	/// Cost of keeping one warship in commission, on the same clock as DivisionUpkeep.
	/// Set so that a navy the size of America's costs about what its army does, which is
	/// roughly the real ratio.
	/// </summary>
	public float HullUpkeep { get; set; } = 1_100_000f;

	/// <summary>Military budget a country is assumed to be running at its historic fleet size.</summary>
	public float BaselineMilitarySpending { get; set; } = 0.06f;

	/// <summary>
	/// How strongly a fleet follows the military budget away from its historic size. One
	/// means doubling the budget roughly doubles the navy - eventually, at shipyard pace.
	/// </summary>
	public float NavalResponse { get; set; } = 1f;

	/// <summary>Multiplier on the defender's combat power. Attacking is harder.</summary>
	public float DefenderAdvantage { get; set; } = 2.30f;

	/// <summary>
	/// Frontage one province gives an attack, before extra directions widen it. This is
	/// what makes a smaller force able to hold: a province only has so much room, and the
	/// divisions that do not fit wait their turn instead of adding their weight.
	/// </summary>
	public float CombatWidth { get; set; } = 80f;

	/// <summary>What a fully dug-in defender adds to its own defence.</summary>
	public float EntrenchmentBonus { get; set; } = 1.80f;

	/// <summary>Entrenchment a division digs per day while it holds ground.</summary>
	public float EntrenchmentPerDay { get; set; } = 0.030f;

	/// <summary>Entrenchment divisions already hold when a campaign opens mid-war.</summary>
	public float EntrenchmentAtScenarioStart { get; set; } = 0.85f;

	/// <summary>
	/// How much a government has to want a decision before it takes one. Scores come from
	/// `common/scripted_modifiers`; a decision has to beat this to be worth the cooldown it
	/// spends, so a country holds its powder for the month it actually needs it.
	/// </summary>
	public float AiDecisionThreshold { get; set; } = 1.0f;

	/// <summary>
	/// The share of its entrenchment a division keeps when it marches to another province
	/// its own country already holds.
	///
	/// Marching used to cost all of it, and every province a division walks through under
	/// its own orders is friendly ground by definition - ground taken from the enemy is
	/// handled by the fighting, not the marching. So a defender that shifted one sector
	/// along its own line arrived in bare earth, was easier to push out, retreated, shifted
	/// again, and the line unravelled from one reshuffle. Half the seeds ran that loop and
	/// Ukraine lost a third of the country in two years without anybody attacking well.
	///
	/// The works are in the ground as much as in the unit: a line held for months has
	/// positions along its whole length, and a division stepping sideways into them is not
	/// starting from nothing. It is not free either, hence a share rather than all of it.
	/// </summary>
	public float EntrenchmentKeptMovingWithinOwnLines { get; set; } = 0.6f;

	/// <summary>Extra frontage each additional province an attack comes from opens up.</summary>
	public float CombatWidthPerDirection { get; set; } = 20f;

	/// <summary>How many attacking directions can widen a single battle.</summary>
	public int MaxAttackDirections { get; set; } = 4;

	/// <summary>Penalty per unit of relative over-frontage. Crowding a province costs.</summary>
	public float OverWidthPenalty { get; set; } = 0.9f;

	/// <summary>Floor on the crowding penalty, so a stack is never worse than useless.</summary>
	public float MinCrowdingFactor { get; set; } = 0.45f;

	/// <summary>Share of a full attack a defender returns while holding.</summary>
	public float DefenderCounterFire { get; set; } = 1.00f;

	/// <summary>Damage that lands when armour is entirely unpierced.</summary>
	public float UnpiercedDamageFloor { get; set; } = 0.5f;

	/// <summary>Organisation a day of even fighting costs, before the force ratio.</summary>
	public float BaseOrganisationDamage { get; set; } = 0.048f;

	public float MinOrganisationDamage { get; set; } = 0.010f;
	public float MaxOrganisationDamage { get; set; } = 0.130f;

	/// <summary>Share of a formation lost as casualties per point of organisation spent.</summary>
	public float CasualtiesPerOrganisationPoint { get; set; } = 0.075f;

	/// <summary>How much faster a formation with no organisation left bleeds.</summary>
	public float BrokenCasualtyMultiplier { get; set; } = 2.4f;

	/// <summary>
	/// How much more the assaulting side loses for the same day of fighting. Assaulting a
	/// prepared position has never cost what holding one does, and without this an
	/// attrition war is won by whoever simply has more men.
	/// </summary>
	public float AttackerCasualtyMultiplier { get; set; } = 2.4f;

	/// <summary>Entrenchment a day of fighting costs the defender.</summary>
	public float EntrenchmentLostPerCombatDay { get; set; } = 0.020f;

	/// <summary>
	/// Share of a day's digging a division still manages while it is under attack. A
	/// prepared line does not stop being prepared because somebody is shooting at it.
	/// </summary>
	public float EntrenchmentUnderFireShare { get; set; } = 0.75f;

	/// <summary>
	/// Share of a nation's people it can eventually put in uniform over a long war,
	/// standing army included. The reserve is what is left of that after the peacetime
	/// army is subtracted, so this is the ceiling on a war of attrition rather than a
	/// pool anyone can draw at once.
	///
	/// It used to be 2.5 %, which punished exactly the countries that had already
	/// mobilised: Ukraine, whose army is large against its population, was left with a
	/// reserve of 135 000 against two years of fighting that cost it 656 000, ran dry
	/// inside five months and then dissolved - while Russia, subtracting the same army
	/// from four times the people, kept 2.3 million. The Russo-Ukrainian front was not
	/// being decided by the combat model at all; it was a countdown on who emptied first,
	/// and which side that was moved with the seed.
	/// </summary>
	public float MobilisationBaseShare { get; set; } = 0.060f;

	/// <summary>
	/// Floor on the reserve, as a share of the standing army, for a nation whose army is
	/// already larger than its mobilisation base would suggest.
	/// </summary>
	public float ManpowerReserveFloorShare { get; set; } = 0.15f;

	/// <summary>
	/// Replacements a nation can move into the line each day, as a share of its standing
	/// army, before barracks and military spending are counted.
	/// </summary>
	public float ReinforcementCapacityShare { get; set; } = 0.0010f;

	public float BattleBreakOrganisation { get; set; } = 0.055f;
	public float UnopposedCapturePerDay { get; set; } = 0.10f;

	/// <summary>How much each defending division in a neighbouring province slows an
	/// advance into an empty one. Zero turns the front into a race.</summary>
	public float ZoneOfControlResistance { get; set; } = 6.0f;

	/// <summary>
	/// Share of the covering force an advance must be worth before it may walk into an
	/// empty province at all. Below one, a gap behind a held line cannot be exploited
	/// until the flank covering it has been beaten.
	/// </summary>
	public float GapExploitAdvantage { get; set; } = 1.60f;

	/// <summary>
	/// Share of its fresh worth a formation still contributes to covering the ground
	/// beside it, however worn down it is. Zero makes a front evaporate the moment a side
	/// has a bad month; one makes casualties irrelevant to where the line sits.
	/// </summary>
	public float CoveringPresenceFloor { get; set; } = 0.45f;

	// ---------------------------------------------------------------- the navy
	// A fleet is built on the same bones as a division, so its numbers read the same way:
	// something is spent by being at sea and in action, and restored in port.

	/// <summary>Degrees of the globe a fleet in good order covers in a day.</summary>
	public float FleetDegreesPerDay { get; set; } = 3.2f;

	/// <summary>How close two hostile fleets must be before they find each other.</summary>
	public float FleetContactRangeDegrees { get; set; } = 3.0f;

	/// <summary>How far a blockade reaches onto the coast it is sitting on.</summary>
	public float FleetBlockadeRangeDegrees { get; set; } = 6.0f;

	/// <summary>Distance from home at which a fleet is burning stores as fast as it can.</summary>
	public float FleetSupplyRangeDegrees { get; set; } = 45f;

	/// <summary>Share of a navy's budget that reaches it each day.</summary>
	public float FleetReadinessDrift { get; set; } = 0.010f;

	public float FleetSupplyInPortPerDay { get; set; } = 0.090f;
	public float FleetSupplyAtSeaPerDay { get; set; } = 0.024f;
	public float FleetOrganisationInPortPerDay { get; set; } = 0.055f;
	public float FleetOrganisationAtSeaPerDay { get; set; } = 0.020f;
	public float FleetRepairInPortPerDay { get; set; } = 0.008f;

	/// <summary>Cohesion a day of action costs, before the odds are weighed in.</summary>
	public float FleetOrganisationLossPerAction { get; set; } = 0.16f;

	/// <summary>Ships lost for each point of cohesion spent fighting.</summary>
	public float FleetLossesPerOrganisationPoint { get; set; } = 0.085f;

	public float FleetExperiencePerAction { get; set; } = 0.010f;

	/// <summary>Cohesion at which a fleet breaks off and runs for its home port.</summary>
	public float FleetBreakOrganisation { get; set; } = 0.22f;

	/// <summary>Days a fleet needs before it will fight again after breaking off.</summary>
	public int FleetRestAfterActionDays { get; set; } = 14;

	/// <summary>What one hull's presence is worth at raiding, guarding and blockading.</summary>
	public float FleetRaidValue { get; set; } = 0.0016f;
	public float FleetEscortValue { get; set; } = 0.0020f;
	public float FleetBlockadeValue { get; set; } = 0.0024f;

	/// <summary>Share of output that moves by sea, and so can be taken away by a navy.</summary>
	public float FleetCommerceShare { get; set; } = 0.0022f;

	/// <summary>How much of a coastal province's supply a full blockade takes.</summary>
	public float FleetBlockadeSupplyPenalty { get; set; } = 0.55f;

	/// <summary>Divisions one hull can carry. Eighty hulls lift about five divisions.</summary>
	public float FleetDivisionsPerHull { get; set; } = 0.062f;

	/// <summary>How close a formation must be to a fleet to walk up its gangway.</summary>
	public float FleetEmbarkRangeDegrees { get; set; } = 5.0f;

	/// <summary>Cohesion a formation loses coming off a ship onto a hostile shore.</summary>
	public float InvasionLandingOrganisationCost { get; set; } = 0.45f;

	/// <summary>Days a formation needs ashore before it can attack out of its beachhead.</summary>
	public int InvasionRestAfterLandingDays { get; set; } = 3;

	/// <summary>How well a fleet fights with an army on its decks. Below one, badly.</summary>
	public float FleetConvoyCombatPenalty { get; set; } = 0.55f;

	/// <summary>What a day of action at sea costs the troops in the holds.</summary>
	public float InvasionLossesPerActionAtSea { get; set; } = 0.09f;

	/// <summary>Days between one AI fleet reconsidering what it is doing.</summary>
	public int AiFleetDecisionInterval { get; set; } = 9;

	/// <summary>How much stronger a navy must be than its enemy's before it blockades.</summary>
	public float AiFleetOffensiveRatio { get; set; } = 1.35f;

	/// <summary>Formations an AI will put aboard one fleet for a landing.</summary>
	public int AiInvasionMaxFormations { get; set; } = 3;

	/// <summary>How far an AI will look for a beach. The map is the whole planet.</summary>
	public float AiInvasionSearchDegrees { get; set; } = 60f;

	/// <summary>Hulls a country needs before it will contemplate a war across water.</summary>
	public int AiInvasionMinimumHulls { get; set; } = 30;

	/// <summary>How much less a country wants a war it would have to sail to.</summary>
	public float AiOverseasWarAppetite { get; set; } = 0.45f;

	/// <summary>How many overseas neighbours a country weighs up. The nearest few, not all.</summary>
	public int AiOverseasTargetsConsidered { get; set; } = 6;


	/// <summary>
	/// Days a formation spends consolidating on a province it has just taken before it can
	/// attack again. Two looks far too few beside the seven an attack that failed costs -
	/// winning a battle should not be cheaper than losing one - but raising it moved the
	/// front the wrong way, and instructively so. The pause falls on both sides, and what
	/// holds a line here is not that assaults are rare: it is that ground taken is
	/// immediately counter-attacked, off entrenchment the attacker has not had time to dig.
	/// Throttling the tempo throttles the riposte, and the attacker's gains stick. Across
	/// five seeds, ten days handed Russia between twenty-seven and sixty-three provinces
	/// where two handed it between two and thirty-four. It stays at two, on measurement.
	/// </summary>
	public int RestAfterCaptureDays { get; set; } = 2;

	/// <summary>Days a formation needs after an attack of its own has broken down.</summary>
	public int RestAfterSpentAttackDays { get; set; } = 7;

	/// <summary>Days a formation needs after being driven out of a province.</summary>
	public int RestAfterRetreatDays { get; set; } = 7;

	/// <summary>How many provinces out from itself a division is taken to hold ground.</summary>
	public int FrontCoverageRadius { get; set; } = 2;

	/// <summary>What each further ring of that sector is worth against the one inside it.</summary>
	public float FrontCoverageFalloff { get; set; } = 0.45f;

	/// <summary>
	/// Share of a day's casualties who are wounded rather than lost for good, and who
	/// rejoin their own formation once they are treated. Roughly three in four of a
	/// modern army's casualties are wounded and most of those return to duty.
	/// </summary>
	public float WoundedReturningShare { get; set; } = 0.55f;

	/// <summary>Share of a formation's wounded who return to it each day.</summary>
	public float WoundedReturnPerDay { get; set; } = 0.020f;

	/// <summary>Supply a formation needs before its wounded can be treated at all.</summary>
	public float WoundedReturnMinimumSupply { get; set; } = 0.22f;

	/// <summary>Share of the untreated wounded a cut-off formation loses each day.</summary>
	public float WoundedLostWhenUnsuppliedPerDay { get; set; } = 0.030f;
	public float WartimeFrontDeploymentShare { get; set; } = 0.84f;
	public float PeacetimeBorderDeploymentShare { get; set; } = 0.68f;
	public float PlanningGainPerDay { get; set; } = 0.018f;
	public float PlanningLossPerProvince { get; set; } = 0.025f;
	public float PlanningBurnPerCombatDay { get; set; } = 0.055f;
	public float MaxPlanningAttackBonus { get; set; } = 0.30f;
	/// <summary>March multiplier for the strongest mapped river crossing. Rivers never block movement.</summary>
	public float RiverMovementMultiplier { get; set; } = 0.78f;
	/// <summary>Attacker power retained while crossing an ordinary river.</summary>
	public float RiverAttackMultiplier { get; set; } = 0.82f;
	/// <summary>Attacker power retained while crossing a major river.</summary>
	public float MajorRiverAttackMultiplier { get; set; } = 0.70f;
	/// <summary>Small logistics benefit for provinces touching a navigable lake.</summary>
	public float LakeSupplyBonus { get; set; } = 0.08f;

	/// <summary>Supply throughput every recognised home state provides before buildings.</summary>
	public float BaseStateSupplyThroughput { get; set; } = 6f;

	/// <summary>Weekly supply throughput consumed by one deployed division.</summary>
	public float SupplyThroughputPerDivision { get; set; } = 0.12f;

	/// <summary>Supply throughput each level of state infrastructure adds.</summary>
	public float SupplyPerInfrastructure { get; set; } = 2.2f;

	/// <summary>Supply throughput each barracks adds.</summary>
	public float SupplyPerBarracks { get; set; } = 0.9f;

	/// <summary>Maximum number of friendly provinces a land supply network can trace from home.</summary>
	public int SupplyTraceRange { get; set; } = 20;

	/// <summary>War score at which the losing side gives in.</summary>
	// ---- Peace conference: what a demand costs against the war score that buys it ----

	/// <summary>Base price of keeping one province in a written peace.</summary>
	public float PeaceCostPerProvince { get; set; } = 0.9f;

	/// <summary>Multiplier for ground held and secured. What you hold is cheap to keep.</summary>
	public float PeaceCostSecuredFactor { get; set; } = 1.0f;

	/// <summary>Multiplier for ground held but not yet secured.</summary>
	public float PeaceCostHeldFactor { get; set; } = 1.8f;

	/// <summary>Multiplier for ground never taken. A conference is not a land grab.</summary>
	public float PeaceCostUnheldFactor { get; set; } = 4.5f;

	/// <summary>Discount inside the war goal that was actually declared.</summary>
	public float PeaceCostWarGoalFactor { get; set; } = 0.65f;

	/// <summary>Multiplier on the region holding the loser's seat of government.</summary>
	public float PeaceCostCapitalFactor { get; set; } = 2.6f;

	/// <summary>People per province at which the population surcharge reaches one.</summary>
	public float PeaceCostPopulationScale { get; set; } = 400_000f;

	public float PeaceCostReparations { get; set; } = 14f;
	public float PeaceCostTradeConcessions { get; set; } = 11f;
	public float PeaceCostDemilitarise { get; set; } = 22f;
	public float PeaceCostHumiliate { get; set; } = 26f;
	public float PeaceCostLongTruce { get; set; } = 9f;

	public float WarScoreForVictory { get; set; } = 55f;

	/// <summary>Base daily progress with which an occupied province is secured. A local
	/// garrison and good supply accelerate it; unrest slows it. It is a data value so a
	/// scenario can make a short 19th-century campaign or a slow modern occupation.</summary>
	public float OccupationDailyProgress { get; set; } = 0.012f;

	/// <summary>Occupation progress needed before land can survive a status-quo peace.</summary>
	public float OccupationSecureThreshold { get; set; } = 0.70f;

	/// <summary>War support at which an AI side is prepared to accept a defeat even when
	/// its army still has formations on the map.</summary>
	public float WarSupportForCapitulation { get; set; } = 0.18f;

	/// <summary>Chance per day that an AI country with a claim starts a war over it.</summary>
	/// <summary>
	/// Daily chance a country that wants a war actually starts one, multiplied by how
	/// badly it wants it. Small on purpose: the point is that wars happen, not that the
	/// map lurches every time a balance of power tips.
	/// </summary>
	public float AiWarChance { get; set; } = 0.012f;

	/// <summary>Whether countries start wars of their own at all.</summary>
	public bool AiMayDeclareWar { get; set; } = true;

	// ---- Diplomacy: where a relationship naturally settles, and what moves it ----





	/// <summary>Opinion a week of courting a neighbour is worth, against that drift.</summary>
	public float AiCourtshipStep { get; set; } = 6f;

	public float AiFriendshipThreshold { get; set; } = 10f;
	public float AiTradeThreshold { get; set; } = 18f;
	public float AiPactThreshold { get; set; } = 42f;
	public float AiAllianceThreshold { get; set; } = 66f;

	/// <summary>What an offer of alliance has to be worth before a country signs it.</summary>
	public float AllianceWillingness { get; set; } = 0.5f;

	/// <summary>How long an offer put to a player stands before it lapses.</summary>
	public int AllianceOfferDays { get; set; } = 30;

	/// <summary>And how long a refusal is remembered, so that asking again is a decision.</summary>
	public int AllianceRefusalDays { get; set; } = 180;

	/// <summary>What it costs an offer that somebody is already fighting a war.</summary>
	public float AllianceWarPenalty { get; set; } = 0.45f;

	/// <summary>How much of an argument it is that they are larger than you.</summary>
	public float AllianceShelterWeight { get; set; } = 0.30f;

	/// <summary>And how much of one against them that the world thinks badly of them.</summary>
	public float AllianceNotorietyWeight { get; set; } = 0.40f;

	/// <summary>What each shared rival is worth, up to three of them.</summary>
	public float AllianceSharedRivalWeight { get; set; } = 0.12f;
	public float AiRivalryThreshold { get; set; } = -30f;

	/// <summary>
	/// How many countries are large enough that everybody has an opinion about them.
	///
	/// The weekly diplomatic pass walks a country's neighbours, whoever it has signed
	/// something with, and these - so this is the number that decides whether a small
	/// country can look for a friend beyond its own borders at all.
	/// </summary>
	public int AiGreatPowers { get; set; } = 12;

	// ---- Economy: how a country governs itself between crises ----

	public float AiTreasuryFloor { get; set; } = 0.02f;
	public float AiTreasuryComfort { get; set; } = 0.12f;
	public float AiTaxStep { get; set; } = 0.012f;
	public float AiMinimumTaxRate { get; set; } = 0.08f;
	public float AiMaximumTaxRate { get; set; } = 0.46f;
	public float AiWartimeMilitarySpending { get; set; } = 0.16f;
	public float AiTenseMilitarySpending { get; set; } = 0.09f;
	public float AiPeacetimeMilitarySpending { get; set; } = 0.045f;
	public float AiStabilityConcern { get; set; } = 0.52f;
	public float AiSocialStep { get; set; } = 0.006f;
	public float AiBuildTreasuryFloor { get; set; } = 0.03f;
	public int AiMaxConcurrentProjects { get; set; } = 4;

	/// <summary>How much each existing building of a kind discourages another one there.</summary>
	public float AiBuildSaturation { get; set; } = 2.5f;

	// ---- War and peace: when a country reaches for one, and when it puts it down ----

	/// <summary>Share of a nation's GDP that counts as latent military strength.</summary>
	public float AiEconomicWeight { get; set; } = 0.004f;

	/// <summary>Strength ratio below which no war is worth thinking about.</summary>
	public float AiWarMinimumAdvantage { get; set; } = 1.45f;

	public float AiHostilityAppetite { get; set; } = 1.2f;
	public float AiRivalryAppetite { get; set; } = 0.8f;
	public float AiOpportunismAppetite { get; set; } = 1.1f;
	public float AiWarDesireThreshold { get; set; } = 0.55f;

	// --- what a war is worth, and what it costs before a shot is fired ---
	//
	// The old appetite had five terms and every one of them argued for war. These are the
	// other half of the sum, and they are subtracted from it rather than multiplying it, so
	// that a price can win the argument outright.

	/// <summary>How much a large population on the other side of the border is worth taking.</summary>
	public float AiConquestAppetite { get; set; } = 0.85f;

	/// <summary>How much a country wants the regions its own people live in.</summary>
	public float AiIrredentaAppetite { get; set; } = 1.15f;

	/// <summary>How much a country wants a good it is going without and the target sells.</summary>
	public float AiResourceAppetite { get; set; } = 0.70f;

	/// <summary>What losing all trade with the target is worth avoiding.</summary>
	public float AiTradeLossWeight { get; set; } = 2.60f;

	/// <summary>What each ally and guarantor of the target is worth avoiding.</summary>
	public float AiAllyDeterrence { get; set; } = 0.95f;

	/// <summary>What each member of a coalition already formed against us is worth avoiding.</summary>
	public float AiCoalitionDeterrence { get; set; } = 0.60f;

	/// <summary>How much a country's own notoriety restrains it.</summary>
	public float AiNotorietyCaution { get; set; } = 1.30f;

	/// <summary>The cost of going to war with nothing to say about why.</summary>
	public float AiNoCasusBelliCaution { get; set; } = 0.80f;

	/// <summary>How much a tired population restrains its government.</summary>
	public float AiWarWearinessCaution { get; set; } = 1.60f;

	/// <summary>How much unrest at home restrains a government.</summary>
	public float AiInstabilityCaution { get; set; } = 1.40f;

	/// <summary>The restraint of an empty treasury.</summary>
	public float AiPovertyCaution { get; set; } = 0.55f;

	/// <summary>Subtracted from the case for a war that has to be carried across water.</summary>
	public float AiOverseasCaution { get; set; } = 1.35f;

	/// <summary>How long a government will prepare for a war before dropping the idea.</summary>
	public int AiWarPlanPatienceDays { get; set; } = 1460;

	/// <summary>How much more dependent on us a rival must be before we sanction them.</summary>
	public float AiEmbargoLeverage { get; set; } = 0.08f;
	public float AiWarSupportFloor { get; set; } = 0.45f;
	public float AiWarStabilityFloor { get; set; } = 0.35f;

	public int AiMinimumWarDays { get; set; } = 240;

	/// <summary>
	/// How long the AI refuses to negotiate a war the scenario opens with. A war already
	/// in its third year is one both sides have declined to end; the ordinary appetite for
	/// peace does not describe it.
	/// </summary>
	public int ScenarioWarNegotiationLockDays { get; set; } = 1095;
	public int AiPeaceProposalInterval { get; set; } = 45;
	public float AiPeaceMinimumBudget { get; set; } = 35f;

	/// <summary>War score a country needs before an indemnity is worth ending a war for.</summary>
	public float AiPeaceIndemnityBudget { get; set; } = 55f;

	/// <summary>War support at which a country gives up a war it is not winning.</summary>
	public float AiCapitulationWarSupport { get; set; } = 0.30f;

	/// <summary>How much of its war score a winning country spends at the table.</summary>
	public float AiPeaceBudgetShare { get; set; } = 0.85f;

	/// <summary>Whether the AI is allowed to declare war on the player.</summary>
	public bool AiMayAttackPlayer { get; set; } = true;

	// --- optional model advisor ---

	/// <summary>
	/// Off by default. When on, the game asks a local OpenAI-compatible server for the
	/// strategic posture of a few major powers. It never blocks the simulation and the
	/// numeric AI runs regardless.
	/// </summary>
	public bool AdvisorEnabled { get; set; }

	/// <summary>Anything speaking the chat completions shape: llama.cpp, Ollama, LM Studio.</summary>
	public string AdvisorEndpoint { get; set; } = "http://localhost:8080/v1/chat/completions";

	/// <summary>
	/// Model name as the server reports it. Qwen3-4B-Instruct-2507 is the reference
	/// choice: Apache 2.0, about 2.5 GB at Q4_K_M, and the only one of the small open
	/// models with genuinely usable Russian.
	/// </summary>
	public string AdvisorModel { get; set; } = "qwen3-4b-instruct-2507";

	/// <summary>Days between consultations. One country is asked each time.</summary>
	public int AdvisorIntervalDays { get; set; } = 90;

	/// <summary>How many of the largest economies are advised at all.</summary>
	public int AdvisorCountries { get; set; } = 12;

	/// <summary>Requests allowed in flight at once.</summary>
	public int AdvisorMaxConcurrent { get; set; } = 2;

	// --- escape hatch ---

	/// <summary>Extra numbers a mod invents. Read with <see cref="Custom"/> lookups.</summary>
	public Dictionary<string, float> CustomValues { get; set; } = new();

	public float Custom( string key, float fallback = 0f )
		=> CustomValues.TryGetValue( key, out var v ) ? v : fallback;

	/// <summary>Seconds per day for a speed setting of 1..5.</summary>
	public float SecondsForSpeed( int speed )
	{
		if ( SpeedFactors.Count == 0 ) return SecondsPerDay;
		var index = M.Clamp( speed - 1, 0, SpeedFactors.Count - 1 );
		return SecondsPerDay * SpeedFactors[index];
	}

	public int MaxSpeed => M.Max( 1, SpeedFactors.Count );
}
