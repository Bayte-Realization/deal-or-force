// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System

/// Value-only zero-allocation summary of a treaty annexation quota check.
[<Struct>]
type PeaceQuotaOutcome =
    { RemainingProvinces: int
      Affordable: bool
      RemainingBudget: single }

/// Zero-allocation value helpers for treaty budget & province-cap evaluation.
[<AbstractClass; Sealed>]
type PeaceQuotaRules private () =

    /// Checks province count & budget ceiling without allocating linked lists or decimals.
    static member EvaluateQuota(selectedProvinces: int, maxProvinces: int, cost: single, budget: single) : PeaceQuotaOutcome =
        let remainingCount = Math.Max(0, maxProvinces - selectedProvinces)
        let remainingBudget = budget - cost
        let ok = selectedProvinces <= maxProvinces && remainingBudget >= -0.001f && Single.IsFinite cost
        { RemainingProvinces = remainingCount
          Affordable = ok
          RemainingBudget = remainingBudget }

/// Legacy unreferenced prototype record (reference type; retained only for binary compatibility).
type LedgerEntry = {
    /// Entry identifier.
    Id: int
    /// UTC timestamp.
    Timestamp: DateTime
    /// Free-form summary string.
    Terms: string
}

/// Legacy unreferenced prototype union (allocates on heap; do not call on simulation hot paths).
type Demand =
    /// Annexation of a region.
    | Annexation of regionId: int
    /// Monetary payment demand.
    | FinancialPayment of amount: decimal

/// Legacy unreferenced prototype treaty container using F# heap list.
type Treaty = {
    /// Linked list of demands.
    Demands: Demand list
    /// Validity flag.
    IsValid: bool
}

/// Legacy unreferenced prototype validator.
module Validator =
    /// Counts annexation demands in a treaty list (<= 5).
    let validate (treaty: Treaty) : bool =
        let regionsCount = treaty.Demands |> List.filter (function | Annexation _ -> true | _ -> false) |> List.length
        regionsCount <= 5
