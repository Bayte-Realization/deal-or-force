using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// What a front asks of the army at war, in divisions a post of its own: a country at war
    /// raises past its peacetime establishment until it can hold its line this deep.
    /// </summary>
    public float ArmyWarDivisionsPerPost { get; set; } = 1.5f;

    /// <summary>How far past its peacetime establishment a country at war will go - and one being overrun.</summary>
    public float ArmyWarCeiling { get; set; } = 2.5f;
    public float ArmyWarCeilingOverrun { get; set; } = 3.5f;

    /// <summary>The most formations a country being overrun raises at once.</summary>
    public int ArmyEmergencyRaises { get; set; } = 3;

    /// <summary>
    /// The most formations a country short of its front raises in one look. Routine rebuilding
    /// stays one at a time - an army recovers over months, which is what losing one should
    /// cost - but a front that exists today cannot be held by a queue that delivers a single
    /// division a month: the shortage itself is the reason the queue runs several wide.
    /// </summary>
    public int ArmyWartimeRaiseBatch { get; set; } = 3;

    /// <summary>
    /// How far over its establishment a country at peace lets its army sit before one formation
    /// goes home at each look, and only when the treasury cannot pay that surplus. A country
    /// still fighting is not disarmed by a peace it has not made.
    /// </summary>
    public float ArmyPeaceDisbandMargin { get; set; } = 1.05f;
}

public sealed partial class Nation
{
    /// <summary>
    /// The army this country began with, learned the first time anybody asks and never
    /// changed after. The baseline everything below is a multiple of.
    /// </summary>
    public float FoundingDivisions;

    /// <summary>
    /// The army this country is trying to keep NOW.
    ///
    /// It used to be the founding figure and nothing else, with a ceiling of exactly one -
    /// so a country could replace what it lost, up to the army it had on the first morning,
    /// and not one division further. Nobody in the world could arm. A country that was
    /// threatened, voted itself a wartime budget and raised its taxes kept precisely the
    /// army it started with, which is the one thing no state under threat has ever done.
    ///
    /// Now it moves, slowly, towards what the country's own military budget implies - and
    /// that budget already answers to war, to a rival next door, to a neighbour's war and
    /// to what the treasury can bear. Counting the situation once, there, rather than twice.
    /// </summary>
    public float EstablishedDivisions;

    /// <summary>Formations this country has had destroyed, for the record and the ledger.</summary>
    public int FormationsLost;
}

/// <summary>
/// What a war actually costs an army, and how an army comes back.
///
/// Before this, a formation could not be destroyed. Losses came off its strength, and a
/// formation reduced to nothing simply stopped being useful - it stayed in the order of
/// battle, walked out of the line, and refilled itself from the national manpower pool as
/// though the week had not happened. Two years of the Russo-Ukrainian front cost three and a
/// half million casualties and not one formation on either side.
///
/// That is why battles here felt like weather rather than like fighting. Nothing could be
/// won: grinding an enemy division to zero achieved nothing, because it would be back at
/// full strength a fortnight later, and the front only ever moved when somebody walked into
/// a province nobody was standing in. Seven hundred and eleven of the world's captures were
/// walking into a gap; a hundred and eighty-five were winning a battle.
///
/// So a broken formation is destroyed. And because an army that can only shrink is an army
/// that loses every long war by arithmetic, a country below its establishment rebuilds -
/// through the same command a player uses, paying the same manpower and the same money. The
/// two together are what makes a battle worth fighting: breaking a division is permanent,
/// and replacing one is expensive.
/// </summary>
public static class Attrition
{
    private static readonly List<Army> Broken = new();
    private static readonly Dictionary<int, float> Standing = new();

    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(Attrition))) return;
        if (campaign is null) return;
        StarveThePockets(campaign);
        BuryTheBroken(campaign);
        // Starving and burying are rates and take their share of the step. Rebuilding is a
        // decision a country makes on a day - it divides the day number by its own interval -
        // so at an hourly step it must happen once on that day rather than every hour of it.
        // Sending home the prisoners of a war that has ended is the same kind of thing.
        if (campaign.StepOpensNewDay)
        {
            RebuildTheLost(campaign);
            WarLosses.Repatriate(campaign);
        }
    }

    /// <summary>
    /// A pocket that cannot be relieved does not hold out for six years.
    ///
    /// Being cut off used to cost a formation forty-five thousandths of a per cent of its
    /// strength a day. At that rate an encircled division breaks in a little under six years,
    /// which is to say encirclement cost the encircled nothing at all - and so the one move
    /// that ought to decide a front was not worth making. Fronts moved when somebody walked
    /// into an empty province instead.
    ///
    /// A pocket now collapses in about six weeks, sooner if it is being attacked while it
    /// starves. That is long enough to be relieved by a counter-attack, which is the whole
    /// point: it makes cutting an enemy off a real threat and breaking through to them a real
    /// answer, and it gives both sides something to be doing besides pushing.
    ///
    /// Two things it did not do. The loss is a rate per day and this runs every step, and it
    /// took a whole day's loss each time: at the hourly step a pocket was gone in two days,
    /// which is no time to relieve it and no time for the player to notice it had formed.
    /// And the men it cost were never counted - they came off the formation and out of the
    /// world, so the war that closed the pocket showed none of it. Now they are killed or
    /// taken, on the books of the war that is starving them (see WarLosses).
    /// </summary>
    private static void StarveThePockets(Campaign campaign)
    {
        var defines = campaign.Defines;
        var scale = campaign.TickScale;
        var captured = M.Clamp01(defines.EncircledLossCapturedShare);
        foreach (var army in campaign.Armies)
        {
            if (!army.CutOff || army.Divisions <= 0f)
            {
                if (!army.CutOff) army.DaysCutOff = 0;
                continue;
            }
            if (MilitaryAccess.MustReturn(campaign, army)) { MilitaryAccess.Provision(army); army.DaysCutOff = 0; continue; }
            // The day count is advanced once, by the supply pass, earlier on this step.
            // Counting it again made every retraced day two days older than the calendar.

            var dayFactor = army.DaysCutOff <= 3 ? 1.0f :
                            army.DaysCutOff <= 9 ? 1.0f + (army.DaysCutOff - 3) * 0.15f :
                            1.6f + MathF.Min(1.0f, (army.DaysCutOff - 9) * 0.2f);
            var pressed = (army.InBattle ? defines.EncircledUnderAttack : 1f) * scale * dayFactor;
            var before = army.Strength;
            army.Strength = M.Clamp01(army.Strength - defines.EncircledStrengthLoss * pressed);
            army.Organisation = M.Clamp01(army.Organisation - defines.EncircledOrganisationLoss * pressed);

            var men = (before - army.Strength) * army.Divisions * MilitaryBootstrap.DivisionSize;
            if (men <= 0f) continue;
            army.Personnel = Math.Max(0, army.Personnel - (int)MathF.Round(men));
            WarLosses.Record(WarLosses.WarOf(campaign, army), army.NationId,
                men * (1f - captured), 0f, men * captured);
        }
    }

    /// <summary>
    /// Formations that no longer exist in any meaningful sense are removed from the world.
    ///
    /// A division at five per cent strength is not a division that is losing; it is one that
    /// has lost. Keeping it in the order of battle is what made every engagement inconclusive
    /// and every front a treadmill.
    /// </summary>
    private static void BuryTheBroken(Campaign campaign)
    {
        var floor = campaign.Defines.DivisionBrokenStrength;
        Broken.Clear();
        foreach (var army in campaign.Armies)
            if (army.Divisions < 0.05f || army.Strength <= floor)
                Broken.Add(army);
        if (Broken.Count == 0) return;

        foreach (var army in Broken)
        {
            // A pocket that has starved down to nothing did not dissolve: what was left of it
            // surrendered, and those men are the other side's prisoners now.
            if (army.CutOff) WarLosses.Surrender(campaign, army);
            Destroy(campaign, army, "destroyed: broken in the field");
        }
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// Take one formation out of the world, wherever the reason came from.
    ///
    /// Shared with the retreat, which needs exactly this and used to have nothing: a
    /// formation beaten out of a province with no friendly ground next to it was left
    /// standing in the province it had just lost, and the code said so in a diagnostic
    /// marked `known_defect=true`. What has actually happened to such a formation is that
    /// it surrendered, so it is removed and counted.
    /// </summary>
    public static void Destroy(Campaign campaign, Army army, string reason, bool lost = true)
    {
        if (campaign is null || army is null) return;
        campaign.Armies.Remove(army);
        var owner = campaign.NationById(army.NationId);
        // A formation stood down is not a formation lost. The ledger, the attrition report
        // and the war tally are all about what a war cost, and a peacetime demobilisation
        // would read as casualties in every one of them.
        if (owner is not null && lost) owner.FormationsLost++;

        // The battle it was standing in, and the line it was posted to, both hold its id
        // rather than the formation itself. A dangling id is harmless where it is read -
        // the lookup simply misses - but leaving it would put it in a save, so it goes.
        foreach (var battle in campaign.Battles)
        {
            if (battle.Ended) continue;
            battle.Attackers.Remove(army.Id);
            battle.Defenders.Remove(army.Id);
        }
        foreach (var line in campaign.FrontLines) line.Posts.Remove(army.Id);

        // The same tally the rest of warfare reports through, so the bench and the in-game
        // console say this out loud rather than leaving a formation to quietly vanish from
        // a list.
        if (lost) Warfare.OutcomeTally[reason] = Warfare.OutcomeTally.GetValueOrDefault(reason) + 1;
    }

    /// <summary>
    /// The army this country is trying to keep, and what decides it.
    ///
    /// One term, and it is deliberate: the military budget. A country at war votes sixteen
    /// per cent of its spending to the army, a threatened one nine, a quiet one four and a
    /// half - see AiStatecraft.Economy - so the whole of the geopolitics is already in that
    /// number, and reading it here counts the situation once rather than twice.
    ///
    /// Two things temper it. Men, because an establishment is people and a country whose
    /// reserve is empty is not keeping a larger army whatever it votes. And the country's
    /// own long habit: two neighbours with the same budget do not keep the same army, and a
    /// world where they did would have every border in it mirror-imaged.
    /// </summary>
    internal static float Establishment(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;
        if (nation.FoundingDivisions <= 0f) return nation.EstablishedDivisions;

        var voted = nation.MilitarySpending / MathF.Max(0.0001f, defines.BaselineMilitarySpending);
        var demand = MathF.Pow(MathF.Max(0.05f, voted), defines.ArmyDemandElasticity);

        // Men. Measured against the army it began with, so a country that has spent its
        // reserve cannot vote itself divisions it has nobody to put in.
        var reserve = nation.ManpowerReserve /
                      MathF.Max(1f, nation.FoundingDivisions * MilitaryBootstrap.DivisionSize);
        demand *= M.Clamp(defines.ArmyManpowerFloor + reserve * defines.ArmyManpowerWeight,
            defines.ArmyManpowerFloor, 1.15f);

        // And its own habit, which is a hash rather than a roll: this is asked for every
        // country every cycle, and consuming the campaign's generator here would make every
        // other roll in the world land differently because one army moved.
        demand *= 1f + (Habit(campaign.Seed, nation.Id, campaign.ElapsedDays,
            defines.ArmyHabitSeasonDays) - 0.5f) * defines.ArmyHabitSpread;

        return nation.FoundingDivisions *
               M.Clamp(demand, defines.ArmyDemandFloor, defines.ArmyDemandCeiling);
    }

    /// <summary>A country's own slow wobble, nought to one, from the seed and the season.</summary>
    private static float Habit(int seed, int nationId, int day, int seasonDays)
    {
        unchecked
        {
            var hash = (uint)(seed * 73856093) ^ (uint)(nationId * 19349663) ^
                       (uint)(day / Math.Max(1, seasonDays) * 83492791);
            hash ^= hash >> 13;
            hash *= 2654435761u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>
    /// A country below the army it is built to keep raises another formation.
    ///
    /// Through <see cref="RaiseArmyCommand"/>, which is the command a player's own recruiting
    /// button issues: the same money, the same manpower, the same placement rules. An AI that
    /// rebuilt by a private path would sooner or later rebuild by different rules, and the
    /// first thing anybody would notice is that it was cheating.
    ///
    /// One formation at a time, and rarely, so an army recovers over months rather than
    /// between two frames - which is both what it should cost and what makes losing one feel
    /// like something.
    /// </summary>
    private static void RebuildTheLost(Campaign campaign)
    {
        var defines = campaign.Defines;

        // Every country's army, counted in one pass over the divisions rather than one pass
        // per country. The difference is three hundred thousand steps a day against thirteen
        // hundred, for the same answer.
        Standing.Clear();
        foreach (var army in campaign.Armies)
            Standing[army.NationId] = Standing.GetValueOrDefault(army.NationId) + army.Divisions;

        foreach (var nation in campaign.Nations)
        {
            if (nation.Population <= 0) continue;
            var strength = Standing.GetValueOrDefault(nation.Id);

            // What it began with, learned on first sight and never changed after.
            if (nation.FoundingDivisions <= 0f)
            {
                nation.FoundingDivisions = strength;
                nation.EstablishedDivisions = strength;
                continue;
            }

            // A country being overrun rebuilds three times as often, and on credit (WarWeariness):
            // the money a country is short of is not a reason to stop defending it.
            var overrun = WarWeariness.Overrun(campaign, nation);
            // A country at war short of the army its front needs raises twice as often; one
            // being overrun three times as often, and several formations at once.
            var warNeed = WarNeed(campaign, nation);
            var shortOfFront = warNeed > strength;
            var interval = overrun ? Math.Max(1, defines.ArmyRebuildIntervalDays / 3)
                : shortOfFront ? Math.Max(1, defines.ArmyRebuildIntervalDays / 2)
                : defines.ArmyRebuildIntervalDays;
            if ((campaign.ElapsedDays + nation.Id) % interval != 0) continue;

            // A player who has taken the army's size into their own hands is left to it: no
            // formation raised on their treasury, none stood down from under them.
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Army)) continue;

            // The establishment follows the country's own budget, and it follows it slowly:
            // an army is not raised or sent home in a fortnight, and a target that jumped
            // would have every country in the world lurching between two sizes.
            nation.EstablishedDivisions = M.Lerp(nation.EstablishedDivisions,
                Establishment(campaign, nation), defines.ArmyEstablishmentDrift);

            // Over establishment, and only at peace. A war the country is still fighting is
            // not a reason to send the army home. At peace the surplus goes only when the
            // treasury cannot pay for it: a country that can pay keeps the army it raised.
            // The same rule is the player's staff and every other government.
            var fighting = false;
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(nation.Id)) { fighting = true; break; }
            if (!nation.IsHuman && !fighting && strength > nation.EstablishedDivisions * defines.ArmyPeaceDisbandMargin)
            {
                var surplus = MathF.Max(1f, strength - nation.EstablishedDivisions);
                var threatened = nation.WarPlanTargetId >= 0 || AiStatecraft.Threatened(campaign, nation);
                if (!threatened && (nation.Treasury < 0f || (nation.Treasury < defines.ArmyRebuildCost * surplus && nation.Gdp > 0 && nation.Treasury / nation.Gdp < 0.005f)))
                {
                    StandDown(campaign, nation);
                    continue;
                }
            }
            // Over the wartime margin and not short of the front: do not raise further.
            if (fighting && strength > nation.EstablishedDivisions * defines.ArmyDisbandMargin &&
                strength >= warNeed) continue;

            // In peace the budget's army; at war the army the front needs, up to a ceiling of the
            // budget's - higher for a country whose ground is being taken. Sudan held ten posts
            // of its civil war with seven divisions and raised nothing, because seven was its
            // peacetime establishment.
            var target = nation.EstablishedDivisions * defines.ArmyRebuildCeiling;
            if (warNeed > target)
            {
                var budgetCeiling = nation.EstablishedDivisions *
                    (overrun ? defines.ArmyWarCeilingOverrun : defines.ArmyWarCeiling);
                target = MathF.Max(budgetCeiling, MathF.Min(warNeed, overrun ? warNeed : MathF.Max(budgetCeiling, warNeed * 0.75f)));
            }
            if (strength >= target) continue;
            // At war it borrows for the army it needs, as far as a country at war may.
            var mayBorrow = (overrun || shortOfFront) && nation.Treasury > -nation.Gdp * defines.WarDebtLimitShare;
            if (nation.Treasury < defines.ArmyRebuildCost && !mayBorrow) continue;

            var home = HomeState(campaign, nation);
            if (home < 0) continue;
            var raises = overrun ? Math.Max(1, Math.Min(defines.ArmyEmergencyRaises, (int)MathF.Ceiling((target - strength) / 3f)))
                : shortOfFront ? Math.Max(1, Math.Min(defines.ArmyWartimeRaiseBatch, (int)MathF.Ceiling(target - strength)))
                : 1;
            for (var raise = 0; raise < raises; raise++)
            {
                if (nation.ManpowerReserve < MilitaryBootstrap.DivisionSize) break;
                // The reserve that refills the formations already in the field is not spent on new ones.
                if (!WarWeariness.MayRaise(campaign, nation)) break;
                if (nation.Treasury < defines.ArmyRebuildCost && !mayBorrow) break;
                new RaiseArmyCommand
                {
                    IssuerId = nation.Id,
                    StateMapIndex = home,
                    Divisions = 1f,
                    AllowDeficit = mayBorrow
                }.Apply(campaign);
                strength += 1f;
            }
            if (mayBorrow) SovereignFinance.NormalizeAccounts(campaign, nation);
            Standing[nation.Id] = strength;
        }
    }

    /// <summary>
    /// The army a country's fronts need: its own posts on every war it is fighting, so deep.
    /// Nothing at peace. A short border war is fought with the army there is.
    /// </summary>
    private static float WarNeed(Campaign campaign, Nation nation)
    {
        var controllers = campaign.ProvinceController;
        if (controllers is null) return 0f;
        var posts = 0;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || war.Limited || !war.Involves(nation.Id)) continue;
            foreach (var province in war.FriendlyFront(nation.Id))
                if (province < controllers.Length && controllers[province] == (ushort)nation.MapIndex) posts++;
        }
        return posts * campaign.Defines.ArmyWarDivisionsPerPost;
    }

    /// <summary>
    /// Send one formation home.
    ///
    /// The weakest one that is not holding a war: not a battle, not a front, not ground
    /// taken from the enemy while the war is still on, and not a plan that is actually
    /// marching. A default plan flag on a garrison is not that. Most of its people go
    /// back into the reserve. The country is told why.
    /// </summary>
    private static void StandDown(Campaign campaign, Nation nation)
    {
        Army weakest = null;
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || HoldingTheWar(campaign, nation, army)) continue;
            if (weakest is null || army.Power < weakest.Power) weakest = army;
        }
        if (weakest is null) return;

        nation.ManpowerReserve += weakest.Personnel * campaign.Defines.ArmyStandDownReturn;
        var name = weakest.Name;
        Destroy(campaign, weakest, "stood down", lost: false);
        campaign.Chronicle.Record(campaign, "army_stood_down", nation.Id,
            subject: name, place: "unpaid_surplus", isPublic: false);
        DiagnosticLogs.Write("simulation",
            $"STAND_DOWN nation={nation.Tag} army={weakest.Id} day={campaign.ElapsedDays} reason=unpaid_surplus");
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// A formation the country still needs for a war it is fighting: on that war's line,
    /// standing on ground it has taken, or carrying out an order. A plan flag with nowhere
    /// to go is a garrison and may be the surplus.
    /// </summary>
    private static bool HoldingTheWar(Campaign campaign, Nation nation, Army army)
    {
        if (army.InBattle || army.AssignedWarId >= 0 || army.FrontLineId >= 0) return true;
        if (army.PlanActive && (army.OrderProvinceId >= 0 || army.Route.Count > 0 || army.PlannedRoute.Count > 0))
            return true;
        if (army.ProvinceId <= 0) return false;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            if (war.FriendlyFront(nation.Id).Contains(army.ProvinceId)) return true;
            if (map is null || controllers is null || army.ProvinceId >= map.ProvinceToNation.Length ||
                army.ProvinceId >= controllers.Length) continue;
            var owner = map.ProvinceToNation[army.ProvinceId];
            if (owner != 0 && owner != (ushort)nation.MapIndex &&
                controllers[army.ProvinceId] == (ushort)nation.MapIndex)
                return true;
        }
        return false;
    }

    /// <summary>Where a country raises troops: the most populous region it still holds.</summary>
    private static int HomeState(Campaign campaign, Nation nation)
    {
        var best = -1;
        var bestControlled = -1;
        var people = 0L;
        var controlledPeople = 0L;
        foreach (var state in campaign.States)
        {
            if (state.NationMapIndex != nation.MapIndex) continue;
            if (state.Population > people)
            {
                people = state.Population;
                best = state.MapIndex;
            }
            if (state.Population > controlledPeople &&
                RecruitmentSystem.DeploymentProvince(campaign, nation.Id, state.MapIndex) > 0)
            {
                controlledPeople = state.Population;
                bestControlled = state.MapIndex;
            }
        }
        // A capital or largest region under occupation is not a place a division can be
        // trained. The emergency path uses the largest region the country still controls;
        // the ordinary path keeps its old answer when every region is occupied.
        return bestControlled >= 0 ? bestControlled : best;
    }
}
