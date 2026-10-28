using System;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How far a side's fighting spirit runs from war to war, year to year: 1 ± this,
    /// triangular. The same army fights a war it believes in and a war it does not.
    /// </summary>
    public float WarSpiritSpread { get; set; } = 0.12f;
    /// <summary>The share of side-years that surprise everybody, and how far: a defender fighting for its own ground adds WarSpiritDefenderSurprise.</summary>
    public float WarSpiritSurprise { get; set; } = 0.06f;
    public float WarSpiritDefenderSurprise { get; set; } = 0.06f;
    public float WarSpiritSurpriseSwing { get; set; } = 0.35f;
    /// <summary>The share of side-years in which an army breaks, raised by an unstable state: WarSpiritCollapseInstability per unit of instability.</summary>
    public float WarSpiritCollapse { get; set; } = 0.03f;
    public float WarSpiritCollapseInstability { get; set; } = 0.12f;
    public float WarSpiritCollapseSwing { get; set; } = 0.40f;
    /// <summary>
    /// What guerrilla cover (0 to 1) gives the weak side when it defends: this much more staying
    /// power at full cover, and this share of its dead it does not lose, because it does not
    /// stand and die - it melts into the hills and the villages and fights again.
    /// </summary>
    public float GuerrillaDefence { get; set; } = 0.7f;
    public float GuerrillaEvasion { get; set; } = 0.5f;
    /// <summary>How much faster an attack into full cover spends its organisation: the ambushes on the way in.</summary>
    public float GuerrillaAmbush { get; set; } = 0.6f;
    /// <summary>The economy ratio from which a war is lopsided, and the one at which it is wholly so.</summary>
    public float GuerrillaAsymmetryFrom { get; set; } = 2f;
    public float GuerrillaAsymmetryFull { get; set; } = 6f;
}

/// <summary>
/// The war of the weak, and the spirit of armies.
///
/// Battles were sums. The stronger economy raised the stronger army, the stronger army won
/// the battles, and a rising that met a state's army in the field was gone by the autumn -
/// every civil war of a ten-year bench ended in the rebels' capitulation inside a year, and
/// no small country ever surprised anybody. History is the opposite record: the Hussites
/// beat five crusades from behind their wagons, the Taliban outlasted two superpowers,
/// Finland held the Red Army, and the Afghan army of 2021 dissolved in a fortnight.
///
/// Two things carry that here. A side's fighting spirit in a war - steady within a band,
/// and in a few years out of many far above or below it; a defender on its own ground is
/// likelier to surprise, an unstable state likelier to break - redrawn each year of the war,
/// from the seed, so one campaign's Ukraine holds and another's does not. And guerrilla
/// cover: a side fighting a much stronger one, on rough ground among its own people, stays
/// in a battle longer and loses fewer men when it gives ground, because it does not stand
/// and die. The formulas are WarRhythm's (F#); this reads the campaign for them.
/// </summary>
public static class GuerrillaWar
{
    /// <summary>The fighting spirit of a nation's armies in this war today, as a factor on their weight.</summary>
    public static float Spirit(Campaign campaign, StrategicWar war, Nation nation)
    {
        if (campaign is null || war is null || nation is null) return 1f;
        var defines = campaign.Defines;
        var year = Math.Max(0, (campaign.ElapsedDays - war.StartedDay) / 365);
        float Roll(int salt) => Escalation.Roll(campaign.Seed, war.Id, nation.Id, year, salt);
        var surprise = defines.WarSpiritSurprise + (war.IsDefending(nation.Id) ? defines.WarSpiritDefenderSurprise : 0f);
        var collapse = defines.WarSpiritCollapse + defines.WarSpiritCollapseInstability * (1f - M.Clamp01(nation.Stability));
        return WarRhythm.Spirit(defines.WarSpiritSpread, Roll(0x5A1), Roll(0x5A2), Roll(0x5A3),
            surprise, collapse, defines.WarSpiritSurpriseSwing, defines.WarSpiritCollapseSwing);
    }

    /// <summary>
    /// How much cover the defender of this province has against this attacker, 0 to 1: none
    /// for an even war, most for a rising or a small country against a far larger one, on
    /// rough ground that is its own.
    /// </summary>
    public static float Cover(Campaign campaign, Nation defender, Nation attacker, int province, TerrainDefinition terrain)
    {
        if (campaign is null || defender is null || attacker is null) return 0f;
        var defines = campaign.Defines;
        float asymmetry;
        if (WarAims.IsRebel(defender) && !WarAims.IsRebel(attacker)) asymmetry = 1f;
        else
        {
            var ratio = MathF.Max(0f, attacker.Gdp) / MathF.Max(0.01f, defender.Gdp);
            var span = MathF.Max(0.01f, defines.GuerrillaAsymmetryFull - defines.GuerrillaAsymmetryFrom);
            asymmetry = M.Clamp01((ratio - defines.GuerrillaAsymmetryFrom) / span);
        }
        if (asymmetry <= 0f) return 0f;
        // Rough is what the ground's own defence says it is: plains nothing, mountains all of it.
        var roughness = M.Clamp01(((terrain?.Defense ?? 1f) - 1f) / 0.4f);
        var map = campaign.ProvinceMap;
        var home = 0.25f;
        if (map is not null && province > 0 && province < map.ProvinceToNation.Length)
        {
            if (map.ProvinceToNation[province] == defender.MapIndex) home = 1f;
            else if (campaign.Homelands.TryGetValue(defender.Id, out var homeland) &&
                     homeland.Contains(map.ProvinceToState[province])) home = 1f;
        }
        return WarRhythm.Cover(asymmetry, roughness, home);
    }
}
