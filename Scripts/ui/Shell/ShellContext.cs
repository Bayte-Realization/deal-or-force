using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Narrow presentation context and instance-scoped material/font cache shared by all standalone
/// views of the Wave 2 campaign shell. Keeps every view class decoupled from DealOrForceGame and
/// avoids static GPU resource dictionaries that outlive a campaign scene.
/// </summary>
internal sealed class ShellContext
{
    private readonly Dictionary<string, FontVariation> _fontCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _textureCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StyleBox> _styleCache = new(StringComparer.Ordinal);

    public Control Root { get; set; }
    public Func<Campaign> GetCampaign { get; set; }
    public Func<Vector2> GetViewportSize { get; set; }
    public Func<bool> IsPaused { get; set; }
    public Func<int> GetSpeed { get; set; }
    public Func<bool> IsObserverMode { get; set; }
    public Func<bool> IsFreeCameraMode { get; set; }
    public Func<bool> IsAnyModalOpen { get; set; }
    public Func<bool> IsProductionOpen { get; set; }
    public Func<string> GetOpenScreen { get; set; }
    public Func<StrategicMapMode> GetMapMode { get; set; }
    public Func<bool> IsFlatMap { get; set; }
    public Func<float> GetViewDistance { get; set; }
    public Func<float> GetFlatZoom { get; set; }
    public Func<(float Latitude, float Longitude, float SpanDegrees)> GetCameraMinimapView { get; set; }
    public Func<IReadOnlyList<Army>> GetSelectedArmies { get; set; }
    public Func<Nation> GetSelectedNation { get; set; }
    public Func<ScenarioState> GetSelectedState { get; set; }
    public Func<int> GetSelectedProvinceId { get; set; }
    public Func<bool> CanNavigateBack { get; set; }

    public Action<string> OpenScreen { get; set; }
    public Action<string> ToggleScreen { get; set; }
    public Action<StrategicMapMode> SetMapMode { get; set; }
    public Action ToggleProjection { get; set; }
    public Action TogglePause { get; set; }
    public Action<int> SetSpeed { get; set; }
    public Action ShowPauseMenu { get; set; }
    public Action OpenDefconOverview { get; set; }
    public Action OpenWarLedger { get; set; }
    public Action<int> ShowWarDetail { get; set; }
    public Action OpenSovereignFinance { get; set; }
    public Action<int> OpenCountryRankings { get; set; }
    public Action OpenRecruitment { get; set; }
    public Action ToggleNavigator { get; set; }
    public Action FocusOwnCapital { get; set; }
    public Action<float, float, float> FocusLatLon { get; set; }
    public Func<float, float, Vector2?> ProjectToScreen { get; set; }
    public Action<IReadOnlySet<int>> SetMergedVisibleArmies { get; set; }
    public Action<Army, bool> PickArmy { get; set; }
    public Action<Nation, ScenarioState, int> PickMapRegion { get; set; }
    public Action<string> ApplySetting { get; set; }
    public Action<string> PlayCue { get; set; }
    public Action<bool> ExecuteZeroBudget { get; set; }
    public Action PerformBatchUndo { get; set; }
    public Func<bool> NavigateBack { get; set; }
    public Action SplitSelectionInHalf { get; set; }
    public Action MergeSelectedArmies { get; set; }
    public Action ClearArmySelection { get; set; }
    public Action RefreshAll { get; set; }

    public bool ExplicitRegionIconsToggle { get; set; }
    public bool AltKeycapsVisible { get; set; }

    public Campaign World => GetCampaign?.Invoke();

    public void ClearCaches()
    {
        _fontCache.Clear();
        _styleCache.Clear();
    }

    public Font Font(string role, bool tabular = false)
    {
        var key = role + (tabular ? "|tnum" : "");
        if (_fontCache.TryGetValue(key, out var cached) && GodotObject.IsInstanceValid(cached))
            return cached;

        var baseFont = PresentationFonts.For(role);
        var variation = new FontVariation { BaseFont = baseFont };
        if (tabular)
        {
            variation.OpentypeFeatures = new Godot.Collections.Dictionary
            {
                ["tnum"] = 1,
                ["lnum"] = 1
            };
        }
        _fontCache[key] = variation;
        return variation;
    }

    /// <summary>
    /// Loads a bundled or mod-overridden texture, falling back to direct disk image loading when
    /// running inside an un-imported worktree so 9-slices and icons never draw blank.
    /// </summary>
    public Texture2D LoadTexture(string resPath)
    {
        if (string.IsNullOrWhiteSpace(resPath)) return null;
        if (_textureCache.TryGetValue(resPath, out var cached) && GodotObject.IsInstanceValid(cached))
            return cached;

        Texture2D loaded = null;
        if (ResourceLoader.Exists(resPath))
        {
            try { loaded = GD.Load<Texture2D>(resPath); }
            catch { loaded = null; }
        }
        if (loaded is null)
        {
            try
            {
                var disk = resPath.StartsWith("res://", StringComparison.Ordinal)
                    ? ProjectSettings.GlobalizePath(resPath)
                    : resPath;
                if (System.IO.File.Exists(disk))
                {
                    var img = Image.LoadFromFile(disk);
                    if (img is not null && !img.IsEmpty())
                        loaded = ImageTexture.CreateFromImage(img);
                }
            }
            catch { loaded = null; }
        }
        if (loaded is not null)
            _textureCache[resPath] = loaded;
        return loaded;
    }

    public Texture2D Icon(string spriteName)
    {
        if (string.IsNullOrWhiteSpace(spriteName)) return null;
        var clean = spriteName.StartsWith("icon_", StringComparison.Ordinal) ? spriteName : "icon_" + spriteName;
        var fallback = $"res://game/gfx/interface/icons/{clean}.png";
        var path = SpriteCatalog.TextureOf(clean, fallback);
        return LoadTexture(path);
    }

    public Func<string, Texture2D> ResolveFlag { get; set; }

    public Texture2D FlagTexture(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (ResolveFlag?.Invoke(tag) is { } resolved) return resolved;
        return LoadTexture($"res://game/gfx/flags/flag_{tag.ToLowerInvariant()}.png");
    }

    public Texture2D PaperTexture() =>
        LoadTexture(SpriteCatalog.TextureOf("shell_paper", "res://game/gfx/interface/shell/paper.jpg"));

    public StyleBox NineSlice(
        string frameName,
        int sliceMargin = 16,
        int contentLeft = 0,
        int contentTop = 0,
        int contentRight = 0,
        int contentBottom = 0,
        string fallbackBgToken = "shell.plate_bottom",
        string fallbackBorderToken = "shell.border",
        int fallbackRadius = 3)
    {
        var hiDpi = (GetViewportSize?.Invoke().X ?? 1920f) >= 1600f;
        var file = hiDpi ? $"{frameName}@2x.png" : $"{frameName}.png";
        var key = $"{file}|{sliceMargin}|{contentLeft},{contentTop},{contentRight},{contentBottom}";
        if (_styleCache.TryGetValue(key, out var cached)) return cached;

        var resPath = $"res://game/gfx/interface/shell/{file}";
        var tex = LoadTexture(resPath) ?? LoadTexture($"res://game/gfx/interface/shell/{frameName}.png");
        if (tex is not null)
        {
            var m = hiDpi ? sliceMargin * 2 : sliceMargin;
            var sb = new StyleBoxTexture
            {
                Texture = tex,
                TextureMarginLeft = m,
                TextureMarginTop = m,
                TextureMarginRight = m,
                TextureMarginBottom = m,
                ContentMarginLeft = contentLeft,
                ContentMarginTop = contentTop,
                ContentMarginRight = contentRight,
                ContentMarginBottom = contentBottom
            };
            _styleCache[key] = sb;
            return sb;
        }

        if (InterfaceSkin.Metric(InterfaceSkin.MaterialSwitch, 0f) >= 0.5f && Ui.Surface("plate") is { } matBox)
        {
            _styleCache[key] = matBox;
            return matBox;
        }
        var flat = new StyleBoxFlat
        {
            BgColor = Ui.Colour(fallbackBgToken),
            BorderColor = Ui.Colour(fallbackBorderToken),
            ContentMarginLeft = contentLeft,
            ContentMarginTop = contentTop,
            ContentMarginRight = contentRight,
            ContentMarginBottom = contentBottom,
            ShadowColor = new Color(0f, 0f, 0f, 0.4f),
            ShadowSize = 4,
            ShadowOffset = new Vector2(0f, 2f)
        };
        flat.SetBorderWidthAll(1);
        flat.SetCornerRadiusAll(fallbackRadius);
        _styleCache[key] = flat;
        return flat;
    }

    public StyleBox PressedWellStyle(bool underlineBottom = false, bool underlineLeft = false, int padX = 0, int padY = 0)
    {
        var key = $"pressed_well|{underlineBottom}|{underlineLeft}|{padX}|{padY}";
        if (_styleCache.TryGetValue(key, out var cached)) return cached;
        var box = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.well_top"),
            BorderColor = underlineBottom || underlineLeft ? Ui.Colour("shell.paper") : Ui.Colour("shell.border"),
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = underlineBottom ? 3 : 1,
            BorderWidthLeft = underlineLeft ? 3 : 1,
            ContentMarginLeft = padX,
            ContentMarginRight = padX,
            ContentMarginTop = padY,
            ContentMarginBottom = padY
        };
        box.SetCornerRadiusAll(2);
        _styleCache[key] = box;
        return box;
    }

    /// <summary>
    /// Deterministic hand-ruled pen stroke matching app.js ruled(x, y, w, seed) so dates, alerts,
    /// and dossier figures read like ink on ledger stock rather than flat digital hairlines.
    /// </summary>
    public static Control CreateRuledLine(int seed, string colourToken = "shell.ruled_stroke", float thickness = 1.5f, float height = 5f)
    {
        var line = new Control
        {
            CustomMinimumSize = new Vector2(0, height),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        line.Draw += () =>
        {
            var w = line.Size.X;
            if (w <= 6f) return;
            long r = seed * 7919L + 104729L;
            float Next()
            {
                r = (r * 9301L + 49297L) % 233280L;
                return (float)(r / 233280.0 - 0.5);
            }
            var drift = Next() * 0.65f;
            var x0 = 1f + MathF.Abs(Next()) * 3f;
            var x1 = MathF.Max(x0 + 4f, w - 1f - MathF.Abs(Next()) * 4f);
            var midY = height * 0.5f;
            var pts = new List<Vector2> { new(x0, midY + Next() * 0.35f) };
            for (var t = x0 + 16f; t < x1; t += 16f + Next() * 5f)
            {
                var y2 = midY + drift * MathF.Sin(t / MathF.Max(1f, w) * MathF.PI) + Next() * 0.3f;
                pts.Add(new Vector2(t, y2));
            }
            pts.Add(new Vector2(x1, midY + Next() * 0.35f));
            var col = Ui.Colour(colourToken, 0.86f);
            line.DrawPolyline(pts.ToArray(), col, thickness, antialiased: true);
        };
        return line;
    }

    /// <summary>
    /// Creates a physical Alt-keycap badge (.kbd in style.css: #efe4c8 paper cap with #1c1916 bold ink).
    /// </summary>
    public PanelContainer CreateKeycapBadge(string keyLabel, bool compact = false)
    {
        var cap = new PanelContainer
        {
            Name = "ShellKbd_" + keyLabel,
            Visible = AltKeycapsVisible,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(compact ? 16 : 18, compact ? 16 : 18)
        };
        var box = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.paper"),
            BorderColor = Ui.Colour("shell.paper_ghost"),
            BorderWidthBottom = 1,
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
            ContentMarginTop = 0,
            ContentMarginBottom = 0,
            ShadowColor = new Color(0f, 0f, 0f, 0.55f),
            ShadowSize = 2,
            ShadowOffset = new Vector2(0f, 1f)
        };
        box.SetCornerRadiusAll(2);
        cap.AddThemeStyleboxOverride("panel", box);

        var lbl = new Label
        {
            Text = keyLabel,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        lbl.AddThemeFontOverride("font", Font("shell.sans", tabular: true));
        lbl.AddThemeFontSizeOverride("font_size", compact ? 10 : 11);
        lbl.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
        cap.AddChild(lbl);
        return cap;
    }

    public static void PlaceRect(Control control, ShellRect rect)
    {
        if (control is null) return;
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        control.OffsetLeft = rect.X;
        control.OffsetTop = rect.Y;
        control.OffsetRight = rect.Right;
        control.OffsetBottom = rect.Bottom;
        control.Position = new Vector2(rect.X, rect.Y);
        control.Size = new Vector2(rect.W, rect.H);
    }

    /// <summary>
    /// Applies the shared top-left relief emboss shader (ui_relief.gdshader) to monochrome icons.
    /// </summary>
    public static void ApplyIconRelief(CanvasItem item, Color tint, float depth = 0.65f)
    {
        if (item is null) return;
        if (MaterialStyles.Relief(tint, depth) is { } shaderMat)
        {
            item.Material = shaderMat;
            item.Modulate = Colors.White;
        }
        else
        {
            item.Modulate = tint.A > 0f ? tint : MaterialStyles.IconTone("shell");
        }
    }
}
