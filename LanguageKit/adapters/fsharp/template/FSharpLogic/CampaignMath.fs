namespace LanguageKit.FSharp

/// Pure, deterministic sample: no Godot objects and no hidden random source.
type CampaignMath =
    static member WeightedScore(values: int64 array, weights: int64 array) : int64 =
        if isNull values then nullArg "values"
        if isNull weights then nullArg "weights"
        if values.Length <> weights.Length then invalidArg "weights" "Mismatched lengths."
        Array.fold2 (fun sum value weight -> Checked.(+) sum (Checked.(*) value weight)) 0L values weights

/// Instances are ordinary managed objects, owned by the C# Godot bridge.
type Counter() =
    let mutable count = 0L
    member _.Increment() =
        count <- Checked.(+) count 1L
        count
