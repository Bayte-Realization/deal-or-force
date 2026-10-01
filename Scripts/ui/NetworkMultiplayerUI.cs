using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Authoritative ENet session for direct IP, physical LAN and virtual LAN tools such as Radmin VPN.
/// Commands use the deterministic campaign queue; peers never receive arbitrary CLR payload types.
/// </summary>
public partial class DealOrForceGame
{
    private const int DefaultNetworkPort = 7270;
    private Control _networkLayer = null!;
    private PanelContainer _networkHud = null!;
    private Label _networkHudStatus = null!;
    private Label _networkStatus = null!;
    private VBoxContainer _networkRoster = null!;
    private VBoxContainer _networkCountries = null!;
    private LineEdit _networkSearch = null!;
    private Label _networkSelection = null!;
    private Button _networkReady = null!;
    private Button _networkHost = null!;
    private Button _networkJoin = null!;
    private readonly Dictionary<long, string> _networkNames = new();
    private readonly HashSet<long> _networkReadyPeers = new();
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

    /// <summary>
    /// How far behind the host a client may fall before it stops trying to catch up and
    /// asks for the world again.
    ///
    /// Catching up is normal and cheap - a client that stalls for a second replays the days
    /// it missed. Catching up forever is not: a client that is simply slower than the host
    /// falls further behind every day, and every order it receives is then applied to a
    /// world several days younger than the one the order was given in. That is a silent
    /// divergence, and it is worse than a pause, so past this many days it takes a
    /// snapshot instead.
    /// </summary>
    private const int NetworkResyncGapDays = 20;

    /// <summary>How often the host says what its world looks like, in days.</summary>
    private const int NetworkChecksumEveryDays = 10;

    private int _networkResyncRequestedDay = int.MinValue;
    private int _networkLastChecksumDay;

    /// <summary>
    /// Orders that arrived before the client had reached the day they were given on.
    ///
    /// Peers exchange orders, not state, and each replays the same simulation - which works
    /// exactly as long as an order is applied on the same day everywhere. A client that has
    /// fallen a few days behind was applying every order the moment it arrived, to a world
    /// several days younger than the one the order was given in: the host raises a tax on
    /// day 400 and the client raises it on day 396, and from then on the two worlds are
    /// quietly different. Nothing errors. The checksum notices ten days later and asks for
    /// the world again, which is a repair rather than a fix.
    ///
    /// So an order now carries the day it was given, and a client that is behind holds it
    /// until it gets there.
    /// </summary>
    private readonly List<(int Day, DealOrForce.Simulation.ICampaignCommand Command)> _networkDeferred = new();

    private void BuildNetworkMultiplayerInterface()
    {
        _networkHud = TexturedPanel(_hudLayer);
        Ui.Place(_networkHud, "hud.network");
        _networkHud.Visible = false;
        var hudRows = PanelRows(_networkHud, 10, 7, 2);
        _networkHudStatus = Title("NETWORK", 11, Ui.Colour("state.info"));
        hudRows.AddChild(_networkHudStatus);

        _networkLayer = FullLayer("NetworkMultiplayer");
        _networkLayer.Visible = false;
        _networkLayer.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_networkLayer);
        var panel = TexturedPanel(_networkLayer);
        panel.AddThemeStyleboxOverride("panel", ShellPanelStyle());
        Ui.Place(panel, "multiplayer.connection");
        var outer = PanelRows(panel, 16, 16, 8);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(rows);
        rows.AddChild(Title("MULTIPLAYER", 24, Ui.Colour("text.accent")));
        var explanation = Title("Host an authoritative campaign or join by IPv4 address. Radmin/Hamachi work as a virtual LAN: enter the host's VPN IPv4 address.", 13, Ui.Colour("text.muted"));
        explanation.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(explanation);

        rows.AddChild(Title("1 · Connect", 16, Ui.Colour("text.accent")));
        rows.AddChild(Title("Player name", 12, Ui.Colour("text.caption")));
        _networkPlayerName = NetworkField(Tr("Player name"), "Player");
        _networkPlayerName.MaxLength = 32;
        rows.AddChild(_networkPlayerName);
        _networkCountryTag = NetworkField("Country tag", "KOR");
        _networkCountryTag.Visible = false;
        rows.AddChild(_networkCountryTag);
        rows.AddChild(Title("Host address · LAN / VPN / Internet", 12, Ui.Colour("text.caption")));
        _networkAddress = NetworkField("Host IPv4", "127.0.0.1");
        rows.AddChild(_networkAddress);
        rows.AddChild(Title("UDP port · must match the host", 12, Ui.Colour("text.caption")));
        _networkPort = NetworkField("UDP port", DefaultNetworkPort.ToString());
        rows.AddChild(_networkPort);

        var connection = new HBoxContainer();
        connection.AddThemeConstantOverride("separation", 8);
        rows.AddChild(connection);
        _networkHost = ActionButton("Create lobby", HostNetworkGame, 0, 38, true);
        _networkJoin = ActionButton("Join lobby", JoinNetworkGame, 0, 38);
        _networkHost.Name = "LobbyHost";
        _networkJoin.Name = "LobbyJoin";
        _networkHost.SizeFlagsHorizontal = _networkJoin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        connection.AddChild(_networkHost);
        connection.AddChild(_networkJoin);

        _networkStart = ActionButton("START CAMPAIGN", StartHostedNetworkCampaign, 0, 44, true);
        _networkStart.Disabled = true;
        _networkReady = ActionButton("Ready", ToggleNetworkReady, 0, 38);
        _networkReady.Name = "LobbyReady";
        _networkStart.Name = "LobbyStart";
        rows.AddChild(_networkReady);
        rows.AddChild(_networkStart);
        _networkStatus = Title("Not connected.", 13, Ui.Colour("text.body_soft"));
        _networkStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_networkStatus);
        rows.AddChild(Title("3 · Players and countries", 16, Ui.Colour("text.accent")));
        _networkRoster = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddChild(_networkRoster);
        var lobbyActions = new HBoxContainer();
        lobbyActions.AddThemeConstantOverride("separation", 6);
        rows.AddChild(lobbyActions);
        var disconnect = ActionButton("DISCONNECT / BACK", CloseNetworkMultiplayer, 0, 36);
        disconnect.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lobbyActions.AddChild(disconnect);
        // Same reason as the country picker: a player waiting in a lobby has time to change
        // their language and nowhere to do it.
        lobbyActions.AddChild(ActionButton("Settings", ShowSettings, 120, 36));
        var help = Title("UDP must be allowed through Windows Firewall. Internet play also requires UDP port forwarding; virtual LAN normally does not.", 11, Ui.Colour("text.muted"));
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(help);

        var countryPanel = TexturedPanel(_networkLayer);
        countryPanel.AddThemeStyleboxOverride("panel", ShellPanelStyle());
        Ui.Place(countryPanel, "multiplayer.countries");
        var countryRows = PanelRows(countryPanel, 16, 16, 8);
        countryRows.AddChild(Title("2 · Choose your country", 18, Ui.Colour("text.accent")));
        var mapHelp = Title("Click a country on the globe, or search below. Drag to rotate; scroll to zoom.", 12, Ui.Colour("text.muted"));
        mapHelp.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        countryRows.AddChild(mapHelp);
        _networkSelection = Title("", 16, Ui.Colour("text.accent"));
        _networkSelection.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        countryRows.AddChild(_networkSelection);
        _networkSearch = NetworkField(Tr("Country name or tag"), "");
        _networkSearch.Name = "LobbyCountrySearch";
        _networkSearch.TextChanged += _ => RenderNetworkCountries();
        countryRows.AddChild(_networkSearch);
        var countryScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        countryRows.AddChild(countryScroll);
        _networkCountries = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        countryScroll.AddChild(_networkCountries);

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
        _globe.CountrySelectionOnly = true;
        _networkStatus.Text = Tr("Choose a country on the map, then create or join a lobby.");
        RenderNetworkLobby();
    }

    private int RequestedNetworkPort() => int.TryParse(_networkPort.Text.Trim(), out var port)
        ? Math.Clamp(port, 1024, 65535) : DefaultNetworkPort;

    private void HostNetworkGame()
    {
        if (!ValidateNetworkPort()) return;
        ShutdownNetworkPeer();
        var nation = RequestedNetworkNation();
        if (nation is null)
        {
            _networkStatus.Text = Tr("Unknown country tag. Use a three-letter tag such as KOR, USA or DEU.");
            return;
        }
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(RequestedNetworkPort(), 8);
        if (error != Error.Ok)
        {
            _networkStatus.Text = Simulation.Loc.Format("Could not host UDP {0}: {1}.", RequestedNetworkPort(), error);
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
        _networkNames[1] = NetworkPlayerName();
        PlayStrategicCue("network_connected");
        _networkStart.Disabled = true;
        _networkStatus.Text = Simulation.Loc.Format("Hosting on UDP {0}. Share this machine's LAN/Radmin IPv4 address.", RequestedNetworkPort());
        RefreshNetworkRoster();
    }

    private void JoinNetworkGame()
    {
        if (!ValidateNetworkPort()) return;
        ShutdownNetworkPeer();
        if (RequestedNetworkNation() is null)
        {
            _networkStatus.Text = Tr("Unknown country tag. Use a three-letter tag such as KOR, USA or DEU.");
            return;
        }
        var address = _networkAddress.Text.Trim();
        if (string.IsNullOrWhiteSpace(address)) address = "127.0.0.1";
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(address, RequestedNetworkPort());
        if (error != Error.Ok)
        {
            _networkStatus.Text = Simulation.Loc.Format("Could not start connection: {0}.", error);
            peer.Close();
            return;
        }
        _networkPeer = peer;
        Multiplayer.MultiplayerPeer = peer;
        _networkIsHost = false;
        _networkConnected = false;
        _networkGameStarted = false;
        _networkStatus.Text = Simulation.Loc.Format("Connecting to {0}:{1}…", address, RequestedNetworkPort());
        RenderNetworkLobby();
    }

    private Nation RequestedNetworkNation()
    {
        var tag = _networkCountryTag.Text.Trim().ToUpperInvariant();
        return _campaign?.NationByTag(tag);
    }

    private void OnConnectedToNetworkServer()
    {
        _networkConnected = true;
        PlayStrategicCue("network_connected");
        var nation = RequestedNetworkNation();
        RpcId(1, nameof(ServerRegisterNetworkPlayer), _networkPlayerName.Text.Trim(), nation?.Tag ?? "");
        _networkStatus.Text = Tr("Connected. Waiting for the host to start the campaign.");
    }

    private void OnNetworkConnectionFailed()
    {
        PlayStrategicCue("action_denied");
        _networkStatus.Text = Tr("Connection failed. Check IPv4 address, UDP port and firewall.");
        ShutdownNetworkPeer();
    }

    private void OnNetworkServerDisconnected()
    {
        PlayStrategicCue("network_disconnected");
        _networkStatus.Text = Tr("Host disconnected.");
        ShutdownNetworkPeer();
        _networkLayer.Visible = true;
        _hudLayer.Visible = false;
    }

    private void OnNetworkPeerConnected(long peerId)
    {
        if (_networkIsHost) _networkStatus.Text = Simulation.Loc.Format("Peer {0} connected; waiting for country registration.", peerId);
    }

    private void OnNetworkPeerDisconnected(long peerId)
    {
        if (!_networkIsHost) return;
        _networkPeerNations.Remove(peerId);
        _networkNames.Remove(peerId);
        _networkReadyPeers.Clear();
        _networkRosterRevision++;
        RefreshNetworkRoster();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRegisterNetworkPlayer(string playerName, string requestedTag)
    {
        if (!_networkIsHost) return;
        var peerId = Multiplayer.GetRemoteSenderId();
        if (_networkGameStarted && _networkPeerNations.ContainsKey(peerId))
        {
            RpcId(peerId, nameof(ClientNetworkLobbyRefusal), "This campaign has already started.");
            return;
        }
        var requested = _campaign.NationByTag(requestedTag);
        if (requested is null || _networkPeerNations.Any(pair => pair.Key != peerId && pair.Value == requested.Id))
        {
            RpcId(peerId, nameof(ClientNetworkLobbyRefusal), "That country is occupied. Choose another country.");
            RefreshNetworkRoster();
            return;
        }
        var assigned = requested;
        if (_networkPeerNations.TryGetValue(peerId, out var previous) && previous == assigned.Id)
        {
            RpcId(peerId, nameof(ClientAssignedNetworkNation), assigned.Tag);
            RefreshNetworkRoster();
            return;
        }
        _networkPeerNations[peerId] = assigned.Id;
        _networkNames[peerId] = string.IsNullOrWhiteSpace(playerName)
            ? Simulation.Loc.Format("Player {0}", peerId)
            : playerName.Trim()[..Math.Min(32, playerName.Trim().Length)];
        _networkReadyPeers.Clear();
        _networkRosterRevision++;
        RpcId(peerId, nameof(ClientAssignedNetworkNation), assigned.Tag);
        var joiner = string.IsNullOrWhiteSpace(playerName)
            ? Simulation.Loc.Format("Peer {0}", peerId)
            : playerName;
        _networkStatus.Text = Simulation.Loc.Format("{0} joined as {1}.", joiner, assigned.Tag);
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
        PlayStrategicCue("country_selected");
        _networkStatus.Text = Simulation.Loc.Format("Registered as {0}. Waiting for host.", tag);
        RenderNetworkLobby();
    }

    private void RefreshNetworkRoster()
    {
        if (!_networkIsHost) return;
        RenderNetworkLobby();
        var ids = _networkPeerNations.Keys.OrderBy(id => id).ToArray();
        Rpc(nameof(ClientReceiveNetworkRoster), ids,
            ids.Select(id => _networkPeerNations[id]).ToArray(),
            ids.Select(id => _networkNames.GetValueOrDefault(id,
                Simulation.Loc.Format("Player {0}", id))).ToArray(),
            ids.Select(id => _networkReadyPeers.Contains(id) ? 1 : 0).ToArray(), _networkRosterRevision);
    }

    private void StartHostedNetworkCampaign()
    {
        if (!_networkIsHost || _networkGameStarted || _networkPeerNations.Count < 2 ||
            !_networkPeerNations.Keys.All(id => _networkReadyPeers.Contains(id))) return;
        _campaign.ConfigureSinglePlayer(_networkLocalNationId);
        foreach (var nationId in _networkPeerNations.Values.Where(id => id != _networkLocalNationId).Distinct())
            _campaign.AddHumanSeat(nationId);
        _networkGameStarted = true;
        AttachNetworkCommandTransport();
        _globe.CountrySelectionOnly = false;
        _networkLayer.Visible = false;
        BeginSession();
        PlayStrategicCue("campaign_start");
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
        var firstStart = !_networkGameStarted;
        AdoptNetworkCampaign(result.Campaign);
        _globe.CountrySelectionOnly = false;
        _networkGameStarted = true;
        _networkLayer.Visible = false;
        if (firstStart) PlayStrategicCue("campaign_start");
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
        _campaignSeed = campaign.Seed;
        _pickerScenarioEntry = null;
        if (_pickerSeed is not null) _pickerSeed.Text = _campaignSeed.ToString();
        var local = _campaign.NationById(_networkLocalNationId) ?? _campaign.Player;
        _campaign.SetPlayer(local.Id);
        _campaign.Changed += Refresh;
        _selectedNation = local;
        _selectedState = _campaign.States.FirstOrDefault(state => state.NationMapIndex == local.MapIndex);
        _pickerNation = local;
        _modApi.Bind(_campaign);
        // Flags are chosen against the world they fly in; left on the last one, a country born
        // in this one - a secession, a rising - has no flag, only an empty frame.
        BindFlags(_campaign);
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
                    Rpc(nameof(ClientApplyNetworkCommand), type, json, _campaign.ElapsedDays);
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
        if (!_networkPeerNations.TryGetValue(sender, out var nationId) || command.IssuerId != nationId)
        {
            // Somebody sent an order for a country they do not play. It is refused, and the
            // refusal is said out loud: a client whose order vanished used to go on believing
            // it had happened, which looks exactly like a desynchronisation and is not one.
            RpcId(sender, nameof(ClientNetworkCommandRefused), typeName);
            return;
        }
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientNetworkCommandRefused(string typeName)
    {
        if (_networkIsHost) return;
        var name = typeName ?? "order";
        var cut = name.LastIndexOf('.');
        if (cut >= 0 && cut < name.Length - 1) name = name[(cut + 1)..];
        _networkHudStatus.Text = Simulation.Loc.Format("REFUSED · the host did not accept your {0}", name);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientApplyNetworkCommand(string typeName, string json, int hostDay)
    {
        if (_networkIsHost || _campaign is null ||
            !NetworkCommandCodec.TryDecode(typeName, json, out var command)) return;

        // On the day it was given, or held until we get there. Applying it early is the
        // silent divergence this whole exchange exists to avoid.
        if (hostDay > _campaign.ElapsedDays)
        {
            _networkDeferred.Add((hostDay, command));
            return;
        }
        ApplyNetworkCommand(command);
    }

    private void ApplyNetworkCommand(DealOrForce.Simulation.ICampaignCommand command)
    {
        _applyingNetworkCommand = true;
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _applyingNetworkCommand = false;
    }

    /// <summary>
    /// Everything that was waiting for today. Held orders are applied in the order they
    /// arrived, which is the order the host applied them in.
    /// </summary>
    private void ApplyDeferredNetworkCommands()
    {
        if (_networkDeferred.Count == 0 || _campaign is null) return;
        for (var index = 0; index < _networkDeferred.Count; index++)
        {
            var waiting = _networkDeferred[index];
            if (waiting.Day > _campaign.ElapsedDays) continue;
            ApplyNetworkCommand(waiting.Command);
            _networkDeferred.RemoveAt(index);
            index--;
        }
    }

    private void NetworkProcess(double delta)
    {
        if (!_networkConnected || !_networkGameStarted || !_networkIsHost) return;
        _networkClockAccumulator += delta;
        if (_networkClockAccumulator < 0.50) return;
        _networkClockAccumulator = 0;
        Rpc(nameof(ClientReceiveNetworkClock), _campaign.ElapsedDays, _campaign.IsPaused, _speed,
            _campaign.TickHours);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientAdvanceNetworkDay(int hostElapsedDays)
    {
        if (_networkIsHost || _campaign is null || hostElapsedDays <= _campaign.ElapsedDays) return;

        // Hours, not days. A day stopped being the unit a tick advances in: at seventy-two
        // hours a step overshoots the day it was aiming at, and at six it takes four steps
        // to move the day counter at all - so a loop that counts days either runs one step
        // too many or spends its whole budget standing still.
        var target = hostElapsedDays * 24L;

        // A step at a time, up to a budget, so a client that is momentarily behind catches
        // up over the next few messages rather than freezing while it replays a month.
        var ticks = 0;
        var budget = Math.Max(1, (int)(8 * 24 / Math.Max(1, _campaign.TickHours)));
        _applyingNetworkCommand = true;
        while (_campaign.ElapsedHours < target && ticks < budget)
        {
            _campaign.Tick();
            // Once a day, for the same reason as the single-player loop: a hook named for a
            // day that fires on every step multiplies whatever a mod does in it by the number
            // of steps in a day, and the two loops must not disagree about that or a mod
            // behaves differently in multiplayer.
            if (_campaign.StepOpensNewDay) _modLoader.OnDayAdvanced(_campaign);
            ticks++;
            // Orders given on the day we have just reached, applied before the next one is
            // simulated - which is the whole of what lockstep means.
            _applyingNetworkCommand = false;
            ApplyDeferredNetworkCommands();
            _applyingNetworkCommand = true;
        }
        _applyingNetworkCommand = false;

        var behind = hostElapsedDays - _campaign.ElapsedDays;
        if (behind <= 0) return;
        if (behind < NetworkResyncGapDays)
        {
            _networkHudStatus.Text = Simulation.Loc.Format("CATCHING UP · {0:N0} day(s) behind the host", behind);
            return;
        }
        // Too far to replay, and every order arriving now would be applied to the wrong
        // world. Ask for the world itself instead - and say so, because a player who sees
        // nothing assumes nothing is wrong.
        _networkHudStatus.Text = Simulation.Loc.Format("RESYNCHRONISING · {0:N0} day(s) behind", behind);
        RequestNetworkResync($"{behind:N0} days behind");
    }

    /// <summary>
    /// Ask the host for the world again, at most once every few days.
    ///
    /// Rate-limited because the trigger conditions repeat: a client that is behind is still
    /// behind on the next message, and a snapshot of a world this size is not something to
    /// send several times a second while it arrives.
    /// </summary>
    private void RequestNetworkResync(string reason)
    {
        if (_networkIsHost || _campaign is null) return;
        if (_campaign.ElapsedDays - _networkResyncRequestedDay < NetworkChecksumEveryDays) return;
        _networkResyncRequestedDay = _campaign.ElapsedDays;
        RpcId(1, nameof(ServerRequestNetworkResync), reason ?? "");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestNetworkResync(string reason)
    {
        if (!_networkIsHost || !_networkGameStarted) return;
        var peer = Multiplayer.GetRemoteSenderId();
        if (!_networkPeerNations.ContainsKey(peer)) return;
        GD.Print($"[net] resynchronising peer {peer}: {reason}");
        SendNetworkSnapshot(peer);
    }

    /// <summary>
    /// What the host's world looks like, said out loud every few days.
    ///
    /// Without it a divergence is invisible until somebody notices the front is in two
    /// different places, by which time neither save can be trusted. With it the client
    /// finds out within ten days and fixes itself.
    /// </summary>
    private void BroadcastNetworkChecksum()
    {
        if (!_networkIsHost || !_networkGameStarted || _campaign is null) return;
        if (_campaign.ElapsedDays - _networkLastChecksumDay < NetworkChecksumEveryDays) return;
        _networkLastChecksumDay = _campaign.ElapsedDays;
        Rpc(nameof(ClientCheckNetworkSync), _campaign.ElapsedDays,
            CampaignFingerprint.Of(_campaign).ToString());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientCheckNetworkSync(int hostElapsedDays, string hostFingerprint)
    {
        if (_networkIsHost || _campaign is null) return;
        // Only comparable on the same day; a client still catching up is not diverged, and
        // telling it so would send it a snapshot every ten days for no reason.
        if (_campaign.ElapsedDays != hostElapsedDays) return;
        if (CampaignFingerprint.Of(_campaign).ToString() == hostFingerprint) return;
        _networkHudStatus.Text = Simulation.Loc.Format("DESYNCHRONISED on day {0:N0} · asking the host for the world", hostElapsedDays);
        RequestNetworkResync($"checksum mismatch on day {hostElapsedDays}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ClientReceiveNetworkClock(int elapsedDays, bool paused, int speed, int hostTickHours)
    {
        if (_networkIsHost || _campaign is null) return;
        // The host's step, not this player's. `hours_per_tick` is a preference when playing
        // alone and a rule when playing together: two worlds stepping different amounts do
        // different arithmetic, and lockstep survives none of it.
        if (hostTickHours > 0 && _campaign.Defines.HoursPerTick != hostTickHours)
        {
            _campaign.Defines.HoursPerTick = hostTickHours;
            GD.Print($"[net] adopting the host's step of {hostTickHours} hour(s)");
        }
        _campaign.IsPaused = paused;
        _speed = Math.Clamp(speed, 1, 5);
        _pause.Text = paused ? "▶" : "II";
        _networkHudStatus.Text = Simulation.Loc.Format("CLIENT · {0} · host day {1:N0}", _campaign.Player.Tag, elapsedDays);
    }

    private void UpdateNetworkHud()
    {
        if (_networkHud is null) return;
        _networkHud.Visible = _hudLayer.Visible && _networkConnected && _networkGameStarted;
        if (!_networkHud.Visible) return;
        _networkHudStatus.Text = _networkIsHost
            ? Simulation.Loc.Format("HOST · {0:N0} PLAYERS · UDP {1}",
                _networkPeerNations.Count, RequestedNetworkPort())
            : Simulation.Loc.Format("CLIENT · {0} · {1}",
                _campaign.Player.Tag, _networkAddress.Text.Trim());
    }

    private void CloseNetworkMultiplayer()
    {
        if (_networkConnected) PlayStrategicCue("network_disconnected");
        ShutdownNetworkPeer();
        _networkLayer.Visible = false;
        _globe.CountrySelectionOnly = false;
        ShowMainMenu();
    }

    private void ShutdownNetworkPeer()
    {
        if (_campaign is not null) _campaign.Commands.SubmitInterceptor = null;
        _networkPeer?.Close();
        _networkPeer = null;
        Multiplayer.MultiplayerPeer = null;
        _networkPeerNations.Clear();
        _networkNames.Clear();
        _networkReadyPeers.Clear();
        _networkDeferred.Clear();
        _networkIsHost = false;
        _networkConnected = false;
        _networkGameStarted = false;
        _networkLocalNationId = -1;
        if (_networkHud is not null) _networkHud.Visible = false;
        if (_networkStart is not null) _networkStart.Disabled = true;
        if (_networkCountries is not null) RenderNetworkLobby();
    }
}
