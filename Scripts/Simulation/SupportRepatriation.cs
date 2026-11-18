namespace DealOrForce.Simulation;

/// <summary>
/// What happens to a long-range formation when the ground under it is lost - for every country,
/// human and computer alike, every step, before anything can project from that ground.
///
/// It used to be a jump: the formation was put down the same hour in the nearest region of its
/// own with a free post, over a front, across a sea or out of an enclave - or, with none free,
/// parked off the map with no base at all, doing nothing, for years (3,257 formation-days of it
/// in one five-year run). Now it is a withdrawal (LongRangeMovement.Withdraw): the overrun costs
/// it part of its strength once, and it leaves by road or by air like any other move; with no
/// way out it is cut off where it stands, on the map, and looks for a way out every day.
/// </summary>
public static class SupportRepatriation
{
    public static int Reconcile(Campaign c)
    {
        if (c?.ProvinceMap is null || c.ProvinceController is null) return 0;
        if (!MechanicFeatures.SystemEnabled(c, nameof(SupportCore))) return 0;
        // Old saves' formations get their position first: a base becomes where they stand.
        LongRangeMovement.Settle(c);
        var changed = LongRangeMovement.Withdraw(c);
        if (changed > 0) c.MarkStrategicChange();
        return changed;
    }
}
