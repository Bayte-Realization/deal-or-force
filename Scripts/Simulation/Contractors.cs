using System.Collections.Generic;
using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Army
{
    /// <summary>The day a contractor formation's contract runs out, or -1 for the country's own troops (Contractors).</summary>
    public int ContractUntilDay = -1;
}

public sealed partial class Defines
{
    /// <summary>What a contractor formation costs on signing, against training a division of line infantry.</summary>
    public float ContractorSigningShare { get; set; } = 1.4f;
    /// <summary>What it costs a week while it serves, as a share of the signing price.</summary>
    public float ContractorWeeklyShare { get; set; } = 0.02f;
    /// <summary>How many men a contractor formation brings: fewer than a division, all of them veterans.</summary>
    public int ContractorPersonnel { get; set; } = 6000;
    public float ContractorExperience { get; set; } = 0.4f;
    /// <summary>How long a contract runs, in days.</summary>
    public int ContractorDays { get; set; } = 182;
    /// <summary>The most contractor formations one country has in its service at once.</summary>
    public int ContractorLimit { get; set; } = 3;
    /// <summary>The share of its treasury a government at war will spend on signing contractors.</summary>
    public float AiContractorTreasuryShare { get; set; } = 0.2f;
}

/// <summary>
/// Private military contractors: men a government at war can buy instead of raise.
///
/// A division of its own takes months to train and takes men out of the country's reserve.
/// Contractors arrive within days, veterans, without a single conscript - for a price on signing
/// and a fee every week, for half a year, and only while there is a war to fight. When the
/// contract runs out, the money does, or the war ends, they go home; they do not die for the
/// flag, and the country's dead are its own. The ones at the front are formations like any other:
/// the general staff posts them where it needs them.
///
/// Governments hire them when the war is going badly - the enemy stronger, or the reserve of men
/// running dry - and when they can pay. A player hires them from the recruitment screen.
/// A company of the for_hire kind in common/movements takes contracts of its own as well.
/// </summary>
public static class Contractors
{
    public const string TemplateId = "line_infantry";

    public static float SigningCost(Campaign c, Nation nation)
    {
        var template = UnitTypeCatalog.Get(TemplateId);
        return template is null ? 0.5f : RecruitmentSystem.CostOf(nation, template) * MathF.Max(0.1f, c.Defines.ContractorSigningShare);
    }

    public static int InService(Campaign c, int nationId) => c.Armies.Count(a => a.NationId == nationId && a.ContractUntilDay >= 0);

    public static bool AtWar(Campaign c, int nationId) => c.Wars.Any(w => !w.Ended && w.Involves(nationId));

    /// <summary>Why this country cannot sign so many contractor formations, or empty when it can.</summary>
    public static string Refusal(Campaign c, Nation nation, int formations)
    {
        if (nation is null || nation.Movement is not null) return Loc.Get("Only a state signs contractors.");
        if (!AtWar(c, nation.Id)) return Loc.Get("Contractors are hired for a war, and there is none.");
        if (formations < 1) return Loc.Get("Choose how many formations.");
        var limit = Math.Max(1, c.Defines.ContractorLimit);
        if (InService(c, nation.Id) + formations > limit)
            return Loc.Format("At most {0} contractor formations serve one country at once.", limit);
        if (Deployment(c, nation) <= 0) return Loc.Get("There is no ground of ours left to land them on.");
        var cost = SigningCost(c, nation) * formations;
        if (nation.Treasury < cost) return Loc.Format("Signing costs {0}.", Fmt.Money(cost));
        return "";
    }

    /// <summary>Where they arrive: the capital's region if the country still holds it, else any ground it holds.</summary>
    private static int Deployment(Campaign c, Nation nation)
    {
        var province = RecruitmentSystem.DeploymentProvince(c, nation.Id, nation.CapitalStateMapIndex);
        if (province > 0) return province;
        var map = c.ProvinceMap;
        if (map is null) return -1;
        var controllers = c.ProvinceController ?? map.ProvinceToNation;
        for (var p = 1; p <= map.ProvinceCount && p < controllers.Length; p++)
            if (controllers[p] == nation.MapIndex && !map.WaterByProvince[p]) return p;
        return -1;
    }

    internal static void Sign(Campaign c, Nation nation, int formations)
    {
        var province = Deployment(c, nation);
        var map = c.ProvinceMap;
        var position = map.CenterOf(province);
        var cost = SigningCost(c, nation) * formations;
        nation.Treasury -= cost;
        var id = c.Armies.Count == 0 ? 1 : c.Armies.Max(a => a.Id) + 1;
        var serial = c.Armies.Count(a => a.NationId == nation.Id && a.ContractUntilDay >= 0) + 1;
        var until = c.ElapsedDays + Math.Max(30, c.Defines.ContractorDays);
        var personnel = Math.Max(500, c.Defines.ContractorPersonnel);
        for (var i = 0; i < formations; i++)
            c.Armies.Add(new Army
            {
                Id = id + i, NationId = nation.Id, StateMapIndex = map.ProvinceToState[province],
                ProvinceId = province, Name = Loc.Format("{0}. {1} Contractors", serial + i, nation.Tag), TemplateId = TemplateId,
                Divisions = 1, Personnel = personnel, MaxPersonnel = personnel,
                Organisation = 0.8f, Strength = 1, Equipment = 0.9f, Experience = M.Clamp01(c.Defines.ContractorExperience),
                Cohesion = 0.8f, SupplyLevel = 0.85f, OrderKind = ArmyOrder.Manual, PlanActive = false,
                ReinforcementPriority = ReinforcementPriority.Low,
                Latitude = position.Latitude, Longitude = position.Longitude, ContractUntilDay = until
            });
        c.Chronicle.Record(c, "contractors_hired", nation.Id, -1, formations.ToString());
        DiagnosticLogs.Write("simulation", $"CONTRACTORS_SIGNED nation={nation.Tag} formations={formations} cost={cost:0.000} until={until}");
        c.MarkStrategicChange();
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same contractor pass, yielded between batches of armies.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(Contractors))) yield break;
        // The weekly fee, and the end of service: contract over, no war left, or no money left.
        if (c.ElapsedDays % 7 == 0)
        {
            var going = new System.Collections.Generic.List<Army>();
            var scanned = 0;
            foreach (var army in c.Armies)
            {
                if (++scanned % 256 == 0) yield return 0;
                if (army.ContractUntilDay < 0) continue;
                var nation = c.NationById(army.NationId);
                if (nation is null || c.ElapsedDays >= army.ContractUntilDay || !AtWar(c, nation.Id)) { going.Add(army); continue; }
                var fee = SigningCost(c, nation) * MathF.Max(0f, c.Defines.ContractorWeeklyShare) * army.Personnel / (float)Math.Max(1, army.MaxPersonnel);
                if (nation.Treasury < fee) { going.Add(army); continue; }
                nation.Treasury -= fee;
            }
            foreach (var army in going)
                Attrition.Destroy(c, army, "stood down: contract over", lost: false);
            if (going.Count > 0) c.MarkStrategicChange();
        }
        yield return 0;
        if (c.ElapsedDays % 30 == 13) Ai(c);
    }

    /// <summary>
    /// A government at war hires when it is losing the count - the enemy stronger than it, or its
    /// reserve of men nearly gone - and when signing would not empty the treasury.
    /// </summary>
    private static void Ai(Campaign c)
    {
        var limit = Math.Max(1, c.Defines.ContractorLimit);
        foreach (var nation in c.Nations)
        {
            if (nation.IsHuman || nation.Movement is not null || nation.Population <= 0) continue;
            var war = c.Wars.FirstOrDefault(w => !w.Ended && w.Involves(nation.Id));
            if (war is null || InService(c, nation.Id) >= limit) continue;
            var ours = AiStatecraft.CachedStrength(c, nation);
            var theirs = war.EnemiesOf(nation.Id).Select(c.NationById).Where(n => n is not null)
                .Sum(enemy => CampaignIntel.EstimatedStrength(c, nation.Id, enemy));
            var template = UnitTypeCatalog.Get(TemplateId);
            var drained = template is not null && nation.ManpowerReserve < template.RecruitmentPersonnel * 2f;
            if (theirs <= ours * 1.1f && !drained) continue;
            var cost = SigningCost(c, nation);
            var affordable = (int)MathF.Floor(nation.Treasury * M.Clamp01(c.Defines.AiContractorTreasuryShare) / MathF.Max(0.001f, cost));
            var formations = Math.Min(Math.Min(affordable, limit - InService(c, nation.Id)), 2);
            if (formations < 1) continue;
            new HireContractorsCommand { IssuerId = nation.Id, Formations = formations }.Apply(c);
        }
    }
}

/// <summary>Sign contractor formations for the war the country is fighting.</summary>
public sealed class HireContractorsCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int Formations { get; init; } = 1;
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        Refusal = Contractors.Refusal(campaign, nation, Formations);
        if (Refusal.Length > 0) return;
        Contractors.Sign(campaign, nation, Formations);
    }
}
