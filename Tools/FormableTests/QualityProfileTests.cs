using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The quality ladder, and the governor that walks it.
///
/// The report was a campaign at one and a half frames a second on a machine whose graphics
/// card had fallen off the bus, and the game had no answer to it: the authored picture, at
/// whatever rate the software rasteriser could manage. These checks are about the answer -
/// that the ladder is ordered, that a software adapter starts at the bottom, that a tier the
/// player chose by hand is never moved, and that sustained slow frames really do buy a cheaper
/// picture while a single hitch does not.
/// </summary>
static class QualityProfileTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("quality: " + label);
        Console.WriteLine("PASS quality: " + label);
    }

    public static void Run()
    {
        QualityProfiles.ResetCatalog();
        var ladder = QualityProfiles.Ladder;
        Check(ladder.Count >= 3, "the ladder is data, not a constant (" + ladder.Count + " tiers)");
        var ordered = true;
        for (var index = 1; index < ladder.Count; index++)
            if (ladder[index].RenderScale > ladder[index - 1].RenderScale + 0.0001f) ordered = false;
        Check(ordered, "a cheaper tier never draws more pixels than the one above it");
        Check(ladder[^1].RenderScale < ladder[0].RenderScale, "the last tier is cheaper than the first");

        var software = QualityProfiles.For("auto", "Microsoft Basic Render Driver", "forward_plus");
        Check(software.Id == ladder[^1].Id, "a software rasteriser starts at the bottom");
        var llvmpipe = QualityProfiles.For("auto", "llvmpipe (LLVM 17.0, 256 bits)", "gl_compatibility");
        Check(llvmpipe.Id == ladder[^1].Id, "and so does Mesa's software renderer");
        var nvidia = QualityProfiles.For("auto", "NVIDIA GeForce RTX 3060", "forward_plus");
        Check(nvidia.Id == ladder[0].Id, "a graphics card starts at the top");
        var chosen = QualityProfiles.For("low", "NVIDIA GeForce RTX 3060", "forward_plus");
        Check(chosen.Id == "low", "a tier the player chose wins over the adapter");

        var governor = new FrameRateGovernor(ladder[0], automatic: true, windowFrames: 12);
        QualityProfile step = null;
        for (var frame = 0; frame < 12 * 200 && step is null; frame++) step = governor.Sample(50);
        Check(step is not null && step.Order > ladder[0].Order,
            "sustained fifty-millisecond frames buy a cheaper tier (now " + governor.Profile.Id + ")");

        QualityProfile back = null;
        for (var frame = 0; frame < 12 * 4000 && back is null; frame++) back = governor.Sample(5);
        Check(back is not null, "and a machine that recovers is allowed back up");

        var manual = new FrameRateGovernor(ladder[0], automatic: false, windowFrames: 12);
        QualityProfile moved = null;
        for (var frame = 0; frame < 12 * 4000 && moved is null; frame++) moved = manual.Sample(90);
        Check(moved is null && manual.Profile.Id == ladder[0].Id, "a tier chosen by hand is never moved");
    }
}
