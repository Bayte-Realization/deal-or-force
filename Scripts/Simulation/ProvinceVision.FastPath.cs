using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class ProvinceVision
{
    internal bool FullReachValid;
    internal ulong LastActorSig;
    internal int LastManualEpoch = -1;
    internal int LastIntelCount = -1;
    internal int LastIntelHeldRevision = -1;
    internal bool LastIntelEnabled;
}

public static partial class CampaignIntel
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MixVisionHash(ref ulong hash, uint value)
    {
        hash ^= value;
        hash *= 1099511628211ul;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LookupArmyProvince(Cache cache, int armyId)
    {
        if ((uint)armyId < (uint)cache.ArmyProvinceByIdDense.Length)
            return cache.ArmyProvinceStampDense[armyId] == cache.ArmyProvinceStamp
                ? cache.ArmyProvinceByIdDense[armyId] : -1;
        return cache.ArmyProvinceOverflow.TryGetValue(armyId, out var prov) ? prov : -1;
    }

    private static bool HasActiveAirReconMission(Campaign c, int viewerId)
    {
        if (!c.SupportByNation.TryGetValue(viewerId, out var formations) || formations.Count == 0)
            return false;
        for (var i = 0; i < formations.Count; i++)
        {
            var f = formations[i];
            if (f.Mission == AirReconnaissance.Mission && f.BaseProvinceId > 0)
                return true;
        }
        return false;
    }

    private static ulong ComputeViewerVisionSignature(
        Campaign c,
        Cache cache,
        Nation nation,
        int viewerId,
        bool hasBattles,
        int borderDepth,
        int armyDepth,
        out int intelCount,
        out bool intelEnabled)
    {
        ulong sig = 14695981039346656037ul;
        MixVisionHash(ref sig, (uint)borderDepth);
        MixVisionHash(ref sig, (uint)armyDepth);

        if ((uint)viewerId < (uint)cache.ArmiesByNationDense.Length &&
            cache.ArmiesByNationDense[viewerId] is { Count: > 0 } myArmies)
        {
            MixVisionHash(ref sig, (uint)myArmies.Count);
            for (var i = 0; i < myArmies.Count; i++)
                MixVisionHash(ref sig, (uint)myArmies[i]);
        }
        else
        {
            MixVisionHash(ref sig, 0u);
        }

        MixVisionHash(ref sig, 0x9E3779B9u);
        if (hasBattles &&
            (uint)viewerId < (uint)cache.BattlesByNationDense.Length &&
            cache.BattlesByNationDense[viewerId] is { Count: > 0 } myBattles)
        {
            MixVisionHash(ref sig, (uint)myBattles.Count);
            var battles = c.Battles;
            for (var i = 0; i < myBattles.Count; i++)
            {
                var bi = myBattles[i];
                if ((uint)bi >= (uint)battles.Count) continue;
                var b = battles[bi];
                MixVisionHash(ref sig, (uint)b.ProvinceId);
                MixVisionHash(ref sig, (uint)b.Attackers.Count);
                MixVisionHash(ref sig, (uint)b.Defenders.Count);
                foreach (var aid in b.Attackers)
                {
                    MixVisionHash(ref sig, (uint)aid);
                    MixVisionHash(ref sig, (uint)LookupArmyProvince(cache, aid));
                }
                foreach (var did in b.Defenders)
                {
                    MixVisionHash(ref sig, (uint)did);
                    MixVisionHash(ref sig, (uint)LookupArmyProvince(cache, did));
                }
            }
        }
        else
        {
            MixVisionHash(ref sig, 0u);
        }

        MixVisionHash(ref sig, 0x85EBCA6Bu);
        if (cache.InvasionBeachesByNation.TryGetValue(viewerId, out var beaches) && beaches.Count > 0)
        {
            MixVisionHash(ref sig, (uint)beaches.Count);
            for (var i = 0; i < beaches.Count; i++)
                MixVisionHash(ref sig, (uint)beaches[i]);
        }
        else
        {
            MixVisionHash(ref sig, 0u);
        }

        intelCount = nation.IntelNetworks.Count;
        intelEnabled = intelCount > 0 && c.FeatureEnabled("intelligence");
        if (intelEnabled)
        {
            MixVisionHash(ref sig, 0xC2B2AE35u);
            foreach (var (targetId, strength) in nation.IntelNetworks)
            {
                if (strength <= 0.01f) continue;
                MixVisionHash(ref sig, (uint)targetId);
                MixVisionHash(ref sig, (uint)BitConverter.SingleToInt32Bits(strength));
            }
        }
        return sig;
    }

    private static bool TryReuseViewerVision(
        Campaign c,
        Cache cache,
        ProvinceVision view,
        Nation nation,
        int viewerId,
        bool territoryMatched,
        bool hasBattles,
        int borderDepth,
        int armyDepth,
        out ulong actorSig,
        out int intelCount,
        out bool intelEnabled,
        out bool hasAirRecon)
    {
        actorSig = ComputeViewerVisionSignature(c, cache, nation, viewerId, hasBattles,
            borderDepth, armyDepth, out intelCount, out intelEnabled);
        hasAirRecon = HasActiveAirReconMission(c, viewerId);
        if (hasAirRecon || !territoryMatched || !view.FullReachValid)
            return false;
        if (view.LastActorSig != actorSig || view.LastManualEpoch != cache.ManualEpoch)
            return false;
        if (view.LastIntelCount != intelCount || view.LastIntelEnabled != intelEnabled)
            return false;
        if (intelEnabled && view.LastIntelHeldRevision != cache.HeldRevision)
            return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StampViewerVision(
        ProvinceVision view,
        Cache cache,
        ulong actorSig,
        int intelCount,
        bool intelEnabled,
        bool hasAirRecon)
    {
        // Never mark FullReachValid when AirReconnaissance lifted fog over enemy ground;
        // otherwise stopping/losing the recon flight on the next tick would reuse stale vision!
        view.FullReachValid = !hasAirRecon;
        view.LastActorSig = actorSig;
        view.LastManualEpoch = cache.ManualEpoch;
        view.LastIntelCount = intelCount;
        view.LastIntelEnabled = intelEnabled;
        view.LastIntelHeldRevision = cache.HeldRevision;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendSeed(ProvinceVision view, int province, byte depth)
    {
        if (province <= 0) return;
        var idx = view.SeedCount;
        if (idx >= view.SeedProvinces.Length)
        {
            var next = Math.Max(32, view.SeedProvinces.Length * 2);
            Array.Resize(ref view.SeedProvinces, next);
            Array.Resize(ref view.SeedDepths, next);
        }
        view.SeedProvinces[idx] = province;
        view.SeedDepths[idx] = depth;
        view.SeedCount = idx + 1;
    }

    private static void RebuildVisionSeedsAndFlood(
        Campaign c,
        Cache cache,
        ProvinceVision view,
        ProvinceTopology map,
        Kernels.NativeProvinceGraph nativeGraph,
        int viewerId,
        bool hasBattles,
        int armyDepth)
    {
        view.SeedCount = 0;
        var useNativeFlood = nativeGraph is not null &&
                             Kernels.NativeKernelsLoader.EnableWartimeGraphDefault &&
                             Kernels.NativeKernelsLoader.HasCapability(Kernels.NativeKernelsLoader.CapWartimeGraph);
        if ((uint)viewerId < (uint)cache.ArmiesByNationDense.Length &&
            cache.ArmiesByNationDense[viewerId] is { Count: > 0 } myArmies)
        {
            if (useNativeFlood)
                for (var i = 0; i < myArmies.Count; i++) AppendSeed(view, myArmies[i], (byte)armyDepth);
            else
                for (var i = 0; i < myArmies.Count; i++) RevealProvince(view.Reach, view.Frontier, myArmies[i], armyDepth);
        }
        if (hasBattles &&
            (uint)viewerId < (uint)cache.BattlesByNationDense.Length &&
            cache.BattlesByNationDense[viewerId] is { Count: > 0 } myBattles)
        {
            for (var i = 0; i < myBattles.Count; i++)
            {
                var bi = myBattles[i];
                if ((uint)bi >= (uint)c.Battles.Count) continue;
                var battle = c.Battles[bi];
                if (useNativeFlood) AppendSeed(view, battle.ProvinceId, (byte)armyDepth);
                else RevealProvince(view.Reach, view.Frontier, battle.ProvinceId, armyDepth);
                view.CombatContacts.UnionWith(battle.Attackers);
                view.CombatContacts.UnionWith(battle.Defenders);
            }
        }
        if (view.CombatContacts.Count > 0)
        {
            var denseProv = cache.ArmyProvinceByIdDense;
            var denseStamp = cache.ArmyProvinceStampDense;
            var curStamp = cache.ArmyProvinceStamp;
            foreach (var contactId in view.CombatContacts)
            {
                if ((uint)contactId < (uint)denseProv.Length)
                {
                    if (denseStamp[contactId] == curStamp)
                    {
                        if (useNativeFlood) AppendSeed(view, denseProv[contactId], 0);
                        else RevealProvince(view.Reach, view.Frontier, denseProv[contactId], 0);
                    }
                }
                else if (cache.ArmyProvinceOverflow.TryGetValue(contactId, out var prov))
                {
                    if (useNativeFlood) AppendSeed(view, prov, 0);
                    else RevealProvince(view.Reach, view.Frontier, prov, 0);
                }
            }
        }
        if (cache.InvasionBeachesByNation.TryGetValue(viewerId, out var beaches))
        {
            if (useNativeFlood)
                for (var i = 0; i < beaches.Count; i++) AppendSeed(view, beaches[i], (byte)armyDepth);
            else
                for (var i = 0; i < beaches.Count; i++) RevealProvince(view.Reach, view.Frontier, beaches[i], armyDepth);
        }
        if (useNativeFlood)
        {
            if (view.SeedCount > 0 &&
                !nativeGraph!.TryFloodVision(view.Reach,
                    view.SeedProvinces.AsSpan(0, view.SeedCount),
                    view.SeedDepths.AsSpan(0, view.SeedCount)))
            {
                for (var i = 0; i < view.SeedCount; i++)
                    RevealProvince(view.Reach, view.Frontier, view.SeedProvinces[i], view.SeedDepths[i]);
                FloodVision(view.Reach, view.Frontier, map);
            }
        }
        else
        {
            FloodVision(view.Reach, view.Frontier, map);
        }
    }
}
