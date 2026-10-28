using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>A formation's weekly desertion at a pressure of one (WarRhythm.Desertion), and the most it can be.</summary>
    public float DesertionBaseWeekly { get; set; } = 0.002f;
    public float DesertionCapWeekly { get; set; } = 0.03f;
    /// <summary>
    /// The share of deserters the enemy takes into its own ranks: in a civil war, from a people
    /// the enemy counts as its own, and otherwise. The rest go home, and a share of those
    /// (DesertionHomeReturn) is back in the national reserve, counted again.
    /// </summary>
    public float DesertionToEnemyCivil { get; set; } = 0.35f;
    public float DesertionToEnemyKin { get; set; } = 0.22f;
    public float DesertionToEnemy { get; set; } = 0.05f;
    public float DesertionHomeReturn { get; set; } = 0.3f;
    /// <summary>The share of a country's soldiers deserting in a week that is news, and how long before it is news again.</summary>
    public float DesertionNewsShare { get; set; } = 0.012f;
    public int DesertionNewsDays { get; set; } = 180;
}

/// <summary>
/// Soldiers who leave, and the enemy who takes them in.
///
/// Whole formations changing flags (Defections) is the dramatic end of it. Most of it is
/// quieter: men walk away from a unit that has stopped feeding them, from a war nobody at
/// home believes in, from a line that broke last week, from pay that stopped coming. Every
/// war of the last century bled this way - the Russian army of 1917, the Afghan army before
/// 2021, both armies in Ukraine - and every side that could took some of the other's into
/// its own ranks: prisoners, defectors, the kin of its own people on the other side.
///
/// Weekly, for every formation of a country at war: a share of its soldiers deserts
/// (WarRhythm.Desertion, F#: nothing in a well-fed army fighting a popular war, up to three
/// in a hundred a week in a starving, unpaid, beaten one). The enemy takes some into its
/// reserve - many in a civil war, more where it counts the deserters' people as its own -
/// and some of the rest are back in their own country's reserve, counted again. The war's
/// books show them as lost; a country losing more than about one soldier in a hundred a
/// week to it makes the news.
/// </summary>
public static class Desertion
{
    private static readonly Dictionary<int, float> LostThisWeek = new();
    private static readonly Dictionary<int, float> Strength = new();

    public static void Tick(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays <= 0 || campaign.ElapsedDays % 7 != 3) return;
        var defines = campaign.Defines;
        LostThisWeek.Clear();
        Strength.Clear();
        foreach (var army in campaign.Armies)
        {
            if (army.Personnel <= 0 || army.EmbarkedFleetId >= 0) continue;
            Strength[army.NationId] = Strength.GetValueOrDefault(army.NationId) + army.Personnel;
            var war = WarLosses.WarOf(campaign, army);
            if (war is null || war.Ended) continue;
            var nation = campaign.NationById(army.NationId);
            if (nation is null) continue;
            var unpaid = nation.DebtCondition is DebtCondition.Default or DebtCondition.Insolvent || nation.Treasury < 0f ? 1f : 0f;
            var rate = WarRhythm.Desertion(nation.WarSupport, army.Cohesion, army.SupplyLevel, army.Organisation, unpaid,
                GuerrillaWar.Spirit(campaign, war, nation), defines.DesertionBaseWeekly, defines.DesertionCapWeekly);
            var men = (int)MathF.Floor(army.Personnel * rate);
            if (men <= 0) continue;
            var complement = army.Divisions * MilitaryBootstrap.DivisionSize;
            army.Personnel -= men;
            if (complement > 1f) army.Strength = M.Clamp01(army.Strength - men / complement);
            LostThisWeek[nation.Id] = LostThisWeek.GetValueOrDefault(nation.Id) + men;

            var enemy = campaign.NationById(war.EnemyLeaderOf(nation.Id));
            var toEnemy = enemy is null ? 0f : ShareToEnemy(campaign, war, nation, enemy) * men;
            if (enemy is not null) enemy.ManpowerReserve += toEnemy;
            nation.ManpowerReserve += (men - toEnemy) * defines.DesertionHomeReturn;
            WarLosses.Record(war, nation.Id, 0f, 0f, men);
        }
        foreach (var (nationId, lost) in LostThisWeek)
        {
            var nation = campaign.NationById(nationId);
            var total = Strength.GetValueOrDefault(nationId) + lost;
            if (nation is null || total <= 0f || lost / total < defines.DesertionNewsShare) continue;
            const string key = "desertion_news";
            if (nation.DecisionAvailableDay.GetValueOrDefault(key) > campaign.ElapsedDays) continue;
            nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(7, defines.DesertionNewsDays);
            campaign.Chronicle.Record(campaign, "mass_desertion", nationId, subject: $"{lost:N0}");
        }
    }

    /// <summary>The share of a formation's deserters its enemy takes into its own ranks.</summary>
    public static float ShareToEnemy(Campaign campaign, StrategicWar war, Nation deserting, Nation enemy)
    {
        var defines = campaign.Defines;
        if (war.CivilConflict) return defines.DesertionToEnemyCivil;
        var ours = DemographyCatalog.PrimarySet(deserting.Tag);
        var theirs = DemographyCatalog.PrimarySet(enemy.Tag);
        if (ours is not null && theirs is not null)
            foreach (var culture in ours)
                if (theirs.Contains(culture)) return defines.DesertionToEnemyKin;
        return defines.DesertionToEnemy;
    }
}
