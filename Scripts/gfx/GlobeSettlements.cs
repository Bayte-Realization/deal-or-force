using System;
using System.Collections.Generic;
using DealOrForce.Presentation;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private const int SettlementLimit = 96;
    private const int SettlementCapacity = SettlementLimit * SettlementSilhouette.MaximumBlocks;
    private readonly Dictionary<int, SettlementSilhouette.Block[]> _settlementShapes = new();
    /// <summary>How many cities stand in each region (by its map index), for sharing its townspeople out.</summary>
    private readonly Dictionary<int, int> _settlementCitiesInRegion = new();
    private (ProvinceTopology Map, int Revision) _settlementCountedFor;
    private MultiMeshInstance3D _settlementBuildings, _settlementRoofs;
    private readonly CityMiniatureRenderer _cityMiniatures = new();
    private double _settlementClock;
    private readonly (float Latitude,float Longitude,float Width,float Depth,float LongitudeScale)[] _settlementFootprints =
        new (float,float,float,float,float)[SettlementCapacity];
    internal bool? SettlementVerificationEnabled;
    internal int SettlementBuildingCount => (_settlementBuildings?.Multimesh.VisibleInstanceCount ?? 0) + _cityMiniatures.VisibleInstanceCount;
    internal int SettlementCityCount { get; private set; }
    internal int SettlementRejectedWater { get; private set; }

    private void CreateSettlementModels()
    {
        MultiMeshInstance3D Layer(string name, Color color) => new()
        {
            Name = name,
            CustomAabb = new Aabb(new Vector3(-120f, -120f, -120f), new Vector3(240f, 240f, 240f)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true,
                Mesh = new BoxMesh { Size = Vector3.One, Material = new StandardMaterial3D
                {
                    AlbedoColor = color, VertexColorUseAsAlbedo = true,
                    Roughness = .95f, Metallic = 0, SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled
                } },
                InstanceCount = SettlementCapacity, VisibleInstanceCount = 0
            }
        };
        _settlementBuildings = Layer("UrbanDistrictBuildings", Ui.Colour("map.city_building"));
        _settlementRoofs = Layer("UrbanDistrictRoofs", Ui.Colour("map.city_roof"));
        _cityRoot.AddChild(_settlementBuildings); _cityRoot.AddChild(_settlementRoofs);
    }

    private void UpdateSettlementModels(double delta)
    {
        var sunDir = IsInstanceValid(_daySun) ? _daySun.GlobalBasis.Z.Normalized() : new Vector3(0.35f, 0.55f, 0.75f);
        if (IsInstanceValid(_cityRoot))
        {
            _cityMiniatures.Update(new CityMiniatureRenderer.FrameInput(
                _cityRoot, _camera, _campaign, _cities, _cityAnchors, _cityIsCapital,
                _mapApplied, _flatMap, _distance, _flatZoom, _flatX,
                QualityCityLabels, CityLabelsEnabled, SettlementVerificationEnabled,
                _mapLookMode, sunDir, LocalReliefLift,
                (int)MathF.Round(_reliefDisplacement * 10000f) + (_reliefHeightGrid is null ? 0 : 1)));
        }

        var visibility = SettlementSilhouette.Visibility(_flatMap,_distance,_flatZoom);
        var wanted = !_cityMiniatures.IsHandlingCurrentLook &&
            _mapApplied && QualityCityLabels && CityLabelsEnabled &&
            _cityRoot is { Visible:true } && _cityAnchors.Length == _cities.Count &&
            (SettlementVerificationEnabled ?? SettingsCatalog.Flag("map_city_models")) && visibility > .015f;
        if (!wanted)
        {
            if (_settlementBuildings is not null)
            { _settlementBuildings.Multimesh.VisibleInstanceCount=0; _settlementRoofs.Multimesh.VisibleInstanceCount=0; }
            SettlementCityCount=0; return;
        }
        if (_settlementBuildings is null) CreateSettlementModels();
        _settlementClock += delta;
        if (_settlementClock < .10 && SettlementBuildingCount > 0) return;
        _settlementClock=0; SettlementCityCount=0; SettlementRejectedWater=0;
        var count=0;
        foreach (var pin in _cityPins)
        {
            if (SettlementCityCount >= SettlementLimit) break;
            var index=pin.City;
            if (!pin.Node.Visible || index < 0 || index >= _cities.Count) continue;
            var city=_cities[index];
            // A city has no head count of its own: its region's townspeople are shared equally
            // among the cities standing in it. Big enough to draw from 150 000, or a capital.
            var people=SettlementPeople(index);
            if (people < 150_000 && !IsCapitalNow(index)) continue;
            var anchor=_cityAnchors[index];
            if (TerrainGone(anchor.Latitude,anchor.Longitude)) continue;
            if (!_settlementShapes.TryGetValue(index,out var blocks))
            {
                if (_settlementShapes.Count >= 256) _settlementShapes.Clear();
                _settlementShapes[index]=blocks=SettlementSilhouette.Build(city.Id,people);
            }
            var before=count;
            var latitudeScale=Math.Max(.12f,Mathf.Cos(Mathf.DegToRad(anchor.Latitude)));
            foreach (var block in blocks)
            {
                var latitude=Math.Clamp(anchor.Latitude+block.North,-89.9f,89.9f);
                var longitude=anchor.Longitude+block.East/latitudeScale;
                if (!SettlementOnLand(latitude,longitude,block.Width,block.Depth,latitudeScale))
                { SettlementRejectedWater++; continue; }
                var angular=GlobeRadius*Mathf.Pi/180f;
                var width=block.Width*(_flatMap ? FlatMapWidth/360f/latitudeScale : angular);
                var depth=block.Depth*(_flatMap ? FlatMapHeight/180f : angular);
                var height=block.Height*visibility;
                var basis = _flatMap ? Basis.Identity : CountryLabelSurfaceBasis(latitude, longitude, 1f, 0f);
                var origin = _flatMap ? LocalFlatPoint(latitude, longitude, FlatLineZ) : LocalSurfacePoint(latitude, longitude, GlobeRadius * 1.0002f);
                // Footprints shrink continuously as the camera recedes; no alpha blend,
                // per-building Node, light, physics body or new picking surface.
                var fade=Mathf.Sqrt(visibility);
                var shape=new Basis(basis.X*width*fade,basis.Y*depth*fade,basis.Z*height);
                _settlementBuildings.Multimesh.SetInstanceTransform(count,new Transform3D(shape,origin+basis.Z*(height*.5f)));
                _settlementBuildings.Multimesh.SetInstanceColor(count,new Color(block.Shade,block.Shade,block.Shade));
                var roofShape=new Basis(basis.X*width*fade*.88f,basis.Y*depth*fade*.88f,basis.Z*.00045f*visibility);
                _settlementRoofs.Multimesh.SetInstanceTransform(count,new Transform3D(roofShape,origin+basis.Z*(height+.00025f*visibility)));
                _settlementRoofs.Multimesh.SetInstanceColor(count,new Color(block.Shade,block.Shade,block.Shade));
                _settlementFootprints[count]=(latitude,longitude,block.Width,block.Depth,latitudeScale);
                if (++count >= SettlementCapacity) break;
            }
            if (count>before) SettlementCityCount++;
            if (count>=SettlementCapacity) break;
        }
        _settlementBuildings.Multimesh.VisibleInstanceCount=count;
        _settlementRoofs.Multimesh.VisibleInstanceCount=count;
    }

    /// <summary>
    /// The townspeople a city's silhouette is sized for: its region's urban population as it is
    /// today, divided equally among the cities of the region. Live, so a region that empties
    /// shrinks its cities; counted again when the map changes hands or a region is cut.
    /// </summary>
    private long SettlementPeople(int index)
    {
        var map=_campaign?.ProvinceMap;
        if (map is null || index < 0 || index >= _cityAnchors.Length) return 0;
        if (!ReferenceEquals(_settlementCountedFor.Map,map) || _settlementCountedFor.Revision != _campaign.MapRevision)
        {
            _settlementCountedFor=(map,_campaign.MapRevision);
            _settlementCitiesInRegion.Clear();
            _settlementShapes.Clear();
            foreach (var anchor in _cityAnchors)
            {
                if (anchor.ProvinceId <= 0 || anchor.ProvinceId > map.ProvinceCount) continue;
                var region=map.ProvinceToState[anchor.ProvinceId];
                _settlementCitiesInRegion[region]=_settlementCitiesInRegion.GetValueOrDefault(region)+1;
            }
        }
        var province=_cityAnchors[index].ProvinceId;
        if (province <= 0 || province > map.ProvinceCount) return 0;
        var mapIndex=map.ProvinceToState[province];
        var state=_campaign.StateByMapIndex(mapIndex);
        var sharing=_settlementCitiesInRegion.GetValueOrDefault(mapIndex);
        return state is null || sharing <= 0 ? 0 : CityLife.Urban(state)/sharing;
    }

    private bool SettlementOnLand(float latitude,float longitude,float width,float depth,float longitudeScale)
    {
        longitude = Mathf.PosMod(longitude + 180f,360f)-180f;
        if (CityCatalog.ProvinceUnder(_campaign.ProvinceMap,latitude,longitude)==0 || TerrainGone(latitude,longitude)) return false;
        for(var corner=0;corner<4;corner++)
        {
            var lat=latitude+((corner&1)==0?-.5f:.5f)*depth;
            var lon=Mathf.PosMod(longitude+((corner&2)==0?-.5f:.5f)*width/longitudeScale+180f,360f)-180f;
            if(CityCatalog.ProvinceUnder(_campaign.ProvinceMap,lat,lon)==0 || TerrainGone(lat,lon))return false;
        }
        return true;
    }

    internal bool SettlementFootprintsOnLand()
    {
        var legacyCount = _settlementBuildings?.Multimesh.VisibleInstanceCount ?? 0;
        for(var i=0;i<legacyCount;i++)
        {var p=_settlementFootprints[i];if(!SettlementOnLand(p.Latitude,p.Longitude,p.Width,p.Depth,p.LongitudeScale))return false;}
        return _cityMiniatures.VisibleFootprintsOnLand(_campaign?.ProvinceMap, _campaign?.DestroyedProvinces);
    }
}
