using System;
using System.Linq;
using DealOrForce.Simulation;

static class RadicalismTests
{
    static void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); }
    public static void Run(Campaign c)
    {
        var n = c.NationByTag("KOR"); var s = c.StatesOf(n)[0];
        c.Wars.Clear(); n.Inflation = InflationCore.Weights.Comfort;
        var own = EconomySystem.ForeignShare(s, n.Tag);
        s.StandardOfLiving = .65f; s.Radicalism = 1;
        var initial = RadicalismCore.WeeklyChange(c, n, s, false, 0, 0);
        Check(initial < 0, "100% radicalism can recover without a one-off event");
        for (var week = 0; week < 156; week++) s.Radicalism += RadicalismCore.WeeklyChange(c, n, s, false, 0, 0);
        Check(s.Radicalism < .35f, "three peaceful years unwind high radicalism gradually");
        s.StandardOfLiving = .15f; s.Radicalism = 0;
        Check(RadicalismCore.WeeklyChange(c, n, s, false, 0, 0) > 0, "poor living conditions still raise radicalism");
        for (var week = 0; week < 2600; week++) s.Radicalism += RadicalismCore.WeeklyChange(c, n, s, false, 0, 0);
        var lowLivingEquilibrium = s.Radicalism;
        Check(s.Radicalism > .05f && s.Radicalism < .8f, "fifty years of fixed hardship approach an equilibrium, not automatic saturation");
        s.StandardOfLiving = .8f;
        for (var week = 0; week < 156; week++) s.Radicalism += RadicalismCore.WeeklyChange(c, n, s, false, 0, 0);
        Check(s.Radicalism < lowLivingEquilibrium, "improved conditions reverse an established trend");
        s.Radicalism = .5f;
        var calm = RadicalismCore.WeeklyChange(c, n, s, false, 0, 0);
        var war = RadicalismCore.WeeklyChange(c, n, s, true, 0, 0);
        Check(war > calm, "war slows recovery");
        Check(RadicalismCore.WeeklyChange(c, n, s, false, -.0003f, -.00015f) < calm, "welfare and cohesion help through normal shared modifiers");
        n.Inflation = .3f;
        Check(RadicalismCore.WeeklyChange(c, n, s, false, 0, 0) > calm, "inflation still adds pressure exactly once");
        var drivers = RadicalismCore.Forecast(c, n, s);
        Check(Math.Abs(drivers.Entries.Sum(x => x.Value) - drivers.Total) < .000001f, "forecast components sum to the clamped weekly change");
        foreach (var region in c.StatesOf(n)) region.Radicalism = .5f;
        n.Stability = .9f; n.Treasury = n.Gdp;
        var welfare = n.WelfareSpending;
        AiStatecraft.GovernTheBudget(c, n);
        Check(n.WelfareSpending > welfare && n.WelfareSpending <= .18f, "solvent AI responds to radicalism even when headline stability is high");
        s.Radicalism = 1;
        var snapshot = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "radicalism-test"), "radicalism-test").Campaign;
        var loadedNation = snapshot.NationByTag(n.Tag); var loadedState = snapshot.States.First(x => x.MapIndex == s.MapIndex);
        loadedNation.Inflation = InflationCore.Weights.Comfort;
        Check(RadicalismCore.WeeklyChange(snapshot, loadedNation, loadedState, false, 0, 0) < 0, "loaded saturated save recovers with no reset or migration");
    }
}
