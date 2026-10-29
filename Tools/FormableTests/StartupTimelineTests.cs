using System;
using DealOrForce.Presentation;

static class StartupTimelineTests
{
    public static void Run()
    {
        var t = new StartupTimeline();
        Check(t.Current == StartupTimeline.Card.Studio && t.Opacity == 0, "starts on studio with fade-in");
        t.Tick(.1, false); var early = t.Opacity; t.Tick(.1, false);
        Check(early > 0 && t.Opacity > early && t.Opacity < 1, "studio fades progressively");
        t.Skip(); var before = t.Opacity; t.Tick(.05, false);
        Check(t.Opacity <= before, "early skip never jumps to full opacity");
        Until(t, () => t.Current == StartupTimeline.Card.Game, false);
        Check(t.Current == StartupTimeline.Card.Game && !t.SkipRequested, "studio skip advances only one card");
        t.Skip(); for (var i=0;i<150;i++) t.Tick(.1,false);
        Check(t.Waiting && t.Opacity == 1 && t.SkipRequested, "game skip waits for genuine preparation");
        t.Tick(.05,true); Check(t.CurrentPhase == StartupTimeline.Phase.Out, "ready releases the waiting skip");
        Until(t, () => t.Current == StartupTimeline.Card.Complete, true);
        Check(t.Opacity == 0, "completion ends at zero opacity");
        t.Skip(); t.Tick(1,true); Check(t.Current == StartupTimeline.Card.Complete, "complete is terminal");
        t = new(); Until(t, () => t.Current == StartupTimeline.Card.Game, false);
        Check(!t.SkipRequested, "studio completes automatically without preparation");
        for(var i=0;i<80;i++) t.Tick(.1,false);
        Check(t.Waiting && t.Opacity==1, "automatic game completion also respects preparation");
        Until(t, () => t.Current == StartupTimeline.Card.Complete, true);
        Check(t.Current == StartupTimeline.Card.Complete, "normal sequence ends in order");
        t = new(); t.Tick(double.NaN,true); t.Tick(-1,true);
        Check(t.Opacity==0, "invalid deltas are ignored");
        t.Tick(120,true); Check(t.Current==StartupTimeline.Card.Studio && t.Opacity<1, "one stalled frame cannot swallow the ident");
    }
    private static void Until(StartupTimeline t, Func<bool> condition, bool ready)
    { for(var i=0;i<200&&!condition();i++)t.Tick(.05,ready);Check(condition(),"bounded timeline transition"); }
    private static void Check(bool ok,string label){if(!ok)throw new Exception("FAIL startup: "+label);Console.WriteLine("PASS startup: "+label);}
}
