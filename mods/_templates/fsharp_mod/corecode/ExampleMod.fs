namespace ExampleFSharpMod

open DealOrForce.Modding

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
