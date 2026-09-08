using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>
    /// The army this country is built to keep - the number of formations it began with.
    ///
    /// Set the first time anybody asks and never again, so it survives a save and does not
    /// have to be threaded through the bootstrap. It is what a country rebuilds towards
    /// after a war has cost it formations, which is the other half of letting formations be
    /// lost at all: destruction without replacement is not attrition, it is a death spiral.
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
        if (campaign is null) return;
        StarveThePockets(campaign);
        BuryTheBroken(campaign);
        RebuildTheLost(campaign);
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
    /// </summary>
    private static void StarveThePockets(Campaign campaign)
    {
        var defines = campaign.Defines;
        foreach (var army in campaign.Armies)
        {
            if (!army.CutOff || army.Divisions <= 0f) continue;
            var pressed = army.InBattle ? defines.EncircledUnderAttack : 1f;
            army.Strength = M.Clamp01(army.Strength - defines.EncircledStrengthLoss * pressed);
            army.Organisation = M.Clamp01(army.Organisation - defines.EncircledOrganisationLoss * pressed);
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
            campaign.Armies.Remove(army);
            var owner = campaign.NationById(army.NationId);
            if (owner is not null) owner.FormationsLost++;

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

            // The same tally the rest of warfare reports through, so the bench and the
            // in-game console say this out loud rather than leaving a formation to quietly
            // vanish from a list.
            Warfare.OutcomeTally["destroyed: broken in the field"] =
                Warfare.OutcomeTally.GetValueOrDefault("destroyed: broken in the field") + 1;
        }
        campaign.MarkStrategicChange();
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

            // The establishment is whatever the country started with, learned on first sight.
            if (nation.EstablishedDivisions <= 0f)
            {
                nation.EstablishedDivisions = strength;
                continue;
            }

            if ((campaign.ElapsedDays + nation.Id) % defines.ArmyRebuildIntervalDays != 0) continue;
            if (strength >= nation.EstablishedDivisions * defines.ArmyRebuildCeiling) continue;
            if (nation.Treasury < defines.ArmyRebuildCost) continue;
            if (nation.ManpowerReserve < MilitaryBootstrap.DivisionSize) continue;

            var home = HomeState(campaign, nation);
            if (home < 0) continue;
            new RaiseArmyCommand
            {
                IssuerId = nation.Id,
                StateMapIndex = home,
                Divisions = 1f
            }.Apply(campaign);
            Standing[nation.Id] = strength + 1f;
        }
    }

    /// <summary>Where a country raises troops: the most populous region it still holds.</summary>
    private static int HomeState(Campaign campaign, Nation nation)
    {
        var best = -1;
        var people = 0L;
        foreach (var state in campaign.States)
        {
            if (state.NationMapIndex != nation.MapIndex) continue;
            if (state.Population <= people) continue;
            people = state.Population;
            best = state.MapIndex;
        }
        return best;
    }
}
