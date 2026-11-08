using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Homelands that move with history: integration after a generation, fading after a lifetime (HomelandIntegration).</summary>
internal static class HomelandIntegrationTests
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL homelands: " + message);
        Console.WriteLine("PASS homelands: " + message);
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);

    /// <summary>The first day on or after <paramref name="from"/> that is this region's own day.</summary>
    static int ItsDay(int mapIndex, int from)
    {
        var day = from;
        while ((day + (mapIndex % HomelandIntegration.Cycle + HomelandIntegration.Cycle) % HomelandIntegration.Cycle) % HomelandIntegration.Cycle != 0) day++;
        return day;
    }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20241108);
        MovementFall.RememberHomelands(c); // what the first deployment does on a mapped world
        var russia = c.NationByTag("RUS");
        var kazakhstan = c.NationByTag("KAZ");
        var region = c.StatesOf(russia).Where(s => s.ParentMapIndex == 0 && s.Population > 100_000)
            .OrderBy(s => s.MapIndex).First();
        Check(TerritorialClaims.TitleOf(c, russia, region) == TerritorialTitle.Homeland &&
              TerritorialClaims.TitleOf(c, kazakhstan, region) != TerritorialTitle.Homeland,
            "a Russian region opens as Russia's homeland and not Kazakhstan's");

        var day = ItsDay(region.MapIndex, 30);
        SetDay(c, day);
        HomelandIntegration.Tick(c);
        Check(region.TrackedOwnerMapIndex == russia.MapIndex && region.OwnerSinceDay == -1,
            "the first look is not a change of hands: held since before the campaign");

        region.NationMapIndex = kazakhstan.MapIndex;
        region.NationId = kazakhstan.Tag;
        c.InvalidateStateIndex();
        var handed = ItsDay(region.MapIndex, day + 1);
        SetDay(c, handed);
        HomelandIntegration.Tick(c);
        Check(region.OwnerSinceDay == handed && region.TrackedOwnerMapIndex == kazakhstan.MapIndex,
            "a change of hands starts the clock on the day it is noticed");

        c.Defines.HomelandIntegrationOwnShare = 0f; // whoever lives there: the clock is under test
        c.Defines.HomelandFadeOwnShare = 1.01f;
        var before = CampaignFingerprint.Of(c);
        SetDay(c, ItsDay(region.MapIndex, handed + 25 * 365 - 40));
        HomelandIntegration.Tick(c);
        Check(TerritorialClaims.TitleOf(c, kazakhstan, region) != TerritorialTitle.Homeland && c.HomelandChanges == 0,
            "short of a generation it is not yet home");
        SetDay(c, ItsDay(region.MapIndex, handed + 25 * 365));
        HomelandIntegration.Tick(c);
        Check(TerritorialClaims.TitleOf(c, kazakhstan, region) == TerritorialTitle.Homeland && c.HomelandChanges == 1,
            "after a generation under one flag it becomes the holder's homeland");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "homeland_integrated" && e.ActorId == kazakhstan.Id),
            "and the chronicle says so");
        Check(CampaignFingerprint.Of(c) != before, "a homeland that moved enters the fingerprint");
        Check(TerritorialClaims.TitleOf(c, russia, region) == TerritorialTitle.Homeland,
            "the loser's memory does not fade in a generation");

        SetDay(c, ItsDay(region.MapIndex, handed + 60 * 365));
        HomelandIntegration.Tick(c);
        Check(TerritorialClaims.TitleOf(c, russia, region) == TerritorialTitle.Claim,
            "after a lifetime with hardly any of its people left, Russia's homeland fades to a claim");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "homeland_faded" && e.ActorId == russia.Id && e.TargetId == kazakhstan.Id),
            "the fading is told too");

        var other = c.StatesOf(russia).Where(s => s.MapIndex != region.MapIndex && s.ParentMapIndex == 0)
            .OrderBy(s => s.MapIndex).First();
        russia.RenouncedTerritoryOrigins.Clear();
        kazakhstan.RenouncedTerritoryOrigins.Add(TerritorialClaims.Origin(other));
        SetDay(c, ItsDay(other.MapIndex, c.ElapsedDays + 1));
        HomelandIntegration.Tick(c);
        other.NationMapIndex = kazakhstan.MapIndex; other.NationId = kazakhstan.Tag; c.InvalidateStateIndex();
        var otherHanded = ItsDay(other.MapIndex, c.ElapsedDays + 1);
        SetDay(c, otherHanded); HomelandIntegration.Tick(c);
        SetDay(c, ItsDay(other.MapIndex, otherHanded + 30 * 365)); HomelandIntegration.Tick(c);
        Check(TerritorialClaims.TitleOf(c, kazakhstan, other) != TerritorialTitle.Homeland,
            "a region the holder renounced is never integrated");

        var off = Campaign.CreateBootstrap(20241108);
        off.Defines.HomelandIntegrationYears = 0;
        off.Defines.HomelandFadeYears = 0;
        HomelandIntegration.Tick(off);
        Check(off.States.All(s => s.TrackedOwnerMapIndex == 0), "both at zero, the system does nothing at all");
    }
}
