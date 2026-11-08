using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Army
{
    /// <summary>
    /// The country this formation really belongs to while it serves another's war as volunteers
    /// (Expeditions), or -1. While lent it flies the host's colours - <see cref="NationId"/> is the
    /// host - so every battle, front and supply rule treats it as the host's own; it goes home
    /// when the host's wars end, when the sender calls it back, or when the two fall out.
    /// </summary>
    public int LentById = -1;
    public int LentDay = -1;
}

public sealed partial class Defines
{
    /// <summary>
    /// Moving a formation over ground a march cannot cross - an exclave, another continent, a
    /// friend's war - by air and sea: kilometres a day, the least and most days it is out of
    /// action, and what it costs per division per thousand kilometres (billions).
    /// </summary>
    public float AirliftKmPerDay { get; set; } = 500f;
    public int AirliftMinDays { get; set; } = 4;
    public int AirliftMaxDays { get; set; } = 40;
    public float AirliftCostPerDivisionThousandKm { get; set; } = 0.012f;
    /// <summary>Volunteers: the political credit an order costs, the share of a country's divisions
    /// that may serve abroad at once, and the AI's least opinion of a country it sends them to.</summary>
    public float VolunteerPoliticalCost { get; set; } = 8f;
    public float VolunteerShare { get; set; } = 0.25f;
    public float AiVolunteerOpinion { get; set; } = 35f;
    /// <summary>The chance a week that a willing AI country sends volunteers to a friend losing a war.</summary>
    public float AiVolunteerChance { get; set; } = 0.04f;
    /// <summary>Tension each enemy of the host feels towards a country whose volunteers fight it.</summary>
    public float VolunteerTension { get; set; } = 10f;
}

/// <summary>
/// Sending forces far away: by air and sea where no march goes (an exclave, an ally across the
/// ocean, a base abroad held "just in case"), and as volunteers into somebody else's war.
///
/// Volunteers are the oldest way of fighting a war without declaring one - Spain in 1936, Korea
/// in 1950, Donbas in 2014. The sender stays at peace; its formations change flags for the
/// duration and fight under the host's command, and every government on the other side knows
/// whose they are. The same rules for every country: nobody is named here.
/// </summary>
public static class Expeditions
{
    public const string Airlift = "airlift";
    public const string Volunteer = "volunteer";
    public const string Recall = "recall";

    /// <summary>Days in transit and what it costs, for a formation going to a province.</summary>
    public static (int Days, float Cost) Quote(Campaign c, Army army, int province)
    {
        var d = c.Defines;
        var km = SupportCore.DistanceKm(c, army.ProvinceId, province);
        var days = Math.Clamp((int)MathF.Ceiling(km / MathF.Max(50f, d.AirliftKmPerDay)), d.AirliftMinDays, Math.Max(d.AirliftMinDays, d.AirliftMaxDays));
        return (days, MathF.Max(0.25f, army.Divisions) * km / 1000f * d.AirliftCostPerDivisionThousandKm);
    }

    /// <summary>Why this formation cannot be flown and shipped there, or "".</summary>
    public static string AirliftRefusal(Campaign c, Nation n, Army army, int province)
    {
        var map = c?.ProvinceMap;
        if (map is null || n is null || army is null || army.NationId != n.Id) return Loc.Get("Not a formation of ours.");
        if (army.InBattle) return Loc.Get("It is in battle and cannot be pulled out.");
        if (army.EmbarkedFleetId >= 0) return Loc.Get("It is already at sea.");
        if (army.CutOff) return Loc.Get("It is cut off: nothing reaches it and nothing leaves.");
        if (army.ReadyDay > c.ElapsedDays) return Loc.Get("It is still regrouping.");
        if (province <= 0 || province > map.ProvinceCount || map.IsWater(province) || c.DestroyedProvinces.Contains(province))
            return Loc.Get("No army can be put down there.");
        var controller = c.ProvinceController is { } controls && province < controls.Length ? controls[province] : map.ProvinceToNation[province];
        if (!MilitaryAccess.CanEnter(c, n.Id, controller)) return Loc.Get("Our forces have no right to be on that ground.");
        if (province == army.ProvinceId) return Loc.Get("It is already there.");
        var (_, cost) = Quote(c, army, province);
        if (n.Treasury < cost) return Loc.Format("The move costs {0:0.##} bn; the treasury cannot pay it.", cost);
        return "";
    }

    /// <summary>Why this country cannot send volunteers to that one, or "".</summary>
    public static string VolunteerRefusal(Campaign c, Nation sponsor, Nation host)
    {
        if (c is null || sponsor is null || host is null || sponsor.Id == host.Id) return Loc.Get("No country to send them to.");
        if (sponsor.Movement is not null) return Loc.Get("A movement has no army to lend.");
        if (!c.HasOwnedLand(host)) return Loc.Get("They hold no ground to fight on.");
        if (MilitaryAccess.Hostile(c, sponsor.Id, host.Id)) return Loc.Get("We are at war with them.");
        if (!c.Wars.Any(w => !w.Ended && w.Involves(host.Id))) return Loc.Get("They are not at war.");
        // A country already in the war beside them sends its army, not volunteers.
        if (c.Wars.Any(w => !w.Ended && w.Involves(host.Id) && w.Involves(sponsor.Id))) return Loc.Get("We are already in their war.");
        if (sponsor.PoliticalPoints < c.Defines.VolunteerPoliticalCost)
            return Loc.Format("The order costs {0:0} political credit; there is {1:0.#}.", c.Defines.VolunteerPoliticalCost, sponsor.PoliticalPoints);
        if (LentDivisions(c, sponsor) >= Cap(c, sponsor)) return Loc.Get("As many of our divisions serve abroad as the country can spare.");
        return "";
    }

    public static float LentDivisions(Campaign c, Nation sponsor) =>
        c.Armies.Where(a => a.LentById == sponsor.Id).Sum(a => a.Divisions);

    /// <summary>The divisions a country can spare abroad: a share of all it has, lent or not.</summary>
    public static float Cap(Campaign c, Nation sponsor) =>
        MathF.Max(1f, (c.Armies.Where(a => a.NationId == sponsor.Id).Sum(a => a.Divisions) + LentDivisions(c, sponsor)) * c.Defines.VolunteerShare);

    /// <summary>Where volunteers arriving in a country are put down: its government's seat.</summary>
    public static int Arrival(Campaign c, Nation host) => CapitalCore.SupplyOriginProvinceId(c, host);

    public static bool Fly(Campaign c, Nation n, Army army, int province)
    {
        if (AirliftRefusal(c, n, army, province).Length > 0) return false;
        var (days, cost) = Quote(c, army, province);
        n.Treasury -= cost;
        Place(c, army, province, days);
        DiagnosticLogs.Write("orders", $"AIRLIFT army={army.Id} nation={n.Tag} province={province} days={days} cost={cost:0.###}");
        return true;
    }

    public static bool Lend(Campaign c, Nation sponsor, Army army, Nation host)
    {
        if (army is null || army.NationId != sponsor?.Id || VolunteerRefusal(c, sponsor, host).Length > 0) return false;
        var arrival = Arrival(c, host);
        if (arrival <= 0 || army.InBattle || army.EmbarkedFleetId >= 0 || army.CutOff) return false;
        var (days, cost) = Quote(c, army, arrival);
        sponsor.PoliticalPoints -= c.Defines.VolunteerPoliticalCost;
        sponsor.Treasury -= cost;
        army.LentById = sponsor.Id;
        army.LentDay = c.ElapsedDays;
        army.NationId = host.Id;
        army.ArmyGroupId = -1;
        Place(c, army, arrival, days);

        // The host remembers who came; the side they fight against remembers it too.
        if (CampaignDiplomacy.Get(c, host.Id, sponsor.Id) is { } grateful) OpinionSystem.Remember(c, grateful, "sent_volunteers");
        foreach (var war in c.Wars.Where(w => !w.Ended && w.Involves(host.Id)).ToList())
            foreach (var enemyId in war.EnemiesOf(host.Id).Distinct().ToList())
            {
                if (enemyId == sponsor.Id) continue;
                if (CampaignDiplomacy.Get(c, enemyId, sponsor.Id) is { } angry)
                {
                    OpinionSystem.Remember(c, angry, "volunteers_against_us");
                    angry.Tension = MathF.Min(100f, angry.Tension + c.Defines.VolunteerTension);
                }
            }
        // One headline for a contingent, not one per formation in it.
        if (!c.Armies.Any(a => a != army && a.LentById == sponsor.Id && a.NationId == host.Id && a.LentDay == c.ElapsedDays))
            c.Chronicle.Record(c, "volunteers_sent", sponsor.Id, host.Id, "", c.ProvinceMap is null ? "" :
            c.States.Find(s => s.MapIndex == c.ProvinceMap.ProvinceToState[arrival])?.DisplayName ?? "");
        DiagnosticLogs.Write("orders", $"VOLUNTEERS sponsor={sponsor.Tag} host={host.Tag} army={army.Id} divisions={army.Divisions:0.#}");
        return true;
    }

    /// <summary>Bring a lent formation home, to the sender's seat.</summary>
    public static bool Return(Campaign c, Army army)
    {
        if (army is null || army.LentById < 0) return false;
        var sponsor = c.NationById(army.LentById);
        army.LentById = -1;
        army.LentDay = -1;
        if (sponsor is null || sponsor.Population <= 0 || !c.HasOwnedLand(sponsor)) return false; // nobody to go home to: it stays
        var home = CapitalCore.SupplyOriginProvinceId(c, sponsor);
        if (home <= 0) return false;
        var (days, _) = Quote(c, army, home);
        army.NationId = sponsor.Id;
        army.ArmyGroupId = -1;
        Place(c, army, home, days);
        DiagnosticLogs.Write("orders", $"VOLUNTEERS_HOME sponsor={sponsor.Tag} army={army.Id}");
        return true;
    }

    /// <summary>
    /// Take a garrison out of ground that has just risen, by sea or by air, to the province named
    /// (StrandedGarrisons.AtRising). Unlike an airlift it is not refused for being cut off - the
    /// ring is closing as it leaves, which is the whole reason it leaves - and it pays for the
    /// haste: a share of the men lost on the way, a larger share of the heavy equipment left on
    /// the quay. The caller has already paid the journey (<see cref="Quote"/>).
    /// </summary>
    internal static void Evacuate(Campaign c, Army army, int province, float strengthLoss, float equipmentLoss)
    {
        if (c?.ProvinceMap is null || army is null || province <= 0 || province > c.ProvinceMap.ProvinceCount) return;
        var before = army.Strength;
        army.Strength = M.Clamp01(army.Strength * (1f - M.Clamp01(strengthLoss)));
        army.Equipment = M.Clamp01(army.Equipment * (1f - M.Clamp01(equipmentLoss)));
        var men = (before - army.Strength) * army.Divisions * MilitaryBootstrap.DivisionSize;
        if (men > 0f)
        {
            army.Personnel = Math.Max(0, army.Personnel - (int)MathF.Round(men));
            WarLosses.Record(WarLosses.WarOf(c, army), army.NationId, men * 0.5f, men * 0.5f, 0f);
        }
        var (days, _) = Quote(c, army, province);
        Place(c, army, province, days);
        army.BreakingOut = false;
        army.HoldoutSinceDay = -1;
        army.PocketStores = -1f;
        army.PocketSinceDay = -1;
        army.ReliefDistance = -1;
        DiagnosticLogs.Write("orders", $"EVACUATED army={army.Id} province={province} days={days}");
    }

    /// <summary>Put a formation down somewhere new, out of action for the time the journey took.</summary>
    private static void Place(Campaign c, Army army, int province, int days)
    {
        MilitaryAccess.Provision(army);
        army.Route.Clear(); army.PlannedRoute.Clear(); army.PostBattleWaypoints.Clear(); army.MoveProgress = 0f;
        army.OrderProvinceId = army.OrderStateMapIndex = army.ObjectiveStateMapIndex = -1;
        army.AssignedWarId = army.FrontLineId = -1; army.RedeployTarget = -1;
        army.DirectOrder = army.SupportOnly = false;
        army.OrderKind = ArmyOrder.Manual; army.OrderRouteSteps = army.OrderStepsCompleted = 0;
        foreach (var line in c.FrontLines) line.Posts.Remove(army.Id);
        army.BattleId = -1;
        army.ProvinceId = province;
        army.StateMapIndex = c.ProvinceMap.ProvinceToState[province];
        (army.Latitude, army.Longitude) = c.ProvinceMap.CenterOf(province);
        army.Entrenchment = army.Planning = 0f;
        army.Organisation = MathF.Min(army.Organisation, 0.35f);
        army.ReadyDay = Math.Max(army.ReadyDay, c.ElapsedDays + days);
        c.MarkStrategicChange();
    }

    /// <summary>Daily: volunteers go home when they have nothing left to do; once a week each AI looks for a friend to help.</summary>
    public static void Tick(Campaign c)
    {
        if (!c.StepOpensNewDay) return;
        foreach (var army in c.Armies)
        {
            if (army.LentById < 0 || army.InBattle) continue;
            var host = c.NationById(army.NationId);
            var sponsor = c.NationById(army.LentById);
            var hostAtWar = host is not null && c.Wars.Any(w => !w.Ended && w.Involves(host.Id));
            if (sponsor is null || sponsor.Population <= 0 || !c.HasOwnedLand(sponsor))
            { army.LentById = -1; army.LentDay = -1; continue; }   // the sender is gone: they stay where they fought
            if (!hostAtWar || host is null || !c.HasOwnedLand(host) || MilitaryAccess.Hostile(c, sponsor.Id, host.Id) ||
                c.Wars.Any(w => !w.Ended && w.Involves(host.Id) && w.Involves(sponsor.Id)))
                Return(c, army);
        }
        if (c.Defines.AiVolunteerChance <= 0f || c.ElapsedDays % 7 != 0) return;
        foreach (var sponsor in c.Nations)
        {
            if (sponsor.IsHuman || sponsor.Movement is not null || sponsor.Population <= 0) continue;
            if (c.Wars.Any(w => !w.Ended && w.Involves(sponsor.Id))) continue;   // its own wars come first
            if (MovementRules.Roll(c.Seed, sponsor.Id, c.ElapsedDays, 0x7011) >= c.Defines.AiVolunteerChance * AiTemper.Aggression(c)) continue;
            var host = Friend(c, sponsor);
            if (host is null) continue;
            var room = Cap(c, sponsor) - LentDivisions(c, sponsor);
            foreach (var army in c.Armies.Where(a => a.NationId == sponsor.Id && a.LentById < 0 && !a.InBattle &&
                                                       a.EmbarkedFleetId < 0 && !a.CutOff && a.AssignedWarId < 0)
                         .OrderByDescending(a => a.Organisation * a.Divisions).ThenBy(a => a.Id).ToList())
            {
                if (army.Divisions > room) continue;
                if (!Lend(c, sponsor, army, host)) break;
                room -= army.Divisions;
                if (room < 1f) break;
            }
        }
    }

    /// <summary>
    /// The friend at war a country would send its sons to: liked enough, losing on paper, and
    /// fighting nobody the sender is afraid of - a neighbour much stronger than the sender
    /// would come for it next, and fear in proportion is what keeps most volunteers at home.
    /// Only a great power sends them across the world; anyone else helps a neighbour, or a
    /// regime of its own kind on its own continent.
    /// </summary>
    private static Nation Friend(Campaign c, Nation sponsor)
    {
        var strength = c.Armies.Where(a => a.NationId == sponsor.Id).Sum(a => a.Divisions);
        if (strength < 4f) return null;
        var great = AllianceSystem.GreatPowers(c).Contains(sponsor.Id);
        var creed = PoliticsCore.IdeologyOf(sponsor)?.Id ?? "";
        Nation best = null;
        var bestScore = 0f;
        foreach (var war in c.Wars)
        {
            if (war.Ended) continue;
            foreach (var side in new[] { true, false })
                foreach (var hostId in war.Side(side).Distinct())
                {
                    var host = c.NationById(hostId);
                    if (host is null || VolunteerRefusal(c, sponsor, host).Length > 0) continue;
                    if (c.Armies.Any(a => a.LentById == sponsor.Id && a.NationId == host.Id)) continue;   // already there
                    var neighbour = CampaignDiplomacy.SharesLandBorder(c, sponsor.MapIndex, host.MapIndex);
                    var kindred = creed.Length > 0 && (PoliticsCore.IdeologyOf(host)?.Id ?? "") == creed &&
                                  string.Equals(sponsor.Continent, host.Continent, StringComparison.OrdinalIgnoreCase);
                    // What the war is worth to the sender, not only to the friend: an enemy it
                    // hates, an enemy on its own border it would rather see bled by somebody
                    // else, a host that is its ally or answers to it. A country sends its sons
                    // where the war serves it - Spain in 1936 was a rehearsal and a rival's
                    // defeat as much as a friendship.
                    var hatred = 0f;
                    var rivalNextDoor = false;
                    foreach (var enemyId in war.EnemiesOf(hostId).Distinct())
                    {
                        var enemy = c.NationById(enemyId);
                        if (enemy is null) continue;
                        var feeling = CampaignDiplomacy.Peek(c, sponsor.Id, enemyId)?.Opinion ?? 0f;
                        hatred = MathF.Max(hatred, -feeling);
                        if (feeling < -25f && CampaignDiplomacy.SharesLandBorder(c, sponsor.MapIndex, enemy.MapIndex)) rivalNextDoor = true;
                    }
                    // An ally has the alliance and its own war to join; a client has only its patron.
                    var bound = host.AnswersTo(sponsor.Id);
                    if (!great && !neighbour && !kindred && !rivalNextDoor && !bound) continue;
                    var opinion = CampaignDiplomacy.Peek(c, sponsor.Id, host.Id)?.Opinion ?? 0f;
                    if (opinion < c.Defines.AiVolunteerOpinion - MathF.Min(40f, hatred * 0.5f) - (bound ? 30f : 0f)) continue;
                    var ours = c.Armies.Where(a => a.NationId == hostId).Sum(a => a.Divisions);
                    var theirs = 0f;
                    var feared = false;
                    foreach (var enemyId in war.EnemiesOf(hostId).Distinct())
                    {
                        var enemy = c.NationById(enemyId);
                        if (enemy is null) continue;
                        var power = c.Armies.Where(a => a.NationId == enemyId).Sum(a => a.Divisions);
                        theirs += power;
                        if (power > strength * 1.5f && CampaignDiplomacy.SharesLandBorder(c, sponsor.MapIndex, enemy.MapIndex)) feared = true;
                    }
                    if (feared || theirs <= ours) continue;
                    var strategic = 1f + MathF.Min(2f, hatred / 50f) + (rivalNextDoor ? 1f : 0f) + (bound ? 1.5f : 0f);
                    var score = MathF.Max(5f, opinion + hatred * 0.5f) * strategic * (theirs / MathF.Max(1f, ours));
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = host;
                }
        }
        return best;
    }
}

/// <summary>A formation flown and shipped somewhere, lent as volunteers, or called home.</summary>
public sealed class ExpeditionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public string Action { get; init; } = Expeditions.Airlift;
    /// <summary>The province for an airlift, the host country's id for volunteers.</summary>
    public int TargetId { get; init; } = -1;
    public bool Done { get; private set; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var issuer = campaign.NationById(IssuerId);
        var army = campaign.Armies.Find(a => a.Id == ArmyId);
        Done = Action switch
        {
            Expeditions.Airlift => Expeditions.Fly(campaign, issuer, army, TargetId),
            Expeditions.Volunteer => Expeditions.Lend(campaign, issuer, army, campaign.NationById(TargetId)),
            Expeditions.Recall => army is not null && army.LentById == IssuerId && !army.InBattle && Expeditions.Return(campaign, army),
            _ => false
        };
    }
}
