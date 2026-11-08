using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Modding;

namespace DealOrForce.Simulation;

// Physical sites are sparse and opt-in. Ordinary campaigns keep these collections empty.
public sealed class CrossoverWork
{
    public string Id = "";
    public int Province, State, Nation;
    public BuildingKind Building;
    public float X, Y, Z;
    public bool Active;
    public long[] Blocks = Array.Empty<long>();
    public CrossoverWork Copy() => new() { Id=Id, Province=Province, State=State, Nation=Nation,
        Building=Building, X=X, Y=Y, Z=Z, Active=Active, Blocks=(long[])Blocks.Clone() };
}
public sealed record CrossoverImpact(long Hour, int Province, string Kind, long Population, float Gdp, string Message);

public sealed partial class Campaign
{
    public const int CrossoverRequiredBlocks = 24;
    public readonly HashSet<long> CrossoverScars = new();
    public readonly List<CrossoverWork> CrossoverWorks = new();
    public readonly List<CrossoverImpact> CrossoverImpacts = new();
    private ProvinceTopology _crossoverAreaMap;
    private double[] _crossoverAreas;

    public static long CrossoverCellKey(int x, int y, int z) =>
        ((long)(x+1600)<<24) | ((long)(y+1600)<<12) | (uint)(z+1600);

    public int CrossoverProvince(double x, double y, double z)
    {
        var map=ProvinceMap;
        var r=Math.Sqrt(x*x+y*y+z*z);
        if (map is null || !double.IsFinite(r) || r<1) return 0;
        var u=(Math.Atan2(z,x)+Math.PI)/(Math.PI*2);
        var v=(Math.PI/2-Math.Asin(Math.Clamp(y/r,-1,1)))/Math.PI;
        var cx=Math.Clamp((int)(u*map.Width),0,map.Width-1);
        var cy=Math.Clamp((int)(v*map.Height),0,map.Height-1);
        return map.CellToProvince[cy*map.Width+cx];
    }

    private bool CrossoverLand(int p) => ProvinceMap is { } map && p>0 && p<=map.ProvinceCount &&
        !map.IsWater(p) && !DestroyedProvinces.Contains(p);

    public static bool CrossoverSurfaceCell(CrossoverVoxel v)
    {
        if (v.surface is not null) return CrossoverSurface.ValidFlat(v);
        if (!v.terrain || v.pose is not null || v.town || v.flora) return false;
        double min=0,max=0;
        foreach(var axis in new[]{v.x,v.y,v.z}) {
            var a=(double)axis*axis; var b=(axis+1d)*(axis+1d);
            min+=Math.Min(a,b); max+=Math.Max(a,b);
        }
        const double r=1023.4;
        return min<=r*r && max>=r*r;
    }

    // Baseline records old holes without killing people retroactively. Later edits are
    // queued with the campaign and persisted; filling/re-mining a cell cannot farm damage.
    public void ApplyCrossoverSnapshot(CrossoverVoxel[] cells, bool baseline)
    {
        if (IsHotseat || Commands.SubmitInterceptor is not null || ProvinceMap is null) return;
        foreach (var v in cells.Where(v=>(v.block==0 || v.excavated) && CrossoverSurfaceCell(v))
                     .OrderBy(v=>CrossoverCellKey(v.x,v.y,v.z))) {
            // Flat surrogate addresses must not collide with scars from the old sphere.
            var key=CrossoverCellKey(v.x,v.y,v.z) | (v.surface is null ? 0 : 1L<<60);
            if (CrossoverScars.Count>=16384 || !CrossoverScars.Add(key) || baseline) continue;
            var p=v.surface is not null ? CrossoverSurface.Province(v,ProvinceMap) :
                CrossoverProvince(v.x+.5,v.y+.5,v.z+.5);
            if (!CrossoverLand(p)) continue;
            var s=States.Find(s=>s.MapIndex==ProvinceMap.ProvinceToState[p]);
            var n=Nations.Find(n=>n.MapIndex==ProvinceMap.ProvinceToNation[p]);
            if (s is null || n is null) continue;
            var area=CrossoverRegionArea(s.MapIndex);
            var share=Math.Min(.015,CrossoverSurface.Area(v)/Math.Max(64,area));
            var loss=Math.Min(s.Population,Math.Max(s.Population>0?1:0,(long)(s.Population*share)));
            loss=Math.Min(loss,n.Population);
            var gdp=n.Population>0 ? (float)(n.Gdp*(loss/(double)n.Population)) : 0;
            s.Population-=loss; ResizePopulation(s.PopulationGroups,s.Population);
            n.Population-=loss; n.Gdp=Math.Max(0,n.Gdp-gdp);
            s.WarDamage=Math.Clamp(s.WarDamage+(float)share,0,1);
            foreach(var good in s.ResourceDeposits.Keys.ToArray()) s.ResourceDeposits[good]*=1-(float)share;
            RecordCrossover(new(ElapsedHours,p,"excavation",loss,gdp,
                $"Excavation: {loss:N0} population and ${gdp*1000:0.###}M GDP lost locally."));
            MarkStrategicChange();
        }
        if (baseline) return;
        var standing=cells.Where(v=>v.block>0 && !v.flora && !v.town)
            .Select(v=>CrossoverCellKey(v.x,v.y,v.z)).ToHashSet();
        foreach(var work in CrossoverWorks.Where(w=>w.Active)) {
            var stillLand=CrossoverLand(work.Province);
            var state=States.Find(s=>s.MapIndex==ProvinceMap.ProvinceToState[work.Province]);
            var oldState=States.Find(s=>s.MapIndex==work.State);
            if (stillLand && state==oldState && work.Blocks.Count(standing.Contains)>=CrossoverRequiredBlocks/2 &&
                state.Buildings.GetValueOrDefault(work.Building)>0) continue;
            work.Active=false;
            // Whole-province destruction already accounts for its lost assets.
            if (stillLand && oldState is not null && oldState.Buildings.GetValueOrDefault(work.Building)>0) {
                oldState.Buildings[work.Building]--;
                if(work.Building==BuildingKind.Hospital) oldState.StandardOfLiving=Math.Clamp(oldState.StandardOfLiving-.018f,0,1);
                var owner=Nations.Find(n=>n.MapIndex==oldState.NationMapIndex);
                if (owner is not null) {
                    var def=BuildingCatalog.Get(work.Building);
                    owner.ConstructionCapacity=Math.Max(0,owner.ConstructionCapacity-def.CapacityGain);
                }
            }
            RecordCrossover(new(ElapsedHours,work.Province,"demolition",0,0,
                $"{BuildingCatalog.Get(work.Building).Name}: site dismantled; strategic contribution removed."));
            MarkStrategicChange();
        }
    }

    private double CrossoverRegionArea(int state)
    {
        var map=ProvinceMap;
        if (_crossoverAreaMap!=map) {
            _crossoverAreaMap=map; _crossoverAreas=new double[map.ProvinceCount+1];
            var scale=1024d*1024*(2*Math.PI/map.Width)*(Math.PI/map.Height);
            for(var y=0;y<map.Height;y++) {
                var rowArea=scale*Math.Cos((.5-(y+.5)/map.Height)*Math.PI);
                for(var x=0;x<map.Width;x++) _crossoverAreas[map.CellToProvince[y*map.Width+x]]+=rowArea;
            }
        }
        double result=0;
        for(var p=1;p<=map.ProvinceCount;p++)
            if(map.ProvinceToState[p]==state && !map.IsWater(p)) result+=_crossoverAreas[p];
        return result;
    }

    public int CrossoverNearbyBlocks(CrossoverVoxel[] cells, double x, double y, double z, int province)
    {
        return EligibleCrossoverCells(cells,x,y,z,province).Count();
    }
    private IEnumerable<CrossoverVoxel> EligibleCrossoverCells(CrossoverVoxel[] cells, double x, double y, double z, int province)
    {
        var reserved=CrossoverWorks.SelectMany(w=>w.Blocks).ToHashSet();
        foreach(var v in cells) {
            if(v.block<=0 || v.surface is not null || v.pose is not {Length:7} || v.town || v.flora ||
                reserved.Contains(CrossoverCellKey(v.x,v.y,v.z))) continue;
            var dx=v.pose[0]-x; var dy=v.pose[1]-y; var dz=v.pose[2]-z;
            var r=Math.Sqrt((double)v.pose[0]*v.pose[0]+(double)v.pose[1]*v.pose[1]+(double)v.pose[2]*v.pose[2]);
            if(dx*dx+dy*dy+dz*dz<=144 && r>=1023.5 && r<=1048 &&
                CrossoverProvince(v.pose[0],v.pose[1],v.pose[2]) is var cellProvince && CrossoverLand(cellProvince) &&
                ProvinceMap.ProvinceToState[cellProvince]==ProvinceMap.ProvinceToState[province] &&
                ProvinceMap.ProvinceToNation[cellProvince]==ProvinceMap.ProvinceToNation[province]) yield return v;
        }
    }

    public string RegisterCrossoverWork(string id, string kind, int province, float x, float y, float z, CrossoverVoxel[] cells)
    {
        var r=Math.Sqrt((double)x*x+(double)y*y+(double)z*z);
        if(IsHotseat || Commands.SubmitInterceptor is not null) return "Use a local single-player campaign.";
        if(!float.IsFinite(x)||!float.IsFinite(y)||!float.IsFinite(z)||r<1023||r>1048 ||
            !CrossoverLand(province)||CrossoverProvince(x,y,z)!=province) return "Stand on the selected land province, near your building.";
        if(CrossoverWorks.Count>=128) return "The campaign has reached its 128-site limit.";
        if(CrossoverWorks.Any(w=>w.Id==id || (w.X-x)*(w.X-x)+(w.Y-y)*(w.Y-y)+(w.Z-z)*(w.Z-z)<576))
            return "This site is already registered or reserved. Build at least 24 blocks away.";
        var building=kind switch { "workshop"=>BuildingKind.CivilianIndustry, "hospital"=>BuildingKind.Hospital,
            "supply"=>BuildingKind.Infrastructure, _=>(BuildingKind)(-1) };
        if((int)building<0) return "Unknown site type.";
        var selected=EligibleCrossoverCells(cells,x,y,z,province).OrderBy(v=>CrossoverCellKey(v.x,v.y,v.z)).ToArray();
        if(selected.Length<CrossoverRequiredBlocks) return $"Build {CrossoverRequiredBlocks} player blocks within 12 blocks here ({selected.Length} eligible); generated towns and plants do not count.";
        var s=States.Find(s=>s.MapIndex==ProvinceMap.ProvinceToState[province]);
        var n=Nations.Find(n=>n.MapIndex==ProvinceMap.ProvinceToNation[province]);
        if(s is null || n is null) return "This land has no active region or country.";
        var def=BuildingCatalog.Get(building);
        if(n.Treasury<def.Cost) return $"{n.Name} needs ${def.Cost:0.00}B treasury to commission this site.";
        n.Treasury-=def.Cost;
        s.Buildings[building]=s.Buildings.GetValueOrDefault(building)+1;
        // No GDP step: the market counts the building's output from next week (ConstructionSystem.Complete).
        n.ConstructionCapacity+=def.CapacityGain;
        if (building==BuildingKind.Hospital) s.StandardOfLiving=Math.Clamp(s.StandardOfLiving+.018f,0,1);
        CrossoverWorks.Add(new() {Id=id,Province=province,State=s.MapIndex,Nation=n.Id,Building=building,
            X=x,Y=y,Z=z,Active=true,Blocks=selected.Select(v=>CrossoverCellKey(v.x,v.y,v.z)).ToArray()});
        RecordCrossover(new(ElapsedHours,province,"construction",0,0,
            $"{def.Name} commissioned in {s.Name}; ${def.Cost:0.00}B paid, +1 real regional building."));
        MarkStrategicChange();
        return "";
    }

    private void RecordCrossover(CrossoverImpact impact)
    {
        CrossoverImpacts.Add(impact);
        if(CrossoverImpacts.Count>24) CrossoverImpacts.RemoveRange(0,CrossoverImpacts.Count-24);
    }
}
