using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Pure read-only telemetry queries for Region Cards, Compact Province Info, and Country Dossiers.
/// Never mutates <see cref="Campaign"/>, <see cref="Nation"/>, <see cref="ScenarioState"/>, or any
/// simulation cache that could alter <see cref="CampaignFingerprint"/>.
/// </summary>
public static class CardTelemetryAudit
{
    public sealed record RegionMovementEntry(
        int NationId,
        string Tag,
        string Name,
        string ColourHex,
        float ResidentSupport,
        float PeopleSupport,
        bool IsHeadquarters,
        bool IsHomeland,
        bool ParallelLevy,
        float UprisingChance,
        bool HighRisingRisk,
        float Heat,
        float Cadres,
        float InfiltratedFighters,
        bool CanInspectDetails);

    public sealed record RegionClaimantEntry(
        int NationId,
        string Tag,
        string Name,
        bool IsHomeland);

    public sealed record RegionCrisisEntry(
        string NameKey,
        float Severity,
        bool IsDisease);

    public sealed record RegionAuditSnapshot(
        IReadOnlyList<RegionMovementEntry> Movements,
        TerritorialTitle OwnerTitle,
        IReadOnlyList<RegionClaimantEntry> ForeignClaimants,
        bool IsAutonomous,
        float AutonomyWeeklyCost,
        bool AutonomyRecentlyRevoked,
        bool IsUnentitled,
        float ConquestAcceptanceShare,
        float ConquestWeeklyGain,
        string ConquestStallReason,
        float SecessionPressure,
        int ParallelAdminNationId,
        string ParallelAdminName,
        float ParallelAdminCoverage,
        bool ParallelAdminExposed,
        int HeldByNationId,
        string HeldByName,
        bool HeldAdministered,
        float WarDamageShare,
        float OutputPenaltyShare,
        int RebuildingDaysLeft,
        long DisplacedPeople,
        int OccupiedProvinces,
        int TotalProvinces,
        float ProvinceOccupationShare,
        float ProvinceWeeklyResistanceSlip,
        float BrainDrainPressure,
        float ForeignMinorityShare,
        bool CohortsReviewed,
        float GovernmentApproval,
        float LawApproval,
        float PoliticalDivergence,
        float CivicAwareness,
        int InfrastructureLevel,
        int PortLevel,
        int RailLevel,
        int BarracksLevel,
        float AverageGarrisonEntrenchment,
        IReadOnlyList<RegionCrisisEntry> ActiveCrises);

    public sealed record CountryMovementThreat(
        int NationId,
        string Name,
        string TopRegionName,
        float TopResidentSupport,
        bool HasHeadquartersInside,
        float MaxUprisingChance);

    public sealed record CountryRankBreakdown(
        int OverallRank,
        double OverallScore,
        int TotalRanked,
        int EconomyRank,
        int PopulationRank,
        int FieldPowerRank,
        int TechnologyRank,
        int PrestigeRank);

    public sealed record CountryAuditSnapshot(
        int ActiveMovementsCount,
        int MovementsWithHqInsideCount,
        IReadOnlyList<CountryMovementThreat> TopMovements,
        int RestiveRegionsCount,
        float MaxSecessionPressure,
        string MostRestiveRegionName,
        int AutonomousRegionsCount,
        float ImperialOverstretch,
        float CoupPressure,
        float CoupThreshold,
        float BrainDrainRate,
        long WeeklyNetMigration,
        long CumulativeNetMigration,
        string BrainDrainDestinationName,
        string BrainGainSourceName,
        int OpenWarCount,
        int StalledWarCount,
        float WarFatigue,
        int DamagedRegionsCount,
        float AverageWarDamage,
        bool NuclearEraActive,
        int DeterrentTier,
        int OwnNuclearPosture,
        NuclearDoctrine NuclearDoctrine,
        int ActiveArmsControlPacts,
        int GlobalDefconLevel,
        CountryRankBreakdown RankBreakdown,
        bool CohortsReviewed,
        float GovernmentApproval,
        float LawApproval,
        float PoliticalDivergence,
        bool BlocsReviewed,
        PoliticalBloc DominantBloc,
        float DominantBlocShare,
        float Polarization,
        IReadOnlyList<RegionCrisisEntry> ActiveCrises);

    /// <summary>Collects active movements with meaningful presence or HQ inside <paramref name="state"/>.</summary>
    public static IReadOnlyList<RegionMovementEntry> ReadRegionMovements(
        Campaign world, ScenarioState state, bool canInspectAll = false)
    {
        if (world is null || state is null || state.Unclaimed || !world.FeatureEnabled("movements"))
            return Array.Empty<RegionMovementEntry>();

        var result = new List<RegionMovementEntry>(4);
        var playerId = world.Player?.Id ?? -1;
        var uprisingThreshold = MathF.Max(0.05f, world.Defines.MovementUprisingSupport);
        var parallelThreshold = MathF.Max(0.10f, world.Defines.MovementParallelSupport);

        foreach (var nation in Movements.Active(world))
        {
            var mv = nation.Movement;
            if (mv is null || mv.Dissolved) continue;
            var def = Movements.DefinitionOf(nation);
            if (def is null) continue;

            var isHq = mv.HeadquartersStateMapIndex == state.MapIndex;
            var peopleSupport = M.Clamp01(mv.Support.GetValueOrDefault(state.MapIndex));
            var residentSupport = MovementReach.ResidentSupport(world, nation, state);
            var infiltrated = mv.Infiltrated.GetValueOrDefault(state.MapIndex);
            var canInspect = canInspectAll || nation.Id == playerId || CampaignIntel.PlayerCanInspect(world, nation.Id);

            if (!isHq && residentSupport < 0.01f && peopleSupport < 0.08f && (!canInspect || infiltrated < 50f))
                continue;

            var isHomeland = MovementReach.IsHomeland(world, def, state);
            var uprisingChance = state.NationMapIndex != nation.MapIndex
                ? MovementRules.UprisingChance(world, nation, state)
                : 0f;
            var parallelLevy = state.NationMapIndex != nation.MapIndex && peopleSupport >= parallelThreshold;
            var highRisk = uprisingChance >= 0.22f || (uprisingChance > 0f && peopleSupport >= uprisingThreshold);

            result.Add(new RegionMovementEntry(
                NationId: nation.Id,
                Tag: nation.Tag ?? "",
                Name: nation.DisplayName ?? nation.Name ?? "",
                ColourHex: string.IsNullOrWhiteSpace(nation.Color) ? def.Colour : nation.Color,
                ResidentSupport: residentSupport,
                PeopleSupport: peopleSupport,
                IsHeadquarters: isHq,
                IsHomeland: isHomeland,
                ParallelLevy: parallelLevy,
                UprisingChance: uprisingChance,
                HighRisingRisk: highRisk,
                Heat: M.Clamp01(mv.Heat),
                Cadres: MathF.Max(0f, mv.Cadres),
                InfiltratedFighters: MathF.Max(0f, infiltrated),
                CanInspectDetails: canInspect));
        }

        result.Sort(static (a, b) =>
        {
            var hq = b.IsHeadquarters.CompareTo(a.IsHeadquarters);
            if (hq != 0) return hq;
            var sup = b.ResidentSupport.CompareTo(a.ResidentSupport);
            if (sup != 0) return sup;
            var ps = b.PeopleSupport.CompareTo(a.PeopleSupport);
            return ps != 0 ? ps : a.NationId.CompareTo(b.NationId);
        });
        return result;
    }

    /// <summary>Builds a complete read-only telemetry audit snapshot for a region.</summary>
    public static RegionAuditSnapshot ReadRegionAudit(
        Campaign world, Nation owner, ScenarioState state, int provinceId, bool canInspectAll = false)
    {
        if (world is null || state is null)
            return new RegionAuditSnapshot(
                Array.Empty<RegionMovementEntry>(), TerritorialTitle.None, Array.Empty<RegionClaimantEntry>(),
                false, 0f, false, false, 0f, 0f, "", 0f,
                -1, "", 0f, false, -1, "", false,
                0f, 0f, 0, 0L, 0, 0, 0f, 0f,
                0f, 0f, false, 0f, 0f, 0f, 0f,
                0, 0, 0, 0, 0f, Array.Empty<RegionCrisisEntry>());

        var movements = ReadRegionMovements(world, state, canInspectAll);
        var ownerTitle = owner is not null ? TerritorialClaims.TitleOf(world, owner, state) : TerritorialTitle.None;
        var claimants = ReadForeignClaimants(world, owner, state);

        var isAuto = RegionalAutonomy.Autonomous(state);
        var autoCost = isAuto && owner is not null ? RegionalAutonomy.WeeklyPrice(world, owner, state) : 0f;
        var autoRevoked = !isAuto && state.AutonomyRevokedDay >= 0 &&
                          world.ElapsedDays - state.AutonomyRevokedDay < world.Defines.AutonomyRegrantDays;

        var unentitled = owner is not null && ConquestAcceptance.Unentitled(world, owner, state);
        var acceptance = unentitled ? ConquestAcceptance.Of(state, owner) : 0f;
        var stall = "";
        var weeklyGain = unentitled ? ConquestAcceptance.WeeklyGain(world, owner, state, out stall) : 0f;

        var playerId = world.Player?.Id ?? -1;
        var paId = -1;
        var paName = "";
        var paCov = 0f;
        var paExp = false;
        if (state.AdministeredById >= 0 && (state.AdministrationExposed || canInspectAll || state.AdministeredById == playerId))
        {
            if (world.NationById(state.AdministeredById) is { } paNation)
            {
                paId = paNation.Id;
                paName = paNation.DisplayName;
                paCov = ParallelAdministration.Coverage(state);
                paExp = state.AdministrationExposed;
            }
        }

        var heldById = state.HeldById;
        var heldByName = heldById >= 0 ? (world.NationById(heldById)?.DisplayName ?? "") : "";
        var heldAdmin = heldById >= 0 && HeldRegions.Administered(world, state);

        var damage = M.Clamp01(state.WarDamage);
        var outputPenalty = MathF.Max(0f, 1f - WarDamage.OutputFactor(world, state));
        var rebuildLeft = state.RebuildingUntilDay > world.ElapsedDays ? state.RebuildingUntilDay - world.ElapsedDays : 0;
        long displaced = 0;
        foreach (var kv in state.Displaced)
            if (kv.Value > 0) displaced += kv.Value;

        var (occProvs, totProvs, provOcc, provSlip) = ReadOccupationMetrics(world, owner, state, provinceId);
        var foreignShare = owner is not null ? EconomySystem.ForeignShare(state, owner.Tag) : 0f;

        var cohortsReviewed = state.GovernmentApproval >= 0f && state.LawApproval >= 0f && state.PoliticalDivergence >= 0f;
        var awareness = ResidentAwareness.Of(world, state);

        var infra = state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);
        var port = state.Buildings.GetValueOrDefault(BuildingKind.Port);
        var rail = state.Buildings.GetValueOrDefault(BuildingKind.RailNetwork);
        var barracks = state.Buildings.GetValueOrDefault(BuildingKind.Barracks);
        var avgEntrench = ReadAverageEntrenchment(world, state.MapIndex, canInspectAll);
        var crises = ReadRegionCrises(owner, state.MapIndex);

        return new RegionAuditSnapshot(
            Movements: movements,
            OwnerTitle: ownerTitle,
            ForeignClaimants: claimants,
            IsAutonomous: isAuto,
            AutonomyWeeklyCost: autoCost,
            AutonomyRecentlyRevoked: autoRevoked,
            IsUnentitled: unentitled,
            ConquestAcceptanceShare: acceptance,
            ConquestWeeklyGain: weeklyGain,
            ConquestStallReason: stall ?? "",
            SecessionPressure: MathF.Max(0f, state.SecessionPressure),
            ParallelAdminNationId: paId,
            ParallelAdminName: paName,
            ParallelAdminCoverage: paCov,
            ParallelAdminExposed: paExp,
            HeldByNationId: heldById,
            HeldByName: heldByName,
            HeldAdministered: heldAdmin,
            WarDamageShare: damage,
            OutputPenaltyShare: outputPenalty,
            RebuildingDaysLeft: rebuildLeft,
            DisplacedPeople: displaced,
            OccupiedProvinces: occProvs,
            TotalProvinces: totProvs,
            ProvinceOccupationShare: provOcc,
            ProvinceWeeklyResistanceSlip: provSlip,
            BrainDrainPressure: M.Clamp01(state.BrainDrainPressure),
            ForeignMinorityShare: M.Clamp01(foreignShare),
            CohortsReviewed: cohortsReviewed,
            GovernmentApproval: cohortsReviewed ? M.Clamp01(state.GovernmentApproval) : 0f,
            LawApproval: cohortsReviewed ? M.Clamp01(state.LawApproval) : 0f,
            PoliticalDivergence: cohortsReviewed ? M.Clamp01(state.PoliticalDivergence) : 0f,
            CivicAwareness: awareness,
            InfrastructureLevel: infra,
            PortLevel: port,
            RailLevel: rail,
            BarracksLevel: barracks,
            AverageGarrisonEntrenchment: avgEntrench,
            ActiveCrises: crises);
    }

    /// <summary>Builds a complete read-only telemetry audit snapshot for a country dossier.</summary>
    public static CountryAuditSnapshot ReadCountryAudit(Campaign world, Nation nation, bool canInspectAll = false)
    {
        if (world is null || nation is null)
            return new CountryAuditSnapshot(
                0, 0, Array.Empty<CountryMovementThreat>(), 0, 0f, "", 0, 0f, 0f, 0.78f,
                0f, 0L, 0L, "", "", 0, 0, 0f, 0, 0f,
                false, 0, 5, NuclearDoctrine.RetaliatoryTriad, 0, 5, null,
                false, 0f, 0f, 0f, false, PoliticalBloc.Reformist, 0f, 0f, Array.Empty<RegionCrisisEntry>());

        var states = world.StatesOf(nation);
        var stateByMap = new Dictionary<int, ScenarioState>(states.Length);
        var restiveCount = 0;
        var maxSecession = 0f;
        var mostRestiveName = "";
        var autoCount = 0;
        var damagedCount = 0;
        var damageSum = 0f;

        for (var i = 0; i < states.Length; i++)
        {
            var s = states[i];
            if (s is null) continue;
            stateByMap[s.MapIndex] = s;
            if (RegionalAutonomy.Autonomous(s)) autoCount++;
            if (s.SecessionPressure >= 0.20f) restiveCount++;
            if (s.SecessionPressure > maxSecession)
            {
                maxSecession = s.SecessionPressure;
                mostRestiveName = s.DisplayName;
            }
            if (s.WarDamage >= 0.05f)
            {
                damagedCount++;
                damageSum += M.Clamp01(s.WarDamage);
            }
        }

        var (movCount, hqInside, topMovements) = ReadCountryMovements(world, nation, stateByMap);
        var crisisSnap = DomesticCrises.Read(world, nation);

        var drainDest = nation.BrainDrainDestinationId >= 0
            ? (world.NationById(nation.BrainDrainDestinationId)?.DisplayName ?? "")
            : "";
        var gainSrc = nation.BrainGainSourceId >= 0
            ? (world.NationById(nation.BrainGainSourceId)?.DisplayName ?? "")
            : "";

        var openWars = 0;
        var stalledWars = 0;
        var liveWars = Warfare.LiveWars(world);
        for (var i = 0; i < liveWars.Count; i++)
        {
            var w = liveWars[i];
            if (w is { Ended: false } && w.Involves(nation.Id))
            {
                openWars++;
                if (w.Stalled) stalledWars++;
            }
        }
        var fatigue = openWars > 0 ? WarWeariness.NationalFatigue(world, nation) : 0f;

        var nucEra = Deterrence.IsNuclearEraEnabled(world);
        var detTier = nucEra ? Deterrence.TierOf(world, nation) : 0;
        var armsPacts = 0;
        if (detTier > 0 && nation.ArmsControlPacts is not null)
        {
            foreach (var kv in nation.ArmsControlPacts)
                if (kv.Value > world.ElapsedDays) armsPacts++;
        }

        var rankBreakdown = ReadRankBreakdown(world, nation.Id);
        var mood = PoliticalCohorts.Mood(world, nation);
        var blocs = PoliticalCohorts.NationalBlocs(world, nation);
        var crises = ReadCountryCrises(nation);

        return new CountryAuditSnapshot(
            ActiveMovementsCount: movCount,
            MovementsWithHqInsideCount: hqInside,
            TopMovements: topMovements,
            RestiveRegionsCount: restiveCount,
            MaxSecessionPressure: maxSecession,
            MostRestiveRegionName: mostRestiveName,
            AutonomousRegionsCount: autoCount,
            ImperialOverstretch: MathF.Max(0f, nation.ImperialOverstretch),
            CoupPressure: crisisSnap.CoupPressure,
            CoupThreshold: crisisSnap.CoupThreshold,
            BrainDrainRate: nation.BrainDrainRate,
            WeeklyNetMigration: nation.WeeklyNetMigration,
            CumulativeNetMigration: nation.CumulativeNetMigration,
            BrainDrainDestinationName: drainDest,
            BrainGainSourceName: gainSrc,
            OpenWarCount: openWars,
            StalledWarCount: stalledWars,
            WarFatigue: fatigue,
            DamagedRegionsCount: damagedCount,
            AverageWarDamage: damagedCount > 0 ? damageSum / damagedCount : 0f,
            NuclearEraActive: nucEra,
            DeterrentTier: detTier,
            OwnNuclearPosture: Math.Clamp(nation.NuclearPosture, 1, 5),
            NuclearDoctrine: nation.NuclearDoctrine,
            ActiveArmsControlPacts: armsPacts,
            GlobalDefconLevel: Math.Clamp(world.DefconLevel, 1, 5),
            RankBreakdown: rankBreakdown,
            CohortsReviewed: mood.Reviewed,
            GovernmentApproval: mood.Reviewed ? M.Clamp01(mood.Government) : 0f,
            LawApproval: mood.Reviewed ? M.Clamp01(mood.Law) : 0f,
            PoliticalDivergence: mood.Reviewed ? M.Clamp01(mood.Divergence) : 0f,
            BlocsReviewed: blocs.Reviewed,
            DominantBloc: blocs.Dominant,
            DominantBlocShare: blocs.Reviewed ? M.Clamp01(blocs.ShareOf(blocs.Dominant)) : 0f,
            Polarization: blocs.Reviewed ? M.Clamp01(blocs.Polarization) : 0f,
            ActiveCrises: crises);
    }

    private static IReadOnlyList<RegionClaimantEntry> ReadForeignClaimants(Campaign world, Nation owner, ScenarioState state)
    {
        var seen = new HashSet<int>();
        var list = new List<RegionClaimantEntry>(4);
        var origin = TerritorialClaims.Origin(state);

        void TryAdd(Nation n, bool isHomeland)
        {
            if (n is null || n.Population <= 0 || n.Movement is not null) return;
            if (owner is not null && n.Id == owner.Id) return;
            if (n.RenouncedTerritoryOrigins.Contains(origin)) return;
            if (!seen.Add(n.Id)) return;
            list.Add(new RegionClaimantEntry(n.Id, n.Tag ?? "", n.DisplayName ?? n.Name ?? "", isHomeland));
        }

        if (state.CoreTags is not null)
            for (var i = 0; i < state.CoreTags.Count; i++)
                TryAdd(world.NationByTag(state.CoreTags[i]), isHomeland: true);

        foreach (var kv in world.Homelands)
            if (kv.Value is not null && (kv.Value.Contains(state.MapIndex) || (origin != state.MapIndex && kv.Value.Contains(origin))))
                TryAdd(world.NationById(kv.Key), isHomeland: true);

        if (state.Claims is not null)
            for (var i = 0; i < state.Claims.Count; i++)
                TryAdd(world.NationByTag(state.Claims[i]), isHomeland: false);

        list.Sort(static (a, b) =>
        {
            var h = b.IsHomeland.CompareTo(a.IsHomeland);
            return h != 0 ? h : a.NationId.CompareTo(b.NationId);
        });
        return list;
    }

    private static (int Occupied, int Total, float ProvOcc, float ProvSlip) ReadOccupationMetrics(
        Campaign world, Nation owner, ScenarioState state, int provinceId)
    {
        if (world.ProvinceMap is not { } map) return (0, 0, 0f, 0f);
        var controllers = world.ProvinceController ?? map.ProvinceToNation;
        var toState = map.ProvinceToState;
        if (controllers is null || toState is null) return (0, 0, 0f, 0f);

        var occupied = 0;
        var total = 0;
        var max = Math.Min(map.ProvinceCount, Math.Min(controllers.Length - 1, toState.Length - 1));
        var ownerMap = owner?.MapIndex ?? state.NationMapIndex;
        for (var p = 1; p <= max; p++)
        {
            if (toState[p] != state.MapIndex || map.WaterByProvince[p]) continue;
            total++;
            var ctrl = controllers[p];
            if (ctrl > 0 && ctrl != ownerMap) occupied++;
        }

        var provOcc = 0f;
        var provSlip = 0f;
        if (provinceId > 0 && provinceId <= max && controllers[provinceId] != ownerMap)
        {
            provOcc = world.ProvinceOccupation is not null && provinceId < world.ProvinceOccupation.Length
                ? M.Clamp01(world.ProvinceOccupation[provinceId])
                : 0f;
            provSlip = OccupationResistance.WeeklySlip(world, provinceId);
        }
        else if (occupied > 0 && world.ProvinceOccupation is not null)
        {
            var occSum = 0f;
            var slipSum = 0f;
            for (var p = 1; p <= max; p++)
            {
                if (toState[p] != state.MapIndex || map.WaterByProvince[p]) continue;
                if (controllers[p] <= 0 || controllers[p] == ownerMap) continue;
                if (p < world.ProvinceOccupation.Length) occSum += M.Clamp01(world.ProvinceOccupation[p]);
                slipSum += OccupationResistance.WeeklySlip(world, p);
            }
            provOcc = occSum / occupied;
            provSlip = slipSum / occupied;
        }
        return (occupied, total, provOcc, provSlip);
    }

    private static float ReadAverageEntrenchment(Campaign world, int stateMapIndex, bool canInspectAll)
    {
        var divSum = 0f;
        var entSum = 0f;
        for (var i = 0; i < world.Armies.Count; i++)
        {
            var a = world.Armies[i];
            if (a.StateMapIndex != stateMapIndex || a.Divisions <= 0f) continue;
            if (!canInspectAll && !CampaignIntel.PlayerCanSeeArmy(world, a)) continue;
            divSum += a.Divisions;
            entSum += a.Divisions * M.Clamp01(a.Entrenchment);
        }
        return divSum > 0.01f ? entSum / divSum : 0f;
    }

    private static IReadOnlyList<RegionCrisisEntry> ReadRegionCrises(Nation owner, int stateMapIndex)
    {
        if (owner is null) return Array.Empty<RegionCrisisEntry>();
        var list = new List<RegionCrisisEntry>(2);
        if (owner.ActiveHealthCrises is not null)
            for (var i = 0; i < owner.ActiveHealthCrises.Count; i++)
            {
                var h = owner.ActiveHealthCrises[i];
                if (h is not { IsActive: true } || h.AffectedStateMapIndex != stateMapIndex) continue;
                var def = DiseaseCatalog.Find(h.DefinitionId);
                list.Add(new RegionCrisisEntry(def?.NameKey ?? h.DefinitionId, h.Severity, IsDisease: true));
            }
        if (owner.ActiveDisasters is not null)
            for (var i = 0; i < owner.ActiveDisasters.Count; i++)
            {
                var d = owner.ActiveDisasters[i];
                if (d is not { ResolvedDay: < 0 } || d.AffectedStateMapIndex != stateMapIndex) continue;
                var def = DisasterCatalog.Find(d.DefinitionId);
                list.Add(new RegionCrisisEntry(def?.NameKey ?? d.DefinitionId, d.Severity, IsDisease: false));
            }
        return list;
    }

    private static IReadOnlyList<RegionCrisisEntry> ReadCountryCrises(Nation nation)
    {
        var list = new List<RegionCrisisEntry>(4);
        if (nation.ActiveHealthCrises is not null)
            for (var i = 0; i < nation.ActiveHealthCrises.Count; i++)
            {
                var h = nation.ActiveHealthCrises[i];
                if (h is not { IsActive: true }) continue;
                var def = DiseaseCatalog.Find(h.DefinitionId);
                list.Add(new RegionCrisisEntry(def?.NameKey ?? h.DefinitionId, h.Severity, IsDisease: true));
            }
        if (nation.ActiveDisasters is not null)
            for (var i = 0; i < nation.ActiveDisasters.Count; i++)
            {
                var d = nation.ActiveDisasters[i];
                if (d is not { ResolvedDay: < 0 }) continue;
                var def = DisasterCatalog.Find(d.DefinitionId);
                list.Add(new RegionCrisisEntry(def?.NameKey ?? d.DefinitionId, d.Severity, IsDisease: false));
            }
        return list;
    }

    private static (int Total, int HqInside, IReadOnlyList<CountryMovementThreat> Top) ReadCountryMovements(
        Campaign world, Nation nation, Dictionary<int, ScenarioState> stateByMap)
    {
        if (stateByMap.Count == 0 || !world.FeatureEnabled("movements"))
            return (0, 0, Array.Empty<CountryMovementThreat>());

        var list = new List<CountryMovementThreat>(4);
        var hqInside = 0;
        foreach (var mov in Movements.Active(world))
        {
            var mv = mov.Movement;
            if (mv is null || mv.Dissolved || mov.Id == nation.Id) continue;

            var hasHq = stateByMap.ContainsKey(mv.HeadquartersStateMapIndex);
            var bestSup = 0f;
            var bestName = "";
            var maxRise = 0f;

            if (hasHq && stateByMap.TryGetValue(mv.HeadquartersStateMapIndex, out var hqRegion))
            {
                bestSup = MovementReach.ResidentSupport(world, mov, hqRegion);
                bestName = hqRegion.DisplayName;
                maxRise = MovementRules.UprisingChance(world, mov, hqRegion);
            }

            foreach (var kv in mv.Support)
            {
                if (kv.Value < 0.08f || !stateByMap.TryGetValue(kv.Key, out var region)) continue;
                var resSup = MovementReach.ResidentSupport(world, mov, region);
                var rise = MovementRules.UprisingChance(world, mov, region);
                if (rise > maxRise) maxRise = rise;
                if (resSup > bestSup)
                {
                    bestSup = resSup;
                    bestName = region.DisplayName;
                }
            }

            if (!hasHq && bestSup < 0.02f) continue;
            if (hasHq) hqInside++;
            list.Add(new CountryMovementThreat(mov.Id, mov.DisplayName, bestName, bestSup, hasHq, maxRise));
        }

        list.Sort(static (a, b) =>
        {
            var r = b.MaxUprisingChance.CompareTo(a.MaxUprisingChance);
            if (r != 0) return r;
            var h = b.HasHeadquartersInside.CompareTo(a.HasHeadquartersInside);
            if (h != 0) return h;
            var s = b.TopResidentSupport.CompareTo(a.TopResidentSupport);
            return s != 0 ? s : a.NationId.CompareTo(b.NationId);
        });
        return (list.Count, hqInside, list.Count <= 4 ? list : list.GetRange(0, 4));
    }

    private static CountryRankBreakdown ReadRankBreakdown(Campaign world, int nationId)
    {
        if (world.MidStep) return null;
        var rows = CountryRankings.Read(world, CountryRankings.Weights.Default);
        if (rows.Count == 0) return null;

        CountryRankings.Row target = null;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].NationId == nationId) { target = rows[i]; break; }
        if (target is null) return null;

        int eco = 1, pop = 1, mil = 1, tech = 1, pres = 1;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.NationId == nationId) continue;
            if (r.Economy > target.Economy) eco++;
            if (r.Population > target.Population) pop++;
            if (r.FieldPower > target.FieldPower) mil++;
            if (r.Technology > target.Technology) tech++;
            if (r.Prestige > target.Prestige) pres++;
        }
        return new CountryRankBreakdown(target.OverallRank, target.Overall, rows.Count, eco, pop, mil, tech, pres);
    }
}
