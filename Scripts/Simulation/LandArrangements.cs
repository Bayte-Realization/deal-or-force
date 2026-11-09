using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>A lease asked for in play runs this many years unless the asker names another term.</summary>
    public int LeaseDefaultYears { get; set; } = 25;

    /// <summary>The rent a lease asks a year: this share of what the region makes a year, and at least the floor, in billions.</summary>
    public float LeaseRentShare { get; set; } = 0.3f;
    public float LeaseRentFloor { get; set; } = 0.02f;

    /// <summary>What the owner must think of the asker before a computer government lets it have the land.</summary>
    public float LeaseOpinionNeeded { get; set; } = 25f;

    /// <summary>Weeks of unpaid rent after which a lease lapses.</summary>
    public int LeaseUnpaidWeeks { get; set; } = 8;

    /// <summary>Days a tenant has to leave once its lease has ended or been ended - and in which it may decide not to.</summary>
    public int LeaseGraceDays { get; set; } = 90;

    /// <summary>How long an offer of a lease waits on a player's desk.</summary>
    public int LeaseOfferDays { get; set; } = 60;

    /// <summary>
    /// Troops in without a war: the share of a region's people that must be the sender's own for
    /// "protecting compatriots" to be a reason it can give; the owner's stability below which the
    /// state is collapsing and nobody is there to stop it; and the divisions it must have.
    /// </summary>
    public float PresenceKinShare { get; set; } = 0.4f;
    public float PresenceCollapseStability { get; set; } = 0.3f;
    public float PresenceMinimumDivisions { get; set; } = 2f;

    /// <summary>What sending troops in without a war costs the sender in the world's eyes, and in the owner's and its friends' opinion.</summary>
    public float PresenceNotoriety { get; set; } = 12f;

    /// <summary>How long troops must have stood in a region before a vote there can be held, and the share of its people for whom the result is not wholly written in advance.</summary>
    public int PresenceReferendumDays { get; set; } = 30;
    public float PresenceReferendumKinShare { get; set; } = 0.5f;

    /// <summary>
    /// The computer: the monthly chance a government with a reason and an appetite sends troops
    /// in; the owner's strength, with its allies, against the sender's, at which a computer owner
    /// fights for the ground rather than protesting; and its monthly chance of doing so.
    /// </summary>
    public float AiPresenceChance { get; set; } = 0.1f;
    /// <summary>The share of a region's people that must be the sender's own for the computer to call it theirs, when it has no title to it.</summary>
    public float AiPresenceKinMajority { get; set; } = 0.55f;
    /// <summary>The highest war restraint (AiStatecraft.Restraint) at which a computer government sends troops in without a war.</summary>
    public float AiPresenceRestraintMax { get; set; } = 0.65f;
    /// <summary>The owner's strength with its allies, as a share of the sender's, that keeps a computer's troops at home.</summary>
    public float AiPresenceDeterrence { get; set; } = 0.7f;
    public float AiPresenceWarRatio { get; set; } = 0.8f;
    public float AiPresenceWarChance { get; set; } = 0.25f;
    public float AiPresenceReferendumChance { get; set; } = 0.15f;

    /// <summary>The computer's monthly chance, for a great power, of asking a friendly small coastal country for a base.</summary>
    public float AiLeaseRequestChance { get; set; } = 0.01f;

    /// <summary>What a lease between two countries adds to the price either pays, in the computer's reckoning, for a war on the other.</summary>
    public float AiLeaseWarCaution { get; set; } = 0.8f;
}

/// <summary>
/// Ground one state owns and another holds by arrangement rather than by war.
///
/// A lease: the owner keeps the land in law and the tenant runs it for a term at a rent -
/// Baikonur to 2050, Guantanamo since 1903, Hong Kong's New Territories for ninety-nine years,
/// Port Arthur, Sevastopol before 2014. A presence: troops in without a war - Crimea in February
/// 2014, Turkish soldiers in northern Cyprus, Russian ones on the Dniester - which the owner can
/// swallow, protest, or fight.
/// </summary>
public sealed class LandArrangement
{
    public int Id;
    /// <summary>"lease" or "presence".</summary>
    public string Kind = "lease";
    public int OwnerId;
    public int HolderId;
    /// <summary>The regions (state map indices) it covers.</summary>
    public List<int> Regions = new();
    public int StartDay;
    /// <summary>The day a lease ends; -1 for one with no end or for a presence.</summary>
    public int EndDay = -1;
    public float RentPerYear;
    public int UnpaidWeeks;
    /// <summary>What it is for, as a caption id: "spaceport", "naval_base", "military_base"; empty for a presence.</summary>
    public string Purpose = "";
    /// <summary>Why the troops are there: "history", "agreed", "stayed", "compatriots", "lease", "collapse".</summary>
    public string Origin = "agreed";
    /// <summary>The day a tenant whose lease has ended must be gone by; -1 while it has a right to be there.</summary>
    public int LeavingOnDay = -1;
}

/// <summary>An offer of a lease waiting on a player's desk, or a renewal a player must answer.</summary>
public sealed class LeaseOffer
{
    public int Id, FromId, ToId, Region, Years, CreatedDay, ExpiresDay;
    public float RentPerYear;
    public bool Renewal;
    public int ArrangementId = -1;
    public string Purpose = "";
    /// <summary>"pending", "accepted", "refused", "expired".</summary>
    public string State = "pending";
}

public sealed partial class Campaign
{
    public readonly List<LandArrangement> LandArrangements = new();
    public int NextLandArrangementId = 1;
    public readonly List<LeaseOffer> LeaseOffers = new();
    public int NextLeaseOfferId = 1;
}

public static class LandArrangementSystem
{
    public const string Lease = "lease";
    public const string Presence = "presence";

    /// <summary>This session's tallies, for the bench. Not campaign state.</summary>
    public static int Leased, Entered, Annexed, Fought, Stayed, Withdrew, Republics;
    /// <summary>Why the computer's candidates for troops in without a war were passed over, for the bench.</summary>
    public static readonly Dictionary<string, int> Passed = new();
    private static void Pass(string why) => Passed[why] = Passed.GetValueOrDefault(why) + 1;
    /// <summary>For the bench: each passed-over pair and the owner's strength against the sender's, or the refusal.</summary>
    public static readonly Dictionary<string, string> PassedPairs = new();
    /// <summary>For the bench: every arrangement that ended, how and when.</summary>
    public static readonly List<string> EndedLog = new();

    // ---- reading ------------------------------------------------------------------------

    /// <summary>The arrangement that puts this holder on this region, or null.</summary>
    public static LandArrangement Covering(Campaign c, int regionMapIndex, int holderId = -1)
    {
        foreach (var arrangement in c.LandArrangements)
            if ((holderId < 0 || arrangement.HolderId == holderId) && arrangement.Regions.Contains(regionMapIndex))
                return arrangement;
        return null;
    }

    /// <summary>Whether this holder stands on this province by arrangement, so the end of a war does not send it home.</summary>
    public static bool Covers(Campaign c, int province, int holderId)
    {
        if (c.LandArrangements.Count == 0 || c.ProvinceMap is null || province <= 0 || province > c.ProvinceMap.ProvinceCount) return false;
        return Covering(c, c.ProvinceMap.ProvinceToState[province], holderId) is not null;
    }

    public static IEnumerable<LandArrangement> Of(Campaign c, Nation nation) =>
        c.LandArrangements.Where(a => a.OwnerId == nation.Id || a.HolderId == nation.Id);

    /// <summary>What a region makes in a year, in billions, at its owner's scale.</summary>
    internal static float RegionYearlyValue(Campaign c, ScenarioState region)
    {
        var owner = c.NationByMapIndex(region.NationMapIndex);
        if (owner is null) return 0f;
        var value = region.RegionalOutputValue * 52f * MathF.Max(0f, owner.OutputScale);
        return float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
    }

    /// <summary>The rent a lease of these regions asks a year.</summary>
    public static float FairRent(Campaign c, IEnumerable<ScenarioState> regions) =>
        MathF.Max(c.Defines.LeaseRentFloor, regions.Sum(r => RegionYearlyValue(c, r)) * c.Defines.LeaseRentShare);

    private static IEnumerable<int> Provinces(Campaign c, LandArrangement arrangement)
    {
        var map = c.ProvinceMap;
        if (map is null) yield break;
        var regions = arrangement.Regions.ToHashSet();
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (!map.IsWater(province) && regions.Contains(map.ProvinceToState[province])) yield return province;
    }

    private static bool AtWar(Campaign c, int a, int b) =>
        CampaignDiplomacy.Get(c, a, b)?.Stance == DiplomaticStance.AtWar ||
        c.Wars.Any(w => !w.Ended && ((w.Side(true).Contains(a) && w.Side(false).Contains(b)) ||
                                     (w.Side(true).Contains(b) && w.Side(false).Contains(a))));

    /// <summary>
    /// The arrangements a campaign opens with, laid on the ground the moment the map is attached -
    /// before the first day, so a lease is on the map the moment the player looks at it, not a
    /// day later. No checks and no news: history says they stand.
    /// </summary>
    public static int LayOnGround(Campaign c, ProvinceTopology topology, ushort[] controllers)
    {
        if (c is null || topology is null || controllers is null || c.LandArrangements.Count == 0 ||
            !c.FeatureEnabled("land_arrangements")) return 0;
        var holders = new Dictionary<int, ushort>();
        foreach (var arrangement in c.LandArrangements)
            if (c.NationById(arrangement.HolderId) is { } holder)
                foreach (var region in arrangement.Regions) holders[region] = (ushort)holder.MapIndex;
        var laid = 0;
        for (var province = 1; province <= topology.ProvinceCount && province < controllers.Length; province++)
            if (!topology.IsWater(province) && holders.TryGetValue(topology.ProvinceToState[province], out var holderIndex))
            {
                controllers[province] = holderIndex;
                laid++;
            }
        return laid;
    }

    // ---- the daily hold -----------------------------------------------------------------

    /// <summary>
    /// Every day, before the occupations go home: the holder holds what it holds by arrangement,
    /// and an arrangement whose ground has changed hands, whose parties are at war or one of
    /// whom is gone, is over.
    /// </summary>
    public static void Enforce(Campaign c)
    {
        if (c.LandArrangements.Count == 0 || c.ProvinceController is null || c.ProvinceMap is null) return;
        var changed = false;
        for (var index = c.LandArrangements.Count - 1; index >= 0; index--)
        {
            var arrangement = c.LandArrangements[index];
            var owner = c.NationById(arrangement.OwnerId);
            var holder = c.NationById(arrangement.HolderId);
            arrangement.Regions.RemoveAll(region => c.States.Find(s => s.MapIndex == region) is not { } state ||
                                                   owner is null || state.NationMapIndex != owner.MapIndex);
            if (owner is null || holder is null || arrangement.Regions.Count == 0 ||
                c.StatesOf(holder).Length == 0 && holder.Movement is null)
            {
                End(c, arrangement, "ended");
                changed = true;
                continue;
            }
            // A war between them settles it the way wars do; the troops are an army now.
            if (AtWar(c, owner.Id, holder.Id))
            {
                c.LandArrangements.RemoveAt(index);
                EndedLog.Add($"day {c.ElapsedDays}: {arrangement.Kind} {holder.Tag} in {RegionNames(c, arrangement)} ({owner.Tag}) ended by war: " +
                             string.Join(" | ", c.Wars.Where(w => !w.Ended && w.Involves(owner.Id) && w.Involves(holder.Id)).Select(w => w.Name)));
                c.Chronicle.Record(c, arrangement.Kind == Lease ? "lease_voided_by_war" : "presence_became_war",
                    holder.Id, owner.Id, place: RegionNames(c, arrangement));
                changed = true;
                continue;
            }
            foreach (var province in Provinces(c, arrangement))
                if (c.SetProvinceController(province, (ushort)holder.MapIndex)) changed = true;
        }
        if (changed) c.MarkMapChanged();
    }

    // ---- the weekly round ---------------------------------------------------------------

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same weekly arrangements pass, yielded between leases and offers.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c.ElapsedDays % 7 != 5 || !c.FeatureEnabled("land_arrangements")) yield break;
        var ticked = 0;
        foreach (var arrangement in c.LandArrangements.ToList())
        {
            if (++ticked % 16 == 0) yield return 0;
            var owner = c.NationById(arrangement.OwnerId);
            var holder = c.NationById(arrangement.HolderId);
            if (owner is null || holder is null) continue;
            if (arrangement.Kind == Lease) LeaseWeek(c, arrangement, owner, holder);
            else PresenceWeek(c, arrangement, owner, holder);
        }
        foreach (var offer in c.LeaseOffers)
            if (offer.State == "pending" && offer.ExpiresDay <= c.ElapsedDays)
            {
                offer.State = "expired";
                if (offer.Renewal && c.LandArrangements.Find(a => a.Id == offer.ArrangementId) is { } lapsed)
                    Refuse(c, lapsed);
            }
        c.LeaseOffers.RemoveAll(offer => offer.State != "pending" && offer.ExpiresDay < c.ElapsedDays - 365);
        yield return 0;
        if (c.ElapsedDays % 28 == 5) AiMonth(c);
    }

    private static void LeaseWeek(Campaign c, LandArrangement lease, Nation owner, Nation holder)
    {
        // The rent.
        if (lease.LeavingOnDay < 0 && lease.RentPerYear > 0f)
        {
            var due = lease.RentPerYear / 52f;
            if (holder.Treasury >= due)
            {
                holder.Treasury -= due;
                owner.Treasury += due;
                lease.UnpaidWeeks = 0;
            }
            else if (++lease.UnpaidWeeks >= Math.Max(1, c.Defines.LeaseUnpaidWeeks))
            {
                c.Chronicle.Record(c, "lease_unpaid", holder.Id, owner.Id, place: RegionNames(c, lease));
                Refuse(c, lease);
                return;
            }
        }
        // A term run out: the owner decides whether it goes on.
        if (lease.EndDay >= 0 && lease.LeavingOnDay < 0 && c.ElapsedDays >= lease.EndDay)
        {
            if (owner.IsHuman)
            {
                if (!c.LeaseOffers.Any(o => o.State == "pending" && o.ArrangementId == lease.Id))
                    Offer(c, holder, owner, lease.Regions[0], Math.Max(1, c.Defines.LeaseDefaultYears), lease.RentPerYear, lease.Purpose, lease.Id);
            }
            else if (WouldLease(c, owner, holder, lease.RentPerYear))
            {
                lease.EndDay = c.ElapsedDays + 365 * Math.Max(1, c.Defines.LeaseDefaultYears);
                c.Chronicle.Record(c, "lease_renewed", holder.Id, owner.Id, place: RegionNames(c, lease));
            }
            else Refuse(c, lease);
        }
        // Out of time to leave: the computer decides whether it goes; a player who did not choose to stay goes.
        if (lease.LeavingOnDay >= 0 && c.ElapsedDays >= lease.LeavingOnDay)
        {
            if (!holder.IsHuman && WouldStay(c, holder, owner, lease)) Stay(c, lease, out _);
            else Withdraw(c, lease, "lease_ended_withdrew");
        }
    }

    private static void PresenceWeek(Campaign c, LandArrangement presence, Nation owner, Nation holder)
    {
        // The owner's people in the region do not take it quietly, and its government loses face.
        foreach (var region in presence.Regions)
            if (c.States.Find(s => s.MapIndex == region) is { } state)
                state.Radicalism = M.Clamp01(state.Radicalism + 0.002f);
        if (c.ElapsedDays - presence.StartDay < 120) owner.Stability = M.Clamp01(owner.Stability - 0.001f);
    }

    // ---- what the parties can do --------------------------------------------------------

    /// <summary>Why a holder may not ask for a lease of this region, or empty.</summary>
    public static string LeaseRefusal(Campaign c, Nation holder, ScenarioState region)
    {
        if (!c.FeatureEnabled("land_arrangements")) return Loc.Get("Leases are switched off.");
        if (holder is null || region is null) return Loc.Get("Nothing to lease.");
        var owner = c.NationByMapIndex(region.NationMapIndex);
        if (owner is null || owner.Id == holder.Id) return Loc.Get("This is our own ground.");
        if (owner.Movement is not null) return Loc.Get("A movement has no land to let.");
        if (Covering(c, region.MapIndex) is not null) return Loc.Get("Someone already holds this region by arrangement.");
        if (owner.CapitalStateMapIndex == region.MapIndex) return Loc.Get("No government lets its capital.");
        if (AtWar(c, owner.Id, holder.Id)) return Loc.Get("Not while we are at war with them.");
        if (c.LeaseOffers.Any(o => o.State == "pending" && o.FromId == holder.Id && o.ToId == owner.Id))
            return Loc.Get("They have our offer already.");
        var rent = FairRent(c, new[] { region });
        if (holder.Treasury < rent) return Loc.Format("A year's rent, {0:0.00} bn, is more than we have.", rent);
        return "";
    }

    /// <summary>
    /// Ask for a lease. A computer government answers now; a player finds it on the desk.
    /// Returns whether a lease exists afterwards.
    /// </summary>
    public static bool RequestLease(Campaign c, Nation holder, ScenarioState region, int years, string purpose, out string result)
    {
        result = LeaseRefusal(c, holder, region);
        if (result.Length > 0) return false;
        var owner = c.NationByMapIndex(region.NationMapIndex);
        var rent = FairRent(c, new[] { region });
        years = Math.Clamp(years <= 0 ? c.Defines.LeaseDefaultYears : years, 1, 99);
        purpose = string.IsNullOrWhiteSpace(purpose) ? PurposeFor(c, region) : purpose;
        if (owner.IsHuman)
        {
            Offer(c, holder, owner, region.MapIndex, years, rent, purpose, -1);
            result = Loc.Format("The offer is with {0}.", owner.DisplayName);
            return false;
        }
        if (!WouldLease(c, owner, holder, rent))
        {
            result = Loc.Format("{0} will not let the land to us.", owner.DisplayName);
            OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, holder.Id, owner.Id), "refused_our_lease");
            return false;
        }
        Grant(c, owner, holder, new[] { region.MapIndex }, years, rent, purpose, "agreed");
        result = Loc.Format("{0} lets us {1} for {2} years at {3:0.00} bn a year.", owner.DisplayName, region.DisplayName, years, rent);
        return true;
    }

    private static string PurposeFor(Campaign c, ScenarioState region) =>
        region.Buildings.GetValueOrDefault(BuildingKind.Port) > 0 || region.Buildings.GetValueOrDefault(BuildingKind.Shipyard) > 0
            ? "naval_base" : "military_base";

    private static void Offer(Campaign c, Nation from, Nation to, int region, int years, float rent, string purpose, int arrangementId)
    {
        c.LeaseOffers.Add(new LeaseOffer
        {
            Id = c.NextLeaseOfferId++, FromId = from.Id, ToId = to.Id, Region = region, Years = years,
            RentPerYear = rent, Purpose = purpose, Renewal = arrangementId >= 0, ArrangementId = arrangementId,
            CreatedDay = c.ElapsedDays, ExpiresDay = c.ElapsedDays + Math.Max(7, c.Defines.LeaseOfferDays)
        });
        c.MarkStrategicChange();
    }

    /// <summary>A player answers an offer on the desk.</summary>
    public static bool Answer(Campaign c, Nation owner, int offerId, bool accept, out string result)
    {
        result = "";
        var offer = c.LeaseOffers.Find(o => o.Id == offerId && o.ToId == owner.Id && o.State == "pending");
        if (offer is null) { result = Loc.Get("The offer is no longer open."); return false; }
        var holder = c.NationById(offer.FromId);
        var lease = offer.Renewal ? c.LandArrangements.Find(a => a.Id == offer.ArrangementId) : null;
        offer.State = accept ? "accepted" : "refused";
        if (holder is null) return false;
        if (offer.Renewal)
        {
            if (lease is null) return false;
            if (accept)
            {
                lease.EndDay = c.ElapsedDays + 365 * Math.Max(1, offer.Years);
                c.Chronicle.Record(c, "lease_renewed", holder.Id, owner.Id, place: RegionNames(c, lease));
            }
            else Refuse(c, lease);
            return true;
        }
        if (!accept)
        {
            OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, holder.Id, owner.Id), "refused_our_lease");
            return true;
        }
        var region = c.States.Find(s => s.MapIndex == offer.Region);
        if (region is null || region.NationMapIndex != owner.MapIndex || Covering(c, region.MapIndex) is not null)
        { result = Loc.Get("The region is no longer ours to let."); return false; }
        Grant(c, owner, holder, new[] { region.MapIndex }, offer.Years, offer.RentPerYear, offer.Purpose, "agreed");
        return true;
    }

    /// <summary>Whether a computer owner lets the land: it must think well enough of the tenant, not be its rival, and the rent must be fair.</summary>
    internal static bool WouldLease(Campaign c, Nation owner, Nation holder, float rent)
    {
        var relation = CampaignDiplomacy.Get(c, owner.Id, holder.Id);
        if (relation is null || relation.Stance is DiplomaticStance.Rival or DiplomaticStance.AtWar) return false;
        var opinion = relation.Opinion;
        // A poor owner is readier to rent: the money matters more to it.
        var need = owner.Gdp <= 0f ? 0f : M.Clamp01(rent / MathF.Max(0.01f, owner.Gdp) * 200f) * 15f;
        return opinion + need >= c.Defines.LeaseOpinionNeeded || owner.AnswersTo(holder.Id);
    }

    internal static LandArrangement Grant(Campaign c, Nation owner, Nation holder, IEnumerable<int> regions, int years, float rent, string purpose, string origin)
    {
        var lease = new LandArrangement
        {
            Id = c.NextLandArrangementId++, Kind = Lease, OwnerId = owner.Id, HolderId = holder.Id,
            StartDay = c.ElapsedDays, EndDay = years <= 0 ? -1 : c.ElapsedDays + 365 * years,
            RentPerYear = rent, Purpose = purpose ?? "", Origin = origin
        };
        lease.Regions.AddRange(regions);
        c.LandArrangements.Add(lease);
        var relation = CampaignDiplomacy.Get(c, owner.Id, holder.Id);
        OpinionSystem.Remember(c, relation, "leased_land");
        Leased++;
        c.Chronicle.Record(c, "lease_granted", holder.Id, owner.Id, place: RegionNames(c, lease),
            subject: years <= 0 ? Loc.Get("without a term") : Loc.Format("for {0} years", years));
        Enforce(c);
        return lease;
    }

    /// <summary>The owner will not go on: the tenant has LeaseGraceDays to leave - or to decide not to.</summary>
    private static void Refuse(Campaign c, LandArrangement lease)
    {
        if (lease.LeavingOnDay >= 0) return;
        lease.LeavingOnDay = c.ElapsedDays + Math.Max(7, c.Defines.LeaseGraceDays);
        c.Chronicle.Record(c, "lease_not_renewed", lease.HolderId, lease.OwnerId, place: RegionNames(c, lease));
        c.MarkStrategicChange();
    }

    /// <summary>The owner ends a lease before its term.</summary>
    public static bool EndLease(Campaign c, Nation owner, LandArrangement lease, out string result)
    {
        result = "";
        if (lease is null || lease.Kind != Lease || lease.OwnerId != owner.Id) { result = Loc.Get("No lease of ours to end."); return false; }
        if (lease.LeavingOnDay >= 0) { result = Loc.Get("They are already leaving."); return false; }
        OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, owner.Id, lease.HolderId), "ended_our_lease");
        Refuse(c, lease);
        return true;
    }

    /// <summary>Whether a computer tenant refused its lease stays anyway: a strong one with its people there, an appetite and little to fear.</summary>
    internal static bool WouldStay(Campaign c, Nation holder, Nation owner, LandArrangement lease)
    {
        var defines = c.Defines;
        var ours = WarAims.Cultures(holder);
        var kin = lease.Regions.Select(r => c.States.Find(s => s.MapIndex == r)).Where(r => r is not null)
            .Select(r => WarAims.CultureShare(r, ours)).DefaultIfEmpty(0f).Max();
        if (kin < defines.PresenceKinShare) return false;
        return OwnerSide(c, owner, holder) < AiStatecraft.StrengthOf(c, holder) * defines.AiPresenceDeterrence &&
               AiStatecraft.Restraint(c, holder) <= defines.AiPresenceRestraintMax;
    }

    /// <summary>A tenant whose lease has ended keeps its troops where they are: the lease becomes a presence.</summary>
    public static bool Stay(Campaign c, LandArrangement lease, out string result)
    {
        result = "";
        if (lease is null || lease.Kind != Lease || lease.LeavingOnDay < 0) { result = Loc.Get("We have every right to be there."); return false; }
        var owner = c.NationById(lease.OwnerId);
        var holder = c.NationById(lease.HolderId);
        if (owner is null || holder is null) return false;
        lease.Kind = Presence;
        lease.Origin = "stayed";
        lease.LeavingOnDay = -1;
        lease.EndDay = -1;
        lease.RentPerYear = 0f;
        lease.StartDay = c.ElapsedDays;
        Condemn(c, holder, owner);
        Stayed++;
        c.Chronicle.Record(c, "lease_overstayed", holder.Id, owner.Id, place: RegionNames(c, lease));
        c.MarkStrategicChange();
        return true;
    }

    /// <summary>The holder goes home: a lease or a presence, at the holder's own choice or at the end of its time.</summary>
    public static void Withdraw(Campaign c, LandArrangement arrangement, string kind = "withdrew")
    {
        if (!c.LandArrangements.Remove(arrangement)) return;
        var owner = c.NationById(arrangement.OwnerId);
        foreach (var province in Provinces(c, arrangement))
            if (owner is not null) c.SetProvinceController(province, (ushort)owner.MapIndex);
        Withdrew++;
        c.Chronicle.Record(c, arrangement.Kind == Lease && kind == "withdrew" ? "lease_given_up" : kind,
            arrangement.HolderId, arrangement.OwnerId, place: RegionNames(c, arrangement));
        c.MarkMapChanged();
    }

    private static void End(Campaign c, LandArrangement arrangement, string why)
    {
        c.LandArrangements.Remove(arrangement);
        EndedLog.Add($"day {c.ElapsedDays}: {arrangement.Kind} {arrangement.HolderId}>{arrangement.OwnerId} ended: {why}");
        DiagnosticLogs.Write("simulation", $"ARRANGEMENT_ENDED id={arrangement.Id} kind={arrangement.Kind} why={why} day={c.ElapsedDays}");
    }

    // ---- troops in without a war ---------------------------------------------------------

    /// <summary>The reason a country may give for sending troops into this region without a war, or empty when it has none.</summary>
    public static string PresenceClaim(Campaign c, Nation holder, ScenarioState region)
    {
        var owner = c.NationByMapIndex(region.NationMapIndex);
        if (owner is null || owner.Id == holder.Id) return "";
        if (Covering(c, region.MapIndex, holder.Id) is { Kind: Lease }) return "lease";
        var touches = TerritorialCoherence.TouchesOwnedGround(c, holder, region.MapIndex);
        if (!touches) return "";
        var title = TerritorialClaims.TitleOf(c, holder, region);
        if (title == TerritorialTitle.Homeland) return "homeland";
        if (WarAims.CultureShare(region, WarAims.Cultures(holder)) >= c.Defines.PresenceKinShare) return "compatriots";
        if (title == TerritorialTitle.Claim) return "claim";
        if (owner.Stability < c.Defines.PresenceCollapseStability) return "collapse";
        return "";
    }

    /// <summary>Why a country may not send troops in, or empty.</summary>
    public static string PresenceRefusal(Campaign c, Nation holder, ScenarioState region)
    {
        if (!c.FeatureEnabled("land_arrangements")) return Loc.Get("Arrangements over land are switched off.");
        if (holder is null || region is null) return Loc.Get("Nowhere to send them.");
        var owner = c.NationByMapIndex(region.NationMapIndex);
        if (owner is null || owner.Id == holder.Id) return Loc.Get("This is our own ground.");
        if (AtWar(c, owner.Id, holder.Id)) return Loc.Get("We are at war with them: this is a matter for the army.");
        if (Covering(c, region.MapIndex) is { Kind: Presence }) return Loc.Get("Foreign troops already stand there.");
        if (owner.CapitalStateMapIndex == region.MapIndex) return Loc.Get("A capital is not taken without a war.");
        if (CampaignDiplomacy.Get(c, holder.Id, owner.Id)?.Stance == DiplomaticStance.Allied) return Loc.Get("Not against an ally.");
        if (owner.AnswersTo(holder.Id) || holder.AnswersTo(owner.Id)) return Loc.Get("Not between an overlord and its subject.");
        var divisions = c.Armies.Where(a => a.NationId == holder.Id).Sum(a => a.Divisions);
        if (divisions < c.Defines.PresenceMinimumDivisions) return Loc.Get("We have too few troops to spare.");
        if (PresenceClaim(c, holder, region).Length == 0)
            return Loc.Format("We need a title to the region, our people to be {0:P0} of it, a lease there, or a collapsing state - and a border.", c.Defines.PresenceKinShare);
        return "";
    }

    /// <summary>Send troops in without a war. Crimea, 27 February 2014: the soldiers without insignia.</summary>
    public static bool SendTroops(Campaign c, Nation holder, ScenarioState region, out string result)
    {
        result = PresenceRefusal(c, holder, region);
        if (result.Length > 0) return false;
        var owner = c.NationByMapIndex(region.NationMapIndex);
        var claim = PresenceClaim(c, holder, region);
        // A lease of the region becomes the bridgehead: the base's soldiers walk out of the gate.
        var lease = Covering(c, region.MapIndex, holder.Id);
        if (lease is not null) c.LandArrangements.Remove(lease);
        var presence = new LandArrangement
        {
            Id = lease?.Id ?? c.NextLandArrangementId++, Kind = Presence, OwnerId = owner.Id, HolderId = holder.Id,
            StartDay = c.ElapsedDays, Origin = claim
        };
        presence.Regions.Add(region.MapIndex);
        c.LandArrangements.Add(presence);
        // The owner's garrison is shown the road out.
        EvictArmies(c, owner, presence);
        Condemn(c, holder, owner);
        Entered++;
        c.Chronicle.Record(c, "troops_without_war", holder.Id, owner.Id, place: region.DisplayName,
            subject: Loc.Get("presence.claim." + claim));
        Enforce(c);
        result = Loc.Format("Our troops are in {0}. {1} has not declared war - yet.", region.DisplayName, owner.DisplayName);
        return true;
    }

    /// <summary>What the world thinks of it: notoriety, the owner's grievance, the owner's friends' displeasure.</summary>
    private static void Condemn(Campaign c, Nation holder, Nation owner)
    {
        holder.Notoriety += c.Defines.PresenceNotoriety;
        OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, holder.Id, owner.Id), "troops_on_our_land");
        foreach (var other in c.Nations)
        {
            if (other.Id == holder.Id || other.Id == owner.Id || other.Movement is not null || c.StatesOf(other).Length == 0) continue;
            var tie = CampaignDiplomacy.Get(c, other.Id, owner.Id);
            if (tie is null || (tie.Stance != DiplomaticStance.Allied && tie.Opinion < 50f && !tie.GuaranteedBy(other.Id))) continue;
            // A friend of the owner's, so a witness rather than the victim: as much as it
            // concerns it, which for an ally next door is all of it (OpinionWitnesses).
            OpinionWitnesses.Witness(c, other, holder, owner, "troops_on_friends_land");
        }
    }

    private static void EvictArmies(Campaign c, Nation owner, LandArrangement arrangement)
    {
        var map = c.ProvinceMap;
        if (map is null) return;
        var inside = Provinces(c, arrangement).ToHashSet();
        foreach (var army in c.Armies)
        {
            if (army.NationId != owner.Id || !inside.Contains(army.ProvinceId)) continue;
            var refuge = NearestOwn(c, owner, army.ProvinceId, inside);
            if (refuge <= 0) continue;
            army.ProvinceId = refuge;
            army.StateMapIndex = map.ProvinceToState[refuge];
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.MoveProgress = 0;
        }
    }

    private static int NearestOwn(Campaign c, Nation owner, int start, HashSet<int> excluded)
    {
        var map = c.ProvinceMap;
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0 && seen.Count < 4000)
        {
            var province = queue.Dequeue();
            foreach (var next in map.Neighbors[province])
            {
                if (next <= 0 || next > map.ProvinceCount || map.IsWater(next) || !seen.Add(next)) continue;
                if (!excluded.Contains(next) && c.ProvinceController[next] == owner.MapIndex) return next;
                queue.Enqueue(next);
            }
        }
        return owner.CapitalProvinceId;
    }

    /// <summary>Why the holder may not hold a vote on joining it, or empty.</summary>
    public static string ReferendumRefusal(Campaign c, Nation holder, LandArrangement presence)
    {
        if (presence is null || presence.Kind != Presence || presence.HolderId != holder.Id) return Loc.Get("Our troops are not there.");
        var days = c.ElapsedDays - presence.StartDay;
        if (days < c.Defines.PresenceReferendumDays)
            return Loc.Format("Too soon: a vote can be held in {0} days.", c.Defines.PresenceReferendumDays - days);
        return "";
    }

    /// <summary>
    /// The vote, and the annexation it was held for. Crimea, 16 March 2014: 96.77 % in favour, by
    /// the count of those who held it. The result is written by the troops; how far the world
    /// believes it is written by who lives there.
    /// </summary>
    public static bool HoldReferendum(Campaign c, Nation holder, LandArrangement presence, out string result)
    {
        result = ReferendumRefusal(c, holder, presence);
        if (result.Length > 0) return false;
        var owner = c.NationById(presence.OwnerId);
        if (owner is null) return false;
        var regions = presence.Regions.Select(r => c.States.Find(s => s.MapIndex == r)).Where(r => r is not null).ToList();
        var ours = WarAims.Cultures(holder);
        var kin = regions.Sum(r => WarAims.CultureShare(r, ours) * r.Population) / MathF.Max(1f, regions.Sum(r => (float)r.Population));
        var declared = M.Clamp(kin + 0.35f + c.Random.NextFloat() * 0.08f, 0.55f, 0.97f);
        var provinces = Provinces(c, presence).ToArray();
        c.LandArrangements.Remove(presence);
        StatePartition.Annex(c, provinces, holder, asConquest: true);
        // A vote the world does not believe costs as much again.
        if (kin < c.Defines.PresenceReferendumKinShare) holder.Notoriety += c.Defines.PresenceNotoriety;
        OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, holder.Id, owner.Id), "annexed_by_referendum");
        Annexed++;
        var share = $"{declared * 100f:0.0} %";
        c.Chronicle.Record(c, "annexed_by_referendum", holder.Id, owner.Id, subject: share,
            place: string.Join(", ", regions.Select(r => r.DisplayName)));
        c.MarkMapChanged();
        result = Loc.Format("{0} votes to join us: {1} in favour, by our count.", string.Join(", ", regions.Select(r => r.DisplayName)), share);
        return true;
    }

    /// <summary>
    /// A people's republic: the region declared a state of its own, under the holder's protection -
    /// Abkhazia and South Ossetia in 2008, Donetsk and Luhansk in 2014, Transnistria in 1992. In the
    /// owner's eyes a breakaway (it seceded from it, so the owner keeps its claim and its road back);
    /// in the holder's, a subject that answers to it.
    /// </summary>
    public static bool ProclaimRepublic(Campaign c, Nation holder, LandArrangement presence, out string result)
    {
        result = ReferendumRefusal(c, holder, presence);
        if (result.Length > 0) return false;
        var owner = c.NationById(presence.OwnerId);
        var map = c.ProvinceMap;
        if (owner is null || map is null) return false;
        var regions = presence.Regions.Select(r => c.States.Find(s => s.MapIndex == r)).Where(r => r is not null).ToList();
        if (regions.Count == 0 || c.StatesOf(owner).Length <= regions.Count) { result = Loc.Get("Nothing would be left of the country it leaves."); return false; }
        // Named for the region's own largest people, not for the soldiers' - it is their republic, on paper.
        var culture = regions.SelectMany(r => r.PopulationGroups).GroupBy(g => g.Culture, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Culture: g.Key, Size: g.Sum(each => each.Size))).OrderByDescending(g => g.Size)
            .ThenBy(g => g.Culture, StringComparer.Ordinal).Select(g => g.Culture).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? owner.Tag;
        var people = regions.Sum(r => r.Population);
        var ownerPeople = Math.Max(1L, c.StatesOf(owner).Sum(s => s.Population));
        var born = SecessionEvents.FoundReleased(c, owner, regions.Select(r => new SecessionPiece
        {
            MapIndex = r.MapIndex, Name = r.Name, Population = r.Population, Cause = SecessionCause.Nationality
        }).ToList(), culture, people);
        if (born is null) { result = Loc.Get("No new country can be founded now."); return false; }
        var ids = presence.Regions.ToHashSet();
        var provinces = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => !map.IsWater(p) && ids.Contains(map.ProvinceToState[p]) && map.ProvinceToNation[p] == owner.MapIndex).ToList();
        c.LandArrangements.Remove(presence);
        born.Government = holder.Government;
        born.Ideology = holder.Ideology;
        born.StartGovernment = born.Government;
        born.StartIdeology = born.Ideology;
        born.SecededFromId = owner.Id;
        // Recorded at birth, as every other founding path does, so a save/load does not add it later.
        if (born.RevoltParentId < 0) born.RevoltParentId = owner.Id;
        born.IdentityChangedDay = c.ElapsedDays;
        RegionalResourcePartition.Transfer(owner, born, M.Clamp(people / (float)ownerPeople, 0.01f, 0.9f));
        StatePartition.Annex(c, provinces, born, asConquest: false);
        RegionalResourcePartition.RecountResidents(c, owner, born);
        CampaignDiplomacy.ForgetBorders();
        SecessionResearch.Inherit(c, owner, born, c.StatesOf(born), ownerPeople);
        c.Homelands[born.Id] = c.StatesOf(born).Select(s => s.MapIndex).ToList();
        c.LocateCapitals(preserveExisting: true);
        born.Stability = 0.5f;
        born.WarSupport = 0.6f;
        born.StabilityBias = born.Stability - StabilityCore.Target(c, born);
        SubjectSystem.Subjugate(c, holder, born, "puppet", out _);
        holder.Notoriety += c.Defines.PresenceNotoriety * 0.5f;
        OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, holder.Id, owner.Id), "annexed_by_referendum");
        Republics++;
        c.Chronicle.Record(c, "republic_proclaimed", born.Id, owner.Id, subject: holder.DisplayName,
            place: string.Join(", ", regions.Select(r => r.DisplayName)));
        c.MarkMapChanged();
        result = Loc.Format("{0} is proclaimed under our protection.", born.DisplayName);
        return true;
    }

    /// <summary>
    /// An armistice on the line (a ceasefire): every region a belligerent holds most of on the
    /// other side's ground becomes a presence - held without a war, owned by the other - with no
    /// notoriety, for nobody sent troops in today; they were already there. Returns the regions
    /// frozen, so the peace can leave their provinces where they stand.
    /// </summary>
    public static HashSet<int> FreezeLine(Campaign c, StrategicWar war)
    {
        var frozen = new HashSet<int>();
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController;
        if (map is null || controllers is null || war is null) return frozen;
        var counts = new Dictionary<int, Dictionary<ushort, int>>();
        var totals = new Dictionary<int, int>();
        foreach (var province in war.OriginalController.Keys)
            if (province > 0 && province <= map.ProvinceCount && !map.IsWater(province))
            {
                var region = map.ProvinceToState[province];
                if (!counts.ContainsKey(region))
                {
                    counts[region] = new Dictionary<ushort, int>();
                    var all = 0;
                    for (var p = 1; p <= map.ProvinceCount; p++)
                        if (!map.IsWater(p) && map.ProvinceToState[p] == region)
                        {
                            all++;
                            counts[region][controllers[p]] = counts[region].GetValueOrDefault(controllers[p]) + 1;
                        }
                    totals[region] = all;
                }
            }
        foreach (var (regionIndex, byController) in counts.OrderBy(pair => pair.Key))
        {
            var state = c.States.Find(s => s.MapIndex == regionIndex);
            var owner = state is null ? null : c.NationByMapIndex(state.NationMapIndex);
            if (owner is null || Covering(c, regionIndex) is not null) continue;
            foreach (var (controller, count) in byController.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key))
            {
                if (controller == owner.MapIndex || count * 2 <= totals[regionIndex]) continue;
                var holder = c.NationByMapIndex(controller);
                if (holder is null || holder.Movement is not null || !war.Opposes(holder.Id, owner.Id)) break;
                var presence = new LandArrangement
                {
                    Id = c.NextLandArrangementId++, Kind = Presence, OwnerId = owner.Id, HolderId = holder.Id,
                    StartDay = c.ElapsedDays, Origin = "armistice"
                };
                presence.Regions.Add(regionIndex);
                c.LandArrangements.Add(presence);
                frozen.Add(regionIndex);
                break;
            }
        }
        if (frozen.Count > 0)
            c.Chronicle.Record(c, "armistice_line", war.AttackerId, war.DefenderId, subject: frozen.Count.ToString());
        return frozen;
    }

    // ---- the computer ---------------------------------------------------------------------

    /// <summary>The owner's strength with the allies and guarantors who would stand by it.</summary>
    internal static float OwnerSide(Campaign c, Nation owner, Nation holder)
    {
        var strength = AiStatecraft.StrengthOf(c, owner);
        foreach (var other in c.Nations)
        {
            if (other.Id == owner.Id || other.Id == holder.Id || other.Movement is not null) continue;
            var tie = CampaignDiplomacy.Get(c, other.Id, owner.Id);
            if (tie is null) continue;
            if (tie.Stance == DiplomaticStance.Allied || tie.GuaranteedBy(other.Id)) strength += AiStatecraft.StrengthOf(c, other) * 0.5f;
        }
        return strength;
    }

    private static void AiMonth(Campaign c)
    {
        var defines = c.Defines;
        // Owners of ground with foreign troops on it: fight, or put up with it.
        foreach (var presence in c.LandArrangements.Where(a => a.Kind == Presence).ToList())
        {
            var owner = c.NationById(presence.OwnerId);
            var holder = c.NationById(presence.HolderId);
            if (owner is null || holder is null) continue;
            if (!owner.IsHuman && c.Random.NextFloat() < defines.AiPresenceWarChance &&
                OwnerSide(c, owner, holder) >= AiStatecraft.StrengthOf(c, holder) * defines.AiPresenceWarRatio)
            {
                if (CampaignDiplomacy.DeclareWar(c, owner.Id, holder.Id, presence.Regions[0], "reconquest") is not null)
                {
                    Fought++;
                    continue;
                }
            }
            // Holders with the troops in long enough: where the people are theirs, a vote and the
            // region joins; where they are not, a republic that answers to them.
            if (!holder.IsHuman && ReferendumRefusal(c, holder, presence).Length == 0 &&
                c.Random.NextFloat() < defines.AiPresenceReferendumChance)
            {
                var ours = WarAims.Cultures(holder);
                var kin = presence.Regions.Select(r => c.States.Find(s => s.MapIndex == r)).Where(r => r is not null)
                    .Select(r => WarAims.CultureShare(r, ours)).DefaultIfEmpty(0f).Average();
                if (kin >= defines.PresenceReferendumKinShare || !ProclaimRepublic(c, holder, presence, out _))
                    HoldReferendum(c, holder, presence, out _);
            }
        }

        // Governments with a reason and an appetite: troops in.
        foreach (var holder in c.Nations)
        {
            if (holder.IsHuman || holder.Movement is not null || c.StatesOf(holder).Length == 0) continue;
            if (c.Random.NextFloat() >= defines.AiPresenceChance) continue;
            var target = AiPresenceTarget(c, holder);
            if (target is not null) SendTroops(c, holder, target, out _);
        }

        // Great powers asking for bases.
        var strongest = c.Nations.Where(n => n.Movement is null && c.StatesOf(n).Length > 0)
            .OrderByDescending(n => AiStatecraft.StrengthOf(c, n)).ThenBy(n => n.Id).Take(8).ToList();
        foreach (var power in strongest)
        {
            if (power.IsHuman || c.Random.NextFloat() >= defines.AiLeaseRequestChance) continue;
            var region = AiBaseTarget(c, power);
            if (region is not null) RequestLease(c, power, region, defines.LeaseDefaultYears, "naval_base", out _);
        }
    }

    private static ScenarioState AiPresenceTarget(Campaign c, Nation holder)
    {
        var defines = c.Defines;
        // An unrestrained government only: Crimea and South Ossetia were not sent by cautious ones.
        if (AiStatecraft.Restraint(c, holder) > defines.AiPresenceRestraintMax) { Pass("restrained"); return null; }
        var own = AiStatecraft.StrengthOf(c, holder);
        ScenarioState best = null;
        var bestScore = 0f;
        var ours = WarAims.Cultures(holder);
        foreach (var mine in c.StatesOf(holder))
            foreach (var index in TerritorialCoherence.AdjacentRegions(c, mine.MapIndex))
            {
                var region = c.States.Find(s => s.MapIndex == index);
                if (region is null || region.NationMapIndex == holder.MapIndex) continue;
                // A motive the country's own people would follow: the region is its land by title,
                // or a clear majority there is its own. A shared language across a border is not one.
                var kin = WarAims.CultureShare(region, ours);
                var title = TerritorialClaims.TitleOf(c, holder, region);
                if (title == TerritorialTitle.None && kin < defines.AiPresenceKinMajority) continue;
                var owner = c.NationByMapIndex(region.NationMapIndex);
                if (owner is null) continue;
                var refusal = PresenceRefusal(c, holder, region);
                if (refusal.Length > 0) { Pass("refused"); PassedPairs[$"{holder.Tag}>{owner.Tag}"] = refusal; continue; }
                // Little to fear: an owner that, with its friends, is no match - or one in turmoil
                // that would have to fight a stronger neighbour on the morrow of its own crisis.
                var side = OwnerSide(c, owner, holder);
                var troubled = owner.Stability < 0.4f && side < own;
                if (side >= own * defines.AiPresenceDeterrence && !troubled)
                { Pass("deterred"); PassedPairs[$"{holder.Tag}>{owner.Tag}"] = $"ratio {side / MathF.Max(0.01f, own):0.00}"; continue; }
                Pass("candidate");
                PassedPairs[$"{holder.Tag}>{owner.Tag}"] = $"candidate {region.Name} kin {kin:0.00} title {title}";
                var score = (kin + (title == TerritorialTitle.Homeland ? 0.5f : title == TerritorialTitle.Claim ? 0.25f : 0f)) * region.Population;
                if (score <= bestScore) continue;
                bestScore = score;
                best = region;
            }
        return best;
    }

    private static ScenarioState AiBaseTarget(Campaign c, Nation power)
    {
        ScenarioState best = null;
        var bestOpinion = c.Defines.LeaseOpinionNeeded;
        foreach (var host in c.Nations)
        {
            if (host.Id == power.Id || host.Movement is not null || host.Population > 20_000_000) continue;
            var tie = CampaignDiplomacy.Get(c, power.Id, host.Id);
            if (tie is null || tie.Opinion < bestOpinion || c.LandArrangements.Any(a => a.HolderId == power.Id && a.OwnerId == host.Id)) continue;
            var port = c.StatesOf(host).Where(r => r.MapIndex != host.CapitalStateMapIndex &&
                                                   r.Buildings.GetValueOrDefault(BuildingKind.Port) > 0 &&
                                                   Covering(c, r.MapIndex) is null)
                .OrderBy(r => r.Population).ThenBy(r => r.MapIndex).FirstOrDefault();
            if (port is null) continue;
            bestOpinion = tie.Opinion;
            best = port;
        }
        return best;
    }

    // ---- history ----------------------------------------------------------------------------

    /// <summary>
    /// A standing arrangement the bookmark opens with (history/diplomacy "arrangements"):
    /// `{"kind": "lease", "owner": "KAZ", "holder": "RUS", "region": "99_x05", "until": 2050,
    /// "rent": 0.115, "purpose": "spaceport"}`.
    /// </summary>
    public static void FromHistory(Campaign c, System.Text.Json.JsonElement entry, List<string> errors)
    {
        string Text(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : "";
        var owner = c.NationByTag(Text("owner"));
        var holder = c.NationByTag(Text("holder"));
        var regionIds = new List<string>();
        if (Text("region").Length > 0) regionIds.Add(Text("region"));
        if (entry.TryGetProperty("regions", out var many) && many.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var each in many.EnumerateArray()) if (each.GetString() is { Length: > 0 } id) regionIds.Add(id);
        if (owner is null || holder is null || owner.Id == holder.Id)
        { errors?.Add($"Unknown or identical countries '{Text("owner")}' and '{Text("holder")}' in an arrangement."); return; }
        var regions = new List<int>();
        foreach (var id in regionIds)
        {
            var region = c.States.Find(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (region is null) { errors?.Add($"No region '{id}' for the arrangement between {owner.Tag} and {holder.Tag}."); continue; }
            regions.Add(region.MapIndex);
        }
        if (regions.Count == 0) return;
        var kind = Text("kind") is Presence ? Presence : Lease;
        var until = entry.TryGetProperty("until", out var year) && year.TryGetInt32(out var y) ? y : -1;
        var origin = new HistoricalDate(c.Date.Year, c.Date.Month, c.Date.Day).AddDays(-c.ElapsedDays);
        var endDay = until <= 0 ? -1 : new HistoricalDate(until, 1, 1).DayNumber - origin.DayNumber;
        if (Text("until_date").Length > 0)
        {
            if (!HistoricalDate.TryParse(Text("until_date"), out var end))
            { errors?.Add("An arrangement's until_date must be a valid calendar date."); return; }
            endDay = end.DayNumber - origin.DayNumber;
        }
        var arrangement = new LandArrangement
        {
            Id = c.NextLandArrangementId++, Kind = kind, OwnerId = owner.Id, HolderId = holder.Id,
            StartDay = c.ElapsedDays - 3650, EndDay = endDay,
            RentPerYear = entry.TryGetProperty("rent", out var rent) && rent.TryGetSingle(out var r) ? MathF.Max(0f, r) : 0f,
            Purpose = Text("purpose"), Origin = "history"
        };
        arrangement.Regions.AddRange(regions);
        c.LandArrangements.Add(arrangement);
    }

    /// <summary>The calendar date of a campaign day.</summary>
    internal static string DateOfDay(Campaign c, int day)
    {
        var start = new HistoricalDate(c.Date.Year, c.Date.Month, c.Date.Day).AddDays(-c.ElapsedDays);
        var date = start.AddDays(Math.Clamp(day, -36500, 365000));
        return new GameDate(date.Year, date.Month, date.Day).ToString();
    }

    /// <summary>The caption of the regions it covers.</summary>
    public static string RegionNames(Campaign c, LandArrangement arrangement) =>
        string.Join(", ", arrangement.Regions.Select(r => c.States.Find(s => s.MapIndex == r)?.DisplayName).Where(n => !string.IsNullOrEmpty(n)));

    /// <summary>One line for the region's tooltip and panel: who holds it and on what terms.</summary>
    public static string Describe(Campaign c, ScenarioState region)
    {
        var arrangement = Covering(c, region.MapIndex);
        if (arrangement is null) return "";
        var holder = c.NationById(arrangement.HolderId)?.DisplayName ?? "?";
        var owner = c.NationById(arrangement.OwnerId)?.DisplayName ?? "?";
        if (arrangement.Kind == Presence)
            return Loc.Format("{0} troops stand here without a war since {1}; the land is {2}'s in law.", holder,
                DateOfDay(c, arrangement.StartDay), owner);
        var purpose = arrangement.Purpose.Length > 0 ? Loc.Get("lease.purpose." + arrangement.Purpose) : Loc.Get("lease.purpose.military_base");
        var term = arrangement.LeavingOnDay >= 0
            ? Loc.Format("the lease is over and {0} must leave by {1}", holder, DateOfDay(c, arrangement.LeavingOnDay))
            : arrangement.EndDay < 0 ? Loc.Get("without a term") : Loc.Format("until {0}", DateOfDay(c, arrangement.EndDay));
        return Loc.Format("Leased by {0} from {1} ({2}), {3}; rent {4:0.000} bn a year.", holder, owner, purpose, term, arrangement.RentPerYear);
    }
}

/// <summary>Every land arrangement order, for the player and the computer alike.</summary>
public sealed class LandArrangementCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    /// <summary>"request_lease", "end_lease", "stay", "withdraw", "send_troops", "referendum", "republic", "accept_offer", "refuse_offer".</summary>
    public string Action { get; init; } = "";
    public int RegionMapIndex { get; init; } = -1;
    public int OfferId { get; init; } = -1;
    public int Years { get; init; }
    public bool Done { get; private set; }
    public string Result { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Done = false;
        Result = "";
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var issuer = campaign.NationById(IssuerId);
        if (issuer is null) return;
        var region = campaign.States.Find(s => s.MapIndex == RegionMapIndex);
        var arrangement = region is null ? null : LandArrangementSystem.Covering(campaign, region.MapIndex);
        string result;
        switch (Action)
        {
            case "request_lease":
                Done = LandArrangementSystem.RequestLease(campaign, issuer, region, Years, "", out result);
                break;
            case "end_lease":
                Done = LandArrangementSystem.EndLease(campaign, issuer, arrangement, out result);
                break;
            case "stay":
                Done = arrangement?.HolderId == issuer.Id && LandArrangementSystem.Stay(campaign, arrangement, out result);
                result = Done ? "" : Loc.Get("We have every right to be there.");
                break;
            case "withdraw":
                if (arrangement is null || arrangement.HolderId != issuer.Id) { result = Loc.Get("Our troops are not there."); break; }
                LandArrangementSystem.Withdraw(campaign, arrangement);
                Done = true;
                result = "";
                break;
            case "send_troops":
                Done = LandArrangementSystem.SendTroops(campaign, issuer, region, out result);
                break;
            case "referendum":
                Done = LandArrangementSystem.HoldReferendum(campaign, issuer, arrangement, out result);
                break;
            case "republic":
                Done = LandArrangementSystem.ProclaimRepublic(campaign, issuer, arrangement, out result);
                break;
            case "held_referendum":
                Done = HeldRegions.HoldReferendum(campaign, issuer, region, out result);
                break;
            case "accept_offer":
            case "refuse_offer":
                Done = LandArrangementSystem.Answer(campaign, issuer, OfferId, Action == "accept_offer", out result);
                break;
            default:
                result = "";
                break;
        }
        Result = result;
        if (Done) campaign.MarkStrategicChange();
    }
}
