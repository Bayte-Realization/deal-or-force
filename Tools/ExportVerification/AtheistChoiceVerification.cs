using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyAtheistChoice(DealOrForceGame game)
    {
        var world = game.VerificationWorld; var nation = world.Player;
        nation.IdentityChangedDay = -9999;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText"); game.Call("OpenStateAffairs"); await Wait(.2);
            game.VerificationRevealReligion(); await Wait(.2);
            var atheist = game.FindChild("StateReligion_atheism", true, false) as Button;
            var secular = game.FindChild("StateReligion_irreligion", true, false) as Button;
            Check(atheist is { Disabled: false } && secular is not null && atheist.Text != secular.Text,
                "actual government UI distinguishes atheist state from neutral secularism " + language + size.X);
            CheckFits(atheist, "atheist declaration " + language + size.X);
            CheckFits(secular, "secular declaration " + language + size.X);
            var before = CampaignFingerprint.Of(world);
            game.VerificationRevealReligion(); await Wait(.1);
            Check(CampaignFingerprint.Of(world) == before, "reading the new worldview option changes no residents or state identity");
            await Capture("atheist-state-choice-" + language + "-" + size.X);
            game.Call("CloseStateAffairs");
        }
        game.Call("OpenStateAffairs"); await Wait(.1);
        var share = StateIdentity.ReligionShare(world, nation, "atheism"); var hours = world.ElapsedHours;
        (game.FindChild("StateReligion_atheism", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2); game.VerificationRevealReligion();
        Check(nation.DeclaredReligion == "atheism" && world.IsPaused && world.ElapsedHours == hours,
            "real atheist-state button adopts immediately while paused through the campaign command");
        Check(StateIdentity.ReligionShare(world, nation, "atheism") == share,
            "real UI declaration does not instantly convert any residents");
        Check((game.FindChild("StateReligion_atheism", true, false) as Button) is { Disabled: true },
            "selected atheist state cannot be declared and charged twice");
        await Capture("atheist-state-adopted-russian-1280");
        game.Call("CloseStateAffairs");
    }
}
