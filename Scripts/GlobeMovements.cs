using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The headquarters of every movement without land (Movements): a round medallion with the
/// movement's own flag in it, ringed in its colour on a dark disc, and its name under it, on the
/// globe and the flat map, that a player can click to open its panel. Dark disc and light name
/// so both read on any country's colour - a ring in the movement's own green vanished on a green
/// map.
///
/// Headquarters stand on a province (Movements.SeatOf), and several often share one: Doha, Abu
/// Dhabi and London are where such offices are. Those are drawn as one row, fanned out side by
/// side around the city, with their names stacked underneath - never on top of each other.
///
/// A headquarters is the one place such a country is, so it is drawn at every distance, where
/// a capital is only drawn close up.
/// </summary>
public sealed partial class GlobePresentation
{
    /// <summary>A headquarters was clicked.</summary>
    public event Action<Simulation.Nation> MovementPicked;

    private MovementLayer _movementLayer;

    private void BuildMovementLayer(Node3D world)
    {
        _movementLayer = new MovementLayer(this) { Name = "MovementHeadquarters" };
        world.AddChild(_movementLayer);
    }

    private sealed partial class MovementLayer : Node3D
    {
        /// <summary>Sprite pixels, at the medallion's pixel size.</summary>
        private const float PixelSize = 0.00018f;
        private const float Spacing = 138f;
        private const float LabelPixelSize = 0.00021f;
        private const float LabelLine = 40f;
        private const float PickRadius = 20f;

        private readonly GlobePresentation _globe;
        private readonly List<Marker> _markers = new();
        private readonly Dictionary<string, Texture2D> _roundels = new();
        private Simulation.Campaign _campaign;
        private int _revision = -1;
        private bool _insignia;
        private Texture2D _ring;
        private Texture2D _disc;
        private Texture2D _mark;

        private sealed class Marker
        {
            public Node3D Node;
            public Simulation.Nation Nation;
            /// <summary>Its place in the row it shares a city with, in sprite pixels from the middle.</summary>
            public float Offset;
        }

        public MovementLayer(GlobePresentation globe) => _globe = globe;

        public MovementLayer() { }

        public override void _Process(double delta)
        {
            var campaign = _globe?._campaign;
            if (campaign is null) { Visible = false; return; }
            Visible = true;
            var insignia = SettingsCatalog.Flag("movement_flags");
            if (!ReferenceEquals(campaign, _campaign) || campaign.MovementRevision != _revision || insignia != _insignia)
                Rebuild(campaign, insignia);

            var camera = _globe._camera;
            foreach (var marker in _markers)
            {
                if (!IsInstanceValid(marker.Node)) continue;
                var state = marker.Nation.Movement;
                // A headquarters out on the street, or one that is no more, is nowhere on the map.
                var shown = Standing(state);
                if (shown)
                {
                    marker.Node.Position = _globe.PresentationPoint(state.HeadquartersLatitude, state.HeadquartersLongitude,
                        SurfaceMarkerRadius * 1.002f);
                    // On the globe, only the side that faces the camera.
                    if (!_globe._flatMap && IsInstanceValid(camera))
                        shown = marker.Node.Position.Normalized().Dot(camera.GlobalPosition.Normalized()) > 0.05f;
                }
                if (marker.Node.Visible != shown) marker.Node.Visible = shown;
            }
        }

        private static bool Standing(MovementState state) =>
            state is { Landless: true, Dissolved: false } && state.HeadquartersStateMapIndex >= 0;

        public override void _Input(InputEvent inputEvent)
        {
            if (inputEvent is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
            if (GetViewport().GuiGetHoveredControl() is not null) return;
            var camera = _globe?._camera;
            if (!IsInstanceValid(camera)) return;
            Simulation.Nation best = null;
            var bestDistance = PickRadius;
            var right = camera.GlobalBasis.X.Normalized();
            foreach (var marker in _markers)
            {
                if (!IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
                var at = marker.Node.GlobalPosition;
                if (camera.IsPositionBehind(at)) continue;
                // A fixed-size sprite is as large at any depth as it would be one unit away, so
                // its place in the row is that many pixels scaled by how far away it is.
                var depth = MathF.Max(0.001f, -(camera.GlobalTransform.AffineInverse() * at).Z);
                var centre = camera.UnprojectPosition(at + right * (marker.Offset * PixelSize * depth));
                var distance = centre.DistanceTo(click.Position);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = marker.Nation;
            }
            if (best is null) return;
            _globe.MovementPicked?.Invoke(best);
            GetViewport().SetInputAsHandled();
        }

        private void Rebuild(Simulation.Campaign campaign, bool insignia)
        {
            if (!ReferenceEquals(campaign, _campaign) || insignia != _insignia) _roundels.Clear();
            _campaign = campaign;
            _revision = campaign.MovementRevision;
            _insignia = insignia;
            foreach (var marker in _markers) if (IsInstanceValid(marker.Node)) marker.Node.QueueFree();
            _markers.Clear();
            _ring ??= CircleTexture(52f, 60f);
            _disc ??= CircleTexture(0f, 60f);
            _mark ??= GD.Load<Texture2D>(SpriteCatalog.TextureOf("icon_movement_headquarters",
                "res://game/gfx/interface/icons/icon_movement_headquarters.png"));

            // Headquarters sharing a province are one row: fanned out, names stacked below.
            var groups = Movements.Active(campaign)
                .Where(nation => Standing(nation.Movement))
                .GroupBy(nation => nation.Movement.HeadquartersProvinceId > 0
                    ? nation.Movement.HeadquartersProvinceId
                    : -nation.Movement.HeadquartersStateMapIndex)
                .ToList();
            foreach (var group in groups)
            {
                var members = group.OrderBy(nation => nation.DisplayName, StringComparer.CurrentCulture).ToList();
                for (var index = 0; index < members.Count; index++)
                {
                    var nation = members[index];
                    var offset = (index - (members.Count - 1) / 2f) * Spacing;
                    var colour = Color.FromHtml(string.IsNullOrWhiteSpace(nation.Color) ? "#808080" : nation.Color);
                    var node = new Node3D { Name = $"Headquarters_{nation.Tag}" };
                    var roundel = Roundel(nation);
                    node.AddChild(Medallion(_disc, PixelSize, Ui.Colour("surface.overlay", 0.94f), 20, offset));
                    node.AddChild(roundel is null
                        ? Medallion(_mark, PixelSize * 0.58f, Colors.White, 21, offset / 0.58f)
                        : Medallion(roundel, PixelSize, Colors.White, 21, offset));
                    node.AddChild(Medallion(_ring, PixelSize, colour.Lightened(0.2f), 22, offset));
                    node.AddChild(new Label3D
                    {
                        Text = Simulation.Loc.Format("HQ · {0}", nation.DisplayName),
                        FixedSize = true,
                        PixelSize = LabelPixelSize,
                        FontSize = 34,
                        OutlineSize = 12,
                        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                        NoDepthTest = true,
                        RenderPriority = 23,
                        Modulate = Ui.Colour("globe.label"),
                        OutlineModulate = Ui.Colour("globe.label_outline", 0.95f),
                        // One name per line under the whole row, in the row's order.
                        Offset = new Vector2(0f, -80f - index * LabelLine)
                    });
                    AddChild(node);
                    _markers.Add(new Marker { Node = node, Nation = nation, Offset = offset });
                }
            }
        }

        /// <summary>The complete flag or emblem, fitted inside the ring without cropping.</summary>
        private Texture2D Roundel(Simulation.Nation nation)
        {
            var key = nation.Tag.ToLowerInvariant();
            if (_roundels.TryGetValue(key, out var cached)) return cached;
            var sprite = _insignia && SpriteCatalog.Has($"flag_{key}_insignia") ? $"flag_{key}_insignia" : $"flag_{key}";
            Texture2D made = null;
            var path = SpriteCatalog.TextureOf(sprite, $"res://game/gfx/flags/{sprite}.png");
            if (!string.IsNullOrWhiteSpace(path) && ResourceLoader.Exists(path) && GD.Load<Texture2D>(path) is { } flag)
            {
                var image = flag.GetImage();
                if (image is not null)
                {
                    if (image.IsCompressed()) image.Decompress();
                    image.Convert(Image.Format.Rgba8);
                    // Fit the rectangle's diagonal, not just its width. Its corners then
                    // stay inside the ring even for square emblems or very tall mod flags.
                    // Never assume the important part of a flag is in its centre.
                    var radius = Mathf.Clamp(Ui.Metric("movement_marker.art_radius", 46f), 16f, 48f);
                    var diagonal = MathF.Sqrt((float)image.GetWidth() * image.GetWidth() +
                                              (float)image.GetHeight() * image.GetHeight());
                    var scale = radius * 2f / MathF.Max(1f, diagonal);
                    var width = Math.Max(1, (int)MathF.Floor(image.GetWidth() * scale));
                    var height = Math.Max(1, (int)MathF.Floor(image.GetHeight() * scale));
                    image.Resize(width, height, Image.Interpolation.Lanczos);
                    var canvas = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
                    canvas.Fill(Colors.Transparent);
                    canvas.BlitRect(image, new Rect2I(0, 0, width, height),
                        new Vector2I((128 - width) / 2, (128 - height) / 2));
                    made = ImageTexture.CreateFromImage(canvas);
                }
            }
            _roundels[key] = made;
            return made;
        }

        private static Sprite3D Medallion(Texture2D texture, float pixelSize, Color modulate, int priority, float offset) => new()
        {
            Texture = texture,
            PixelSize = pixelSize,
            FixedSize = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
            Shaded = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            NoDepthTest = true,
            RenderPriority = priority,
            Modulate = modulate,
            Offset = new Vector2(offset, 0f)
        };

        /// <summary>A white disc or ring with a soft edge, for a colour to tint.</summary>
        private static Texture2D CircleTexture(float inner, float outer)
        {
            const int size = 128;
            var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
            var centre = (size - 1) / 2f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var distance = new Vector2(x - centre, y - centre).Length();
                var alpha = Mathf.Clamp(outer - distance + 0.5f, 0f, 1f);
                if (inner > 0f) alpha = Mathf.Min(alpha, Mathf.Clamp(distance - inner + 0.5f, 0f, 1f));
                image.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
            return ImageTexture.CreateFromImage(image);
        }
    }
}
