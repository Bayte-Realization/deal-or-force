using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Parallel administration: how much of a foreign region has to be ours before we can put
    /// our own offices in it, what they cost to open and to run, and what they take.
    /// </summary>
    public float KinAdminMinShare { get; set; } = 0.25f;

    /// <summary>Opening the offices: cash per million of our people, and the political credit of admitting it.</summary>
    public float KinAdminOpenCashPerMillion { get; set; } = 0.006f;
    public float KinAdminPoliticalCost { get; set; } = 5f;

    /// <summary>
    /// What the offices take each week: the region's share of the host's tax income, times our
    /// share of the region, times this. It is not free money - it is tax the host no longer sees.
    /// </summary>
    public float KinAdminLevy { get; set; } = 0.55f;

    /// <summary>What running the offices costs us every week, in billions, per million of our people.</summary>
    public float KinAdminUpkeepPerMillion { get; set; } = 0.0009f;

    /// <summary>
    /// What living under somebody else's paperwork does to the ground: unrest added each week,
    /// while the host still cannot see who is doing it.
    /// </summary>
    public float KinAdminRadicalism { get; set; } = 0.004f;

    /// <summary>
    /// The weekly chance the host notices, and what being noticed costs on the day. This is the
    /// base rate only: <see cref="ParallelAdministration.Watchfulness"/> scales it by how closely
    /// the host can watch and by how well our people cover the offices. At 3,5 % a week an office
    /// lived about eight months, so it never outlived the rising it was meant to survive.
    /// </summary>
    public float KinAdminDiscovery { get; set; } = 0.008f;
    public float KinAdminGrievance { get; set; } = 6f;
    public float KinAdminCaughtGrievance { get; set; } = 18f;

    /// <summary>
    /// After it is public, the chance each week that the host shuts the offices down. A weak
    /// host cannot; a strong one does, and we lose what we put in.
    /// </summary>
    public float KinAdminShutdownChance { get; set; } = 0.18f;

    /// <summary>How willing the computer is to open offices at all, on its monthly look.</summary>
    public float AiKinAdminChance { get; set; } = 0.03f;
}

public sealed partial class ScenarioState
{
    /// <summary>
    /// The country whose own offices stand in this region, or -1. A parallel administration is
    /// not an occupation and not a lease: the host still owns the ground, still polices it, and
    /// still believes it governs - while somebody else's documents are what people actually use.
    /// </summary>
    public int AdministeredById = -1;

    /// <summary>The day the offices opened, or -1.</summary>
    public int AdministeredSinceDay = -1;

    /// <summary>True once the host has noticed and said so in public.</summary>
    public bool AdministrationExposed;

    /// <summary>What the offices have taken out of the host so far, for the panel and the report.</summary>
    public float AdministrationTaken;

    /// <summary>Empty means the whole region (legacy saves). Local office coverage
    /// survives reunion without forcing a second official region to stay on the map.</summary>
    public List<int> AdministrationProvinces = new();
    public float AdministrationPopulationShare = 1f;
}

/// <summary>
/// A country running its own institutions inside somebody else's region: documents, payments,
/// schools, a court people trust more than the host's.
///
/// It is the quietest thing in this kit and the one that takes longest to work: no soldiers, no
/// lease, no referendum - the host's flag still flies and the host's police still walk the
/// streets. What changes is who people go to. The region's unrest rises, the host loses tax it
/// never sees leave, and we gain a foothold that costs money every week and can be shut down the
/// moment somebody proves whose it is.
///
/// Everything here is per region and saved with it: a region can have at most one such
/// administration, which is what makes the state fit on the ground rather than in a list.
/// </summary>
public static class ParallelAdministration
{
    public static float Coverage(ScenarioState region) => region is not null && float.IsFinite(region.AdministrationPopulationShare)
        ? M.Clamp01(region.AdministrationPopulationShare) : 0f;
    /// <summary>Whether these offices are ours.</summary>
    public static bool Ours(Campaign campaign, Nation country, ScenarioState region) =>
        campaign is not null && country is not null && region is not null &&
        region.AdministeredById == country.Id;

    /// <summary>Whether anybody runs them.</summary>
    public static bool Any(ScenarioState region) => region is not null && region.AdministeredById >= 0;

    /// <summary>What opening them costs in cash, before the treasury is asked.</summary>
    public static float OpenCost(Campaign campaign, Nation country, ScenarioState region)
    {
        if (campaign?.Defines is null || country is null || region is null) return 0f;
        var (ours, _, _) = KinStatecraft.Kin(campaign, country, region);
        return MathF.Max(campaign.Defines.KinAgitationCashFloor,
            campaign.Defines.KinAdminOpenCashPerMillion * ours / 1_000_000f);
    }

    /// <summary>What they cost us every week to keep running.</summary>
    public static float WeeklyUpkeep(Campaign campaign, Nation country, ScenarioState region)
    {
        if (campaign?.Defines is null || country is null || region is null) return 0f;
        var (ours, _, _) = KinStatecraft.Kin(campaign, country, region);
        return campaign.Defines.KinAdminUpkeepPerMillion * ours / 1_000_000f * Coverage(region);
    }

    /// <summary>What they take from the host every week, net of what they cost us.</summary>
    public static float WeeklyTake(Campaign campaign, Nation host, ScenarioState region)
    {
        if (campaign?.Defines is null || host is null || region is null || region.Population <= 0) return 0f;
        if (region.AdministeredById < 0) return 0f;
        var admin = campaign.NationById(region.AdministeredById);
        if (admin is null) return 0f;
        var (_, _, share) = KinStatecraft.Kin(campaign, admin, region);
        var groundShare = host.Population <= 0 ? 0f : M.Clamp01(region.Population / (float)host.Population);
        var levy = MathF.Max(0f, host.WeeklyTaxIncome) * groundShare * share * campaign.Defines.KinAdminLevy * Coverage(region);
        return levy - WeeklyUpkeep(campaign, admin, region);
    }

    /// <summary>
    /// How likely the host's service is to find these offices in a given week.
    ///
    /// Not a constant, because a constant turns a foothold into a countdown: with 3,5 % a week
    /// sixteen offices were opened in ten years of the bench and sixteen were found, so the rung
    /// that is supposed to outlive a single rising never outlived one. Two things move it, both
    /// already in the world rather than kept aside for this mechanic:
    ///
    /// - A stable state with nothing else on its mind watches; one that is coming apart has other
    ///   worries. (<see cref="Nation.Stability"/>.)
    /// - Where our people *are* the region, foreign paperwork has somewhere to hide among its own;
    ///   where they are a bare quarter of it, every clerk stands out. (The same share that decides
    ///   whether the region may be worked at all.)
    /// </summary>
    public static float Watchfulness(Campaign campaign, Nation host, float share)
    {
        if (campaign?.Defines is null || host is null) return 1f;
        var attention = 0.35f + M.Clamp01(host.Stability);
        var cover = M.Clamp(1.6f - share, 0.4f, 1.6f);
        return campaign.Defines.KinAdminDiscovery * attention * cover;
    }

    /// <summary>What the panel says about them, in one line, or an empty string when there are none.</summary>
    public static string Describe(Campaign campaign, ScenarioState region)
    {
        if (campaign is null || region is null || region.AdministeredById < 0) return "";
        var admin = campaign.NationById(region.AdministeredById);
        if (admin is null) return "";
        var money = region.AdministrationTaken >= 1f
            ? region.AdministrationTaken.ToString("0.00") + " bn"
            : (region.AdministrationTaken * 1000f).ToString("0.0") + " m";
        return Loc.Format("{0} runs its own offices here: {1} taken so far{2}.", admin.DisplayName, money,
            region.AdministrationExposed ? Loc.Get(", and everybody knows it") : "") +
            (Coverage(region) < .9999f ? " " + Loc.Format("Local coverage: {0:P1} of this region's residents.", Coverage(region)) : "");
    }

    /// <summary>Why the offices cannot be opened here, in a sentence, or an empty string when they can.</summary>
    public static string Refusal(Campaign campaign, Nation country, ScenarioState region)
    {
        if (campaign?.Defines is null || country is null || region is null) return Loc.Get("This cannot be done.");
        if (Any(region)) return Loc.Get("Somebody's own offices already stand in this region.");
        var host = KinStatecraft.HostOf(campaign, region);
        if (host is null || host.Id == country.Id)
            return Loc.Get("These are our own people, in our own country; there is no border to work across.");
        if (CampaignDiplomacy.Peek(campaign, country.Id, host.Id)?.Stance == DiplomaticStance.AtWar)
            return Loc.Get("We are already at war with them; offices would be an occupation, and we have soldiers for that.");
        var (ours, _, share) = KinStatecraft.Kin(campaign, country, region);
        if (ours <= 0 || share < campaign.Defines.KinAdminMinShare)
            return Loc.Format("Too few of our people live in {0} to make our paperwork the one people use.", region.DisplayName);
        if (country.Treasury < OpenCost(campaign, country, region)) return Loc.Get("The treasury cannot pay to open them.");
        if (country.PoliticalPoints < campaign.Defines.KinAdminPoliticalCost)
            return Loc.Get("No government can be seen opening these against its own credit.");
        return "";
    }

    /// <summary>Open our offices in a foreign region. The caller has asked <see cref="Refusal"/>.</summary>
    public static bool Open(Campaign campaign, Nation country, ScenarioState region)
    {
        if (campaign?.Defines is null || country is null || region is null || Any(region)) return false;
        region.AdministeredById = country.Id;
        region.AdministeredSinceDay = campaign.ElapsedDays;
        region.AdministrationExposed = false;
        region.AdministrationTaken = 0f;
        region.AdministrationProvinces.Clear(); region.AdministrationPopulationShare = 1f;
        country.Treasury -= OpenCost(campaign, country, region);
        country.PoliticalPoints = MathF.Max(0f, country.PoliticalPoints - campaign.Defines.KinAdminPoliticalCost);
        var host = KinStatecraft.HostOf(campaign, region);
        if (host is not null)
        {
            var tie = CampaignDiplomacy.Get(campaign, country.Id, host.Id);
            var amount = campaign.Defines.KinAdminGrievance * (0.5f + KinStatecraft.Kin(campaign, country, region).Share);
            if (tie.A == host.Id) tie.GrievanceA = MathF.Min(100f, tie.GrievanceA + amount);
            else if (tie.B == host.Id) tie.GrievanceB = MathF.Min(100f, tie.GrievanceB + amount);
            tie.LastActionDay = campaign.ElapsedDays;
        }
        campaign.Chronicle.Record(campaign, "administration_opened", country.Id, host?.Id ?? -1, region.DisplayName, isPublic: false);
        DiagnosticLogs.Write("simulation",
            $"KIN_ADMIN_OPEN by={country.Tag} region={region.Name} host={host?.Tag ?? "?"} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>Close them: ours to close, or the host's to shut.</summary>
    public static void Close(Campaign campaign, ScenarioState region, string kind)
    {
        if (campaign is null || region is null || !Any(region)) return;
        var admin = campaign.NationById(region.AdministeredById);
        var host = KinStatecraft.HostOf(campaign, region);
        region.AdministeredById = -1;
        region.AdministeredSinceDay = -1;
        region.AdministrationExposed = false;
        region.AdministrationProvinces.Clear(); region.AdministrationPopulationShare = 1f;
        campaign.Chronicle.Record(campaign, kind == "shut" ? "administration_shut" : "administration_closed",
            admin?.Id ?? host?.Id ?? -1, host?.Id ?? -1, region.DisplayName, isPublic: kind == "shut");
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// The week's work of every administration in the world: what it takes, what it costs, what
    /// it does to the ground, and whether anybody has noticed yet.
    ///
    /// The three fates are all here on purpose, because they are the whole mechanic: it pays, it
    /// sours the ground under the host, and it can end - quietly when nobody uses it any more,
    /// loudly when the host proves it and has the strength to act.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same weekly gate, staged so the frame can pause inside the region pass.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign?.States is null || campaign?.Defines is null) yield break;
        if (campaign.ElapsedDays <= 0 || campaign.ElapsedDays % 7 != 0) yield break;
        foreach (var _ in TickWeekStages(campaign)) yield return 0;
    }

    /// <summary>
    /// One week's work, without asking whether today is the day for it: the bench and the tests
    /// need to call the week directly, and a second copy of these rules would drift from this one.
    /// </summary>
    internal static void TickWeek(Campaign campaign)
    {
        foreach (var _ in TickWeekStages(campaign)) { }
    }

    /// <summary>The same week, yielded between batches of regions.</summary>
    internal static IEnumerable<int> TickWeekStages(Campaign campaign)
    {
        if (campaign?.States is null || campaign?.Defines is null) yield break;
        var defines = campaign.Defines;
        var scanned = 0;
        foreach (var region in campaign.States)
        {
            if (++scanned % 128 == 0) yield return 0;
            if (region is null || region.AdministeredById < 0) continue;
            var admin = campaign.NationById(region.AdministeredById);
            var host = KinStatecraft.HostOf(campaign, region);
            if (admin is null || host is null || host.Id == admin.Id)
            {
                Close(campaign, region, "gone");
                continue;
            }
            var (ours, _, share) = KinStatecraft.Kin(campaign, admin, region);
            if (ours <= 0 || share < defines.KinAdminMinShare * 0.5f)
            {
                // The people it was for are no longer there in numbers: it withers rather than
                // being shut, which is what happens to an institution nobody uses.
                Close(campaign, region, "gone");
                continue;
            }

            // What the host no longer collects, and what we pay to be the ones people go to.
            var groundShare = host.Population <= 0 ? 0f : M.Clamp01(region.Population / (float)host.Population);
            var levy = MathF.Max(0f, host.WeeklyTaxIncome) * groundShare * share * defines.KinAdminLevy * Coverage(region);
            var upkeep = WeeklyUpkeep(campaign, admin, region);
            host.Treasury -= levy;
            admin.Treasury = MathF.Max(0f, admin.Treasury + levy - upkeep);
            region.AdministrationTaken += levy;
            region.Radicalism = M.Clamp01(region.Radicalism + defines.KinAdminRadicalism * (0.5f + share) * Coverage(region));

            if (!region.AdministrationExposed)
            {
                var roll = MovementRules.Roll(campaign.Seed, admin.Id, campaign.ElapsedDays, region.MapIndex + 7);
                if (roll < Watchfulness(campaign, host, share))
                {
                    region.AdministrationExposed = true;
                    var tie = CampaignDiplomacy.Get(campaign, admin.Id, host.Id);
                    if (tie.A == host.Id) tie.GrievanceA = MathF.Min(100f, tie.GrievanceA + defines.KinAdminCaughtGrievance);
                    else if (tie.B == host.Id) tie.GrievanceB = MathF.Min(100f, tie.GrievanceB + defines.KinAdminCaughtGrievance);
                    OpinionSystem.Remember(campaign, tie, "caught_running_our_offices");
                    campaign.Chronicle.Record(campaign, "administration_exposed", admin.Id, host.Id, region.DisplayName, isPublic: true);
                    DiagnosticLogs.Write("simulation",
                        $"KIN_ADMIN_EXPOSED by={admin.Tag} host={host.Tag} region={region.Name} day={campaign.ElapsedDays}");
                }
                continue;
            }

            // Public: the host tries to shut them, and how hard it can depends on how strong it is.
            var shutdown = MovementRules.Roll(campaign.Seed, host.Id, campaign.ElapsedDays, region.MapIndex + 13);
            var grip = M.Clamp01(host.Stability * 0.6f + host.WarSupport * 0.4f);
            if (shutdown < defines.KinAdminShutdownChance * grip)
            {
                OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, admin.Id, host.Id), "shut_our_offices");
                Close(campaign, region, "shut");
                DiagnosticLogs.Write("simulation",
                    $"KIN_ADMIN_SHUT by={host.Tag} admin={admin.Tag} region={region.Name} day={campaign.ElapsedDays}");
            }
        }
    }
}
