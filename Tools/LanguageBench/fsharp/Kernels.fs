/// The F# contender: the same three kernels, ported line by line from the C# the game runs
/// (Scripts/Simulation/FrontierField.cs sequential, BudgetCore.Evaluate as flat arrays, and
/// the ownership CSR walk of Tools/GodotLanguages/kernels/ownership/managed). Imperative on
/// purpose: an idiomatic rewrite would be a different algorithm, and a separate row.
module DealOrForce.LanguageBench.FSharpKernels

open System

[<Literal>]
let private Cap = 8.0f

[<Literal>]
let private Sea = 0us

[<Literal>]
let private Unowned = 65535us

let inline private minf (a: float32) (b: float32) = if a < b then a else b

let private currentHolder (authored: uint16) (province: int) (owners: uint16[]) (authoredOwners: uint16[]) =
    if province <= 0 || province >= owners.Length then authored
    else
        let owner = owners.[province]
        if province >= authoredOwners.Length || authoredOwners.[province] = owner then authored
        elif owner = 0us then Unowned
        else owner

let private distances (holders: uint16[]) (width: int) (height: int) =
    let d = Array.zeroCreate<float32> (width * height)
    for y in 0 .. height - 1 do
        let row = y * width
        for x in 0 .. width - 1 do
            let cell = row + x
            let here = holders.[cell]
            let frontier =
                holders.[row + (if x + 1 = width then 0 else x + 1)] <> here
                || holders.[row + (if x = 0 then width - 1 else x - 1)] <> here
                || (y > 0 && holders.[cell - width] <> here)
                || (y + 1 < height && holders.[cell + width] <> here)
            d.[cell] <- if frontier then 0.5f else Cap
    let straight = 1.0f
    let diagonal = 1.41421356f
    for y in 0 .. height - 1 do
        let row = y * width
        for x in 0 .. width - 1 do
            let cell = row + x
            let mutable best = d.[cell]
            if best > 0.5f then
                let west = row + (if x = 0 then width - 1 else x - 1)
                best <- minf best (d.[west] + straight)
                if y > 0 then
                    let up = cell - width
                    best <- minf best (d.[up] + straight)
                    best <- minf best (d.[row - width + (if x = 0 then width - 1 else x - 1)] + diagonal)
                    best <- minf best (d.[row - width + (if x + 1 = width then 0 else x + 1)] + diagonal)
                d.[cell] <- best
    for y in height - 1 .. -1 .. 0 do
        let row = y * width
        for x in width - 1 .. -1 .. 0 do
            let cell = row + x
            let mutable best = d.[cell]
            if best > 0.5f then
                let east = row + (if x + 1 = width then 0 else x + 1)
                best <- minf best (d.[east] + straight)
                if y + 1 < height then
                    let down = cell + width
                    best <- minf best (d.[down] + straight)
                    best <- minf best (d.[row + width + (if x = 0 then width - 1 else x - 1)] + diagonal)
                    best <- minf best (d.[row + width + (if x + 1 = width then 0 else x + 1)] + diagonal)
                d.[cell] <- minf best Cap
    d

let private smooth (field: float32[]) (width: int) (height: int) (sigma: float32) =
    if sigma > 0.0f then
        let radius = int (MathF.Ceiling(sigma * 3.0f))
        let weights = Array.zeroCreate<float32> (radius * 2 + 1)
        let mutable total = 0.0f
        for i in -radius .. radius do
            let w = MathF.Exp(float32 (-i * i) / (2.0f * sigma * sigma))
            weights.[i + radius] <- w
            total <- total + w
        for i in 0 .. weights.Length - 1 do
            weights.[i] <- weights.[i] / total
        let across = Array.zeroCreate<float32> field.Length
        for y in 0 .. height - 1 do
            let row = y * width
            for x in 0 .. width - 1 do
                let mutable sum = 0.0f
                for i in -radius .. radius do
                    let mutable column = x + i
                    if column < 0 then column <- column + width
                    elif column >= width then column <- column - width
                    sum <- sum + field.[row + column] * weights.[i + radius]
                across.[row + x] <- sum
        for y in 0 .. height - 1 do
            let row = y * width
            for x in 0 .. width - 1 do
                let mutable sum = 0.0f
                for i in -radius .. radius do
                    let line = Math.Clamp(y + i, 0, height - 1)
                    sum <- sum + across.[line * width + x] * weights.[i + radius]
                field.[row + x] <- sum

/// FrontierField.Build, single-threaded, line by line.
let frontierSequential (authoredHolders: uint16[]) (cellToProvince: uint16[]) (width: int) (height: int)
                       (owners: uint16[]) (authoredOwners: uint16[]) : byte[] =
    let cells = width * height
    let holders = Array.zeroCreate<uint16> cells
    for y in 0 .. height - 1 do
        let row = y * width
        for x in 0 .. width - 1 do
            let cell = row + x
            let authored = authoredHolders.[cell]
            holders.[cell] <-
                if authored = Sea then Sea
                else currentHolder authored (int cellToProvince.[cell]) owners authoredOwners
    let distance = distances holders width height
    smooth distance width height 1.0f
    let bytes = Array.zeroCreate<byte> cells
    let scale = 255.0f / Cap
    for y in 0 .. height - 1 do
        let row = y * width
        for x in 0 .. width - 1 do
            let value = distance.[row + x] * scale
            bytes.[row + x] <-
                if value <= 0.0f then 0uy
                elif value >= 255.0f then 255uy
                else byte (int (value + 0.5f))
    bytes

// ------------------------------------------------------------------------------- budget

let inline private max0 (x: float32) = if x > 0.0f then x elif Single.IsNaN x then x else 0.0f

let private scaleOf (lineFloats: float32[]) (offsets: int[]) (slotsIdx: int[]) (weights: float32[])
                    (slots: float32[]) (line: int) (slotBase: int) =
    let mutable scale = lineFloats.[line * 12 + 2]
    for k in offsets.[line] .. offsets.[line + 1] - 1 do
        scale <- scale + weights.[k] * slots.[slotBase + slotsIdx.[k]]
    scale

/// BudgetCore.Evaluate for every country over the flat layout of BudgetInput.
let budgetEvaluate (nations: int) (lines: int) (taxTolerance: float32) (taxEvasion: float32)
                   (lineFloats: float32[]) (lineInts: int[]) (factorOffsets: int[]) (factorSlots: int[])
                   (factorWeights: float32[]) (slots: float32[]) (nationFloats: float32[]) (rates: float32[])
                   (output: float32[]) =
    for c in 0 .. nations - 1 do
        let slotBase = c * 8
        let n = c * 8
        let rateBase = c * lines
        let weeklyOutput = nationFloats.[n]
        let compliance =
            if weeklyOutput <= 0.0f || taxEvasion <= 0.0f then 1.0f
            else
                let mutable burden = 0.0f
                for line in 0 .. lines - 1 do
                    if lineInts.[line * 4] = 0 && lineInts.[line * 4 + 1] = 0 then
                        let rate = rates.[rateBase + line]
                        burden <- burden + rate * lineFloats.[line * 12] * scaleOf lineFloats factorOffsets factorSlots factorWeights slots line slotBase
                1.0f / (1.0f + taxEvasion * max0 (burden - taxTolerance))
        let mutable revenue = 0.0f
        let mutable taxation = 0.0f
        let mutable expenses = 0.0f
        let mutable growth = 0.0f
        let mutable living = 0.0f
        let mutable literacy = 0.0f
        let mutable radicalism = 0.0f
        let mutable stability = 0.0f
        for line in 0 .. lines - 1 do
            let f = line * 12
            let side = lineInts.[line * 4]
            let charge = lineInts.[line * 4 + 1]
            let basis =
                match charge with
                | 1 -> nationFloats.[n + 1]
                | 2 -> nationFloats.[n + 2]
                | 3 -> nationFloats.[n + 3]
                | _ -> weeklyOutput
            let rate = rates.[rateBase + line]
            let lossYield = lineFloats.[f + 1]
            let yld = if not (Single.IsNaN lossYield) && basis < 0.0f then lossYield else lineFloats.[f]
            let mutable amount = basis * rate * yld * scaleOf lineFloats factorOffsets factorSlots factorWeights slots line slotBase
            if nationFloats.[n + 7] <> 0.0f && lineInts.[line * 4 + 3] <> 0 then
                amount <- amount + nationFloats.[n + 4] * nationFloats.[n + 5] / 52.0f
            if side = 0 && charge = 0 then amount <- amount * (compliance * nationFloats.[n + 6])
            amount <- if charge = 3 then amount else max0 amount
            if side = 0 then
                revenue <- revenue + amount
                if charge = 0 || charge = 1 then taxation <- taxation + amount
            else expenses <- expenses + amount
            if lineInts.[line * 4 + 2] <> 0 then
                let above = rate - lineFloats.[f + 3]
                growth <- growth + above * lineFloats.[f + 4]
                living <- living + above * lineFloats.[f + 5]
                literacy <- literacy + above * lineFloats.[f + 6]
                radicalism <- radicalism + above * lineFloats.[f + 7]
                stability <- stability + above * lineFloats.[f + 8]
                let resentedAbove = lineFloats.[f + 9]
                if resentedAbove > 0.0f then
                    let resented = max0 (rate - resentedAbove)
                    if resented > 0.0f then
                        living <- living + resented * lineFloats.[f + 10]
                        radicalism <- radicalism + resented * lineFloats.[f + 11]
        let o = c * 8
        output.[o] <- revenue
        output.[o + 1] <- taxation
        output.[o + 2] <- expenses
        output.[o + 3] <- growth
        output.[o + 4] <- living
        output.[o + 5] <- literacy
        output.[o + 6] <- radicalism
        output.[o + 7] <- stability

// ------------------------------------------------------------------------------- ownership

/// OwnershipComponentGraph.FillManaged, line by line. Returns (components, members).
let ownershipComponents (offsets: int[]) (neighbors: int[]) (owners: uint16[]) (water: byte[])
                        (labels: int[]) (members: int[]) (componentOffsets: int[]) : struct (int * int) =
    let vertexCount = offsets.Length - 1
    let mutable components = 0
    let mutable tail = 0
    for start in 1 .. vertexCount - 1 do
        if not (labels.[start] <> 0 || owners.[start] = 0us || water.[start] <> 0uy) then
            let owner = owners.[start]
            let mutable head = tail
            components <- components + 1
            labels.[start] <- components
            members.[tail] <- start
            tail <- tail + 1
            while head < tail do
                let vertex = members.[head]
                head <- head + 1
                for edge in offsets.[vertex] .. offsets.[vertex + 1] - 1 do
                    let next = neighbors.[edge]
                    if not (labels.[next] <> 0 || owners.[next] <> owner || water.[next] <> 0uy) then
                        labels.[next] <- components
                        members.[tail] <- next
                        tail <- tail + 1
            componentOffsets.[components] <- tail
    struct (components, tail)
