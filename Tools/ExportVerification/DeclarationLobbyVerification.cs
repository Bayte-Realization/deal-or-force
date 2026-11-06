using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyDeclarationAndLobby(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var attacker = world.Player; var target = world.NationByTag("RUS"); var ally = world.NationByTag("USA");
        if (target.Id == attacker.Id) target = world.NationByTag("RUS");
        var bond = CampaignDiplomacy.Get(world, attacker.Id, ally.Id); bond.Stance = DiplomaticStance.Allied; bond.Opinion = 100;
        var before = world.Wars.Count;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            game.VerificationOpenWarReview(target.Id); await Wait(.4);
            Check(world.Wars.Count == before, "opening war review never declares war");
            var confirm = game.FindChild("ConfirmWarDeclaration", true, false) as Button;
            Check(confirm is not null && !confirm.Disabled, "explicit war confirmation available " + language + size.X);
            CheckFits(confirm, "war confirmation " + language + size.X);
            CheckFits(game.FindChild("CancelWarDeclaration", true, false) as Control, "war cancel " + language + size.X);
            Check(game.FindChild("InviteWarPartner_" + ally.Id, true, false) is CheckBox, "offensive ally has a selectable invitation");
            await Capture("war-review-" + language + "-" + size.X);
            game._Input(new InputEventKey { Keycode = Key.Escape, Pressed = true }); await Wait(.1);
            Check(game.FindChild("WarDeclarationReview", true, false) is null && world.Wars.Count == before, "Escape cancels without declaring");
        }
        SettingsCatalog.Store("map_camera_inertia", false); game.VerificationApplyCameraPreference();
        Check(!game.VerificationCameraInertia, "camera inertia disables immediately");
        SettingsCatalog.Store("map_camera_inertia", true); game.VerificationApplyCameraPreference();
        Check(game.VerificationCameraInertia, "camera inertia enables immediately");
        game.VerificationOpenWarReview(target.Id);
        var invitation = game.FindChild("InviteWarPartner_" + ally.Id, true, false) as CheckBox;
        if (invitation is not null && !invitation.Disabled) invitation.ButtonPressed = true;
        (game.FindChild("ConfirmWarDeclaration", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.5);
        Check(world.Wars.Count == before + 1 && CampaignDiplomacy.AtWar(world, attacker.Id, target.Id), "actual confirm button declares exactly one war");
        game.Call("OpenNetworkMultiplayer"); await Wait(.3);
        var scenarios = game.FindChild("LobbyScenario", true, false) as OptionButton;
        Check(scenarios is not null && !scenarios.Disabled && scenarios.ItemCount > 0, "scenario selector is usable before creating a lobby");
        await Capture("multiplayer-scenario-choice");
        game.VerificationLeaveLobbyForCampaign();
    }

    private async Task VerifyScenarioPeer(DealOrForceGame game, string[] args)
    {
        var at = Array.IndexOf(args, "--verify-scenario-peer");
        var host = at + 1 < args.Length && args[at + 1] == "host";
        var port = at + 2 < args.Length ? int.Parse(args[at + 2]) : 17270;
        if (host)
        {
            game.VerificationPrepareAncientLobby(port);
            GD.Print("LOBBY_VERIFY host-ready");
        }
        else game.VerificationJoinAncientLobby(port);
        var deadline = Time.GetTicksMsec() + 180_000;
        while (Time.GetTicksMsec() < deadline && !game.VerificationNetworkStarted)
        {
            if (game.VerificationNetworkSeats >= 2)
            {
                game.VerificationReadyNetworkSeat();
                if (host) game.VerificationStartNetworkCampaign();
            }
            else if (!host && game.VerificationWorld.ScenarioId == "verification_ancient_lobby")
                game.VerificationSelectAncientSeat();
            await Wait(.2);
        }
        Check(game.VerificationNetworkStarted, "two real ENet peers start the chosen scenario: " + (host ? "host" : "client"));
        Check(game.VerificationWorld.ScenarioId == "verification_ancient_lobby" && game.VerificationWorld.Date.Year == -3499 &&
            game.VerificationWorld.NationByTag("QAZ") is not null, "host's BCE date and custom country roster reach the peer unchanged");
        Check(game.VerificationNetworkSeats == 2, "both players keep different reserved countries");
        if (host) await Wait(4); // let the client acknowledge the start snapshot before closing this owned server
    }
}

public partial class DealOrForceGame
{
    internal bool VerificationNetworkStarted => _networkGameStarted;
    internal int VerificationNetworkSeats => _networkPeerNations.Count;
    internal void VerificationPrepareAncientLobby(int port)
    {
        _campaign.ScenarioId = "verification_ancient_lobby";
        _campaign.Date = new GameDate(-3499, 1, 1);
        _campaign.NationByTag("KOR").Tag = "QAZ";
        _campaign.SetPlayer(_campaign.NationByTag("JPN").Id);
        OpenNetworkMultiplayer(); _networkPort.Text = port.ToString(); _networkScenarioTitle = "BCE verification scenario";
        HostNetworkGame();
    }
    internal void VerificationJoinAncientLobby(int port)
    { OpenNetworkMultiplayer(); _networkPort.Text = port.ToString(); _networkAddress.Text = "127.0.0.1"; JoinNetworkGame(); }
    internal void VerificationSelectAncientSeat()
    {
        var nation = _campaign.NationByTag("QAZ");
        if (nation is not null && _networkLocalNationId != nation.Id) SelectNetworkCountry(nation, false);
    }
    internal void VerificationReadyNetworkSeat()
    { if (_networkPeerNations.ContainsKey(NetworkLocalPeer) && !_networkReadyPeers.Contains(NetworkLocalPeer)) ToggleNetworkReady(); }
    internal void VerificationStartNetworkCampaign() => StartHostedNetworkCampaign();
    internal void VerificationOpenWarReview(int id) => OpenWarDeclaration(_campaign.NationById(id));
    internal bool VerificationCameraInertia => _globe.CameraInertiaEnabled;
    internal void VerificationApplyCameraPreference() => ApplySetting("map_camera_inertia");
    internal void VerificationLeaveLobbyForCampaign()
    { ShutdownNetworkPeer(); _networkLayer.Visible = false; _globe.CountrySelectionOnly = false; BeginSession(); }
}
