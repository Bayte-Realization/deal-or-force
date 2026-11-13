using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The least time, in days, between two restoration proclamations by one government.</summary>
    public int RestorationCooldownDays { get; set; } = 180;
    /// <summary>What the proclamation itself costs, in billions - offices, documents, the move home.</summary>
    public float RestorationCost { get; set; } = 0.02f;
    /// <summary>Legacy opinion setting retained for mod/save compatibility; friendship is not territorial consent.</summary>
    public float RestorationConsentOpinion { get; set; } = 35f;
}

/// <summary>
/// The ground a country lost, brought back under its own flag.
///
/// A country that loses the last of its ground either goes into exile or is gone
/// (<see cref="MovementFall"/>). An exile keeps everything that made it that country - its tag,
/// its name, its colour, its government, the peoples it is for - and holds no ground anywhere,
/// so there is no second country of that name for the restoration to stand beside. What it
/// lacks is the ground. This is the way back: it is restored to its own homeland - the ground
/// the world first saw it holding, and the titles it kept there
/// (<see cref="TerritorialClaims.TitleOf"/>) - and stands again as the country it was.
///
/// Two ways home, and both are a real transfer rather than a renaming:
/// a military return, when the government already stands on the region's ground, and a
/// negotiated return, explicitly authorised by the country holding it. Friendship alone never
/// transfers territory. Either way the region changes owner through the same <see cref="StatePartition"/> and
/// resource accounting every other border change uses, so its people, its output and its debts
/// are counted once and under one flag - never duplicated, never conjured.
///
/// A restoration is not a claim and not a conquest: it needs the ground, and it needs the
/// country to be genuinely gone, so a state that already stands is not "restored" a second
/// time - it takes the rest of its homeland back the way any country does.
/// </summary>
public static class Restoration
{
    /// <summary>Whether the region is the country's own remembered or authored homeland.</summary>
    public static bool IsHomeland(Campaign campaign, Nation nation, ScenarioState region) =>
        campaign is not null && nation is not null && region is not null && !region.Unclaimed &&
        TerritorialClaims.TitleOf(campaign, nation, region) == TerritorialTitle.Homeland;

    /// <summary>Whether the exiled government controls all of the region's land, not one foothold.</summary>
    public static bool Controls(Campaign campaign, Nation nation, ScenarioState region)
    {
        var topology = campaign?.ProvinceMap;
        var control = campaign?.ProvinceController;
        if (topology is null || control is null || nation is null || region is null) return false;
        var any = false;
        // The provinces of this one region. The old loop read every province on the map
        // to find them, once per homeland piece, on every way home.
        var provinces = topology.ProvincesOfState(region.MapIndex);
        for (var i = 0; i < provinces.Length; i++)
        {
            var province = provinces[i];
            if (topology.WaterByProvince[province]) continue;
            any = true;
            if (province >= control.Length || control[province] != (ushort)nation.MapIndex) return false;
        }
        return any;
    }

    /// <summary>The country that governs the region now, if a real one does.</summary>
    public static Nation Holder(Campaign campaign, ScenarioState region) =>
        region is null ? null : campaign.NationByMapIndex(region.NationMapIndex);

    public static string ConsentKey(int holderId) => "restoration_consent:" + holderId;

    /// <summary>Only the holder may authorise a hand-back. The saved permission is single-use.</summary>
    public static bool Consents(Campaign campaign, Nation nation, Nation holder) =>
        campaign is not null && nation is not null && holder is not null && holder.Id != nation.Id &&
        nation.Movement is { Exile: true, Landless: true, Dissolved: false } state &&
        holder.Movement is null && !holder.IsSubject && !CampaignDiplomacy.AtWar(campaign, nation.Id, holder.Id) &&
        !state.DesignatedBy.Contains(holder.Id) &&
        state.ReadyOnDay.TryGetValue(ConsentKey(holder.Id), out var until) && until > campaign.ElapsedDays;

    public static bool HoldsHomeland(Campaign campaign, Nation nation, Nation holder) =>
        campaign is not null && nation?.Movement is { Exile: true, Landless: true, Dissolved: false } &&
        holder is not null && campaign.StatesOf(holder).Any(region => IsHomeland(campaign, nation, region));

    /// <summary>Why the country cannot be restored to this region now; empty when it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, ScenarioState region) =>
        CheckRefusal(campaign, nation, region, explain: true);

    private static string CheckRefusal(Campaign campaign, Nation nation, ScenarioState region, bool explain)
    {
        var state = nation?.Movement;
        if (state is null || state.Dissolved) return explain ? Loc.Get("Only a country that still has a government can be restored.") : "-";
        if (!state.Exile) return explain ? Loc.Get("This is not a government in exile.") : "-";
        if (region is null) return explain ? Loc.Get("Choose a region first.") : "-";
        if (!IsHomeland(campaign, nation, region))
            return explain ? Loc.Get("A country can only be restored to its own homeland.") : "-";
        if (region.NationMapIndex == nation.MapIndex) return explain ? Loc.Get("That ground is already ours.") : "-";
        // No active duplicate: the country must not already stand anywhere under its own name.
        if (campaign.HasOwnedLand(nation))
            return explain ? Loc.Get("The country already stands on its own ground.") : "-";
        if (AnotherLandedName(campaign, nation))
            return explain ? Loc.Get("Another country already bears this name.") : "-";
        if (!float.IsFinite(region.Stability) || !float.IsFinite(region.StandardOfLiving) ||
            !float.IsFinite(region.Literacy) || !float.IsFinite(nation.Gdp))
            return explain ? Loc.Get("Invalid simulation values prevent this action. Pause and inspect the campaign.") : "-";
        var holder = Holder(campaign, region);
        if (holder is not null && !float.IsFinite(holder.Gdp))
            return explain ? Loc.Get("Invalid simulation values prevent this action. Pause and inspect the campaign.") : "-";
        var controls = Controls(campaign, nation, region);
        if (!controls && !Consents(campaign, nation, holder))
            return explain ? Loc.Get("The holder will not hand it back, and we do not hold it.") : "-";
        // Ground taken back from a rising is not handed straight to the government that lost
        // it. This is the half of the reconquest clock a rising cannot check (RevoltRecovery
        // stops uprisings and secessions; a return is neither), and without it an exile was
        // walking home three days after losing the war - by permission of a holder that never
        // gave one, or by a negotiation its own army had just lost - and came back holding
        // three regions under a peace treaty that then forbade the state it left from
        // answering. A country that has just been thrown out of a region does not get it back
        // until the region has stopped being a battlefield.
        var recovery = controls
            ? 0
            : RevoltRecovery.DaysLeft(campaign, holder, region);
        if (recovery > 0)
            return explain ? Loc.Format("This ground was retaken too recently: {0} days must pass before it can change hands again.", recovery) : "-";
        return "";
    }

    public static bool CanRestore(Campaign campaign, Nation nation, ScenarioState region) =>
        CheckRefusal(campaign, nation, region, explain: false).Length == 0;

    [ThreadStatic] static HashSet<int> _rememberedHomeland;

    /// <summary>The largest piece of its own homeland the country can be restored to now, or nothing.</summary>
    public static ScenarioState BestRegion(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return null;
        var state = nation.Movement;
        if (state is null || state.Dissolved || !state.Exile || campaign.HasOwnedLand(nation)) return null;
        if (AnotherLandedName(campaign, nation)) return null;
        ScenarioState best = null;
        // Only a homeland piece can be restored to. The title is a core tag or a remembered
        // region, including a partition of one. The index is this campaign's and is rebuilt
        // when the region list changes or a region is renamed. It does not walk every country.
        var remembered = _rememberedHomeland ??= new HashSet<int>();
        remembered.Clear();
        if (campaign.Homelands.TryGetValue(nation.Id, out var homes) && homes is not null)
            for (var i = 0; i < homes.Count; i++) remembered.Add(homes[i]);
        var renounced = nation.RenouncedTerritoryOrigins;
        var scanAll = false;
        foreach (var id in remembered)
            if (id <= 0) { scanAll = true; break; }
        void Consider(ScenarioState region)
        {
            if (region is null || region.Unclaimed) return;
            var origin = region.ParentMapIndex > 0 ? region.ParentMapIndex : region.MapIndex;
            if (renounced is not null && renounced.Contains(origin)) return;
            if (!remembered.Contains(region.MapIndex) && !remembered.Contains(origin) &&
                !MovementDefinition.Listed(region.CoreTags, nation.Tag)) return;
            if (!CanRestore(campaign, nation, region)) return;
            if (best is null || region.Population > best.Population ||
                region.Population == best.Population && region.MapIndex < best.MapIndex) best = region;
        }
        if (scanAll)
        {
            var states = campaign.States;
            for (var si = 0; si < states.Count; si++) Consider(states[si]);
            return best;
        }
        var index = OrderIndexOf(campaign);
        foreach (var id in remembered)
        {
            Consider(campaign.StateByMapIndex(id));
            if (index.Children.TryGetValue(id, out var children))
                for (var i = 0; i < children.Count; i++) Consider(children[i]);
        }
        if (nation.Tag is { Length: > 0 } tag && index.ByCore.TryGetValue(tag, out var tagged))
            for (var i = 0; i < tagged.Count; i++) Consider(tagged[i]);
        return best;
    }

    internal static bool SharesOrderIndex(Campaign left, Campaign right)
    {
        if (left is null || right is null || ReferenceEquals(left, right)) return false;
        OrderIndexes.TryGetValue(left, out var a);
        OrderIndexes.TryGetValue(right, out var b);
        return a is not null && ReferenceEquals(a, b);
    }

    sealed class OrderIndex
    {
        public int Generation = int.MinValue;
        public int Count = -1;
        public List<ScenarioState> List;
        public readonly Dictionary<int, List<ScenarioState>> Children = new();
        public readonly Dictionary<string, List<ScenarioState>> ByCore = new(StringComparer.OrdinalIgnoreCase);
        public readonly Stack<List<ScenarioState>> Pool = new();
    }

    static readonly ConditionalWeakTable<Campaign, OrderIndex> OrderIndexes = new();

    static OrderIndex OrderIndexOf(Campaign campaign)
    {
        var index = OrderIndexes.GetValue(campaign, static _ => new OrderIndex());
        var states = campaign.States;
        var generation = Movements.RegionListGeneration(campaign);
        if (index.Generation == generation && index.Count == states.Count && ReferenceEquals(index.List, states))
            return index;
        foreach (var list in index.Children.Values) { list.Clear(); index.Pool.Push(list); }
        foreach (var list in index.ByCore.Values) { list.Clear(); index.Pool.Push(list); }
        index.Children.Clear();
        index.ByCore.Clear();
        List<ScenarioState> Take()
        {
            if (index.Pool.Count > 0) return index.Pool.Pop();
            return new List<ScenarioState>();
        }
        for (var i = 0; i < states.Count; i++)
        {
            var region = states[i];
            if (region is null) continue;
            if (region.ParentMapIndex > 0)
            {
                if (!index.Children.TryGetValue(region.ParentMapIndex, out var children))
                    index.Children[region.ParentMapIndex] = children = Take();
                children.Add(region);
            }
            var tags = region.CoreTags;
            if (tags is null) continue;
            for (var t = 0; t < tags.Count; t++)
            {
                var core = tags[t];
                if (string.IsNullOrEmpty(core)) continue;
                if (!index.ByCore.TryGetValue(core, out var tagged))
                    index.ByCore[core] = tagged = Take();
                tagged.Add(region);
            }
        }
        index.List = states;
        index.Count = states.Count;
        index.Generation = Movements.RegionListGeneration(campaign);
        return index;
    }

    sealed class NameMemory
    {
        public int Revision = int.MinValue;
        public int Count = -1;
        public readonly Dictionary<string, int> First = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Extra = new(StringComparer.OrdinalIgnoreCase);
    }

    static readonly ConditionalWeakTable<Campaign, NameMemory> NameMemories = new();

    /// <summary>Another country with land already bears this tag. With a map, the answer is cached until ownership or the country list changes.</summary>
    static bool AnotherLandedName(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return false;
        if (campaign.ProvinceMap is null)
        {
            var open = campaign.Nations;
            for (var i = 0; i < open.Count; i++)
            {
                var other = open[i];
                if (other.Id != nation.Id &&
                    string.Equals(other.Tag, nation.Tag, StringComparison.OrdinalIgnoreCase) && campaign.HasOwnedLand(other))
                    return true;
            }
            return false;
        }
        var memory = NameMemories.GetValue(campaign, static _ => new NameMemory());
        if (memory.Revision != campaign.OwnershipRevision || memory.Count != campaign.Nations.Count)
        {
            memory.Revision = campaign.OwnershipRevision;
            memory.Count = campaign.Nations.Count;
            memory.First.Clear();
            memory.Extra.Clear();
            var nations = campaign.Nations;
            for (var i = 0; i < nations.Count; i++)
            {
                var other = nations[i];
                if (!campaign.HasOwnedLand(other)) continue;
                var tag = other.Tag ?? "";
                if (!memory.First.TryAdd(tag, other.Id)) memory.Extra.Add(tag);
            }
        }
        var key = nation.Tag ?? "";
        if (!memory.First.TryGetValue(key, out var first)) return false;
        return first != nation.Id || memory.Extra.Contains(key);
    }

    /// <summary>Bring the country home: the region becomes its own again, with its people and output.</summary>
    public static bool Return(Campaign campaign, Nation nation, ScenarioState region)
    {
        if (Refusal(campaign, nation, region).Length != 0) return false;
        var holder = Holder(campaign, region);
        // A return the exile does not already stand on is one the holder agreed to.
        var negotiated = !Controls(campaign, nation, region);
        // A return is not a doorstep: the whole homeland the same holder still governs comes
        // home with the first region, through the ordinary accounting, in one act.
        if (MovementRules.TakeZone(campaign, nation, region, holder, restoration: true) == 0) return false;
        if (holder is not null && nation.Movement is not null) nation.Movement.ReadyOnDay.Remove(ConsentKey(holder.Id));
        campaign.Chronicle.Record(campaign, "restoration", nation.Id, holder?.Id ?? -1, place: region.DisplayName);
        if (negotiated && holder is not null)
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, holder.Id, nation.Id), "restored_us");
        RestoreExiledNation(campaign, nation);
        return true;
    }

    /// <summary>
    /// When an exiled government returns home and holds land of its own again, it ceases
    /// to be an exile abroad and resumes full sovereign governance as a regular nation.
    /// </summary>
    public static void RestoreExiledNation(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation?.Movement is null) return;
        var state = nation.Movement;
        if (!state.Landless && !campaign.Fallen.Contains(nation.Id) && state.DesignatedBy.Count == 0) return;
        state.Landless = false;
        state.DesignatedBy.Clear();
        state.RecognisedBy.Clear();
        campaign.Fallen.Remove(nation.Id);
        var held = campaign.StatesOf(nation);
        var seat = held.OrderByDescending(s => s.Population).FirstOrDefault();
        if (seat is not null)
        {
            CapitalCore.Establish(campaign, nation, seat);
        }
        campaign.LocateCapitals(preserveExisting: true);
        campaign.MovementRevision++;
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation", $"NATION_RESTORED nation={nation.Tag} name={nation.Name} regions={held.Length}");
    }
}
