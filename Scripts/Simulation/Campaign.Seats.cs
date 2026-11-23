using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    /// <summary>
    /// Look at the world through one country without sitting in its chair: nobody becomes human,
    /// no seat is taken and the computer keeps running it. What an observer's interface reads.
    /// </summary>
    public void ViewAs(int nationId)
    {
        var nation = NationById(nationId);
        if (nation is not null) Player = nation;
    }

    public void SetPlayer(int nationId)
    {
        foreach (var nation in Nations) nation.IsPlayer = false;
        Player = NationById(nationId);
        if (Player is null) return;
        Player.IsPlayer = true;
        if (HumanSeats.Count == 0)
        {
            foreach (var nation in Nations) nation.IsHuman = false;
            Player.IsHuman = true;
            HumanSeats.Add(Player.Id);
            ActiveSeat = 0;
        }
        Changed?.Invoke();
    }

    public bool AddHumanSeat(int nationId)
    {
        var nation = NationById(nationId);
        if (nation is null || HumanSeats.Contains(nationId)) return false;
        HumanSeats.Add(nationId);
        nation.IsHuman = true;
        foreach (var army in Armies)
        {
            if (army.NationId != nationId || army.InBattle) continue;
            army.OrderKind = ArmyOrder.Manual;
            army.AssignedWarId = -1;
            army.OrderProvinceId = -1;
            army.OrderStateMapIndex = -1;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.MoveProgress = 0f;
        }
        Changed?.Invoke();
        return true;
    }

    public bool RemoveHumanSeat(int nationId)
    {
        if (HumanSeats.Count <= 1 || !HumanSeats.Remove(nationId)) return false;
        var nation = NationById(nationId);
        if (nation is not null) nation.IsHuman = false;
        if (ActiveSeat >= HumanSeats.Count) ActiveSeat = 0;
        TakeSeat(ActiveSeat);
        if (ProvinceMap is not null) FrontPlanner.AssignAi(this);
        return true;
    }

    public void ConfigureSinglePlayer(int nationId)
    {
        foreach (var nation in Nations) { nation.IsHuman = false; nation.IsPlayer = false; }
        HumanSeats.Clear();
        ActiveSeat = 0;
        AddHumanSeat(nationId);
        TakeSeat(0);
    }

    public bool TakeSeat(int index)
    {
        if (HumanSeats.Count == 0) return false;
        ActiveSeat = M.Clamp(index, 0, HumanSeats.Count - 1);
        var nation = NationById(HumanSeats[ActiveSeat]);
        if (nation is null) return false;
        foreach (var other in Nations) other.IsPlayer = false;
        nation.IsPlayer = true;
        nation.IsHuman = true;
        Player = nation;
        IsPaused = true;
        // Sitting down in front of a country is the moment its books have to make sense.
        // Every path arrives here - a new campaign, a hot seat handed over, a save just
        // restored - and the weekly tick that writes these figures does not run on day zero
        // and does not survive a save, which is the whole of why the economy screen was
        // sometimes blank.
        EconomySystem.Explain(this);
        Changed?.Invoke();
        return true;
    }

    public bool NextSeat() => HumanSeats.Count > 1 && TakeSeat((ActiveSeat + 1) % HumanSeats.Count);

    public void SetMilitarySpending(int nationId, float value)
    {
        var nation = NationById(nationId);
        if (nation is null) return;
        nation.MilitarySpending = M.Clamp01(value);
        Changed?.Invoke();
    }

    public void SetTaxRate(int nationId, float value)
    {
        var nation = NationById(nationId);
        if (nation is null) return;
        nation.TaxRate = M.Clamp(value, 0.05f, 0.55f);
        Changed?.Invoke();
    }

    public void SetTradePolicy(int nationId, TradePolicy policy)
    {
        var nation = NationById(nationId);
        if (nation is null || !Enum.IsDefined(policy)) return;
        nation.TradePolicy = policy;
        Changed?.Invoke();
    }

    internal void RestoreRuntimeState(GameDate date, int elapsedDays, bool paused, uint randomState,
        int playerId, IReadOnlyList<int> humanSeats, int activeSeat, int nextBattleId,
        int mapRevision, bool initialDeploymentCompleted, ushort[] controllers, float[] occupation, ushort[] owners, ushort[] states,
        bool noHumanAuthority = false)
    {
        Date = date;
        ElapsedHours = Math.Max(0, elapsedDays) * 24L + Math.Clamp(date.Hour, 0, 23);
        IsPaused = paused;
        Random.Restore(randomState);
        NextBattleId = Math.Max(0, nextBattleId);
        MapRevision = Math.Max(0, mapRevision);
        InitialDeploymentCompleted = initialDeploymentCompleted;
        HumanSeats.Clear();
        if (humanSeats is not null)
            foreach (var id in humanSeats.Where(id => id >= 0 && id < Nations.Count).Distinct())
                HumanSeats.Add(id);
        if (noHumanAuthority) HumanSeats.Clear();
        else if (HumanSeats.Count == 0 && Nations.Count > 0) HumanSeats.Add(Math.Clamp(playerId, 0, Nations.Count - 1));
        ActiveSeat = Math.Clamp(activeSeat, 0, Math.Max(0, HumanSeats.Count - 1));
        foreach (var nation in Nations)
        {
            nation.IsHuman = HumanSeats.Contains(nation.Id);
            nation.IsPlayer = nation.Id == playerId;
        }
        Player = NationById(playerId) ??
            (HumanSeats.Count > 0 ? NationById(HumanSeats[ActiveSeat]) : null) ?? Nations.FirstOrDefault();
        if (Player is not null) Player.IsPlayer = true;
        _pendingProvinceController = controllers;
        _pendingProvinceOccupation = occupation;
        _pendingProvinceOwner = owners;
        _pendingProvinceState = states;
        PaceRoadsDay = -1;
        BorderDay = -1;
        BorderRevision = -1;
        BorderForgetStamp = -1;
        LastReunifyMapRevision = -1;
        LastReunifyStateCount = -1;
        ReunifyBlockedPairs.Clear();
        ControlTallyTables = null;
        AiStatecraftTables = null;
    }

    public bool SetProvinceController(int provinceId, ushort controller)
    {
        if (ProvinceController is null || provinceId <= 0 || provinceId >= ProvinceController.Length ||
            ProvinceController[provinceId] == controller)
            return false;
        ProvinceController[provinceId] = controller;
        ProvinceOccupation[provinceId] = 0f;
        MapRevision++;
        return true;
    }

    /// <summary>
    /// Something happened that the interface should redraw for.
    ///
    /// Sets a dirty flag flushed once per frame instead of rebuilding the UI mid-step.
    /// </summary>
    internal int StrategicRevision;
    public void MarkStrategicChange() { StrategicRevision++; InterfaceDirty = true; }

    internal int AllianceRevision;
    internal void NoteAllianceChange() => AllianceRevision++;

    /// <summary>Something moved a border. This is what invalidates map-derived caches.</summary>
    public void MarkMapChanged()
    {
        MapRevision++;
        // The map is authoritative, including annexation by deals and peace. Waiting
        // for a movement's staggered weekly update can make a landed country look exiled.
        if (ProvinceMap is not null)
            foreach (var n in Nations)
                if (n.Movement is not null) MovementRules.SynchroniseTerritory(this, n);
        // And the same for the fallen list: a country back on the map is not fallen, by
        // whichever road its ground came back (MovementFall.ReturnTheLanded).
        MovementFall.ReturnTheLanded(this);
        InterfaceDirty = true;
    }

    /// <summary>Whether anything has happened that the interface has not drawn yet.</summary>
    public bool InterfaceDirty { get; private set; }

    /// <summary>
    /// A change the player's interface should show. Outside a step it redraws now; inside one
    /// it waits for the frame's flush so background AI changes never trigger mid-step HUD rebuilds.
    /// </summary>
    internal void NotifyInterface()
    {
        if (_runningStages || DeferMainThreadNotifications) InterfaceDirty = true;
        else Changed?.Invoke();
    }

    /// <summary>Redraw once for everything that has happened since the last frame.</summary>
    public void FlushInterfaceChanges()
    {
        if (!InterfaceDirty) return;
        InterfaceDirty = false;
        Changed?.Invoke();
    }

    internal Nation AddNation(Nation nation, bool player = false)
    {
        if (nation is null) throw new ArgumentNullException(nameof(nation));

        nation.Id = Nations.Count;
        if (nation.MilitarySpending <= 0f)
            nation.MilitarySpending = Defines.BaselineMilitarySpending;

        nation.IsPlayer = player;
        nation.IsHuman = player;
        Nations.Add(nation);
        TacticalDoctrines.BindHome(this, nation);
        // Initial civil-war factions and later emergent states are added after the
        // bookmark's financial migration. They need the same books immediately.
        SovereignFinance.NormalizeAccounts(this, nation);
        if (player)
        {
            Player = nation;
            HumanSeats.Add(nation.Id);
        }
        return nation;
    }
}
