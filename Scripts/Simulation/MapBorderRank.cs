using System;

namespace DealOrForce.Simulation;

/// <summary>
/// What kind of line an edge of the map is. Where two coincide the higher one is drawn and the
/// lower one is not: a country border that runs along a province line is a country border.
/// </summary>
public enum MapBorderRank : byte
{
    None = 0,
    Province = 1,
    Region = 2,
    Country = 3
}

/// <summary>
/// Who holds a piece of the map NOW, and what line that puts between two neighbours.
///
/// The map has one picture of the world it was authored with - the country raster, sixteen
/// million cells, never rewritten - and one table of who owns each province today. Every border
/// the player sees has to come out of the second, read through the first only where nothing has
/// changed. This is that rule, once, in a form a test can call: the map shader's `nation_at`,
/// the safe material's `owner_at` and the far plate's painter (<see cref="PoliticalPlate"/>)
/// are the same three lines. A border that a peace, a secession or an annexation drew is then a
/// country border for exactly the reason the borders of the first morning are: the holder on
/// one side is not the holder on the other. Nothing here knows, or can ask, how old a border is.
/// </summary>
public static class MapBorders
{
    /// <summary>The raster's id for water. Not a country: a shore is where a holder meets it.</summary>
    public const ushort Sea = 0;

    /// <summary>
    /// The id land that belongs to nobody is drawn under. Not 0 - that is the sea - so ground
    /// whose owner became nobody stays land, in its own grey, with a border round it.
    /// </summary>
    public const ushort Unowned = 65535;

    /// <summary>The grey of land no flag flies over, and of a country the palette has no colour for.</summary>
    public const uint UnownedColour = 0x6E7B8BFFu;

    /// <summary>
    /// The country a cell belongs to now.
    ///
    /// `authoredHolder` is what the country raster says at that cell, `province` the province the
    /// cell lies in (0 for none), `owners` who owns each province today and `authoredOwners` who
    /// owned it when the raster was drawn. A province whose owner has not moved keeps the
    /// raster's answer, cell by cell, because the raster is finer than the provinces; one whose
    /// owner has moved answers with the new owner over all of its cells.
    /// </summary>
    public static ushort CurrentHolder(ushort authoredHolder, int province, ushort[] owners, ushort[] authoredOwners)
    {
        if (province <= 0 || owners is null || province >= owners.Length) return authoredHolder;
        var owner = owners[province];
        if (authoredOwners is null || province >= authoredOwners.Length || authoredOwners[province] == owner)
            return authoredHolder;
        return owner == 0 ? Unowned : owner;
    }

    /// <summary>
    /// The line between two neighbouring pieces of the map, from who holds them now.
    ///
    /// Different holders: a country border, whatever regions and provinces lie under it, and a
    /// shore counts - the sea is a holder like any other here, as it is in the shader. The same
    /// holder on both sides is never a country border, however the ground between was divided
    /// before: then it is a region line, a province line, or nothing.
    /// </summary>
    public static MapBorderRank Between(ushort holderHere, ushort holderThere,
        int regionHere, int regionThere, int provinceHere, int provinceThere)
    {
        if (holderHere != holderThere) return MapBorderRank.Country;
        if (holderHere == Sea) return MapBorderRank.None;
        if (regionHere != regionThere) return MapBorderRank.Region;
        return provinceHere != provinceThere ? MapBorderRank.Province : MapBorderRank.None;
    }

    /// <summary>The higher of two ranks: what is drawn where two lines lie on the same edge.</summary>
    public static MapBorderRank Higher(MapBorderRank first, MapBorderRank second) =>
        (MapBorderRank)Math.Max((byte)first, (byte)second);
}
