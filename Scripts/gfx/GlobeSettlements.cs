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
    private MultiMeshInstance3D _settlementBuildings, _settlementRoofs;
    private double _settlementClock;
    private readonly (float Latitude,float Longitude,float Width,float Depth,float LongitudeScale)[] _settlementFootprints =
        new (float,float,float,float,float)[SettlementCapacity];
    internal bool? SettlementVerificationEnabled;
    internal int SettlementBuildingCount => _settlementBuildings?.Multimesh.VisibleInstanceCount ?? 0;
    internal int SettlementCityCount { get; private set; }
    internal int SettlementRejectedWater { get; private set; }

    private void CreateSettlementModels()
    {
        MultiMeshInstance3D Layer(string name, Color color) => new()
        {
            Name = name, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
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
        var visibility = SettlementSilhouette.Visibility(_flatMap,_distance,_flatZoom);
        var wanted = _mapApplied && QualityCityLabels && CityLabelsEnabled &&
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
            if (city.Population < 150_000 && !city.IsNationalCapital) continue;
            var anchor=_cityAnchors[index];
            if (TerrainGone(anchor.Latitude,anchor.Longitude)) continue;
            if (!_settlementShapes.TryGetValue(index,out var blocks))
            {
                if (_settlementShapes.Count >= 256) _settlementShapes.Clear();
                _settlementShapes[index]=blocks=SettlementSilhouette.Build(city.Id,city.Population);
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
                var basis=_flatMap ? Basis.Identity : CountryLabelSurfaceBasis(latitude,longitude,1f,0f);
                var origin=_flatMap ? FlatPoint(latitude,longitude,FlatLineZ) : SurfacePoint(latitude,longitude,GlobeRadius*1.0002f);
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
        for(var i=0;i<SettlementBuildingCount;i++)
        {var p=_settlementFootprints[i];if(!SettlementOnLand(p.Latitude,p.Longitude,p.Width,p.Depth,p.LongitudeScale))return false;}
        return true;
    }
}
