using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyGovernmentSupply(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var nation = c.NationByTag("POL"); var enemy = c.NationByTag("DEU");
        var map = c.ProvinceMap; var legal = nation.CapitalProvinceId;
        c.ConfigureSinglePlayer(nation.Id); c.IsPaused = true;
        c.Armies.Clear(); c.Battles.Clear(); c.Wars.Clear(); c.FrontLines.Clear();
        c.Wars.Add(new StrategicWar { Id = 8890, AttackerId = enemy.Id, DefenderId = nation.Id });
        var pocket = Enumerable.Range(1, map.ProvinceCount).First(p =>
            p != legal && map.ProvinceToNation[p] == nation.MapIndex && !map.IsWater(p) &&
            c.ProvinceController[p] == nation.MapIndex && !map.Neighbors[p].Contains(legal) &&
            map.Neighbors[p].Length > 0 && map.Neighbors[p].All(n =>
                c.ProvinceController[n] == nation.MapIndex && !map.IsWater(n)) &&
            !SeaLanes.TryHarbourPoint(map, p, out _, out _));
        foreach (var neighbour in map.Neighbors[pocket]) c.SetProvinceController(neighbour, (ushort)enemy.MapIndex);
        c.SetProvinceController(legal, (ushort)enemy.MapIndex);
        CapitalCore.Tick(c);
        var seat = CapitalCore.EffectiveProvinceId(c, nation);
        Check(seat > 0 && seat != pocket && nation.CapitalProvinceId == legal,
            "real Polish government evacuates to a usable rear, preserving its legal city");
        if (seat <= 0 || seat == pocket) return;
        foreach (var p in new[] { pocket, seat })
        {
            var state = map.ProvinceToState[p];
            c.States.First(s => s.MapIndex == state).Buildings[BuildingKind.Barracks] = 2;
            var army = new Army { Id = p == pocket ? 8891 : 8892, NationId = nation.Id,
                ProvinceId = p, StateMapIndex = state, TemplateId = "line_infantry", Divisions = 1,
                Personnel = 10000, MaxPersonnel = 10000, Strength = 1, Equipment = .2f,
                SupplyLevel = 1, Organisation = .8f };
            (army.Latitude, army.Longitude) = map.CenterOf(p); c.Armies.Add(army);
        }
        nation.StockpilesSeeded = true;
        nation.MilitarySpending = c.Defines.BaselineMilitarySpending;
        foreach (var good in StrategicStockpiles.TrackedGoods) nation.Stockpiles[good] = 100;
        void Morning(int day)
        {
            c.RestoreRuntimeState(new GameDate(2024, 1, day + 1), day, true, c.Random.State,
                nation.Id, c.HumanSeats.ToArray(), c.ActiveSeat, c.NextBattleId, c.MapRevision,
                c.InitialDeploymentCompleted, c.ProvinceController, c.ProvinceOccupation, null, null);
            LogisticsSystem.Tick(c);
        }
        for (var day = 3; day <= 16; day++) Morning(day);
        var isolated = c.Armies[0]; var rear = c.Armies[1];
        Check(isolated.CutOff && isolated.DaysCutOff == 14 && isolated.SupplyLevel == 0 && !rear.CutOff,
            "actual province graph keeps only the evacuated rear supplied after fourteen mornings");
        var kit = isolated.Equipment;
        EquipmentLogistics.ReplenishDay(c);
        Check(isolated.Equipment == kit && rear.Equipment > kit,
            "real national equipment delivery cannot reach the other barracks pocket");

        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            var label = language + size.X;
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
            game.Call("Refresh");
            game.VerificationOccupationView(true, CapitalCore.EffectiveLatitude(c, nation),
                CapitalCore.EffectiveLongitude(c, nation), 5f);
            var hash = CampaignFingerprint.Of(c); var random = c.Random.State;
            game.VerificationInspectPick(new GlobePick(nation, null, -1, 0, 0)); await Wait(.5);
            var card = game.FindChild("CountryCard", true, false) as Control;
            var labels = card?.FindChildren("*", "Label", true, false).OfType<Label>().Select(l => l.Text).ToArray();
            Check(card is not null && card.IsVisibleInTree() && labels.Contains(Loc.Get("Temporary government seat")) &&
                labels.Contains(CountryNames.Capital(nation, c)), "actual supply government's seat appears on the country card: " + label);
            Check(game.VerificationSeatMarkerMatches(nation), "government map marker matches the supply rear: " + label);
            Check(CampaignFingerprint.Of(c) == hash && c.Random.State == random,
                "government supply card and camera are read-only: " + label);
            await Capture("supply-government-" + language + "-" + size.X);
        }
        foreach (var neighbour in map.Neighbors[pocket]) c.SetProvinceController(neighbour, (ushort)nation.MapIndex);
        c.SetProvinceController(legal, (ushort)nation.MapIndex); CapitalCore.Tick(c); Morning(18);
        Check(nation.GovernmentSeatProvinceId == 0 && c.Armies.All(a => !a.CutOff && a.DaysCutOff == 0),
            "liberated legal capital and corridor restore both real formations' supply");
    }
}
