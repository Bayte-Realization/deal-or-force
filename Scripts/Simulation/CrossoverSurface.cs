using System;
using DealOrForce.Modding;

namespace DealOrForce.Simulation;

/// <summary>Flat top-cell geography, shared by validation, simulation and the globe mask.</summary>
public static class CrossoverSurface
{
    public const int Width = 6144, Height = 3072, Top = 79;
    public const double Radius = 1023.5;

    public static bool ValidFlat(CrossoverVoxel v)
    {
        if (v.surface is not {Length:2} || v.pose is not {Length:7} ||
            !v.terrain || v.town || v.flora) return false;
        int x=v.surface[0],z=v.surface[1];
        if (x<0 || x>=Width || z<0 || z>=Height || v.x!=x/2-1536 ||
            v.y!=Top*2+x%2 || v.z!=z-1536) return false;
        var lon=((x+.5)/Width-.5)*Math.PI*2;
        var lat=(.5-(z+.5)/Height)*Math.PI;
        var dx=v.pose[0]-Radius*Math.Cos(lat)*Math.Cos(lon);
        var dy=v.pose[1]-Radius*Math.Sin(lat);
        var dz=v.pose[2]-Radius*Math.Cos(lat)*Math.Sin(lon);
        return double.IsFinite(dx+dy+dz) && dx*dx+dy*dy+dz*dz<.0004;
    }

    public static double Area(CrossoverVoxel v)
    {
        if (!ValidFlat(v)) return 1;
        // Exact spherical strip area, including the vanishing polar cap.
        var north=(.5-v.surface[1]/(double)Height)*Math.PI;
        var south=(.5-(v.surface[1]+1d)/Height)*Math.PI;
        return 1024d*1024*(2*Math.PI/Width)*(Math.Sin(north)-Math.Sin(south));
    }

    public static int Province(CrossoverVoxel v, ProvinceTopology map)
    {
        int x=(int)((v.surface[0]+.5)*map.Width/Width);
        int y=(int)((v.surface[1]+.5)*map.Height/Height);
        return map.CellToProvince[y*map.Width+x];
    }

    public static void Stamp(byte[] mask, int width, int height, CrossoverVoxel v)
    {
        if (v.block!=0 || !ValidFlat(v)) return;
        var left=v.surface[0]*(double)width/Width;
        var right=(v.surface[0]+1d)*width/Width;
        var top=v.surface[1]*(double)height/Height;
        var bottom=(v.surface[1]+1d)*height/Height;
        // Area coverage keeps even a sub-texel single block visible without
        // moving its damage across borders or losing cells at the date line.
        for(int y=(int)Math.Floor(top);y<Math.Ceiling(bottom)&&y<height;y++)
            for(int x=(int)Math.Floor(left);x<Math.Ceiling(right)&&x<width;x++) {
                var coverage=(Math.Min(x+1d,right)-Math.Max(x,left))*
                    (Math.Min(y+1d,bottom)-Math.Max(y,top));
                var i=y*width+x;
                mask[i]=(byte)Math.Min(255,mask[i]+(int)Math.Round(coverage*255));
            }
    }
}
