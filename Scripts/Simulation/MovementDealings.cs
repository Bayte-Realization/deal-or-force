using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>What a crackdown costs, how long until the next against the same movement, and what it leaves of the following in the country's regions.</summary>
    public float MovementCrackdownCost { get; set; } = 0.02f;
    public int MovementCrackdownDays { get; set; } = 90;
    public float MovementCrackdownSupportKept { get; set; } = 0.6f;
    /// <summary>What a crackdown costs the country doing it in stability.</summary>
    public float MovementCrackdownStability { get; set; } = 0.01f;
}

/// <summary>One thing a country does about a movement, from its player or its AI (<see cref="MovementDealings"/>).</summary>
public sealed class MovementDealingCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int MovementId { get; init; }
    public string Action { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        var country = campaign.NationById(IssuerId);
        var movement = campaign.NationById(MovementId);
        if (country is null || movement?.Movement is null) return;
        if (MovementDealings.Refusal(campaign, country, movement, Action).Length > 0) return;
        MovementDealings.Do(campaign, country, movement, Action);
    }
}

/// <summary>
/// What the rest of the world does about a movement: recognise it or stop, fund it or stop,
/// list it as a terrorist organisation or take it off the list, crack down on its following in
/// the country's own regions, throw its headquarters out, or offer it a home. The movement's
/// own actions are <see cref="MovementActions"/>; these are the country's side of the same
/// relationship, and every country's AI uses them as well as the player.
/// </summary>
public static class MovementDealings
{
    public sealed record Kind(string Id, string Name, string Help, string Icon);

    public static readonly IReadOnlyList<Kind> Kinds = new Kind[]
    {
        new("recognise", "Recognise", "Recognise it as the rightful voice of its people. Its standing rises; the country whose ground it claims will not forgive it.", "movement_legitimacy"),
        new("withdraw_recognition", "Withdraw recognition", "Stop recognising it. It will remember.", "movement_legitimacy"),
        new("sponsor", "Fund it", "Pay it every week out of the treasury. It thinks the world of whoever pays.", "movement_fundraise"),
        new("stop_sponsoring", "Stop funding it", "No more money from the treasury.", "movement_fundraise"),
        new("designate", "List as terrorist", "List it as a terrorist organisation: recognition and money from here are over, its donations shrink everywhere and it will hate the country for it.", "war"),
        new("lift_designation", "Take off the list", "Stop listing it as a terrorist organisation.", "treaty"),
        new("crack_down", "Crack down", "Police, arrests and raids against its following in the country's own regions - and its headquarters, if it is here. It costs stability and it is never forgotten.", "unrest"),
        new("expel", "Expel the headquarters", "Throw its headquarters out of the country.", "movement_move"),
        new("offer_asylum", "Offer a home", "Offer its headquarters a place in the country: a movement run by the game comes at once, and a player's may move here whatever anyone else thinks of it.", "movement_headquarters"),
    };

    public static Kind Of(string id) => Kinds.FirstOrDefault(kind => kind.Id == id);

    /// <summary>The actions that make sense for this country and this movement now - one of each pair.</summary>
    public static IEnumerable<Kind> For(Campaign campaign, Nation country, Nation movement)
    {
        var state = movement?.Movement;
        if (state is null || country is null || country.Movement is not null) yield break;
        yield return Of(state.RecognisedBy.Contains(country.Id) ? "withdraw_recognition" : "recognise");
        yield return Of(state.SponsoredBy.ContainsKey(country.Id) ? "stop_sponsoring" : "sponsor");
        yield return Of(state.DesignatedBy.Contains(country.Id) ? "lift_designation" : "designate");
        yield return Of("crack_down");
        if (state.Landless) yield return Of(Movements.HostOf(campaign, movement) == country ? "expel" : "offer_asylum");
    }

    public static float Cost(Campaign campaign, string action) => action == "crack_down" ? campaign.Defines.MovementCrackdownCost : 0f;

    /// <summary>What one thinks of the other without making a relation that is not there yet.</summary>
    private static float Opinion(Campaign campaign, int a, int b)
    {
        if (a == b || a < 0 || b < 0) return 0f;
        var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
        return campaign.Relations.TryGetValue(key, out var relation) ? relation.Opinion : 0f;
    }

    /// <summary>The regions of this country where the movement has a following worth the name.</summary>
    private static IEnumerable<ScenarioState> FollowingIn(Campaign campaign, Nation country, MovementState state) =>
        campaign.StatesOf(country).Where(region => state.Support.GetValueOrDefault(region.MapIndex) >= 0.05f ||
                                                    state.Infiltrated.ContainsKey(region.MapIndex));

    /// <summary>Whether this country holds ground the movement claims, or is allied to one that does.</summary>
    public static bool HoldsItsClaim(Campaign campaign, Nation country, Nation movement)
    {
        var definition = Movements.DefinitionOf(movement);
        if (definition is null) return false;
        foreach (var id in definition.Homeland)
        {
            var region = Movements.StateById(campaign, id);
            var holder = region is null ? null : campaign.Nations.Find(nation => nation.MapIndex == region.NationMapIndex);
            if (holder is null || holder.Id == movement.Id) continue;
            if (holder.Id == country.Id || CampaignDiplomacy.Get(campaign, country.Id, holder.Id).Stance == DiplomaticStance.Allied) return true;
        }
        return false;
    }

    /// <summary>Why this cannot be done now; empty when it can.</summary>
    public static string Refusal(Campaign campaign, Nation country, Nation movement, string action)
    {
        var state = movement?.Movement;
        if (state is null || Of(action) is null) return Loc.Get("That is not something a country does to a movement.");
        if (country is null || country.Movement is not null || campaign.StatesOf(country).Length == 0)
            return Loc.Get("Only a country with ground of its own can do this.");
        if (state.Dissolved) return Loc.Get("The movement is no more.");
        if (country.Id == movement.Id) return Loc.Get("Not to itself.");
        var at = campaign.ElapsedDays;
        switch (action)
        {
            case "recognise":
                if (state.RecognisedBy.Contains(country.Id)) return Loc.Get("Already recognised.");
                if (state.DesignatedBy.Contains(country.Id)) return Loc.Get("Take it off the terrorist list first.");
                if (HoldsItsClaim(campaign, country, movement)) return Loc.Get("It claims this country's ground, or an ally's.");
                return "";
            case "withdraw_recognition":
                return state.RecognisedBy.Contains(country.Id) ? "" : Loc.Get("It is not recognised here.");
            case "sponsor":
                if (state.SponsoredBy.ContainsKey(country.Id)) return Loc.Get("Already funded.");
                if (state.DesignatedBy.Contains(country.Id)) return Loc.Get("Take it off the terrorist list first.");
                if (CampaignDiplomacy.AtWar(campaign, country.Id, movement.Id)) return Loc.Get("Not while at war with it.");
                if (country.Treasury < campaign.Defines.MovementSponsorPerYear) return Loc.Get("The treasury cannot afford it.");
                return "";
            case "stop_sponsoring":
                return state.SponsoredBy.ContainsKey(country.Id) ? "" : Loc.Get("It is not funded from here.");
            case "designate":
                return state.DesignatedBy.Contains(country.Id) ? Loc.Get("Already listed.") : "";
            case "lift_designation":
                return state.DesignatedBy.Contains(country.Id) ? "" : Loc.Get("It is not listed here.");
            case "crack_down":
            {
                var ready = state.ReadyOnDay.GetValueOrDefault($"crackdown:{country.Id}", 0);
                if (at < ready) return Loc.Format("Not again for {0} days.", ready - at);
                if (country.Treasury < Cost(campaign, action)) return Loc.Get("The treasury cannot afford it.");
                var hostsIt = state.Landless && Movements.HostOf(campaign, movement) == country;
                if (!hostsIt && !FollowingIn(campaign, country, state).Any()) return Loc.Get("It has no following here to crack down on.");
                return "";
            }
            case "expel":
                if (!state.Landless || Movements.HostOf(campaign, movement) != country) return Loc.Get("Its headquarters is not here.");
                return "";
            case "offer_asylum":
                if (!state.Landless) return Loc.Get("It has a country of its own.");
                if (Movements.HostOf(campaign, movement) == country) return Loc.Get("Its headquarters is already here.");
                if (state.InvitedBy.Contains(country.Id)) return Loc.Get("The offer stands.");
                if (state.DesignatedBy.Contains(country.Id)) return Loc.Get("Take it off the terrorist list first.");
                if (CampaignDiplomacy.AtWar(campaign, country.Id, movement.Id)) return Loc.Get("Not while at war with it.");
                return "";
        }
        return "";
    }

    /// <summary>Do it. The command has already asked Refusal.</summary>
    internal static void Do(Campaign campaign, Nation country, Nation movement, string action)
    {
        var state = movement.Movement;
        var defines = campaign.Defines;
        var towards = CampaignDiplomacy.Get(campaign, movement.Id, country.Id);
        void Resent(string reason)
        {
            // The country whose ground it claims takes a side being taken as taking a side.
            foreach (var id in Movements.DefinitionOf(movement)?.Homeland ?? new List<string>())
            {
                var region = Movements.StateById(campaign, id);
                var holder = region is null ? null : campaign.Nations.Find(nation => nation.MapIndex == region.NationMapIndex);
                if (holder is null || holder.Id == movement.Id || holder.Id == country.Id) continue;
                OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, holder.Id, country.Id), reason);
            }
        }
        country.Treasury -= Cost(campaign, action);

        switch (action)
        {
            case "recognise":
                state.RecognisedBy.Add(country.Id);
                state.Legitimacy = M.Clamp01(state.Legitimacy + 0.015f);
                OpinionSystem.Remember(campaign, towards, "recognised_us");
                Resent("recognised_our_rebels");
                state.LastReport = Loc.Format("{0} recognises it.", country.DisplayName);
                break;
            case "withdraw_recognition":
                state.RecognisedBy.Remove(country.Id);
                OpinionSystem.Remember(campaign, towards, "withdrew_recognition");
                state.LastReport = Loc.Format("{0} no longer recognises it.", country.DisplayName);
                break;
            case "sponsor":
                state.SponsoredBy[country.Id] = defines.MovementSponsorPerYear;
                OpinionSystem.Remember(campaign, towards, "funds_us");
                Resent("funds_our_rebels");
                state.LastReport = Loc.Format("{0} has begun to fund it.", country.DisplayName);
                break;
            case "stop_sponsoring":
                state.SponsoredBy.Remove(country.Id);
                state.LastReport = Loc.Format("{0} has stopped funding it.", country.DisplayName);
                break;
            case "designate":
                state.DesignatedBy.Add(country.Id);
                state.RecognisedBy.Remove(country.Id);
                state.SponsoredBy.Remove(country.Id);
                state.InvitedBy.Remove(country.Id);
                state.Legitimacy = M.Clamp01(state.Legitimacy - 0.02f);
                OpinionSystem.Remember(campaign, towards, "listed_us_as_terrorists");
                state.LastReport = Loc.Format("{0} lists it as a terrorist organisation.", country.DisplayName);
                // Listing a movement whose headquarters is here is throwing it out.
                if (state.Landless && Movements.HostOf(campaign, movement) == country) MovementRules.Expel(campaign, movement);
                break;
            case "lift_designation":
                state.DesignatedBy.Remove(country.Id);
                state.LastReport = Loc.Format("{0} no longer lists it as a terrorist organisation.", country.DisplayName);
                break;
            case "crack_down":
            {
                state.ReadyOnDay[$"crackdown:{country.Id}"] = campaign.ElapsedDays + defines.MovementCrackdownDays;
                var regions = FollowingIn(campaign, country, state).ToList();
                foreach (var region in regions)
                {
                    state.Support[region.MapIndex] = state.Support.GetValueOrDefault(region.MapIndex) * defines.MovementCrackdownSupportKept;
                    if (state.Infiltrated.TryGetValue(region.MapIndex, out var hidden)) state.Infiltrated[region.MapIndex] = hidden * 0.3f;
                    region.Radicalism = M.Clamp01(region.Radicalism + 0.01f);
                }
                var hostsIt = state.Landless && Movements.HostOf(campaign, movement) == country;
                state.Cadres *= hostsIt ? 0.75f : 0.93f;
                if (hostsIt) state.Heat = M.Clamp01(state.Heat + 0.3f);
                country.Stability = M.Clamp(country.Stability - defines.MovementCrackdownStability, 0.1f, 0.98f);
                OpinionSystem.Remember(campaign, towards, "cracked_down_on_us");
                state.LastReport = Loc.Format("{0} has cracked down on it in {1} region(s).", country.DisplayName, regions.Count + (hostsIt ? 1 : 0));
                break;
            }
            case "expel":
                MovementRules.Expel(campaign, movement);
                break;
            case "offer_asylum":
            {
                state.InvitedBy.Add(country.Id);
                OpinionSystem.Remember(campaign, towards, "offered_us_a_home");
                state.LastReport = Loc.Format("{0} offers its headquarters a home.", country.DisplayName);
                // A movement the game runs answers at once; a player's decides for itself.
                if (!movement.IsHuman)
                {
                    var seat = MovementFall.SeatRegion(campaign, country, movement);
                    if (seat is not null && state.MovingToStateMapIndex < 0)
                    {
                        state.MovingToStateMapIndex = seat.MapIndex;
                        state.ArrivesOnDay = campaign.ElapsedDays + Math.Max(1, defines.MovementRelocationDays / 3);
                        state.HomelessWeeks = 0;
                    }
                }
                break;
            }
        }
        campaign.MovementRevision++;
    }

    // ------------------------------------------------------------------ the AI --------

    /// <summary>
    /// What every country the game runs does about the movements, once a week: few things, and
    /// the obvious ones. The country a movement rose against lists it and cracks down; its
    /// friends recognise it; a friend that is its claim's enemy funds it; and anybody gives a
    /// home to one that has nowhere to go and that it thinks well of.
    /// </summary>
    internal static void Ai(Campaign campaign)
    {
        var movements = new List<Nation>();
        foreach (var candidate in campaign.Nations) if (candidate.Movement is { Dissolved: false }) movements.Add(candidate);
        if (movements.Count == 0) return;
        // A quarter of the world a week: no country's policy towards a movement is a weekly matter.
        var week = campaign.ElapsedDays / Math.Max(1, campaign.Defines.EconomyTickDays);
        foreach (var country in campaign.Nations)
        {
            if ((country.Id & 3) != (week & 3)) continue;
            if (country.IsHuman || country.Movement is not null || campaign.StatesOf(country).Length == 0) continue;
            var acted = false;
            foreach (var movement in movements)
            {
                if (acted) break;
                var state = movement.Movement;
                // Relations nobody has yet are not made here just to be read: they are neutral.
                var opinion = Opinion(campaign, country.Id, movement.Id);
                var claimant = opinion <= -30f || HoldsItsClaim(campaign, country, movement);
                var hosting = state.Landless && state.HeadquartersStateMapIndex >= 0 && MovementRules.OwnerOf(campaign,
                    campaign.States.Find(region => region.MapIndex == state.HeadquartersStateMapIndex)) == country;
                // Nothing to decide: neither side of the argument, not its host, and no stranger in need.
                if (!claimant && !hosting && opinion < 20f && !(state.Landless && state.HeadquartersStateMapIndex < 0)) continue;
                var roll = MovementRules.Roll(campaign.Seed, country.Id, campaign.ElapsedDays, movement.Id);
                bool Try(string action)
                {
                    if (Refusal(campaign, country, movement, action).Length > 0) return false;
                    new MovementDealingCommand { IssuerId = country.Id, MovementId = movement.Id, Action = action }.Apply(campaign);
                    acted = true;
                    return true;
                }

                var threatened = HoldsItsClaim(campaign, country, movement) || opinion <= -40f;
                if (threatened)
                {
                    if (opinion <= -30f && roll < 0.08f && Try("designate")) continue;
                    var following = FollowingIn(campaign, country, state).Sum(region => state.Support.GetValueOrDefault(region.MapIndex));
                    if (following >= 0.3f && roll < 0.35f && Try("crack_down")) continue;
                    continue;
                }
                if (state.DesignatedBy.Contains(country.Id)) continue;
                if (opinion >= MovementActions.RecognitionNeeds(campaign.Defines, state) + 25f && roll < 0.03f && Try("recognise")) continue;
                if (state.Landless && state.HeadquartersStateMapIndex < 0 && opinion >= 20f && roll < 0.25f && Try("offer_asylum")) continue;
                if (opinion >= 60f && roll < 0.02f && country.Treasury > 5f && Try("sponsor")) continue;
            }
        }
    }
}
