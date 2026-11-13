using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// Every country border looks the same, whatever its origin.
///
/// The bug this pins: a border the scenario opened with and a border a peace, a secession or an
/// annexation drew were painted by different code - a per-province colour overlay close up, a
/// second plate painter from far away - and so never looked alike. The rank of an edge is now
/// one pure rule (MapBorders) applied to who holds the two sides today, the far plate has one
/// painter (PoliticalPlate), and the style of a rank is one set of tokens (MapBorderStyle).
/// The game cannot be launched from the test stand, so what is checked here is everything of
/// that which lives in C#, and the shader sources as text.
/// Run: FormableTests --border-rank-only
/// </summary>
static class BorderRankTests
{
    private static int _passed;

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("border rank: " + label);
        _passed++;
        Console.WriteLine("PASS border rank: " + label);
    }

    public static void Run()
    {
        _passed = 0;
        Ranks();
        HolderMatchesTheShaderTable();
        Plate();
        CatchUp();
        Style();
        ShaderSources();
        Console.WriteLine($"BORDER_RANK passed={_passed} failed=0");
    }

    // ---- the rule ---------------------------------------------------------------------------

    private static void Ranks()
    {
        Check(MapBorders.Between(4, 9, 1, 1, 7, 7) == MapBorderRank.Country,
            "two different owners make a country border even inside one region and one province");
        Check(MapBorders.Between(4, 9, 1, 2, 7, 8) == MapBorderRank.Country &&
              MapBorders.Between(9, 4, 2, 1, 8, 7) == MapBorderRank.Country,
            "a country border is a country border from either side, whatever lies under it");
        Check(MapBorders.Between(4, 4, 1, 2, 7, 8) == MapBorderRank.Region,
            "one owner on both sides of a region line is a region line, never a country border");
        Check(MapBorders.Between(4, 4, 1, 1, 7, 8) == MapBorderRank.Province,
            "one owner and one region on both sides of a province line is a province line");
        Check(MapBorders.Between(4, 4, 1, 1, 7, 7) == MapBorderRank.None, "the same ground is no line at all");
        Check(MapBorders.Between(4, MapBorders.Sea, 1, 0, 7, 0) == MapBorderRank.Country &&
              MapBorders.Between(MapBorders.Sea, MapBorders.Sea, 0, 0, 0, 0) == MapBorderRank.None,
            "a shore ranks as a country border; open water is no line");
        Check(MapBorders.Between(4, MapBorders.Unowned, 1, 1, 7, 8) == MapBorderRank.Country,
            "land that belongs to nobody has a border with land that belongs to somebody");
        Check(MapBorders.Higher(MapBorderRank.Province, MapBorderRank.Country) == MapBorderRank.Country &&
              MapBorders.Higher(MapBorderRank.Region, MapBorderRank.Province) == MapBorderRank.Region,
            "where two ranks coincide the higher one is the one drawn");

        // The same edge, three histories: authored, annexed, handed back. The rule is given only
        // today's owners, so it cannot tell them apart - which is the requirement.
        var authored = new ushort[] { 0, 4, 9 };
        var annexed = new ushort[] { 0, 4, 4 };
        var seceded = new ushort[] { 0, 4, 31 };
        ushort Holder(ushort raster, int province, ushort[] owners) =>
            MapBorders.CurrentHolder(raster, province, owners, authored);
        Check(MapBorders.Between(Holder(4, 1, authored), Holder(9, 2, authored), 1, 2, 1, 2) == MapBorderRank.Country,
            "original ownership: the scenario's own border ranks as a country border");
        Check(MapBorders.Between(Holder(4, 1, annexed), Holder(9, 2, annexed), 1, 2, 1, 2) == MapBorderRank.Region,
            "annexation: the scenario's border between two provinces of one owner is no longer a country border");
        Check(MapBorders.Between(Holder(4, 1, seceded), Holder(9, 2, seceded), 1, 2, 1, 2) == MapBorderRank.Country &&
              Holder(9, 2, seceded) == 31,
            "secession: a country that did not exist at the start has a country border like any other");
        Check(MapBorders.Between(Holder(4, 1, seceded), Holder(4, 1, seceded), 1, 1, 1, 1) == MapBorderRank.None &&
              MapBorders.Between(Holder(9, 2, annexed), Holder(9, 2, annexed), 2, 2, 2, 2) == MapBorderRank.None,
            "the inside of one owner is never a border, moved or not");
    }

    /// <summary>
    /// The shader decides a country border from `nation_at`: the raster's country, unless the
    /// province's entry in the moved-owner table is set. That table is packed by
    /// RasterOverlayKernel. Decoded the way the shader decodes it, it must give exactly
    /// MapBorders.CurrentHolder for every province - or the C# rule tested above is not the rule
    /// the picture is drawn by.
    /// </summary>
    private static void HolderMatchesTheShaderTable()
    {
        const int provinces = 4000;
        var random = new Random(20261113);
        var owners = new ushort[provinces + 1];
        var authored = new ushort[provinces + 1];
        var controllers = new ushort[provinces + 1];
        for (var p = 1; p <= provinces; p++)
        {
            authored[p] = (ushort)random.Next(0, 300);
            owners[p] = random.Next(0, 4) == 0 ? (ushort)random.Next(0, 340) : authored[p];
            controllers[p] = owners[p];
        }
        var palette = new Dictionary<int, uint>();
        for (var i = 1; i < 340; i++) palette[i] = 0x10203000u + (uint)i * 7919u | 0xFFu;
        var control = new byte[256 * 256 * 4];
        var colour = new byte[256 * 256 * 4];
        var ids = new byte[256 * 256 * 4];
        var moved = RasterOverlayKernel.PackProvinceOverlays(provinces, owners, authored, controllers, palette,
            160, MapBorders.Unowned, MapBorders.UnownedColour, control, colour, ids);

        var mismatches = 0;
        var movedProvinces = 0;
        for (var p = 1; p <= provinces; p++)
        {
            var raster = authored[p];
            var entrySet = ids[p * 4 + 2] > 127;
            var shader = entrySet ? (ushort)(ids[p * 4] + ids[p * 4 + 1] * 256) : raster;
            if (entrySet) movedProvinces++;
            if (shader != MapBorders.CurrentHolder(raster, p, owners, authored)) mismatches++;
        }
        Check(moved && movedProvinces > 500, $"the fixture moves owners ({movedProvinces} of {provinces} provinces)");
        Check(mismatches == 0,
            "the owner the shader reads for a province is MapBorders.CurrentHolder, for moved and unmoved alike");
        Check(MapBorders.CurrentHolder(17, 0, owners, authored) == 17 &&
              MapBorders.CurrentHolder(17, provinces + 50, owners, authored) == 17,
            "a cell with no province keeps the raster's country");
        var nobody = (ushort[])authored.Clone();
        authored[1] = 12;
        nobody[1] = 0;
        Check(MapBorders.CurrentHolder(12, 1, nobody, authored) == MapBorders.Unowned,
            "ground whose owner became nobody is held by the unowned id, not by the sea");
    }

    // ---- the far plate ------------------------------------------------------------------------

    // Twelve columns by six rows. Row 0 is water, row 1 is the shore. Three countries of four
    // columns each, two provinces of two columns each:
    //   country 1: provinces 1 | 2, in two regions (a region line between columns 1 and 2)
    //   country 2: provinces 3 | 4, in one region  (only a province edge between columns 5 and 6)
    //   country 3: provinces 5 | 6, in one region
    // East and west wrap, so country 3 also borders country 1.
    private const int Width = 12, Height = 6;
    private const ushort A = 1, B = 2, C = 3, Newborn = 77;
    private static readonly MapPlateShades Shades = new(0.34f, 0.70f, 0.92f);
    private static readonly Dictionary<int, uint> Colours = new()
    {
        [A] = 0xC86432FFu, [B] = 0x3264C8FFu, [C] = 0x64C832FFu, [Newborn] = 0xC832C8FFu
    };

    private static PoliticalPlateSource World(ushort[] ownerOfProvince)
    {
        var source = new PoliticalPlateSource
        {
            Width = Width, Height = Height,
            Provinces = new ushort[Width * Height],
            Holders = new ushort[Width * Height],
            Geography = new byte[Width * Height]
        };
        for (var y = 1; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var cell = y * Width + x;
                var province = (ushort)(x / 2 + 1);
                source.Provinces[cell] = province;
                source.Holders[cell] = ownerOfProvince[province];
                // What the map builder records: a region line wherever the authored regions
                // differ, which includes every authored country border.
                if (x is 1 or 2 || x % 4 is 0 or 3) source.Geography[cell] |= PoliticalPlate.RegionLine;
                if (y == 1) source.Geography[cell] |= PoliticalPlate.Coast;
            }
        return source;
    }

    private static uint Pixel(byte[] plate, int x, int y)
    {
        var at = (y * Width + x) * 4;
        return (uint)(plate[at] << 24 | plate[at + 1] << 16 | plate[at + 2] << 8 | plate[at + 3]);
    }

    private static uint Paint(uint colour, float shade) =>
        (uint)((byte)(((colour >> 24) & 0xFF) * shade) << 24 | (byte)(((colour >> 16) & 0xFF) * shade) << 16 |
               (byte)(((colour >> 8) & 0xFF) * shade) << 8 | 0xFF);

    private static void Plate()
    {
        var authored = new ushort[] { 0, A, A, B, B, C, C };
        var source = World(authored);
        var opening = PoliticalPlate.Paint(source, authored, authored, Colours, Shades);
        const int row = 3;

        Check(Pixel(opening, 3, row) == Paint(Colours[A], Shades.Country) &&
              Pixel(opening, 4, row) == Paint(Colours[B], Shades.Country) &&
              Pixel(opening, 7, row) == Paint(Colours[B], Shades.Country) &&
              Pixel(opening, 8, row) == Paint(Colours[C], Shades.Country),
            "opening plate: both sides of each scenario border are painted at the country shade");
        Check(Pixel(opening, 11, row) == Paint(Colours[C], Shades.Country) &&
              Pixel(opening, 0, row) == Paint(Colours[A], Shades.Country),
            "the border across the date line is a border too");
        Check(Pixel(opening, 1, row) == Paint(Colours[A], Shades.Region) &&
              Pixel(opening, 2, row) == Paint(Colours[A], Shades.Region),
            "a region line inside one country is painted at the region shade, never the country one");
        Check(Pixel(opening, 5, row) == Paint(Colours[B], Shades.Inland) &&
              Pixel(opening, 6, row) == Paint(Colours[B], Shades.Inland),
            "a province edge inside one country and one region is not shaded as a border at all");
        Check(Enumerable.Range(0, Width).All(x => Pixel(opening, x, 0) == 0u) &&
              Enumerable.Range(0, Width).All(x => (Pixel(opening, x, 1) & 0xFF) == 0xFF),
            "water stays transparent for the shader's own sea; land is opaque");
        Check(Pixel(opening, 5, 1) == Paint(Colours[B], Shades.Country),
            "a shore is painted at the country shade");

        // A peace: country 1 annexes province 3. The new border runs between columns 5 and 6,
        // where the scenario had only a province edge.
        var annexed = new ushort[] { 0, A, A, A, B, C, C };
        var afterPeace = PoliticalPlate.Paint(source, annexed, authored, Colours, Shades);
        Check(Pixel(afterPeace, 5, row) == Pixel(opening, 3, row) && Pixel(afterPeace, 6, row) == Pixel(opening, 4, row),
            "changed ownership: a border a peace drew is painted byte for byte like the scenario's own border");
        Check(Pixel(afterPeace, 3, row) == Paint(Colours[A], Shades.Region) &&
              Pixel(afterPeace, 4, row) == Paint(Colours[A], Shades.Region),
            "the scenario's border, now inside one country, keeps only the region shade");
        Check(Pixel(afterPeace, 5, 1) == Paint(Colours[A], Shades.Country) &&
              Pixel(afterPeace, 1, 1) == Pixel(opening, 1, 1),
            "shores keep the country shade after a repaint, on moved land and on untouched land");

        // Origin does not matter: the same world authored into the raster, with nothing moved,
        // is the same picture as the world reached by moving an owner.
        var bakedIn = World(annexed);
        Check(PoliticalPlate.Paint(bakedIn, annexed, annexed, Colours, Shades).AsSpan().SequenceEqual(afterPeace),
            "a world reached by treaty and the same world authored from the start are the same plate");

        // History does not matter: the province is handed back.
        Check(PoliticalPlate.Paint(source, authored, authored, Colours, Shades).AsSpan().SequenceEqual(opening) &&
              PoliticalPlate.Paint(source, (ushort[])authored.Clone(), authored, Colours, Shades).AsSpan().SequenceEqual(opening),
            "owners returned to where they started give back the opening plate exactly");

        // A country that did not exist at the start takes province 6; province 5 becomes nobody's.
        var released = new ushort[] { 0, A, A, B, B, 0, Newborn };
        var afterRelease = PoliticalPlate.Paint(source, released, authored, Colours, Shades);
        Check(Pixel(afterRelease, 10, row) == Paint(Colours[Newborn], Shades.Country) &&
              Pixel(afterRelease, 11, row) == Paint(Colours[Newborn], Shades.Country) &&
              Pixel(afterRelease, 0, row) == Paint(Colours[A], Shades.Country),
            "a released country is outlined at the country shade on every side, in its own colour");
        Check(Pixel(afterRelease, 9, row) == Paint(MapBorders.UnownedColour, Shades.Country) &&
              Pixel(afterRelease, 8, row) == Paint(MapBorders.UnownedColour, Shades.Country) &&
              Pixel(afterRelease, 7, row) == Paint(Colours[B], Shades.Country),
            "land that became nobody's is grey land with a border, not sea and not its old owner");

        // No cell of one owner that touches only its own owner is ever at the country shade.
        var countryShadeInside = 0;
        foreach (var owners in new[] { authored, annexed, released })
        {
            var holders = PoliticalPlate.Holders(source, owners, authored);
            for (var y = 2; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    var here = holders[y * Width + x];
                    var alone = holders[y * Width + (x + 1) % Width] == here &&
                                holders[y * Width + (x + Width - 1) % Width] == here &&
                                holders[(y - 1) * Width + x] == here &&
                                (y + 1 == Height || holders[(y + 1) * Width + x] == here);
                    if (alone && PoliticalPlate.RankAt(source, holders, x, y) == MapBorderRank.Country) countryShadeInside++;
                }
        }
        Check(countryShadeInside == 0, "an internal edge of one owner is never ranked a country border on the plate");
    }

    private static void CatchUp()
    {
        var authored = new ushort[] { 0, A, A, B, B, C, C };
        var source = World(authored);
        var opening = PoliticalPlate.Paint(source, authored, authored, Colours, Shades);
        var pixels = new byte[256 * 256 * 4];

        Check(!PoliticalPlate.PackCatchUp(6, authored, authored, Colours, Shades.Inland, pixels) &&
              pixels.All(value => value == 0),
            "a plate that shows today's owners leaves the catch-up table empty");

        var taken = new ushort[] { 0, B, A, B, B, C, 0 };
        Check(PoliticalPlate.PackCatchUp(6, taken, authored, Colours, Shades.Inland, pixels),
            "a province whose owner moved since the plate was painted is waiting");
        uint Entry(int province) => (uint)(pixels[province * 4] << 24 | pixels[province * 4 + 1] << 16 |
                                           pixels[province * 4 + 2] << 8 | pixels[province * 4 + 3]);
        Check(Entry(1) == Pixel(opening, 5, 3),
            "the waiting province is painted exactly what the plate paints its new owner's inland ground");
        Check(Entry(6) == Paint(MapBorders.UnownedColour, Shades.Inland), "ground that became nobody's waits in grey");
        Check(new[] { 2, 3, 4, 5 }.All(province => Entry(province) == 0u), "provinces that did not move are not in the table");

        Check(!PoliticalPlate.PackCatchUp(6, taken, (ushort[])taken.Clone(), Colours, Shades.Inland, pixels) &&
              pixels.All(value => value == 0),
            "once the repainted plate is on screen the table is empty again: moved land is drawn like any other");
    }

    // ---- the style ----------------------------------------------------------------------------

    private static bool Near(float a, float b, float tolerance = 0.004f) => MathF.Abs(a - b) <= tolerance;

    private static bool Near(LinearColour colour, float r, float g, float b) =>
        Near(colour.R, r) && Near(colour.G, g) && Near(colour.B, b);

    private static void Style()
    {
        InterfaceSkin.ResetCatalog();
        var globe = MapBorderStyles.For(false);
        var flat = MapBorderStyles.For(true);

        foreach (var token in new[] { "map.border_country", "map.border_region", "map.border_province",
                     "map.border_province_halo", "map.border_occupation", "map.border_occupation_key" })
            Check(InterfaceSkin.Has(token) && !InterfaceSkin.Overridden.Contains(token),
                "the shipped skin and the compiled defaults agree on " + token);
        foreach (var (token, compiled) in new[] {
                     ("map.country_border_width", MapBorderStyles.CountryWidth),
                     ("map.country_border_opacity", MapBorderStyles.CountryOpacity),
                     ("map.country_border_far_weight", MapBorderStyles.CountryFarWeightDefault),
                     ("map.region_border_width", MapBorderStyles.RegionWidth),
                     ("map.region_border_opacity", MapBorderStyles.RegionOpacity),
                     ("map.plate_country_shade", MapBorderStyles.PlateCountryShade),
                     ("map.plate_region_shade", MapBorderStyles.PlateRegionShade),
                     ("map.plate_inland_shade", MapBorderStyles.PlateInlandShade) })
            Check(Near(InterfaceSkin.Metric(token, float.NaN), compiled, 0.00001f),
                "game/gui/77_map_borders.json ships " + token + " at the compiled value");

        // The values the shader used to spell as literals. The skin is sRGB hex, so each comes
        // back within a quantisation step of the linear number it replaces.
        Check(Near(globe.Country.Colour, 0.035f, 0.05f, 0.075f) && Near(globe.Country.Opacity, 0.86f) &&
              Near(globe.Country.Width, 1.35f) && Near(globe.CountryFarWeight, 0.62f),
            "country border: the same ink, weight and width the map has always drawn");
        Check(Near(globe.Province.Colour, 0.055f, 0.075f, 0.09f) && Near(globe.ProvinceHalo, 0.78f, 0.80f, 0.77f) &&
              Near(globe.Province.Width, 2.0f) && Near(globe.Province.Opacity, 0.85f) && Near(globe.ProvinceHaloOpacity, 0.15f),
            "province line on the globe: unchanged, at the weight game/gui/24_workshop.json ships");
        Check(Near(flat.Province.Width, 1.1f) && Near(flat.Province.Opacity, 0.52f) && Near(flat.ProvinceHaloOpacity, 0.025f),
            "province line on the flat map: the finer line game/gui/38_flat_map.json asks for");
        Check(Near(globe.Region.Colour, 0f, 0f, 0f) && Near(globe.Region.Opacity, 0.13f * 0.32f, 0.0001f) &&
              Near(globe.Region.Width, 1.35f * 2.8f, 0.0001f),
            "region line: the shade it was, now a named colour, opacity and reach");
        Check(Near(globe.Occupation, 0.92f, 0.88f, 0.71f) && Near(globe.OccupationKey, 0.045f, 0.065f, 0.08f),
            "occupation contour: unchanged, and distinct from every rank");
        Check(globe.Country == flat.Country && globe.Region == flat.Region &&
              globe.Province.Colour == flat.Province.Colour && globe.ProvinceHalo == flat.ProvinceHalo &&
              globe.Occupation == flat.Occupation && globe.Plate == flat.Plate,
            "the globe and the flat map draw a rank in one colour; only widths may differ");
        Check(globe.Plate.Of(MapBorderRank.Country) < globe.Plate.Of(MapBorderRank.Region) &&
              globe.Plate.Of(MapBorderRank.Region) < globe.Plate.Of(MapBorderRank.Province) &&
              globe.Plate.Of(MapBorderRank.Province) == globe.Plate.Of(MapBorderRank.None),
            "far plate: country darker than region darker than inland; a province line is not shaded");

        // A mod's skin wins key by key, and cannot turn a border into a pale seam.
        var errors = new List<string>();
        InterfaceSkin.Merge("{ \"colours\": { \"map.border_country\": \"#ff0000\" }, \"metrics\": { " +
                            "\"map.country_border_width\": 2.5, \"map.flat_country_border_width\": 1.5, " +
                            "\"map.plate_country_shade\": 0.99, \"map.plate_region_shade\": 0.5 } }", "test", errors);
        var modded = MapBorderStyles.For(false);
        var moddedFlat = MapBorderStyles.For(true);
        Check(errors.Count == 0 && Near(modded.Country.Colour, 1f, 0f, 0f) && Near(modded.Country.Width, 2.5f) &&
              Near(moddedFlat.Country.Width, 1.5f) && moddedFlat.Country.Colour == modded.Country.Colour,
            "a later skin file overrides a rank's colour and width, with a flat-map width of its own");
        Check(Near(modded.Plate.Region, 0.5f) && modded.Plate.Country <= modded.Plate.Region &&
              modded.Plate.Region <= modded.Plate.Inland,
            "plate shades are kept in order however a mod writes them");
        InterfaceSkin.ResetCatalog();
        Check(MapBorderStyles.For(false) == globe, "resetting the skin restores the shipped style");

        Check(Near(LinearColour.FromHex("#fff", new LinearColour(0, 0, 0)), 1f, 1f, 1f) &&
              LinearColour.FromHex("not a colour", new LinearColour(0.1f, 0.2f, 0.3f)) == new LinearColour(0.1f, 0.2f, 0.3f) &&
              Near(LinearColour.FromHex("#808080ff", default).R, 0.2158f, 0.0005f),
            "skin hex is read as sRGB and handed to the shader as linear light");
    }

    // ---- the shaders, as text -----------------------------------------------------------------

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static bool Balanced(string shader)
    {
        var code = Regex.Replace(shader, @"//[^\n]*", "");
        int round = 0, curly = 0, square = 0;
        foreach (var c in code)
        {
            if (c == '(') round++; else if (c == ')') round--;
            else if (c == '{') curly++; else if (c == '}') curly--;
            else if (c == '[') square++; else if (c == ']') square--;
            if (round < 0 || curly < 0 || square < 0) return false;
        }
        return round == 0 && curly == 0 && square == 0;
    }

    private static void ShaderSources()
    {
        var scripts = Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts");
        var globe = File.ReadAllText(Path.Combine(scripts, "gfx", "GlobePresentation.cs"));
        var start = globe.IndexOf("Code = @\"shader_type spatial;", StringComparison.Ordinal) + "Code = @\"".Length;
        var end = globe.IndexOf("\n}\"", start, StringComparison.Ordinal);
        Check(start > 20 && end > start, "the full map shader is located in GlobePresentation.cs");
        var full = globe[start..(end + 2)];
        var safe = CompatibilityMapShader.Source;

        Check(full.Contains("float has_nation_border = step(0.5, abs(near_nation - other_nation)) * step(0.5, near_nation);"),
            "full map: a country border is where the holder under the pixel differs from its neighbour's, and nothing else");
        var nationAt = full[full.IndexOf("float nation_at(vec2 cell, vec2 size) {", StringComparison.Ordinal)..];
        nationAt = nationAt[..nationAt.IndexOf("\n}", StringComparison.Ordinal)];
        Check(nationAt.Contains("id_at(nation_ids, cell, size)") && nationAt.Contains("texture(province_owner_ids,") &&
              nationAt.Contains("moved.b > 0.5"),
            "full map: the holder is the raster's country unless the province's owner has moved (MapBorders.CurrentHolder)");
        Check(full.Contains("nation_coverage(map_uv, smooth_edges, near_nation, other_nation, nation_reach)"),
            "full map: the border's two sides are read through nation_at, not from a baked border layer");

        Check(!full.Contains("owner_shade") && !full.Contains("owner_paint") && !full.Contains("owner_color("),
            "full map: the per-province owner colour overlay no longer repaints moved land close up");
        var patch = full.IndexOf("political_smooth.rgb = mix(political_smooth.rgb, plate_patch.rgb, plate_patch.a);", StringComparison.Ordinal);
        var mixed = full.IndexOf("vec4 political = mix(political_smooth, political_crisp, detail_mix);", StringComparison.Ordinal);
        var saturated = full.IndexOf("political.rgb, political_saturation);", StringComparison.Ordinal);
        Check(patch > 0 && mixed > patch && saturated > mixed && Count(full, "plate_catch_up(") == 2,
            "full map: the catch-up table touches the far plate sample alone, before the mix and the saturation");
        Check(Count(full, "political.rgb = mix(political.rgb, ") == 3,
            "full map: after the mix only occupation, the map modes and the resource view repaint the ground");

        Check(full.Contains("base = mix(base, country_border_color,") &&
              full.Contains("country_edge * max(detail_mix, country_border_far_weight) * political.a * country_border_opacity);") &&
              Count(full, "country_border_color") == 2,
            "full map: one ink, one opacity and one far weight for every country border");
        Check(full.Contains("base = mix(base, province_border_color, province_line * province_detail * political.a * province_border_opacity);") &&
              full.Contains("base = mix(base, province_halo_color, province_shoulder * province_detail * political.a * province_border_halo);") &&
              full.Contains("base = mix(base, region_border_color, region_cushion * detail_mix * political.a * region_border_opacity);"),
            "full map: province and region lines are inked from the same style table");
        foreach (var literal in new[] { "vec3(0.035, 0.05, 0.075)", "vec3(0.055, 0.075, 0.09)", "vec3(0.78, 0.80, 0.77)",
                     "vec3(0.92, 0.88, 0.71)", "vec3(0.045, 0.065, 0.08)" })
            Check(Count(full, literal) == 1, "full map: " + literal + " survives only as a uniform's default");

        Check(full.Contains("float province_line = province_edge * (1.0 - on_country_border);") &&
              full.Contains("float province_shoulder = province_halo * (1.0 - on_country_border);") &&
              full.Contains("(1.0 - has_nation_border) * (1.0 - step(0.5, terrain_mode));"),
            "full map: under a country border neither a province line nor a region shade is drawn");
        Check(Count(full, "(1.0 - map_mode) * (1.0 - on_country_border);") == 2,
            "full map: occupation and lease contours give way to a country border on the same edge");
        Check(full.Contains("province_edge * province_selected * province_detail"),
            "full map: the selected province keeps its whole outline, frontier side included");

        var style = File.ReadAllText(Path.Combine(scripts, "gfx", "GlobeBorderStyle.cs"));
        var uniforms = Regex.Matches(style, "SetShaderParameter\\(\"(\\w+)\"").Select(match => match.Groups[1].Value).ToList();
        Check(uniforms.Count == 14 && uniforms.Distinct().Count() == 14, "fourteen style uniforms are set, each once");
        foreach (var uniform in uniforms)
            Check(Regex.IsMatch(full, @"uniform\s+(float|vec3)\s+" + uniform + @"\s*="),
                "full map declares the style uniform " + uniform);
        foreach (var uniform in new[] { "country_border_color", "country_border_opacity", "province_border_color", "province_border_opacity" })
            Check(uniforms.Contains(uniform) && Regex.IsMatch(safe, @"uniform\s+(float|vec3)\s+" + uniform + @"\s*="),
                "safe map declares the same style uniform " + uniform);

        Check(safe.Contains("vec3 land = mix(paint.rgb, owner_.rgb, owner_.a);") && !safe.Contains("owner_.rgb * 0.82"),
            "safe map: moved land is painted by the plate like all other land; the table only bridges a stale plate");
        Check(safe.Contains("step(0.5, abs(owner_id - owner_at(at + dx)))") && safe.Contains("float owner_at(vec2 at)") &&
              safe.Contains("texture(province_owner_ids, lookup(province_))"),
            "safe map: a country border is where the current owner differs from a neighbour's");
        Check(safe.Contains("land = mix(land, country_border_color, national * country_border_opacity);") &&
              safe.Contains("provincial * (1.0 - national) * province_detail"),
            "safe map: one ink for every country border, and the higher rank drawn alone");

        Check(Balanced(full.Replace("\" + OccupationShaderFunctions + @\"", "").Replace("\" + SeamSamplingFunctions() + @\"", "")) &&
              Balanced(safe),
            "both shader sources keep balanced brackets after the edit");
        var packed = PackedMapShader.Build(MapColorSpaceShader.Build(full));
        Check(Regex.Matches(packed, @"uniform\s+sampler\w+\s+").Count == PackedMapShader.TextureUnits &&
              packed.Contains("vec4 plate_patch = plate_catch_up(province);") &&
              packed.Contains("atlas_color_sample(packed_lookup(1.0,") && packed.Contains("packed_lookup(2.0,") &&
              !packed.Contains("texture(province_owner_colors,"),
            "the packed Compatibility build carries the same border logic in the same twelve texture units");

        var plate = File.ReadAllText(Path.Combine(scripts, "gfx", "GlobePlateRepaint.cs"));
        Check(globe.Contains("Simulation.PoliticalPlate.Paint(plateSource, plateOwners, plateOwners, colors, shades)") &&
              plate.Contains("Simulation.PoliticalPlate.Paint(source, owners, authoredOwners, colors, shades)") &&
              !globe.Contains("0.34f") && !plate.Contains("0.34f"),
            "the opening plate and every repaint are painted by the one painter, with no second set of shades");
        Check(Regex.IsMatch(globe, @"SetShaderParameter\(""owners_moved"".*\r?\n(\s*//.*\r?\n)*\s*RequestPlateRepaint\(topology\);"),
            "the plate is asked for on every map refresh, not only when owners differ from the authored world");
    }
}
