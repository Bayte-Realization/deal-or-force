using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifySupportProvinceAnchors(DealOrForceGame game)
    {
        var world=game.VerificationWorld;var player=world.Player;var map=world.ProvinceMap;
        var army=world.Armies.First(a=>a.NationId==player.Id&&a.ProvinceId>0);
        var province=army.ProvinceId;var centre=map.CenterOf(province);
        var firstId=world.SupportFormations.Select(f=>f.Id).DefaultIfEmpty(0).Max()+1;
        for(var i=0;i<3;i++)world.SupportFormations.Add(new SupportFormation
        {Id=firstId+i,NationId=player.Id,TypeId="artillery_brigade",BaseProvinceId=province,StateMapIndex=map.ProvinceToState[province]});
        world.MarkStrategicChange();
        GetTree().Root.Size=new Vector2I(1920,1080);
        foreach(var flat in new[]{false,true})
        foreach(var distance in new[]{5.35f,7.4f})
        {
            game.VerificationContinuityView(flat,centre.Latitude,centre.Longitude,distance);
            await Wait(.5);game.VerificationRefreshSupportLayout();await Wait(.1);
            var stamp=CampaignFingerprint.Of(world);
            var result=game.VerificationSupportAudit();
            GD.Print($"SUPPORT_LAYOUT flat={flat} distance={distance} nodes={result.Visible} misplaced={result.Misplaced} overlaps={result.Overlaps} crowded={result.Crowded}");
            Check(result.Visible>=3,"co-located support fixture renders in both projections and zoom levels");
            Check(result.Misplaced==0,"support anchors and rendered centres remain in their actual base province");
            Check(result.Overlaps==0,"uncrowded support cards do not cover land counters; crowded cards stay behind them");
            Check(result.Unpickable==0,"separated support cards retain their real formation hit targets");
            game.VerificationRepeatSupportLayout();var again=game.VerificationSupportAudit();
            Check(result.Positions==again.Positions,"repeated support layout is stable at rest");
            Check(stamp==CampaignFingerprint.Of(world),"support layout does not alter units, commands or RNG");
            await Capture("support-anchored-"+(flat?"flat":"globe")+"-"+distance.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture));
        }
        var next=map.Neighbors[province].FirstOrDefault(p=>!map.IsWater(p)&&map.ProvinceToNation[p]==player.MapIndex);
        Check(next>0,"support fixture has a neighbouring home province");
        if(next>0)
        {
            var formation=world.SupportFormations.First(f=>f.Id==firstId);
            formation.BaseProvinceId=next;formation.StateMapIndex=map.ProvinceToState[next];world.MarkStrategicChange();
            game.VerificationRefreshSupportLayout();await Wait(.2);
            Check(game.VerificationSupportAudit().Misplaced==0,"visual base follows a changed BaseProvinceId without stale geographic offsets");
        }
    }
}

public partial class DealOrForceGame
{
    internal void VerificationRefreshSupportLayout()=>_globe.VerificationRefreshSupportLayout();
    internal void VerificationRepeatSupportLayout()=>_globe.VerificationRepeatSupportLayout();
    internal (int Visible,int Misplaced,int Overlaps,int Crowded,int Unpickable,int Positions) VerificationSupportAudit()=>_globe.VerificationSupportAudit();
}

public sealed partial class GlobePresentation
{
    internal void VerificationRepeatSupportLayout()=>LayoutSupportCounters();
    internal void VerificationRefreshSupportLayout()
    {SyncSupportMarkers();ReconcileArmyMarkers(_campaign);SnapArmyMarkerPositions();ReprojectSupportMarkers(true);LayoutSupportCounters();}
    internal (int Visible,int Misplaced,int Overlaps,int Crowded,int Unpickable,int Positions) VerificationSupportAudit()
    {
        var visible=0;var misplaced=0;var overlaps=0;var crowded=0;var unpickable=0;var hash=new HashCode();
        foreach(var marker in _supportMapMarkers.Values)
        {
            if(!TryCounterScreenRoom(marker.Plate,out var room))continue;
            visible++;hash.Add(marker.Formation.Id);hash.Add(marker.Node.Position);
            var centre=_campaign.ProvinceMap.CenterOf(marker.Formation.BaseProvinceId);
            if(Math.Abs(centre.Latitude-marker.Latitude)>.00001f||Math.Abs(centre.Longitude-marker.Longitude)>.00001f||
               !TryLocate(_camera.UnprojectPosition(marker.Node.GlobalPosition),out _,out _,out _,out var under)||
               under!=marker.Formation.BaseProvinceId)misplaced++;
            if(marker.DisplayFaded)
            {
                crowded++;
                if(marker.Plate.NoDepthTest||marker.Readiness.NoDepthTest||marker.Plate.AlphaCut==SpriteBase3D.AlphaCutMode.Discard||
                   (_flatMap?marker.Node.Position.Z>=FlatMarkerZ:marker.Node.Position.Length()>=SurfaceMarkerRadius))overlaps++;
            }
            else
            {
                foreach(var army in _armyStacks.Values)
                    if(TryCounterScreenRoom(army.Plate,out var ground)&&room.Intersects(ground)){overlaps++;break;}
                if(!TryPickSupport(_camera.UnprojectPosition(marker.Node.GlobalPosition),out var picked)||picked.Id!=marker.Formation.Id)unpickable++;
            }
        }
        return(visible,misplaced,overlaps,crowded,unpickable,hash.ToHashCode());
    }
}
