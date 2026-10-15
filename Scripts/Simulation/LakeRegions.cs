using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Lakes as places on the map rather than paint on top of one.
///
/// A lake used to be a shape drawn over the land it covered. The cells underneath still
/// belonged to whichever land region held the shore, so a province could span half a lake
/// and an army could march straight across Lake Michigan without anything in the game
/// noticing - which is not a difficulty, it is simply wrong.
///
/// A lake big enough to matter is a region of its own now, and no army can set foot in it,
/// because the ground it would be standing on is water.
///
/// And it belongs to nobody (item 39 on the list). It used to go to whichever country held
/// the longest stretch of its shore and was painted in that country's colour - so Lake
/// Victoria was Ugandan ground, the Caspian was a Kazakh province and Baikal read as a
/// patch of Russia. Nobody lives on a lake, nothing is built on one and no army stands on
/// one, so owning it gave nothing and cost the map its water. A lake is the first region in
/// the game with no owner: it is drawn as water, it has no border of its own, and a country
/// whose shore it is keeps the shore.
///
/// Which lakes count is data. `map_data/lakes/` says how large a lake has to be before it
/// becomes a region and what to call the ones worth naming, and the shapes themselves come
/// from the same vector source the map is drawn from - so a mod that adds a lake to the
/// hydrology gets a lake region for free, and a mod that thinks the threshold is wrong
/// changes one number.
/// </summary>
public static class LakeRegions
{
    /// <summary>How much water it takes to be worth a region of its own, in map cells.</summary>
    public static int MinimumCells { get; private set; } = 40;

    /// <summary>
    /// How wide the water has to be before marching round it stops being an afternoon.
    ///
    /// Area alone is the wrong test. A reservoir on a river is long and thin - the Kakhovka
    /// and Kremenchuk reservoirs cover as many cells as a middling lake, but they are a few
    /// kilometres across, and every army that has ever fought on the Dnipro crossed them.
    /// Made impassable they do not read as water, they read as a wall down the middle of
    /// Ukraine, and the cells behind them are stranded by the resolution of the map rather
    /// than by anything on the ground.
    ///
    /// Depth is measured from the shore inwards, so this is the half-width: three cells is
    /// water that stays water for forty kilometres or so, which is Ladoga and Baikal and
    /// Superior, and is not the Dnipro.
    /// </summary>
    public static int MinimumWidthCells { get; private set; } = 3;

    /// <summary>Where lake regions start numbering, clear of the shipped table and its corrections.</summary>
    public static int FirstMapIndex { get; private set; } = 2000;

    private static readonly HashSet<int> LakeStateIndices = new();

    /// <summary>How many lake regions the last pass created, for the scenario import's arithmetic.</summary>
    public static int CreatedCount { get; private set; }

    /// <summary>Whether a region is water. The province layer asks this to mark itself.</summary>
    public static bool IsLakeRegion(int stateMapIndex) => LakeStateIndices.Contains(stateMapIndex);

    /// <summary>
    /// Whether a region is water, asked of the region itself.
    ///
    /// The index list is rebuilt by the pass that draws the lakes, and a caller that runs
    /// before that pass - or after a save is loaded, when nothing has drawn anything - would
    /// be told a lake is dry land. The kind is written on the region and survives both, so
    /// it is the answer that holds; the index list only has to agree with it.
    /// </summary>
    public static bool IsWaterState(ScenarioState state) => state is not null &&
        (string.Equals(state.Kind, "Lake", StringComparison.OrdinalIgnoreCase) || IsLakeRegion(state.MapIndex));

    public static void ResetCatalog()
    {
        MinimumCells = 40;
        MinimumWidthCells = 3;
        FirstMapIndex = 2000;
        foreach (var document in GameDataLoader.DataDocuments("map_data", "lakes"))
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("minimum_cells", out var minimum) &&
                    minimum.TryGetInt32(out var cells) && cells > 0) MinimumCells = cells;
                if (root.TryGetProperty("minimum_width_cells", out var thickness) &&
                    thickness.TryGetInt32(out var across) && across > 0) MinimumWidthCells = across;
                if (root.TryGetProperty("first_map_index", out var first) &&
                    first.TryGetInt32(out var index) && index > 0) FirstMapIndex = index;
            }
    }

    private static bool[] _water;
    private static ulong _waterLakes;
    private static int _waterWidth, _waterHeight;

    /// <summary>
    /// Which cells are lake water, drawn from the hydrology once and kept until the lakes
    /// change: a campaign is built more than once a session, and drawing every lake of
    /// the world onto a sixteen-million-cell raster each time was most of what this pass cost.
    /// Null when there is no hydrology to draw.
    /// </summary>
    private static bool[] LakeWater(int width, int height)
    {
        var lakes = HydrologyCatalog.LakeFingerprint;
        if (_water is not null && _waterLakes == lakes && _waterWidth == width && _waterHeight == height)
            return _water;
        HydrologyRaster hydrology;
        try { hydrology = HydrologyCatalog.BuildVectorRaster(width, height); }
        catch (Exception) { return null; }
        if (hydrology.Pixels.Length != width * height * 2) return null;
        var water = new bool[width * height];
        for (var cell = 0; cell < water.Length; cell++) water[cell] = hydrology.Pixels[cell * 2] > 0;
        (_water, _waterLakes, _waterWidth, _waterHeight) = (water, lakes, width, height);
        return water;
    }

    /// <summary>
    /// Turn every body of water above the threshold into its own region.
    ///
    /// The lake shapes come from the vector hydrology rather than from the raster the
    /// compiler writes, because the compiler writes that raster in the same run: reading
    /// it back would be the map eating its own output, and the game and the compiler would
    /// end up drawing different lakes.
    ///
    /// Idempotent by construction. Which cells are water is decided by the hydrology, not
    /// by what the state map currently says, so a second pass finds the same bodies, gives
    /// them the same names in the same order and reaches the same answer.
    /// </summary>
    public static int Apply(Campaign campaign, ushort[] control, ushort[] states, int width, int height)
    {
        CreatedCount = 0;
        LakeStateIndices.Clear();
        if (campaign is null || control is null || states is null) return 0;

        var water = LakeWater(width, height);
        if (water is null) return 0;

        var cellCount = width * height;
        var visited = new bool[cellCount];
        var component = new List<int>();
        var queue = new Queue<int>();
        var next = FirstMapIndex;

        for (var seed = 0; seed < cellCount; seed++)
        {
            if (visited[seed] || !water[seed]) continue;

            // A body of water, and the land it touches.
            component.Clear();
            queue.Clear();
            visited[seed] = true;
            queue.Enqueue(seed);
            var shore = new Dictionary<ushort, int>();
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                component.Add(cell);
                var y = cell / width;
                var x = cell % width;
                for (var side = 0; side < 4; side++)
                {
                    var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                    var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                    if (ny < 0 || ny >= height) continue;
                    nx = (nx + width) % width;
                    var neighbour = ny * width + nx;
                    if (water[neighbour])
                    {
                        if (visited[neighbour]) continue;
                        visited[neighbour] = true;
                        queue.Enqueue(neighbour);
                        continue;
                    }
                    // Dry land, or open sea. Only land counts as a shore.
                    var owner = control[neighbour];
                    if (owner != 0 && !IsLakeRegion(states[neighbour]))
                        shore[owner] = shore.GetValueOrDefault(owner) + 1;
                }
            }

            // Water with no shore is the sea, whatever the hydrology calls it, and water
            // too small to sail across is a pond somebody can walk round.
            if (component.Count < MinimumCells || shore.Count == 0) continue;
            // And water you can see across is not a barrier either, however far it runs.
            if (WidthOf(component, width, height) < MinimumWidthCells) continue;

            var mapIndex = next++;
            var name = NameFor(component, width, height);
            var state = campaign.States.FirstOrDefault(candidate => candidate.MapIndex == mapIndex);
            if (state is null)
            {
                state = new ScenarioState
                {
                    MapIndex = mapIndex,
                    NationMapIndex = 0,
                    NationId = "",
                    Id = $"lake_{mapIndex}",
                    Name = name,
                    Iso = $"LAKE-{mapIndex}",
                    Kind = "Lake"
                };
                campaign.States.Add(state);
                CreatedCount++;
            }
            else
            {
                state.Name = name;
                state.NationMapIndex = 0;
                state.NationId = "";
            }

            LakeStateIndices.Add(mapIndex);
            foreach (var cell in component)
            {
                states[cell] = (ushort)mapIndex;
                control[cell] = 0;
            }
        }

        return LakeStateIndices.Count;
    }

    /// <summary>
    /// How far it is from the shore to the middle of this water, in cells.
    ///
    /// A wave of cells started at every edge of the body and run inwards; the last cell it
    /// reaches is the furthest point from any shore, and that distance is the half-width.
    /// It is the honest measure for a shape that bends, which a bounding box is not: a
    /// reservoir that winds for three hundred kilometres has a wide box and no width at all.
    /// </summary>
    private static int WidthOf(List<int> component, int width, int height)
    {
        var inside = new HashSet<int>(component);
        var depth = new Dictionary<int, int>(component.Count);
        var wave = new Queue<int>();
        foreach (var cell in component)
        {
            var y = cell / width;
            var x = cell % width;
            var onTheEdge = false;
            for (var side = 0; side < 4 && !onTheEdge; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                // The top and bottom of the map are as good as a shore: nothing is beyond them.
                if (ny < 0 || ny >= height) { onTheEdge = true; break; }
                nx = (nx + width) % width;
                if (!inside.Contains(ny * width + nx)) onTheEdge = true;
            }
            if (!onTheEdge) continue;
            depth[cell] = 1;
            wave.Enqueue(cell);
        }

        var deepest = 0;
        while (wave.Count > 0)
        {
            var cell = wave.Dequeue();
            var here = depth[cell];
            if (here > deepest) deepest = here;
            var y = cell / width;
            var x = cell % width;
            for (var side = 0; side < 4; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                if (ny < 0 || ny >= height) continue;
                nx = (nx + width) % width;
                var next = ny * width + nx;
                if (!inside.Contains(next) || depth.ContainsKey(next)) continue;
                depth[next] = here + 1;
                wave.Enqueue(next);
            }
        }
        return deepest;
    }

    /// <summary>The nearest named lake in the hydrology, so the region is called something.</summary>
    private static string NameFor(List<int> component, int width, int height)
    {
        double sumX = 0, sumY = 0;
        foreach (var cell in component)
        {
            sumX += cell % width;
            sumY += cell / width;
        }
        var longitude = sumX / component.Count / width * 360.0 - 180.0;
        var latitude = 90.0 - sumY / component.Count / height * 180.0;

        var best = "";
        var bestDistance = double.MaxValue;
        foreach (var lake in HydrologyCatalog.LakeFeatures)
        {
            if (lake.Rings.Length == 0 || lake.Rings[0].Length == 0) continue;
            double ringX = 0, ringY = 0;
            foreach (var point in lake.Rings[0])
            {
                ringX += point.Longitude;
                ringY += point.Latitude;
            }
            ringX /= lake.Rings[0].Length;
            ringY /= lake.Rings[0].Length;
            var distance = (ringX - longitude) * (ringX - longitude) + (ringY - latitude) * (ringY - latitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = lake.Name;
        }
        // Two degrees is close enough to be the same water; further away it is some other
        // lake that happens to be nearest, and an unnamed lake is better than a wrong name.
        return bestDistance <= 4.0 && !string.IsNullOrWhiteSpace(best) ? best : "Lake";
    }
}
