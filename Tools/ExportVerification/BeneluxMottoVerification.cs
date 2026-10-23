using System;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyBeneluxMotto(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        var belgium = c.NationByTag("BEL"); var luxembourg = c.NationByTag("LUX");
        Check(belgium?.Color.Equals("#d8ae3b",StringComparison.OrdinalIgnoreCase) == true,
            "actual campaign Belgium uses the requested warm golden yellow");
        Check(luxembourg?.Color.Equals("#41cfcb",StringComparison.OrdinalIgnoreCase) == true,
            "actual campaign Luxembourg uses the requested aqua");
        var fp = CampaignFingerprint.Of(c);
        foreach (var (language,width,height) in new[] { ("english",1920,1080), ("english",1280,720), ("russian",1920,1080), ("russian",1280,720) })
        {
            Loc.Language = language; GetTree().Root.Size = new(width,height); game.Call("RefreshLocalisedText");
            var cover = new LoadingScreen { Name = "MottoVerification", ZIndex = 200 };
            GetTree().Root.AddChild(cover); await Wait(.1);
            cover.SetStage("Loading scenario", "Preparing map", 50, "Loading advances as each stage is completed.");
            await Wait(.1);
            var motto = cover.FindChild("LoadingMotto",true,false) as Label;
            var expected = language == "russian" ? "Жизнь — это вечная борьба. История — это вечная война." :
                "Life is an eternal struggle. History is an eternal war.";
            Check(motto?.Text == expected && motto.IsVisibleInTree(), "loading motto is exact and localized: " + language + width);
            if (motto is not null)
            {
                var rect = motto.GetGlobalRect();
                Check(cover.GetViewportRect().Grow(1).Encloses(rect),
                    "loading motto stays inside the actual viewport");
            }
            await Capture("loading-motto-" + language + "-" + width);
            cover.QueueFree(); await Wait(.1);
            game.VerificationOccupationView(true, 50.3f, 5.3f, 5.0f); await Wait(.4);
            await Capture("benelux-colours-" + language + "-" + width);
            Check(CampaignFingerprint.Of(c) == fp, "loading and map inspection do not change campaign state");
        }
    }
}
