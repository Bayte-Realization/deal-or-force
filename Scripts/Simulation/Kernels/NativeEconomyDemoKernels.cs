// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Batched native SIMD economy &amp; demography wrappers backed by <c>dof_kernels</c>
/// (<c>dof_demo_*</c>, <c>dof_econ_*</c>) with bit-exact pure-C# fallbacks.
/// </summary>
public static partial class NativeEconomyDemoKernels
{
    public const byte VitalFlagAtWar = 1 << 0;
    public const byte VitalFlagTraditionBonus = 1 << 1;
    public const byte VitalFlagSecularPenalty = 1 << 2;
    public const byte VitalFlagPronatalist = 1 << 3;
    public const byte VitalFlagDemoBoom = 1 << 4;
    public const byte VitalFlagAgeEnabled = 1 << 5;

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_demo_step_cohorts_batch_v1(
        uint count,
        float* population,
        long* reviewBirths,
        long* reviewDeaths,
        float* youngShare,
        float* oldShare,
        float maturationAnnual,
        float ageingAnnual,
        float mortalityOldWeight,
        float interval,
        float* outYoung,
        float* outOld,
        float* outWorking);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_demo_aggregate_nations_v1(
        uint nationCount,
        uint* stateOffsets,
        long* statePop,
        float* stateYoung,
        float* stateOld,
        long* stateBirths,
        long* stateDeaths,
        long* stateMigration,
        uint stateCount,
        float defaultYoung,
        float defaultOld,
        float defaultWorking,
        float refOld,
        float* outYoung,
        float* outOld,
        float* outWorking,
        float* outOldExcess,
        long* outBirths,
        long* outDeaths,
        long* outMigration);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_econ_household_groups_v1(
        uint groupCount,
        long* sizes,
        byte* isUpper,
        float* anchorWealth,
        float* employment,
        float* wealth,
        float* sol,
        float* radicalism,
        float workingShare,
        byte needEmployment,
        float buildingJobs,
        float nonBuildingJobsIn,
        float annualConditions,
        float interval,
        float damage,
        float food,
        float energy,
        float medicine,
        float scarcityTolerance,
        float employmentRate,
        float wealthRate,
        float nationSol,
        float stateRad,
        float* outNonBuildingJobs,
        float* outEmpTarget,
        float* outBasicNeeds,
        float* outStress,
        double* outLivingSum);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_econ_vital_rates_batch_v1(
        uint count,
        long* pop,
        float* warDamage,
        int* hospitals,
        float* literacy,
        float* urbanization,
        float* sol,
        float* radicalism,
        float* youngShare,
        float* oldShare,
        float* welfareSpending,
        float* inflation,
        float* warSupport,
        float* nationStability,
        float* stateStability,
        float* capitalLat,
        float* food,
        float* energy,
        float* medicine,
        byte* flags,
        float scarcityTolerance,
        float birthBase,
        float lowLitBirth,
        float deathBase,
        float shortageDeath,
        float refOld,
        float mortOldWeight,
        float interval,
        float* outBirth,
        float* outDeath,
        float* outOldBurden,
        long* outReviewBirths,
        long* outReviewDeaths);

    public static bool StepCohortsBatch(
        ReadOnlySpan<float> population,
        ReadOnlySpan<long> reviewBirths,
        ReadOnlySpan<long> reviewDeaths,
        ReadOnlySpan<float> youngShare,
        ReadOnlySpan<float> oldShare,
        float maturationAnnual,
        float ageingAnnual,
        float mortalityOldWeight,
        float interval,
        Span<float> outYoung,
        Span<float> outOld,
        Span<float> outWorking)
    {
        if (NativeKernelsLoader.EnableEconomyDemoDefault &&
            TryStepCohortsBatch(
                population, reviewBirths, reviewDeaths, youngShare, oldShare,
                maturationAnnual, ageingAnnual, mortalityOldWeight, interval,
                outYoung, outOld, outWorking))
        {
            return true;
        }

        ManagedStepCohortsBatch(
            population, reviewBirths, reviewDeaths, youngShare, oldShare,
            maturationAnnual, ageingAnnual, mortalityOldWeight, interval,
            outYoung, outOld, outWorking);
        return false;
    }

    public static unsafe bool TryStepCohortsBatch(
        ReadOnlySpan<float> population,
        ReadOnlySpan<long> reviewBirths,
        ReadOnlySpan<long> reviewDeaths,
        ReadOnlySpan<float> youngShare,
        ReadOnlySpan<float> oldShare,
        float maturationAnnual,
        float ageingAnnual,
        float mortalityOldWeight,
        float interval,
        Span<float> outYoung,
        Span<float> outOld,
        Span<float> outWorking)
    {
        int n = population.Length;
        if (reviewBirths.Length < n || reviewDeaths.Length < n ||
            youngShare.Length < n || oldShare.Length < n ||
            outYoung.Length < n || outOld.Length < n || outWorking.Length < n)
        {
            return false;
        }
        if (n == 0) return true;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconomyDemo)) return false;

        fixed (float* pPop = population)
        fixed (long* pBirths = reviewBirths)
        fixed (long* pDeaths = reviewDeaths)
        fixed (float* pYoung = youngShare)
        fixed (float* pOld = oldShare)
        fixed (float* pOutY = outYoung)
        fixed (float* pOutO = outOld)
        fixed (float* pOutW = outWorking)
        {
            return dof_demo_step_cohorts_batch_v1(
                (uint)n, pPop, pBirths, pDeaths, pYoung, pOld,
                maturationAnnual, ageingAnnual, mortalityOldWeight, interval,
                pOutY, pOutO, pOutW) == 0;
        }
    }

    public static void ManagedStepCohortsBatch(
        ReadOnlySpan<float> population,
        ReadOnlySpan<long> reviewBirths,
        ReadOnlySpan<long> reviewDeaths,
        ReadOnlySpan<float> youngShare,
        ReadOnlySpan<float> oldShare,
        float maturationAnnual,
        float ageingAnnual,
        float mortalityOldWeight,
        float interval,
        Span<float> outYoung,
        Span<float> outOld,
        Span<float> outWorking)
    {
        int n = population.Length;
        for (int i = 0; i < n; i++)
        {
            DemographyRulesHook.ManagedStepCohorts(
                population[i], reviewBirths[i], reviewDeaths[i], youngShare[i], oldShare[i],
                maturationAnnual, ageingAnnual, mortalityOldWeight, interval,
                out outYoung[i], out outOld[i], out outWorking[i]);
        }
    }

    public static bool AggregateNations(
        int nationCount,
        ReadOnlySpan<uint> stateOffsets,
        ReadOnlySpan<long> statePop,
        ReadOnlySpan<float> stateYoung,
        ReadOnlySpan<float> stateOld,
        ReadOnlySpan<long> stateBirths,
        ReadOnlySpan<long> stateDeaths,
        ReadOnlySpan<long> stateMigration,
        int stateCount,
        float defaultYoung,
        float defaultOld,
        float defaultWorking,
        float refOld,
        Span<float> outYoung,
        Span<float> outOld,
        Span<float> outWorking,
        Span<float> outOldExcess,
        Span<long> outBirths,
        Span<long> outDeaths,
        Span<long> outMigration)
    {
        if (NativeKernelsLoader.EnableEconomyDemoDefault &&
            TryAggregateNations(
                nationCount, stateOffsets, statePop, stateYoung, stateOld,
                stateBirths, stateDeaths, stateMigration, stateCount,
                defaultYoung, defaultOld, defaultWorking, refOld,
                outYoung, outOld, outWorking, outOldExcess, outBirths, outDeaths, outMigration))
        {
            return true;
        }

        ManagedAggregateNations(
            nationCount, stateOffsets, statePop, stateYoung, stateOld,
            stateBirths, stateDeaths, stateMigration, stateCount,
            defaultYoung, defaultOld, defaultWorking, refOld,
            outYoung, outOld, outWorking, outOldExcess, outBirths, outDeaths, outMigration);
        return false;
    }

    public static unsafe bool TryAggregateNations(
        int nationCount,
        ReadOnlySpan<uint> stateOffsets,
        ReadOnlySpan<long> statePop,
        ReadOnlySpan<float> stateYoung,
        ReadOnlySpan<float> stateOld,
        ReadOnlySpan<long> stateBirths,
        ReadOnlySpan<long> stateDeaths,
        ReadOnlySpan<long> stateMigration,
        int stateCount,
        float defaultYoung,
        float defaultOld,
        float defaultWorking,
        float refOld,
        Span<float> outYoung,
        Span<float> outOld,
        Span<float> outWorking,
        Span<float> outOldExcess,
        Span<long> outBirths,
        Span<long> outDeaths,
        Span<long> outMigration)
    {
        if (nationCount < 0 || stateCount < 0) return false;
        if (nationCount == 0) return true;
        if (stateOffsets.Length < nationCount + 1 ||
            statePop.Length < stateCount || stateYoung.Length < stateCount || stateOld.Length < stateCount ||
            stateBirths.Length < stateCount || stateDeaths.Length < stateCount || stateMigration.Length < stateCount ||
            outYoung.Length < nationCount || outOld.Length < nationCount || outWorking.Length < nationCount ||
            outOldExcess.Length < nationCount || outBirths.Length < nationCount ||
            outDeaths.Length < nationCount || outMigration.Length < nationCount)
        {
            return false;
        }
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconomyDemo)) return false;

        fixed (uint* pOff = stateOffsets)
        fixed (long* pPop = statePop)
        fixed (float* pYoung = stateYoung)
        fixed (float* pOld = stateOld)
        fixed (long* pBirths = stateBirths)
        fixed (long* pDeaths = stateDeaths)
        fixed (long* pMig = stateMigration)
        fixed (float* pOutY = outYoung)
        fixed (float* pOutO = outOld)
        fixed (float* pOutW = outWorking)
        fixed (float* pOutEx = outOldExcess)
        fixed (long* pOutB = outBirths)
        fixed (long* pOutD = outDeaths)
        fixed (long* pOutM = outMigration)
        {
            return dof_demo_aggregate_nations_v1(
                (uint)nationCount, pOff, pPop, pYoung, pOld, pBirths, pDeaths, pMig, (uint)stateCount,
                defaultYoung, defaultOld, defaultWorking, refOld,
                pOutY, pOutO, pOutW, pOutEx, pOutB, pOutD, pOutM) == 0;
        }
    }

    public static void ManagedAggregateNations(
        int nationCount,
        ReadOnlySpan<uint> stateOffsets,
        ReadOnlySpan<long> statePop,
        ReadOnlySpan<float> stateYoung,
        ReadOnlySpan<float> stateOld,
        ReadOnlySpan<long> stateBirths,
        ReadOnlySpan<long> stateDeaths,
        ReadOnlySpan<long> stateMigration,
        int stateCount,
        float defaultYoung,
        float defaultOld,
        float defaultWorking,
        float refOld,
        Span<float> outYoung,
        Span<float> outOld,
        Span<float> outWorking,
        Span<float> outOldExcess,
        Span<long> outBirths,
        Span<long> outDeaths,
        Span<long> outMigration)
    {
        if (nationCount <= 0) return;
        for (int n = 0; n < nationCount; n++)
        {
            int start = (int)stateOffsets[n];
            int end = (int)stateOffsets[n + 1];
            if (end <= start || end > stateCount)
            {
                outYoung[n] = defaultYoung;
                outOld[n] = defaultOld;
                outWorking[n] = defaultWorking;
                outOldExcess[n] = 0f;
                outBirths[n] = 0;
                outDeaths[n] = 0;
                outMigration[n] = 0;
                continue;
            }
            double people = 0, young = 0, old = 0;
            long births = 0, deaths = 0, migration = 0;
            for (int s = start; s < end; s++)
            {
                long size = Math.Max(0, statePop[s]);
                people += size;
                young += size * stateYoung[s];
                old += size * stateOld[s];
                births += stateBirths[s];
                deaths += stateDeaths[s];
                migration += stateMigration[s];
            }
            double total = Math.Max(1d, people);
            float y = (float)(young / total);
            float o = (float)(old / total);
            outYoung[n] = y;
            outOld[n] = o;
            outWorking[n] = Math.Clamp(1f - y - o, 0.05f, 0.95f);
            outOldExcess[n] = Math.Max(0f, o - refOld);
            outBirths[n] = births;
            outDeaths[n] = deaths;
            outMigration[n] = migration;
        }
    }

    public static bool HouseholdGroups(
        int groupCount,
        ReadOnlySpan<long> sizes,
        ReadOnlySpan<byte> isUpper,
        ReadOnlySpan<float> anchorWealth,
        Span<float> employment,
        Span<float> wealth,
        Span<float> sol,
        Span<float> radicalism,
        float workingShare,
        bool needEmployment,
        float buildingJobs,
        float nonBuildingJobsIn,
        float annualConditions,
        float interval,
        float damage,
        float food,
        float energy,
        float medicine,
        float scarcityTolerance,
        float employmentRate,
        float wealthRate,
        float nationSol,
        float stateRad,
        out float outNonBuildingJobs,
        out float outEmpTarget,
        out float outBasicNeeds,
        out float outStress,
        out double outLivingSum)
    {
        if (NativeKernelsLoader.EnableEconomyDemoDefault &&
            TryHouseholdGroups(
                groupCount, sizes, isUpper, anchorWealth, employment, wealth, sol, radicalism,
                workingShare, needEmployment, buildingJobs, nonBuildingJobsIn, annualConditions, interval,
                damage, food, energy, medicine, scarcityTolerance, employmentRate, wealthRate,
                nationSol, stateRad,
                out outNonBuildingJobs, out outEmpTarget, out outBasicNeeds, out outStress, out outLivingSum))
        {
            return true;
        }

        ManagedHouseholdGroups(
            groupCount, sizes, isUpper, anchorWealth, employment, wealth, sol, radicalism,
            workingShare, needEmployment, buildingJobs, nonBuildingJobsIn, annualConditions, interval,
            damage, food, energy, medicine, scarcityTolerance, employmentRate, wealthRate,
            nationSol, stateRad,
            out outNonBuildingJobs, out outEmpTarget, out outBasicNeeds, out outStress, out outLivingSum);
        return false;
    }

    public static unsafe bool TryHouseholdGroups(
        int groupCount,
        ReadOnlySpan<long> sizes,
        ReadOnlySpan<byte> isUpper,
        ReadOnlySpan<float> anchorWealth,
        Span<float> employment,
        Span<float> wealth,
        Span<float> sol,
        Span<float> radicalism,
        float workingShare,
        bool needEmployment,
        float buildingJobs,
        float nonBuildingJobsIn,
        float annualConditions,
        float interval,
        float damage,
        float food,
        float energy,
        float medicine,
        float scarcityTolerance,
        float employmentRate,
        float wealthRate,
        float nationSol,
        float stateRad,
        out float outNonBuildingJobs,
        out float outEmpTarget,
        out float outBasicNeeds,
        out float outStress,
        out double outLivingSum)
    {
        outNonBuildingJobs = 0f;
        outEmpTarget = 0f;
        outBasicNeeds = 0f;
        outStress = 0f;
        outLivingSum = 0d;

        if (groupCount < 0) return false;
        if (sizes.Length < groupCount || isUpper.Length < groupCount || anchorWealth.Length < groupCount ||
            employment.Length < groupCount || wealth.Length < groupCount || sol.Length < groupCount ||
            radicalism.Length < groupCount)
        {
            return false;
        }
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconomyDemo)) return false;

        fixed (long* pSizes = sizes)
        fixed (byte* pUpper = isUpper)
        fixed (float* pAnchor = anchorWealth)
        fixed (float* pEmp = employment)
        fixed (float* pWealth = wealth)
        fixed (float* pSol = sol)
        fixed (float* pRad = radicalism)
        fixed (float* pOutNb = &outNonBuildingJobs)
        fixed (float* pOutEt = &outEmpTarget)
        fixed (float* pOutBn = &outBasicNeeds)
        fixed (float* pOutSt = &outStress)
        fixed (double* pOutLiving = &outLivingSum)
        {
            return dof_econ_household_groups_v1(
                (uint)groupCount, pSizes, pUpper, pAnchor, pEmp, pWealth, pSol, pRad,
                workingShare, needEmployment ? (byte)1 : (byte)0, buildingJobs, nonBuildingJobsIn,
                annualConditions, interval, damage, food, energy, medicine, scarcityTolerance,
                employmentRate, wealthRate, nationSol, stateRad,
                pOutNb, pOutEt, pOutBn, pOutSt, pOutLiving) == 0;
        }
    }

    public static void ManagedHouseholdGroups(
        int groupCount,
        ReadOnlySpan<long> sizes,
        ReadOnlySpan<byte> isUpper,
        ReadOnlySpan<float> anchorWealth,
        Span<float> employment,
        Span<float> wealth,
        Span<float> sol,
        Span<float> radicalism,
        float workingShare,
        bool needEmployment,
        float buildingJobs,
        float nonBuildingJobsIn,
        float annualConditions,
        float interval,
        float damage,
        float food,
        float energy,
        float medicine,
        float scarcityTolerance,
        float employmentRate,
        float wealthRate,
        float nationSol,
        float stateRad,
        out float outNonBuildingJobs,
        out float outEmpTarget,
        out float outBasicNeeds,
        out float outStress,
        out double outLivingSum)
    {
        double sumLabour = 0.0;
        double sumEmployed = 0.0;
        if (needEmployment)
        {
            for (int i = 0; i < groupCount; i++)
            {
                double active = sizes[i] * workingShare * 1e-6 * (isUpper[i] != 0 ? .15 : 1);
                sumLabour += active;
                sumEmployed += active * employment[i];
            }
        }
        else
        {
            for (int i = 0; i < groupCount; i++)
            {
                sumLabour += sizes[i] * workingShare * 1e-6 * (isUpper[i] != 0 ? .15 : 1);
            }
        }

        float labour = (float)sumLabour;
        float nonBuilding = nonBuildingJobsIn;
        if (needEmployment)
        {
            float emp = labour <= 0f ? 0f : (float)sumEmployed / labour;
            nonBuilding = Math.Max(0, labour * emp - buildingJobs);
        }

        float dmg = Math.Clamp(damage, 0, 1);
        nonBuilding = Math.Max(0, nonBuilding * (1 + Math.Clamp(annualConditions, -.25f, .15f) * interval));
        float empTarget = labour <= 0 ? 0 : Math.Clamp((nonBuilding + buildingJobs) * (1 - dmg * .5f) / labour, 0, 1);
        float basicNeeds = food * .6f + energy * .25f + medicine * .15f;
        float scarcity = Math.Max(0, 1 - basicNeeds - scarcityTolerance);
        float unemployment = Math.Max(0, .9f - empTarget);
        float stress = Math.Clamp(scarcity * .65f + unemployment * .25f + dmg * .25f, 0, 1);
        float empRate = Math.Clamp(employmentRate, 0, 1);
        float wRate = Math.Clamp(wealthRate, 0, 1);

        double living = 0;
        for (int i = 0; i < groupCount; i++)
        {
            float e = M.Lerp(employment[i], empTarget, empRate);
            employment[i] = e;
            float targetWealth = Math.Clamp(anchorWealth[i] + (e - .9f) * .22f - scarcity * .18f - dmg * .12f, 0, 1);
            float w = M.Lerp(wealth[i], targetWealth, wRate);
            wealth[i] = w;
            float targetLiving = Math.Clamp(nationSol + (w - .5f) * .12f - stress * .25f, 0, 1);
            float sVal = M.Lerp(sol[i], targetLiving, .04f);
            sol[i] = sVal;
            radicalism[i] = Math.Clamp(M.Lerp(radicalism[i], stateRad + stress * .2f, .025f), 0, 1);
            living += sizes[i] * (double)sVal;
        }

        outNonBuildingJobs = nonBuilding;
        outEmpTarget = empTarget;
        outBasicNeeds = basicNeeds;
        outStress = stress;
        outLivingSum = living;
    }

    public static bool VitalRatesBatch(
        ReadOnlySpan<long> pop,
        ReadOnlySpan<float> warDamage,
        ReadOnlySpan<int> hospitals,
        ReadOnlySpan<float> literacy,
        ReadOnlySpan<float> urbanization,
        ReadOnlySpan<float> sol,
        ReadOnlySpan<float> radicalism,
        ReadOnlySpan<float> youngShare,
        ReadOnlySpan<float> oldShare,
        ReadOnlySpan<float> welfareSpending,
        ReadOnlySpan<float> inflation,
        ReadOnlySpan<float> warSupport,
        ReadOnlySpan<float> nationStability,
        ReadOnlySpan<float> stateStability,
        ReadOnlySpan<float> capitalLat,
        ReadOnlySpan<float> food,
        ReadOnlySpan<float> energy,
        ReadOnlySpan<float> medicine,
        ReadOnlySpan<byte> flags,
        float scarcityTolerance,
        float birthBase,
        float lowLitBirth,
        float deathBase,
        float shortageDeath,
        float refOld,
        float mortOldWeight,
        float interval,
        Span<float> outBirth,
        Span<float> outDeath,
        Span<float> outOldBurden,
        Span<long> outReviewBirths = default,
        Span<long> outReviewDeaths = default)
    {
        if (NativeKernelsLoader.EnableEconomyDemoDefault &&
            TryVitalRatesBatch(
                pop, warDamage, hospitals, literacy, urbanization, sol, radicalism, youngShare, oldShare,
                welfareSpending, inflation, warSupport, nationStability, stateStability, capitalLat,
                food, energy, medicine, flags,
                scarcityTolerance, birthBase, lowLitBirth, deathBase, shortageDeath, refOld, mortOldWeight, interval,
                outBirth, outDeath, outOldBurden, outReviewBirths, outReviewDeaths))
        {
            return true;
        }

        ManagedVitalRatesBatch(
            pop, warDamage, hospitals, literacy, urbanization, sol, radicalism, youngShare, oldShare,
            welfareSpending, inflation, warSupport, nationStability, stateStability, capitalLat,
            food, energy, medicine, flags,
            scarcityTolerance, birthBase, lowLitBirth, deathBase, shortageDeath, refOld, mortOldWeight, interval,
            outBirth, outDeath, outOldBurden, outReviewBirths, outReviewDeaths);
        return false;
    }

    public static unsafe bool TryVitalRatesBatch(
        ReadOnlySpan<long> pop,
        ReadOnlySpan<float> warDamage,
        ReadOnlySpan<int> hospitals,
        ReadOnlySpan<float> literacy,
        ReadOnlySpan<float> urbanization,
        ReadOnlySpan<float> sol,
        ReadOnlySpan<float> radicalism,
        ReadOnlySpan<float> youngShare,
        ReadOnlySpan<float> oldShare,
        ReadOnlySpan<float> welfareSpending,
        ReadOnlySpan<float> inflation,
        ReadOnlySpan<float> warSupport,
        ReadOnlySpan<float> nationStability,
        ReadOnlySpan<float> stateStability,
        ReadOnlySpan<float> capitalLat,
        ReadOnlySpan<float> food,
        ReadOnlySpan<float> energy,
        ReadOnlySpan<float> medicine,
        ReadOnlySpan<byte> flags,
        float scarcityTolerance,
        float birthBase,
        float lowLitBirth,
        float deathBase,
        float shortageDeath,
        float refOld,
        float mortOldWeight,
        float interval,
        Span<float> outBirth,
        Span<float> outDeath,
        Span<float> outOldBurden,
        Span<long> outReviewBirths = default,
        Span<long> outReviewDeaths = default)
    {
        int n = pop.Length;
        if (warDamage.Length < n || hospitals.Length < n || literacy.Length < n || urbanization.Length < n ||
            sol.Length < n || radicalism.Length < n || youngShare.Length < n || oldShare.Length < n ||
            welfareSpending.Length < n || inflation.Length < n || warSupport.Length < n ||
            nationStability.Length < n || stateStability.Length < n || capitalLat.Length < n ||
            food.Length < n || energy.Length < n || medicine.Length < n || flags.Length < n ||
            outBirth.Length < n || outDeath.Length < n || outOldBurden.Length < n ||
            (!outReviewBirths.IsEmpty && outReviewBirths.Length < n) ||
            (!outReviewDeaths.IsEmpty && outReviewDeaths.Length < n))
        {
            return false;
        }
        if (n == 0) return true;
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapEconomyDemo)) return false;

        fixed (long* pPop = pop)
        fixed (float* pWd = warDamage)
        fixed (int* pHosp = hospitals)
        fixed (float* pLit = literacy)
        fixed (float* pUrb = urbanization)
        fixed (float* pSol = sol)
        fixed (float* pRad = radicalism)
        fixed (float* pYs = youngShare)
        fixed (float* pOs = oldShare)
        fixed (float* pWel = welfareSpending)
        fixed (float* pInf = inflation)
        fixed (float* pWs = warSupport)
        fixed (float* pNStab = nationStability)
        fixed (float* pSStab = stateStability)
        fixed (float* pLat = capitalLat)
        fixed (float* pFd = food)
        fixed (float* pEn = energy)
        fixed (float* pMd = medicine)
        fixed (byte* pFlg = flags)
        fixed (float* pOutB = outBirth)
        fixed (float* pOutD = outDeath)
        fixed (float* pOutOb = outOldBurden)
        fixed (long* pOutRb = outReviewBirths)
        fixed (long* pOutRd = outReviewDeaths)
        {
            return dof_econ_vital_rates_batch_v1(
                (uint)n, pPop, pWd, pHosp, pLit, pUrb, pSol, pRad, pYs, pOs,
                pWel, pInf, pWs, pNStab, pSStab, pLat, pFd, pEn, pMd, pFlg,
                scarcityTolerance, birthBase, lowLitBirth, deathBase, shortageDeath,
                refOld, mortOldWeight, interval,
                pOutB, pOutD, pOutOb,
                outReviewBirths.IsEmpty ? null : pOutRb,
                outReviewDeaths.IsEmpty ? null : pOutRd) == 0;
        }
    }

    public static void ManagedVitalRatesBatch(
        ReadOnlySpan<long> pop,
        ReadOnlySpan<float> warDamage,
        ReadOnlySpan<int> hospitals,
        ReadOnlySpan<float> literacy,
        ReadOnlySpan<float> urbanization,
        ReadOnlySpan<float> sol,
        ReadOnlySpan<float> radicalism,
        ReadOnlySpan<float> youngShare,
        ReadOnlySpan<float> oldShare,
        ReadOnlySpan<float> welfareSpending,
        ReadOnlySpan<float> inflation,
        ReadOnlySpan<float> warSupport,
        ReadOnlySpan<float> nationStability,
        ReadOnlySpan<float> stateStability,
        ReadOnlySpan<float> capitalLat,
        ReadOnlySpan<float> food,
        ReadOnlySpan<float> energy,
        ReadOnlySpan<float> medicine,
        ReadOnlySpan<byte> flags,
        float scarcityTolerance,
        float birthBase,
        float lowLitBirth,
        float deathBase,
        float shortageDeath,
        float refOld,
        float mortOldWeight,
        float interval,
        Span<float> outBirth,
        Span<float> outDeath,
        Span<float> outOldBurden,
        Span<long> outReviewBirths = default,
        Span<long> outReviewDeaths = default)
    {
        int n = pop.Length;
        bool wantRb = !outReviewBirths.IsEmpty;
        bool wantRd = !outReviewDeaths.IsEmpty;

        for (int i = 0; i < n; i++)
        {
            float dmg = Math.Clamp(warDamage[i], 0f, 1f);
            float fd = food[i];
            float en = energy[i];
            float md = medicine[i];
            float coverage = fd * .6f + en * .25f + md * .15f;
            float scarcity = Math.Max(0f, 1f - coverage - scarcityTolerance);
            float capacity = hospitals[i] / Math.Max(.1f, pop[i] / 1e6f);
            float health = capacity / (1f + capacity) * md;

            float lit = literacy[i];
            float urb = urbanization[i];
            float wel = welfareSpending[i];
            float inf = inflation[i];
            float ws = warSupport[i];
            float nStab = nationStability[i];
            float sStab = stateStability[i];
            float rad = radicalism[i];
            float ys = youngShare[i];
            float os = oldShare[i];
            byte flg = flags[i];
            bool atWar = (flg & VitalFlagAtWar) != 0;
            bool ageEnabled = (flg & VitalFlagAgeEnabled) != 0;

            float lowLitB = (1f - lit) * lowLitBirth;
            float urbanSqueeze = urb > 0.55f ? -(urb - 0.55f) * 0.010f : 0f;
            float postIndustrial = lit > 0.82f && urb > 0.60f
                ? -(lit - 0.82f) * (urb - 0.60f) * 0.032f
                : 0f;
            float familyWelfare = urb > 0.01f && wel > 0.06f
                ? (wel - 0.06f) * 0.042f
                : 0f;
            float traditionBonus = urb > 0.01f && (flg & VitalFlagTraditionBonus) != 0 ? 0.0035f : 0f;
            float secularPenalty = urb > 0.01f && (flg & VitalFlagSecularPenalty) != 0 ? -0.0020f : 0f;
            float pronatalist = (flg & VitalFlagPronatalist) != 0 ? 0.0065f
                : (flg & VitalFlagDemoBoom) != 0 ? 0.0085f : 0f;
            float inflationDrag = urb > 0.01f && inf > 0.08f
                ? -Math.Clamp(inf - 0.08f, 0f, 0.30f) * 0.014f
                : 0f;

            float exhaustion = M.Clamp01(1f - ws);
            float mobShock = exhaustion > 0.08f ? -Math.Min(0.0075f, (exhaustion - 0.08f) * 0.02f) : 0f;
            if (atWar) mobShock -= 0.0020f;
            float warNestingDrag = dmg > 0.04f ? -Math.Min(0.006f, (dmg - 0.04f) * 0.012f) : 0f;
            float peaceBabyBoom = !atWar && nStab > 0.72f && dmg < 0.02f
                ? (nStab - 0.72f) * 0.008f
                : 0f;

            float preAgeBirth = Math.Clamp(birthBase + lowLitB + urbanSqueeze + postIndustrial +
                familyWelfare + traditionBonus + secularPenalty + pronatalist + inflationDrag +
                mobShock + warNestingDrag + peaceBabyBoom, 0f, .08f);
            float fertilityFactor = ageEnabled ? DemographyRulesHook.ManagedFertilityFactor(ys) : 1f;
            float ageBirthEffect = preAgeBirth * (fertilityFactor - 1f);
            float birth = Math.Clamp(preAgeBirth + ageBirthEffect, 0f, .08f);

            float povertyDeath = (1f - sol[i]) * .004f;
            float shortageD = scarcity * shortageDeath;
            float warDeath = dmg * .012f;
            float famineSpike = fd < 0.40f ? (0.40f - fd) * 0.045f : 0f;
            float winterExposure = MathF.Abs(capitalLat[i]) > 35f && en < 0.45f ? (0.45f - en) * 0.015f : 0f;
            float epidemicDeath = md < 0.35f && (dmg > 0.10f || scarcity > 0.15f) ? (0.35f - md) * 0.020f : 0f;
            float violenceDeath = rad > 0.65f && sStab < 0.25f ? (rad - 0.65f) * (0.25f - sStab) * 0.035f : 0f;

            float preHealthDeath = Math.Clamp(deathBase + povertyDeath + shortageD + warDeath +
                famineSpike + winterExposure + epidemicDeath + violenceDeath, 0f, .08f);
            float hospitalRelief = preHealthDeath * health * HouseholdEconomy.HospitalDeathRelief;
            float postHealthDeath = preHealthDeath - hospitalRelief;
            float mortalityFactor = ageEnabled ? DemographyRulesHook.ManagedMortalityFactor(os, refOld, mortOldWeight) : 1f;
            float oldAgeDeathEffect = postHealthDeath * (mortalityFactor - 1f);
            float death = Math.Clamp(postHealthDeath + oldAgeDeathEffect, 0f, .08f);

            float oldAgeBurden = M.Clamp01(
                Math.Max(0f, os - refOld) * 4.5f +
                (death > birth ? (death - birth) * 35f : 0f));

            outBirth[i] = birth;
            outDeath[i] = death;
            outOldBurden[i] = oldAgeBurden;

            if (wantRb || wantRd)
            {
                long rb = (long)Math.Round(pop[i] * (double)birth * interval);
                if (wantRb) outReviewBirths[i] = rb;
                if (wantRd) outReviewDeaths[i] = Math.Min(pop[i] + rb, (long)Math.Round(pop[i] * (double)death * interval));
            }
        }
    }
}
