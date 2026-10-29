// SPDX-License-Identifier: GPL-3.0-or-later
namespace DealOrForce.Rules

open System
open System.Collections.Generic

/// Pure deterministic peace boundary, territorial recovery and graph arbitration rules.
/// No Godot, IO, RNG or reference to mutable campaign state.
[<AbstractClass; Sealed>]
type PeaceRules private () =

    /// Computes the compactness factor for adding a region to a treaty.
    /// Broad shared borders yield compact frontiers, while single-point connections are penalized.
    static member CompactnessScore(sharedEdges: int, totalEdges: int, compactnessWeight: single) : single =
        let w = MathF.Max(0.0f, compactnessWeight)
        let denom = MathF.Max(1.0f, single totalEdges)
        0.35f + w * (single sharedEdges) / denom

    /// Determines whether an isolated pocket of ground is small enough relative to the main
    /// national body that it should fall together with the surrounding land.
    static member IsPocket(pocketPopulation: int64, mainPopulation: int64, pocketShare: single) : bool =
        let limit = single mainPopulation * pocketShare
        (single pocketPopulation) <= limit

    /// Tests whether removing a region from a chosen set leaves all remaining regions
    /// still connected to the winner's anchors.
    static member IsLeaf(adjacency: Func<int, seq<int>>,
                         chosen: ISet<int>,
                         anchors: ISet<int>,
                         region: int) : bool =
        if chosen.Count <= 1 then true
        else
            let rest = HashSet<int>()
            for x in chosen do
                if x <> region then rest.Add(x) |> ignore

            if rest.Count = 0 then true
            else
                let reached = HashSet<int>()
                let queue = Queue<int>()

                for start in rest do
                    if anchors.Contains(start) && reached.Add(start) then
                        queue.Enqueue(start)

                while queue.Count > 0 do
                    let curr = queue.Dequeue()
                    let nexts = adjacency.Invoke(curr)
                    if not (isNull nexts) then
                        for n in nexts do
                            if rest.Contains(n) && reached.Add(n) then
                                queue.Enqueue(n)

                reached.Count = rest.Count

    /// Identifies the connected components of a subset of nodes in a graph.
    static member FindParts(adjacency: Func<int, seq<int>>, nodes: seq<int>) : int array array =
        if isNull nodes then Array.empty
        else
            let pool = HashSet<int>(nodes)
            let seen = HashSet<int>()
            let parts = ResizeArray<int array>()

            for start in pool do
                if seen.Add(start) then
                    let part = ResizeArray<int>()
                    part.Add(start)
                    let queue = Queue<int>()
                    queue.Enqueue(start)

                    while queue.Count > 0 do
                        let curr = queue.Dequeue()
                        let nexts = adjacency.Invoke(curr)
                        if not (isNull nexts) then
                            for n in nexts do
                                if pool.Contains(n) && seen.Add(n) then
                                    part.Add(n)
                                    queue.Enqueue(n)
                    parts.Add(part.ToArray())

            parts.ToArray()

    /// The stalemate discount a long, tiring or already-stalled war applies to the winner's
    /// resolve over an achievable war aim.
    ///
    /// Pulled out of `PeaceNegotiation.WarAimResolve` as a pure function of five numbers,
    /// because that is what it always was: time since the war began, whether the term on the
    /// table is a return to the status quo, whether the front has stalled, how weary both
    /// sides are, and how much of the responder's ground is occupied. The surrounding method
    /// still decides *whether* the penalty applies at all - that needs the campaign - but the
    /// arithmetic is here, where a wrong sign or a missing clamp is a compile-time absence
    /// rather than a number nobody notices for a year.
    ///
    /// Float32 throughout, in the same order as the C# it replaces: a differential harness
    /// compares the two over a grid and requires exact equality, so the order matters.
    static member StalemateDiscount(durationDays: int,
                                    statusQuo: bool,
                                    stalled: bool,
                                    weariness: single,
                                    responderOccupied: single) : single =
        let mutable discount = 0.0f
        if durationDays > 365 then
            let years = single (durationDays - 365) / 365.0f
            discount <-
                if statusQuo then MathF.Min(0.85f, years * 0.35f)
                else MathF.Min(0.60f, years * 0.20f)
        if stalled then
            discount <- MathF.Max(discount, if statusQuo then 0.65f else 0.40f)
        let wearinessDiscount = weariness / 100.0f * 0.5f
        discount <- MathF.Min(0.90f, discount + wearinessDiscount)
        if statusQuo && responderOccupied > 0.02f then
            discount <- MathF.Max(discount, 0.50f + MathF.Min(0.40f, responderOccupied))
        MathF.Max(0.0f, discount)

    /// Evaluates the strategic desirability of claiming a region in peace demands.
    /// aim: 0 = People, 1 = Sea, 2 = Buffer, 3 = Wealth.
    static member EvaluateRegionValue(population: int64,
                                      provinceCount: int,
                                      heldShare: single,
                                      oursShare: single,
                                      isHomeland: bool,
                                      isCoastal: bool,
                                      isLandlockedWinner: bool,
                                      isNearCapital: bool,
                                      aim: int,
                                      builtCount: int,
                                      isGoal: bool,
                                      rebelWeight: single,
                                      isEnemyCapital: bool,
                                      roll: single) : single =
        let perProvince = (single population) / MathF.Max(1.0f, single provinceCount)
        let mutable v = ((single population) / 1000000.0f + 0.05f) * (0.20f + perProvince / 100000.0f) * (0.35f + heldShare)

        let peopleMul = if aim = 0 then 4.0f else 2.0f
        v <- v * (1.0f + peopleMul * oursShare)

        if isHomeland then v <- v * 2.2f
        if isCoastal then
            let coastalMul =
                if isLandlockedWinner then 3.0f
                elif aim = 1 then 2.2f
                else 1.15f
            v <- v * coastalMul

        if isNearCapital then
            let bufferMul = if aim = 2 then 2.2f else 1.25f
            v <- v * bufferMul

        if aim = 3 then
            let popM = MathF.Max(1.0f, (single population) / 1000000.0f)
            let builtRatio = (single builtCount) / popM * 0.1f
            v <- v * (0.6f + MathF.Min(2.0f, builtRatio))

        if isGoal then v <- v * 5.0f
        if rebelWeight > 0.0f then v <- v * rebelWeight
        if aim = 0 && oursShare > 0.5f && isEnemyCapital then v <- v * 3.0f

        v * (0.8f + 0.45f * roll)
