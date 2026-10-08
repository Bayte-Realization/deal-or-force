using System;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Shared accounting for negotiated independence and armed secession.
/// Region ownership changes do not themselves move national accounts.</summary>
internal static class RegionalResourcePartition
{
    internal static bool ValidAccounts(Nation parent) => parent is not null &&
        new[] { parent.Gdp, parent.Treasury, parent.ManpowerReserve, parent.PublicDebt,
            parent.DebtArrears, parent.PaymentArrears }.All(float.IsFinite);

    internal static void Transfer(Nation parent, Nation born, float share)
    {
        if (!ValidAccounts(parent) || !float.IsFinite(share) || share < 0 || share > 1)
            throw new InvalidOperationException("Invalid regional resource partition.");

        // The new government inherits physical productive capacity, not a fresh baseline
        // at today's prices. Otherwise a civil war could restart the GDP/output feedback.
        var capacity = ProductionCapacity.Of(parent);
        born.ProductionReferenceInitialized = true;
        born.ProductionReferencePopulation = born.Population;
        born.ProductionReferenceGdp = capacity * share;

        born.Gdp = parent.Gdp * share; parent.Gdp -= born.Gdp;
        born.ManpowerReserve = parent.ManpowerReserve * share; parent.ManpowerReserve -= born.ManpowerReserve;
        born.Treasury = Math.Max(0, parent.Treasury) * share; parent.Treasury -= born.Treasury;
        born.PublicDebt = parent.PublicDebt * share; parent.PublicDebt -= born.PublicDebt;
        born.DebtArrears = parent.DebtArrears * share; parent.DebtArrears -= born.DebtArrears;
        born.PaymentArrears = parent.PaymentArrears * share; parent.PaymentArrears -= born.PaymentArrears;
        born.DebtInitialized = parent.DebtInitialized;
        born.CreditConfidence = parent.CreditConfidence;
        born.DebtCondition = parent.DebtCondition;
        born.DebtMechanicActive = parent.DebtMechanicActive;
        born.DebtSpreadAnnual = parent.DebtSpreadAnnual;
        born.DebtMissedWeeks = parent.DebtMissedWeeks;
        born.DebtCrisisWeeks = parent.DebtCrisisWeeks;
        born.DebtRestructuringProgress = parent.DebtRestructuringProgress;
        born.DebtRestructuringLastDay = parent.DebtRestructuringLastDay;
    }

    internal static void RecountResidents(Campaign c, Nation parent, Nation born)
    {
        born.Population = c.StatesOf(born).Sum(s => s.Population);
        parent.Population = c.StatesOf(parent).Sum(s => s.Population);
    }
}
