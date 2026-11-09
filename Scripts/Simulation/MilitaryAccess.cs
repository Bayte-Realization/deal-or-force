using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float MilitaryAccessWillingness { get; set; } = .25f;
    public int MilitaryAccessReplyDays { get; set; } = 30;
    public int MilitaryAccessRetryDays { get; set; } = 90;
    public int MilitaryRepatriationRestDays { get; set; } = 3;
}

public sealed partial class DiplomaticRelation
{
    // A grant by A lets B enter A, not the reverse. Old saves default to no grant.
    public bool MilitaryAccessByA;
    public bool MilitaryAccessByB;
    public int MilitaryAccessRequestedBy = -1;
    public int MilitaryAccessRequestedDay = -9999;
    public bool GrantsMilitaryAccess(int hostId) => hostId == A ? MilitaryAccessByA : hostId == B && MilitaryAccessByB;
}

/// <summary>Directional permission to march: no transfer of control, no declaration of war.</summary>
public static class MilitaryAccess
{
    public static bool Hostile(Campaign c, int first, int second)
    {
        if (first == second) return false;
        if (CampaignDiplomacy.Peek(c, first, second)?.Stance == DiplomaticStance.AtWar) return true;
        var wars = Warfare.LiveWars(c);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (!war.Ended && war.Opposes(first, second)) return true;
        }
        return false;
    }

    public static bool Granted(Campaign c, int guest, int host) =>
        c is not null && CampaignDiplomacy.Peek(c, guest, host)?.GrantsMilitaryAccess(host) == true;

    public static bool CanEnter(Campaign c, int guest, ushort controller)
    {
        var own = c?.NationById(guest);
        if (own is null || controller == 0) return false;
        if (controller == own.MapIndex) return true;
        if (!c.NationsByMapIndex().TryGetValue(controller, out var host) || Hostile(c, guest, host.Id)) return false;
        if (Granted(c, guest, host.Id)) return true;
        if (own.AnswersTo(host.Id) || host.AnswersTo(own.Id)) return true;
        var wars = Warfare.LiveWars(c);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (!war.Ended && WarCoalition.SameSide(war, guest, host.Id)) return true;
        }
        return false;
    }

    public static bool MustReturn(Campaign c, Army army)
    {
        var map = c?.ProvinceMap;
        var controls = c?.ProvinceController;
        // The opt-in console curse deliberately overrides every border and ordinary order.
        if (c?.Curse == WorldCurses.WeirdRoute || army is null || army.EmbarkedFleetId >= 0 || map is null || controls is null ||
            army.ProvinceId <= 0 || army.ProvinceId > map.ProvinceCount || army.ProvinceId >= controls.Length) return false;
        var who = controls[army.ProvinceId];
        var own = c.NationById(army.NationId);
        return own is not null && who != 0 && who != own.MapIndex &&
            c.NationsByMapIndex().TryGetValue(who, out var host) &&
            !Hostile(c, own.Id, host.Id) && !CanEnter(c, own.Id, who);
    }

    public static HashSet<ushort> Controllers(Campaign c, int guest)
    {
        var allowed = new HashSet<ushort>();
        foreach (var n in c.Nations)
            if (n.MapIndex > 0 && CanEnter(c, guest, (ushort)n.MapIndex)) allowed.Add((ushort)n.MapIndex);
        return allowed;
    }

    public static int[] Route(Campaign c, int guest, int from, int to, ushort[] controls) =>
        c.ProvinceMap.FindOperationalRoute(from, to, Controllers(c, guest), controls);

    public static string Refusal(Campaign c, Nation guest, Nation host)
    {
        if (c is null || guest is null || host is null || guest == host || guest.Movement is not null ||
            host.Movement is not null || guest.Population <= 0 || host.Population <= 0) return "Invalid target.";
        if (Hostile(c, guest.Id, host.Id)) return "Already at war.";
        if (CanEnter(c, guest.Id, (ushort)host.MapIndex)) return "Military access is already available.";
        if (Pending(c, CampaignDiplomacy.Peek(c, guest.Id, host.Id))) return "A military access request awaits a reply.";
        if (guest.DecisionAvailableDay.GetValueOrDefault("military_access/" + host.Id) > c.ElapsedDays)
            return "Wait before requesting military access again.";
        return "";
    }

    public static float Willingness(Campaign c, Nation guest, Nation host)
    {
        var r = CampaignDiplomacy.Peek(c, guest.Id, host.Id);
        var value = (r?.Opinion ?? 0f) / 100f - MathF.Min(.35f, MathF.Max(0f, guest.Notoriety) * .005f);
        if (r?.Stance == DiplomaticStance.Allied) value += .5f;
        if (r?.Stance == DiplomaticStance.Rival) value -= .6f;
        var wars = Warfare.LiveWars(c);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (!war.Ended && war.Involves(guest.Id))
                foreach (var enemy in war.EnemiesOf(guest.Id))
                    if (CampaignDiplomacy.Peek(c, host.Id, enemy)?.Stance == DiplomaticStance.Rival)
                        return value + .2f;
        }
        return value;
    }

    public static bool Pending(Campaign c, DiplomaticRelation r) => r is not null && r.MilitaryAccessRequestedBy >= 0 &&
        c.ElapsedDays - r.MilitaryAccessRequestedDay < Math.Max(1, c.Defines.MilitaryAccessReplyDays);

    public static int Incoming(Campaign c, int host)
    {
        var count = 0;
        foreach (var r in c.Relations.Values)
            if ((r.A == host || r.B == host) && Pending(c, r) && r.MilitaryAccessRequestedBy != host) count++;
        return count;
    }

    internal static void Set(Campaign c, DiplomaticRelation r, int host, bool grant)
    {
        if (host == r.A) r.MilitaryAccessByA = grant;
        else if (host == r.B) r.MilitaryAccessByB = grant;
        if (r.MilitaryAccessRequestedBy == r.Other(host)) r.MilitaryAccessRequestedBy = -1;
        WarCoalition.InvalidateRoutes(c);
        Warfare.InvalidateAccessRoutes(c);
        c.DealInboxRevision++;
        c.MarkStrategicChange();
    }

    /// <summary>
    /// A neutral government repatriates trespassing columns, not besieges them. Nearest
    /// owned AND controlled dry land, id tie-break. No combat escape, captures, healing,
    /// manpower refunds or RNG. With no homeland left, intern safely until return is possible.
    /// </summary>
    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same daily access pass, yielded between batches of relationships and armies.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (!c.StepOpensNewDay) yield break;
        var scanned = 0;
        foreach (var r in c.Relations.Values)
        {
            if (++scanned % 512 == 0) yield return 0;
            if (r.MilitaryAccessRequestedBy >= 0 && !Pending(c, r))
            { r.MilitaryAccessRequestedBy = -1; c.DealInboxRevision++; }
            if ((r.MilitaryAccessByA || r.MilitaryAccessByB) && Hostile(c, r.A, r.B))
            { r.MilitaryAccessByA = r.MilitaryAccessByB = false; WarCoalition.InvalidateRoutes(c); Warfare.InvalidateAccessRoutes(c); c.MarkStrategicChange(); }
        }
        if (c.ProvinceMap is null || c.ProvinceController is null) yield break;
        Dictionary<ushort, List<int>> homes = null;
        var marched = 0;
        foreach (var army in c.Armies)
        {
            if (++marched % 256 == 0) yield return 0;
            if (!MustReturn(c, army)) continue;
            if (army.InBattle && c.Battles.Exists(b => b.Id == army.BattleId && !b.Ended)) continue;
            if (homes is null)
            {
                homes = new();
                for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
                {
                    var owner = c.ProvinceMap.ProvinceToNation[p];
                    if (owner == 0 || c.ProvinceController[p] != owner || c.ProvinceMap.IsWater(p)) continue;
                    if (!homes.TryGetValue(owner, out var list)) homes[owner] = list = new();
                    list.Add(p);
                }
            }
            var nation = c.NationById(army.NationId);
            var destination = 0;
            var best = float.MaxValue;
            var origin = c.ProvinceMap.CenterOf(army.ProvinceId);
            if (homes.TryGetValue((ushort)nation.MapIndex, out var places))
                foreach (var p in places)
                {
                    var point = c.ProvinceMap.CenterOf(p);
                    var distance = NavalOperations.Distance(origin.Latitude, origin.Longitude, point.Latitude, point.Longitude);
                    if (distance >= best) continue;
                    best = distance; destination = p;
                }
            Provision(army);
            army.Route.Clear(); army.PlannedRoute.Clear(); army.MoveProgress = 0f;
            army.OrderProvinceId = army.OrderStateMapIndex = army.ObjectiveStateMapIndex = -1;
            army.AssignedWarId = army.FrontLineId = -1; army.RedeployTarget = -1;
            army.DirectOrder = army.SupportOnly = army.PlanActive = false;
            army.OrderKind = ArmyOrder.Manual; army.OrderRouteSteps = army.OrderStepsCompleted = 0;
            foreach (var line in c.FrontLines) line.Posts.Remove(army.Id);
            if (destination <= 0) continue;
            army.BattleId = -1; army.ProvinceId = destination;
            army.StateMapIndex = c.ProvinceMap.ProvinceToState[destination];
            (army.Latitude, army.Longitude) = c.ProvinceMap.CenterOf(destination);
            army.Entrenchment = army.Planning = 0f;
            army.ReadyDay = Math.Max(army.ReadyDay, c.ElapsedDays + Math.Max(1, c.Defines.MilitaryRepatriationRestDays));
            DiagnosticLogs.Write("orders", $"REPATRIATE army={army.Id} nation={nation.Tag} province={destination} rest_until={army.ReadyDay}");
            c.MarkStrategicChange();
        }
    }

    internal static void Provision(Army army)
    {
        army.CutOff = false; army.DaysCutOff = 0; army.SupplyDistance = 0; army.SupplySourceStateMapIndex = -1;
        army.SupplyCongestion = 1f; army.SupplyLevel = MathF.Max(army.SupplyLevel, .65f);
    }

    internal static bool TryWarTransit(Campaign c, Nation guest, Nation host)
    {
        // Most friendly candidates are distant partners, or have no current war to
        // provide transit for. Do not inspect permissions for every one of them.
        // These geographic/war queries are read-only; the real request below still
        // uses exactly the same refusal and command as a player's request.
        if (c is null || guest is null || host is null ||
            !CampaignDiplomacy.SharesLandBorder(c, guest.MapIndex, host.MapIndex)) return false;
        var wars = Warfare.LiveWars(c);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (war.Ended || !war.Involves(guest.Id) || war.Involves(host.Id)) continue;
            foreach (var enemyId in war.EnemiesOf(guest.Id))
                if (c.NationById(enemyId) is { } enemy &&
                    CampaignDiplomacy.SharesLandBorder(c, host.MapIndex, enemy.MapIndex))
                {
                    if (Refusal(c, guest, host).Length > 0) return false;
                    new MilitaryAccessCommand { IssuerId = guest.Id, TargetId = host.Id,
                        Action = MilitaryAccessAction.Request }.Apply(c);
                    return true;
                }
        }
        return false;
    }
}

public enum MilitaryAccessAction { Request, Grant, Revoke, Renounce, Decline }

public sealed class MilitaryAccessCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; }
    public MilitaryAccessAction Action { get; init; }
    public string Result { get; private set; } = "";

    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var issuer = c.NationById(IssuerId); var target = c.NationById(TargetId);
        if (issuer is null || target is null || issuer == target) { Result = "Invalid target."; return; }
        var r = CampaignDiplomacy.Peek(c, IssuerId, TargetId);
        if (Action == MilitaryAccessAction.Request)
        {
            Result = MilitaryAccess.Refusal(c, issuer, target);
            if (Result.Length > 0) return;
            r = CampaignDiplomacy.Get(c, IssuerId, TargetId);
            issuer.DecisionAvailableDay["military_access/" + target.Id] = c.ElapsedDays + Math.Max(1, c.Defines.MilitaryAccessRetryDays);
            if (target.IsHuman)
            {
                r.MilitaryAccessRequestedBy = IssuerId; r.MilitaryAccessRequestedDay = c.ElapsedDays;
                c.DealInboxRevision++; c.MarkStrategicChange(); Result = "Military access request sent."; return;
            }
            if (MilitaryAccess.Willingness(c, issuer, target) < c.Defines.MilitaryAccessWillingness)
            { Result = "Military access refused."; return; }
            MilitaryAccess.Set(c, r, TargetId, true); Result = "Military access granted."; return;
        }
        if (Action == MilitaryAccessAction.Grant)
        {
            if (MilitaryAccess.Hostile(c, IssuerId, TargetId) || issuer.Movement is not null || target.Movement is not null ||
                issuer.Population <= 0 || target.Population <= 0) { Result = "Invalid target."; return; }
            r ??= CampaignDiplomacy.Get(c, IssuerId, TargetId);
            MilitaryAccess.Set(c, r, IssuerId, true); Result = "Military access granted.";
        }
        else if (Action == MilitaryAccessAction.Revoke || Action == MilitaryAccessAction.Renounce)
        {
            var host = Action == MilitaryAccessAction.Revoke ? IssuerId : TargetId;
            if (r?.GrantsMilitaryAccess(host) != true) { Result = "Nothing to cancel."; return; }
            MilitaryAccess.Set(c, r, host, false); Result = "Military access ended.";
        }
        else if (Action == MilitaryAccessAction.Decline && r?.MilitaryAccessRequestedBy == TargetId)
        {
            r.MilitaryAccessRequestedBy = -1; c.DealInboxRevision++; c.MarkStrategicChange();
            Result = "Military access refused.";
        }
    }
}
