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
    private bool _militaryAccessByA;
    private bool _militaryAccessByB;
    public bool MilitaryAccessByA
    {
        get => _militaryAccessByA;
        set
        {
            if (value == _militaryAccessByA) return;
            _militaryAccessByA = value;
            System.Threading.Interlocked.Increment(ref _routeAccessMutationVersion);
        }
    }
    public bool MilitaryAccessByB
    {
        get => _militaryAccessByB;
        set
        {
            if (value == _militaryAccessByB) return;
            _militaryAccessByB = value;
            System.Threading.Interlocked.Increment(ref _routeAccessMutationVersion);
        }
    }
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

    [ThreadStatic] private static HashSet<int> _controllerHostileIds;
    [ThreadStatic] private static HashSet<int> _controllerSameSideIds;

    /// <summary>Only relations that change access, borrowed by a single support movement pass.</summary>
    internal sealed class RouteRelations
    {
        private List<(int Partner, DiplomaticRelation Relation)>[] _neighbors = Array.Empty<List<(int, DiplomaticRelation)>>();
        private DiplomaticRelation[] _countryRow = Array.Empty<DiplomaticRelation>();
        private int _awakeCount = -1, _dormantCount, _nationsCount, _accessVersion;

        internal void Rebuild(Campaign campaign)
        {
            Clear();
            _nationsCount = campaign.Nations.Count;
            if (_neighbors.Length < _nationsCount)
            {
                Array.Resize(ref _neighbors, _nationsCount);
                Array.Resize(ref _countryRow, _nationsCount);
            }
            _accessVersion = DiplomaticRelation.RouteAccessMutationVersion;
            foreach (var (key, relation) in campaign.Relations)
                if (AffectsAccess(relation)) Add(key, relation);
            foreach (var (key, relation) in campaign.DormantRelations)
                if (AffectsAccess(relation) && !campaign.Relations.ContainsKey(key))
                    Add(key, relation);
            _awakeCount = campaign.Relations.Count;
            _dormantCount = campaign.DormantRelations.Count;
        }

        private void Add(long key, DiplomaticRelation relation)
        {
            var a = (int)(key >> 32);
            var b = unchecked((int)(uint)key);
            if ((uint)a >= (uint)_nationsCount || (uint)b >= (uint)_nationsCount || a == b) return;
            (_neighbors[a] ??= new()).Add((b, relation));
            (_neighbors[b] ??= new()).Add((a, relation));
        }

        internal DiplomaticRelation[] ForCountry(Campaign campaign, int country)
        {
            if (campaign.Relations.Count != _awakeCount || campaign.DormantRelations.Count != _dormantCount ||
                campaign.Nations.Count != _nationsCount || DiplomaticRelation.RouteAccessMutationVersion != _accessVersion)
                return null;
            Array.Clear(_countryRow, 0, _nationsCount);
            if (_neighbors[country] is { } neighbors)
                foreach (var (partner, relation) in neighbors) _countryRow[partner] = relation;
            return _countryRow;
        }

        internal void Clear()
        {
            for (var i = 0; i < _nationsCount; i++) _neighbors[i]?.Clear();
            Array.Clear(_countryRow, 0, _nationsCount);
            _nationsCount = 0;
            _awakeCount = -1;
        }

        private static bool AffectsAccess(DiplomaticRelation relation) =>
            relation is not null &&
            (relation.Stance == DiplomaticStance.AtWar || relation.MilitaryAccessByA || relation.MilitaryAccessByB);
    }

    public static void FillControllers(Campaign c, int guest, ISet<ushort> allowed)
        => FillControllers(c, guest, allowed, enemies: null);

    /// <summary>Optionally collect the enemy controllers needed by support movement in the same walk.</summary>
    internal static void FillControllers(Campaign c, int guest, ISet<ushort> allowed, HashSet<ushort> enemies,
        RouteRelations routeRelations = null)
    {
        if (allowed is null) return;
        var own = c?.NationById(guest);
        if (own is null) return;

        var hostile = _controllerHostileIds ??= new HashSet<int>();
        var sameSide = _controllerSameSideIds ??= new HashSet<int>();
        hostile.Clear();
        sameSide.Clear();

        var wars = Warfare.LiveWars(c);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            if (war.Ended) continue;
            var attacking = war.IsAttacking(guest);
            if (!attacking && !war.IsDefending(guest)) continue;
            foreach (var id in war.Side(attacking)) sameSide.Add(id);
            foreach (var id in war.Side(!attacking))
            {
                hostile.Add(id);
                if (enemies is not null && c.NationById(id) is { MapIndex: > 0 } foe)
                    enemies.Add((ushort)foe.MapIndex);
            }
        }

        var byMapIndex = c.NationsByMapIndex();
        var routeRow = routeRelations?.ForCountry(c, guest);
        var routeHosts = c.Nations.Count;
        DiplomaticRelation RelationWith(int hostId) => routeRow is not null && (uint)hostId < (uint)routeHosts
            ? routeRow[hostId] : CampaignDiplomacy.Peek(c, guest, hostId);
        // Refresh retirement before reading RetiredDay. Relations alone count as enemies only
        // for active countries, while the live belligerents above count regardless of retirement.
        var active = enemies is null ? null : c.ActiveNations;
        foreach (var pair in byMapIndex)
        {
            var mapIndex = pair.Key;
            if (mapIndex <= 0) continue;
            var code = (ushort)mapIndex;
            var host = pair.Value;
            var atHome = mapIndex == own.MapIndex;
            if (atHome) allowed.Add(code);
            if (atHome && enemies is null) continue;
            if (host is null || (host.Id != guest && hostile.Contains(host.Id))) continue;
            var rel = RelationWith(host.Id);
            if (rel?.Stance == DiplomaticStance.AtWar)
            {
                if (enemies is not null && host.RetiredDay < 0) enemies.Add(code);
                continue;
            }
            if (!atHome && ((rel is not null && rel.GrantsMilitaryAccess(host.Id)) ||
                own.AnswersTo(host.Id) || host.AnswersTo(guest) ||
                sameSide.Contains(host.Id)))
            {
                allowed.Add(code);
            }
        }

        // Old scenarios can share a map code. The map index chooses the first owner; another
        // active country using that code can still mark it hostile in support movement.
        if (enemies is null || byMapIndex.Count == c.Nations.Count) return;
        for (var i = 0; i < active.Count; i++)
        {
            var other = active[i];
            if (other.Id != guest && other.MapIndex > 0 &&
                !ReferenceEquals(byMapIndex.GetValueOrDefault(other.MapIndex), other) &&
                RelationWith(other.Id)?.Stance == DiplomaticStance.AtWar)
                enemies.Add((ushort)other.MapIndex);
        }
    }

    public static HashSet<ushort> Controllers(Campaign c, int guest)
    {
        var allowed = new HashSet<ushort>();
        FillControllers(c, guest, allowed);
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
        var map = c.ProvinceMap;
        var controls = c.ProvinceController;
        var needing = _repatriationArmies ??= new List<Army>();
        var homes = _repatriationHomes ??= new Dictionary<ushort, List<int>>();
        var pool = _repatriationListPool ??= new Stack<List<int>>();
        needing.Clear();
        var marched = 0;
        foreach (var army in c.Armies)
        {
            if (++marched % 256 == 0) yield return 0;
            if (!MustReturn(c, army)) continue;
            if (army.InBattle && HasActiveBattle(c, army.BattleId)) continue;
            var owner = c.NationById(army.NationId);
            if (owner is not null && owner.MapIndex > 0 && !homes.ContainsKey((ushort)owner.MapIndex))
                homes[(ushort)owner.MapIndex] = pool.Count > 0 ? pool.Pop() : new List<int>();
            needing.Add(army);
        }
        if (homes.Count > 0)
        {
            var provToNation = map.ProvinceToNation;
            var count = Math.Min(map.ProvinceCount, controls.Length - 1);
            for (var p = 1; p <= count; p++)
            {
                var owner = provToNation[p];
                if (owner == 0 || controls[p] != owner || map.IsWater(p)) continue;
                if (homes.TryGetValue(owner, out var list)) list.Add(p);
            }
        }
        for (var ai = 0; ai < needing.Count; ai++)
        {
            var army = needing[ai];
            var nation = c.NationById(army.NationId);
            var destination = 0;
            var best = float.MaxValue;
            var origin = map.CenterOf(army.ProvinceId);
            if (nation is not null && homes.TryGetValue((ushort)nation.MapIndex, out var places))
                foreach (var p in places)
                {
                    var point = map.CenterOf(p);
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
            army.StateMapIndex = map.ProvinceToState[destination];
            (army.Latitude, army.Longitude) = map.CenterOf(destination);
            army.Entrenchment = army.Planning = 0f;
            army.ReadyDay = Math.Max(army.ReadyDay, c.ElapsedDays + Math.Max(1, c.Defines.MilitaryRepatriationRestDays));
            DiagnosticLogs.Write("orders", $"REPATRIATE army={army.Id} nation={nation.Tag} province={destination} rest_until={army.ReadyDay}");
            c.MarkStrategicChange();
        }
        needing.Clear();
        if (homes.Count > 0)
        {
            foreach (var list in homes.Values) { list.Clear(); pool.Push(list); }
            homes.Clear();
        }
    }

    [ThreadStatic] private static List<Army> _repatriationArmies;
    [ThreadStatic] private static Dictionary<ushort, List<int>> _repatriationHomes;
    [ThreadStatic] private static Stack<List<int>> _repatriationListPool;

    internal static void Provision(Army army)
    {
        army.CutOff = false; army.LivingOffLand = false; army.DaysCutOff = 0; army.SupplyDistance = 0; army.SupplySourceStateMapIndex = -1;
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

    private static bool HasActiveBattle(Campaign c, int battleId)
    {
        var battles = c.Battles;
        for (var i = 0; i < battles.Count; i++)
        {
            var b = battles[i];
            if (b.Id == battleId && !b.Ended) return true;
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
