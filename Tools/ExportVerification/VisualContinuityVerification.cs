using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyArmyCounterScale(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var army = world.Armies.First(a => a.NationId == world.Player.Id && a.ProvinceId > 0);
        var centre = world.ProvinceMap.CenterOf(army.ProvinceId);
        var before = CampaignFingerprint.Of(world);
        foreach (var flat in new[] { false, true })
        foreach (var distance in new[] { 5.35f, 7.4f })
        {
            game.VerificationContinuityView(flat, centre.Latitude, centre.Longitude, distance);
            await Wait(.6);
            var result = game.VerificationArmyCounterScale(army);
            GD.Print($"ARMY_COUNTER_SCALE flat={flat} distance={distance} width={result.Width:F6} max_drift={result.MaxDrift:F7}");
            Check(result.Visible, "stationary army counter is visible at its province");
            Check(result.Width > 0f && result.MaxDrift < .00001f,
                "rebuilding army counters preserves the projected size at a fixed camera");
            await Capture($"army-counter-scale-{(flat ? "flat" : "globe")}-{(distance < 6 ? "near" : "far")}");
        }
        Check(before == CampaignFingerprint.Of(world), "army counter refreshes preserve the campaign fingerprint");
    }

    private async Task VerifyVisualContinuity(DealOrForceGame game)
    {
        var world=game.VerificationWorld;
        world.IsPaused=true;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id);
        game.VerificationAtlasQuality=new QualityProfile();
        game.VerificationSelectPlayerCountry();
        GetTree().Root.Size=new Vector2I(1920,1080);
        foreach(var flat in new[]{false,true})
        {
            game.VerificationContinuityView(flat,52.23f,21.01f,5.35f);
            await Wait(1);
            var stamp=CampaignFingerprint.Of(world);
            game.VerificationMapProcessing=false;
            game.VerificationRiverInset=.18f;
            await Wait(.2);await Capture("river-old-"+(flat?"flat":"globe"));
            game.VerificationRiverInset=TerrainCatalog.Water.RiverInset;
            await Wait(.2);await Capture("river-continuous-"+(flat?"flat":"globe"));
            Check(stamp==CampaignFingerprint.Of(world),"river presentation does not change crossings or campaign state");
            game.VerificationMapProcessing=true;
        }
        foreach(var language in new[]{"english","russian"})
        foreach(var size in new[]{new Vector2I(1920,1080),new Vector2I(1280,720)})
        {
            Loc.Language=language;GetTree().Root.Size=size;
            game.Call("RefreshLocalisedText");
            game.Call("SetDeskSide",false);game.Call("SetDeskOpen",true);
            await Wait(.2);
            var panel=(Control)game.FindChild("SituationDesk",true,false);
            var buttons=Enum.GetValues<DeskLens>().Select(l=>(Button)panel.FindChild("DeskLens"+l,true,false)).ToArray();
            foreach(var lens in Enum.GetValues<DeskLens>())
            {
                var button=buttons[(int)lens];
                button.GrabFocus();button.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(.08);
                Check(world.IsPaused && world.Settings["desk.lens"]==lens.ToString(),"desk lens changes immediately while paused");
                Check(button.HasFocus(),"desk lens remains keyboard reachable");
                Check(((Label)panel.FindChild("DeskPurpose",true,false)).Text==Loc.Get(DeskPurpose(lens)),
                    "desk purpose matches selected lens and language");
            }
            foreach(var button in buttons)
            {
                var rect=button.GetGlobalRect();var bounds=panel.GetGlobalRect();
                Check(button.IsVisibleInTree() && !button.ClipText && button.Size.X+1>=button.GetMinimumSize().X,
                    "desk caption and count are not clipped "+language+size.X+button.Name);
                Check(bounds.HasPoint(rect.Position+Vector2.One) && bounds.HasPoint(rect.End-Vector2.One),
                    "wrapped lens stays within the dock");
                Check(!buttons.Any(other=>other!=button && rect.Intersects(other.GetGlobalRect())),"desk lenses do not overlap");
            }
            await Capture("desk-readable-"+language+"-"+size.X);
        }
        await VerifyDeskSelectionClearance(game);
        await VerifySupportProvinceAnchors(game);
        Check(PoliticalPolicies.GoalMet(world,world.Player,new PoliticalAgenda{Goal="peace"})==
              !world.Wars.Any(w=>!w.Ended&&w.Involves(world.Player.Id)),"production F# policy goal runs inside Godot");
    }

    private static string DeskPurpose(DeskLens lens)=>lens switch
    {
        DeskLens.Security=>"the front, the pockets, and whether anything stands in the way",
        DeskLens.Economy=>"the balance, the debt, and the price of money",
        DeskLens.Society=>"the street, the mood, and how the country carries the war",
        DeskLens.Diplomacy=>"the papers on the table and the friends who are counting",
        _=>"what is ready to be decided, and with what credit"
    };
}

public partial class DealOrForceGame
{
    internal (bool Visible, float Width, float MaxDrift) VerificationArmyCounterScale(Army army) =>
        _globe.VerificationArmyCounterScale(army);

    internal bool VerificationMapProcessing {set=>_globe.SetProcess(value);}
    internal float VerificationRiverInset {set=>_globe.VerificationRiverInset=value;}
    internal void VerificationContinuityView(bool flat,float latitude,float longitude,float distance)=>
        _globe.VerificationContinuityView(flat,latitude,longitude,distance);
}

public sealed partial class GlobePresentation
{
    internal (bool Visible, float Width, float MaxDrift) VerificationArmyCounterScale(Army army)
    {
        ReconcileArmyMarkers(_campaign);
        var key = ArmyPresentationBucket(_campaign, army);
        if (!_armyStacks.TryGetValue(key, out var marker)) return default;
        UpdateCounterScale(force: true);
        var width = _flatMap ? _counterWorldPixel * _counterFlatFar : SurfaceCounterWidth(marker.Node);
        var maxDrift = MathF.Abs(marker.Node.Scale.X - width);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            ReconcileArmyMarkers(_campaign);
            maxDrift = MathF.Max(maxDrift, MathF.Abs(marker.Node.Scale.X - width));
        }
        return (marker.Node.IsVisibleInTree() && marker.Plate.IsVisibleInTree(), width, maxDrift);
    }

    internal float VerificationRiverInset {set=>_globeMaterial.SetShaderParameter("river_inset",value);}
    internal void VerificationContinuityView(bool flat,float latitude,float longitude,float distance)
    {
        SetShowcasePan(false);SetFreeAutoOrbit(0);SetFreeCamera(false);
        SetFlatMap(flat);SetMapMode(StrategicMapMode.Political);FocusOn(latitude,longitude,distance);
        _pitch=_targetPitch;_yaw=_targetYaw;_distance=_targetDistance;
        _flatX=_targetFlatX;_flatY=_targetFlatY;_flatZoom=_targetFlatZoom;
        ApplyCamera();
    }
}
