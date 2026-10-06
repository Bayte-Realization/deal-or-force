namespace ExampleFSharpMod

open DealOrForce.Modding

// Explicit examples, not automatic campaign mutations. Each returns queue acceptance.
type GovernanceHelpers =
    static member Borrow(context: ModContext, tag: string, amount: double) =
        context.ManageDebt(tag, "borrow", amount)
    static member EnactLaw(context: ModContext, tag: string, policy: string) =
        context.EnactPolicy(tag, policy, "", 0, false)
    static member CourseShock(context: ModContext, tag: string, strength: double, reason: string) =
        context.AddCourseShock(tag, strength, reason)

// Same language-neutral managed contract as C# mods. No Godot inheritance required.
type ExampleMod() =
    interface IGlobalityMod with
        member _.OnLoad(context: ModContext) =
            context.Log("F# corecode loaded")

        member _.OnCampaignCreated(context: ModContext) =
            context.Log("F# campaign ready: " + context.Date)

        member _.OnDayAdvanced(context: ModContext) =
            // Observation-only example: does not silently rebalance a player's campaign.
            if context.Day % 30 = 0 then
                context.Log("F# monthly observation: " + context.Date)
