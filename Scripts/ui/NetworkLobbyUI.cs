using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private int _networkRosterRevision;
    private long NetworkLocalPeer => _networkIsHost ? 1 : _networkPeer is not null && _networkConnected ? Multiplayer.GetUniqueId() : 0;

    private bool ValidateNetworkPort()
    {
        if (int.TryParse(_networkPort.Text.Trim(), out var port) && port >= 1024 && port <= 65535) return true;
        _networkStatus.Text = Tr("Enter a UDP port between 1024 and 65535.");
        _networkPort.GrabFocus();
        return false;
    }
    private string NetworkPlayerName() => string.IsNullOrWhiteSpace(_networkPlayerName.Text)
        ? "Player" : _networkPlayerName.Text.Trim();

    private void SelectNetworkCountry(Nation nation, bool focus)
    {
        if (nation is null || _networkGameStarted) return;
        if (RequestedNetworkNation()?.Id == nation.Id && (_networkPeer is null || _networkLocalNationId == nation.Id)) return;
        var localPeer = NetworkLocalPeer;
        if (_networkPeerNations.Any(pair => pair.Key != localPeer && pair.Value == nation.Id))
        {
            _networkStatus.Text = Tr("That country is occupied. Choose another country.");
            PlayStrategicCue("action_denied");
            return;
        }
        if (_networkConnected && !_networkIsHost)
        {
            // Only the host can reserve a country. Keep the previous selection until acknowledged.
            RpcId(1, nameof(ServerRegisterNetworkPlayer), NetworkPlayerName(), nation.Tag);
            _networkStatus.Text = Simulation.Loc.Format("Requesting {0}…", nation.DisplayName);
        }
        else if (_networkPeer is null || _networkIsHost)
        {
            _networkCountryTag.Text = nation.Tag;
            PlayStrategicCue("country_selected");
            if (_networkIsHost)
            {
                _networkLocalNationId = nation.Id;
                _networkPeerNations[1] = nation.Id;
                _networkReadyPeers.Clear();
                _networkRosterRevision++;
                RefreshNetworkRoster();
            }
            else RenderNetworkLobby();
        }
        if (focus) _globe.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude);
    }

    private void RenderNetworkCountries()
    {
        if (_networkCountries is null || _campaign is null) return;
        foreach (var child in _networkCountries.GetChildren()) { _networkCountries.RemoveChild(child); child.QueueFree(); }
        var query = _networkSearch.Text.Trim();
        var localPeer = NetworkLocalPeer;
        foreach (var nation in _campaign.Nations.Where(n => !Simulation.CountryMerges.IsArtefact(_campaign, n)).Where(n => query.Length == 0 ||
                     n.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     n.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     n.Tag.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.DisplayName))
        {
            var occupied = _networkPeerNations.Any(p => p.Key != localPeer && p.Value == nation.Id);
            var button = ActionButton($"{nation.DisplayName} · {nation.Tag}" + (occupied ? " · " + Tr("Occupied") : ""),
                () => SelectNetworkCountry(nation, true), 0, 38);
            button.Name = "LobbyCountry_" + nation.Tag;
            button.Icon = FlagTexture(nation.Tag);
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 30);
            button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            button.Disabled = occupied || (_networkPeer is not null && !_networkConnected);
            _networkCountries.AddChild(button);
        }
        if (_networkCountries.GetChildCount() == 0)
            _networkCountries.AddChild(Title("No countries found.", 12, Ui.Colour("text.muted")));
    }

    private void RenderNetworkLobby()
    {
        if (_networkRoster is null) return;
        RefreshNetworkScenarioChoice();
        foreach (var child in _networkRoster.GetChildren()) { _networkRoster.RemoveChild(child); child.QueueFree(); }
        var localPeer = NetworkLocalPeer;
        foreach (var pair in _networkPeerNations.OrderBy(p => p.Key))
        {
            var nation = _campaign.NationById(pair.Value);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(FlagBadge(nation?.Tag ?? "", 22));
            var label = Title($"{_networkNames.GetValueOrDefault(pair.Key, "Player")}" +
                (pair.Key == 1 ? " · " + Tr("Host") : "") + (pair.Key == localPeer ? " · " + Tr("You") : "") +
                $"\n{nation?.DisplayName} · {Tr(_networkReadyPeers.Contains(pair.Key) ? "Ready" : "Choosing")}",
                12, Ui.Colour(_networkReadyPeers.Contains(pair.Key) ? "text.accent" : "text.body_soft"));
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            _networkRoster.AddChild(row);
        }
        if (_networkPeerNations.Count == 0) _networkRoster.AddChild(Title("No players connected yet.", 12, Ui.Colour("text.muted")));
        _networkSelection.Text = RequestedNetworkNation()?.DisplayName ?? Tr("Choose a country");
        _networkReady.Visible = _networkConnected && !_networkGameStarted;
        _networkReady.Disabled = !_networkPeerNations.ContainsKey(localPeer);
        _networkReady.Text = Tr(_networkReadyPeers.Contains(localPeer) ? "Not ready" : "Ready");
        _networkStart.Visible = _networkIsHost && !_networkGameStarted;
        _networkStart.Disabled = _networkPeerNations.Count < 2 || !_networkPeerNations.Keys.All(id => _networkReadyPeers.Contains(id));
        _networkStart.TooltipText = Tr("At least two players must choose different countries and confirm readiness. Country or roster changes reset readiness.");
        _networkHost.Visible = _networkJoin.Visible = _networkPeer is null;
        _networkPlayerName.Editable = _networkAddress.Editable = _networkPort.Editable = _networkPeer is null;
        RenderNetworkCountries();
    }

    private void ToggleNetworkReady()
    {
        if (!_networkConnected || _networkGameStarted) return;
        if (_networkIsHost)
        {
            if (!_networkReadyPeers.Add(1)) _networkReadyPeers.Remove(1);
            PlayStrategicCue(_networkReadyPeers.Contains(1) ? "player_ready" : "player_not_ready");
            RefreshNetworkRoster();
        }
        else RpcId(1, nameof(ServerSetNetworkReady), !_networkReadyPeers.Contains(Multiplayer.GetUniqueId()), _networkRosterRevision);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerSetNetworkReady(bool ready, int revision)
    {
        var peer = Multiplayer.GetRemoteSenderId();
        if (!_networkIsHost || _networkGameStarted || !_networkPeerNations.ContainsKey(peer) || revision != _networkRosterRevision) return;
        if (ready) _networkReadyPeers.Add(peer); else _networkReadyPeers.Remove(peer);
        RefreshNetworkRoster();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientReceiveNetworkRoster(long[] peers, int[] nations, string[] names, int[] ready, int revision)
    {
        if (_networkIsHost || peers.Length != nations.Length || peers.Length != names.Length || peers.Length != ready.Length || peers.Length > 9) return;
        var wasReady = _networkReadyPeers.Contains(NetworkLocalPeer);
        _networkPeerNations.Clear(); _networkNames.Clear(); _networkReadyPeers.Clear();
        _networkRosterRevision = revision;
        for (var i = 0; i < peers.Length; i++)
        {
            _networkPeerNations[peers[i]] = nations[i]; _networkNames[peers[i]] = names[i];
            if (ready[i] != 0) _networkReadyPeers.Add(peers[i]);
        }
        RenderNetworkLobby();
        var isReady = _networkReadyPeers.Contains(NetworkLocalPeer);
        if (isReady != wasReady) PlayStrategicCue(isReady ? "player_ready" : "player_not_ready");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientNetworkLobbyRefusal(string reason)
    {
        _networkStatus.Text = Tr(reason);
        PlayStrategicCue("action_denied");
    }
}
