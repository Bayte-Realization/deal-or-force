using System;
using System.Collections.Generic;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// The date line is a drawing cut on one world. Shore distance continues across
/// it, a ribbon does not streak the long way round, and an ocean wave meets itself.
/// </summary>
static class DateLineSeamTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("date line: " + label);
        Console.WriteLine("PASS date line: " + label);
    }

    public static void Run()
    {
        var campaign = new Campaign(30);
        campaign.Nations.Add(new Nation { Id = 0, Tag = "DL0", Name = "Seam", Population = 1_000_000 });
        Check(campaign.Nations[0].Id == 0 && campaign.NationById(0) == campaign.Nations[0],
            "nation id 0 stands at index 0");
        var before = CampaignFingerprint.Of(campaign);
        ShoreContinuesAcrossTheCut();
        RibbonBreaksInsteadOfStreaking();
        WaveMeetsItself();
        Check(CampaignFingerprint.Of(campaign) == before, "reading the seam does not change the fingerprint");
        Check(SaveSystem.FormatVersion == 2, "save format stays 2");
    }

    static void ShoreContinuesAcrossTheCut()
    {
        const int width = 16, height = 8;
        var seam = Lake(width, height, 15);
        var mirror = Lake(width, height, 7);
        string mismatch = null;
        for (var y = 0; y < height && mismatch is null; y++)
        for (var x = 0; x < width; x++)
        {
            var here = seam[(y * width + x) * 3];
            var there = mirror[(y * width + (x + 8) % width) * 3];
            if (here == there) continue;
            mismatch = $"({x},{y}) is {here} at the cut and {there} eight columns away";
            break;
        }
        Check(mismatch is null, mismatch ?? "a lake across the date line has the same shore as the same lake away from it");
        Check(seam[(3 * width) * 3] < 128 && seam[(3 * width + 15) * 3] < 128,
            "the two columns that meet at the cut are both still water");
        Check(seam[(3 * width + 8) * 3] >= 128, "the far side of that lake is not painted around the earth");
    }

    static byte[] Lake(int width, int height, int origin)
    {
        var pixels = new byte[width * height * 2];
        for (var y = 2; y <= 5; y++)
        for (var i = 0; i < 7; i++)
        {
            var x = (origin + i) % width;
            pixels[(y * width + x) * 2] = 255;
        }
        return HydrologySurfaceData.Build(new HydrologyRaster(width, height, pixels, 1, 0));
    }

    static void RibbonBreaksInsteadOfStreaking()
    {
        var runs = new List<DateLineSeam.Run>();
        DateLineSeam.Split(new[] { 1f, 2f, 3f, 4f }, 20f, runs);
        Check(runs.Count == 1 && runs[0].Start == 0 && runs[0].Count == 4,
            "a course that stays on the sheet stays one ribbon");
        DateLineSeam.Split(new[] { 8f, 9f, -9f, -8f }, 20f, runs);
        Check(runs.Count == 2 && runs[0].Count == 2 && runs[1].Start == 2 && runs[1].Count == 2,
            "a course across the date line keeps both edges and is not joined the long way");
        DateLineSeam.Split(new[] { 9.5f, -9.5f }, 20f, runs);
        Check(runs.Count == 0, "one point on each edge is not sent out as a two-vertex strip");
    }

    static void WaveMeetsItself()
    {
        const float width = 5760f;
        foreach (var frequency in new[] { 0.0105f, -0.0180f, 0.0072f, 0.002f })
        {
            var wrapped = DateLineSeam.WrappedFrequency(frequency, width);
            var closed = MathF.Abs(MathF.Sin(width * wrapped));
            Check(closed < 0.001f, $"ocean frequency {frequency} meets itself at the date line");
        }
    }
}
