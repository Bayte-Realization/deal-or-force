using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DealOrForce.Modding;
using DealOrForce.Simulation;

namespace DealOrForce.BtaBridge;

/// <summary>Opt-in local sandbox adapter. Only the campaign thread touches simulation state.</summary>
public sealed class BtaBridgeMod : IGlobalityMod, IDisposable
{
    private BridgeServer _server;
    private Action<bool> _setAtmosphere;
    private string _session = "";
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly Dictionary<string, BridgeResult> _results = new();
    private byte[] _map = Array.Empty<byte>();
    private ProvinceTopology _topology;
    private double _heartbeat;
    private string _voxelClient = "";
    private long _voxelRevision = -1;
    private CrossoverVoxel[] _voxels = Array.Empty<CrossoverVoxel>();
    private bool _visualDirty = true;
    private bool _voxelEconomyPending;
    private CrossoverVoxel[] _baselineVoxels;

    public void OnFrame(ModContext context, double delta)
    {
        _heartbeat += delta;
        if (_heartbeat < .25 || _server is null) return;
        _heartbeat = 0;
        if (_server.SurfaceRequested && context.CaptureSurface?.Invoke() is { Length: > 8 } surface)
            _server.PublishSurface(surface);
        if (_server.HydrologyRequested && _server.HydrologyEmpty && context.CaptureHydrology?.Invoke() is { Length: > 8 } hydro)
            _server.PublishHydrology(hydro);
        if (_server.TakeVoxels() is { } frame && frame.session == _session &&
            (frame.client != _voxelClient || frame.revision > _voxelRevision))
        {
            if (_voxelRevision < 0) _baselineVoxels = frame.blocks;
            _voxelClient = frame.client; _voxelRevision = frame.revision;
            _voxels = frame.blocks; _visualDirty = true;
            if (!_voxelEconomyPending && context.Campaign is { } campaign &&
                !campaign.IsHotseat && campaign.Commands.SubmitInterceptor is null) {
                _voxelEconomyPending = true;
                campaign.Commands.Enqueue(new CrossoverSnapshotCommand(() => _voxels, () => _baselineVoxels, () => {
                    _voxelEconomyPending = false; _baselineVoxels = null;
                }));
            }
        }
        if (_visualDirty && context.PresentVoxels is not null) {
            context.PresentVoxels(_voxels); _visualDirty = false;
        }
        Pump(context.Campaign);
        context.PresentWorks?.Invoke(context.Campaign?.CrossoverWorks.Select(w => w.Copy()).ToArray() ?? Array.Empty<CrossoverWork>());
        Publish(context.Campaign);
    }

    public void OnLoad(ModContext context)
    {
        context.RegisterFrontendAction("launch", () => BtaLauncher.Launch(_server is not null));
        context.RegisterFrontendAction("status", () => Loc.Get(_server is null
            ? "BTA bridge is not configured. Run Tools/BtaBridge/setup.ps1 first." : "BTA bridge is ready for this local campaign."));
        var configPath = Environment.GetEnvironmentVariable("DOF_BTA_CONFIG") ??
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(BtaBridgeMod).Assembly.Location)!,
                "../../../../bridge.local.json"));
        if (!File.Exists(configPath)) { context.Log("BTA bridge not configured; run Tools/BtaBridge/setup.ps1."); return; }
        var config = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(configPath));
        if (config is null || config.token is not { Length: >= 32 } || config.port is < 1024 or > 65535)
            throw new InvalidDataException("BTA bridge configuration is invalid.");
        _server = new BridgeServer(config.port, config.token);
        _server.Start();
        _setAtmosphere = context.SetCrossoverPresentation;
        _setAtmosphere?.Invoke(true);
        context.Log($"BTA bridge listening on 127.0.0.1:{config.port}.");
    }

    public void Dispose()
    {
        _server?.Dispose(); _server = null;
        _setAtmosphere?.Invoke(false); _setAtmosphere = null;
    }

    public void OnCampaignCreated(ModContext context)
    {
        _session = Guid.NewGuid().ToString("N");
        // Settings travel with every native DoF save. Transport sessions may change
        // on reload, but the guest world's identity must survive that reload.
        if (context.Campaign is { } campaign &&
            (!campaign.Settings.TryGetValue("bta_world_id", out var worldId) ||
             !Guid.TryParseExact(worldId, "N", out _)))
            campaign.Settings["bta_world_id"] = Guid.NewGuid().ToString("N");
        _seen.Clear(); _seenOrder.Clear(); _results.Clear(); _topology = null;
        _voxelClient = ""; _voxelRevision = -1; _voxels = Array.Empty<CrossoverVoxel>(); _visualDirty = true;
        _voxelEconomyPending = false; _baselineVoxels = null;
        _server?.PublishHydrology(Array.Empty<byte>());
        _server?.PublishSurface(Array.Empty<byte>());
        _server?.DiscardPending();
        Publish(context.Campaign);
    }

    public void OnDayAdvanced(ModContext context)
    {
        if (_server is null) return;
        if (_server.HydrologyRequested && _server.HydrologyEmpty && context.CaptureHydrology?.Invoke() is { Length: > 8 } hydro)
            _server.PublishHydrology(hydro);
        if (_server.TakeVoxels() is { } frame && frame.session == _session &&
            (frame.client != _voxelClient || frame.revision > _voxelRevision))
        {
            if (_voxelRevision < 0) _baselineVoxels = frame.blocks;
            _voxelClient = frame.client; _voxelRevision = frame.revision;
            _voxels = frame.blocks; _visualDirty = true;
            if (!_voxelEconomyPending && context.Campaign is { } campaign &&
                !campaign.IsHotseat && campaign.Commands.SubmitInterceptor is null) {
                _voxelEconomyPending = true;
                campaign.Commands.Enqueue(new CrossoverSnapshotCommand(() => _voxels, () => _baselineVoxels, () => {
                    _voxelEconomyPending = false; _baselineVoxels = null;
                }));
            }
        }
        if (_visualDirty && context.PresentVoxels is not null) {
            context.PresentVoxels(_voxels); _visualDirty = false;
        }
        Pump(context.Campaign);
        context.PresentWorks?.Invoke(context.Campaign?.CrossoverWorks.Select(w => w.Copy()).ToArray() ?? Array.Empty<CrossoverWork>());
        Publish(context.Campaign);
    }

    // Kept public for the headless verification harness. Called only on the simulation thread.
    public void Pump(Campaign campaign)
    {
        if (_server is null || campaign is null) return;
        for (var count = 0; count < 64 && _server.TryTake(out var action); count++)
        {
            if (action.session != _session) continue;
            if (_seen.Contains(action.id)) continue;
            _seen.Add(action.id); _seenOrder.Enqueue(action.id);
            while (_seenOrder.Count > 4096) { var old = _seenOrder.Dequeue(); _seen.Remove(old); _results.Remove(old); }
            if (campaign.Commands.SubmitInterceptor is not null || campaign.IsHotseat)
            {
                _results[action.id] = new BridgeResult(action.id, "rejected", "Use a local single-player campaign.");
                continue;
            }
            _results[action.id] = new BridgeResult(action.id, "queued", "Waiting for the next campaign tick.");
            if (action.kind == "commission")
                campaign.Commands.Enqueue(new CrossoverCommissionCommand(action, () => _voxels, result => _results[action.id] = result));
            else campaign.Commands.Enqueue(new TricksterCommand(action, result => _results[action.id] = result));
        }
    }

    public void Publish(Campaign campaign)
    {
        if (_server is null || campaign is null) return;
        var map = campaign.ProvinceMap;
        if (map is not null && map != _topology)
        {
            _topology = map;
            _map = MakeMap(map);
            _server.PublishSurface(MakeFallbackSurface(campaign));
        }
        var provinces = map is null ? Array.Empty<int>() :
            map.ProvinceToNation.Select(value => (int)value).ToArray();
        var formations = campaign.Armies.Where(a => a.ProvinceId > 0 && map is not null &&
                a.ProvinceId <= map.ProvinceCount).Take(2048).Select(a =>
        {
            var cell = map!.ProvinceCenterCell[a.ProvinceId];
            return new { id = a.Id, name = a.Name, province = a.ProvinceId, nation = a.NationId,
                u = (cell % map.Width + 0.5) / map.Width,
                v = (cell / map.Width + 0.5) / map.Height,
                strength = a.Strength, organisation = a.Organisation, type = a.TemplateId };
        }).ToArray();
        var snapshot = new
        {
            version = 1, session = _session, updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            campaignId = campaign.Settings.GetValueOrDefault("bta_world_id", _session),
            seed = campaign.Seed, scenario = campaign.ScenarioId,
            day = campaign.ElapsedDays, hours = campaign.ElapsedHours, date = campaign.Date.ToString(), paused = campaign.IsPaused,
            water = map?.WaterByProvince.Select((wet, id) => (wet, id)).Where(p => p.wet).Select(p => p.id).ToArray() ?? Array.Empty<int>(),
            ready = map is not null, radius = 1024, centerY = 0, storageSliceHeight = 256,
            voxelRevision = _voxelRevision, voxelClient = _voxelClient, voxelCount = _voxels.Count(v => v.block > 0),
            nations = campaign.Nations.Select(n => new { id = n.Id, map = n.MapIndex, tag = n.Tag,
                name = n.Name, color = n.Color, stability = n.Stability, treasury = n.Treasury,
                population = n.Population, gdp = n.Gdp }).ToArray(),
            provinceStates = map?.ProvinceToState.Select(s => (int)s).ToArray() ?? Array.Empty<int>(),
            regions = campaign.States.Select(s => new { id = s.MapIndex, name = s.Name, population = s.Population,
                damage = s.WarDamage, industry = s.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry),
                hospital = s.Buildings.GetValueOrDefault(BuildingKind.Hospital),
                supply = s.Buildings.GetValueOrDefault(BuildingKind.Infrastructure) }).ToArray(),
            works = campaign.CrossoverWorks.Select(w => new { id = w.Id, province = w.Province, active = w.Active,
                name = BuildingCatalog.Get(w.Building).Name, type = w.Building.ToString(), x=w.X, y=w.Y, z=w.Z }).ToArray(),
            impacts = campaign.CrossoverImpacts.Select(i => new { hour=i.Hour, province=i.Province,
                kind=i.Kind, population=i.Population, gdp=i.Gdp, message=i.Message }).ToArray(),
            workCosts = new { workshop = BuildingCatalog.Get(BuildingKind.CivilianIndustry).Cost,
                hospital = BuildingCatalog.Get(BuildingKind.Hospital).Cost, supply = BuildingCatalog.Get(BuildingKind.Infrastructure).Cost },
            requiredBlocks = Campaign.CrossoverRequiredBlocks,
            owners = provinces, terrain = map?.TerrainByProvince.Select(t => t?.Id ?? "plains").ToArray() ?? Array.Empty<string>(),
            destroyed = campaign.DestroyedProvinces.OrderBy(p => p).ToArray(),
            capitals = campaign.Nations.Where(n => !string.IsNullOrWhiteSpace(n.CapitalName) &&
                float.IsFinite(n.CapitalLatitude) && float.IsFinite(n.CapitalLongitude)).Take(512).Select(n => new {
                    id = n.Id, name = n.CapitalName, nation = n.Id, color = n.Color,
                    u = (n.CapitalLongitude + 180d)/360d, v = (90d-n.CapitalLatitude)/180d }).ToArray(),
            armies = formations, armyCount = campaign.Armies.Count,
            results = _seenOrder.TakeLast(64).Where(_results.ContainsKey).Select(id => _results[id]).ToArray()
        };
        _server.Publish(JsonSerializer.SerializeToUtf8Bytes(snapshot), _map);
    }

    // A small political preview for headless tests. The running game replaces it with
    // pixels baked from GlobePresentation's actual shader, including terrain and oceans.
    public static byte[] MakeFallbackSurface(Campaign campaign)
    {
        var map = campaign.ProvinceMap;
        var colors = campaign.Nations.ToDictionary(n => n.MapIndex, n =>
            uint.TryParse(n.Color.TrimStart('#'), System.Globalization.NumberStyles.HexNumber,
                null, out var value) ? value : 0x6e7b8bu);
        const int width = 512, height = 256;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(Encoding.ASCII.GetBytes("DFT1")); writer.Write((ushort)width); writer.Write((ushort)height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = map.CellToProvince[Math.Min(map.Height - 1, y * map.Height / height) * map.Width +
                Math.Min(map.Width - 1, x * map.Width / width)];
            var rgb = p > 0 && !map.WaterByProvince[p] && colors.TryGetValue(map.ProvinceToNation[p], out var color)
                ? color : 0x0f4a6bu;
            writer.Write((byte)(rgb >> 16)); writer.Write((byte)(rgb >> 8)); writer.Write((byte)rgb);
            writer.Write((byte)255);
        }
        return buffer.ToArray();
    }

    public static byte[] MakeMap(ProvinceTopology map)
    {
        // Keep the authored tactical raster: downsampling can select a neighbour
        // at a province border even though the visible block has the right pose.
        var width = map.Width; var height = map.Height;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("DFB1")); writer.Write((ushort)width); writer.Write((ushort)height);
        for (var z = 0; z < height; z++)
        for (var x = 0; x < width; x++)
        {
            writer.Write(map.CellToProvince[z * width + x]);
        }
        return buffer.ToArray();
    }
}

public sealed class BridgeConfig
{
    public int port { get; set; } = 18764;
    public string token { get; set; } = "";
}

public sealed class BridgeAction
{
    public int version { get; set; }
    public string session { get; set; } = "";
    public string id { get; set; } = "";
    public string kind { get; set; } = "";
    public int province { get; set; }
    public int army { get; set; }
    public string projectType { get; set; } = "";
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
}

public sealed record BridgeResult(string id, string status, string message);

public sealed class CrossoverSnapshotCommand : ICampaignCommand
{
    private readonly Func<CrossoverVoxel[]> _latest, _baseline;
    private readonly Action _done;
    public int IssuerId => -1;
    public CrossoverSnapshotCommand(Func<CrossoverVoxel[]> latest, Func<CrossoverVoxel[]> baseline, Action done) {
        _latest=latest; _baseline=baseline; _done=done;
    }
    public void Apply(Campaign campaign) {
        try {
            if (_baseline() is { } baseline) campaign.ApplyCrossoverSnapshot(baseline,true);
            campaign.ApplyCrossoverSnapshot(_latest(),false);
        } finally { _done(); }
    }
}
public sealed class CrossoverCommissionCommand : ICampaignCommand
{
    private readonly BridgeAction _action;
    private readonly Func<CrossoverVoxel[]> _cells;
    private readonly Action<BridgeResult> _done;
    public int IssuerId => -1;
    public CrossoverCommissionCommand(BridgeAction action, Func<CrossoverVoxel[]> cells, Action<BridgeResult> done) {
        _action=action; _cells=cells; _done=done;
    }
    public void Apply(Campaign campaign) {
        var reason=campaign.RegisterCrossoverWork(_action.id,_action.projectType,_action.province,
            _action.x,_action.y,_action.z,_cells());
        _done(new(_action.id,reason.Length==0?"applied":"rejected",reason.Length==0 ?
            campaign.CrossoverImpacts.Last().Message : reason));
    }
}
public sealed class VoxelFrame
{
    public int version { get; set; }
    public string session { get; set; } = "";
    public string client { get; set; } = "";
    public long revision { get; set; }
    public CrossoverVoxel[] blocks { get; set; } = Array.Empty<CrossoverVoxel>();
    public bool Valid() => version == 1 && session.Length == 32 && client.Length == 32 && revision >= 0 &&
        blocks is { Length: <= 16384 } &&
        blocks.All(v => v is not null && Math.Abs((long)v.x) <= 1600 && Math.Abs((long)v.y) <= 1600 &&
            Math.Abs((long)v.z) <= 1600 && v.block is >= 0 and <= 65535 && v.metadata is >= 0 and <= 255 &&
            v.color is >= 0 and <= 0xffffff && ValidPose(v.pose) &&
            (v.surface is null ? !v.excavated : CrossoverSurface.ValidFlat(v))) &&
        blocks.Select(v => (v.x,v.y,v.z)).Distinct().Count() == blocks.Length;
    private static bool ValidPose(float[] pose) => pose is null ||
        (pose.Length == 7 && pose.All(float.IsFinite) &&
        Math.Abs(pose[0]) <= 1600 && Math.Abs(pose[1]) <= 1600 && Math.Abs(pose[2]) <= 1600 &&
        Math.Abs(pose.Skip(3).Sum(v => (double)v*v)-1) < .002);
}


public sealed class TricksterCommand : ICampaignCommand
{
    private readonly BridgeAction _action;
    private readonly Action<BridgeResult> _completed;
    public int IssuerId => -1; // Explicit sandbox intervention, independent of the selected country.
    public TricksterCommand(BridgeAction action, Action<BridgeResult> completed) { _action = action; _completed = completed; }

    public void Apply(Campaign campaign)
    {
        var map = campaign.ProvinceMap;
        if (map is null || _action.province <= 0 || _action.province > map.ProvinceCount)
        { Complete("rejected", "Province no longer exists."); return; }
        if (_action.kind == "hit_army")
        {
            var army = campaign.Armies.Find(a => a.Id == _action.army && a.ProvinceId == _action.province);
            if (army is null) { Complete("rejected", "Division moved or disappeared."); return; }
            army.Strength = Math.Max(.01f, army.Strength - .10f);
            army.Organisation = Math.Max(0f, army.Organisation - .20f);
            campaign.MarkStrategicChange();
            Complete("applied", $"Hit {army.Name}: strength {army.Strength:P0}, organisation {army.Organisation:P0}.");
            return;
        }
        var state = campaign.States.Find(s => s.MapIndex == map.ProvinceToState[_action.province]);
        if (state is null) { Complete("rejected", "No region at this location."); return; }
        var nation = campaign.Nations.Find(n => n.MapIndex == map.ProvinceToNation[_action.province]);
        if (_action.kind == "break_block")
        {
            var destroyed = campaign.DestroyProvinceTerrain(_action.province);
            Complete(destroyed ? "applied" : "rejected", destroyed ?
                $"Destroyed province {_action.province}: terrain, population, production and stationed divisions lost." :
                "Terrain is already gone or is water.");
        }
        else if (_action.kind == "aid")
        {
            state.WarDamage = Math.Clamp(state.WarDamage - .05f, 0f, 1f);
            if (nation is not null) nation.Stability = Math.Clamp(nation.Stability + .01f, 0f, 1f);
            Complete("applied", $"Helped {nation?.Name ?? state.Name}: reconstruction and stability.");
        }
        else if (_action.kind == "sabotage" && nation is not null && !campaign.DestroyedProvinces.Contains(_action.province))
        {
            state.WarDamage = Math.Clamp(state.WarDamage + .1f, 0f, 1f);
            nation.Stability = Math.Clamp(nation.Stability - .02f, 0f, 1f);
            nation.Treasury *= .98f;
            Complete("applied", $"Orbital sabotage in {nation.Name}: damage, instability and lost treasury.");
        }
        else { Complete("rejected", "Unknown action or vanished land."); return; }
        campaign.MarkStrategicChange();
    }

    private void Complete(string status, string message) => _completed?.Invoke(new BridgeResult(_action.id, status, message));
}

/// <summary>Bounded localhost HTTP transport; this thread never reads live campaign objects.</summary>
public sealed class BridgeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _token;
    private readonly ConcurrentQueue<BridgeAction> _pending = new();
    private byte[] _state = Encoding.UTF8.GetBytes("{\"version\":1,\"ready\":false}");
    private byte[] _map = Array.Empty<byte>();
    private byte[] _hydrology = Array.Empty<byte>();
    private VoxelFrame _incomingVoxels;
    public bool HydrologyRequested { get; private set; }
    public bool HydrologyEmpty => Volatile.Read(ref _hydrology).Length == 0;
    public void PublishHydrology(byte[] bytes) => Volatile.Write(ref _hydrology, bytes);
    public VoxelFrame TakeVoxels() => Interlocked.Exchange(ref _incomingVoxels, null);
    private sealed record SurfaceFrame(byte[] Pixels, string ETag);
    private SurfaceFrame _surface = new(Array.Empty<byte>(), "\"0\"");
    private long _surfaceVersion;
    public bool SurfaceRequested { get; private set; }
    public void PublishSurface(byte[] bytes)
    {
        if (ReferenceEquals(Volatile.Read(ref _surface).Pixels, bytes)) return;
        Volatile.Write(ref _surface, new SurfaceFrame(bytes, $"\"{Interlocked.Increment(ref _surfaceVersion)}\""));
    }
    private int _count;
    private int _started;

    public BridgeServer(int port, string token)
    {
        _token = token;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _listener.Start();
        _ = Task.Run(Loop);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public void Publish(byte[] state, byte[] map)
    {
        Volatile.Write(ref _map, map);
        Volatile.Write(ref _state, state);
    }

    public bool TryTake(out BridgeAction action)
    {
        if (!_pending.TryDequeue(out action)) return false;
        Interlocked.Decrement(ref _count); return true;
    }

    public void DiscardPending() { while (TryTake(out _)) { } TakeVoxels(); }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext request;
            try { request = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            // Slow clients must not starve a snapshot request.
            _ = Handle(request);
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var request = context.Request; var response = context.Response;
            if (request.Headers["X-DealForce-Key"] != _token) { response.StatusCode = 403; return; }
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/state")
            {
                response.ContentType = "application/json"; await Write(response, Volatile.Read(ref _state)); return;
            }
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/surface")
            {
                SurfaceRequested = true; response.ContentType = "application/octet-stream";
                var surface = Volatile.Read(ref _surface);
                response.Headers["ETag"] = surface.ETag;
                if (request.Headers["If-None-Match"] == surface.ETag)
                { response.StatusCode = 304; return; }
                await Write(response, surface.Pixels); return;
            }
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/map")
            {
                response.ContentType = "application/octet-stream"; await Write(response, Volatile.Read(ref _map)); return;
            }
            if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == "/hydrology") {
                HydrologyRequested = true;
                response.ContentType = "application/octet-stream";
                await Write(response, Volatile.Read(ref _hydrology)); return;
            }
            if (request.HttpMethod == "POST" && request.Url?.AbsolutePath == "/structures") {
                if (request.ContentLength64 is < 1 or > 4000000) { response.StatusCode = 413; return; }
                using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var payload = new byte[request.ContentLength64];
                await request.InputStream.ReadExactlyAsync(payload, expiry.Token);
                var frame = JsonSerializer.Deserialize<VoxelFrame>(payload);
                if (frame is null || !frame.Valid()) { response.StatusCode = 400; return; }
                Interlocked.Exchange(ref _incomingVoxels,frame);
                response.StatusCode = 202; return;
            }
            if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/action") { response.StatusCode = 404; return; }
            if (request.ContentLength64 is < 1 or > 2048) { response.StatusCode = 413; return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var bytes = new byte[request.ContentLength64];
            await request.InputStream.ReadExactlyAsync(bytes, timeout.Token);
            var action = JsonSerializer.Deserialize<BridgeAction>(bytes);
            if (action is null || action.version != 1 || action.id is not { Length: >= 1 and <= 96 } ||
                action.session is not { Length: 32 } || action.kind is not ("break_block" or "hit_army" or "aid" or "sabotage" or "commission") ||
                (action.kind == "commission" && (!float.IsFinite(action.x) || !float.IsFinite(action.y) || !float.IsFinite(action.z) ||
                    Math.Abs(action.x)>1600 || Math.Abs(action.y)>1600 || Math.Abs(action.z)>1600 ||
                    action.projectType is not ("workshop" or "hospital" or "supply"))))
            { response.StatusCode = 400; return; }
            if (Interlocked.Increment(ref _count) > 256) { Interlocked.Decrement(ref _count); response.StatusCode = 429; return; }
            _pending.Enqueue(action);
            response.StatusCode = 202; await Write(response, Encoding.UTF8.GetBytes("{\"status\":\"accepted\"}"));
        }
        catch (JsonException) { context.Response.StatusCode = 400; }
        catch (OperationCanceledException) { context.Response.StatusCode = 408; }
        catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException) { }
        finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
    }

    private static async Task Write(HttpListenerResponse response, byte[] bytes)
    {
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private void OnProcessExit(object sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _listener.Close();
    }
}
