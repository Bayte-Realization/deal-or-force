using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Landless movement headquarters medallions (globe + flat map): rows of at most
/// <see cref="MovementLayer.MaxInRow"/> flags plus a "+N" chip, shown only from regional zoom in.
/// Marker nodes and bounded roundel textures are diffed/reused in place, new roundel resizes are
/// frame-budgeted, and zoomed-out / Speed 7-8 frames avoid read-lock contention and scene churn.
/// </summary>
public sealed partial class GlobePresentation
{
    public event Action<Simulation.Nation> MovementPicked;
    public Func<bool> CampaignInView;
    private MovementLayer _movementLayer;

    private void BuildMovementLayer(Node3D world)
    {
        _movementLayer = new MovementLayer(this) { Name = "MovementHeadquarters" };
        world.AddChild(_movementLayer);
    }

    private sealed partial class MovementLayer : Node3D
    {
        private const float PixelSize = 0.00018f, Spacing = 138f, LabelPixelSize = 0.00021f;
        private const float LabelDistance = 4.2f, MedallionDistance = 7.5f, RowDegrees = 1.6f;
        private const float LabelLine = 40f, LabelDrop = 80f, RowSink = 130f, PickRadius = 20f;
        internal const int MaxInRow = 3;
        private const int MaxCachedRoundels = 256, MaxRoundelsPerFrame = 3;
        private const double RoundelBudgetMs = 1.5, FastSimSyncSeconds = 0.30, NormalSyncSeconds = 0.12;

        private readonly GlobePresentation _globe;
        private readonly List<Marker> _markers = new();
        private readonly Dictionary<string, Marker> _markersByTag = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenTags = new(StringComparer.Ordinal);
        private readonly List<string> _staleKeys = new();
        private readonly Dictionary<string, Texture2D> _roundels = new(StringComparer.Ordinal);
        private readonly List<Rect2> _placed = new();
        private readonly List<Rect2> _medallions = new();
        private Simulation.Campaign _campaign;
        private int _revision = -1, _flags;
        private bool _insignia, _pendingRoundels;
        private double _flagPoll, _sinceLastMovementSync, _syncCooldown;
        private Texture2D _ring, _disc, _mark;
        private ulong _lastCameraSyncProcessFrame = ulong.MaxValue;

        private sealed class Marker
        {
            public Node3D Node;
            public Simulation.Nation Nation;
            public Sprite3D Disc, Art, Ring, ChipDisc, ChipRing;
            public Label3D Label, ChipLabel;
            public float Offset, Drop = LabelDrop, Latitude, Longitude;
            public string RoundelKey = "";
            public bool IsStanding = true, RoundelResolved;
        }

        public MovementLayer(GlobePresentation globe) => _globe = globe;
        public MovementLayer() { }

        public override void _Process(double delta)
        {
            var campaign = _globe?._campaign;
            if (campaign is null || _globe.CampaignInView?.Invoke() == false || _globe.ViewDistance > MedallionDistance)
            { HideLayer(); return; }
            Visible = true;
            var camera = _globe._camera;
            var (fastSim, paused) = ReadClockState(campaign);
            _sinceLastMovementSync += delta;
            _syncCooldown -= delta;
            _flagPoll -= delta;

            var mustSync = _revision < 0 || !ReferenceEquals(campaign, _campaign);
            if (!mustSync && _syncCooldown > 0) { FinishCachedFrame(camera); return; }
            var requestWindow = mustSync || (!fastSim && _sinceLastMovementSync >= 0.25);
            if (!campaign.TryEnterRead(0, requestWindowOnMiss: requestWindow)) { FinishCachedFrame(camera); return; }
            _sinceLastMovementSync = 0;
            _syncCooldown = fastSim ? FastSimSyncSeconds : (paused ? 0.0 : NormalSyncSeconds);
            try { SyncUnderReadLock(campaign, camera, fastSim); }
            finally { campaign.ExitRead(); }
        }

        private void HideLayer()
        {
            if (Visible) { Visible = false; _syncCooldown = 0; }
            if (_placed.Count > 0) _placed.Clear();
            if (_medallions.Count > 0) _medallions.Clear();
        }

        private (bool FastSim, bool Paused) ReadClockState(Simulation.Campaign campaign)
        {
            var snap = _globe.CurrentRenderSnapshot();
            return snap is { Version: > 0 }
                ? (!snap.IsPaused && snap.Speed >= 7, snap.IsPaused)
                : (!campaign.IsPaused && campaign.Speed >= 7, campaign.IsPaused);
        }

        private void FinishCachedFrame(Camera3D camera)
        {
            if (_pendingRoundels) FlushPendingRoundels();
            var frame = Engine.GetProcessFrames();
            if (_lastCameraSyncProcessFrame == frame) return;
            _lastCameraSyncProcessFrame = frame;
            UpdateCachedMarkerTransforms(camera);
        }

        private void SyncUnderReadLock(Simulation.Campaign campaign, Camera3D camera, bool fastSim)
        {
            var insignia = SettingsCatalog.Flag("movement_flags");
            if (_flagPoll <= 0)
            {
                _flagPoll = fastSim ? 1.5 : 0.5;
                var flags = FlagCatalog.VisualStateHash(campaign);
                if (flags != _flags) { _flags = flags; _revision = -1; }
            }
            if (!ReferenceEquals(campaign, _campaign) || campaign.MovementRevision != _revision || insignia != _insignia)
                Rebuild(campaign, insignia);
            else if (_pendingRoundels)
                FlushPendingRoundels();

            foreach (var marker in _markers)
            {
                if (!IsInstanceValid(marker.Node)) continue;
                marker.IsStanding = Standing(marker.Nation.Movement);
                var shown = marker.IsStanding && _globe.ViewDistance <= MedallionDistance;
                if (shown) UpdateSingleMarkerTransform(marker, camera, ref shown);
                if (marker.Node.Visible != shown) marker.Node.Visible = shown;
            }
            _lastCameraSyncProcessFrame = Engine.GetProcessFrames();
            Spread(camera);
        }

        private void UpdateSingleMarkerTransform(Marker marker, Camera3D camera, ref bool shown)
        {
            marker.Node.Scale = Vector3.One * (_globe._flatMap && IsInstanceValid(camera)
                ? 1f / Mathf.Tan(Mathf.DegToRad(camera.Fov * 0.5f)) : 1f);
            marker.Node.Position = _globe.PresentationPoint(marker.Latitude, marker.Longitude,
                GlobePresentation.SurfaceMarkerRadius * 1.002f);
            if (_globe._flatMap || !IsInstanceValid(camera)) return;
            var worldPos = IsInsideTree() ? ToGlobal(marker.Node.Position) : marker.Node.Position;
            shown = !Simulation.MapOverlaySyncRules.IsBeyondGlobeHorizon(
                        GlobePresentation.ToNum(marker.Node.Position.Normalized()),
                        GlobePresentation.ToNum(camera.GlobalPosition.Normalized()),
                        _globe.CityHorizon()) &&
                    !camera.IsPositionBehind(worldPos);
        }

        private static bool Standing(MovementState state) =>
            state is { Landless: true, Dissolved: false } && state.HeadquartersStateMapIndex >= 0 &&
            !state.Id.StartsWith(Simulation.MovementCatalog.UndergroundPrefix, StringComparison.Ordinal);

        internal bool TryPickAt(Vector2 position, out Simulation.Nation best)
        {
            best = null;
            if (!IsVisibleInTree() || _globe?.PlanEraseMode == true) return false;
            var camera = _globe?._camera;
            if (!IsInstanceValid(camera)) return false;
            var bestDistance = PickRadius;
            var right = camera.GlobalBasis.X.Normalized();
            var down = -camera.GlobalBasis.Y.Normalized();
            foreach (var marker in _markers)
            {
                if (!IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
                var at = marker.Node.GlobalPosition;
                if (camera.IsPositionBehind(at)) continue;
                var size = FixedWorldScale(camera, at) * marker.Node.Scale.X;
                var centre = camera.UnprojectPosition(at + right * (marker.Offset * PixelSize * size) +
                                                      down * (RowSink * PixelSize * size));
                var distance = centre.DistanceTo(position);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = marker.Nation;
            }
            return best is not null;
        }

        private void Rebuild(Simulation.Campaign campaign, bool insignia)
        {
            _campaign = campaign;
            _revision = campaign.MovementRevision;
            _insignia = insignia;
            _ring ??= CircleTexture(52f, 60f);
            _disc ??= CircleTexture(0f, 60f);
            _mark ??= GD.Load<Texture2D>(SpriteCatalog.TextureOf("icon_movement_headquarters",
                "res://game/gfx/interface/icons/icon_movement_headquarters.png"));

            var groups = Rows(Movements.Active(campaign).Where(nation => Standing(nation.Movement)));
            _markers.Clear();
            _seenTags.Clear();
            _pendingRoundels = false;
            var budget = Stopwatch.StartNew();
            var builtRoundels = 0;

            foreach (var group in groups)
            {
                var members = group.OrderBy(nation => nation.Tag, StringComparer.Ordinal).ToList();
                var anchor = group[0].Movement;
                var drawn = Math.Min(members.Count, MaxInRow);
                var hidden = members.Count - drawn;
                var slots = drawn + (hidden > 0 ? 1 : 0);
                for (var index = 0; index < drawn; index++)
                {
                    var nation = members[index];
                    var offset = (index - (slots - 1) / 2f) * Spacing;
                    var drop = drawn > 1 && index % 2 == 1 ? LabelDrop + LabelLine * 0.95f : LabelDrop;
                    var marker = UpsertMarker(nation, offset, drop, anchor, index == drawn - 1 ? hidden : 0,
                        insignia, budget, ref builtRoundels);
                    _markers.Add(marker);
                    _seenTags.Add(nation.Tag);
                }
            }
            PruneStaleMarkers();
            TrimRoundelCacheIfNeeded();
        }

        private Marker UpsertMarker(
            Simulation.Nation nation, float offset, float drop, MovementState anchor,
            int hidden, bool insignia, Stopwatch budget, ref int builtRoundels)
        {
            if (!_markersByTag.TryGetValue(nation.Tag, out var marker) || !IsInstanceValid(marker.Node))
                marker = CreateMarker(nation);

            var colour = Color.FromHtml(string.IsNullOrWhiteSpace(nation.Color) ? "#808080" : nation.Color);
            marker.Nation = nation;
            marker.Offset = offset;
            marker.Drop = drop;
            marker.Latitude = anchor.HeadquartersLatitude;
            marker.Longitude = anchor.HeadquartersLongitude;
            marker.IsStanding = true;
            marker.Disc.Offset = marker.Ring.Offset = new Vector2(offset, -RowSink);
            marker.Ring.Modulate = colour.Lightened(0.2f);
            if (marker.Label.Text != nation.DisplayName) marker.Label.Text = nation.DisplayName;
            marker.Label.Offset = new Vector2(offset * PixelSize / LabelPixelSize, -drop - RowSink * PixelSize / LabelPixelSize);

            SyncMarkerRoundel(marker, nation.Tag, offset, insignia, budget, ref builtRoundels);
            SyncOverflowChip(marker, hidden, offset);
            return marker;
        }

        private void SyncMarkerRoundel(
            Marker marker, string tag, float offset, bool insignia, Stopwatch budget, ref int builtRoundels)
        {
            var key = BuildRoundelKey(tag, insignia, out var flag);
            if (marker.RoundelResolved && string.Equals(marker.RoundelKey, key, StringComparison.Ordinal))
            {
                var scale = ReferenceEquals(marker.Art.Texture, _mark) ? 0.58f : 1f;
                marker.Art.Offset = new Vector2(offset / scale, -RowSink / scale);
                return;
            }
            marker.RoundelKey = key;
            var ok = TryGetOrBuildRoundel(key, flag, budget, ref builtRoundels, out var roundel);
            ApplyArtSprite(marker, ok ? roundel : null, offset);
            marker.RoundelResolved = ok;
            if (!ok) _pendingRoundels = true;
        }

        private Marker CreateMarker(Simulation.Nation nation)
        {
            var node = new Node3D { Name = $"Headquarters_{nation.Tag}" };
            var disc = Medallion(_disc, PixelSize, Ui.Colour("surface.overlay", 0.94f), 20, 0f);
            var art = Medallion(_mark, PixelSize * 0.58f, Colors.White, 21, 0f);
            var ring = Medallion(_ring, PixelSize, Colors.White, 22, 0f);
            var label = CreateBillboardLabel(34, 12, 23);
            label.Text = nation.DisplayName;
            label.OutlineModulate = Ui.Colour("globe.label_outline", 0.95f);
            node.AddChild(disc); node.AddChild(art); node.AddChild(ring); node.AddChild(label);
            AddChild(node);
            return _markersByTag[nation.Tag] = new Marker { Node = node, Nation = nation, Disc = disc, Art = art, Ring = ring, Label = label };
        }

        private static Label3D CreateBillboardLabel(int fontSize, int outlineSize, int priority) => new()
        {
            FixedSize = true, PixelSize = LabelPixelSize, FontSize = fontSize, OutlineSize = outlineSize,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            RenderPriority = priority, Modulate = Ui.Colour("globe.label")
        };

        private void SyncOverflowChip(Marker marker, int hidden, float offset)
        {
            if (hidden <= 0) { if (marker.ChipDisc is not null) marker.ChipDisc.Visible = marker.ChipRing.Visible = marker.ChipLabel.Visible = false; return; }
            var chipOffset = offset + Spacing;
            if (marker.ChipDisc is null)
            {
                marker.ChipDisc = Medallion(_disc, PixelSize * 0.8f, Ui.Colour("surface.overlay", 0.94f), 20, chipOffset / 0.8f);
                marker.ChipRing = Medallion(_ring, PixelSize * 0.8f, Ui.Colour("surface.line"), 22, chipOffset / 0.8f);
                marker.ChipLabel = CreateBillboardLabel(40, 0, 24);
                marker.Node.AddChild(marker.ChipDisc); marker.Node.AddChild(marker.ChipRing); marker.Node.AddChild(marker.ChipLabel);
            }
            marker.ChipDisc.Visible = marker.ChipRing.Visible = marker.ChipLabel.Visible = true;
            marker.ChipDisc.Offset = marker.ChipRing.Offset = new Vector2(chipOffset / 0.8f, -RowSink / 0.8f);
            var text = "+" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (marker.ChipLabel.Text != text) marker.ChipLabel.Text = text;
            marker.ChipLabel.Offset = new Vector2(chipOffset * PixelSize / LabelPixelSize, -RowSink * PixelSize / LabelPixelSize);
        }

        private void PruneStaleMarkers()
        {
            _staleKeys.Clear();
            foreach (var kv in _markersByTag) if (!_seenTags.Contains(kv.Key)) _staleKeys.Add(kv.Key);
            for (var i = 0; i < _staleKeys.Count; i++)
                if (_markersByTag.Remove(_staleKeys[i], out var old) && IsInstanceValid(old.Node)) old.Node.QueueFree();
        }

        private void FlushPendingRoundels()
        {
            var budget = Stopwatch.StartNew();
            var built = 0;
            var remaining = false;
            for (var i = 0; i < _markers.Count; i++)
            {
                var marker = _markers[i];
                if (marker.RoundelResolved || !IsInstanceValid(marker.Node)) continue;
                var key = BuildRoundelKey(marker.Nation.Tag, _insignia, out var flag);
                marker.RoundelKey = key;
                if (TryGetOrBuildRoundel(key, flag, budget, ref built, out var roundel)) { ApplyArtSprite(marker, roundel, marker.Offset); marker.RoundelResolved = true; }
                else remaining = true;
            }
            _pendingRoundels = remaining;
            TrimRoundelCacheIfNeeded();
        }

        private void ApplyArtSprite(Marker marker, Texture2D roundel, float offset)
        {
            var scale = roundel is null ? 0.58f : 1f;
            marker.Art.Texture = roundel ?? _mark;
            marker.Art.PixelSize = PixelSize * scale;
            marker.Art.Offset = new Vector2(offset / scale, -RowSink / scale);
        }

        private static List<List<Simulation.Nation>> Rows(IEnumerable<Simulation.Nation> seats)
        {
            var rows = new List<List<Simulation.Nation>>();
            foreach (var nation in seats.OrderBy(nation => nation.Tag, StringComparer.Ordinal))
            {
                var state = nation.Movement;
                var row = rows.FirstOrDefault(candidate => Arc(candidate[0].Movement, state) <= RowDegrees);
                if (row is null) rows.Add(new List<Simulation.Nation> { nation }); else row.Add(nation);
            }
            return rows;
        }

        private static float Arc(MovementState a, MovementState b)
        {
            var la = Mathf.DegToRad(a.HeadquartersLatitude); var lb = Mathf.DegToRad(b.HeadquartersLatitude);
            var dl = Mathf.DegToRad(a.HeadquartersLongitude - b.HeadquartersLongitude);
            return Mathf.RadToDeg(MathF.Acos(Math.Clamp(MathF.Sin(la) * MathF.Sin(lb) + MathF.Cos(la) * MathF.Cos(lb) * MathF.Cos(dl), -1f, 1f)));
        }

        internal void AddMedallions(List<Rect2> into) => into.AddRange(_medallions);
        internal void AddNames(List<Rect2> into) => into.AddRange(_placed);

        private void Spread(Camera3D camera)
        {
            _placed.Clear(); _medallions.Clear();
            if (!IsInstanceValid(camera)) return;
            var up = camera.GlobalBasis.Y.Normalized();
            var named = _globe.ViewDistance <= LabelDistance;
            foreach (var marker in _markers)
            {
                if (!IsInstanceValid(marker.Node) || !marker.Node.Visible || marker.Label is null) continue;
                var at = marker.Node.GlobalPosition;
                if (marker.Label.Visible != named) marker.Label.Visible = named;
                if (camera.IsPositionBehind(at)) continue;
                var size = FixedWorldScale(camera, at) * marker.Node.Scale.X;
                var origin = camera.UnprojectPosition(at);
                var linePixels = MathF.Abs(camera.UnprojectPosition(at + up * (LabelLine * LabelPixelSize * size)).Y - origin.Y);
                if (linePixels < 0.5f) continue;
                var scale = linePixels / LabelLine;
                var sprite = scale * PixelSize / LabelPixelSize;
                var radius = 64f * sprite; var sunk = RowSink * sprite;
                _medallions.Add(new Rect2(origin.X + marker.Offset * sprite - radius, origin.Y + sunk - radius, radius * 2f, radius * 2f));
                if (!named) continue;
                var width = marker.Label.Text.Length * marker.Label.FontSize * 0.56f * scale;
                _placed.Add(new Rect2(origin.X + marker.Offset * sprite - width / 2f, origin.Y + sunk + (marker.Drop - LabelLine / 2f) * scale, width, LabelLine * scale));
            }
        }

        private static float FixedWorldScale(Camera3D camera, Vector3 at) =>
            camera.Projection == Camera3D.ProjectionType.Orthogonal
                ? 1f / MathF.Abs(camera.GetCameraProjection().Y.Y) : MathF.Max(0.001f, -(camera.GlobalTransform.AffineInverse() * at).Z);

        private static string BuildRoundelKey(string tag, bool insignia, out Texture2D flag)
        {
            flag = DealOrForceGame.FlagTextureFor(tag);
            var radiusPx = Mathf.RoundToInt(Mathf.Clamp(Ui.Metric("movement_marker.art_radius", 46f), 16f, 48f));
            return $"{tag.ToLowerInvariant()}|{DealOrForceGame.FlagSpriteFor(tag)}|{flag?.GetInstanceId() ?? 0}|{radiusPx}|{(insignia ? '1' : '0')}";
        }

        private bool TryGetOrBuildRoundel(string key, Texture2D flag, Stopwatch budget, ref int builtRoundels, out Texture2D texture)
        {
            if (_roundels.TryGetValue(key, out texture)) return true;
            if (flag is null) { _roundels[key] = null; texture = null; return true; }
            if (!_globe._campaignEntryFlushActive && (builtRoundels >= MaxRoundelsPerFrame || (builtRoundels > 0 && budget.Elapsed.TotalMilliseconds >= RoundelBudgetMs)))
            { texture = null; return false; }
            builtRoundels++;
            texture = _roundels[key] = BuildRoundelTexture(flag);
            return true;
        }

        private static Texture2D BuildRoundelTexture(Texture2D flag)
        {
            using var image = flag.GetImage();
            if (image is null) return null;
            if (image.IsCompressed()) image.Decompress();
            image.Convert(Image.Format.Rgba8);
            var radius = Mathf.Clamp(Ui.Metric("movement_marker.art_radius", 46f), 16f, 48f);
            var diagonal = MathF.Sqrt((float)image.GetWidth() * image.GetWidth() + (float)image.GetHeight() * image.GetHeight());
            var scale = radius * 2f / MathF.Max(1f, diagonal);
            var width = Math.Max(1, (int)MathF.Floor(image.GetWidth() * scale));
            var height = Math.Max(1, (int)MathF.Floor(image.GetHeight() * scale));
            image.Resize(width, height, Image.Interpolation.Lanczos);
            using var canvas = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
            canvas.Fill(Colors.Transparent);
            canvas.BlitRect(image, new Rect2I(0, 0, width, height), new Vector2I((128 - width) / 2, (128 - height) / 2));
            canvas.GenerateMipmaps();
            return ImageTexture.CreateFromImage(canvas);
        }

        private void TrimRoundelCacheIfNeeded()
        {
            if (_roundels.Count <= MaxCachedRoundels) return;
            _staleKeys.Clear();
            foreach (var key in _roundels.Keys)
            {
                var inUse = false;
                for (var i = 0; i < _markers.Count; i++) if (string.Equals(_markers[i].RoundelKey, key, StringComparison.Ordinal)) { inUse = true; break; }
                if (!inUse) _staleKeys.Add(key);
            }
            for (var i = 0; i < _staleKeys.Count; i++) _roundels.Remove(_staleKeys[i]);
            if (_roundels.Count > MaxCachedRoundels) _roundels.Clear();
        }

        private static Sprite3D Medallion(Texture2D texture, float pixelSize, Color modulate, int priority, float offset) => new()
        {
            Texture = texture, PixelSize = pixelSize, FixedSize = true, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            AlphaCut = SpriteBase3D.AlphaCutMode.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            Shaded = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, NoDepthTest = true,
            RenderPriority = priority, Modulate = modulate, Offset = new Vector2(offset, -RowSink * PixelSize / pixelSize)
        };

        private static Texture2D CircleTexture(float inner, float outer)
        {
            const int size = 128;
            using var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            var centre = (size - 1) / 2f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2(x - centre, y - centre).Length();
                var a = Mathf.Clamp(outer - d + 0.5f, 0f, 1f);
                if (inner > 0f) a = Mathf.Min(a, Mathf.Clamp(d - inner + 0.5f, 0f, 1f));
                image.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            image.GenerateMipmaps();
            return ImageTexture.CreateFromImage(image);
        }
    }
}
