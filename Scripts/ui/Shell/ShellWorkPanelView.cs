using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Step 6 Left Dossier (state-2.jpg Country, state-3.jpg Region, Foreign Country) in the Wave 2
/// campaign shell: chamfered antique-brass frame docked strictly at x = rail.Right + 8 .. 544
/// (58..390 at 720p), natural-aspect flag or brass-ringed category emblem, Source Serif 4 title +
/// subtitle, Back/Pin/Close header buttons, 3 ruled headline figures, attached folder tabs,
/// ruled document rows, +1/x3/all regional construction chips, and brass-rimmed primary CTA.
/// </summary>
internal sealed class ShellWorkPanelView
{
    public enum DossierMode { None, Country, Region }

    private readonly ShellContext _ctx;

    private PanelContainer _panel;
    private Control _emblemHost;
    private TextureRect _flagTex;
    private Control _flagEdge;
    private PanelContainer _roundEmblem;
    private TextureRect _roundEmblemIcon;
    private Label _titleLabel;
    private Label _subtitleLabel;
    private Button _backBtn;
    private Button _pinBtn;
    private Button _closeBtn;

    private HBoxContainer _figuresRow;
    private readonly Label[] _figMainLabels = new Label[3];
    private readonly Label[] _figSubLabels = new Label[3];

    private HBoxContainer _tabsRow;
    private readonly List<Button> _tabButtons = new();
    private ScrollContainer _bodyScroll;
    private VBoxContainer _bodyRows;
    private HBoxContainer _footerRow;
    private Label _feedbackLabel;

    private DossierMode _mode = DossierMode.None;
    private int _activeTab;
    private bool _pinned;
    private int _targetNationId = -1;
    private int _targetStateMapIndex = -1;
    private int _targetProvinceId = -1;
    private ShellLayoutProfile _profile;
    private string _lastBuildSig = "";
    private string _lastTabSig = "";
    private (ProvinceTopology Map, int Rev, Dictionary<int, int> Counts) _stateProvinceCache;
    private readonly Dictionary<int, int> _rankByNationCache = new();
    private (Campaign World, int Day, int Rev) _rankCacheStamp;

    public bool IsVisible => _panel is { Visible: true };
    public bool IsPinned => _pinned;
    public DossierMode Mode => _mode;

    public ShellWorkPanelView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _panel = new PanelContainer
        {
            Name = "ShellWorkPanel",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _panel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_brass_9s", 16, 14, 12, 14, 10, "shell.body_top", "shell.brass_hi", 3));
        _ctx.Root.AddChild(_panel);
        ChromePlateArt.AttachSurfaceTexture(_panel, "linen", 0.30f);

        var frame = new Control
        {
            Name = "WorkPanelFrame",
            ClipContents = true,
            CustomMinimumSize = Vector2.Zero,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _panel.AddChild(frame);

        if (_ctx.LoadTexture(SpriteCatalog.TextureOf("shell_dossier_header", "res://game/gfx/interface/shell/dossier_paper_header.png")) is { } hdrTex)
        {
            var hdrBg = new TextureRect
            {
                Texture = hdrTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                Modulate = new Color(1f, 1f, 1f, 0.20f),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            hdrBg.AnchorRight = 1f;
            hdrBg.OffsetBottom = 92f;
            frame.AddChild(hdrBg);
        }

        var rootCol = new VBoxContainer();
        rootCol.AddThemeConstantOverride("separation", 6);
        rootCol.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        frame.AddChild(rootCol);

        BuildHeader(rootCol);
        BuildFiguresRow(rootCol);
        rootCol.AddChild(ShellContext.CreateRuledLine(11, "shell.ruled_stroke", 1.5f, 5f));

        _tabsRow = new HBoxContainer();
        _tabsRow.AddThemeConstantOverride("separation", 3);
        rootCol.AddChild(_tabsRow);

        var bodyWell = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var bodyBox = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.body_bottom"),
            BorderColor = new Color(0f, 0f, 0f, 0.55f),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        bodyBox.SetBorderWidthAll(1);
        bodyWell.AddThemeStyleboxOverride("panel", bodyBox);
        rootCol.AddChild(bodyWell);

        _bodyScroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        bodyWell.AddChild(_bodyScroll);

        _bodyRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _bodyRows.AddThemeConstantOverride("separation", 4);
        _bodyScroll.AddChild(_bodyRows);

        _feedbackLabel = new Label
        {
            Text = "",
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _feedbackLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _feedbackLabel.AddThemeFontSizeOverride("font_size", 11);
        _feedbackLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        rootCol.AddChild(_feedbackLabel);

        _footerRow = new HBoxContainer();
        _footerRow.AddThemeConstantOverride("separation", 8);
        rootCol.AddChild(_footerRow);
    }

    private void BuildHeader(VBoxContainer parent)
    {
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        parent.AddChild(head);

        _emblemHost = new Control
        {
            CustomMinimumSize = new Vector2(54, 44),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        head.AddChild(_emblemHost);

        _flagTex = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _emblemHost.AddChild(_flagTex);

        _flagEdge = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _flagEdge.Draw += () =>
        {
            var sz = _flagEdge.Size;
            if (sz.X > 2 && sz.Y > 2)
                _flagEdge.DrawRect(new Rect2(0, 0, sz.X, sz.Y), Ui.Colour("shell.border", 0.88f), filled: false, width: 1f);
        };
        _emblemHost.AddChild(_flagEdge);

        _roundEmblem = new PanelContainer { Visible = false };
        _roundEmblem.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("round_bezel", 0, 10, 10, 10, 10, "shell.plate_top", "shell.brass", 24));
        _roundEmblem.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _emblemHost.AddChild(_roundEmblem);

        _roundEmblemIcon = new TextureRect
        {
            Texture = _ctx.Icon("icon_region"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.icon")
        };
        _roundEmblem.AddChild(_roundEmblemIcon);

        var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 1);
        head.AddChild(titles);

        _titleLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _titleLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _titleLabel.AddThemeFontSizeOverride("font_size", 22);
        _titleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        titles.AddChild(_titleLabel);

        _subtitleLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MaxLinesVisible = 2
        };
        _subtitleLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 12);
        _subtitleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        titles.AddChild(_subtitleLabel);

        _backBtn = CreateHeaderIconButton("icon_shell_back", Loc.Get("Back"), () => _ctx.NavigateBack?.Invoke());
        _pinBtn = CreateHeaderIconButton("icon_shell_pin", Loc.Get("Pin or unpin headline"), () =>
        {
            _pinned = !_pinned;
            UpdatePinButtonState();
        });
        _closeBtn = CreateHeaderIconButton("icon_shell_close", Loc.Get("Close"), Close);

        head.AddChild(_backBtn);
        head.AddChild(_pinBtn);
        head.AddChild(_closeBtn);
    }

    private Button CreateHeaderIconButton(string iconName, string tip, Action onClick)
    {
        var empty = new StyleBoxEmpty();
        var hover = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.08f) };
        hover.SetCornerRadiusAll(2);
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(30, 30),
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = Ui.Tip(tip)
        };
        btn.AddThemeStyleboxOverride("normal", empty);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle());
        btn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            onClick();
        };

        var tex = new TextureRect
        {
            Texture = _ctx.Icon(iconName),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.paper_muted"),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        tex.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        tex.OffsetLeft = 7; tex.OffsetRight = -7; tex.OffsetTop = 7; tex.OffsetBottom = -7;
        btn.AddChild(tex);
        return btn;
    }

    private void BuildFiguresRow(VBoxContainer parent)
    {
        _figuresRow = new HBoxContainer();
        _figuresRow.AddThemeConstantOverride("separation", 10);
        parent.AddChild(_figuresRow);

        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                var sep = new ColorRect
                {
                    CustomMinimumSize = new Vector2(1, 18),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    Color = Ui.Colour("shell.engrave")
                };
                _figuresRow.AddChild(sep);
            }
            var item = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            item.AddThemeConstantOverride("separation", 5);
            _figuresRow.AddChild(item);

            var b = new Label { VerticalAlignment = VerticalAlignment.Bottom };
            b.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            b.AddThemeFontSizeOverride("font_size", i == 0 ? 18 : 14);
            b.AddThemeColorOverride("font_color", Ui.Colour(i == 0 ? "shell.paper" : "shell.paper_muted"));
            item.AddChild(b);
            _figMainLabels[i] = b;

            var s = new Label
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            s.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
            s.AddThemeFontSizeOverride("font_size", 11);
            s.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            item.AddChild(s);
            _figSubLabels[i] = s;
        }
    }

    public void OpenCountry(Nation nation)
    {
        if (nation is null) return;
        _mode = DossierMode.Country;
        if (_targetNationId != nation.Id) _activeTab = 0;
        _targetNationId = nation.Id;
        _feedbackLabel.Visible = false;
        _lastBuildSig = "";
        _panel.Visible = true;
        Refresh();
    }

    public void OpenRegion(Nation owner, ScenarioState state, int provinceId)
    {
        if (state is null && owner is not null)
        {
            OpenCountry(owner);
            return;
        }
        if (state is null) return;
        _mode = DossierMode.Region;
        if (_targetStateMapIndex != state.MapIndex) _activeTab = 0;
        _targetNationId = owner?.Id ?? -1;
        _targetStateMapIndex = state.MapIndex;
        _targetProvinceId = provinceId;
        _feedbackLabel.Visible = false;
        _lastBuildSig = "";
        _panel.Visible = true;
        Refresh();
    }

    public void Close()
    {
        _mode = DossierMode.None;
        _pinned = false;
        if (_panel is not null) _panel.Visible = false;
        _ctx.RefreshAll?.Invoke();
    }

    public void ApplyDock(ShellLayoutProfile profile, ShellDockGeometry dock)
    {
        var compactChanged = _profile is null || _profile.Compact != profile.Compact;
        _profile = profile;
        if (_panel is null) return;
        _panel.CustomMinimumSize = Vector2.Zero;
        ShellContext.PlaceRect(_panel, dock.LeftPanel);
        var compact = profile.Compact;
        _titleLabel.AddThemeFontSizeOverride("font_size", compact ? 18 : 22);
        _figMainLabels[0].AddThemeFontSizeOverride("font_size", compact ? 15 : 18);
        if (compactChanged) _lastTabSig = "";
    }

    public void Refresh()
    {
        if (!IsVisible || _ctx.World is not { } world) return;

        _backBtn.Disabled = !(_ctx.CanNavigateBack?.Invoke() ?? false);
        UpdatePinButtonState();

        var sig = $"{_mode}|{_targetNationId}|{_targetStateMapIndex}|{_targetProvinceId}|{_activeTab}|{world.ElapsedDays}|{world.MapRevision}|{Loc.Language}";
        if (_lastBuildSig == sig) return;
        _lastBuildSig = sig;

        if (_mode == DossierMode.Country)
        {
            var nation = world.NationById(_targetNationId) ?? world.Player;
            if (nation is not null) PopulateCountry(world, nation);
        }
        else if (_mode == DossierMode.Region)
        {
            var state = world.StateByMapIndex(_targetStateMapIndex);
            var owner = _targetNationId >= 0 ? world.NationById(_targetNationId) : null;
            if (owner is null && state is not null)
                owner = world.Nations.FirstOrDefault(n => n.MapIndex == state.NationMapIndex);
            if (state is not null) PopulateRegion(world, owner, state);
        }
    }

    private void PopulateCountry(Campaign world, Nation nation)
    {
        var isOwn = world.Player?.Id == nation.Id;
        ShowFlagEmblem(nation.Tag);

        _titleLabel.Text = nation.DisplayName;
        var ruler = Rulers.HeadOfState(world, nation);
        _subtitleLabel.Text = $"{Loc.Get(PoliticsCore.GovernmentOf(nation).Name)} · {Loc.Get(ruler.Title)} {ruler.Name}";

        var overallRank = ResolveOverallRank(world, nation.Id);
        var armyDivs = (int)MathF.Ceiling(world.Armies.Where(a => a.NationId == nation.Id).Sum(a => a.Divisions));
        SetFigures(
            ($"№ {overallRank}", Loc.Get("shell.overall_rank")),
            ($"{(int)MathF.Round(nation.Stability * 100f)}%", Loc.Get("Stability")),
            ($"{armyDivs} {Loc.Get("shell.div_short")}", Loc.Get("Army")));

        SetFolderTabs(new[] { "Overview", "Society", "Decisions", "Diplomacy" });
        ClearBodyAndFooter();

        if (_activeTab == 0)
        {
            ShellCountryDossierView.PopulateOverview(_ctx, _bodyRows, world, nation, isOwn, OpenCountry);
        }
        else if (_activeTab == 1)
        {
            ShellCountryDossierView.PopulateSociety(_ctx, _bodyRows, world, nation);
        }
        else if (_activeTab == 2)
        {
            ShellCountryDossierView.PopulateDecisions(_ctx, _bodyRows, world, nation);
        }
        else
        {
            ShellCountryDossierView.PopulateDiplomacy(_ctx, _bodyRows, world, nation, isOwn);
        }

        if (isOwn)
        {
            AddFooterButton(Loc.Get("Laws"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Laws"); });
            AddFooterButton(Loc.Get("Decisions"), primary: true, () => { Close(); _ctx.OpenScreen?.Invoke("Decisions"); });
            AddFooterButton(Loc.Get("Economy"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Economy"); });
            AddFooterButton(Loc.Get("Trade"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Trade"); });
            AddFooterButton(Loc.Get("Military"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Military"); });
        }
        else
        {
            AddFooterButton(Loc.Get("Reports"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Ledger"); });
            AddFooterButton(Loc.Get("Trade"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Trade"); });
            AddFooterButton(Loc.Get("Diplomacy"), primary: true, () => { Close(); _ctx.OpenScreen?.Invoke("Diplomacy"); });
        }
    }

    private void PopulateRegion(Campaign world, Nation owner, ScenarioState state)
    {
        ShowRoundCategoryEmblem("icon_region");
        var isOwn = owner is not null && world.Player?.Id == owner.Id;

        _titleLabel.Text = state.DisplayName;
        var provCount = CountStateProvinces(world, state.MapIndex);
        _subtitleLabel.Text = $"{owner?.DisplayName ?? "—"} · {provCount} {Loc.Get("shell.provinces_count")}";

        var estGdp = RegionalStatistics.EstimatedGdp(world, state);
        var gdpShare = owner is not null && owner.Gdp > 0.01f ? estGdp / owner.Gdp : 0f;
        SetFigures(
            (Fmt.People(state.Population), Loc.Get("Population")),
            (Fmt.Money(estGdp), $"{(int)MathF.Round(gdpShare * 100f)}% {Loc.Get("GDP")}"),
            ($"{(int)MathF.Round(state.Stability * 100f)}%", Loc.Get("Stability")));

        SetFolderTabs(new[] { "Overview", "Population", "shell.tab_build", "shell.tab_recruit" });
        ClearBodyAndFooter();

        if (owner is not null)
        {
            var capturedOwner = owner;
            AddCountryComparisonRow(owner, "", Loc.Get("shell.to_country_link"), "shell.paper_muted", () => OpenCountry(capturedOwner));
        }

        ShellRegionVisualsView.PopulateRegion(
            _ctx, _bodyRows, _feedbackLabel, world, owner, state, _targetProvinceId, provCount, _activeTab, isOwn,
            () => { _lastBuildSig = ""; Refresh(); });

        if (isOwn)
        {
            AddFooterButton(Loc.Get("Economy"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Economy"); });
            AddFooterButton(Loc.Get("shell.btn_recruit"), primary: true, () => { _ctx.OpenRecruitment?.Invoke(); });
        }
        else if (owner is not null)
        {
            AddFooterButton(Loc.Get("Country"), primary: false, () => OpenCountry(owner));
            AddFooterButton(Loc.Get("Diplomacy"), primary: true, () => { Close(); _ctx.OpenScreen?.Invoke("Diplomacy"); });
        }
    }

    private int ResolveOverallRank(Campaign world, int nationId)
    {
        if (!ReferenceEquals(_rankCacheStamp.World, world) || _rankCacheStamp.Day != world.ElapsedDays ||
            _rankCacheStamp.Rev != world.MapRevision)
        {
            if (world.MidStep)
                return _rankByNationCache.GetValueOrDefault(nationId, 1);

            var weights = new CountryRankings.Weights(
                Ui.Metric("rankings.weight.economy", 40),
                Ui.Metric("rankings.weight.population", 15),
                Ui.Metric("rankings.weight.field_power", 30),
                Ui.Metric("rankings.weight.technology", 15));
            _rankByNationCache.Clear();
            foreach (var r in CountryRankings.Read(world, weights))
                _rankByNationCache[r.NationId] = r.OverallRank;
            _rankCacheStamp = (world, world.ElapsedDays, world.MapRevision);
        }
        return _rankByNationCache.GetValueOrDefault(nationId, 1);
    }

    private int CountStateProvinces(Campaign world, int stateMapIndex)
    {
        if (world.ProvinceMap is not { } map) return 1;
        if (!ReferenceEquals(_stateProvinceCache.Map, map) || _stateProvinceCache.Rev != world.MapRevision)
        {
            var counts = _stateProvinceCache.Counts ?? new Dictionary<int, int>();
            counts.Clear();
            var arr = map.ProvinceToState;
            for (var p = 1; p <= map.ProvinceCount && p < arr.Length; p++)
            {
                var stIdx = arr[p];
                counts[stIdx] = counts.GetValueOrDefault(stIdx) + 1;
            }
            _stateProvinceCache = (map, world.MapRevision, counts);
        }
        return Math.Max(1, _stateProvinceCache.Counts.GetValueOrDefault(stateMapIndex));
    }

    private void ShowFlagEmblem(string tag)
    {
        _roundEmblem.Visible = false;
        _flagTex.Visible = true;
        var tex = _ctx.FlagTexture(tag);
        _flagTex.Texture = tex;
        var fit = ShellLayout.FitFlag(tex?.GetWidth() ?? 300, tex?.GetHeight() ?? 200, 52f, 36f, tag);
        var pos = new Vector2((54f - fit.Width) * 0.5f, (44f - fit.Height) * 0.5f);
        _flagTex.Position = pos;
        _flagTex.Size = new Vector2(fit.Width, fit.Height);
        _flagEdge.Visible = !fit.CutoutSilhouette;
        _flagEdge.Position = pos;
        _flagEdge.Size = new Vector2(fit.Width, fit.Height);
        _flagEdge.QueueRedraw();
    }

    private void ShowRoundCategoryEmblem(string iconName)
    {
        _flagTex.Visible = false;
        _flagEdge.Visible = false;
        _roundEmblem.Visible = true;
        _roundEmblemIcon.Texture = _ctx.Icon(iconName);
    }

    private void SetFigures((string Main, string Sub) f0, (string Main, string Sub) f1, (string Main, string Sub) f2)
    {
        var arr = new[] { f0, f1, f2 };
        for (var i = 0; i < 3; i++)
        {
            _figMainLabels[i].Text = arr[i].Main;
            _figSubLabels[i].Text = arr[i].Sub;
        }
    }

    private void SetFolderTabs(string[] tabKeys)
    {
        if (_activeTab >= tabKeys.Length) _activeTab = 0;
        var tabSig = string.Join("|", tabKeys) + $"|{_activeTab}|{_profile?.Compact ?? false}|{Loc.Language}";
        if (_lastTabSig == tabSig && _tabButtons.Count == tabKeys.Length) return;
        _lastTabSig = tabSig;

        foreach (var ch in _tabsRow.GetChildren())
        {
            _tabsRow.RemoveChild(ch);
            ch.QueueFree();
        }
        _tabButtons.Clear();

        var onStyle = _ctx.NineSlice("tab_folder_9s", 10, 8, 5, 8, 5);
        var offStyle = _ctx.PressedWellStyle(padX: 8, padY: 5);

        for (var i = 0; i < tabKeys.Length; i++)
        {
            var idx = i;
            var on = i == _activeTab;
            var compact = _profile?.Compact == true;
            var b = new Button
            {
                Text = Loc.Get(tabKeys[i]),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, compact ? 28 : 32),
                ClipText = true,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            b.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            b.AddThemeFontSizeOverride("font_size", compact ? 12 : 14);
            b.AddThemeStyleboxOverride("normal", on ? onStyle : offStyle);
            b.AddThemeStyleboxOverride("hover", on ? onStyle : offStyle);
            b.AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
            b.Pressed += () =>
            {
                _ctx.PlayCue?.Invoke("ui_select");
                _activeTab = idx;
                _lastBuildSig = "";
                Refresh();
            };
            _tabsRow.AddChild(b);
            _tabButtons.Add(b);
        }
    }

    private void ClearBodyAndFooter()
    {
        foreach (var ch in _bodyRows.GetChildren()) { _bodyRows.RemoveChild(ch); ch.QueueFree(); }
        foreach (var ch in _footerRow.GetChildren()) { _footerRow.RemoveChild(ch); ch.QueueFree(); }
    }

    private void AddCountryComparisonRow(Nation nation, string midStat, string rightStat, string rightColorTok, Action onClick)
    {
        var btn = new Button { CustomMinimumSize = new Vector2(0, 28), MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None };
        var normal = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.03f), BorderColor = Ui.Colour("shell.paper", 0.08f), BorderWidthBottom = 1 };
        var hover = new StyleBoxFlat { BgColor = Ui.Colour("shell.paper", 0.08f) };
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 7);
        row.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        row.OffsetLeft = 4; row.OffsetRight = -4;
        btn.AddChild(row);

        var fl = new TextureRect
        {
            Texture = _ctx.FlagTexture(nation.Tag), CustomMinimumSize = new Vector2(21, 14), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(fl);

        var nm = new Label { Text = nation.DisplayName, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        nm.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        nm.AddThemeFontSizeOverride("font_size", 13);
        nm.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(nm);

        if (!string.IsNullOrEmpty(midStat))
        {
            var m = new Label { Text = midStat, HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(56, 0) };
            m.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            m.AddThemeFontSizeOverride("font_size", 12);
            m.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
            row.AddChild(m);
        }

        var r = new Label { Text = rightStat, HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(44, 0) };
        r.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        r.AddThemeFontSizeOverride("font_size", 12);
        r.AddThemeColorOverride("font_color", Ui.Colour(rightColorTok));
        row.AddChild(r);
        _bodyRows.AddChild(btn);
    }

    private void AddFooterButton(string caption, bool primary, Action onClick)
    {
        var btn = new Button
        {
            Text = caption,
            CustomMinimumSize = new Vector2(0, primary ? 36 : 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        var style = primary
            ? _ctx.NineSlice("button_brass_cta_9s", 10, 8, 5, 8, 5, "shell.plate_top", "shell.brass_hi", 3)
            : _ctx.NineSlice("button_umber_9s", 10, 8, 4, 8, 4, "shell.plate_bottom", "shell.engrave", 3);
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeStyleboxOverride("hover", style);
        btn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle(padX: 8, padY: 4));
        btn.AddThemeFontOverride("font", _ctx.Font(primary ? "shell.serif_semibold" : "shell.sans"));
        btn.AddThemeFontSizeOverride("font_size", primary ? 14 : 12);
        btn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        btn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };
        _footerRow.AddChild(btn);
    }

    private void UpdatePinButtonState()
    {
        if (_pinBtn is null) return;
        _pinBtn.AddThemeStyleboxOverride("normal", _pinned ? _ctx.PressedWellStyle() : new StyleBoxEmpty());
    }
}
