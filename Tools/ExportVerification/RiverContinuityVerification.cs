using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyRiverContinuity(DealOrForceGame game)
    {
        Check(DisplayServer.GetName() != "headless", "river continuity uses a real rendered viewport");
        var c = game.VerificationWorld; c.IsPaused = true;
        var fingerprint = CampaignFingerprint.Of(c); var rng = c.Random.State;
        GetTree().Root.Size = new Vector2I(1920, 1080);
        game.Call("CloseMapSelection");
        foreach (var flat in new[] { false, true })
        foreach (var location in new[] { ("perm", 58.01f, 56.25f), ("volga", 48.8f, 44.5f) })
        foreach (var distance in new[] { 3.6f, 5.4f })
        {
            game.VerificationContinuityView(flat, location.Item2, location.Item3, distance);
            await Wait(1.2);
            await Capture($"river-{location.Item1}-{(flat ? "flat" : "globe")}-{distance:0.0}");
        }
        Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == rng,
            "changing river scale and projection preserves the campaign and RNG");
        Check(GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn") == Error.Ok, "river probe returns to the menu");
        await Wait(1);
    }
}
