using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyIsolatedDepot(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var poland = c.NationByTag("POL");
        var enemy = c.NationByTag("DEU");
        c.ConfigureSinglePlayer(poland.Id);
        c.IsPaused = true;
        c.Armies.Clear(); c.Battles.Clear(); c.Wars.Clear(); c.FrontLines.Clear();
        var map = c.ProvinceMap;
        var province = Enumerable.Range(1, map.ProvinceCount).First(p =>
            p != poland.CapitalProvinceId && map.ProvinceToNation[p] == poland.MapIndex &&
            c.ProvinceController[p] == poland.MapIndex && !map.IsWater(p) &&
            map.Neighbors[p].Length > 0 && map.Neighbors[p].All(n =>
                c.ProvinceController[n] == poland.MapIndex && !map.IsWater(n)) &&
            !SeaLanes.TryHarbourPoint(map, p, out _, out _));
        var army = new Army { Id = 8802, NationId = poland.Id, ProvinceId = province,
            StateMapIndex = map.ProvinceToState[province], TemplateId = "line_infantry",
            Divisions = 1, Personnel = 12000, MaxPersonnel = 15000, Strength = .8f,
            Name = "1. POL Division", Equipment = .2f, Organisation = .8f, SupplyLevel = 1f };
        (army.Latitude, army.Longitude) = map.CenterOf(province);
        c.Armies.Add(army);
        c.States.First(s => s.MapIndex == army.StateMapIndex).Buildings[BuildingKind.Barracks] = 1;
        poland.StockpilesSeeded = true;
        poland.MilitarySpending = c.Defines.BaselineMilitarySpending;
        game.VerificationFocusAdministration(poland);

        void Morning(int day)
        {
            // Controlled logistics mornings, not a fabricated multi-year campaign observation.
            c.RestoreRuntimeState(new GameDate(2024, 1, day + 1), day, true, c.Random.State,
                poland.Id, c.HumanSeats.ToArray(), c.ActiveSeat, c.NextBattleId, c.MapRevision,
                c.InitialDeploymentCompleted, c.ProvinceController, c.ProvinceOccupation, null, null);
            LogisticsSystem.Tick(c);
        }

        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            var label = language + size.X;
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
            foreach (var neighbor in map.Neighbors[province]) c.ProvinceController[neighbor] = (ushort)poland.MapIndex;
            army.CutOff = false; army.DaysCutOff = 0; army.SupplyLevel = 1f;
            army.Organisation = .8f; army.Equipment = .2f;
            foreach (var good in StrategicStockpiles.TrackedGoods) poland.Stockpiles[good] = 100;
            Morning(0);
            Check(!army.CutOff, "real Polish position initially has a supply route: " + label);
            foreach (var neighbor in map.Neighbors[province]) c.ProvinceController[neighbor] = (ushort)enemy.MapIndex;
            for (var day = 3; day <= 9; day++) Morning(day);
            Check(army.CutOff && army.DaysCutOff == 7 && army.SupplyLevel > 0f,
                "actual graph isolates the barracks without deleting carried reserves: " + label);
            var kit = army.Equipment; var stock = poland.Stockpiles["Arms"];
            EquipmentLogistics.ReplenishDay(c);
            Check(army.Equipment == kit && poland.Stockpiles["Arms"] == stock,
                "real isolated position cannot draw equipment from remote stores: " + label);
            game.Call("Refresh");
            game.VerificationSelectBattleOrders(army);
            await Wait(.3);
            var mark = game.FindChild("SupplyStatus_8802", true, false) as Control;
            Check(mark is not null && mark.IsVisibleInTree(), "supply warning is visible on the real army row: " + label);
            if (mark is null) continue;
            var readable = string.Join(" ", mark.TooltipText.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            Check(readable.Contains(Loc.Format("Days in the cauldron: {0}.", 7)),
                "localized warning names the actual seven-day pocket: " + label);
            var before = CampaignFingerprint.Of(c);
            GetViewport().NotifyMouseExited(); GetViewport().NotifyMouseEntered();
            // Keep the private viewport pointer on the small warning through the dwell;
            // real desktop mouse motion must not silently replace the requested subject.
            for (var pulse = 0; pulse < 10; pulse++)
            {
                GetViewport().PushInput(new InputEventMouseMotion {
                    Position = mark.GetGlobalTransformWithCanvas() * (mark.Size * .5f) }, true);
                await Wait(.12);
            }
            // Stop sending motion during the native tooltip delay; repeated motion can
            // keep the timer restarting even while the correct control remains hovered.
            await Wait(1.1);
            var hovered = GetViewport().GuiGetHoveredControl();
            Check(hovered == mark, "actual supply-warning hover reaches the marker: " + label +
                " hovered=" + hovered?.Name);
            Check(CampaignFingerprint.Of(c) == before, "supply-warning observation changes no campaign state: " + label);
            await Capture("depot-pocket-" + language + "-" + size.X);
            GetViewport().PushInput(new InputEventMouseMotion { Position = new Vector2(800, 500) }, true);

            foreach (var neighbor in map.Neighbors[province]) c.ProvinceController[neighbor] = (ushort)poland.MapIndex;
            Morning(12);
            Check(!army.CutOff && army.DaysCutOff == 0, "real deblocking clears the pocket clock: " + label);
            EquipmentLogistics.ReplenishDay(c);
            Check(army.Equipment > kit && army.Personnel == 12000 && army.Strength == .8f,
                "restored route supplies durable kit without free personnel: " + label);
            game.Call("Refresh");
            game.VerificationSelectBattleOrders(army);
            await Wait(.3);
            var bar = game.FindChild("EquipmentBar_8802", true, false) as Control;
            Check(bar is not null && !bar.TooltipText.Contains(Loc.Get("No supply route for equipment replacements.")),
                "real equipment panel no longer reports a closed route: " + label);
            if (bar is null) continue;
            before = CampaignFingerprint.Of(c);
            for (var pulse = 0; pulse < 10; pulse++)
            {
                GetViewport().PushInput(new InputEventMouseMotion {
                    Position = bar.GetGlobalTransformWithCanvas() * (bar.Size * .5f) }, true);
                await Wait(.12);
            }
            await Wait(1.1);
            Check(GetViewport().GuiGetHoveredControl() == bar,
                "actual deblocked-equipment hover reaches the bar: " + label);
            Check(CampaignFingerprint.Of(c) == before,
                "deblocked-equipment observation changes no campaign state: " + label);
            await Capture("depot-deblocked-" + language + "-" + size.X);
            GetViewport().PushInput(new InputEventMouseMotion { Position = new Vector2(800, 500) }, true);
        }
    }
}
