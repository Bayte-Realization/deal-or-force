namespace DealOrForce.Rules

type LedgerEntry = {
    Id: int
    Timestamp: System.DateTime
    Terms: string
}

type Demand =
    | Annexation of regionId: int
    | FinancialPayment of amount: decimal

type Treaty = {
    Demands: Demand list
    IsValid: bool
}

module Validator =
    let validate (treaty: Treaty) : bool =
        let regionsCount = treaty.Demands |> List.filter (function | Annexation _ -> true | _ -> false) |> List.length
        regionsCount <= 5
