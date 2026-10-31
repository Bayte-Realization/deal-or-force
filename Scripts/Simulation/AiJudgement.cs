using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// What a government's restraint (common/governments `war_restraint`) costs a war it is
    /// weighing, and how much of that is left when it has a reason it could give the world.
    /// </summary>
    public float AiRestraintWeight { get; set; } = 1.0f;
    public float AiRestraintWithCause { get; set; } = 0.35f;

    /// <summary>
    /// Whether a government without a reason for the war it wants first demands the region it
    /// wants by ultimatum, and how long before it asks the same country again.
    /// </summary>
    public bool AiUltimatumsBeforeWar { get; set; } = true;
    public int AiUltimatumIntervalDays { get; set; } = 365;

    /// <summary>
    /// Whether a government nobody plays needs a casus belli to declare a war of its own.
    /// </summary>
    public bool AiNeedsCauseForWar { get; set; } = true;

    /// <summary>
    /// How grave a remembered grievance has to be before a government will name it as the reason
    /// for a war it wants.
    ///
    /// The scale is the opinion modifier's own weight, read from
    /// <c>common/opinion_modifiers</c> - so this is the one number in the game that says how deep
    /// a grudge has to go, and a mod that decides breaking a pact is a graver insult than the base
    /// game thought makes it a casus belli by that act alone. At -30 the reasons are: a war
    /// declared on us (-60), land taken (-45), a secession (-40), a non-aggression pact broken
    /// (-35), an embargo (-30), arms sent to our enemy (-30), and two covert operations (-25 each).
    /// A denunciation, a moved border marker or a seized fishing boat is not a reason for a war on
    /// its own, however many of them there have been.
    /// </summary>
    public float CasusBelliGrudgeFloor { get; set; } = -30f;

    /// <summary>
    /// How much an authoritarian creed thins the restraint of the arrangement it runs: the
    /// restraint is multiplied by one less this times the ideology's authority above zero. A
    /// presidential republic run on nationalism (authority 0.6) keeps about three fifths of it.
    /// </summary>
    public float AiRestraintAuthority { get; set; } = 0.7f;

    /// <summary>
    /// Two governments both at least this restrained, and what a war between them costs on top:
    /// democracies have very nearly never fought each other.
    /// </summary>
    public float AiDemocraticPeaceRestraint { get; set; } = 1.0f;
    public float AiDemocraticPeacePrice { get; set; } = 2.5f;

    /// <summary>
    /// How much of the strength of a country's allies and guarantors an attacker reckons with
    /// when it weighs whether it is strong enough - the friends do not all come, and not with
    /// everything, but they come.
    /// </summary>
    public float AiAllyCommitment { get; set; } = 0.6f;

    /// <summary>
    /// What it costs to turn on a country we think well of, per hundred points of opinion: the
    /// hostility appetite only ever counted the dislike, so a friendly neighbour cost no more to
    /// invade than an indifferent one.
    /// </summary>
    public float AiFriendshipCaution { get; set; } = 1.5f;

    /// <summary>
    /// How many regions a target has to hold before its weakness is worth all it can be: a
    /// country of one region - the Vatican, San Marino - is a fraction of it, never less than
    /// the floor.
    /// </summary>
    public float AiWorthRegions { get; set; } = 4f;
    public float AiWorthFloor { get; set; } = 0.15f;
}

public sealed partial class Nation
{
    /// <summary>
    /// The war this government has decided it wants but has not started.
    ///
    /// The old AI had no such thing: it asked "do I want a war today" every eleven days,
    /// and either declared one or forgot the question. So it never prepared, never isolated
    /// anybody, never waited for a better moment, and never turned down a war it could have
    /// had later on much better terms. A country with a plan raises its army, courts its
    /// target's friends away, and waits - which is both what a foreign ministry does and
    /// what makes an AI feel like it is thinking rather than rolling dice.
    /// </summary>
    public int WarPlanTargetId = -1;
    public int WarPlanSinceDay = -1;
}

/// <summary>
/// What a government decides, and why.
///
/// The old judgement was an appetite: strength ratio, plus dislike, plus a bonus if the
/// neighbour was already busy, times war support. Everything in it argued for war and
/// nothing argued against, so the only brake on the map was a low random chance - and a
/// hundred wars in two years came out the other end, with the same handful of countries
/// annexing everything and nothing anywhere pushing back.
///
/// What replaces it is a balance sheet. A war has a value: land, people, our own countrymen
/// on the wrong side of a border, a resource we cannot buy. It also has a price, and until
/// now the game could not name a single item of it. Now it can - the trade we lose the day
/// we declare, the allies and guarantors who arrive with the defender, the coalition our
/// last war already earned us, the standing we spend when we cannot say what we are fighting
/// for. A country that wants a war it cannot afford does not forget the idea; it makes a
/// plan, builds, and works on the target's friends until the sum comes out differently.
///
/// The intent is not a passive AI. It is a patient one: fewer wars, chosen better, prepared
/// for, and fought against countries that have been carefully left without friends.
/// </summary>
public static partial class AiStatecraft
{
    /// <summary>
    /// What one country would get out of taking another's ground, in the same units the
    /// cost is priced in - so that the two can simply be subtracted.
    /// </summary>
    private static float WarValue(Campaign campaign, Nation nation, Nation other,
        List<(string Part, float Amount)> parts = null, bool refreshAssessment = true)
    {
        var defines = campaign.Defines;

        // Weakness invites. This is the old rule, kept, because it is true - but weakness is
        // reckoned with the friends who would arrive, as a general staff reckons it: a small
        // country with a great power's guarantee is not a small country. And it is worth what
        // there is to take: the weakness of a country of one region is a fraction of the same
        // weakness in a country of ten.
        var mine = Strength(campaign, nation);
        var theirs = CampaignIntel.EstimatedStrength(campaign, nation.Id, other, refreshAssessment) +
                     HelpFor(campaign, nation, other, refreshAssessment) * defines.AiAllyCommitment;
        if (mine <= 0.01f) return 0f;
        var advantage = mine / M.Max(0.01f, theirs);
        var relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
        var minimumAdvantage = MinimumAdvantageFor(campaign, nation, other, relation);
        if (advantage < minimumAdvantage) return 0f;
        var regionsHeld = refreshAssessment ? RegionsHeldBy(campaign, other.MapIndex) : campaign.States.Count(s => s.NationMapIndex == other.MapIndex);
        var worth = M.Clamp(regionsHeld / M.Max(1f, defines.AiWorthRegions),
            defines.AiWorthFloor, 1f);
        parts?.Add(($"advantage {advantage:0.00} (ours {mine:0} vs theirs {theirs:0})", 0f));
        // Weakness makes a war possible; it does not make it wanted. A government with no
        // reason it could give - no lost ground, no countrymen across the border, no ally under
        // attack - counts a neighbour's weakness for a fraction of what it would with one:
        // Turkey looked at Georgia, saw an army a fifteenth of its own and nothing else, and
        // that alone was enough for an invasion in most runs.
        var cause = !string.IsNullOrEmpty(Statecraft.CasusBelli(campaign, nation, other));
        var value = Part(parts, "weakness", M.Clamp(advantage - minimumAdvantage, 0f, 4f) * worth *
                                            (cause ? 1f : defines.AiWeaknessWithoutCause));

        // What is actually over there. A neighbour with forty million people is worth more
        // than one with four, and the old rule could not tell them apart.
        var people = regionsHeld / 30f;
        value += Part(parts, "people", M.Clamp(people, 0f, 2.2f) * defines.AiConquestAppetite);

        // Our own people on the wrong side of the border. The sharpest motive there is, and
        // the demography already knows the answer region by region.
        value += Part(parts, "irredenta", Irredentism(campaign, nation, other) * defines.AiIrredentaAppetite);

        // And a crisis between the two: shooting at the border, a grievance from the last war
        // (Escalation). What makes a war between old enemies likelier than one between strangers.
        value += Part(parts, "escalation", Escalation.WarAppetite(campaign, nation, other) * defines.AiEscalationAppetite);

        // And ground that was ours. A region that seceded, rose or was signed away is still on
        // every map the country prints; while the other side holds it, that is a reason.
        value += Part(parts, "revanche", LostLands.Weight(campaign, nation, other) * defines.AiRevancheAppetite);

        // And a member of the union we are bringing together, holding out - counted only by a
        // government that wants the union badly enough (Consolidation). It still needs a cause.
        value += Part(parts, "union", Consolidation.WarValue(campaign, nation, other));

        // Something we cannot buy. A country short of a good the target sells has a reason
        // that a country with a full market does not - which is the whole point of having
        // made trade bilateral.
        value += Part(parts, "resources", Covets(campaign, nation, other) * defines.AiResourceAppetite);

        // And the old motives, which were the only motives.
        if (relation is not null)
        {
            value += Part(parts, "hostility", M.Clamp(-relation.Opinion / 100f, 0f, 1f) * defines.AiHostilityAppetite);
            if (relation.Stance == DiplomaticStance.Rival) value += Part(parts, "rivalry", defines.AiRivalryAppetite);
            if (OpinionSystem.Has(relation, "unfinished_war_claim"))
                value += Part(parts, "unfinished goals", defines.AiUnfinishedGoalAppetite * AiTemperament.OpportunismOf(campaign, nation));
        }

        // Target vulnerability: a neighbour reeling from exhaustion (low war support or collapsed stability) is an inviting opening for opportunists.
        var targetExhaustion = 0f;
        if (other.WarSupport < defines.AiTargetExhaustionWarSupport)
            targetExhaustion += (defines.AiTargetExhaustionWarSupport - other.WarSupport) / M.Max(0.01f, defines.AiTargetExhaustionWarSupport);
        if (other.Stability < defines.AiTargetExhaustionStability)
            targetExhaustion += (defines.AiTargetExhaustionStability - other.Stability) / M.Max(0.01f, defines.AiTargetExhaustionStability);

        if (targetExhaustion > 0.05f)
        {
            value += Part(parts, "target exhaustion", targetExhaustion * defines.AiTargetExhaustionMultiplier * AiTemperament.OpportunismOf(campaign, nation));
        }

        // Target distress: a neighbour defaulting on sovereign obligations, gripped by domestic
        // coup danger, or crippled by runaway inflation invites opportunists to press claims.
        var targetDistress = 0f;
        if (campaign.FeatureEnabled("public_debt"))
        {
            if (other.DebtCondition >= DebtCondition.Insolvent)
                targetDistress += 1.0f;
            else if (other.DebtCondition == DebtCondition.Default)
                targetDistress += 0.75f;
            else if (other.DebtCondition == DebtCondition.Restructuring)
                targetDistress += 0.40f;
            else if (other.DebtCondition == DebtCondition.Stressed)
                targetDistress += 0.15f;
        }

        if (campaign.FeatureEnabled("domestic_crises") && other.DomesticCrisis is { } crisis)
        {
            if (crisis.CoupPressure > 0.35f)
                targetDistress += (crisis.CoupPressure - 0.35f) * 1.2f;
            if (crisis.DaysAboveThreshold > 0)
                targetDistress += M.Clamp(crisis.DaysAboveThreshold / 30f, 0f, 0.5f);
        }

        if (other.Inflation > 0.15f)
        {
            targetDistress += M.Clamp((other.Inflation - 0.15f) / 0.30f, 0f, 0.6f);
        }

        if (targetDistress > 0.05f)
        {
            value += Part(parts, "target distress", targetDistress * defines.AiTargetDistressMultiplier * AiTemperament.OpportunismOf(campaign, nation));
        }
        // A country already at war is an opening - unless the only war it is in is with its own
        // rebels. Neighbours of a civil war arm a side; they do not invade: China armed both the
        // Myanmar army and the northern alliance in 2024, and a China that marched on Mandalay
        // because the junta was busy in Rakhine was the opportunism rule reading the wrong war.
        if (AtWarWithAnybody(campaign, other))
        {
            if (OnlyCivilWars(campaign, other))
            {
                value += Part(parts, "opportunity", -defines.AiCivilWarRestraint);
            }
            else
            {
                // An opening is not one flat fact about the enemy. It is a fact about the enemy
                // and about the government looking at it, and until now it was priced as neither:
                // a country fighting three wars and losing all of them was worth exactly the same
                // bonus as one fighting a single war and winning it, and every government in the
                // world noticed openings equally. The stretch is measured, and the noticing is
                // this government's own character.
                var opening = defines.AiOpportunismAppetite *
                              (1f + Stretch(campaign, other) * defines.AiOpportunismStretch) *
                              AiTemperament.OpportunismOf(campaign, nation);
                value += Part(parts, "opportunity", opening);
            }
        }

        return value;
    }

    /// <summary>
    /// The strength ratio a government asks for before weighing a war at all.
    ///
    /// An unprovoked war of choice demands the full <see cref="Defines.AiWarMinimumAdvantage"/>.
    /// A country facing a rival, a hot border crisis, or a neighbour in severe domestic/fiscal
    /// distress will weigh a closer contest near parity, using only O(1) relation and target fields.
    /// </summary>
    private static float MinimumAdvantageFor(Campaign campaign, Nation nation, Nation other, DiplomaticRelation relation)
    {
        var floor = campaign.Defines.AiWarMinimumAdvantage;
        var discount = 0f;
        if (relation is not null)
        {
            if (relation.Stance == DiplomaticStance.Rival || relation.Opinion <= -25f)
                discount += 0.30f;
            if (Escalation.StageOf(relation) >= TensionStage.Friction)
                discount += 0.15f;
        }
        if (other.Stability < campaign.Defines.AiTargetExhaustionStability ||
            other.WarSupport < campaign.Defines.AiTargetExhaustionWarSupport ||
            other.DebtCondition >= DebtCondition.Default)
            discount += 0.15f;
        return discount <= 0f ? floor : M.Clamp(floor - discount, 1.00f, floor);
    }

    /// <summary>
    /// How thin an enemy is spread: how many separate wars it is fighting, and how badly the
    /// worst of them is going. One front and holding is no opening at all; three fronts and
    /// losing is an invitation. Civil conflicts are not counted - see the caller - and neither
    /// are wars that have ended.
    ///
    /// Public for the same reason <see cref="Restraint"/> and <see cref="Deterrence"/> are: it is
    /// a read-only strategic question that a bench, a test or a console has to be able to ask,
    /// and the only other way to see it would be to parse it back out of a sentence.
    /// </summary>
    public static float Stretch(Campaign campaign, Nation other)
    {
        var fronts = 0;
        var worst = 0f;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || war.CivilConflict || !war.Involves(other.Id)) continue;
            fronts++;
            // Read from our side of the war: WarScore is positive for the attacker, so a target
            // that is attacking and winning scores negative here, and one that is losing scores
            // positive. Only the bad news about them is of any interest to us.
            var against = war.IsAttacking(other.Id) ? -war.WarScore : war.WarScore;
            if (against > worst) worst = against;
        }
        if (fronts == 0) return 0f;
        return M.Clamp((fronts - 1) * 0.5f + M.Clamp01(worst / 100f), 0f, 2f);
    }

    /// <summary>
    /// What the war would cost before a shot is fired.
    ///
    /// Every item here is something the game could not previously say out loud, which is why
    /// the old AI behaved as though war were free. It very nearly was.
    /// </summary>
    private static float WarPrice(Campaign campaign, Nation nation, Nation other,
        List<(string Part, float Amount)> parts = null)
    {
        var defines = campaign.Defines;
        var price = 0f;

        // The trade we lose on the morning of the declaration. For a country that runs half
        // its economy through its neighbour this is the whole argument.
        price += Part(parts, "trade", nation.TradeDependenceOn(other.Id) * defines.AiTradeLossWeight);
        var commodityVulnerability = CommodityLeverage.StrategicVulnerability(campaign, nation, other);
        if (commodityVulnerability > 0.02f)
            price += Part(parts, "strategic dependence", commodityVulnerability * defines.AiTradeLossWeight * 0.75f);

        // Everybody who arrives with them. Each is a separate war on a separate front, and
        // since the call to arms became real this is no longer a hypothetical. Soft-saturated
        // past the first two defenders because their actual military strength is already in HelpFor.
        var foresight = AiTemperament.ForesightOf(campaign, nation);
        var friends = Statecraft.DefendersOf(campaign, other);
        var effectiveFriends = friends <= 2 ? friends : 2f + MathF.Sqrt(friends - 2) * 0.75f;
        price += Part(parts, "their friends", effectiveFriends * defines.AiAllyDeterrence * foresight);

        // What we are already carrying. A country the world is organising against does not
        // help itself by taking something else.
        price += Part(parts, "notoriety", nation.Notoriety / 100f * defines.AiNotorietyCaution);
        if (Statecraft.CoalitionAgainst(campaign, nation.Id) is { } coalition)
            price += Part(parts, "coalition", coalition.MemberIds.Count * defines.AiCoalitionDeterrence * foresight);

        // And the standing we spend if we cannot say why. A country with a reason is far
        // more likely to act on it, which is exactly how it should read.
        var cause = !string.IsNullOrEmpty(Statecraft.CasusBelli(campaign, nation, other));
        if (!cause) price += Part(parts, "no reason", defines.AiNoCasusBelliCaution);

        // What kind of government is deciding. A parliament has to be carried, a press read
        // and an election faced; a junta answers to nobody. A reason the world would accept
        // takes most of it away - and two restrained governments almost never fight at all.
        // Turning on a friend. The opinion it costs is the least of it: everybody watching
        // learns what this country's friendship is worth.
        var regard = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id)?.Opinion ?? 0f;
        if (regard > 0f) price += Part(parts, "friendship", regard / 100f * defines.AiFriendshipCaution);

        // A lease between them: the tenant would lose its base, the owner its rent, and a
        // garrison on the owner's soil is not a border to march across. Guantanamo made the
        // United States a land neighbour of Cuba, and the computer invaded it in the spring.
        if (campaign.LandArrangements.Count > 0 && campaign.LandArrangements.Any(a => a.Kind == LandArrangementSystem.Lease &&
                ((a.HolderId == nation.Id && a.OwnerId == other.Id) || (a.HolderId == other.Id && a.OwnerId == nation.Id))))
            price += Part(parts, "lease", defines.AiLeaseWarCaution);

        // And turning on a country we are actually allied to, which is a different thing from
        // overcoming a fondness for one. Not a dislike defeated but a promise broken in public,
        // with everybody watching to see what this country's signature turns out to be worth.
        if (CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is { Stance: DiplomaticStance.Allied })
            price += Part(parts, "betrayal", defines.AiBetrayalNotoriety * 0.1f);

        var restraint = Restraint(campaign, nation);
        price += Part(parts, "restraint", restraint * defines.AiRestraintWeight * (cause ? defines.AiRestraintWithCause : 1f));
        if (restraint >= defines.AiDemocraticPeaceRestraint &&
            Restraint(campaign, other) >= defines.AiDemocraticPeaceRestraint)
            price += Part(parts, "democratic peace", defines.AiDemocraticPeacePrice * (cause ? 0.6f : 1f));

        // Two countries that each hold a deterrent the other cannot survive using do not
        // fight directly: they fight by proxy, by sanction and by patience. The price is
        // what makes that true in the sum rather than in a script - and it is paid only
        // between holders, so a holder's war against anybody else stays an ordinary war.
        var deterrent = Simulation.Deterrence.MutualPrice(campaign, nation, other);
        if (deterrent > 0f) price += Part(parts, "mutual deterrence", deterrent);
        // And a country under an alarmed holder's umbrella borrows a share of that wall.
        var umbrella = NuclearPosture.UmbrellaPrice(campaign, nation, other, out _);
        if (umbrella > 0f) price += Part(parts, "nuclear umbrella", umbrella);

        // The state of the country itself. A tired, poor, unstable government has other
        // problems, and the old rule folded this into a multiplier where it could never
        // outweigh an appetite.
        price += Part(parts, "war weariness", M.Clamp(0.75f - nation.WarSupport, 0f, 0.75f) * defines.AiWarWearinessCaution);
        price += Part(parts, "instability", M.Clamp(0.70f - nation.Stability, 0f, 0.70f) * defines.AiInstabilityCaution);
        if (nation.Gdp > 0.01f && nation.Treasury / nation.Gdp < defines.AiTreasuryFloor)
            price += Part(parts, "poverty", defines.AiPovertyCaution);
        if (campaign.FeatureEnabled("public_debt") && nation.DebtCondition >= DebtCondition.Default)
            price += Part(parts, "insolvency", defines.AiInsolvencyCaution);
        if (campaign.FeatureEnabled("domestic_crises") && nation.DomesticCrisis?.CoupPressure > 0.40f)
            price += Part(parts, "domestic coup risk", (nation.DomesticCrisis.CoupPressure - 0.40f) * defines.AiDomesticCrisisCaution);

        return price;
    }

    private static float Part(List<(string Part, float Amount)> parts, string name, float amount)
    {
        if (parts is not null && MathF.Abs(amount) > 0.0001f) parts.Add((name, amount));
        return amount;
    }

    /// <summary>
    /// The balance sheet as data rather than as a sentence.
    ///
    /// <see cref="ExplainWar"/> answers "why did this country attack that one". This answers
    /// "why does nobody attack anybody", which is a different question and the one a bench has to
    /// be able to ask - and it cannot be answered by reading a hundred thousand sentences. It is
    /// the same arithmetic either way, one implementation, so a change to what a war costs cannot
    /// make the reported sentence and the total disagree.
    /// </summary>
    public static (float Value, float Price,
                   List<(string Part, float Amount)> ValueParts,
                   List<(string Part, float Amount)> PriceParts)
        JudgeWarParts(Campaign campaign, Nation nation, Nation other, bool refreshAssessment = true)
    {
        var value = new List<(string Part, float Amount)>();
        var price = new List<(string Part, float Amount)>();
        if (campaign is null || nation is null || other is null) return (0f, 0f, value, price);
        var worth = WarValue(campaign, nation, other, value, refreshAssessment);
        var cost = WarPrice(campaign, nation, other, price);
        return (worth, cost, value, price);
    }

    /// <summary>
    /// The balance sheet behind one government's view of one war, item by item: what it would
    /// get, what it would pay, and the sum. For the bench and the console, which until now could
    /// say that Turkey attacked Georgia but not why.
    ///
    /// The sum reported here is the sum <see cref="WarJudgement"/> actually reaches, nerve and
    /// all. Reporting the arithmetic without the bar an alliance sets would let the console show
    /// a war this government would never start, and the whole use of this method is that what it
    /// says is what the country decided.
    /// </summary>
    public static string ExplainWar(Campaign campaign, Nation nation, Nation other)
    {
        if (campaign is null || nation is null || other is null) return "";
        var (worth, cost, value, price) = JudgeWarParts(campaign, nation, other);
        var cause = Statecraft.CasusBelli(campaign, nation, other);
        if (Statecraft.AllyUnderAttack(campaign, nation, other) is { } ward) cause += $" ({ward.Tag})";
        var sum = worth <= 0f ? -10f : worth - cost;
        var allied = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is { Stance: DiplomaticStance.Allied };
        var nerve = allied
            ? campaign.Defines.AiBetrayalNerve / M.Max(0.2f, AiTemperament.CunningOf(campaign, nation))
            : 0f;
        var judgement = allied && sum < nerve ? -10f : sum;
        return $"{nation.Tag}->{other.Tag} value={worth:0.00} [{string.Join(", ", value.Select(p => p.Amount == 0f ? p.Part : $"{p.Part} {p.Amount:0.00}"))}] " +
               $"price={cost:0.00} [{string.Join(", ", price.Select(p => $"{p.Part} {p.Amount:0.00}"))}] " +
               $"judgement={judgement:0.00}" + (allied ? $" nerve={nerve:0.00}" : "") + $" cause='{cause}'";
    }

    /// <summary>
    /// The whole sum. Positive means the war is worth having; how positive decides whether
    /// it is worth having now or worth preparing for.
    ///
    /// An alliance is the one relationship the sum used to be refused for without being worked
    /// out at all - a flat -10, so no government in the world would ever break one for profit
    /// and every alliance was a permanent fact rather than an arrangement. It is now priced like
    /// anything else (see the "betrayal" item of <see cref="WarPrice"/>) and set against a nerve
    /// that only an overwhelming case clears, lowered for a government whose character is to keep
    /// a promise only as long as it pays. Reaching the nerve decides that a country wants to
    /// turn; turning itself is a separate open act, in <c>Warmaking</c>, and still needs a reason
    /// it can give - which in practice means an ultimatum its ally refuses.
    /// </summary>
    private static float WarJudgement(Campaign campaign, Nation nation, Nation other)
    {
        var relation = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id);
        var value = WarValue(campaign, nation, other);
        if (value <= 0f) return -10f;
        var sum = value - WarPrice(campaign, nation, other);
        if (relation is not { Stance: DiplomaticStance.Allied }) return sum;
        var nerve = campaign.Defines.AiBetrayalNerve / M.Max(0.2f, AiTemperament.CunningOf(campaign, nation));
        return sum >= nerve ? sum : -10f;
    }

    /// <summary>
    /// The same sum, for anything outside the AI that wants to know what a government makes
    /// of a war - which is to say, for the bench check that holds the price to account.
    /// </summary>
    public static float JudgeWarFor(Campaign campaign, Nation nation, Nation other) =>
        WarJudgement(campaign, nation, other);

    /// <summary>How much of the target's population is our own people, as a share of ours.</summary>
    private static float Irredentism(Campaign campaign, Nation nation, Nation other)
    {
        if (!CampaignIntel.CanInspect(nation.Id, other.Id)) return 0f;
        if (!CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, other.MapIndex)) return 0f;
        var ours = 0L;
        foreach (var state in campaign.States)
        {
            if (state.NationMapIndex != other.MapIndex) continue;
            foreach (var group in state.PopulationGroups)
                if (DemographyCatalog.IsPrimary(nation.Tag, group.Culture)) ours += group.Size;
        }
        if (ours <= 0) return 0f;
        return M.Clamp(ours / 12_000_000f, 0f, 2.5f);
    }

    /// <summary>
    /// Whether the target sells something we are short of and cannot get elsewhere.
    ///
    /// Only counted for goods we are actually going without: wanting more of something the
    /// market already supplies is not a war aim, it is a purchase order.
    /// </summary>
    private static float Covets(Campaign campaign, Nation nation, Nation other)
    {
        if (!CampaignIntel.CanInspect(nation.Id, other.Id)) return 0f;
        var hunger = 0f;
        foreach (var (commodity, quote) in nation.Market)
        {
            if (quote.Satisfaction > 0.88f) continue;
            if (!other.Market.TryGetValue(commodity, out var theirs)) continue;
            if (theirs.Production <= theirs.Demand) continue;
            hunger += (1f - quote.Satisfaction) * CommodityCatalog.Get(commodity).EssentialWeight;
        }
        return M.Clamp(hunger * 0.5f, 0f, 1.5f);
    }

    /// <summary>
    /// How hard it is for this government to start a war of choice: what its arrangement puts
    /// in the way (`war_restraint`), thinned by how authoritarian the creed running it is - a
    /// presidential republic run by a nationalist is not the same brake as one run by a liberal.
    /// </summary>
    public static float Restraint(Campaign campaign, Nation nation)
    {
        var arrangement = PoliticsCore.GovernmentOf(nation).WarRestraint;
        var authority = M.Max(0f, PoliticsCore.IdeologyOf(nation).Authority);
        return arrangement * M.Max(0f, 1f - campaign.Defines.AiRestraintAuthority * authority);
    }

    private static readonly Dictionary<int, List<int>> FriendsOf = new();
    private static int _friendsDay = int.MinValue;
    private static Campaign _friendsFor;

    /// <summary>
    /// What attacking this country would mean facing, as the attacker reckons it: its forces
    /// and its friends'. The friends are everybody who would answer an attack - its allies
    /// and guarantors - not the attacker itself, and not a friend it shares with the target:
    /// a country allied to both stays out. Who is whose friend is worked out once a day, not
    /// once a question: the question is asked of every neighbour of every country every
    /// eleven days.
    /// </summary>
    internal static float Deterrence(Campaign campaign, Nation attacker, Nation target, bool refreshAssessment = true) =>
        CampaignIntel.EstimatedStrength(campaign, attacker.Id, target, refreshAssessment) +
        HelpFor(campaign, attacker, target, refreshAssessment) * campaign.Defines.AiAllyCommitment;

    private static float HelpFor(Campaign campaign, Nation attacker, Nation target, bool refreshAssessment = true)
    {
        if (refreshAssessment && (_friendsFor != campaign || _friendsDay != campaign.ElapsedDays))
        {
            _friendsFor = campaign;
            _friendsDay = campaign.ElapsedDays;
            foreach (var list in FriendsOf.Values) list.Clear();
            foreach (var relation in campaign.Relations.Values)
            {
                var allied = relation.Stance == DiplomaticStance.Allied;
                if (allied || relation.GuaranteedBy(relation.A)) Friend(relation.B, relation.A);
                if (allied || relation.GuaranteedBy(relation.B)) Friend(relation.A, relation.B);
            }
        }
        var help = 0f;
        // A faction's patrons are neither its allies nor at war for it, but their soldiers,
        // advisers and money are on the ground, and a general staff counts them (Factions):
        // the Americans in Hasakah are why Turkey's threatened offensive of 2022 never came.
        if (Factions.Of(target) is { Patrons.Count: > 0 } faction)
            foreach (var tag in faction.Patrons)
                if (campaign.NationByTag(tag) is { Population: > 0 } patron && patron.Id != attacker.Id)
                    help += CampaignIntel.EstimatedStrength(campaign, attacker.Id, patron, refreshAssessment) * campaign.Defines.FactionPatronDeterrence;
        var partners = InternationalOrganizations.DefencePartners(campaign, target.Id).ToArray();
        var shared = InternationalOrganizations.DefencePartners(campaign, attacker.Id).ToHashSet();
        foreach (var id in partners)
            if (id != attacker.Id && !shared.Contains(id) && campaign.NationById(id) is { Population: > 0 } partner)
                help += CampaignIntel.EstimatedStrength(campaign, attacker.Id, partner, refreshAssessment);
        IEnumerable<int> friends;
        if (refreshAssessment)
        {
            if (!FriendsOf.TryGetValue(target.Id, out var cached)) return help;
            friends = cached;
        }
        else
        {
            // Viewing a page must not prime the planner's day cache. A treaty signed
            // later today must be seen by its first real strategic assessment.
            friends = campaign.Relations.Values.Where(r =>
                    r.A == target.Id && (r.Stance == DiplomaticStance.Allied || r.GuaranteedBy(r.B)) ||
                    r.B == target.Id && (r.Stance == DiplomaticStance.Allied || r.GuaranteedBy(r.A)))
                .Select(r => r.A == target.Id ? r.B : r.A);
        }
        foreach (var friendId in friends)
        {
            if (friendId == attacker.Id) continue;
            if (partners.Contains(friendId)) continue;
            var friend = campaign.NationById(friendId);
            if (friend is null || friend.Population <= 0) continue;
            var theirs = CampaignDiplomacy.Peek(campaign, attacker.Id, friendId);
            if (theirs is { Stance: DiplomaticStance.Allied }) continue;
            var commitment = theirs is { NonAggressionPact: true } ? 0.35f : 1f;
            if (campaign.Wars.Any(w => !w.Ended && w.CivilConflict && w.Involves(friendId))) commitment *= 0.2f;
            if (campaign.FeatureEnabled("public_debt") && friend.DebtCondition >= DebtCondition.Default) commitment *= 0.5f;
            if (friend.Stability < 0.35f) commitment *= 0.5f;
            help += CampaignIntel.EstimatedStrength(campaign, attacker.Id, friend, refreshAssessment) * commitment;
        }
        return help;

        static void Friend(int protectedId, int friendId)
        {
            if (!FriendsOf.TryGetValue(protectedId, out var list)) FriendsOf[protectedId] = list = new List<int>();
            list.Add(friendId);
        }
    }

    private static bool OnlyCivilWars(Campaign campaign, Nation nation)
    {
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nation.Id) && !war.CivilConflict) return false;
        return true;
    }

    private static bool AtWarWithAnybody(Campaign campaign, Nation nation)
    {
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nation.Id)) return true;
        return false;
    }

    // ------------------------------------------------------------- patience -------------

    /// <summary>
    /// What a country does about a war it wants and cannot yet afford.
    ///
    /// This is the difference between an AI that is less aggressive and one that is merely
    /// slower. It keeps the target, spends on the army, and - the part that actually
    /// changes the map - works on whoever would come to the target's aid, because the
    /// cheapest way to win a war is to arrange that only one country turns up to it.
    /// </summary>
    private static void PursueWarPlan(Campaign campaign, Nation nation)
    {
        if (nation.WarPlanTargetId < 0) return;
        var target = campaign.NationById(nation.WarPlanTargetId);
        if (target is null || target.Population <= 0 || AtWarWithAnybody(campaign, nation))
        {
            ClearWarPlan(nation);
            return;
        }

        // A plan is not a life sentence. If the sum has stopped making sense - they found
        // friends, we found a coalition, our people stopped wanting it - it is dropped. And how
        // long a government will hold a plan before it loses interest is its own character: a
        // far-sighted one waits years for the moment it prepared for, a short-sighted one drifts
        // off it and finds something else to want next month.
        var judgement = WarJudgement(campaign, nation, target);
        var patience = campaign.Defines.AiWarPlanPatienceDays * AiTemperament.ForesightOf(campaign, nation);
        var hasCause = !string.IsNullOrEmpty(Statecraft.CasusBelli(campaign, nation, target));
        var planFloor = hasCause ? -1.5f : 0f;
        if (judgement <= planFloor || campaign.ElapsedDays - nation.WarPlanSinceDay > patience)
        {
            ClearWarPlan(nation);
            return;
        }

        // Isolate them. One friend courted away is worth more than a year of recruiting - and a
        // government that can see further than the next quarter does not stop at one, because a
        // target with three friends is not isolated by one of them drifting. The order is the
        // cheapest bond first: the friend that already likes the target least and us most.
        PlanCandidates.Clear();
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != target.Id && relation.B != target.Id) continue;
            var friendId = relation.Other(target.Id);
            if (friendId == nation.Id) continue;
            if (relation.Stance != DiplomaticStance.Allied && !relation.GuaranteedBy(friendId)) continue;
            var friend = campaign.NationById(friendId);
            if (friend is null || friend.Population <= 0) continue;
            // What the friend thinks of us is the other half of how cheaply it can be moved. A
            // bond of ten with a country that already dislikes us is not a bond that will come
            // away, whatever the number on the other side of it says - and the old rule read only
            // that other side, so it spent the year courting the one friend that hated us most.
            var theirs = CampaignDiplomacy.Peek(campaign, nation.Id, friendId)?.Opinion ?? 0f;
            PlanCandidates.Add((friend, relation.Opinion - theirs * 0.5f));
        }
        if (PlanCandidates.Count == 0) return;
        PlanCandidates.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Friend.Id.CompareTo(b.Friend.Id));

        var reach = 1 + (int)MathF.Floor(M.Max(0f, campaign.Defines.AiForesightReach) *
                                         M.Clamp01(AiTemperament.Of(campaign, nation).Foresight));
        var worked = 0;
        foreach (var (friend, _) in PlanCandidates)
        {
            if (worked >= reach) return;
            var ours = CampaignDiplomacy.Get(campaign, nation.Id, friend.Id);
            if (campaign.ElapsedDays - ours.LastActionDay < 14) continue;
            if (nation.Treasury < 0.1f) return;
            nation.Treasury -= 0.1f;
            OpinionSystem.Remember(campaign, ours, "improved_relations");
            ours.LastActionDay = campaign.ElapsedDays;
            var targetBond = CampaignDiplomacy.Peek(campaign, target.Id, friend.Id);
            if (!friend.IsHuman && targetBond is not null &&
                targetBond.Stance != DiplomaticStance.Allied &&
                targetBond.GuaranteedBy(friend.Id) &&
                ours.Opinion >= targetBond.Opinion + 15f && ours.Opinion >= 25f)
            {
                new GuaranteeCommand { IssuerId = friend.Id, TargetId = target.Id, Guarantee = false }.Apply(campaign);
                Count("guarantee_withdrawn");
            }
            worked++;
        }
    }

    /// <summary>Reused rather than allocated: this runs for every country holding a plan.</summary>
    private static readonly List<(Nation Friend, float Score)> PlanCandidates = new();

    private static void ClearWarPlan(Nation nation)
    {
        nation.WarPlanTargetId = -1;
        nation.WarPlanSinceDay = -1;
    }

    // ------------------------------------------------------------- soft power -----------

    /// <summary>
    /// The things a government does instead of declaring war.
    ///
    /// The old AI's entire non-military vocabulary was "improve relations with whoever I
    /// like most" and "call whoever I like least a rival". These are the moves a country
    /// with a rival and no appetite for a war actually makes, and each of them is now
    /// something the rest of the simulation can feel: an embargo cuts real goods, a
    /// guarantee brings a real army, and a coalition is a real bloc.
    /// </summary>
    internal static bool ActShortOfWar(Campaign campaign, Nation nation, Nation rival)
    {
        if (rival is null) return false;
        var relation = CampaignDiplomacy.Get(campaign, nation.Id, rival.Id);
        if (campaign.ElapsedDays - relation.LastActionDay < 21) return false;

        // Treaties are commitments, not permanent shackles. A government that has made
        // this country its rival must first pay the normal political price to break the
        // promises that would otherwise keep it allied to its own embargo target forever.
        if (relation.TradeAgreement)
        {
            new CancelAgreementCommand
            {
                IssuerId = nation.Id, TargetId = rival.Id, Kind = AgreementKind.TradeAgreement
            }.Apply(campaign);
            Count("trade_cancelled");
            return true;
        }
        if (relation.NonAggressionPact)
        {
            new CancelAgreementCommand
            {
                IssuerId = nation.Id, TargetId = rival.Id, Kind = AgreementKind.NonAggressionPact
            }.Apply(campaign);
            Count("pact_broken");
            return true;
        }

        // Sanctions, if we can stand to lose the trade and they cannot. A country that runs
        // more of its economy through us than we do through it is a country we can squeeze.
        var theirDependence = rival.TradeDependenceOn(nation.Id) + CommodityLeverage.Calculate(campaign, nation, rival) * 0.5f;
        var ourDependence = nation.TradeDependenceOn(rival.Id) + CommodityLeverage.Calculate(campaign, rival, nation) * 0.5f;
        if (!relation.EmbargoBy(nation.Id) && relation.Opinion <= campaign.Defines.AiRivalryThreshold &&
            theirDependence > ourDependence + campaign.Defines.AiEmbargoLeverage)
        {
            new SetEmbargoCommand { IssuerId = nation.Id, TargetId = rival.Id, Embargo = true }.Apply(campaign);
            Count("embargo_started");
            return true;
        }

        // Or shelter whoever is next on their list. A guarantee costs nothing today and is
        // the cheapest way a large country has of making a small one not worth invading.
        Nation shelter = null;
        var mostExposed = 0f;
        foreach (var mapIndex in CampaignDiplomacy.Neighbours(campaign, rival.MapIndex))
        {
            var small = ByMapIndex(mapIndex);
            if (small is null || small.Id == nation.Id || small.Id == rival.Id) continue;
            if (small.Population <= 0) continue;
            var exposure = CampaignIntel.EstimatedStrength(campaign, nation.Id, rival) / M.Max(0.01f, CampaignIntel.EstimatedStrength(campaign, nation.Id, small));
            if (exposure < 1.6f || exposure <= mostExposed) continue;
            var bond = CampaignDiplomacy.Peek(campaign, nation.Id, small.Id);
            if (bond is not null && (bond.GuaranteedBy(nation.Id) || bond.Stance == DiplomaticStance.AtWar)) continue;
            mostExposed = exposure;
            shelter = small;
        }
        if (shelter is null) return false;
        if (Strength(campaign, nation) < CampaignIntel.EstimatedStrength(campaign, nation.Id, rival) * 0.85f) return false;

        new GuaranteeCommand { IssuerId = nation.Id, TargetId = shelter.Id, Guarantee = true }.Apply(campaign);
        Count("guarantee_issued");
        relation.LastActionDay = campaign.ElapsedDays;
        return true;
    }
}
