using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyEquipmentReplenishment(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var poland = c.NationByTag("POL");
        var province = c.Armies.First(a => a.NationId == poland.Id && a.ProvinceId > 0 && a.EmbarkedFleetId < 0).ProvinceId;
        c.ConfigureSinglePlayer(poland.Id); c.IsPaused = true;
        c.Armies.Clear(); c.Battles.Clear(); c.Wars.Clear(); c.FrontLines.Clear();
        var army = new Army { Id = 8801, NationId = poland.Id, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], TemplateId = "line_infantry",
            Divisions = 1, Personnel = 12000, MaxPersonnel = 15000, Strength = .8f, Organisation = .75f,
            SupplyLevel = 1, Equipment = .2f, Name = "1. POL Division" };
        (army.Latitude, army.Longitude) = c.ProvinceMap.CenterOf(province);
        c.Armies.Add(army);
        game.VerificationFocusAdministration(poland);
        poland.MilitarySpending = c.Defines.BaselineMilitarySpending;
        poland.StockpilesSeeded = true;

        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
            army.CutOff = false; army.Equipment = .2f;
            foreach (var good in StrategicStockpiles.TrackedGoods) poland.Stockpiles[good] = 100;
            var before = CampaignFingerprint.Of(c);
            game.VerificationSelectBattleOrders(army);
            await Wait(.3);
            Check(CampaignFingerprint.Of(c) == before, "opening equipment inspector does not repair or spend: " + language + size.X);
            var hours = c.ElapsedHours; var people = army.Personnel; var strength = army.Strength;
            StrategicStockpiles.TickDay(c);
            Check(army.Equipment > .2f && poland.DailyEquipmentReplacement.GetValueOrDefault("Arms") > 0 &&
                army.Personnel == people && army.Strength == strength && c.ElapsedHours == hours,
                "real depot pass supplies kit independently of personnel: " + language + size.X);
            game.VerificationSelectBattleOrders(army);
            await Wait(.3);
            var bar = game.FindChild("EquipmentBar_8801", true, false) as Control;
            Check(bar is not null && bar.IsVisibleInTree() && bar.Size.X >= 30,
                "equipment bar remains visible and usable: " + language + size.X);
            if (bar is null) continue;
            // Ui.Tip deliberately inserts word-wrap newlines. Verify every word,
            // independent of the selected language's line length and user measure.
            var readable = string.Join(" ", bar.TooltipText.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            Check(readable.Contains(Loc.Get("Equipment and personnel are replaced separately. Ammunition is a consumable, not equipment completeness.")) &&
                bar.TooltipText.Contains(Loc.Get("Arms")), "equipment hover explains distinction and material bill: " + language + size.X);
            before = CampaignFingerprint.Of(c);
            GetViewport().NotifyMouseExited(); GetViewport().NotifyMouseEntered();
            var point = bar.GetGlobalTransformWithCanvas() * (bar.Size * .5f);
            GetViewport().PushInput(new InputEventMouseMotion { Position = point }, true);
            await Wait(1.1);
            Check(GetViewport().GuiGetHoveredControl() == bar && CampaignFingerprint.Of(c) == before,
                "actual equipment hover reaches the bar without changing the world: " + language + size.X);
            await Capture("equipment-delivery-" + language + "-" + size.X);
            GetViewport().PushInput(new InputEventMouseMotion { Position = new Vector2(700, 400) }, true);
            army.CutOff = true;
            var equipment = army.Equipment; var arms = poland.Stockpiles["Arms"];
            EquipmentLogistics.ReplenishDay(c);
            Check(army.Equipment == equipment && poland.Stockpiles["Arms"] == arms,
                "no delivery teleports through encirclement: " + language + size.X);
            game.VerificationSelectBattleOrders(army);
            await Wait(.3);
            bar = game.FindChild("EquipmentBar_8801", true, false) as Control;
            Check(bar is not null && bar.TooltipText.Contains(Loc.Get("No supply route for equipment replacements.")),
                "encirclement refusal is localized in the actual inspector: " + language + size.X);
            if (bar is null) continue;
            GetViewport().PushInput(new InputEventMouseMotion { Position = bar.GetGlobalTransformWithCanvas() * (bar.Size * .5f) }, true);
            await Wait(1.1);
            await Capture("equipment-blocked-" + language + "-" + size.X);
            GetViewport().PushInput(new InputEventMouseMotion { Position = new Vector2(700, 400) }, true);
        }
    }
}
