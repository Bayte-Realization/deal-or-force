using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    // Only opt-in, private paused world. Never opens or overwrites a player's save.
    private async Task VerifyOccupationMap(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id);
        world.ObserverMode = true;
        // This private inspection fixture must not tint foreign ground with human fog.
        world.Player.IsHuman = false;
        var owner = world.NationByTag("UKR");
        var map = world.ProvinceMap;
        var province = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => !map.IsWater(p) && map.ProvinceToNation[p] == owner.MapIndex)
            .OrderBy(p => { var at = map.CenterOf(p); return Math.Abs(at.Latitude - 50.0f) + Math.Abs(at.Longitude - 36.25f); }).First();
        var region = map.ProvinceToState[province];
        var ground = Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToState[p] == region && !map.IsWater(p)).ToArray();
        var same = new Nation { Id = world.Nations.Max(n => n.Id) + 1, MapIndex = 60000,
            Tag = "OCC", Name = "Occupation contrast fixture", Color = owner.Color };
        world.Nations.Add(same);
        var point = map.CenterOf(province);
        foreach (var controller in new[] { world.NationByTag("RUS"), same })
        {
            foreach (var p in ground) world.SetProvinceController(p, (ushort)controller.MapIndex);
            Check(ground.All(p => map.ProvinceToNation[p] == owner.MapIndex), "fixture remains occupation, not annexation");
            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
            foreach (var flat in new[] { false, true })
            {
                Loc.Language = language; game.Call("RefreshLocalisedText");
                GetTree().Root.Size = size;
                game.VerificationOccupationView(flat, point.Latitude, point.Longitude, 6.8f);
                await Wait(.7);
                var before = CampaignFingerprint.Of(world);
                Check(game.VerificationOccupationProjection == flat, "occupation projection " + flat);
                Check(ground.All(p => map.ProvinceToNation[p] == owner.MapIndex &&
                    world.ProvinceController[p] == controller.MapIndex), "owner and controller stay distinct while viewing");
                await Capture("occupation-" + (controller == same ? "identical" : "national") + "-" +
                    (flat ? "flat" : "globe") + "-" + language + "-" + size.X);
                Check(before == CampaignFingerprint.Of(world), "occupation rendering and capture are read-only");
            }
        }
        Loc.Language = "english";
        GetTree().Root.Size = new Vector2I(1920,1080);
        foreach (var flat in new[] { false, true })
        foreach (var distance in new[] { 5.5f, 10f })
        {
            game.VerificationOccupationView(flat, point.Latitude, point.Longitude, distance);
            await Wait(.8);
            var before = CampaignFingerprint.Of(world);
            await Capture("occupation-identical-" + (flat ? "flat" : "globe") + "-zoom-" + distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            Check(CampaignFingerprint.Of(world) == before, "zooming changes no occupation state");
        }
    }
}
public partial class DealOrForceGame
{
    internal void VerificationOccupationView(bool flat, float latitude, float longitude, float distance)
    { _globe.SetFlatMap(flat); _globe.FocusOn(latitude, longitude, distance); }
    internal bool VerificationOccupationProjection => _globe.IsFlatMap;
}
