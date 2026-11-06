using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private static readonly Dictionary<string, Texture2D> PortraitCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> PortraitCacheOrder = new();

    private static Texture2D RememberPortrait(string key, Texture2D texture)
    {
        // Centuries of new politicians must not retain centuries of GPU textures.
        // Controls still showing an evicted texture keep their own reference to it.
        while (PortraitCache.Count >= 256 && PortraitCacheOrder.TryDequeue(out var oldest)) PortraitCache.Remove(oldest);
        PortraitCache[key] = texture; PortraitCacheOrder.Enqueue(key);
        return texture;
    }

    /// <summary>
    /// Builds a classic Grand Strategy framed portrait for a ruler or head of government,
    /// using a mod-resolved photo or a single anonymous silhouette, never an invented face.
    /// </summary>
    private Control BuildLeaderPortraitBadge(LeaderProfile leader, string nationColorHex, int width = 62, int height = 76)
    {
        var frame = new PanelContainer
        {
            CustomMinimumSize = new Vector2(width, height),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Pass
        };
        var frameStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("surface.inset"),
            BorderColor = Ui.Colour("text.accent"),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 2,
            ContentMarginRight = 2,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        frame.AddThemeStyleboxOverride("panel", frameStyle);

        var rect = new TextureRect
        {
            Texture = LeaderPortraitTexture(leader, nationColorHex),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            CustomMinimumSize = new Vector2(width - 4, height - 4),
            MouseFilter = MouseFilterEnum.Ignore
        };
        frame.AddChild(rect);
        return frame;
    }

    private static bool _portraitCatalogueWatching;

    private static void ResetLeaderPortraitCache()
    { PortraitCache.Clear(); PortraitCacheOrder.Clear(); }

    private static Texture2D LeaderPortraitTexture(LeaderProfile leader, string nationColorHex)
    {
        if (!_portraitCatalogueWatching)
        {
            _portraitCatalogueWatching = true;
            SpriteCatalog.Changed += ResetLeaderPortraitCache;
        }
        var slug = SlugLeaderName(leader.Name);
        // File-name sprites and explicit mod declarations share the existing gfx overlay.
        var path = string.IsNullOrEmpty(leader.PortraitId) ? "" : SpriteCatalog.TextureOf(leader.PortraitId);
        if (string.IsNullOrEmpty(path)) path = SpriteCatalog.TextureOf("leader_" + slug);
        var background = Ui.Colour("portrait.anonymous_background");
        var foreground = Ui.Colour("portrait.anonymous_foreground");
        var anonymousKey = "anonymous:" + background.ToHtml() + ":" + foreground.ToHtml();
        var key = string.IsNullOrEmpty(path) ? anonymousKey + ":" + slug + ":" + leader.PortraitId : "photo:" + path;
        if (PortraitCache.TryGetValue(key, out var cached)) return cached;
        if (string.IsNullOrEmpty(path))
        {
            var legacy = $"res://game/gfx/leaders/leader_{slug}.png";
            if (Godot.FileAccess.FileExists(legacy) || ResourceLoader.Exists(legacy)) path = legacy;
        }
        if (!string.IsNullOrEmpty(path) && LoadLeaderPhoto(path) is { } photo)
            return RememberPortrait(key, photo);

        if (!PortraitCache.TryGetValue(anonymousKey, out var anonymous))
        {
            var silhouettePath = SpriteCatalog.TextureOf("leader_anonymous");
            if (string.IsNullOrEmpty(silhouettePath)) silhouettePath = "res://game/gfx/leaders/leader_anonymous.png";
            if (LoadLeaderPhoto(silhouettePath) is { } silhouette)
            {
                anonymous = RememberPortrait(anonymousKey, silhouette);
            }
            else
            {
                // The same anonymous circle-and-shoulders silhouette for every unknown person.
                // No invented face, ethnicity, gender, uniform or nationality-dependent variation.
                const int w = 96, h = 112;
                var bytes = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var dx = x + .5f - 48f; var dy = y + .5f - 36f;
                    var head = MathF.Sqrt(dx * dx + dy * dy) - 22f;
                    var bodyY = y + .5f - 108f;
                    var body = (MathF.Sqrt(dx * dx / (45f * 45f) + bodyY * bodyY / (53f * 53f)) - 1f) * 45f;
                    var coverage = Math.Clamp(.5f - MathF.Min(head, body), 0f, 1f);
                    var c = background.Lerp(foreground, coverage); var at = (y * w + x) * 4;
                    bytes[at] = (byte)(Math.Clamp(c.R, 0, 1) * 255);
                    bytes[at + 1] = (byte)(Math.Clamp(c.G, 0, 1) * 255);
                    bytes[at + 2] = (byte)(Math.Clamp(c.B, 0, 1) * 255);
                    bytes[at + 3] = 255;
                }
                using var image = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
                anonymous = RememberPortrait(anonymousKey, ImageTexture.CreateFromImage(image));
            }
        }
        return RememberPortrait(key, anonymous);
    }

    private static Texture2D LoadLeaderPhoto(string path)
    {
        // Local loose images work without editor import; packed imports still work in exports.
        if (path.Contains("://", StringComparison.Ordinal) &&
            !path.StartsWith("res://", StringComparison.Ordinal) && !path.StartsWith("user://", StringComparison.Ordinal)) return null;
        try
        {
            if (path.StartsWith("res://", StringComparison.Ordinal))
            {
                if (ResourceLoader.Exists(path)) return GD.Load<Texture2D>(path);
                // Game data/mod PNGs are loose files in a release too. A missing
                // .import sidecar is not a missing portrait and needs no editor.
                path = ProjectSettings.GlobalizePath(path);
            }
            if (!Godot.FileAccess.FileExists(path))
                return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
            using (var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read))
                if (file is null || file.GetLength() > 32UL * 1024 * 1024) return null;
            using var image = new Image();
            if (image.Load(path) != Error.Ok || image.IsEmpty()) return null;
            if (image.IsCompressed() && image.Decompress() != Error.Ok) return null;
            var size = image.GetSize();
            if (size.X > 512 || size.Y > 512)
            {
                var scale = 512f / Math.Max(size.X, size.Y);
                image.Resize(Math.Max(1, (int)(size.X * scale)), Math.Max(1, (int)(size.Y * scale)), Image.Interpolation.Lanczos);
            }
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception error)
        {
            DiagnosticLogs.Write("errors", "LEADER_PHOTO_REJECTED path=" + path + " error=" + error.Message);
            return null;
        }
    }

    private static string SlugLeaderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "default";
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Builds a compact HOI4/Vic3/EU5-inspired Grand Strategy header block for a country,
    /// showing its flag, formal title, Head of State & Head of Government portraits, traits,
    /// and a visual ideology/stability summary bar.
    /// </summary>
    private Control BuildGrandStrategyLeaderHeader(Nation nation, bool compact = false)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 6);
        if (_campaign is null || nation is null) return box;

        var stateLeader = Rulers.HeadOfState(_campaign, nation);
        var dual = Rulers.HasDualExecutive(nation);
        var govLeader = dual ? Rulers.HeadOfGovernment(_campaign, nation) : default;
        var stateTrait = Rulers.TraitOf(stateLeader.TraitId);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 10);
        box.AddChild(row);

        var portrait = BuildLeaderPortraitBadge(stateLeader, nation.Color, compact ? 52 : 64, compact ? 64 : 78);
        var tipLines = new List<string>
        {
            $"{Tr(stateLeader.Title)}: {stateLeader.Name} ({Simulation.Loc.Format("{0} yrs", stateLeader.Age)})",
            $"{Tr("Trait")}: {Tr(stateLeader.TraitName)}"
        };
        if (stateTrait is not null)
        {
            if (!string.IsNullOrEmpty(stateTrait.Description)) tipLines.Add(Tr(stateTrait.Description));
            foreach (var (modKey, modVal) in stateTrait.Modifiers)
                tipLines.Add($"  {Tr(NationalModifiers.Of(modKey)?.Name ?? modKey)}: {modVal:+0.#%;-0.#%;0%}");
        }
        portrait.TooltipText = Ui.Tip(string.Join("\n", tipLines));
        row.AddChild(portrait);

        var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        info.AddThemeConstantOverride("separation", 2);
        row.AddChild(info);

        var officeLine = Title($"{Tr(stateLeader.Title).ToUpperInvariant()} · {Simulation.Loc.Format("{0} YRS", stateLeader.Age)}",
            10, Ui.Colour("text.caption"));
        officeLine.ClipText = true;
        officeLine.AutowrapMode = TextServer.AutowrapMode.Off;
        officeLine.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        info.AddChild(officeLine);

        var nameLine = Title(stateLeader.Name, compact ? 15 : 17, Ui.Colour("text.bright"));
        nameLine.ClipText = true;
        nameLine.AutowrapMode = TextServer.AutowrapMode.Off;
        nameLine.TooltipText = stateLeader.Name;
        nameLine.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        info.AddChild(nameLine);

        var traitLine = Title(Tr(stateLeader.TraitName), 12, Ui.Colour("text.accent"));
        traitLine.ClipText = true;
        traitLine.AutowrapMode = TextServer.AutowrapMode.Off;
        traitLine.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        traitLine.TooltipText = portrait.TooltipText;
        info.AddChild(traitLine);

        if (dual && !string.Equals(govLeader.Name, stateLeader.Name, StringComparison.OrdinalIgnoreCase))
        {
            var hogLine = Title($"{Tr(govLeader.Title)}: {govLeader.Name} ({Tr(govLeader.TraitName)})", 11, Ui.Colour("text.body_soft"));
            hogLine.ClipText = true;
            hogLine.AutowrapMode = TextServer.AutowrapMode.Off;
            hogLine.TooltipText = hogLine.Text;
            hogLine.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            info.AddChild(hogLine);
        }
        else
        {
            var govPol = Title($"{Tr(PoliticsCore.GovernmentOf(nation).Name)} · {Tr(PoliticsCore.IdeologyOf(nation).Name)}",
                11, Ui.Colour("text.body_soft"));
            govPol.ClipText = true;
            govPol.AutowrapMode = TextServer.AutowrapMode.Off;
            govPol.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            info.AddChild(govPol);
        }

        return box;
    }

    /// <summary>
    /// Builds a 2-column Grand Strategy key-figures grid (EU5 / Victoria 3 style) for rapid inspection.
    /// </summary>
    private GridContainer BuildGrandStrategyMetricsGrid(params (string Label, string Value, string ColourToken, string Tooltip)[] cells)
    {
        var grid = new GridContainer
        {
            Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 4);

        foreach (var (caption, val, token, tip) in cells)
        {
            var cell = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var style = new StyleBoxFlat
            {
                BgColor = Ui.Colour("surface.inset"),
                BorderColor = Ui.Colour("surface.line"),
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 3,
                CornerRadiusTopRight = 3,
                CornerRadiusBottomLeft = 3,
                CornerRadiusBottomRight = 3,
                ContentMarginLeft = 7,
                ContentMarginRight = 7,
                ContentMarginTop = 3,
                ContentMarginBottom = 3
            };
            cell.AddThemeStyleboxOverride("panel", style);
            if (!string.IsNullOrEmpty(tip))
            {
                cell.TooltipText = Ui.Tip(tip);
                cell.MouseFilter = MouseFilterEnum.Stop;
            }

            var hbox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            hbox.AddThemeConstantOverride("separation", 6);
            cell.AddChild(hbox);

            var lbl = Title(Tr(caption), 11, Ui.Colour("text.caption"));
            lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            lbl.ClipText = true;
            lbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            hbox.AddChild(lbl);

            var fig = Title(val, 12, Ui.Colour(string.IsNullOrEmpty(token) ? "text.bright" : token));
            fig.HorizontalAlignment = HorizontalAlignment.Right;
            TabularFigures(fig);
            hbox.AddChild(fig);

            grid.AddChild(cell);
        }
        return grid;
    }
}
