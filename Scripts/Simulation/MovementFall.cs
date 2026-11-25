using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The chance a fallen country's government goes into exile rather than simply ending, before what raises it.</summary>
    public float MovementExileBase { get; set; } = 0.2f;
    /// <summary>Added for a country a friend will take in (opinion above 30, not at war with its conqueror).</summary>
    public float MovementExileFriend { get; set; } = 0.25f;
    /// <summary>Added per unit of the country's stability when it fell, and once for a country of more than five million.</summary>
    public float MovementExileStability { get; set; } = 0.25f;
    public float MovementExileLarge { get; set; } = 0.15f;
    /// <summary>
    /// Added for a country a human plays. Nought: a player's government goes abroad on the
    /// same chances as anybody's (the rule of parity), and a country that does not still
    /// leaves the player its movements and its exile's comeback to play.
    /// </summary>
    public float MovementExilePlayer { get; set; } = 0f;
    /// <summary>Support for a government in exile among its own people in its homeland, when it goes.</summary>
    public float MovementExileSupport { get; set; } = 0.45f;
    /// <summary>Cadres per division it had in the field, which go abroad with it rather than into captivity.</summary>
    public float MovementExileCadresPerDivision { get; set; } = 2500f;
    /// <summary>A movement with nowhere to stand for this many weeks is no more.</summary>
    public int MovementDissolveHomelessWeeks { get; set; } = 12;
    /// <summary>A landless movement with fewer cadres than this, no money and no following is no more.</summary>
    public float MovementDissolveCadres { get; set; } = 300f;
    /// <summary>
    /// A movement (not a government in exile) beaten down this far - cadres under this, a following
    /// under MovementCrushedFollowing - is broken: leaders in prison, the rank and file gone home.
    /// </summary>
    public float MovementCrushedCadres { get; set; } = 1500f;
    public float MovementCrushedFollowing { get; set; } = 0.06f;
    /// <summary>A peaceful movement this marginal and this tame folds into ordinary politics.</summary>
    public float MovementFoldFollowing { get; set; } = 0.04f;
    /// <summary>
    /// How settled under its new rulers every region of a government in exile's homeland must be
    /// (ScenarioState.BorderHabit, 0..1) before, with its following gone, it fades into history.
    /// 0.9 is about 28 years at BorderHabitYears 12. Above 1 keeps every exile for ever.
    /// </summary>
    public float MovementExileForgottenHabit { get; set; } = 0.9f;
    public float MovementFoldMilitancy { get; set; } = 0.08f;
    /// <summary>
    /// The chance a beaten rising or breakaway goes underground rather than ending, before what
    /// raises it: the radicalism of its homeland (MovementUndergroundRadicalism per unit of it,
    /// weighted by people) and a friend abroad who would shelter it.
    /// </summary>
    public float MovementUndergroundBase { get; set; } = 0.25f;
    /// <summary>
    /// Whether a side of the war of the weak that has lost the last region it owned, and still
    /// fights on (Factions.FightsOn as if it held nothing), is a side under arms rather than a
    /// fallen country: it does not fall, its war is not ended for it, and its formations are
    /// not laid down with a state that is not there (Factions.FightsOnWithoutGround). With it,
    /// such a side does not sign a peace that leaves it nothing - the line as the border, or a
    /// treaty taking the last of its ground (Factions.KeepsNothingByTheLine, PeaceConference) -
    /// and one that came out of a peace with ground and an army is not folded back into the
    /// state it fought (Factions.SettleCivilWarSurvivors). Off, each of those is as it was.
    /// </summary>
    public bool RisingsFightOnWithoutGround { get; set; } = true;
    public float MovementUndergroundRadicalism { get; set; } = 0.9f;
    public float MovementUndergroundSanctuary { get; set; } = 0.15f;
    /// <summary>Its following at home when it goes: this much, plus as much again as the region is radical.</summary>
    public float MovementUndergroundSupport { get; set; } = 0.2f;
    /// <summary>Where its methods start: well into armed struggle.</summary>
    public float MovementUndergroundMilitancy { get; set; } = 0.72f;
    /// <summary>
    /// A fallen country's movement abroad or underground that the world has stopped taking
    /// seriously - legitimacy under this - that no country both recognises and pays for, and that
    /// has no real strength left at home, is fading. An unrecognised movement drifts towards a
    /// standing of 0.1, a hundredth of the gap a week (MovementRules), and each country listing
    /// it as terrorist pulls 0.0004 a week, so one listing alone settles it near 0.06: 0.05 is
    /// reached only by two or more listings that recognition does not offset, or by its own
    /// violence, and a movement that stops either climbs back over the line.
    /// </summary>
    public float MovementExileFadeLegitimacy { get; set; } = 0.05f;
    /// <summary>
    /// How long it may go on fading before it is gone: three years, long enough that a bad
    /// spell, a lost patron or a wave of listings can be lived through and a new patron found,
    /// short enough that a government nobody recognises does not keep an office on the map for
    /// decades. 0 or less turns the rule off.
    /// </summary>
    public int MovementExileFadeDays { get; set; } = 1095;
}

/// <summary>
/// The two ends of a movement's life that are not an uprising: a country that loses the last of
/// its ground and carries on as a government in exile - or does not, and is gone - and a
/// movement that is ground down until there is nothing left of it.
///
/// A fall is decided once, the week the country is found with no ground. Whether it goes into
/// exile is a draw against a chance that a friend abroad, a settled country and a large one all
/// raise, and a human player's country nearly always makes: the war being lost should not also
/// be the game being over. The exile is a movement made from the `government_in_exile` template:
/// its own peoples, a homeland of the regions it held when it was first seen, a headquarters in
/// the friendliest country that will have it, the respect a government has, the recognition of
/// everybody who thought well of it, and cadres out of the army it had.
///
/// Only a country that had a government goes into exile. A faction of the bookmark, a front of
/// a civil war, a breakaway or a piece a war carved out of a state that still claims it has no
/// government for anybody to recognise abroad: beaten, it goes underground among its own people
/// or it is over (<see cref="HadGovernment"/>). There is never a second exile for a state that
/// already has one, and an exile nobody takes seriously and nobody pays for fades.
/// </summary>
public static class MovementFall
{
    /// <summary>The id every government in exile is founded under, followed by its tag.</summary>
    internal const string ExilePrefix = "exile_";

    /// <summary>
    /// Whether this country had a government of its own - a state the world dealt with as one -
    /// rather than an armed group, a front, or a piece cut out of a state that still claims it.
    /// Read from what the country recorded when it was born and whether anybody still claims it,
    /// never from its size, its place or who plays it: a faction of history/factions (the Rapid
    /// Support Forces, the Kachin Independence Organisation), a country born in a rising, and a
    /// breakaway or piece of a state that still claims it are not; a country of the bookmark is,
    /// and so is a side that took the state's own name and seat, because from that day it was
    /// that state's government, and so is a country released without a rising - by a treaty, a
    /// dismantlement, its own parent - once no living state claims it, because then it governed
    /// as a state.
    /// </summary>
    public static bool HadGovernment(Campaign campaign, Nation nation)
    {
        if (nation is null || !string.IsNullOrEmpty(nation.FactionId)) return false;
        if (nation.Name?.StartsWith("rising:", StringComparison.Ordinal) == true) return false;
        // Taking the state over gives a side the state's name and clears its parent's claim on it
        // (Factions.TakeOver, SettleCivilWarSurvivors). A breakaway that only named itself after
        // a country no longer standing keeps that claim, and is still a breakaway.
        if (nation.FoundNameKind == "tag" && nation.SecededFromId < 0) return true;
        if (!string.IsNullOrEmpty(nation.FoundedInRevolt)) return false;
        if (nation.SecededFromId < 0 && nation.RevoltParentId < 0 && nation.ReleasedFromId < 0) return true;
        // Let go rather than torn away: released, and nobody holding ground claims it back now -
        // the claim was never written or was cleared, or the state that held it is gone.
        if (nation.ReleasedFromId >= 0 && !StillClaimed(campaign, nation)) return true;
        // A country the scenario shipped that walked out of an overlord carries the overlord as
        // its parent, but it was a state before it was a subject: nobody released it and it was
        // never given a founding name.
        return nation.ReleasedFromId < 0 && string.IsNullOrEmpty(nation.FoundNameKind);
    }

    /// <summary>Whether the state it broke from still stands on ground of its own and so still claims it.</summary>
    private static bool StillClaimed(Campaign campaign, Nation nation) =>
        nation.SecededFromId >= 0 && campaign?.NationById(nation.SecededFromId) is { } claimant &&
        claimant.Id != nation.Id && claimant.Movement is null && campaign.HasOwnedLand(claimant);

    /// <summary>A government in exile, as against a beaten rising gone underground (which is also flagged Exile).</summary>
    internal static bool GovernmentInExile(MovementState state) =>
        state is { Exile: true } && !state.Id.StartsWith(MovementCatalog.UndergroundPrefix, StringComparison.Ordinal);

    /// <summary>The ground each country holds the first time it is seen holding any.</summary>
    internal static void RememberHomelands(Campaign campaign)
    {
        // Almost always everybody is known already, and then there is nothing to walk.
        var anyone = false;
        foreach (var nation in campaign.Nations)
            if (nation.Movement is null && !campaign.Homelands.ContainsKey(nation.Id) && campaign.StatesOf(nation).Length > 0)
            {
                anyone = true;
                break;
            }
        if (!anyone) return;
        Dictionary<int, List<int>> held = null;
        foreach (var region in campaign.States)
        {
            var owner = MovementRules.OwnerOf(campaign, region);
            if (owner is null || owner.Movement is not null || campaign.Homelands.ContainsKey(owner.Id)) continue;
            // A map artefact (common/country_merges) carried by an old save is not a people's state:
            // no homeland to return to.
            if (CountryMerges.IsMerged(owner.Tag)) continue;
            held ??= new Dictionary<int, List<int>>();
            if (!held.TryGetValue(owner.Id, out var list)) held[owner.Id] = list = new List<int>();
            list.Add(region.MapIndex);
        }
        if (held is null) return;
        foreach (var (id, regions) in held) campaign.Homelands[id] = regions;
    }

    /// <summary>
    /// The fall undone by the map: a country the weekly review ruled fallen, that holds ground of
    /// its own again, is a country again.
    ///
    /// The review puts a country on the list the week it is found with no ground, and four paths
    /// took it off - a restoration, a movement graduating, a release that revives the tag, a
    /// movement re-founded under it. Every other road back to the map did not: a status quo peace
    /// that leaves a fallen country's surviving divisions holding what they occupied, a coalition
    /// that hands a beaten ally its land back, a civil war returning ground to its parent. All of
    /// them go through StatePartition.Annex and end at MarkMapChanged, and none of them knew the
    /// list existed. So the country stood on the map with its regions and its people in them,
    /// while everything that reads the list took it for dead - the economy most of all, which
    /// emptied it every week: 41 of them in a 107-year campaign, 360 countries with ground and
    /// only 320 with anybody in them.
    ///
    /// The rule is the review's own, run backwards: the review rules a country fallen when it
    /// holds no region (StatesOf), and a country holding one is not. Only a country without a
    /// movement - an exile or an underground that takes ground back is settled by its own path
    /// (MovementRules.SynchroniseTerritory, Restoration.RestoreExiledNation). Its people are
    /// counted again on the spot rather than at the economy's next week, because a week of a
    /// country with ground and nobody in it is what the player saw. Walked over the nation list,
    /// in order, so the day it happens is the same in every replay.
    ///
    /// People are not the whole of a country, and the four older roads back each gave the rest
    /// too. The books: the fall closed them at zero (StatePartition.Annex, the economy's week), and
    /// a country returned with zero converges on what it makes at a tenth a week - a Congo of seven
    /// million taxing 0.007 a week, two months below the world's poorest tenth, unable to pay for
    /// an army or a budget. A closed book is reopened at what its people made by the country's own
    /// 2024 measure (ProductionCapacity), the way MovementRules.TakeZone seeds a movement's first
    /// ground, and the market corrects it from there. And the seat: the fall took the capital to
    /// nothing, and CapitalCore never places one for a country that has none - only
    /// LocateCapitals does, which loading a save runs and a running campaign did not. So a revived
    /// country had no seat until it was saved and opened again, and the two worlds differed on the
    /// seat, the supply origin and the capital's battles with the same fingerprint. The same call
    /// the load makes is made here.
    /// </summary>
    internal static int ReturnTheLanded(Campaign campaign)
    {
        if (campaign is null || campaign.Fallen.Count == 0) return 0;
        var returned = 0;
        var seatless = false;
        for (var id = 0; id < campaign.Nations.Count; id++)
        {
            var nation = campaign.Nations[id];
            if (nation.Movement is not null || !campaign.Fallen.Contains(nation.Id)) continue;
            var held = campaign.StatesOf(nation);
            if (held.Length == 0) continue;
            campaign.Fallen.Remove(nation.Id);
            long people = 0;
            foreach (var region in held) people += Math.Max(0L, region.Population);
            nation.Population = people;
            ReopenBooks(nation);
            if (nation.CapitalProvinceId <= 0) seatless = true;
            returned++;
            DiagnosticLogs.Write("simulation",
                $"FALLEN_RETURNED nation={nation.Tag} regions={held.Length} people={people} gdp={nation.Gdp:0.###} day={campaign.ElapsedDays}");
        }
        if (returned > 0)
        {
            // Once for all of them, and only with the map attached: a save being read has none
            // yet, and attaching it runs this same call.
            if (seatless && campaign.ProvinceMap is not null)
            {
                campaign.LocateCapitals(preserveExisting: true);
                campaign.CapitalRevision++;
            }
            campaign.MovementRevision++;
            campaign.MarkStrategicChange();
        }
        return returned;
    }

    /// <summary>
    /// A returned country's books, closed at zero by its fall, opened at what its people make by
    /// its own measure. Only a closed book: a country the list kept by mistake but the economy
    /// never emptied has a GDP of its own, and that is better than any estimate. The capacity is
    /// the 2024 output per head of this country times the people it has now (ProductionCapacity),
    /// in the scale the economy prices it at, so the next week's market moves it by what changed
    /// rather than rebuilding it from nothing.
    /// </summary>
    private static void ReopenBooks(Nation nation)
    {
        if (nation.Gdp > 0f || nation.Population <= 0) return;
        var capacity = ProductionCapacity.Of(nation);
        if (!(capacity > 0f) || !float.IsFinite(capacity)) return;
        nation.Gdp = capacity;
        if (nation.OutputScale > 0f) nation.GrossOutput = capacity / (52f * nation.OutputScale);
    }

    /// <summary>Every country found with no ground this week has its fall decided, once.</summary>
    internal static void Decide(Campaign campaign)
    {
        foreach (var _ in DecideStages(campaign)) { }
    }

    internal static IEnumerable<int> DecideStages(Campaign campaign)
    {
        // Nothing in this walk founds or removes a nation. Each fall is settled before
        // looking at the next country, including any new exile the next country may find.
        for (var id = 0; id < campaign.Nations.Count; id++)
        {
            if (id > 0 && Campaign.StageOverdue()) yield return 0;
            var nation = campaign.Nations[id];
            if (nation.Movement is not null || campaign.Fallen.Contains(nation.Id)) continue;
            // A real country (people, a tag, a founding) holding no ground at the weekly
            // review is a ghost: every founding path seeds homelands now, so a missing
            // entry can no longer exempt a stateless straggler from the fall decision.
            if ((nation.Population <= 0 && (!campaign.Homelands.ContainsKey(nation.Id) || nation.CollapseAnnounced)) ||
                campaign.StatesOf(nation).Length > 0) continue;
            // A rising that has lost its last town and is still fighting has not fallen: it falls
            // the week its fighters or its will are gone (Factions.FightsOnWithoutGround).
            if (Factions.FightsOnWithoutGround(campaign, nation)) continue;
            campaign.Fallen.Add(nation.Id);
            // A country that never held ground of its own was a line on a map, not a state, and
            // has no government to send abroad. Nothing the world is built from is one any more
            // (the map source's glaciers and base areas are read past, CountryMerges); what
            // still arrives here is a country a mod or an old save left without a homeland.
            // So is an entry of the merge table an old save still carries, whatever was remembered
            // for it: no exile, no underground, nothing for a later rising to revive.
            if (!campaign.Homelands.TryGetValue(nation.Id, out var held) || held.Count == 0 || CountryMerges.IsMerged(nation.Tag))
            {
                nation.Population = 0;
                if (CountryMerges.IsMerged(nation.Tag)) campaign.Homelands.Remove(nation.Id);
                nation.PublicImages.Clear();
                continue;
            }
            // Only a government goes abroad; an armed group or a fragment has nobody to recognise.
            // The refuge is looked for either way, because a friend abroad who would shelter its
            // leaders is also what makes a beaten rising likelier to go underground.
            var government = HadGovernment(campaign, nation);
            // Whatever happens next, it is not a front of a civil war any more: it is named the way
            // every other new country is, so no list, chronicle or save shows "rising:...".
            SettleName(campaign, nation);
            var chance = ExileChance(campaign, nation, out var refuge);
            if (government && refuge is not null && MovementRules.Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, 91) < chance)
            {
                // Never two governments in exile for one lost state: the one already abroad
                // speaks for this ground, and a second office beside it would speak for nobody.
                // Only a government abroad stands in the way: a rising gone underground on the same
                // ground does not stop the state's own government leaving, and gives way to it at
                // its next review (OutrankedBy) - the fall is decided once, and refusing here
                // would erase the government altogether.
                if (ExileOfTheSameState(campaign, nation, governmentsOnly: true) is null)
                {
                    GoIntoExile(campaign, nation, refuge);
                    continue;
                }
            }
            if ((!government || Insurgent(nation)) && ExileOfTheSameState(campaign, nation) is null &&
                MovementRules.Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, 92) < UndergroundChance(campaign, nation, refuge))
                GoUnderground(campaign, nation);
        }
    }

    /// <summary>
    /// A live exile or underground that already carries this country's cause: the same lost
    /// state (<see cref="SameState"/>). Most of the ground on both sides, not any of it: a region
    /// a front cut in two is the homeland of both sides, and a militia whose valley lies inside
    /// a fallen country is not that country.
    /// </summary>
    internal static Nation ExileOfTheSameState(Campaign campaign, Nation nation, bool governmentsOnly = false)
    {
        HashSet<string> homeland = null;
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id || other.Movement is not { Exile: true, Landless: true, Dissolved: false } theirs ||
                (governmentsOnly && !GovernmentInExile(theirs))) continue;
            if (SharesStateName(nation, other)) return other;
            if (theirs.Homeland.Count == 0) continue;
            if (homeland is null)
            {
                homeland = new HashSet<string>(StringComparer.Ordinal);
                if (campaign.Homelands.TryGetValue(nation.Id, out var held))
                    foreach (var index in held)
                        if (campaign.StateByMapIndex(index) is { } region) homeland.Add(region.Id);
            }
            var needed = Math.Max(homeland.Count, theirs.Homeland.Count) / 2 + 1;
            if (Math.Min(homeland.Count, theirs.Homeland.Count) < needed) continue;
            var shared = 0;
            foreach (var region in homeland)
                if (theirs.Homeland.Contains(region) && ++shared >= needed) return other;
        }
        return null;
    }

    /// <summary>
    /// A front of a civil war is written down as "rising:template:PARENT:Parent" while it holds
    /// ground, because the war reads its parent from that record. Once it has none it is a front
    /// of nothing, and the record would be the name a list, a chronicle line or a save shows. It
    /// takes the name its founding gave it (NationNaming, the same path every new country's name
    /// comes from); one founded before that path existed is given one now.
    /// </summary>
    internal static void SettleName(Campaign campaign, Nation nation)
    {
        if (nation?.Name is not { } record || !record.StartsWith("rising:", StringComparison.Ordinal)) return;
        if (string.IsNullOrEmpty(nation.FoundNameKind) && string.IsNullOrWhiteSpace(nation.CustomName))
        {
            var parent = campaign.NationById(nation.RevoltParentId >= 0 ? nation.RevoltParentId : nation.SecededFromId);
            var regions = campaign.Homelands.TryGetValue(nation.Id, out var held) ? held.ToArray() : Array.Empty<int>();
            NationNaming.ApplyTo(campaign, nation, parent, regions, "auto", nation.FoundedCultures.FirstOrDefault() ?? "");
        }
        var proper = CountryNames.Resolve(nation, false);
        if (string.IsNullOrWhiteSpace(proper) || proper.StartsWith("rising:", StringComparison.Ordinal) ||
            NationNaming.LooksLikeKey(proper)) return;
        nation.Name = proper;
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// A war is over when one side has nothing left to fight from. A leader with no region of
    /// its own and no ally holding any - a rising whose last town has been taken, a country
    /// overrun - surrenders that week, on the map as it stands, and what is left of its army
    /// lays down its arms. Before this the war went on with nobody on the other side: the
    /// state that had taken back every province of its breakaway kept a war open against a
    /// country without an inch of ground, and in the end signed a peace with it.
    /// A side whose allies still hold ground fights on: a government overrun in a coalition
    /// war is liberated or not by the coalition, as Norway and the Netherlands were.
    /// </summary>
    public static void EndLostWars(Campaign campaign)
    {
        foreach (var _ in EndLostWarsStages(campaign, sliced: false)) { }
    }

    internal static IEnumerable<int> EndLostWarsStages(Campaign campaign, bool sliced = true)
    {
        var openWars = Warfare.OpenWars(campaign);
        for (var index = 0; index < openWars.Count; index++)
        {
            if (sliced && index > 0 && Campaign.StageOverdue()) yield return 0;
            var war = openWars[index];
            if (war.Ended) continue;
            for (var side = 0; side < 2; side++)
            {
                var attackers = side == 0;
                var leader = campaign.NationById(attackers ? war.AttackerId : war.DefenderId);
                var victor = campaign.NationById(attackers ? war.DefenderId : war.AttackerId);
                if (leader is null || victor is null || campaign.StatesOf(leader).Length > 0) continue;
                // A movement that never had ground fights from among its people; this is for
                // the country that lost what it had - or the government it sent abroad.
                if (leader.Movement is not null && !campaign.Fallen.Contains(leader.Id)) continue;
                // Nor is this for a side of the war of the weak still under arms: it has lost its
                // towns, not its war (Factions.FightsOnWithoutGround).
                if (Factions.FightsOnWithoutGround(campaign, leader)) continue;
                var standing = false;
                foreach (var ally in war.Side(attackers))
                    if (ally != leader.Id && campaign.NationById(ally) is { } friend && campaign.StatesOf(friend).Length > 0)
                    {
                        standing = true;
                        break;
                    }
                if (standing) continue;
                if (sliced)
                {
                    Campaign.HeavyNext();
                    yield return 0;
                }
                PeaceNegotiation.Resolve(campaign, war, leader.Id, PeaceTerms.Capitulate);
                if (!war.Ended) continue;
                campaign.Chronicle.Record(campaign, "peace_signed", victor.Id, leader.Id);
                foreach (var army in campaign.Armies.Where(army => army.NationId == leader.Id).ToList())
                {
                    WarLosses.Surrender(campaign, army);
                    Attrition.Destroy(campaign, army, "laid down its arms: its country holds no ground");
                }
                campaign.MarkStrategicChange();
                if (sliced) yield return 0;
                break;
            }
        }
    }

    /// <summary>A country that was born fighting: a rising, a breakaway, a movement that took ground.</summary>
    private static bool Insurgent(Nation nation) =>
        nation.SecededFromId >= 0 || !string.IsNullOrEmpty(nation.FoundedInRevolt) ||
        nation.Name.StartsWith("rising:", StringComparison.Ordinal);

    /// <summary>
    /// How likely a beaten rising is to go underground: the Taliban of 2001 rather than the
    /// Confederacy of 1865. A furious homeland hides its fighters and a friend abroad shelters
    /// their leaders; a calm one hands them over. The roll against it is what makes one
    /// insurgency vanish and the next come back twenty years later.
    /// </summary>
    public static float UndergroundChance(Campaign campaign, Nation nation, ScenarioState sanctuary)
    {
        var defines = campaign.Defines;
        double people = 0, radical = 0;
        if (campaign.Homelands.TryGetValue(nation.Id, out var homeland))
            foreach (var index in homeland)
                if (campaign.States.Find(region => region.MapIndex == index) is { Population: > 0 } region)
                {
                    people += region.Population;
                    radical += region.Population * (double)M.Clamp01(region.Radicalism);
                }
        if (people <= 0) return 0f;
        var chance = defines.MovementUndergroundBase + defines.MovementUndergroundRadicalism * (float)(radical / people);
        if (sanctuary is not null) chance += defines.MovementUndergroundSanctuary;
        return M.Clamp(chance, 0f, 0.95f);
    }

    /// <summary>
    /// A beaten rising goes to ground in its own homeland: a movement of its own peoples with
    /// its headquarters in the region that hates the victor most, its fighters hidden among
    /// the people as cadres, a following as deep as the region is radical, and its methods well
    /// into armed struggle. Nothing about it is a country any more; everything about it can
    /// rise again, once the victor's hold on the ground has stopped being new (RevoltRecovery).
    /// </summary>
    /// <summary>This session's count of risings that went underground, for the bench. Not campaign state.</summary>
    public static int WentUnderground;

    public static void GoUnderground(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;
        if (!campaign.Homelands.TryGetValue(nation.Id, out var homeland) || homeland.Count == 0) return;
        var regions = homeland.Select(index => campaign.States.Find(region => region.MapIndex == index))
            .Where(region => region is { Population: > 0 }).ToList();
        if (regions.Count == 0) return;
        var seat = regions.OrderByDescending(region => region.Radicalism).ThenByDescending(region => region.Population)
            .ThenBy(region => region.MapIndex).First();
        var state = new MovementState
        {
            Id = MovementCatalog.UndergroundPrefix + nation.Tag.ToLowerInvariant(),
            Exile = true,
            Landless = true,
            HeadquartersStateMapIndex = seat.MapIndex,
            Legitimacy = MovementCatalog.Of(MovementCatalog.UndergroundTemplate)?.Legitimacy ?? 0.2f,
            Militancy = defines.MovementUndergroundMilitancy
        };
        // Its peoples: the ones it was founded for, else whoever is most of its homeland.
        var peoples = nation.FoundedCultures is { Count: > 0 }
            ? nation.FoundedCultures.ToList()
            : regions.SelectMany(region => region.PopulationGroups)
                .GroupBy(group => group.Culture, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Culture: group.Key, Size: group.Sum(each => each.Size)))
                .OrderByDescending(group => group.Size).ThenBy(group => group.Culture, StringComparer.Ordinal)
                .Take(2).Where(group => group.Culture.Length > 0).Select(group => group.Culture).ToList();
        state.Peoples.AddRange(peoples.Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (var region in regions)
        {
            state.Homeland.Add(region.Id);
            state.Support[region.MapIndex] = M.Clamp(defines.MovementUndergroundSupport + region.Radicalism, 0f, 0.85f);
        }

        // The fighters do not surrender; they go home with their rifles.
        var divisions = 0f;
        foreach (var army in campaign.Armies.Where(army => army.NationId == nation.Id).ToList())
        {
            divisions += army.Divisions * M.Clamp01(army.Strength);
            Attrition.Destroy(campaign, army, "went underground", lost: false);
        }
        state.Cadres = MathF.Max(1500f, divisions * defines.MovementExileCadresPerDivision + nation.ManpowerReserve * 0.03f);
        state.Infiltrated[seat.MapIndex] = state.Cadres * 0.3f;

        // Its first year is a year of grace like any movement's (Matured): the crushed and folded
        // endings judge what it does, not the week it went to ground.
        state.ReadyOnDay["lifecycle_born"] = campaign.ElapsedDays;
        nation.Movement = state;
        nation.Treasury *= 0.3f;
        nation.Population = 0;
        nation.ManpowerReserve = state.Cadres;
        if (state.Peoples.Count > 0 && nation.FoundedCultures.Count == 0) nation.FoundedCultures.AddRange(state.Peoples);
        state.LastReport = Loc.Format("Beaten in the field, it has gone underground in {0}.", seat.DisplayName);
        WentUnderground++;
        campaign.Chronicle.Record(campaign, "went_underground", nation.Id,
            MovementRules.OwnerOf(campaign, seat)?.Id ?? -1, place: seat.DisplayName);
        Movements.Place(campaign);
        campaign.MarkStrategicChange();
    }

    /// <summary>How likely this country's government is to carry on abroad, and where it would go.</summary>
    public static float ExileChance(Campaign campaign, Nation nation, out ScenarioState refuge)
    {
        var defines = campaign.Defines;
        var found = Refuge(campaign, nation);
        refuge = found;
        if (found is null) return 0f;
        var host = campaign.NationByMapIndex(found.NationMapIndex);
        var chance = defines.MovementExileBase + defines.MovementExileStability * nation.Stability;
        if (host is not null && (CampaignDiplomacy.Peek(campaign, host.Id, nation.Id)?.Opinion ?? 0f) >= 30f) chance += defines.MovementExileFriend;
        if (campaign.Homelands.TryGetValue(nation.Id, out var homeland))
        {
            long homelandPopulation = 0L;
            foreach (var index in homeland) homelandPopulation += campaign.StateByMapIndex(index)?.Population ?? 0L;
            if (homelandPopulation > 5_000_000L) chance += defines.MovementExileLarge;
        }
        if (nation.IsHuman) chance += defines.MovementExilePlayer;
        return M.Clamp01(chance);
    }

    /// <summary>The countries that took its ground, and anybody allied to one of them.</summary>
    private static HashSet<int> ConquerorsOf(Campaign campaign, Nation nation)
    {
        var conquerors = new HashSet<int>();
        if (campaign.Homelands.TryGetValue(nation.Id, out var homeland))
            foreach (var index in homeland)
                if (campaign.StateByMapIndex(index) is { } region &&
                    MovementRules.OwnerOf(campaign, region) is { } owner && owner.Id != nation.Id)
                    conquerors.Add(owner.Id);
        if (conquerors.Count > 0)
        {
            var primary = conquerors.ToArray();
            foreach (var candidate in campaign.Nations)
                for (var i = 0; i < primary.Length; i++)
                {
                    var conqueror = primary[i];
                    if (candidate.Id != conqueror && CampaignDiplomacy.Peek(campaign, candidate.Id, conqueror)?.Stance == DiplomaticStance.Allied)
                        conquerors.Add(candidate.Id);
                }
        }
        return conquerors;
    }

    /// <summary>
    /// How much a country is a friend to this one - what a government in exile or a movement looks
    /// for in a host, and what makes a government recognise one: what it thinks of it, whether they
    /// are allies or friends, whether it has promised to defend it, how alike their politics are.
    /// Opinion alone is nought between most countries on the first day, which is how a Ukrainian
    /// government in exile ended up in Java.
    /// </summary>
    public static float Warmth(Campaign campaign, Nation host, Nation guest, IEnumerable<int> against = null)
    {
        if (host is null || guest is null || host.Id == guest.Id) return 0f;
        var relation = CampaignDiplomacy.Peek(campaign, host.Id, guest.Id);
        // And the enemy of its enemy: a rival of whoever took its ground, or holds what it claims.
        var enmity = 0f;
        if (against is not null)
            foreach (var enemyId in against)
            {
                if (enemyId == host.Id) return -200f;
                var toEnemy = CampaignDiplomacy.Peek(campaign, host.Id, enemyId);
                if (toEnemy is null) continue;
                // The enemy's own ally never takes them in, and its friends think twice.
                if (toEnemy.Stance == DiplomaticStance.Allied) return -200f;
                var felt = toEnemy.Stance switch
                {
                    DiplomaticStance.Rival or DiplomaticStance.AtWar => 35f,
                    DiplomaticStance.Friendly => -30f,
                    _ => 0f
                };
                felt -= toEnemy.Opinion * 0.4f;
                // And politics far from the enemy's - or close to it: nobody has to have quarrelled
                // yet on the first day for a democracy to take in the government a dictatorship threw out.
                felt -= PoliticsCore.Affinity(campaign, host.Id, enemyId) * 0.6f;
                enmity += felt;
            }
        var warmth = (relation?.Opinion ?? 0f) + PoliticsCore.Affinity(campaign, host.Id, guest.Id) * 0.5f;
        warmth += (relation?.Stance ?? DiplomaticStance.Neutral) switch
        {
            DiplomaticStance.Allied => 60f,
            DiplomaticStance.Friendly => 30f,
            DiplomaticStance.Rival => -40f,
            DiplomaticStance.AtWar => -200f,
            _ => 0f
        };
        if (relation is not null)
        {
            if (relation.GuaranteeByA || relation.GuaranteeByB) warmth += 30f;
            if (relation.TradeAgreement) warmth += 8f;
        }
        if (CampaignDiplomacy.SharesLandBorder(campaign, host.MapIndex, guest.MapIndex)) warmth += 6f;
        // And nearness: a government that has to flee goes next door or to its own part of the
        // world far more often than across it - London and Warsaw, not Havana.
        var apart = NavalOperations.Distance(host.CapitalLatitude, host.CapitalLongitude, guest.CapitalLatitude, guest.CapitalLongitude);
        warmth += 30f * MathF.Exp(-apart / 15f);
        return warmth + enmity;
    }

    /// <summary>The friendliest country not at war with it and not one of those that took its ground, and a city there.</summary>
    private static ScenarioState Refuge(Campaign campaign, Nation nation)
    {
        var conquerors = ConquerorsOf(campaign, nation);
        Nation best = null;
        var bestWarmth = float.MinValue;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Id == nation.Id || candidate.Movement is not null || conquerors.Contains(candidate.Id)) continue;
            if (CampaignDiplomacy.AtWar(campaign, candidate.Id, nation.Id)) continue;
            if (campaign.StatesOf(candidate).Length == 0) continue;
            // A bigger country has more room for a government that is not its own.
            // A bigger country has room for a government that is not its own; a sliver of one has not.
            var warmth = Warmth(campaign, candidate, nation, conquerors) + MathF.Min(20f, MathF.Sqrt(MathF.Max(0f, candidate.Gdp)) / 3f);
            if (warmth <= bestWarmth) continue;
            bestWarmth = warmth;
            best = candidate;
        }
        return best is null ? null : SeatRegion(campaign, best, nation);
    }

    /// <summary>
    /// Which of a host's regions a headquarters goes to: one of its five biggest - where the offices,
    /// the embassies and the airports are - and likelier the more of the guest's own people live
    /// there, chosen once per guest and host so it does not wander between loads.
    /// </summary>
    public static ScenarioState SeatRegion(Campaign campaign, Nation host, Nation guest)
    {
        var regions = campaign.StatesOf(host).OrderByDescending(region => region.Population).Take(5).ToList();
        if (regions.Count == 0) return null;
        var peoples = Movements.DefinitionOf(guest) is { } definition
            ? definition.Peoples
            : guest.FoundedCultures ?? new List<string>();
        var weights = new List<float>();
        foreach (var region in regions)
        {
            var weight = MathF.Sqrt(MathF.Max(1f, region.Population));
            foreach (var (culture, size) in EconomySystem.Cultures(region))
                if (MovementDefinition.Listed(peoples, culture) && region.Population > 0)
                    weight *= 1f + 4f * size / (float)region.Population;
            weights.Add(weight);
        }
        var roll = MovementRules.Roll(campaign.Seed, guest.Id, host.Id, 5) * weights.Sum();
        for (var index = 0; index < regions.Count; index++)
        {
            roll -= weights[index];
            if (roll <= 0f) return regions[index];
        }
        return regions[^1];
    }

    /// <summary>The government leaves and carries on: the country becomes a movement.</summary>
    internal static void GoIntoExile(Campaign campaign, Nation nation, ScenarioState refuge)
    {
        var defines = campaign.Defines;
        var state = new MovementState
        {
            Id = "exile_" + nation.Tag.ToLowerInvariant(),
            Exile = true,
            Landless = true,
            HeadquartersStateMapIndex = refuge.MapIndex,
            Legitimacy = MovementCatalog.Of(MovementCatalog.ExileTemplate)?.Legitimacy ?? 0.5f
        };
        var peoples = nation.FoundedCultures is { Count: > 0 }
            ? nation.FoundedCultures
            : (IEnumerable<string>)DemographyCatalog.PrimarySet(nation.Tag) ?? Array.Empty<string>();
        state.Peoples.AddRange(peoples.Distinct(StringComparer.OrdinalIgnoreCase));
        if (campaign.Homelands.TryGetValue(nation.Id, out var homeland))
            foreach (var index in homeland)
                if (campaign.States.Find(region => region.MapIndex == index) is { } region)
                {
                    state.Homeland.Add(region.Id);
                    state.Support[region.MapIndex] = defines.MovementExileSupport;
                }

        // What is left of the army goes abroad as cadres, not into captivity.
        var divisions = 0f;
        foreach (var army in campaign.Armies.Where(army => army.NationId == nation.Id).ToList())
        {
            divisions += army.Divisions;
            Attrition.Destroy(campaign, army, "went into exile", lost: false);
        }
        state.Cadres = MathF.Max(2000f, divisions * defines.MovementExileCadresPerDivision + nation.ManpowerReserve * 0.02f);

        // Its friends go on recognising it; those who took its ground, and their allies, do not.
        // Everybody warm enough, and its five warmest friends in any case: a government in exile
        // is recognised at least by the countries nearest to it in every sense.
        var conquerors = ConquerorsOf(campaign, nation);
        var friends = new List<(Nation Other, float Warmth)>();
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id || other.Movement is not null || campaign.StatesOf(other).Length == 0) continue;
            if (conquerors.Contains(other.Id)) continue;
            var warmth = Warmth(campaign, other, nation, conquerors);
            if (warmth > 0f) friends.Add((other, warmth));
        }
        friends.Sort((left, right) => right.Warmth.CompareTo(left.Warmth));
        for (var index = 0; index < friends.Count; index++)
            if (index < 5 || friends[index].Warmth >= 20f) state.RecognisedBy.Add(friends[index].Other.Id);

        state.ReadyOnDay["lifecycle_born"] = campaign.ElapsedDays;
        nation.Movement = state;
        nation.Treasury *= 0.5f;
        nation.Population = 0;
        nation.ManpowerReserve = state.Cadres;
        if (state.Peoples.Count > 0 && nation.FoundedCultures.Count == 0) nation.FoundedCultures.AddRange(state.Peoples);
        state.LastReport = Loc.Format("The government has left the country and carries on from {0}.", refuge.DisplayName);
        Movements.Place(campaign);
        campaign.MarkStrategicChange();
    }

    /// <summary>Whether a landless movement has come to its end this week, and ends it if so.</summary>
    internal static void Endure(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        if (state is null || state.Dissolved || !state.Landless) return;
        var defines = campaign.Defines;

        // A government of no country: a save from before Decide knew the difference sent the
        // Siachen Glacier's into exile in Illinois. Nobody followed it, because nobody lived there.
        if (state.Exile && state.Homeland.Count == 0)
        {
            Dissolve(campaign, nation, Loc.Get("Nobody is left to carry it on: the movement is no more."));
            return;
        }

        // A save from before only governments went abroad can hold the office of a militia, a
        // front or a carved-off piece. It closes at its next review, as it would never open now.
        if (state.Exile && state.Id.StartsWith(ExilePrefix, StringComparison.Ordinal) && !HadGovernment(campaign, nation))
        {
            SettleName(campaign, nation);
            Dissolve(campaign, nation, Loc.Get("It never was a government: with no ground of its own, what was left of it has scattered."));
            return;
        }
        // And an underground from such a save may still be written down as a front of its war.
        SettleName(campaign, nation);
        // An underground from a save made before undergrounds were stamped at their founding: the
        // day of this review stands in for it, so the crushed and folded endings, which now reach
        // it, wait their year from now rather than having been satisfied since the world's first
        // year. A government abroad is left as it was: those endings never apply to it, and the
        // one that does (a generation's forgetting) is decades long.
        if (state.Exile && !GovernmentInExile(state) && !state.ReadyOnDay.ContainsKey("lifecycle_born"))
            state.ReadyOnDay["lifecycle_born"] = campaign.ElapsedDays;
        // Two for one lost state, from a save made before the second was refused: the government
        // abroad is kept before an underground, and the older country before the younger.
        if (state.Exile && OutrankedBy(campaign, nation) is { } elder)
        {
            Dissolve(campaign, nation, Loc.Format("{0} already speaks for this country: this one has passed into history.", elder.DisplayName));
            return;
        }

        // Nowhere to stand: every week it looks for somewhere, and in the end it gives up.
        var host = Movements.HostOf(campaign, nation);
        if (host is null || host.Movement is not null || CampaignDiplomacy.AtWar(campaign, host.Id, nation.Id))
        {
            var refuge = MovementRules.BestRefuge(campaign, nation, host);
            if (refuge is not null && CampaignDiplomacy.Get(campaign,
                    campaign.Nations.Find(candidate => candidate.MapIndex == refuge.NationMapIndex)?.Id ?? -1, nation.Id).Opinion > -25f)
            {
                state.HeadquartersStateMapIndex = refuge.MapIndex;
                state.HomelessWeeks = 0;
                Movements.Place(campaign);
                state.LastReport = Loc.Format("The headquarters has found a home in {0}.", refuge.DisplayName);
            }
            else state.HomelessWeeks++;
        }
        else state.HomelessWeeks = 0;

        var following = state.Support.Count == 0 ? 0f : state.Support.Values.Max();
        if (state.HomelessWeeks >= defines.MovementDissolveHomelessWeeks)
            Dissolve(campaign, nation, Loc.Get("With nowhere left that would have it, the movement has broken up."));
        else if (state.Cadres < defines.MovementDissolveCadres && nation.Treasury < 0.002f && following < 0.05f)
            Dissolve(campaign, nation, Loc.Get("Nobody is left to carry it on: the movement is no more."));
        // The ways movements really end. A government in exile lives on its legitimacy and
        // outlasts all of this; an organisation of people at home does not - but not in its
        // first year: a movement is judged on what it does, not on the day it was written down.
        // A beaten rising gone underground is such an organisation, for all that it shares the
        // exile's flag (which is what lets it come home): it can be broken like any other.
        else if (!GovernmentInExile(state) && Matured(campaign, state) && state.Cadres < defines.MovementCrushedCadres &&
                 following < defines.MovementCrushedFollowing && !campaign.Armies.Exists(a => a.NationId == nation.Id))
            Dissolve(campaign, nation, Loc.Get("Its leaders are in prison or dead, and the rank and file have gone home: the movement is broken."));
        else if (!GovernmentInExile(state) && Matured(campaign, state) && state.Militancy >= 0f && state.Militancy < defines.MovementFoldMilitancy &&
                 following < defines.MovementFoldFollowing && !Warfare.OpenWars(campaign).Any(w => !w.Ended && w.Involves(nation.Id)))
            Dissolve(campaign, nation, Loc.Get("What is left of it has joined ordinary politics: the movement has folded into the parties."));
        // Legitimacy is what an exile lives on, and recognition from a capital that gives nothing
        // costs that capital nothing. One the world no longer takes seriously and nobody pays for
        // fades, for every country alike, after MovementExileFadeDays of it.
        else if (Faded(campaign, nation, state, following))
            Dissolve(campaign, nation, Loc.Get("Nobody takes it seriously any more and nobody pays for it: what was left of it has faded away."));
        // A government in exile outlives everything but its people's forgetting: a generation
        // grown up at home under other flags, and almost nobody there still following it.
        else if (state.Exile && Matured(campaign, state) && following < defines.MovementFoldFollowing &&
                 HomelandSettled(campaign, state, defines.MovementExileForgottenHabit) &&
                 !Warfare.OpenWars(campaign).Any(w => !w.Ended && w.Involves(nation.Id)))
            Dissolve(campaign, nation, Loc.Get("A generation has grown up at home under other flags: the government in exile is a memory now."));
    }

    private static bool HomelandSettled(Campaign campaign, MovementState state, float habit)
    {
        if (habit > 1f || state.Homeland.Count == 0) return false;
        foreach (var id in state.Homeland)
            if (Movements.StateById(campaign, id) is not { } region || region.BorderHabit < habit) return false;
        return true;
    }

    /// <summary>The day it began to fade, kept with the movement's other clocks so a save keeps it.</summary>
    internal const string FadingKey = "exile_fading";

    /// <summary>
    /// Whether an exile or underground has been fading - legitimacy under MovementExileFadeLegitimacy,
    /// no patron, no war of its own, no real strength at home - for MovementExileFadeDays. The
    /// clock starts the first week all of that is true and is wound back the first week any of
    /// it is not. Strength at home is what the crushed ending reads (MovementFall.Endure): a
    /// following of at least MovementCrushedFollowing with at least MovementCrushedCadres behind
    /// it, or formations in the field. The world's opinion is not the end of a cause its own
    /// people still carry.
    /// </summary>
    private static bool Faded(Campaign campaign, Nation nation, MovementState state, float following)
    {
        var defines = campaign.Defines;
        var strong = following >= defines.MovementCrushedFollowing && state.Cadres >= defines.MovementCrushedCadres ||
                     campaign.Armies.Exists(army => army.NationId == nation.Id);
        if (!state.Exile || defines.MovementExileFadeDays <= 0 || state.Legitimacy >= defines.MovementExileFadeLegitimacy ||
            strong || HasPatron(campaign, state) || Warfare.OpenWars(campaign).Any(w => !w.Ended && w.Involves(nation.Id)))
        {
            state.ReadyOnDay.Remove(FadingKey);
            return false;
        }
        if (!state.ReadyOnDay.TryGetValue(FadingKey, out var since))
        {
            state.ReadyOnDay[FadingKey] = campaign.ElapsedDays;
            return false;
        }
        return campaign.ElapsedDays - since >= defines.MovementExileFadeDays;
    }

    /// <summary>A country with ground that both recognises it and pays for it: a patron, not a well-wisher.</summary>
    internal static bool HasPatron(Campaign campaign, MovementState state)
    {
        foreach (var id in state.RecognisedBy)
            if (state.SponsoredBy.ContainsKey(id) && campaign.NationById(id) is { Movement: null } patron && campaign.HasOwnedLand(patron))
                return true;
        return false;
    }

    /// <summary>
    /// The live exile or underground that carries the same lost state and comes first: a
    /// government abroad before an underground, then the older country. The same state is most
    /// of each one's homeland shared, or one having taken the other's name and seat.
    /// </summary>
    private static Nation OutrankedBy(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id || other.Movement is not { Exile: true, Landless: true, Dissolved: false } theirs) continue;
            if (!SameState(nation, state, other, theirs)) continue;
            var mine = GovernmentInExile(state);
            var elder = GovernmentInExile(theirs);
            if (elder && !mine || elder == mine && other.Id < nation.Id) return other;
        }
        return null;
    }

    private static bool SameState(Nation a, MovementState first, Nation b, MovementState second)
    {
        if (SharesStateName(a, b)) return true;
        if (first.Homeland.Count == 0 || second.Homeland.Count == 0) return false;
        var shared = 0;
        foreach (var id in first.Homeland)
            if (second.Homeland.Contains(id)) shared++;
        return shared * 2 > first.Homeland.Count && shared * 2 > second.Homeland.Count;
    }

    private static bool SharesStateName(Nation a, Nation b) =>
        (a.FoundNameKind == "tag" && string.Equals(a.FoundNameId, b.Tag, StringComparison.OrdinalIgnoreCase)) ||
        (b.FoundNameKind == "tag" && string.Equals(b.FoundNameId, a.Tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>A year since it was founded, or since the world began for one that was there already.</summary>
    private static bool Matured(Campaign campaign, MovementState state) =>
        campaign.ElapsedDays - Math.Max(0, state.ReadyOnDay.GetValueOrDefault("lifecycle_born")) >= 365;

    /// <summary>The end of it.</summary>
    public static void Dissolve(Campaign campaign, Nation nation, string why, bool record = true)
    {
        var state = nation.Movement;
        if (state is null || state.Dissolved) return;
        state.Dissolved = true;
        state.ReadyOnDay["lifecycle_rebirth"] = campaign.ElapsedDays + Math.Max(30, campaign.Defines.MovementRebirthDays);
        state.Support.Clear();
        state.Infiltrated.Clear();
        state.OnTheWay.Clear();
        state.SponsoredBy.Clear();
        state.RecognisedBy.Clear();
        state.InvitedBy.Clear();
        state.MovingToStateMapIndex = -1;
        state.HeadquartersStateMapIndex = -1;
        state.Cadres = 0f;
        nation.ManpowerReserve = 0f;
        nation.PublicImages.Clear();
        state.LastReport = why;
        foreach (var army in campaign.Armies.Where(army => army.NationId == nation.Id).ToList())
            Attrition.Destroy(campaign, army, "movement dissolved", lost: false);
        campaign.MovementRevision++;
        campaign.MarkStrategicChange();
        if (record) campaign.Chronicle.Record(campaign, "movement_dissolved", nation.Id);
    }
}
