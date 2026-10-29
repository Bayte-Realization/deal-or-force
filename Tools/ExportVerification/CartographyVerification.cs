using System;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyCartography(DealOrForceGame game)
    {
        VerifyBattleSymbolCache();
        var world = game.VerificationWorld;
        world.IsPaused = true;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id);
        world.ObserverMode = true;
        world.Player.IsHuman = false;
        var originalQuality = game.VerificationAtlasQuality;
        var originalStamp = CampaignFingerprint.Of(world);
        game.VerificationAtlasQuality = new QualityProfile();
        GetTree().Root.Size = new Vector2I(1920,1080);
        Loc.Language = "english"; game.Call("RefreshLocalisedText");
        foreach(var flat in new[]{false,true})
        {
            game.VerificationContinuityView(flat,52.2297f,21.0122f,5.35f);
            game.VerificationRiverCurveFilter=0;
            await Wait(1.2);await Capture("river-linear-"+(flat?"flat":"globe"));
            game.VerificationRiverCurveFilter=1;
            await Wait(.3);await Capture("river-curved-"+(flat?"flat":"globe"));
            Check(originalStamp==CampaignFingerprint.Of(world),"river contour switch is presentation-only in both projections");
        }
        var baseline = Array.IndexOf(OS.GetCmdlineUserArgs(), "--verify-cartography-baseline") >= 0;
        var prefix = baseline ? "atlas-before-" : "atlas-after-";
        foreach (var view in new[] {
            ("warsaw",false,52.2297f,21.0122f,5.35f,StrategicMapMode.Political),
            ("warsaw-flat",true,52.2297f,21.0122f,5.35f,StrategicMapMode.Political),
            ("europe",false,50f,12f,9.0f,StrategicMapMode.Political),
            ("alps",false,46.6f,10.2f,6.5f,StrategicMapMode.Political),
            ("coast",false,53f,-3f,7.3f,StrategicMapMode.Political),
            ("east-asia",false,33f,122f,9.0f,StrategicMapMode.Political),
            ("dnipro",false,49.0f,32.0f,6.1f,StrategicMapMode.Political),
            ("amazon",true,-3.1f,-60.0f,6.5f,StrategicMapMode.Terrain),
            ("dateline",true,63.0f,179.5f,8.0f,StrategicMapMode.Political),
            ("orbit",false,18f,10f,15f,StrategicMapMode.Political),
            ("europe-flat",true,50f,12f,7.8f,StrategicMapMode.Political),
            ("alps-terrain",true,46.6f,10.2f,6.5f,StrategicMapMode.Terrain),
            ("alps-relief",false,46.6f,10.2f,6.5f,StrategicMapMode.Relief) })
        {
            game.VerificationAtlasView(view.Item2,view.Item3,view.Item4,view.Item5,view.Item6);
            if(view.Item1.StartsWith("warsaw"))
            {
                game.VerificationAtlasSettlements=false;
                await Wait(1.2);await Capture("atlas-disabled-"+view.Item1);
                Check(game.VerificationAtlasBuildings==0,"disabled city miniatures have no visible instances");
            }
            game.VerificationAtlasSettlements = !baseline;
            await Wait(1.2);
            var stamp = CampaignFingerprint.Of(world);
            await Capture(prefix+view.Item1);
            Check(game.VerificationOccupationProjection==view.Item2,"atlas projection matches requested view");
            Check(stamp==CampaignFingerprint.Of(world),"atlas render and camera leave campaign unchanged");
            Check(game.VerificationAtlasBuildings is >=0 and <=1344,"city miniature instance budget is bounded");
            Check(game.VerificationAtlasFootprintsOnLand,"city miniature centre and corner samples remain on land");
            if (view.Item1.StartsWith("warsaw") && !baseline)
                Check(game.VerificationAtlasBuildings>0,"close city view contains urban miniatures");
            if(view.Item1=="orbit")Check(game.VerificationAtlasBuildings==0,"strategic orbit suppresses city miniatures");
        }
        game.VerificationAtlasView(false,51.5f,-.1f,5.35f,StrategicMapMode.Political);
        game.VerificationAtlasSettlements=true;
        await Wait(1.2);
        Check(game.VerificationAtlasBuildings>0 && game.VerificationAtlasFootprintsOnLand,"coastal city has land-checked miniatures");
        await Capture(prefix+"london-coast");
        game.VerificationAtlasSettlements=false;
        await Wait(.3);
        Check(game.VerificationAtlasBuildings==0,"city miniature switch hides existing instances");
        game.VerificationAtlasSettlements=true;
        game.VerificationAtlasQuality=new QualityProfile { CityLabels=false };
        await Wait(.3);
        Check(game.VerificationAtlasBuildings==0,"reduced city detail suppresses urban geometry");
        game.VerificationAtlasQuality=new QualityProfile();
        foreach(var language in new[]{"english","russian"})
        {
            Loc.Language=language;game.Call("RefreshLocalisedText");
            GetTree().Root.Size=new Vector2I(1920,1080);
            game.VerificationAtlasView(false,52.2297f,21.0122f,5.35f,StrategicMapMode.Political);
            await Wait(1.2);await Capture(prefix+"warsaw-1080-"+language);
            VerifyNavigationGroupHeadings(game,language+" 1080");
            GetTree().Root.Size=new Vector2I(1280,720);
            game.VerificationAtlasView(true,52.2297f,21.0122f,5.35f,StrategicMapMode.Political);
            await Wait(1.2);await Capture(prefix+"warsaw-720-"+language);
            VerifyNavigationGroupHeadings(game,language+" 720");
            Check(game.VerificationAtlasBuildings>0,"city miniatures survive compact viewport and localisation");
        }
        Check(originalStamp==CampaignFingerprint.Of(world),"all cartography views, quality and localisation leave simulation unchanged");
        game.VerificationAtlasSettlements=null;
        game.VerificationAtlasQuality=originalQuality;
    }

    private void VerifyNavigationGroupHeadings(DealOrForceGame game,string label)
    {
        var heading=game.FindChild("NavGroupWORLD",true,false) as Label;
        Check(heading is not null && heading.AutowrapMode==TextServer.AutowrapMode.WordSmart && !heading.ClipText,
            "navigation group headings wrap rather than clip "+label);
        if(heading is not null && heading.IsVisibleInTree())
            Check(heading.GetMinimumSize().X<=heading.Size.X+1 && heading.GetMinimumSize().Y<=heading.Size.Y+1,
                "navigation group heading fits its container "+label);
    }
}

public partial class DealOrForceGame
{
    internal QualityProfile VerificationAtlasQuality
    {
        get=>_globe.Quality;
        set{_qualityGovernor=null;_globe.ApplyQuality(value);}
    }
    internal bool? VerificationAtlasSettlements {set=>_globe.SettlementVerificationEnabled=value;}
    internal int VerificationAtlasBuildings=>_globe.SettlementBuildingCount;
    internal float VerificationRiverCurveFilter {set=>_globe.SetVerificationRiverCurveFilter(value);}
    internal bool VerificationAtlasFootprintsOnLand=>_globe.SettlementFootprintsOnLand();
    internal void VerificationAtlasView(bool flat,float latitude,float longitude,float distance,StrategicMapMode mode)
    { _globe.SetFlatMap(flat); _globe.SetMapMode(mode); _globe.FocusOn(latitude,longitude,distance); }
}

public sealed partial class GlobePresentation
{
    internal void SetVerificationRiverCurveFilter(float amount) =>
        _globeMaterial.SetShaderParameter("river_curve_filter",amount);
}
