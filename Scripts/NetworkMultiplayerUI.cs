using System;
using System.Collections.Generic;
using System.Linq;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

/// <summary>
/// Authoritative ENet session for direct IP, physical LAN and virtual LAN tools such as Radmin VPN.
/// Commands use the deterministic campaign queue; peers never receive arbitrary CLR payload types.
/// </summary>
public partial class GlobalityGame
{
    private const int DefaultNetworkPort = 7270;
    private Control _networkLayer = null!;
    private PanelContainer _networkHud = null!;
    private Label _networkHudStatus = null!;
    private Label _networkStatus = null!;
    private Label _networkRoster = null!;
    private LineEdit _networkAddress = null!;
    private LineEdit _networkPort = null!;
    private LineEdit _networkPlayerName = null!;
    private LineEdit _networkCountryTag = null!;
    private Button _networkStart = null!;
    private ENetMultiplayerPeer _networkPeer;
    private readonly Dictionary<long, int> _networkPeerNations = new();
    private bool _networkIsHost;
    private bool _networkConnected;
    private bool _networkGameStarted;
    private bool _applyingNetworkCommand;
    private int _networkLocalNationId = -1;
    private double _networkClockAccumulator;

    private bool IsNetworkClient => _networkConnected && !_networkIsHost;

    private void BuildNetworkMultiplayerInterface()
    {
        _networkHud = TexturedPanel(_hudLayer);
        _networkHud.AnchorLeft = 0;
        _networkHud.AnchorRight = 0;
        _networkHud.AnchorTop = 0;
        _networkHud.AnchorBottom = 0;
        _networkHud.OffsetLeft = 14;
        _networkHud.OffsetRight = 350;
        _networkHud.OffsetTop = 146;
        _networkHud.OffsetBottom = 208;
        _networkHud.Visible = false;
        var hudRows = PanelRows(_networkHud, 10, 7, 2);
        _networkHudStatus = Title("NETWORK", 11, Ui.Colour("state.info"));
        hudRows.AddChild(_networkHudStatus);

        _networkLayer = FullLayer("NetworkMultiplayer");
        _networkLayer.Visible = false;
        _networkLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_networkLayer);
        var shade = new ColorRect { Color = new Color(0.01f, 0.016f, 0.026f, 0.90f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _networkLayer.AddChild(shade);
        var panel = TexturedPanel(_networkLayer);
        panel.AnchorLeft = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -285;
        panel.OffsetRight = 285;
        panel.OffsetTop = -340;
        panel.OffsetBottom = 340;
        var rows = PanelRows(panel, 26, 22, 10);
        rows.AddChild(Title("MULTIPLAYER · LAN / DIRECT IP", 26, Ui.Colour("text.accent")));
        var explanation = Title("Host an authoritative campaign or join by IPv4 address. Radmin/Hamachi work as a virtual LAN: enter the host's VPN IPv4 address.", 13, Ui.Colour("text.muted"));
        explanation.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(explanation);

        _networkPlayerName = NetworkField("Player name", "Player");
        rows.AddChild(_networkPlayerName);
        _networkCountryTag = NetworkField("Country tag", "KOR");
        _networkCountryTag.MaxLength = 3;
        rows.AddChild(_networkCountryTag);
        _networkAddress = NetworkField("Host IPv4", "127.0.0.1");
        rows.AddChild(_networkAddress);
        _networkPort = NetworkField("UDP port", DefaultNetworkPort.ToString());
        rows.AddChild(_networkPort);

        var connection = new HBoxContainer();
        connection.AddThemeConstantOverride("separation", 8);
        rows.AddChild(connection);
        connection.AddChild(ActionButton("HOST", HostNetworkGame, 245, 42, true));
        connection.AddChild(ActionButton("JOIN", JoinNetworkGame, 245, 42));

        _networkStart = ActionButton("START CAMPAIGN", StartHostedNetworkCampaign, 0, 44, true);
        _networkStart.Disabled = true;
        rows.AddChild(_networkStart);
        _networkStatus = Title("Not connected.", 13, Ui.Colour("text.body_soft"));
        _networkStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_networkStatus);
        _networkRoster = Title("", 12, Ui.Colour("text.hint"));
        _networkRoster.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_networkRoster);
        rows.AddChild(ActionButton("DISCONNECT / BACK", CloseNetworkMultiplayer, 0, 36));

        Multiplayer.PeerConnected += OnNetworkPeerConnected;
        Multiplayer.PeerDisconnected += OnNetworkPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToNetworkServer;
        Multiplayer.ConnectionFailed += OnNetworkConnectionFailed;
        Multiplayer.ServerDisconnected += OnNetworkServerDisconnected;
    }

    private static LineEdit NetworkField(string placeholder, string value) => new()
    {
        PlaceholderText = placeholder,
        Text = value,
        CustomMinimumSize = new Vector2(0, 38),
        ClearButtonEnabled = true
    };

    private void OpenNetworkMultiplayer()
    {
        _campaign.IsPaused = true;
        _menuLayer.Visible = false;
        _pickerLayer.Visible = false;
        _hudLayer.Visible = false;
        _networkLayer.Visible = true;
        _networkCountryTag.Text = _campaign.Player?.Tag ?? "KOR";
        _networkStatus.Text = "Choose a unique country tag, then host or join.";
        _networkRoster.Text = "UDP must be allowed through Windows Firewall. Internet play also requires UDP port forwarding; virtual LAN normally does not.";
    }

    private int RequestedNetworkPort() => int.TryParse(_networkPort.Text.Trim(), out var port)
        ? Math.Clamp(port, 1024, 65535) : DefaultNetworkPort;

    private void HostNetworkGame()
    {
        ShutdownNetworkPeer();
        var nation = RequestedNetworkNation();
        if (nation is null)
        {
            _networkStatus.Text = "Unknown country tag. Use a three-letter tag such as KOR, USA or DEU.";
            return;
        }
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(RequestedNetworkPort(), 8);
        if (error != Error.Ok)
        {
            _networkStatus.Text = $"Could not host UDP {RequestedNetworkPort()}: {error}.";
            peer.Close();
            return;
        }
        _networkPeer = peer;
        Multiplayer.MultiplayerPeer = peer;
        _networkIsHost = true;
        _networkConnected = true;
        _networkGameStarted = false;
        _networkLocalNationId = nation.Id;
        _networkPeerNations[1] = nation.Id;
        _networkStart.Disabled = true;
        _networkStatus.Text = $"Hosting on UDP {RequestedNetworkPort()}. Share this machine's LAN/Radmin IPv4 address.";
        RefreshNetworkRoster();
    }

    private void JoinNetworkGame()
    {
        ShutdownNetworkPeer();
        if (RequestedNetworkNation() is null)
        {
            _networkStatus.Text = "Unknown country tag. Use a three-letter tag such as KOR, USA or DEU.";
            return;
        }
        var address = _networkAddress.Text.Trim();
        if (string.IsNullOrWhiteSpace(address)) address = "127.0.0.1";
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(address, RequestedNetworkPort());
        if (error != Error.Ok)
        {
            _networkStatus.Text = $"Could not start connection: {error}.";
            peer.Close();
            return;
        }
        _networkPeer = peer;
        Multiplayer.MultiplayerPeer = peer;
        _networkIsHost = false;
        _networkConnected = false;
        _networkGameStarted = false;
        _networkStatus.Text = $"Connecting to {address}:{RequestedNetworkPort()}…";
    }

    private Nation RequestedNetworkNation()
    {
        var tag = _networkCountryTag.Text.Trim().ToUpperInvariant();
        return _campaign?.NationByTag(tag);
    }

    private void OnConnectedToNetworkServer()
    {
        _networkConnected = true;
        var nation = RequestedNetworkNation();
        RpcId(1, nameof(ServerRegisterNetworkPlayer), _networkPlayerName.Text.Trim(), nation?.Tag ?? "");
        _networkStatus.Text = "Connected. Waiting for the host to start the campaign.";
    }

    private void OnNetworkConnectionFailed()
    {
        _networkStatus.Text = "Connection failed. Check IPv4 address, UDP port and firewall.";
        ShutdownNetworkPeer();
    }

    private void OnNetworkServerDisconnected()
    {
        _networkStatus.Text = "Host disconnected.";
        ShutdownNetworkPeer();
        _networkLayer.Visible = true;
        _hudLayer.Visible = false;
    }

    private void OnNetworkPeerConnected(long peerId)
    {
        if (_networkIsHost) _networkStatus.Text = $"Peer {peerId} connected; waiting for country registration.";
    }

    private void OnNetworkPeerDisconnected(long peerId)
    {
        if (!_networkIsHost) return;
        _networkPeerNations.Remove(peerId);
        RefreshNetworkRoster();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRegisterNetworkPlayer(string playerName, string requestedTag)
    {
        if (!_networkIsHost) return;
        var peerId = Multiplayer.GetRemoteSenderId();
        var requested = _campaign.NationByTag(requestedTag);
        var used = _networkPeerNations.Values.ToHashSet();
        var assigned = requested is not null && !used.Contains(requested.Id)
            ? requested
            : _campaign.Nations.Where(nation => !used.Contains(nation.Id)).OrderByDescending(nation => nation.Gdp).FirstOrDefault();
        if (assigned is null) return;
        _networkPeerNations[peerId] = assigned.Id;
        RpcId(peerId, nameof(ClientAssignedNetworkNation), assigned.Tag);
        _networkStatus.Text = $"{(string.IsNullOrWhiteSpace(playerName) ? $"Peer {peerId}" : playerName)} joined as {assigned.Tag}.";
        RefreshNetworkRoster();
        if (_networkGameStarted)
        {
            // A late joiner must become a human seat before the authoritative
            // snapshot is made, otherwise its country would continue to run AI.
            _campaign.AddHumanSeat(assigned.Id);
            SendNetworkSnapshot(peerId);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientAssignedNetworkNation(string tag)
    {
        var nation = _campaign.NationByTag(tag);
        _networkLocalNationId = nation?.Id ?? -1;
        _networkCountryTag.Text = tag;
        _networkStatus.Text = $"Registered as {tag}. Waiting for host.";
    }

    private void RefreshNetworkRoster()
    {
        if (!_networkIsHost) return;
        var rows = _networkPeerNations.OrderBy(pair => pair.Key).Select(pair =>
        {
            var nation = _campaign.NationById(pair.Value);
            return $"Peer {pair.Key}: {nation?.Tag ?? "?"} · {nation?.Name ?? "Unknown"}";
        });
        _networkRoster.Text = "PLAYERS\n" + string.Join("\n", rows);
        _networkStart.Disabled = _networkPeerNations.Count < 2;
    }

    private void StartHostedNetworkCampaign()
    {
        if (!_networkIsHost || _networkPeerNations.Count < 2) return;
        _campaign.ConfigureSinglePlayer(_networkLocalNationId);
        foreach (var nationId in _networkPeerNations.Values.Where(id => id != _networkLocalNationId).Distinct())
            _campaign.AddHumanSeat(nationId);
        _networkGameStarted = true;
        AttachNetworkCommandTransport();
        _networkLayer.Visible = false;
        BeginSession();
        UpdateNetworkHud();
        foreach (var peerId in _networkPeerNations.Keys.Where(id => id != 1).ToArray()) SendNetworkSnapshot(peerId);
    }

    private void SendNetworkSnapshot(long peerId)
    {
        var snapshot = SaveSystem.ExportNetworkSnapshot(_campaign, ModFingerprint);
        if (snapshot.Length == 0) return;
        RpcId(peerId, nameof(ClientReceiveNetworkSnapshot), snapshot);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientReceiveNetworkSnapshot(byte[] compressed)
    {
        if (_networkIsHost) return;
        var result = SaveSystem.ImportNetworkSnapshot(compressed, ModFingerprint);
        if (!result.Success)
        {
            _networkStatus.Text = result.Warning;
            return;
        }
        AdoptNetworkCampaign(result.Campaign);
        _networkGameStarted = true;
        _networkLayer.Visible = false;
        _menuLayer.Visible = false;
        _pickerLayer.Visible = false;
        _pauseMenuLayer.Visible = false;
        _handoverLayer.Visible = false;
        _hudLayer.Visible = true;
        UpdateNetworkHud();
        _networkStatus.Text = string.IsNullOrWhiteSpace(result.Warning) ? "Campaign synchronised." : result.Warning;
    }

    private void AdoptNetworkCampaign(Campaign campaign)
    {
        if (_campaign is not null) _campaign.Changed -= Refresh;
        _campaign = campaign;
        var local = _campaign.NationById(_networkLocalNationId) ?? _campaign.Player;
        _campaign.SetPlayer(local.Id);
        _campaign.Changed += Refresh;
        _selectedNation = local;
        _selectedState = _campaign.States.FirstOrDefault(state => state.NationMapIndex == local.MapIndex);
        _pickerNation = local;
        _modApi.Bind(_campaign);
        _globe.BindCampaign(_campaign);
        ResetStrategicAudioState();
        AttachNetworkCommandTransport();
        _pause.Text = _campaign.IsPaused ? "▶" : "II";
        Refresh();
    }

    private void AttachNetworkCommandTransport()
    {
        if (!_networkConnected || _campaign is null) return;
        if (_networkIsHost)
        {
            _campaign.Commands.SubmitInterceptor = command =>
            {
                if (!_networkGameStarted || _applyingNetworkCommand) return false;
                if (NetworkCommandCodec.TryEncode(command, out var type, out var json))
                    Rpc(nameof(ClientApplyNetworkCommand), type, json);
                return false;
            };
        }
        else
        {
            _campaign.Commands.SubmitInterceptor = command =>
            {
                if (_applyingNetworkCommand) return false;
                if (NetworkCommandCodec.TryEncode(command, out var type, out var json))
                    RpcId(1, nameof(ServerSubmitNetworkCommand), type, json);
                return true;
            };
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerSubmitNetworkCommand(string typeName, string json)
    {
        if (!_networkIsHost || !_networkGameStarted ||
            !NetworkCommandCodec.TryDecode(typeName, json, out var command)) return;
        var sender = Multiplayer.GetRemoteSenderId();
        if (!_networkPeerNations.TryGetValue(sender, out var nationId) || command.IssuerId != nationId) return;
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientApplyNetworkCommand(string typeName, string json)
    {
        if (_networkIsHost || !NetworkCommandCodec.TryDecode(typeName, json, out var command)) return;
        _applyingNetworkCommand = true;
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _applyingNetworkCommand = false;
    }

    private void NetworkProcess(double delta)
    {
        if (!_networkConnected || !_networkGameStarted || !_networkIsHost) return;
        _networkClockAccumulator += delta;
        if (_networkClockAccumulator < 0.50) return;
        _networkClockAccumulator = 0;
        Rpc(nameof(ClientReceiveNetworkClock), _campaign.ElapsedDays, _campaign.IsPaused, _speed);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientAdvanceNetworkDay(int hostElapsedDays)
    {
        if (_networkIsHost || _campaign is null || hostElapsedDays <= _campaign.ElapsedDays) return;
        var ticks = 0;
        _applyingNetworkCommand = true;
        while (_campaign.ElapsedDays < hostElapsedDays && ticks < 8)
        {
            _campaign.Tick();
            _modLoader.OnDayAdvanced(_campaign);
            ticks++;
        }
        _applyingNetworkCommand = false;
        if (_campaign.ElapsedDays != hostElapsedDays)
            _networkHudStatus.Text = $"RESYNC NEEDED · local {_campaign.ElapsedDays:N0} / host {hostElapsedDays:N0}";
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ClientReceiveNetworkClock(int elapsedDays, bool paused, int speed)
    {
        if (_networkIsHost || _campaign is null) return;
        _campaign.IsPaused = paused;
        _speed = Math.Clamp(speed, 1, 5);
        _pause.Text = paused ? "▶" : "II";
        _networkHudStatus.Text = $"CLIENT · {_campaign.Player.Tag} · host day {elapsedDays:N0}";
    }

    private void UpdateNetworkHud()
    {
        if (_networkHud is null) return;
        _networkHud.Visible = _hudLayer.Visible && _networkConnected && _networkGameStarted;
        if (!_networkHud.Visible) return;
        _networkHudStatus.Text = _networkIsHost
            ? $"HOST · {_networkPeerNations.Count} PLAYERS · UDP {RequestedNetworkPort()}"
            : $"CLIENT · {_campaign.Player.Tag} · {_networkAddress.Text.Trim()}";
    }

    private void CloseNetworkMultiplayer()
    {
        ShutdownNetworkPeer();
        _networkLayer.Visible = false;
        ShowMainMenu();
    }

    private void ShutdownNetworkPeer()
    {
        if (_campaign is not null) _campaign.Commands.SubmitInterceptor = null;
        _networkPeer?.Close();
        _networkPeer = null;
        Multiplayer.MultiplayerPeer = null;
        _networkPeerNations.Clear();
        _networkIsHost = false;
        _networkConnected = false;
        _networkGameStarted = false;
        _networkLocalNationId = -1;
        if (_networkHud is not null) _networkHud.Visible = false;
        if (_networkStart is not null) _networkStart.Disabled = true;
    }
}
