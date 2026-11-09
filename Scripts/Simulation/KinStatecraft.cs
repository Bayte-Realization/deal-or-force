using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How few of our own people a foreign region may hold before working them is not worth
    /// the risk. A share of the region, not a headcount: five hundred thousand people who are
    /// a tenth of a big region vote for somebody else's politics, and a hundred thousand who
    /// are half of a small one are the region.
    /// </summary>
    public float KinAgitationMinShare { get; set; } = 0.15f;

    /// <summary>What one round of organisers, printing and travel costs, per million of our people there.</summary>
    public float KinAgitationCashPerMillion { get; set; } = 0.004f;

    /// <summary>The smallest bill an operation can be, so a village does not cost nothing.</summary>
    public float KinAgitationCashFloor { get; set; } = 0.002f;

    /// <summary>
    /// How much unrest one operation adds to the region, at full kin presence. Unrest is what
    /// the secession and rising rules already read, so the effect is a real one that other
    /// systems act on rather than a hidden counter of our own.
    /// </summary>
    public float KinAgitationRadicalism { get; set; } = 0.030f;

    /// <summary>How long before the same network in the same region can be asked again.</summary>
    public int KinAgitationCooldownDays { get; set; } = 150;

    /// <summary>
    /// The chance, on each operation, that the host's service notices where the leaflets come
    /// from. A quiet operation is a grievance; a caught one is a scandal, and the world reads it.
    /// </summary>
    public float KinAgitationDiscovery { get; set; } = 0.22f;

    /// <summary>Grievance the host takes at full kin presence, and what being caught adds on top.</summary>
    public float KinAgitationGrievance { get; set; } = 5f;
    public float KinAgitationCaughtGrievance { get; set; } = 12f;

    /// <summary>
    /// Volunteers: how many fighters one operation puts into a region, per million of our
    /// people there, and the smallest number worth sending.
    /// </summary>
    public float KinVolunteersPerMillion { get; set; } = 60f;
    public float KinVolunteersFloor { get; set; } = 120f;

    /// <summary>What the volunteers cost the treasury per thousand men, and the manpower reserve.</summary>
    public float KinVolunteersCashPerThousand { get; set; } = 0.006f;
    public int KinVolunteersCooldownDays { get; set; } = 240;
    public float KinVolunteersGrievance { get; set; } = 9f;
    public float KinVolunteersCaughtGrievance { get; set; } = 20f;

    /// <summary>
    /// What the computer is willing to risk. A country does this to a rival it already thinks
    /// badly of, rarely, and only when the treasury can lose the money.
    /// </summary>
    public float AiKinOpinion { get; set; } = -25f;
    public float AiKinAgitationChance { get; set; } = 0.10f;
    public float AiKinVolunteersChance { get; set; } = 0.05f;
    public float AiKinTreasuryYears { get; set; } = 2f;

    /// <summary>
    /// A proxy movement: how much of the region has to be ours before founding one there is
    /// anything but a stunt, and what it costs in money (per million of our people) and in the
    /// political credit a government spends to be seen doing something it will deny.
    /// </summary>
    public float KinProxyMinShare { get; set; } = 0.35f;
    public float KinProxyCashPerMillion { get; set; } = 0.010f;
    public float KinProxyPoliticalCost { get; set; } = 8f;

    /// <summary>How long before the same ground can be worked into a second movement.</summary>
    public int KinProxyCooldownDays { get; set; } = 540;

    /// <summary>What the founders put into it: cadres per million of our people, and the standing it starts with.</summary>
    public float KinProxyCadresPerMillion { get; set; } = 900f;
    public float KinProxyLegitimacy { get; set; } = 0.18f;

    /// <summary>
    /// How much of our kin's presence becomes the movement's following on the first day, what
    /// the host carries when it finds out, and the chance it does on the day it is founded.
    /// </summary>
    public float KinProxySupportSeed { get; set; } = 0.70f;
    public float KinProxyGrievance { get; set; } = 14f;
    public float KinProxyCaughtGrievance { get; set; } = 26f;
    public float KinProxyDiscovery { get; set; } = 0.45f;

    /// <summary>How willing the computer is to found one at all, on its monthly look.</summary>
    public float AiKinProxyChance { get; set; } = 0.02f;
}

/// <summary>One country's hand in another's ground, from its player or its AI (<see cref="KinStatecraft"/>).</summary>
public sealed class KinStatecraftCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int RegionMapIndex { get; init; } = -1;
    public string Action { get; init; } = "";
    public bool Done { get; private set; }
    public string Result { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Done = false;
        Result = "";
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var country = campaign.NationById(IssuerId);
        var region = campaign.States?.Find(state => state is not null && state.MapIndex == RegionMapIndex);
        if (country is null || region is null) return;
        // The sentence the player would be shown is also the answer the command gives, so a
        // refused order explains itself instead of failing quietly.
        Result = KinStatecraft.Refusal(campaign, country, region, Action);
        if (Result.Length > 0) return;
        Done = KinStatecraft.Do(campaign, country, region, Action);
    }
}

/// <summary>
/// What a state does about its own people living on the other side of a border.
///
/// The movements subsystem has always been able to do this from the far side - a movement
/// campaigns, raises money, raises fighters, and rises - and the world has always been able
/// to answer it: recognise, fund, list, crack down, expel, host. What was missing is the
/// ordinary state acting first, which is how most of the last century actually went: a
/// neighbouring country quietly working its kin in somebody else's region for years, and then
/// standing behind what happens there without ever quite being in it.
///
/// Two operations, and both are deniable in the same way:
///
///   agitate    organisers, printing and travel: unrest rises among our people, the host's
///              grievance with us rises, and a service that notices turns a quiet grievance
///              into a public scandal.
///   volunteers our own men, crossing among the season's labour traffic to a rising of their
///              kin: hidden fighters in that region, which the existing rules count when an
///              uprising is called. No declaration of war, because the point is that there
///              was never a declaration.
///
/// Both write into state the rest of the simulation already reads - regional radicalism, the
/// grievance a pair of countries carries, a movement's hidden fighters - rather than into
/// counters of their own, so nothing downstream has to learn about them.
/// </summary>
public static class KinStatecraft
{
    public sealed record Kind(string Id, string Name, string Help, string Icon);

    public static readonly IReadOnlyList<Kind> Kinds = new Kind[]
    {
        new("agitate", "Work our kin",
            "Organisers, printing and travel among our own people in a foreign region. Unrest there rises, the country's grievance with us rises, and a service that notices will say so in public.",
            "movement_legitimacy"),
        new("volunteers", "Send volunteers",
            "Our own men, crossing quietly to a rising of their kin on the other side of a border we share. No declaration and no war - until somebody proves they were ours.",
            "movement_fundraise"),
        new("found_proxy", "Found a movement",
            "Turn our people's grievance in a foreign region into a movement of their own: its own name, its own flag, its own fighters - and us behind it, which it will deny for as long as it can.",
            "movement_headquarters"),
        new("open_administration", "Open our offices",
            "Documents, payments, schools and a court people trust more than the host's: the flag above the door stays theirs while the paperwork inside is ours. It takes tax the host never sees leave, and it ends the day somebody proves whose it is.",
            "movement_legitimacy"),
        new("close_administration", "Close our offices",
            "Hand the region back to the host's paperwork. Nothing taken so far is returned, and nobody forgets who was there.",
            "movement_move")
    };

    public static Kind Of(string id) =>
        id is null ? null : Kinds.FirstOrDefault(kind => string.Equals(kind.Id, id, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------- measuring them ----

    /// <summary>
    /// How many of the country's own peoples live in this region, out of how many people it
    /// has. Answered out of the demography rather than a claim, which is what makes it true of
    /// exactly the regions where it is true.
    /// </summary>
    public static (long Ours, long Total, float Share) Kin(Campaign campaign, Nation country, ScenarioState region)
    {
        if (campaign is null || country is null || region is null || region.Population <= 0) return (0L, 0L, 0f);
        var ours = 0L;
        foreach (var group in region.PopulationGroups)
        {
            if (group is null || group.Size <= 0) continue;
            if (DemographyCatalog.IsPrimary(country.Tag, group.Culture)) ours += group.Size;
        }
        if (ours <= 0) return (0L, region.Population, 0f);
        return (ours, region.Population, M.Clamp01(ours / (float)region.Population));
    }

    /// <summary>The country that holds this region, or null for ground nobody's rules reach.</summary>
    public static Nation HostOf(Campaign campaign, ScenarioState region) =>
        campaign?.Nations is null || region is null
            ? null
            : campaign.Nations.Find(nation => nation is not null && nation.MapIndex == region.NationMapIndex);

    /// <summary>
    /// A movement of our people with a following in this region: the one our volunteers would
    /// be joining. Its peoples are what it says it speaks for, and its following is either
    /// support there or fighters already hidden there.
    /// </summary>
    public static Nation KinMovement(Campaign campaign, Nation country, ScenarioState region)
    {
        if (campaign is null || country is null || region is null) return null;
        foreach (var candidate in campaign.Nations)
        {
            var state = candidate?.Movement;
            if (state is null || state.Dissolved || candidate.Id == country.Id) continue;
            if (!SpeaksFor(country, candidate)) continue;
            if (state.Support.GetValueOrDefault(region.MapIndex) > 0.01f ||
                state.Infiltrated.GetValueOrDefault(region.MapIndex) > 0f)
                return candidate;
        }
        return null;
    }

    /// <summary>Whether a movement speaks for any of this country's own peoples.</summary>
    private static bool SpeaksFor(Nation country, Nation movement)
    {
        var peoples = movement.Movement?.Peoples;
        if (peoples is { Count: > 0 })
        {
            foreach (var people in peoples)
                if (DemographyCatalog.IsPrimary(country.Tag, people)) return true;
            return false;
        }
        var definition = Movements.DefinitionOf(movement);
        if (definition?.Peoples is not { Count: > 0 }) return false;
        foreach (var people in definition.Peoples)
            if (DemographyCatalog.IsPrimary(country.Tag, people)) return true;
        return false;
    }

    /// <summary>Whether this ground is held by somebody who can be worked from across a border.</summary>
    private static bool Answerable(Nation host) => host is not null && host.Movement is not { Landless: true };

    // ------------------------------------------------------------- the price -------

    /// <summary>What one operation costs, in billions, before the treasury is asked for it.</summary>
    public static float Cost(Campaign campaign, Nation country, ScenarioState region, string action)
    {
        if (campaign?.Defines is null || country is null || region is null) return 0f;
        var (ours, _, share) = Kin(campaign, country, region);
        var millions = ours / 1_000_000f;
        switch (action)
        {
            case "agitate":
                return MathF.Max(campaign.Defines.KinAgitationCashFloor,
                    campaign.Defines.KinAgitationCashPerMillion * millions * (0.5f + share));
            case "volunteers":
            {
                var men = Volunteers(campaign, country, region);
                return campaign.Defines.KinVolunteersCashPerThousand * men / 1000f;
            }
            case "found_proxy":
                return MathF.Max(campaign.Defines.KinAgitationCashFloor,
                    campaign.Defines.KinProxyCashPerMillion * millions * (0.5f + share));
            case "open_administration":
                return ParallelAdministration.OpenCost(campaign, country, region);
            case "close_administration":
                return 0f;
            default:
                return 0f;
        }
    }

    /// <summary>The men one operation would put across: our people there, with a floor so a small people can still send somebody.</summary>
    public static float Volunteers(Campaign campaign, Nation country, ScenarioState region)
    {
        if (campaign?.Defines is null) return 0f;
        var (ours, _, _) = Kin(campaign, country, region);
        var men = campaign.Defines.KinVolunteersPerMillion * ours / 1_000_000f;
        return MathF.Max(campaign.Defines.KinVolunteersFloor, men);
    }

    private static string CooldownKey(string action, int regionMapIndex) => $"kin_{action}:{regionMapIndex}";

    /// <summary>Days until this operation can be repeated against this region, or 0 for now.</summary>
    public static int DaysRemaining(Campaign campaign, Nation country, ScenarioState region, string action)
    {
        if (campaign is null || country is null || region is null) return 0;
        if (!country.DecisionAvailableDay.TryGetValue(CooldownKey(action, region.MapIndex), out var day)) return 0;
        return Math.Max(0, day - campaign.ElapsedDays);
    }

    // ------------------------------------------------------------- whether -------

    /// <summary>
    /// Why this cannot be done, in one sentence, or an empty string when it can. The player is
    /// owed the reason rather than a disabled button, and the AI asks the same question, so it
    /// cannot take a shortcut the player is denied.
    /// </summary>
    public static string Refusal(Campaign campaign, Nation country, ScenarioState region, string action)
    {
        if (campaign is null || country is null || region is null) return Loc.Get("This cannot be done.");
        if (country.Movement is not null) return Loc.Get("A movement works a country; a country works its own people.");
        if (campaign.StatesOf(country).Length == 0) return Loc.Get("A country with no ground of its own has no people to work abroad.");
        if (Of(action) is null) return Loc.Get("This cannot be done.");
        // The offices are a different machine with their own rules; ask them rather than
        // repeating half of them here.
        if (action == "open_administration")
            return ParallelAdministration.Refusal(campaign, country, region);
        if (action == "close_administration")
            return ParallelAdministration.Ours(campaign, country, region)
                ? "" : Loc.Get("We have no offices in this region to close.");
        var host = HostOf(campaign, region);
        if (host is null || host.Id == country.Id) return Loc.Get("These are our own people, in our own country; there is no border to work across.");
        // A movement that has won ground is a country like any other; one that has none cannot answer for territory.
        if (!Answerable(host)) return Loc.Get("That ground belongs to no country that could answer for it.");
        if (CampaignDiplomacy.Peek(campaign, country.Id, host.Id)?.Stance == DiplomaticStance.AtWar)
            return Loc.Get("We are already at war with them; nothing about this would be deniable.");
        var (ours, _, share) = Kin(campaign, country, region);
        if (ours <= 0 || share < campaign.Defines.KinAgitationMinShare)
            return Loc.Format("Too few of our people live in {0} for this to be worth the risk.", region.DisplayName);
        var remaining = DaysRemaining(campaign, country, region, action);
        if (remaining > 0)
            return Loc.Format("Our networks in {0} need {1} more days before they can be asked again.", region.DisplayName, remaining);
        switch (action)
        {
            case "agitate":
                if (region.Radicalism >= 0.98f) return Loc.Format("{0} is already as restless as it can be.", region.DisplayName);
                if (country.Treasury < Cost(campaign, country, region, action))
                    return Loc.Get("The treasury cannot pay for the organisers this would need.");
                return "";
            case "volunteers":
            {
                if (!CampaignDiplomacy.SharesLandBorder(campaign, country.MapIndex, host.MapIndex))
                    return Loc.Get("Volunteers have to be able to walk there: only across a border we share.");
                if (KinMovement(campaign, country, region) is null)
                    return Loc.Format("Nobody of ours is fighting in {0} for volunteers to join.", region.DisplayName);
                var men = Volunteers(campaign, country, region);
                if (country.ManpowerReserve < men) return Loc.Get("We have no men to spare for this.");
                if (country.Treasury < Cost(campaign, country, region, action)) return Loc.Get("The treasury cannot pay for it.");
                return "";
            }
            case "found_proxy":
            {
                if (share < campaign.Defines.KinProxyMinShare)
                    return Loc.Format("Founding a movement needs a firmer footing in {0}: at least {1}% of the region must be ours.",
                        region.DisplayName, MathF.Round(campaign.Defines.KinProxyMinShare * 100f));
                if (!CampaignDiplomacy.SharesLandBorder(campaign, country.MapIndex, host.MapIndex))
                    return Loc.Get("A movement needs a lifeline: only across a border we share.");
                if (KinMovement(campaign, country, region) is not null)
                    return Loc.Format("Our people in {0} already have a movement; strengthen that one rather than splitting them.", region.DisplayName);
                if (country.Treasury < Cost(campaign, country, region, action)) return Loc.Get("The treasury cannot pay for it.");
                if (country.PoliticalPoints < campaign.Defines.KinProxyPoliticalCost)
                    return Loc.Get("No government can be seen founding this against its own credit.");
                if (Movements.FreeMapIndex(campaign) <= 0) return Loc.Get("The map has no room for another country.");
                return "";
            }
            default:
                return Loc.Get("This cannot be done.");
        }
    }

    // ------------------------------------------------------------- doing it -------

    /// <summary>
    /// The salt the discovery roll uses, kept apart from the salt that decides whether the
    /// computer acts at all. Two decisions that share one die are not two decisions: with a
    /// single salt, a computer operation was caught every time it took place, because acting
    /// required a roll below 2-10 % and being caught only below 15-45 %.
    ///
    /// The region is multiplied past the whole range decision salts use (regions number in the
    /// tens of thousands), so a discovery die cannot be another decision's die either. The
    /// multipliers stay under the integer limit for the largest map.
    /// </summary>
    internal static int DiscoverySalt(ScenarioState region, string action) =>
        region.MapIndex * 50_000 + (action switch
        {
            "agitate" => 1,
            "volunteers" => 2,
            "found_proxy" => 3,
            _ => 4
        });

    /// <summary>Do one operation. The caller has already asked <see cref="Refusal"/>.</summary>
    public static bool Do(Campaign campaign, Nation country, ScenarioState region, string action)
    {
        if (campaign?.Defines is null || country is null || region is null) return false;
        // The offices keep their own books; they are not a raid with a discovery roll.
        if (action == "open_administration") return ParallelAdministration.Open(campaign, country, region);
        if (action == "close_administration")
        {
            if (!ParallelAdministration.Ours(campaign, country, region)) return false;
            ParallelAdministration.Close(campaign, region, "closed");
            return true;
        }
        var host = HostOf(campaign, region);
        if (host is null) return false;
        var tie = CampaignDiplomacy.Get(campaign, country.Id, host.Id);
        var (_, _, share) = Kin(campaign, country, region);
        var discovery = action switch
        {
            "agitate" => campaign.Defines.KinAgitationDiscovery,
            "found_proxy" => campaign.Defines.KinProxyDiscovery,
            _ => campaign.Defines.KinAgitationDiscovery * 0.7f
        };
        // The die that says whether we are caught must not be the die that decided to act. The
        // computer calls Do for a country, day and region after a roll below its own chance -
        // 2 % to 10 % - so one salt for both made every computer operation caught by
        // construction: acting proved the roll was under the discovery threshold. Ten years of
        // the bench showed 26 agitations, 26 exposures, and none of it was bad luck.
        var caught = MovementRules.Roll(campaign.Seed, country.Id, campaign.ElapsedDays,
            DiscoverySalt(region, action)) < discovery;
        var cost = Cost(campaign, country, region, action);
        switch (action)
        {
            case "agitate":
            {
                country.Treasury -= cost;
                // Unrest is what the secession and rising rules read, so working a people is a
                // real danger to the country that holds them rather than a number kept aside.
                var gain = campaign.Defines.KinAgitationRadicalism * (0.5f + share * 0.5f);
                region.Radicalism = M.Clamp01(region.Radicalism + gain);
                AddGrievance(tie, host.Id,
                    campaign.Defines.KinAgitationGrievance * (0.5f + share) +
                    (caught ? campaign.Defines.KinAgitationCaughtGrievance : 0f));
                OpinionSystem.Remember(campaign, tie, caught ? "caught_working_our_people" : "working_our_people");
                country.DecisionAvailableDay[CooldownKey(action, region.MapIndex)] =
                    campaign.ElapsedDays + Math.Max(1, campaign.Defines.KinAgitationCooldownDays);
                campaign.Chronicle.Record(campaign, caught ? "kin_agitation_exposed" : "kin_agitation",
                    country.Id, host.Id, region.DisplayName, isPublic: caught);
                DiagnosticLogs.Write("simulation",
                    $"KIN_AGITATE by={country.Tag} host={host.Tag} region={region.Name} share={share:0.###} caught={(caught ? 1 : 0)} cost={cost:0.####} day={campaign.ElapsedDays}");
                break;
            }
            case "volunteers":
            {
                var movement = KinMovement(campaign, country, region);
                if (movement?.Movement is null) return false;
                var men = Volunteers(campaign, country, region);
                country.Treasury -= cost;
                country.ManpowerReserve = MathF.Max(0f, country.ManpowerReserve - men);
                // Into the fighters the region already hides, which is the number an uprising is
                // called from - and the number a crackdown finds, which is the risk being taken.
                var hidden = movement.Movement.Infiltrated.GetValueOrDefault(region.MapIndex);
                movement.Movement.Infiltrated[region.MapIndex] = hidden + men;
                AddGrievance(tie, host.Id,
                    campaign.Defines.KinVolunteersGrievance * (0.5f + share) +
                    (caught ? campaign.Defines.KinVolunteersCaughtGrievance : 0f));
                OpinionSystem.Remember(campaign, tie, caught ? "proved_sending_volunteers" : "suspects_our_volunteers");
                country.DecisionAvailableDay[CooldownKey(action, region.MapIndex)] =
                    campaign.ElapsedDays + Math.Max(1, campaign.Defines.KinVolunteersCooldownDays);
                campaign.Chronicle.Record(campaign, caught ? "kin_volunteers_exposed" : "kin_volunteers",
                    country.Id, host.Id, region.DisplayName, isPublic: caught);
                movement.Movement.LastReport =
                    Loc.Format("Volunteers from {0} have joined it in {1}.", country.DisplayName, region.DisplayName);
                DiagnosticLogs.Write("simulation",
                    $"KIN_VOLUNTEERS by={country.Tag} host={host.Tag} movement={movement.Tag} region={region.Name} men={men:0} caught={(caught ? 1 : 0)} day={campaign.ElapsedDays}");
                break;
            }
            case "found_proxy":
            {
                var born = FoundProxy(campaign, country, host, region);
                if (born is null) return false;
                country.Treasury -= cost;
                country.PoliticalPoints = MathF.Max(0f, country.PoliticalPoints - campaign.Defines.KinProxyPoliticalCost);
                AddGrievance(tie, host.Id,
                    campaign.Defines.KinProxyGrievance * (0.5f + share) +
                    (caught ? campaign.Defines.KinProxyCaughtGrievance : 0f));
                OpinionSystem.Remember(campaign, tie, caught ? "caught_founding_proxy" : "founded_proxy_in_our_land");
                country.DecisionAvailableDay[CooldownKey(action, region.MapIndex)] =
                    campaign.ElapsedDays + Math.Max(1, campaign.Defines.KinProxyCooldownDays);
                campaign.Chronicle.Record(campaign, caught ? "proxy_founded_exposed" : "proxy_founded",
                    country.Id, host.Id, region.DisplayName, isPublic: caught);
                DiagnosticLogs.Write("simulation",
                    $"KIN_PROXY by={country.Tag} host={host.Tag} movement={born.Tag} region={region.Name} share={share:0.###} caught={(caught ? 1 : 0)} day={campaign.ElapsedDays}");
                born.Movement.LastReport = Loc.Format("Founded in {0}, with {1} behind it.", region.DisplayName, country.DisplayName);
                break;
            }
            default:
                return false;
        }
        tie.LastActionDay = campaign.ElapsedDays;
        campaign.MarkStrategicChange();
        return true;
    }
    /// <summary>The grievance belongs to the side that was wronged, which is the host.</summary>
    private static void AddGrievance(DiplomaticRelation tie, int wrongedId, float amount)
    {
        if (tie is null || amount <= 0f) return;
        if (tie.A == wrongedId) tie.GrievanceA = MathF.Min(100f, tie.GrievanceA + amount);
        else if (tie.B == wrongedId) tie.GrievanceB = MathF.Min(100f, tie.GrievanceB + amount);
    }

    // ------------------------------------------------------------- founding one ----

    /// <summary>
    /// Found a movement of our people in somebody else's region.
    ///
    /// It is a country in every way the game knows except that it holds no ground: its own tag,
    /// its own name and colour, a headquarters in that region, a following among our kin, cadres
    /// we paid for, our money and our recognition behind it - and no record anywhere that it was
    /// ours, which is the whole point. It then lives its own life through the movement rules:
    /// it campaigns, it recruits, it may rise, and if it rises it is a war between the host and
    /// the people it says it speaks for, which is a war we are not in until we choose to be.
    /// </summary>
    private static Nation FoundProxy(Campaign campaign, Nation country, Nation host, ScenarioState region)
    {
        if (campaign?.Defines is null) return null;
        var culture = DemographyCatalog.PrimaryCulture(country.Tag);
        if (string.IsNullOrWhiteSpace(culture)) return null;
        var tag = FreeProxyTag(campaign, culture, host.Tag);
        var mapIndex = tag.Length == 0 ? 0 : Movements.FreeMapIndex(campaign);
        if (mapIndex <= 0) return null;

        var (_, _, share) = Kin(campaign, country, region);
        var (ours, _, _) = Kin(campaign, country, region);
        var cadres = MathF.Max(400f, campaign.Defines.KinProxyCadresPerMillion * ours / 1_000_000f);
        var (kind, id) = NationNaming.BaseFor(campaign, country, new[] { region.MapIndex }, culture, "movement", rising: true);
        if (string.IsNullOrWhiteSpace(kind)) { kind = "culture"; id = culture; }

        var born = campaign.AddNation(new Nation
        {
            Tag = tag,
            Iso = tag,
            Name = "",
            Continent = host.Continent,
            Region = host.Region,
            Color = SecessionColours.Choose(campaign, host, tag, new[] { region.MapIndex },
                DemographyCatalog.Culture(culture)?.Colour ?? ""),
            MapIndex = mapIndex,
            CapitalName = region.DisplayName,
            Population = 0,
            Gdp = 0.01f,
            Treasury = 0.05f,
            ManpowerReserve = cadres,
            OutputScale = 1f,
            Stability = 0.55f,
            WarSupport = 0.85f,
            Government = "provisional_government",
            Ideology = "nationalism",
            PoliticsChangedDay = campaign.ElapsedDays,
            FoundNameKind = kind,
            FoundNameId = id
        });
        born.FoundNameForm = NationNaming.FormFor(born.Government, born.Ideology, "", culture);
        born.Name = NationNaming.Render(campaign, born, false);
        if (string.IsNullOrWhiteSpace(born.Name)) born.Name = DemographyCatalog.CultureName(culture);

        var peoples = DemographyCatalog.PrimaryCultures(country.Tag);
        born.Movement = new MovementState
        {
            Id = tag.ToLowerInvariant(),
            Landless = true,
            HeadquartersStateMapIndex = region.MapIndex,
            Cadres = cadres,
            Legitimacy = campaign.Defines.KinProxyLegitimacy
        };
        foreach (var people in peoples) born.Movement.Peoples.Add(people);
        born.Movement.Homeland.Add(region.Id);
        born.Movement.Support[region.MapIndex] = M.Clamp01(share * campaign.Defines.KinProxySupportSeed);
        born.Movement.SponsoredBy[country.Id] = campaign.Defines.MovementSponsorPerYear;
        born.Movement.RecognisedBy.Add(country.Id);
        // Who it is *for*, declared so every later check of "is this our people" answers the same
        // way without needing the founding country at hand.
        if (peoples.Count > 0) DemographyCatalog.Declare(born.Tag, peoples);
        born.FoundedCultures.Clear();
        born.FoundedCultures.AddRange(peoples);

        campaign.MovementRevision++;
        Movements.Place(campaign);
        return born;
    }

    /// <summary>
    /// A free three-letter tag from the people it speaks for, the way a secession takes one:
    /// the first three letters of the people's name, then numbered forms if those are taken.
    /// </summary>
    private static string FreeProxyTag(Campaign campaign, string culture, string avoid)
    {
        var letters = new string((culture ?? "").Where(char.IsLetter).ToArray()).ToUpperInvariant();
        if (letters.Length < 2) letters = (avoid ?? "XX").ToUpperInvariant();
        for (var suffix = 1; suffix <= 99; suffix++)
        {
            var candidate = letters.Length >= 3 && suffix == 1
                ? letters[..3]
                : $"{letters[..1]}{suffix:00}";
            if (string.Equals(candidate, avoid, StringComparison.OrdinalIgnoreCase)) continue;
            if (campaign.NationByTag(candidate) is not null) continue;
            return candidate;
        }
        return "";
    }

    // ------------------------------------------------------------- the computer ----

    // One scan a month, reused by every country that day: the ground worth working, worked out
    // from the demography in a single pass rather than a scan of the world for every country.
    // Per campaign: the scan was one static, stamped with the last campaign that asked. Two worlds
    // in one process (a network client holding the host's snapshot beside its own) took turns
    // overwriting it, so each rescanned mid-month on the day's demography and the two drifted -
    // the bench's "two worlds in one process do not read each other's notes" failure.
    private sealed class ScanState
    {
        public int Month = -1;
        public readonly Dictionary<int, List<Candidate>> Candidates = new();
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, ScanState> Scans = new();
    internal static int LastScanKinEvaluations { get; private set; }

    private readonly record struct Candidate(Nation Host, ScenarioState Region, float Share);

    /// <summary>Once a month: which countries have enough of their own people in whose ground.</summary>
    private static Dictionary<int, List<Candidate>> Scan(Campaign campaign)
    {
        var month = campaign.ElapsedDays / 30;
        var state = Scans.GetValue(campaign, _ => new ScanState());
        if (state.Month == month) return state.Candidates;
        foreach (var _ in ScanStages(campaign, state)) { }
        return state.Candidates;
    }

    /// <summary>The same month scan, yielded between regions so one frame does not carry it all.</summary>
    private static IEnumerable<int> ScanStages(Campaign campaign, ScanState state)
    {
        state.Month = campaign.ElapsedDays / 30;
        var Candidates = state.Candidates;
        Candidates.Clear();
        LastScanKinEvaluations = 0;
        if (campaign.States is null || campaign.Nations is null) yield break;

        // Which countries claim which people, built once: the reverse of the demography's own
        // table, so a region's population groups are read once each rather than once per
        // country in the world.
        var claims = new Dictionary<string, List<Nation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var nation in campaign.Nations)
        {
            if (nation is null || nation.Movement is not null || string.IsNullOrEmpty(nation.Tag)) continue;
            foreach (var culture in DemographyCatalog.PrimaryCultures(nation.Tag))
            {
                if (string.IsNullOrWhiteSpace(culture)) continue;
                if (!claims.TryGetValue(culture, out var list)) claims[culture] = list = new List<Nation>(2);
                if (!list.Contains(nation)) list.Add(nation);
            }
        }
        if (claims.Count == 0) yield break;

        var minimum = campaign.Defines?.KinAgitationMinShare ?? 0.15f;
        // Who holds what, once: a region's holder is asked of the whole world for every region
        // otherwise, which is the kind of quiet waste that only shows up on a big map.
        var holders = new Dictionary<int, Nation>(campaign.Nations.Count);
        foreach (var nation in campaign.Nations)
            if (nation is not null) holders.TryAdd(nation.MapIndex, nation);
        var evaluated = new HashSet<int>();
        var scanned = 0;
        foreach (var region in campaign.States)
        {
            using (SimProfiler.Measure("kin:scan"))
            {
            if (region is null || region.Population <= 0) continue;
            if (!holders.TryGetValue(region.NationMapIndex, out var host)) continue;
            if (!Answerable(host)) continue;
            evaluated.Clear();
            foreach (var group in region.PopulationGroups)
            {
                if (group is null || group.Size <= 0) continue;
                if (!claims.TryGetValue(group.Culture, out var claimants)) continue;
                foreach (var country in claimants)
                {
                    if (country.Id == host.Id) continue;
                    // A culture occurs in many class/sector/religion rows. Kin already
                    // totals ALL those rows: repeating that same full scan for every row
                    // made the monthly pass quadratic in a mature region's population mix.
                    // First occurrence and candidate ordering are unchanged; no RNG is used.
                    if (!evaluated.Add(country.Id)) continue;
                    LastScanKinEvaluations++;
                    var (ours, _, share) = Kin(campaign, country, region);
                    if (ours <= 0 || share < minimum) continue;
                    if (!Candidates.TryGetValue(country.Id, out var list))
                        Candidates[country.Id] = list = new List<Candidate>(2);
                    // One region per host is enough for a monthly decision, and it is the best one.
                    var index = list.FindIndex(candidate => candidate.Host.Id == host.Id);
                    if (index < 0) list.Add(new Candidate(host, region, share));
                    else if (list[index].Share < share) list[index] = new Candidate(host, region, share);
                }
            }
            }
            if (++scanned % 48 == 0) yield return 0;
        }
    }

    /// <summary>
    /// What every country the game runs does about its kin abroad: rarely, and only against a
    /// country it already thinks badly of. It asks the same questions the player is asked, so
    /// nothing here is a shortcut round the rules.
    /// </summary>
    public static void Ai(Campaign campaign)
    {
        if (campaign?.Defines is null || !MechanicFeatures.SystemEnabled(campaign, nameof(KinStatecraft))) return;
        foreach (var _ in AiStages(campaign, Scan(campaign))) { }
    }

    /// <summary>The decisions themselves, one country at a time for the smoothed step.</summary>
    private static IEnumerable<int> AiStages(Campaign campaign, Dictionary<int, List<Candidate>> Candidates)
    {
        if (Candidates.Count == 0) yield break;
        var defines = campaign.Defines;
        const int cycle = 30;
        // A private copy, because founding a movement adds a country to the list this loop is
        // reading. That is the crash the campaign reported on 24 July 2026: "Collection was
        // modified; enumeration operation may not execute." A nation founded this month joins
        // next month's deliberations - which is what MovementDealings already does with its
        // own day list, and for the same reason.
        var nations = campaign.Nations.ToArray();
        foreach (var country in nations)
        {
            using (SimProfiler.Measure("kin:ai"))
            {
            if (country is null || country.IsHuman || country.Movement is not null) continue;
            if (!Candidates.TryGetValue(country.Id, out var list) || list.Count == 0) continue;
            if (!MovementRules.OnItsDay(campaign, country.Id, cycle)) continue;
            var annual = MathF.Max(0.01f, country.WeeklyTaxIncome * 52f);
            if (country.Treasury < defines.AiKinTreasuryYears * annual) continue;
            foreach (var candidate in list)
            {
                var tie = CampaignDiplomacy.Peek(campaign, country.Id, candidate.Host.Id);
                if (tie is null || tie.Opinion > defines.AiKinOpinion) continue;
                if (tie.Stance is DiplomaticStance.Allied or DiplomaticStance.AtWar) continue;
                var roll = MovementRules.Roll(campaign.Seed, country.Id, campaign.ElapsedDays, candidate.Region.MapIndex);
                // The rarest act first, and the one that changes the map: a movement of our own
                // people, founded in their ground. Its band is the smallest, so the same roll that
                // would only have paid for leaflets can instead buy a standard.
                if (roll < defines.AiKinProxyChance &&
                    Refusal(campaign, country, candidate.Region, "found_proxy").Length == 0 &&
                    Do(campaign, country, candidate.Region, "found_proxy")) continue;
                // Then the slow one: offices that outlive any single rising, when our people are
                // already a quarter of the region and nobody has got there first.
                if (roll < defines.AiKinAdminChance &&
                    Refusal(campaign, country, candidate.Region, "open_administration").Length == 0 &&
                    Do(campaign, country, candidate.Region, "open_administration")) continue;
                // Volunteers next when somebody of ours is already fighting there: a rising
                // that fails for want of men is a chance spent.
                if (roll < defines.AiKinVolunteersChance &&
                    KinMovement(campaign, country, candidate.Region) is not null &&
                    Refusal(campaign, country, candidate.Region, "volunteers").Length == 0 &&
                    Do(campaign, country, candidate.Region, "volunteers")) continue;
                if (roll < defines.AiKinAgitationChance &&
                    Refusal(campaign, country, candidate.Region, "agitate").Length == 0)
                    Do(campaign, country, candidate.Region, "agitate");
            }
            }
            yield return 0;
        }
    }

    /// <summary>The month's look at the world and its decisions, in stages for the smoothed step.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays <= 0) yield break;
        if (campaign.Defines is null || !MechanicFeatures.SystemEnabled(campaign, nameof(KinStatecraft))) yield break;
        var state = Scans.GetValue(campaign, _ => new ScanState());
        if (state.Month != campaign.ElapsedDays / 30)
            foreach (var _ in ScanStages(campaign, state)) yield return 0;
        foreach (var _ in AiStages(campaign, state.Candidates)) yield return 0;
    }

    /// <summary>The month's look at the world; the decisions themselves are the AI's.</summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>What the month's scan found, for the bench and for tests that must not guess.</summary>
    internal static string ScanSummary(Campaign campaign)
    {
        var Candidates = Scan(campaign);
        var parts = new List<string>();
        foreach (var pair in Candidates)
            parts.Add($"{pair.Key}:" + string.Join(";", pair.Value.Select(c => $"{c.Host.Tag}@{c.Region.MapIndex}={c.Share:0.##}")));
        return $"candidates={Candidates.Count} [{string.Join(" | ", parts)}]";
    }
}
