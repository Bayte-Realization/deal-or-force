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
/// Headquarters stand on a province (Movements.SeatOf), and several often share a city: Doha, Abu
/// Dhabi, London and Kyiv are where such offices are. Every headquarters within RowDegrees of
/// another is drawn in one row, fanned out side by side around the first of them, with each name
/// directly under its own medallion - two offices in two provinces of Kyiv were two rows drawn on
/// top of each other. Names remain visible with their headquarters, without camera-dependent
/// collision hiding or displacement.
///
/// A headquarters is the one place such a country is, so it is drawn at every distance, where
/// a capital is only drawn close up.
/// </summary>
public sealed partial class GlobePresentation
{
    /// <summary>A headquarters was clicked.</summary>
    public event Action<Simulation.Nation> MovementPicked;

    /// <summary>
    /// Whether a campaign is what the map is showing - asked of the game, which knows whether the
    /// main menu is up. The headquarters are part of a campaign's map; over the menu's backdrop
    /// globe they were names floating in front of the title. Unset, they show.
    /// </summary>
    public Func<bool> CampaignInView;

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
        /// <summary>How close the camera has to be before headquarters carry their names.</summary>
        private const float LabelDistance = 7.5f;
        /// <summary>Headquarters this close, in degrees of arc, share one row.</summary>
        private const float RowDegrees = 1.6f;
        private const float LabelLine = 40f;
        private const float LabelDrop = 80f;
        /// <summary>
        /// How far under its seat a row stands, in sprite pixels: the seat is a city, usually a
        /// capital, and a row drawn on it took the room of the city's own star and name - Kyiv
        /// lost its name to the Mejlis.
        /// </summary>
        private const float RowSink = 80f;
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
            /// <summary>Its name, anchored directly under its own medallion.</summary>
            public Label3D Label;
            /// <summary>How far under (positive) or over (negative) the medallion its name stands.</summary>
            public float Drop = LabelDrop;
            /// <summary>Where its row stands: the first headquarters of the row.</summary>
            public float Latitude;
            public float Longitude;
        }

        public MovementLayer(GlobePresentation globe) => _globe = globe;

        public MovementLayer() { }

        public override void _Process(double delta)
        {
            var campaign = _globe?._campaign;
            if (campaign is null || _globe.CampaignInView?.Invoke() == false)
            {
                Visible = false;
                _placed.Clear();
                _medallions.Clear();
                return;
            }
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
                    // FixedSize uses half the orthographic height on the flat map, versus
                    // camera depth on the globe. Match its screen scale in both projections.
                    marker.Node.Scale = Vector3.One * (_globe._flatMap && IsInstanceValid(camera)
                        ? 1f / Mathf.Tan(Mathf.DegToRad(camera.Fov * 0.5f)) : 1f);
                    marker.Node.Position = _globe.PresentationPoint(marker.Latitude, marker.Longitude,
                        SurfaceMarkerRadius * 1.002f);
                    // On the globe, only the side that faces the camera.
                    if (!_globe._flatMap && IsInstanceValid(camera))
                        shown = marker.Node.Position.Normalized().Dot(camera.GlobalPosition.Normalized()) > 0.05f;
                }
                if (marker.Node.Visible != shown) marker.Node.Visible = shown;
            }
            Spread(camera);
        }

        private static bool Standing(MovementState state) =>
            state is { Landless: true, Dissolved: false } && state.HeadquartersStateMapIndex >= 0;

        internal bool TryPickAt(Vector2 position, out Simulation.Nation best)
        {
            best = null;
            if (!IsVisibleInTree() || _globe?.PlanEraseMode == true) return false;
            var camera = _globe?._camera;
            if (!IsInstanceValid(camera)) return false;
            var bestDistance = PickRadius;
            var right = camera.GlobalBasis.X.Normalized();
            foreach (var marker in _markers)
            {
                if (!IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
                var at = marker.Node.GlobalPosition;
                if (camera.IsPositionBehind(at)) continue;
                var size = FixedWorldScale(camera, at) * marker.Node.Scale.X;
                var down = -camera.GlobalBasis.Y.Normalized();
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

            // Headquarters near each other are one row. Every name follows its own
            // medallion; their screen bounds reserve room in the city-label layout.
            var groups = Rows(Movements.Active(campaign).Where(nation => Standing(nation.Movement)));
            foreach (var group in groups)
            {
                // A translation must not rearrange offices sharing the same headquarters.
                var members = group.OrderBy(nation => nation.Tag, StringComparer.Ordinal).ToList();
                var anchor = group[0].Movement;
                for (var index = 0; index < members.Count; index++)
                {
                    var nation = members[index];
                    var offset = (index - (members.Count - 1) / 2f) * Spacing;
                    // In a row, every other name stands a line lower: two long names side by side -
                    // the Mejlis and Ichkeria in Kyiv - were one unreadable line. Both under the
                    // medallions, never over them, where the city's own star and name are.
                    var drop = members.Count > 1 && index % 2 == 1 ? LabelDrop + LabelLine * 0.95f : LabelDrop;
                    var colour = Color.FromHtml(string.IsNullOrWhiteSpace(nation.Color) ? "#808080" : nation.Color);
                    var node = new Node3D { Name = $"Headquarters_{nation.Tag}" };
                    var roundel = Roundel(nation);
                    node.AddChild(Medallion(_disc, PixelSize, Ui.Colour("surface.overlay", 0.94f), 20, offset));
                    node.AddChild(roundel is null
                        ? Medallion(_mark, PixelSize * 0.58f, Colors.White, 21, offset / 0.58f)
                        : Medallion(roundel, PixelSize, Colors.White, 21, offset));
                    node.AddChild(Medallion(_ring, PixelSize, colour.Lightened(0.2f), 22, offset));
                    var label = new Label3D
                    {
                        // The medallion already says it is a headquarters; the name says whose.
                        Text = nation.DisplayName,
                        FixedSize = true,
                        PixelSize = LabelPixelSize,
                        FontSize = 34,
                        OutlineSize = 12,
                        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                        NoDepthTest = true,
                        RenderPriority = 23,
                        Modulate = Ui.Colour("globe.label"),
                        OutlineModulate = Ui.Colour("globe.label_outline", 0.95f),
                        Offset = new Vector2(offset * PixelSize / LabelPixelSize, -drop - RowSink * PixelSize / LabelPixelSize)
                    };
                    node.AddChild(label);
                    AddChild(node);
                    _markers.Add(new Marker
                    {
                        Node = node, Nation = nation, Offset = offset, Label = label, Drop = drop,
                        Latitude = anchor.HeadquartersLatitude, Longitude = anchor.HeadquartersLongitude
                    });
                }
            }
        }

        /// <summary>
        /// The headquarters in rows: each joins the first row whose first headquarters is within
        /// RowDegrees of its own, in tag order so the rows are the same on every machine.
        /// </summary>
        private static List<List<Simulation.Nation>> Rows(IEnumerable<Simulation.Nation> seats)
        {
            var rows = new List<List<Simulation.Nation>>();
            foreach (var nation in seats.OrderBy(nation => nation.Tag, StringComparer.Ordinal))
            {
                var state = nation.Movement;
                var row = rows.FirstOrDefault(candidate =>
                    Arc(candidate[0].Movement, state) <= RowDegrees);
                if (row is null) rows.Add(new List<Simulation.Nation> { nation });
                else row.Add(nation);
            }
            return rows;
        }

        private static float Arc(MovementState a, MovementState b)
        {
            var la = Mathf.DegToRad(a.HeadquartersLatitude);
            var lb = Mathf.DegToRad(b.HeadquartersLatitude);
            var dl = Mathf.DegToRad(a.HeadquartersLongitude - b.HeadquartersLongitude);
            var cosine = MathF.Sin(la) * MathF.Sin(lb) + MathF.Cos(la) * MathF.Cos(lb) * MathF.Cos(dl);
            return Mathf.RadToDeg(MathF.Acos(Math.Clamp(cosine, -1f, 1f)));
        }

        // Never resolve a collision by displacing the name. An accumulated row offset made
        // names drift away during zoom/orbit and could identify the wrong nearby headquarters.
        // Keep one fixed anchor and visibility tied to the medallion. Collision hiding caused
        // names to blink while the camera moved and city-label rectangles were refreshed.
        private readonly List<Rect2> _placed = new();
        private readonly List<Rect2> _medallions = new();

        /// <summary>
        /// Where the headquarters' medallions were drawn last frame, on the screen: a headquarters
        /// is drawn at every distance and is the one place such a country is, so the city names
        /// are laid out around it rather than over it.
        /// </summary>
        internal void AddMedallions(List<Rect2> into) => into.AddRange(_medallions);

        /// <summary>Their anchored names reserve room before the city names are laid out.</summary>
        internal void AddNames(List<Rect2> into) => into.AddRange(_placed);

        private void Spread(Camera3D camera)
        {
            _placed.Clear();
            _medallions.Clear();
            if (!IsInstanceValid(camera)) return;
            var up = camera.GlobalBasis.Y.Normalized();
            foreach (var marker in _markers)
            {
                if (!IsInstanceValid(marker.Node) || !marker.Node.Visible || marker.Label is null)
                    continue;
                var at = marker.Node.GlobalPosition;
                // Names from the regional view in, medallions at every distance. From the whole
                // hemisphere thirty "HQ" names sat on top of each other across Europe and South
                // Asia; the flag in the ring says whose it is, and coming closer says the rest.
                // Tied to the zoom alone, never to what else is on the screen, so it cannot blink.
                var named = _globe.ViewDistance <= LabelDistance;
                if (marker.Label.Visible != named) marker.Label.Visible = named;
                if (camera.IsPositionBehind(at)) continue;
                // Measure exactly the projection-dependent scale used by FixedSize, including
                // the flat-map compensation. Depth is not the scale for an orthographic camera.
                var size = FixedWorldScale(camera, at) * marker.Node.Scale.X;
                var origin = camera.UnprojectPosition(at);
                var linePixels = MathF.Abs(camera.UnprojectPosition(at + up * (LabelLine * LabelPixelSize * size)).Y - origin.Y);
                if (linePixels < 0.5f) continue;
                var scale = linePixels / LabelLine;
                // The medallion: a 128-pixel sprite at its own pixel size, moved along its row.
                var sprite = scale * PixelSize / LabelPixelSize;
                var radius = 64f * sprite;
                var sunk = RowSink * sprite;
                _medallions.Add(new Rect2(origin.X + marker.Offset * sprite - radius, origin.Y + sunk - radius, radius * 2f, radius * 2f));
                if (!named) continue;
                var width = marker.Label.Text.Length * marker.Label.FontSize * 0.56f * scale;
                var box = new Rect2(origin.X + marker.Offset * sprite - width / 2f,
                    origin.Y + sunk + (marker.Drop - LabelLine / 2f) * scale, width, LabelLine * scale);
                _placed.Add(box);
            }
        }

        private static float FixedWorldScale(Camera3D camera, Vector3 at) =>
            camera.Projection == Camera3D.ProjectionType.Orthogonal
                ? 1f / MathF.Abs(camera.GetCameraProjection().Y.Y)
                : MathF.Max(0.001f, -(camera.GlobalTransform.AffineInverse() * at).Z);

        /// <summary>The complete flag or emblem, fitted inside the ring without cropping.</summary>
        private Texture2D Roundel(Simulation.Nation nation)
        {
            // The same flag the interface flies for it - resolved by the flag catalogue, with
            // the provisional flag for a movement no artist has drawn yet. Looking only for
            // `flag_<tag>` left every such movement as a headquarters icon beside the others' flags.
            var key = nation.Tag.ToLowerInvariant() + "|" + DealOrForceGame.FlagSpriteFor(nation.Tag);
            if (_roundels.TryGetValue(key, out var cached)) return cached;
            Texture2D made = null;
            if (DealOrForceGame.FlagTextureFor(nation.Tag) is { } flag)
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
            Offset = new Vector2(offset, -RowSink * PixelSize / pixelSize)
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
