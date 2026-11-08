using System;
using System.Collections.Generic;

namespace DealOrForce;

/// <summary>Ownership-raster layout, independent of Godot rendering and campaign state.
/// Only the largest connected landmass carries the name. The same algorithm runs at
/// full resolution on load and on a reduced ownership snapshot after border changes.</summary>
internal static class TerritoryLabelLayout
{
    internal static Dictionary<int, TerritoryLabelAnchor> BuildTerritoryLabelAnchors(
        ushort[] control, int width, int height, int scale = 1)
    {
        var visited = new System.Collections.BitArray(control.Length);
        var queue = new List<int>(32768);
        var anchors = new Dictionary<int, TerritoryLabelAnchor>();

        for (var start = 0; start < control.Length; start++)
        {
            var nation = control[start];
            if (nation == 0 || visited[start]) continue;
            queue.Clear();
            queue.Add(start);
            visited[start] = true;
            var startX = start % width;
            long sumX = 0;
            long sumY = 0;

            for (var head = 0; head < queue.Count; head++)
            {
                var cell = queue[head];
                var x = cell % width;
                var y = cell / width;
                var dx = x - startX;
                if (dx > width / 2) dx -= width;
                else if (dx < -width / 2) dx += width;
                sumX += startX + dx;
                sumY += y;
                TryAdd(cell - width);
                TryAdd(cell + width);
                TryAdd(y * width + (x == 0 ? width - 1 : x - 1));
                TryAdd(y * width + (x + 1 == width ? 0 : x + 1));
            }

            if (anchors.TryGetValue(nation, out var old) && old.Cells >= queue.Count * scale * scale) continue;
            var meanX = (double)sumX / queue.Count;
            var meanY = (double)sumY / queue.Count;
            // Principal-component orientation produces the familiar strategy-map
            // baseline: Russia and Chile follow their long land axis instead of
            // receiving a detached horizontal nameplate. Sampling caps startup cost
            // for continental countries while retaining the actual component shape.
            var sampleStep = Math.Max(1, queue.Count / 8192);
            double covXX = 0, covXY = 0, covYY = 0;
            var samples = 0;
            for (var index = 0; index < queue.Count; index += sampleStep)
            {
                var cell = queue[index];
                var x = cell % width;
                var y = cell / width;
                var dx = x - startX;
                if (dx > width / 2) dx -= width;
                else if (dx < -width / 2) dx += width;
                var ux = startX + dx;
                var px = ux - meanX;
                var py = y - meanY;
                covXX += px * px;
                covXY += px * py;
                covYY += py * py;
                samples++;
            }
            covXX /= Math.Max(1, samples);
            covXY /= Math.Max(1, samples);
            covYY /= Math.Max(1, samples);
            var angle = 0.5 * Math.Atan2(2.0 * covXY, covXX - covYY);
            // Glyphs have their own tangent frames, so the baseline can follow a
            // peninsula rather than squeezing its name into a northern cross-section.
            // A peninsula can run north-south. Forcing Korea's united outline into
            // a near-horizontal baseline picked a short northern cross-section.
            angle = Math.Clamp(angle, -1.35, 1.35);
            var axisX = (float)Math.Cos(angle);
            var axisY = (float)Math.Sin(angle);
            if (axisX < -0.05f)
            {
                axisX = -axisX;
                axisY = -axisY;
            }

            var minMajor = double.MaxValue;
            var maxMajor = double.MinValue;
            var minMinor = double.MaxValue;
            var maxMinor = double.MinValue;
            for (var index = 0; index < queue.Count; index += sampleStep)
            {
                var cell = queue[index];
                var x = cell % width;
                var y = cell / width;
                var dx = x - startX;
                if (dx > width / 2) dx -= width;
                else if (dx < -width / 2) dx += width;
                var px = startX + dx - meanX;
                var py = y - meanY;
                var major = px * axisX + py * axisY;
                var minor = -px * axisY + py * axisX;
                minMajor = Math.Min(minMajor, major);
                maxMajor = Math.Max(maxMajor, major);
                minMinor = Math.Min(minMinor, minor);
                maxMinor = Math.Max(maxMinor, minor);
            }

            // Where the name will actually fit. The extents above are a bounding box in the
            // principal frame and are used only to bound the search; what comes back is a
            // line that lies inside this country and nowhere else.
            var padMajor = (maxMajor - minMajor) * 0.12;
            var padMinor = (maxMinor - minMinor) * 0.12;
            var chord = FindLabelChord(control, width, height, nation, meanX, meanY, angle,
                minMajor - padMajor, maxMajor + padMajor,
                minMinor - padMinor, maxMinor + padMinor);
            // A bounding-box/centroid fallback can lie in the sea or a neighbour.
            // No readable interior chord is preferable to a detached false label.
            if (chord.Length <= 0 || chord.Thickness <= 0) continue;

            // The largest piece keeps the name, not the last one scanned.
            //
            // This used to assign unconditionally, which is the same thing while a country is
            // in one piece and stops being the same thing the moment one is not: a China that
            // has annexed North America has two pieces, and row-major scanning would have
            // handed the name to whichever of them ended furthest down the raster.
            var effectiveCells = queue.Count * scale * scale;
            if (!anchors.TryGetValue(nation, out var existing) || effectiveCells > existing.Cells)
                anchors[nation] = new TerritoryLabelAnchor(
                    (float)chord.CentreX * scale,
                    (float)chord.CentreY * scale,
                    chord.AxisX,
                    chord.AxisY,
                    Math.Max(3f, chord.Length * 0.5f * scale),
                    Math.Max(2f, chord.Thickness * scale),
                    effectiveCells);

            void TryAdd(int next)
            {
                if (next < 0 || next >= control.Length || visited[next] || control[next] != nation) return;
                visited[next] = true;
                queue.Add(next);
            }
        }
        return anchors;
    }

    /// <summary>
    /// Where a country's name goes, and how much room it actually has.
    ///
    /// <see cref="HalfSpanPixels"/> and <see cref="ThicknessPixels"/> are measured along a
    /// line that lies entirely inside the country - see <see cref="FindLabelChord"/> - rather
    /// than taken from its bounding box. The distinction is the whole of why names used to
    /// run off into the sea and across their neighbours: the bounding box of Norway is most
    /// of Scandinavia, and almost none of it is Norway.
    /// </summary>
    internal readonly record struct TerritoryLabelAnchor(
        float CenterX,
        float CenterY,
        float AxisX,
        float AxisY,
        float HalfSpanPixels,
        float ThicknessPixels,
        int Cells);

    /// <summary>The longest straight run of one country's own ground, and how thick it is there.</summary>
    private readonly record struct LabelChord(
        double CentreX, double CentreY, float AxisX, float AxisY, float Length, float Thickness);

    /// <summary>
    /// The widest place a name will fit without leaving the country.
    ///
    /// A country's name should sit on the country. That sounds too obvious to need code, and
    /// it needed code: the span came from the bounding box of the largest land component, so
    /// Chile's name was as wide as Chile is tall, Norway's crossed Sweden, and Indonesia's
    /// lay mostly on the Java Sea. A bounding box is a statement about extremes, and a
    /// country is the ground between them.
    ///
    /// So the line is searched for instead. Candidate baselines are swept through a range of
    /// angles around the country's principal axis and a range of parallel offsets across it;
    /// each is walked cell by cell, and what is measured is the longest **contiguous** run of
    /// this country's own ground. Contiguity does the work of several special cases at once -
    /// a run cannot jump a strait, so Java and Sumatra cannot share a baseline, and it cannot
    /// cross a border, so nothing lands on a neighbour.
    ///
    /// The best few runs by length are then measured for thickness, because a name also has
    /// height: the longest chord across Chile is a hairline, and a name written on it would
    /// spill into Argentina and the Pacific in the other direction. What comes back is the
    /// line with the best area of text it could carry.
    /// </summary>
    private static LabelChord FindLabelChord(
        ushort[] control, int width, int height, ushort nation,
        double meanX, double meanY, double principalAngle,
        double minMajor, double maxMajor, double minMinor, double maxMinor)
    {
        const int angleSteps = 9;
        const int offsetSteps = 17;
        const double angleSweep = 0.38;
        const int keepBest = 24;

        var majorSpan = Math.Max(4.0, maxMajor - minMajor);
        var minorSpan = Math.Max(2.0, maxMinor - minMinor);

        // Long walks are sampled coarsely. A continental country is thousands of cells
        // across and the answer does not change for looking at every one of them.
        var step = Math.Max(1.0, majorSpan / 520.0);

        Span<double> bestLength = stackalloc double[keepBest];
        Span<double> bestStart = stackalloc double[keepBest];
        Span<double> bestAngle = stackalloc double[keepBest];
        Span<double> bestOffset = stackalloc double[keepBest];
        for (var slot = 0; slot < keepBest; slot++) bestLength[slot] = 0;

        for (var angleIndex = 0; angleIndex < angleSteps; angleIndex++)
        {
            var angle = Math.Clamp(principalAngle +
                        (angleSteps == 1 ? 0 : (angleIndex / (double)(angleSteps - 1) - 0.5) * 2 * angleSweep), -1.5, 1.5);
            var axisX = Math.Cos(angle);
            var axisY = Math.Sin(angle);

            for (var offsetIndex = 0; offsetIndex < offsetSteps; offsetIndex++)
            {
                var offset = minMinor + minorSpan * (offsetIndex + 0.5) / offsetSteps;
                var originX = meanX - axisY * offset;
                var originY = meanY + axisX * offset;

                var runStart = 0.0;
                var runLength = 0.0;
                var currentStart = double.NaN;
                for (var t = minMajor - step; t <= maxMajor + step; t += step)
                {
                    var px = originX + axisX * t;
                    var py = originY + axisY * t;
                    var cellY = (int)Math.Round(py);
                    var inside = false;
                    if (cellY >= 0 && cellY < height)
                    {
                        var cellX = ((int)Math.Round(px) % width + width) % width;
                        inside = control[cellY * width + cellX] == nation;
                    }

                    if (inside)
                    {
                        if (double.IsNaN(currentStart)) currentStart = t;
                        var length = t - currentStart;
                        if (length > runLength)
                        {
                            runLength = length;
                            runStart = currentStart;
                        }
                    }
                    else currentStart = double.NaN;
                }
                if (runLength <= 0) continue;

                // Keep the best few by length; thickness costs another walk and is only
                // worth paying for on candidates that could win.
                for (var slot = 0; slot < keepBest; slot++)
                {
                    if (runLength <= bestLength[slot]) continue;
                    for (var shift = keepBest - 1; shift > slot; shift--)
                    {
                        bestLength[shift] = bestLength[shift - 1];
                        bestStart[shift] = bestStart[shift - 1];
                        bestAngle[shift] = bestAngle[shift - 1];
                        bestOffset[shift] = bestOffset[shift - 1];
                    }
                    bestLength[slot] = runLength;
                    bestStart[slot] = runStart;
                    bestAngle[slot] = angle;
                    bestOffset[slot] = offset;
                    break;
                }
            }
        }

        var chord = new LabelChord(meanX, meanY,
            (float)Math.Cos(principalAngle), (float)Math.Sin(principalAngle),
            0, 0);
        var bestScore = 0.0;

        for (var slot = 0; slot < keepBest && bestLength[slot] > 0; slot++)
        {
            var angle = bestAngle[slot];
            var axisX = Math.Cos(angle);
            var axisY = Math.Sin(angle);
            var offset = bestOffset[slot];
            var originX = meanX - axisY * offset;
            var originY = meanY + axisX * offset;
            var middle = bestStart[slot] + bestLength[slot] * 0.5;
            var centreX = originX + axisX * middle;
            var centreY = originY + axisY * middle;

            // Leave breathing room at both ends, and measure both sides along the
            // whole word. A thick centre alone says nothing about letters at a coast.
            var length = bestLength[slot] * .84;
            var thickness = double.MaxValue;
            for (var sample = -2; sample <= 2; sample++)
            {
                var along = length * sample / 5.0;
                thickness = Math.Min(thickness, Thickness(control, width, height, nation,
                    centreX + axisX * along, centreY + axisY * along, -axisY, axisX,
                    Math.Max(2.0, minorSpan)));
            }
            // Length times height, because what a name needs is area - and a hairline chord
            // across a long thin country is worse than a shorter one with room to breathe.
            var score = length * Math.Min(thickness, length * 0.5);

            // And a thumb on the scale for the horizontal. A country that is genuinely long
            // and thin - Chile, Norway, Vietnam - has a diagonal so much better than its
            // horizontal that this cannot overturn it, which is the point. A roughly square
            // country like France has several near-equal chords, and an atlas picks the flat
            // one every time; without this the winner was whichever diagonal happened to be
            // a few cells longer, and France read as though it were falling over.
            score *= 1.0 - 0.38 * Math.Abs(Math.Sin(angle));
            if (score <= bestScore) continue;
            bestScore = score;
            chord = new LabelChord(centreX, centreY, (float)axisX, (float)axisY,
                (float)length, (float)thickness);
        }

        return chord;
    }

    /// <summary>How much of the country there is above and below a point, across the baseline.</summary>
    private static double Thickness(ushort[] control, int width, int height, ushort nation,
        double centreX, double centreY, double normalX, double normalY, double cap)
    {
        var reach = double.MaxValue;
        for (var side = -1; side <= 1; side += 2)
        {
            var walked = 0.0;
            while (walked < cap)
            {
                walked += 1.0;
                var px = centreX + normalX * walked * side;
                var py = centreY + normalY * walked * side;
                var cellY = (int)Math.Round(py);
                if (cellY < 0 || cellY >= height) { walked -= 1; break; }
                var cellX = ((int)Math.Round(px) % width + width) % width;
                if (control[cellY * width + cellX] != nation) { walked -= 1; break; }
            }
            // Text is centred on its baseline. Adding the two reaches let a wide
            // inland side compensate for no land at all on the coastal side.
            reach = Math.Min(reach, walked);
        }
        return Math.Max(0.0, reach * 2.0);
    }
}
