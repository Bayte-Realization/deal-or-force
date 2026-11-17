using System;
using System.Runtime.InteropServices;

namespace DealOrForce.Tools.KernelBench;

/// <summary>
/// (f) The Rust kernel (Tools/KernelBench/native) through a C ABI and LibraryImport: the whole
/// week in one call, every array pinned for the duration and passed by pointer. Two builds of
/// the same source: x86-64 baseline (SSE2, what a DLL shipped to every PC could assume) and
/// x86-64-v3 (AVX2, what Vector256 in C# uses on this machine).
/// </summary>
internal static unsafe partial class NativeKernel
{
    [StructLayout(LayoutKind.Sequential)]
    public struct KernelArgs
    {
        public int StateCount, GroupCount, Threads, Pad;
        public int* GroupStart, GroupLen;
        public byte* Due;
        public float* Share, BuildingJobs, WarDamage, Annual, Food, Energy, Medicine, NationLiving, StateRadicalism, PopulationF;
        public double* PopulationD;
        public float* NonBuildingJobs, StateLiving, EmploymentTarget, BasicNeeds, Stress;
        public float* SizeF;
        public double* SizeD, ClassFactor;
        public float* Anchor;
        public float* Employment, Wealth, Living, Radicalism;
        public float Interval, Tolerance, EmploymentRate, WealthRate;
    }

    [LibraryImport("household_kernel_v1", EntryPoint = "dof_household_abi")]
    public static partial uint AbiV1();

    [LibraryImport("household_kernel_v1", EntryPoint = "dof_household_review_v1")]
    private static partial int ReviewV1(KernelArgs* args);

    [LibraryImport("household_kernel_v3", EntryPoint = "dof_household_abi")]
    public static partial uint AbiV3();

    [LibraryImport("household_kernel_v3", EntryPoint = "dof_household_review_v1")]
    private static partial int ReviewV3(KernelArgs* args);

    /// <summary>Pins, fills the argument block, calls once. The pinning is inside the timed call.</summary>
    public static int Run(HouseholdSoa d, bool avx2, int threads)
    {
        fixed (int* groupStart = d.GroupStart, groupLen = d.GroupLen)
        fixed (byte* due = d.Due)
        fixed (float* share = d.Share, buildingJobs = d.BuildingJobs, warDamage = d.WarDamage, annual = d.Annual,
               food = d.Food, energy = d.Energy, medicine = d.Medicine, nationLiving = d.NationLiving,
               stateRadicalism = d.StateRadicalism, populationF = d.PopulationF)
        fixed (double* populationD = d.PopulationD, sizeD = d.SizeD, classFactor = d.ClassFactor)
        fixed (float* nbj = d.NonBuildingJobs, stateLiving = d.StateLiving, target = d.EmploymentTarget,
               basicNeeds = d.BasicNeeds, stress = d.Stress, sizeF = d.SizeF, anchor = d.Anchor,
               employment = d.Employment, wealth = d.Wealth, living = d.Living, radicalism = d.Radicalism)
        {
            var args = new KernelArgs
            {
                StateCount = d.StateCount, GroupCount = d.GroupCount, Threads = threads,
                GroupStart = groupStart, GroupLen = groupLen, Due = due,
                Share = share, BuildingJobs = buildingJobs, WarDamage = warDamage, Annual = annual,
                Food = food, Energy = energy, Medicine = medicine, NationLiving = nationLiving,
                StateRadicalism = stateRadicalism, PopulationF = populationF, PopulationD = populationD,
                NonBuildingJobs = nbj, StateLiving = stateLiving, EmploymentTarget = target,
                BasicNeeds = basicNeeds, Stress = stress,
                SizeF = sizeF, SizeD = sizeD, ClassFactor = classFactor, Anchor = anchor,
                Employment = employment, Wealth = wealth, Living = living, Radicalism = radicalism,
                Interval = d.Interval, Tolerance = d.Tolerance, EmploymentRate = d.EmploymentRate, WealthRate = d.WealthRate,
            };
            return avx2 ? ReviewV3(&args) : ReviewV1(&args);
        }
    }
}
