using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// One thing a movement does, given by its player or by its AI. The same command for
/// both, so a player cannot do anything the AI may not, and it crosses the wire in multiplayer
/// like every other order.
/// </summary>
public sealed class MovementActionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string Action { get; init; } = "";
    public int StateMapIndex { get; init; } = -1;
    public int TargetNationId { get; init; } = -1;

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation?.Movement is null) return;
        if (MovementActions.Refusal(campaign, nation, Action, StateMapIndex, TargetNationId).Length > 0) return;
        MovementActions.Do(campaign, nation, Action, StateMapIndex, TargetNationId);
    }
}

/// <summary>
/// The actions, what each costs, and why it cannot be taken. Every action but moving waits
/// for its own cooldown; the regional ones wait region by region.
/// </summary>
public static class MovementActions
{
    /// <summary>One action: its id, what it needs (a region, a country, neither) and which trait it belongs to.</summary>
    public sealed record Kind(string Id, string Name, string Help, bool NeedsRegion, bool NeedsNation, string Trait = null);

    public static readonly IReadOnlyList<Kind> Kinds = new Kind[]
    {
        new("move", "Move the headquarters", "Take the headquarters to another country that will have it. A month on the road; the heat stays behind.", true, false),
        new("campaign", "Campaign", "Organise, preach and hand out leaflets among its peoples in a region: support rises there, and so does the host's temper.", true, false),
        new("fundraise", "Fundraise", "Pass the hat round the whole following: two months of donations at once, and a little standing spent asking.", false, false),
        new("recruit", "Recruit", "Pay to train organisers and volunteers: cadres, which uprisings are made of.", false, false),
        new("recognition", "Seek recognition", "Ask a country to recognise it. One that thinks well enough of it does, and the movement's standing rises with every one.", false, true),
        new("rise", "Rise up", "Call the following into the streets in a region. If it succeeds, the region is its own: a capital, militia, and a war with the country it rose against.", true, false),
        new("referendum", "Negotiate referendum", "Seek a peaceful vote in the homeland. Requires a majority of residents, standing and the parent's consent; no militia or automatic war.", true, false, "civic_independence"),
        new("infiltrate", "Send fighters in", "Smuggle fighters out of the headquarters into a region where its people will hide them. They take days on the road by distance, and an uprising there is stronger for every one that arrives.", true, false),
        new("call_to_arms", "Call to arms", "Hand out rifles in a region it holds: militia out of its supporters there, the more the more of them there are.", true, false),
        new("lobby", "Lobby the host", "Friends in the host's parliament and press: the host thinks better of it and its temper cools.", false, false, "diaspora_lobby"),
        new("patrols", "Street patrols", "Patrols in its own neighbourhoods of the host country: support rises among its peoples there, and so does the heat.", false, false, "street_patrols"),
        new("hospital", "Field hospital", "Open a hospital in a region: its people live a little better, and the order's standing rises.", true, false, "hospitallers"),
        new("appeal", "Appeal to the world", "Speak at every forum that will listen: standing rises, and the free countries warm to the cause.", false, false, "government_in_exile"),
        new("contract", "Take a contract", "Hire its men out to a country at war that will pay: money every week for half a year.", false, true, "for_hire"),
        new("services", "Social services", "Schools, clinics and food for the poor in a region: support rises twice as fast as a campaign, and the region lives a little better.", true, false, "charity_network"),
        new("strike", "General strike", "Call the whole following out of work in the country where most of it lives: six weeks in which its economy produces less and its government loses standing. A government of the people tends to give ground after it; a hard one cracks down.", false, false),
        new("march", "March on the capital", "Fill the streets of a capital whose region follows it. A weak government falls - a political movement takes power, a national one wins the hearing it marched for. A strong one breaks the march up and the movement loses its organisers.", false, false),
    };

    public static Kind Of(string id) => Kinds.FirstOrDefault(kind => string.Equals(kind.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The actions this movement has: the common ones and its traits' own.</summary>
    public static IEnumerable<Kind> For(Nation nation, Campaign campaign = null)
    {
        var definition = Movements.DefinitionOf(nation);
        if (definition is null) yield break;
        foreach (var kind in Kinds)
            // A rising is offered to a movement that stages them by nature - refused, with the
            // reason, while it is not yet under arms - and to one its own radicalism has
            // brought to arms. Never to a company that only takes contracts.
            if ((kind.Trait is null || definition.Has(kind.Trait)) &&
                (kind.Id != "rise" || definition.CanStageUprising || MovementMethods.WillRise(campaign, nation, definition)))
                yield return kind;
    }

    /// <summary>What it costs now, in billions.</summary>
    public static float Cost(Campaign campaign, Nation nation, string action, int stateMapIndex)
    {
        var defines = campaign.Defines;
        var region = campaign.States.Find(state => state.MapIndex == stateMapIndex);
        var size = region is null ? 1f : MathF.Max(0.3f, MathF.Sqrt(region.Population / 1_000_000f));
        return action switch
        {
            "move" => defines.MovementRelocationCost,
            "campaign" => defines.MovementCampaignCost * size,
            "services" => defines.MovementCampaignCost * size * 2f,
            "recruit" => defines.MovementRecruitCostPerThousand * RecruitThousands(nation) *
                         (Movements.DefinitionOf(nation)?.Has("for_hire") == true ? 0.5f : 1f),
            "rise" => defines.MovementUprisingCost,
            "infiltrate" => defines.MovementCampaignCost,
            "call_to_arms" => defines.MovementCallToArmsCost,
            "lobby" => 0.02f,
            "patrols" => 0.008f,
            "hospital" => 0.03f,
            "appeal" => 0.01f,
            "strike" => 0.02f,
            "march" => 0.05f,
            _ => 0f
        };
    }

    private static float RecruitThousands(Nation nation) => 2f;

    /// <summary>The opinion a country must have of it to recognise it: less the more standing it has.</summary>
    public static float RecognitionNeeds(Defines defines, MovementState state) =>
        defines.MovementRecognitionOpinion - state.Legitimacy * 20f;

    private static string CooldownKey(string action, int stateMapIndex, int targetNationId) =>
        action switch
        {
            "campaign" or "services" or "hospital" => $"{action}:{stateMapIndex}",
            "rise" => $"rise:{stateMapIndex}",
            "infiltrate" => $"infiltrate:{stateMapIndex}",
            "call_to_arms" => $"call_to_arms:{stateMapIndex}",
            _ => action
        };

    private static int CooldownDays(Defines defines, string action) => action switch
    {
        "referendum" => defines.CivilReferendumDays,
        "campaign" or "services" => defines.MovementCampaignDays,
        "fundraise" => defines.MovementFundraiseDays,
        "recruit" => defines.MovementRecruitDays,
        "recognition" => defines.MovementRecognitionDays,
        "rise" => defines.MovementUprisingDays,
        "infiltrate" => defines.MovementCampaignDays,
        "call_to_arms" => defines.MovementCallToArmsDays,
        "move" => 0,
        "contract" => 182,
        "strike" => 365,
        "march" => 730,
        _ => defines.MovementTraitActionDays
    };

    /// <summary>Why this cannot be done now; empty when it can.</summary>
    public static string Refusal(Campaign campaign, Nation nation, string action, int stateMapIndex, int targetNationId)
    {
        var state = nation?.Movement;
        var definition = Movements.DefinitionOf(nation);
        var kind = Of(action);
        if (state is null || definition is null || kind is null) return Loc.Get("Only a movement can do this.");
        if (state.Dissolved) return Loc.Get("The movement is no more.");
        if (action == "rise" && !MovementMethods.WillRise(campaign, nation, definition))
            return Loc.Get(definition.Has("for_hire") ? "This company takes contracts, not territory." :
                "The movement has not taken up arms.");
        if (kind.Trait is not null && !definition.Has(kind.Trait)) return Loc.Get("This movement does not do that.");
        var defines = campaign.Defines;
        var region = campaign.States.Find(candidate => candidate.MapIndex == stateMapIndex);
        var target = campaign.NationById(targetNationId);
        if (kind.NeedsRegion && region is null) return Loc.Get("Choose a region first.");
        if (kind.NeedsNation && target is null) return Loc.Get("Choose a country first.");

        var ready = state.ReadyOnDay.GetValueOrDefault(CooldownKey(action, stateMapIndex, targetNationId), 0);
        if (campaign.ElapsedDays < ready) return Loc.Format("Not again for {0} days.", ready - campaign.ElapsedDays);
        var cost = Cost(campaign, nation, action, stateMapIndex);
        if (nation.Treasury < cost) return Loc.Format("It needs {0} and has {1}.", Fmt.Money(cost), Fmt.Money(nation.Treasury));
        if (state.MovingToStateMapIndex >= 0 && action is not ("fundraise" or "recognition" or "appeal" or "lobby"))
            return Loc.Get("The headquarters is on the move.");

        switch (action)
        {
            case "strike":
            case "march":
                return MovementPower.Refusal(campaign, nation, action);
            case "referendum": return CivilIndependence.Refusal(campaign, nation, region);
            case "move":
            {
                if (!state.Landless) return Loc.Get("A country with ground of its own is governed from its capital.");
                if (region.MapIndex == state.HeadquartersStateMapIndex) return Loc.Get("The headquarters is already here.");
                var host = campaign.Nations.Find(candidate => candidate.MapIndex == region.NationMapIndex);
                if (host is null) return Loc.Get("Nobody governs that ground.");
                if (host.Movement is not null) return Loc.Get("Not in another movement's ground.");
                if (CampaignDiplomacy.AtWar(campaign, host.Id, nation.Id)) return Loc.Format("It is at war with {0}.", host.DisplayName);
                var opinion = CampaignDiplomacy.Get(campaign, host.Id, nation.Id).Opinion;
                if (state.DesignatedBy.Contains(host.Id)) return Loc.Format("{0} lists it as a terrorist organisation.", host.DisplayName);
                if (opinion < -25f && !definition.Sponsors.ContainsKey(host.Tag) && !state.InvitedBy.Contains(host.Id))
                    return Loc.Format("{0} will not have it ({1:0} opinion).", host.DisplayName, opinion);
                return "";
            }
            case "campaign":
            case "services":
            case "hospital":
            {
                var own = MovementRules.PeopleOf(region, definition, out var faithful);
                if (action != "hospital" && own + faithful <= 0) return Loc.Get("None of its peoples live there.");
                if (!MovementReach.CanOperate(campaign, nation, region, definition))
                    return Loc.Get("There is no local network or permission for this operation.");
                if (action == "hospital" && region.WarDamage < .02f && region.StandardOfLiving >= .8f)
                    return Loc.Get("This region has no urgent need for a field hospital.");
                return "";
            }
            case "recognition":
            {
                if (target.Id == nation.Id || target.Movement is not null) return Loc.Get("Only a country can recognise it.");
                if (state.RecognisedBy.Contains(target.Id)) return Loc.Format("{0} already recognises it.", target.DisplayName);
                if (state.DesignatedBy.Contains(target.Id)) return Loc.Format("{0} lists it as a terrorist organisation.", target.DisplayName);
                // The country whose ground it claims, and that country's allies, will not.
                foreach (var claimed in definition.Homeland)
                {
                    var claimedRegion = Movements.StateById(campaign, claimed);
                    var holder = claimedRegion is null ? null : campaign.Nations.Find(n => n.MapIndex == claimedRegion.NationMapIndex);
                    if (holder is null || holder.Id == nation.Id) continue;
                    if (holder.Id == target.Id || CampaignDiplomacy.Get(campaign, target.Id, holder.Id).Stance == DiplomaticStance.Allied)
                        return Loc.Format("{0} will not recognise a claim on {1}'s ground.", target.DisplayName, holder.DisplayName);
                }
                return "";
            }
            case "rise":
            {
                if (region.NationMapIndex == nation.MapIndex) return Loc.Get("The region is already its own.");
                if (!MovementReach.CanClaimTerritory(campaign, nation, region))
                    return Loc.Get("Offices and foreign support do not grant a territorial mandate here.");
                var support = state.Support.GetValueOrDefault(region.MapIndex);
                if (!float.IsFinite(support) || !float.IsFinite(state.Cadres) || !float.IsFinite(state.Legitimacy) ||
                    !float.IsFinite(region.Stability) || !float.IsFinite(nation.Treasury))
                    return Loc.Get("Invalid simulation values prevent this action. Pause and inspect the campaign.");
                if (!definition.Has("for_hire") && support < defines.MovementUprisingSupport)
                    return Loc.Format("Support there is {0}; it takes {1}.", Fmt.Percent(support), Fmt.Percent(defines.MovementUprisingSupport));
                if (definition.Has("for_hire") && region.Stability > 0.35f)
                    return Loc.Format("Stability there is {0}; a company only walks into a region that is falling apart.", Fmt.Percent(region.Stability));
                if (state.Cadres < defines.MovementUprisingCadres)
                    return Loc.Format("It has {0} cadres; it takes {1}.", Fmt.People(state.Cadres), Fmt.People(defines.MovementUprisingCadres));
                return "";
            }
            case "infiltrate":
            {
                if (region.NationMapIndex == nation.MapIndex) return Loc.Get("The region is already its own.");
                if (state.Support.GetValueOrDefault(region.MapIndex) < 0.1f) return Loc.Get("Nobody there would hide them.");
                if (state.Cadres < defines.MovementInfiltrateFighters * 0.5f)
                    return Loc.Format("It has {0} cadres; it takes {1}.", Fmt.People(state.Cadres), Fmt.People(defines.MovementInfiltrateFighters * 0.5f));
                if (state.Landless && state.HeadquartersStateMapIndex < 0) return Loc.Get("It has nowhere to send them from.");
                return "";
            }
            case "call_to_arms":
            {
                if (region.NationMapIndex != nation.MapIndex) return Loc.Get("Only in a region it holds.");
                if (state.Support.GetValueOrDefault(region.MapIndex) < 0.15f) return Loc.Get("Too few there would answer the call.");
                if (state.Cadres < defines.MovementCallToArmsCadres)
                    return Loc.Format("It has {0} cadres; it takes {1}.", Fmt.People(state.Cadres), Fmt.People(defines.MovementCallToArmsCadres));
                return "";
            }
            case "contract":
            {
                if (target.Id == nation.Id || target.Movement is not null) return Loc.Get("Only a country can hire it.");
                var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(target.Id));
                if (!atWar) return Loc.Format("{0} is not at war and does not need it.", target.DisplayName);
                if (CampaignDiplomacy.Get(campaign, target.Id, nation.Id).Opinion < 0f)
                    return Loc.Format("{0} will not hire it.", target.DisplayName);
                if (target.Treasury < 0.5f) return Loc.Format("{0} cannot pay.", target.DisplayName);
                return "";
            }
        }
        return "";
    }

    /// <summary>Do it. The command has already asked Refusal.</summary>
    internal static void Do(Campaign campaign, Nation nation, string action, int stateMapIndex, int targetNationId)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        var defines = campaign.Defines;
        var region = campaign.States.Find(candidate => candidate.MapIndex == stateMapIndex);
        var target = campaign.NationById(targetNationId);
        nation.Treasury -= Cost(campaign, nation, action, stateMapIndex);
        var cooldown = CooldownDays(defines, action);
        if (cooldown > 0) state.ReadyOnDay[CooldownKey(action, stateMapIndex, targetNationId)] = campaign.ElapsedDays + cooldown;
        var host = Movements.HostOf(campaign, nation);
        var inHost = region is not null && host is not null && region.NationMapIndex == host.MapIndex;

        switch (action)
        {
            case "strike":
            case "march":
                MovementPower.Do(campaign, nation, action);
                break;

            case "move":
                state.MovingToStateMapIndex = region.MapIndex;
                state.ArrivesOnDay = campaign.ElapsedDays + Math.Max(1, defines.MovementRelocationDays);
                state.LastReport = Loc.Format("The headquarters is on its way to {0}.", region.DisplayName);
                break;

            case "campaign":
            case "services":
            {
                var own = MovementRules.PeopleOf(region, definition, out var faithful);
                var ceiling = MovementReach.SupportCeiling(campaign, nation, region, definition, own, faithful);
                var gain = defines.MovementCampaignSupport * (action == "services" ? 1.6f : 1f);
                MovementRules.Win(state, region.MapIndex, gain);
                state.Support[region.MapIndex] = MathF.Min(ceiling, state.Support[region.MapIndex]);
                if (action == "services") region.StandardOfLiving = M.Clamp01(region.StandardOfLiving + 0.01f);
                state.Heat = M.Clamp01(state.Heat + defines.MovementCampaignHeat * (inHost ? 1f : 0.4f) *
                                       (definition.Has("armed_wing") ? 1.3f : 1f));
                state.LastReport = Loc.Format("Support in {0} is now {1}.", region.DisplayName,
                    Fmt.Percent(state.Support[region.MapIndex]));
                break;
            }

            case "fundraise":
            {
                var raised = state.DonationsWeekly * defines.MovementFundraiseWeeks;
                nation.Treasury += raised;
                state.Legitimacy = M.Clamp01(state.Legitimacy - 0.01f);
                state.LastReport = Loc.Format("The following gave {0}.", Fmt.Money(raised));
                break;
            }

            case "recruit":
                state.Cadres += RecruitThousands(nation) * 1000f;
                state.Heat = M.Clamp01(state.Heat + 0.02f);
                state.LastReport = Loc.Format("It now has {0} cadres.", Fmt.People(state.Cadres));
                break;

            case "referendum":
                CivilIndependence.Ratify(campaign, nation, region);
                break;
            case "recognition":
            {
                var opinion = CampaignDiplomacy.Get(campaign, target.Id, nation.Id).Opinion;
                var needed = RecognitionNeeds(defines, state);
                if (opinion >= needed)
                {
                    state.RecognisedBy.Add(target.Id);
                    state.Legitimacy = M.Clamp01(state.Legitimacy + 0.015f);
                    OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, nation.Id, target.Id), "recognised_us");
                    state.LastReport = Loc.Format("{0} recognises it.", target.DisplayName);
                }
                else state.LastReport = Loc.Format("{0} declined to recognise it ({1:0} opinion; it takes {2:0}).",
                    target.DisplayName, opinion, needed);
                break;
            }

            case "rise":
                MovementRules.Rise(campaign, nation, region);
                break;

            case "lobby":
                if (host is not null)
                {
                    var relation = CampaignDiplomacy.Get(campaign, host.Id, nation.Id);
                    relation.Opinion = MathF.Min(100f, relation.Opinion + 10f);
                }
                state.Heat = M.Clamp01(state.Heat - 0.25f);
                state.LastReport = Loc.Get("The host's temper has cooled.");
                break;

            case "patrols":
                if (host is not null)
                    foreach (var held in campaign.StatesOf(host))
                        if (MovementRules.PeopleOf(held, definition, out _) > 0)
                            MovementRules.Win(state, held.MapIndex, 0.02f);
                state.Heat = M.Clamp01(state.Heat + 0.08f);
                state.LastReport = Loc.Get("Its patrols are out in its neighbourhoods.");
                break;

            case "hospital":
                region.StandardOfLiving = M.Clamp01(region.StandardOfLiving + 0.02f);
                region.WarDamage = MathF.Max(0f, region.WarDamage - 0.02f);
                state.Legitimacy = M.Clamp01(state.Legitimacy + 0.03f * (1f - state.Legitimacy));
                state.LastReport = Loc.Format("A field hospital has opened in {0}.", region.DisplayName);
                break;

            case "appeal":
                state.Legitimacy = M.Clamp01(state.Legitimacy + 0.02f);
                foreach (var other in campaign.Nations)
                {
                    if (other.Movement is not null || other.Id == nation.Id) continue;
                    var government = PoliticsCore.GovernmentOf(other);
                    if (government?.Id is "parliamentary_republic" or "constitutional_monarchy" or "direct_democracy")
                    {
                        var relation = CampaignDiplomacy.Get(campaign, other.Id, nation.Id);
                        relation.Opinion = MathF.Min(100f, relation.Opinion + 3f);
                    }
                }
                state.LastReport = Loc.Get("The world has heard its case.");
                break;

            case "infiltrate":
            {
                // From the headquarters, or from its capital once it has one.
                var fromLatitude = state.Landless ? state.HeadquartersLatitude : nation.CapitalLatitude;
                var fromLongitude = state.Landless ? state.HeadquartersLongitude : nation.CapitalLongitude;
                var (toLatitude, toLongitude) = Movements.CentreOf(campaign, region);
                var distance = NavalOperations.Distance(fromLatitude, fromLongitude, toLatitude, toLongitude);
                var days = Math.Max(defines.MovementInfiltrateMinimumDays, (int)MathF.Ceiling(distance * defines.MovementInfiltrateDaysPerDegree));
                var fighters = MathF.Min(defines.MovementInfiltrateFighters, state.Cadres * 0.4f);
                state.Cadres -= fighters;
                state.OnTheWay.Add(new Infiltration { Region = region.MapIndex, Fighters = fighters, ArrivesOnDay = campaign.ElapsedDays + days });
                state.Heat = M.Clamp01(state.Heat + 0.03f);
                state.LastReport = Loc.Format("{0} fighters are on their way to {1}: {2} days.", Fmt.People(fighters), region.DisplayName, days);
                break;
            }

            case "call_to_arms":
            {
                var supporters = MovementRules.PeopleOf(region, definition, out _) * state.Support.GetValueOrDefault(region.MapIndex);
                var wanted = Math.Clamp((int)MathF.Round(supporters / MathF.Max(1f, defines.MovementMilitiaPerSupporters)), 1, defines.MovementCallToArmsMax);
                var raised = 0;
                for (var attempt = 0; attempt < wanted; attempt++)
                    if (MilitiaCore.Found(campaign, nation, region)) raised++;
                state.Cadres -= defines.MovementCallToArmsCadres;
                state.Support[region.MapIndex] = M.Clamp01(state.Support.GetValueOrDefault(region.MapIndex) - 0.02f * raised);
                state.LastReport = raised > 0
                    ? Loc.Format("{0} answers the call: {1} militia formation(s).", region.DisplayName, raised)
                    : Loc.Format("Nobody in {0} could be formed up: there is no ground of its own left there.", region.DisplayName);
                campaign.MarkStrategicChange();
                break;
            }

            case "contract":
                state.ContractNationId = target.Id;
                state.ContractUntilDay = campaign.ElapsedDays + 182;
                state.LastReport = Loc.Format("A contract with {0}: paid every week for half a year.", target.DisplayName);
                break;
        }
    }
}
