using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private OptionButton _networkScenarioChoice;
    private Label _networkScenarioCaption;
    private IReadOnlyList<ScenarioEntry> _networkScenarioEntries = Array.Empty<ScenarioEntry>();
    private string _networkScenarioTitle = "";

    private void BuildNetworkScenarioChoice(VBoxContainer rows)
    {
        rows.AddChild(Title("Scenario · selected by the host", 16, Ui.Colour("text.accent")));
        _networkScenarioChoice = new OptionButton { Name = "LobbyScenario", FitToLongestItem = false,
            CustomMinimumSize = new(0, 38), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        rows.AddChild(_networkScenarioChoice);
        _networkScenarioCaption = Title("", 12, Ui.Colour("text.muted"));
        _networkScenarioCaption.Name = "LobbyScenarioCaption";
        _networkScenarioCaption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_networkScenarioCaption);
        _networkScenarioChoice.ItemSelected += async index =>
        {
            if (_networkPeer is not null || _isLoading || index < 0 || index >= _networkScenarioEntries.Count) return;
            var entry = _networkScenarioEntries[(int)index];
            var previousTag = RequestedNetworkNation()?.Tag;
            _networkScenarioChoice.Disabled = true;
            if (await RebuildAuthoredScenarioWithLoading(entry))
            {
                _networkScenarioTitle = entry.Name;
                _networkCountryTag.Text = _campaign.NationByTag(previousTag ?? "")?.Tag ?? _campaign.Player.Tag;
                _networkPeerNations.Clear(); _networkReadyPeers.Clear();
                _campaign.IsPaused = true; _hudLayer.Visible = false; _networkLayer.Visible = true;
                _globe.CountrySelectionOnly = true;
            }
            RefreshNetworkScenarioChoice(); RenderNetworkLobby();
        };
    }

    private void RefreshNetworkScenarioChoice()
    {
        if (!IsInstanceValid(_networkScenarioChoice)) return;
        if (_networkPeer is null)
        {
            _networkScenarioEntries = ScenarioLibrary.Discover(ScenarioRoots, Path.Combine(AuthorRoot, "scenarios"));
            _networkScenarioChoice.Clear();
            var selected = -1;
            for (var i = 0; i < _networkScenarioEntries.Count; i++)
            {
                var entry = _networkScenarioEntries[i];
                _networkScenarioChoice.AddItem(Tr(entry.Name));
                _networkScenarioChoice.SetItemTooltip(i, Tr(entry.Description));
                if (entry.Id == _campaign.ScenarioId) selected = i;
            }
            if (selected < 0)
            {
                selected = _networkScenarioChoice.ItemCount;
                _networkScenarioChoice.AddItem(Loc.Format("Current world · {0}", _campaign.ScenarioId));
            }
            _networkScenarioChoice.Select(selected);
            _networkScenarioTitle = selected < _networkScenarioEntries.Count ? _networkScenarioEntries[selected].Name : _campaign.ScenarioId;
        }
        else
        {
            _networkScenarioChoice.Clear();
            _networkScenarioChoice.AddItem(Tr(_networkScenarioTitle));
            _networkScenarioChoice.Select(0);
        }
        _networkScenarioChoice.Disabled = _networkPeer is not null || _isLoading;
        _networkScenarioCaption.Text = Loc.Format("{0} · {1}\n{2}", Tr(_networkScenarioTitle), _campaign.Date.Display(),
            Tr(_networkPeer is null ? "Choose before creating the lobby. Connected players receive the host's world."
                : "The host's scenario is locked for this lobby. Disconnect to choose another."));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientReceiveLobbyScenario(byte[] compressed, string scenarioTitle)
    {
        if (_networkIsHost || !_networkConnected || _networkGameStarted) return;
        var result = SaveSystem.ImportNetworkSnapshot(compressed, ModFingerprint);
        if (!result.Success) { _networkStatus.Text = result.Warning; return; }
        var desiredTag = _networkCountryTag.Text;
        AdoptNetworkCampaign(result.Campaign);
        _campaign.IsPaused = true;
        _networkScenarioTitle = (scenarioTitle ?? "")[..Math.Min(256, (scenarioTitle ?? "").Length)];
        _networkCountryTag.Text = _campaign.NationByTag(desiredTag)?.Tag ?? _campaign.Player.Tag;
        _networkLocalNationId = -1;
        _hudLayer.Visible = false; _networkLayer.Visible = true; _globe.CountrySelectionOnly = true;
        // Registration happens AFTER the authoritative world arrives. A mod scenario
        // may contain no KOR/USA at all; the client must choose from the host's roster.
        RpcId(1, nameof(ServerRegisterNetworkPlayer), NetworkPlayerName(), _networkCountryTag.Text);
        RefreshNetworkScenarioChoice(); RenderNetworkLobby();
    }
}
