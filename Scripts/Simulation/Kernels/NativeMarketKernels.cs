// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Batched native SIMD market price-setting and national output clearing wrappers
/// backed by <c>dof_kernels</c> (<c>dof_market_*</c>) with bit-exact pure C# fallbacks.
/// </summary>
public static partial class NativeMarketKernels
{
    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_market_settle_prices_v1(
        uint goodCount,
        uint nationCount,
        uint nationStride,
        float* basePrice,
        byte* tradedWorldPrice,
        float* demand,
        float* production,
        float* imports,
        float* relieved,
        byte adjustPrice,
        float importMarkupDef,
        float reliefDef,
        float priceAdjustSpeed,
        float* satisfaction,
        float* price,
        float* outWorldScarcity);

    [LibraryImport(NativeKernelsLoader.LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe partial int dof_market_clear_nations_v1(
        uint nationCount,
        uint goodCount,
        uint goodStride,
        float* basePrice,
        float* essentialWeight,
        float* tariffRate,
        float* production,
        float* demand,
        float* imports,
        float* exports,
        float* relieved,
        float* satisfaction,
        float* price,
        float marketTradeShare,
        float gdpPriceShare,
        float gdpPriceBand,
        float* outGrossOutput,
        float* outRealOutput,
        float* outShortageIndex,
        float* outMarketAccess,
        float* outWeeklyTradeBalance,
        float* outWeeklyTariffIncome);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp(float v, float lo, float hi)
        => v < lo ? lo : (v > hi ? hi : v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp01(float v) => Clamp(v, 0f, 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Max0(float v) => v > 0f ? v : (float.IsNaN(v) ? v : 0f);

    /// <summary>
    /// Clears batched market prices across <paramref name="goodCount"/> goods x <paramref name="nationCount"/> nations.
    /// Layout: <c>[good * nationStride + nationIdx]</c>.
    /// </summary>
    public static bool SettlePrices(
        int goodCount,
        int nationCount,
        int nationStride,
        ReadOnlySpan<float> basePrice,
        ReadOnlySpan<byte> tradedWorldPrice,
        ReadOnlySpan<float> demand,
        ReadOnlySpan<float> production,
        ReadOnlySpan<float> imports,
        ReadOnlySpan<float> relieved,
        bool adjustPrice,
        float importMarkupDef,
        float reliefDef,
        float priceAdjustSpeed,
        Span<float> satisfaction,
        Span<float> price,
        Span<float> outWorldScarcity = default)
    {
        if (NativeKernelsLoader.EnableMarketDefault &&
            TryNativeSettlePrices(
                goodCount, nationCount, nationStride, basePrice, tradedWorldPrice,
                demand, production, imports, relieved, adjustPrice, importMarkupDef,
                reliefDef, priceAdjustSpeed, satisfaction, price, outWorldScarcity))
        {
            return true;
        }

        ManagedSettlePrices(
            goodCount, nationCount, nationStride, basePrice, tradedWorldPrice,
            demand, production, imports, relieved, adjustPrice, importMarkupDef,
            reliefDef, priceAdjustSpeed, satisfaction, price, outWorldScarcity);
        return false;
    }

    public static unsafe bool TryNativeSettlePrices(
        int goodCount,
        int nationCount,
        int nationStride,
        ReadOnlySpan<float> basePrice,
        ReadOnlySpan<byte> tradedWorldPrice,
        ReadOnlySpan<float> demand,
        ReadOnlySpan<float> production,
        ReadOnlySpan<float> imports,
        ReadOnlySpan<float> relieved,
        bool adjustPrice,
        float importMarkupDef,
        float reliefDef,
        float priceAdjustSpeed,
        Span<float> satisfaction,
        Span<float> price,
        Span<float> outWorldScarcity = default)
    {
        if (goodCount < 0 || nationCount < 0 || nationStride < nationCount) return false;
        if (goodCount == 0 || nationCount == 0) return true;
        long total = (long)goodCount * nationStride;
        if (total > int.MaxValue ||
            basePrice.Length < goodCount ||
            tradedWorldPrice.Length < goodCount ||
            demand.Length < total ||
            production.Length < total ||
            imports.Length < total ||
            relieved.Length < total ||
            satisfaction.Length < total ||
            price.Length < total ||
            (!outWorldScarcity.IsEmpty && outWorldScarcity.Length < goodCount))
        {
            return false;
        }
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapMarket)) return false;

        fixed (float* pBp = basePrice)
        fixed (byte* pTwp = tradedWorldPrice)
        fixed (float* pDem = demand)
        fixed (float* pProd = production)
        fixed (float* pImp = imports)
        fixed (float* pRel = relieved)
        fixed (float* pSat = satisfaction)
        fixed (float* pPr = price)
        fixed (float* pWs = outWorldScarcity)
        {
            return dof_market_settle_prices_v1(
                (uint)goodCount, (uint)nationCount, (uint)nationStride,
                pBp, pTwp, pDem, pProd, pImp, pRel,
                (byte)(adjustPrice ? 1 : 0),
                importMarkupDef, reliefDef, priceAdjustSpeed,
                pSat, pPr, outWorldScarcity.IsEmpty ? null : pWs) == 0;
        }
    }

    public static void ManagedSettlePrices(
        int goodCount,
        int nationCount,
        int nationStride,
        ReadOnlySpan<float> basePrice,
        ReadOnlySpan<byte> tradedWorldPrice,
        ReadOnlySpan<float> demand,
        ReadOnlySpan<float> production,
        ReadOnlySpan<float> imports,
        ReadOnlySpan<float> relieved,
        bool adjustPrice,
        float importMarkupDef,
        float reliefDef,
        float priceAdjustSpeed,
        Span<float> satisfaction,
        Span<float> price,
        Span<float> outWorldScarcity = default)
    {
        if (goodCount < 0 || nationCount < 0 || nationStride < nationCount)
            throw new ArgumentOutOfRangeException(nameof(nationStride));
        if (goodCount == 0 || nationCount == 0) return;

        long total = (long)goodCount * nationStride;
        if (total > int.MaxValue ||
            basePrice.Length < goodCount ||
            tradedWorldPrice.Length < goodCount ||
            demand.Length < total ||
            production.Length < total ||
            imports.Length < total ||
            relieved.Length < total ||
            satisfaction.Length < total ||
            price.Length < total ||
            (!outWorldScarcity.IsEmpty && outWorldScarcity.Length < goodCount))
        {
            throw new ArgumentException("Mismatched span lengths for ManagedSettlePrices.");
        }

        var adjustSpeed = Clamp01(priceAdjustSpeed);

        for (int g = 0; g < goodCount; g++)
        {
            int row = g * nationStride;
            float totalDemand = 0f;
            float totalProduction = 0f;
            float wantedImports = 0f;
            float spareExports = 0f;

            for (int i = 0; i < nationCount; i++)
            {
                float d = demand[row + i];
                float p = production[row + i];
                totalDemand += d;
                totalProduction += p;
                if (d > p) wantedImports += d - p;
                else spareExports += p - d;
            }

            totalDemand = Max0(totalDemand);
            if (totalDemand < 0.01f) totalDemand = 0.01f;
            totalProduction = Max0(totalProduction);
            if (totalProduction < 0.01f) totalProduction = 0.01f;

            float worldScarcity;
            if (tradedWorldPrice[g] != 0)
            {
                float wi = wantedImports > 0.01f ? wantedImports : (float.IsNaN(wantedImports) ? wantedImports : 0.01f);
                float se = spareExports > 0.01f ? spareExports : (float.IsNaN(spareExports) ? spareExports : 0.01f);
                worldScarcity = Clamp(wi / se, 0.55f, 2.75f);
            }
            else
            {
                worldScarcity = Clamp(totalDemand / totalProduction, 0.55f, 2.75f);
            }
            if (!outWorldScarcity.IsEmpty) outWorldScarcity[g] = worldScarcity;

            float bp = basePrice[g];
            for (int i = 0; i < nationCount; i++)
            {
                int idx = row + i;
                float d = demand[idx];
                float p = production[idx];
                float imp = imports[idx];
                float rel = relieved[idx];

                float sat = d <= 0.001f ? 1.0f : Clamp01((p + imp) / d);
                satisfaction[idx] = sat;

                float localScarcity = 1.0f + (1.0f - sat) * 1.40f;
                float importMarkup = imp > 0.001f
                    ? 1.0f + (importMarkupDef - 1.0f) * (1.0f - Clamp01(reliefDef * Clamp01(rel / imp)))
                    : 1.0f;
                float targetPrice = bp * worldScarcity * localScarcity * importMarkup;
                if (adjustPrice)
                {
                    float cur = price[idx];
                    float from = cur <= 0f ? bp : cur;
                    price[idx] = from + (targetPrice - from) * adjustSpeed;
                }
                else
                {
                    price[idx] = targetPrice;
                }
            }
        }
    }

    /// <summary>
    /// Clears batched national output/shortage/trade metrics across <paramref name="nationCount"/> nations x <paramref name="goodCount"/> goods.
    /// Layout: <c>[nation * goodStride + goodIdx]</c>.
    /// </summary>
    public static bool ClearNations(
        int nationCount,
        int goodCount,
        int goodStride,
        ReadOnlySpan<float> basePrice,
        ReadOnlySpan<float> essentialWeight,
        ReadOnlySpan<float> tariffRate,
        ReadOnlySpan<float> production,
        ReadOnlySpan<float> demand,
        ReadOnlySpan<float> imports,
        ReadOnlySpan<float> exports,
        ReadOnlySpan<float> relieved,
        ReadOnlySpan<float> satisfaction,
        ReadOnlySpan<float> price,
        float marketTradeShare,
        float gdpPriceShare,
        float gdpPriceBand,
        Span<float> outGrossOutput,
        Span<float> outRealOutput,
        Span<float> outShortageIndex,
        Span<float> outMarketAccess,
        Span<float> outWeeklyTradeBalance,
        Span<float> outWeeklyTariffIncome)
    {
        if (NativeKernelsLoader.EnableMarketDefault &&
            TryNativeClearNations(
                nationCount, goodCount, goodStride, basePrice, essentialWeight, tariffRate,
                production, demand, imports, exports, relieved, satisfaction, price,
                marketTradeShare, gdpPriceShare, gdpPriceBand,
                outGrossOutput, outRealOutput, outShortageIndex, outMarketAccess,
                outWeeklyTradeBalance, outWeeklyTariffIncome))
        {
            return true;
        }

        ManagedClearNations(
            nationCount, goodCount, goodStride, basePrice, essentialWeight, tariffRate,
            production, demand, imports, exports, relieved, satisfaction, price,
            marketTradeShare, gdpPriceShare, gdpPriceBand,
            outGrossOutput, outRealOutput, outShortageIndex, outMarketAccess,
            outWeeklyTradeBalance, outWeeklyTariffIncome);
        return false;
    }

    public static unsafe bool TryNativeClearNations(
        int nationCount,
        int goodCount,
        int goodStride,
        ReadOnlySpan<float> basePrice,
        ReadOnlySpan<float> essentialWeight,
        ReadOnlySpan<float> tariffRate,
        ReadOnlySpan<float> production,
        ReadOnlySpan<float> demand,
        ReadOnlySpan<float> imports,
        ReadOnlySpan<float> exports,
        ReadOnlySpan<float> relieved,
        ReadOnlySpan<float> satisfaction,
        ReadOnlySpan<float> price,
        float marketTradeShare,
        float gdpPriceShare,
        float gdpPriceBand,
        Span<float> outGrossOutput,
        Span<float> outRealOutput,
        Span<float> outShortageIndex,
        Span<float> outMarketAccess,
        Span<float> outWeeklyTradeBalance,
        Span<float> outWeeklyTariffIncome)
    {
        if (nationCount < 0 || goodCount < 0 || goodStride < goodCount) return false;
        if (nationCount == 0 || goodCount == 0) return true;
        long total = (long)nationCount * goodStride;
        if (total > int.MaxValue ||
            basePrice.Length < goodCount ||
            essentialWeight.Length < goodCount ||
            tariffRate.Length < nationCount ||
            production.Length < total ||
            demand.Length < total ||
            imports.Length < total ||
            exports.Length < total ||
            relieved.Length < total ||
            satisfaction.Length < total ||
            price.Length < total ||
            outGrossOutput.Length < nationCount ||
            outRealOutput.Length < nationCount ||
            outShortageIndex.Length < nationCount ||
            outMarketAccess.Length < nationCount ||
            outWeeklyTradeBalance.Length < nationCount ||
            outWeeklyTariffIncome.Length < nationCount)
        {
            return false;
        }
        if (!NativeKernelsLoader.HasCapability(NativeKernelsLoader.CapMarket)) return false;

        fixed (float* pBp = basePrice)
        fixed (float* pEw = essentialWeight)
        fixed (float* pTr = tariffRate)
        fixed (float* pProd = production)
        fixed (float* pDem = demand)
        fixed (float* pImp = imports)
        fixed (float* pExp = exports)
        fixed (float* pRel = relieved)
        fixed (float* pSat = satisfaction)
        fixed (float* pPr = price)
        fixed (float* pGo = outGrossOutput)
        fixed (float* pRo = outRealOutput)
        fixed (float* pSi = outShortageIndex)
        fixed (float* pMa = outMarketAccess)
        fixed (float* pTb = outWeeklyTradeBalance)
        fixed (float* pTi = outWeeklyTariffIncome)
        {
            return dof_market_clear_nations_v1(
                (uint)nationCount, (uint)goodCount, (uint)goodStride,
                pBp, pEw, pTr, pProd, pDem, pImp, pExp, pRel, pSat, pPr,
                marketTradeShare, gdpPriceShare, gdpPriceBand,
                pGo, pRo, pSi, pMa, pTb, pTi) == 0;
        }
    }

    public static void ManagedClearNations(
        int nationCount,
        int goodCount,
        int goodStride,
        ReadOnlySpan<float> basePrice,
        ReadOnlySpan<float> essentialWeight,
        ReadOnlySpan<float> tariffRate,
        ReadOnlySpan<float> production,
        ReadOnlySpan<float> demand,
        ReadOnlySpan<float> imports,
        ReadOnlySpan<float> exports,
        ReadOnlySpan<float> relieved,
        ReadOnlySpan<float> satisfaction,
        ReadOnlySpan<float> price,
        float marketTradeShare,
        float gdpPriceShare,
        float gdpPriceBand,
        Span<float> outGrossOutput,
        Span<float> outRealOutput,
        Span<float> outShortageIndex,
        Span<float> outMarketAccess,
        Span<float> outWeeklyTradeBalance,
        Span<float> outWeeklyTariffIncome)
    {
        if (nationCount < 0 || goodCount < 0 || goodStride < goodCount)
            throw new ArgumentOutOfRangeException(nameof(goodStride));
        if (nationCount == 0 || goodCount == 0) return;

        long total = (long)nationCount * goodStride;
        if (total > int.MaxValue ||
            basePrice.Length < goodCount ||
            essentialWeight.Length < goodCount ||
            tariffRate.Length < nationCount ||
            production.Length < total ||
            demand.Length < total ||
            imports.Length < total ||
            exports.Length < total ||
            relieved.Length < total ||
            satisfaction.Length < total ||
            price.Length < total ||
            outGrossOutput.Length < nationCount ||
            outRealOutput.Length < nationCount ||
            outShortageIndex.Length < nationCount ||
            outMarketAccess.Length < nationCount ||
            outWeeklyTradeBalance.Length < nationCount ||
            outWeeklyTariffIncome.Length < nationCount)
        {
            throw new ArgumentException("Mismatched span lengths for ManagedClearNations.");
        }

        float priceShare = Clamp01(gdpPriceShare);
        float band = Max0(gdpPriceBand);
        float bandLow = Max0(1.0f - band);
        float bandHigh = 1.0f + band;

        float totalWeight = 0f;
        for (int g = 0; g < goodCount; g++) totalWeight += essentialWeight[g];

        for (int ni = 0; ni < nationCount; ni++)
        {
            int row = ni * goodStride;
            float tariff = tariffRate[ni];
            float shortages = 0f;
            float trade = 0f;
            float tariffs = 0f;
            float grossOutput = 0f;
            float real = 0f;

            for (int g = 0; g < goodCount; g++)
            {
                int idx = row + g;
                float prod = production[idx];
                float dem = demand[idx];
                float imp = imports[idx];
                float exp = exports[idx];
                float rel = relieved[idx];
                float sat = satisfaction[idx];
                float pr = price[idx];
                float bp = basePrice[g];
                float ew = essentialWeight[g];

                shortages += (1.0f - sat) * ew;
                trade += (exp - imp) * pr;
                tariffs += Max0(imp - rel) * pr * tariff;

                float clampedP = Clamp(pr, bp * bandLow, bp * bandHigh);
                float domestic = Max0(dem - imp) + exp;
                float sold = prod < domestic
                    ? prod
                    : (float.IsNaN(prod) || float.IsNaN(domestic) ? float.NaN : domestic);
                grossOutput += sold * (bp + priceShare * (clampedP - bp));
                real += prod * bp;
            }

            float shortageIdx = totalWeight <= 0f ? 0f : Clamp01(shortages / totalWeight);
            outGrossOutput[ni] = grossOutput;
            outRealOutput[ni] = real;
            outShortageIndex[ni] = shortageIdx;
            outMarketAccess[ni] = 1.0f - shortageIdx;
            outWeeklyTradeBalance[ni] = trade * marketTradeShare;
            outWeeklyTariffIncome[ni] = tariffs * marketTradeShare;
        }
    }
}
