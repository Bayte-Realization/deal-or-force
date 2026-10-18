using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyWarScoreScale(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var attacker = world.NationByTag("RUS");
        var defender = world.NationByTag("UKR");
        var war = world.Wars.First(w => !w.Ended && w.AttackerId == attacker.Id && w.DefenderId == defender.Id);
        world.ConfigureSinglePlayer(attacker.Id);
        world.IsPaused = true;
        war.Collapsed = 0;
        Check(InterfaceSkin.Metric("war_ledger.bar_scale", 100) == 100,
            "base-game war-score presentation uses the full hundred-point range");
        foreach (var score in new[] { -100f, -40f, 0f, 40f, 100f })
        {
            war.WarScore = score; // isolated display fixture, never saved or ticked
            var before = CampaignFingerprint.Of(world);
            game.Call("OpenScreen", "Wars"); game.Call("SetWarLedgerFilter", true);
            await Wait(.1);
            var bar = game.FindChild("WarLedgerScoreBar" + war.Id, true, false) as WarBar;
            var expected = .5f + score / 200f;
            Check(bar is not null && MathF.Abs(bar.Marker - expected) < .0001f,
                "actual war-list marker represents score " + score + " without premature saturation");
            game.Call("ShowWarLedgerDetail", war.Id);
            await Wait(.1);
            bar = game.FindChild("WarLedgerDetailScoreBar", true, false) as WarBar;
            var caption = game.FindChild("WarLedgerScaleCaption", true, false) as Label;
            Check(bar is not null && MathF.Abs(bar.Marker - expected) < .0001f && caption?.Text.Contains("100") == true,
                "war detail uses the same full scale and states its endpoint at score " + score);
            game.Call("CloseWarLedger");
            game.VerificationOpenDiplomacy(defender.Tag);
            await Wait(.1);
            var standing = game.FindChild("DiplomacyStandingScoreBar" + war.Id, true, false) as WarBar;
            Check(standing is not null && MathF.Abs(standing.Marker - (1f - expected)) < .0001f,
                "selected defender's diplomatic standing mirrors the same score " + score);
            var ours = game.FindChild("DiplomacyPlayerScoreBar" + war.Id, true, false) as WarBar;
            if (ours is not null)
                Check(MathF.Abs(ours.Marker - expected) < .0001f, "player's diplomatic war bar uses its own side at score " + score);
            game.Call("CloseDiplomacyScreen");
            Check(CampaignFingerprint.Of(world) == before, "opening score displays changes no campaign state");
        }
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
        {
            Loc.Language = language;
            GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            war.WarScore = size.X == 1920 ? 40 : -40;
            var before = CampaignFingerprint.Of(world);
            game.Call("OpenScreen", "Wars"); game.Call("SetWarLedgerFilter", true);
            game.Call("ShowWarLedgerDetail", war.Id);
            await Wait(.2);
            var bar = game.FindChild("WarLedgerDetailScoreBar", true, false) as WarBar;
            Check(bar is not null && MathF.Abs(bar.Marker - (.5f + war.WarScore / 200f)) < .0001f,
                "full-scale marker remains correct " + language + " " + size.X);
            CheckFits(bar, "war-score bar " + language + " " + size.X);
            await Capture("war-score-scale-" + language + "-" + size.X);
            Check(CampaignFingerprint.Of(world) == before, "score layout and capture are read-only");
            game.Call("CloseWarLedger");
        }
    }
}
