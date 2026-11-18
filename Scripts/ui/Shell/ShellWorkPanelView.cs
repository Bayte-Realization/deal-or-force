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

        var rootCol = new VBoxContainer();
        rootCol.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(rootCol);

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
            var item = new HBoxContainer();
            item.AddThemeConstantOverride("separation", 5);
            _figuresRow.AddChild(item);

            var b = new Label { VerticalAlignment = VerticalAlignment.Bottom };
            b.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            b.AddThemeFontSizeOverride("font_size", i == 0 ? 18 : 14);
            b.AddThemeColorOverride("font_color", Ui.Colour(i == 0 ? "shell.paper" : "shell.paper_muted"));
            item.AddChild(b);
            _figMainLabels[i] = b;

            var s = new Label { VerticalAlignment = VerticalAlignment.Bottom };
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

    public void OpenRegion(Nation owner, RegionState state, int provinceId)
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
        _profile = profile;
        if (_panel is null) return;
        ShellContext.PlaceRect(_panel, dock.LeftPanel);
        var compact = profile.Compact;
        _titleLabel.AddThemeFontSizeOverride("font_size", compact ? 18 : 22);
        _figMainLabels[0].AddThemeFontSizeOverride("font_size", compact ? 15 : 18);
    }

    public void Refresh()
    {
        if (!IsVisible || _ctx.World is not { } world) return;

        _backBtn.Disabled = !(_ctx.CanNavigateBack?.Invoke() ?? false);
        UpdatePinButtonState();

        var sig = $"{_mode}|{_targetNationId}|{_targetStateMapIndex}|{_targetProvinceId}|{_activeTab}|{world.ElapsedDays}|{world.Revision}|{Loc.Language}";
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

        var gdpRank = 1 + world.Nations.Count(n => n.Exists && n.Id != nation.Id && n.Gdp > nation.Gdp);
        var armyDivs = (int)MathF.Ceiling(world.Armies.Where(a => a.NationId == nation.Id).Sum(a => a.Divisions));
        SetFigures(
            ($"№ {gdpRank}", Loc.Get("shell.by_gdp")),
            ($"{(int)MathF.Round(nation.Stability * 100f)}%", Loc.Get("Stability")),
            ($"{armyDivs} {Loc.Get("shell.div_short")}", Loc.Get("Army")));

        SetFolderTabs(new[] { "Overview", "Society", "Decisions", "Diplomacy" });
        ClearBodyAndFooter();

        if (_activeTab == 0)
        {
            AddSectionHeader(Loc.Get("shell.sec_key_figures"), Fmt.People(nation.Population));
            AddKvRow(Loc.Get("GDP"), Fmt.Money(nation.Gdp));
            AddKvRow(Loc.Get("Treasury"), isOwn ? Fmt.Money(nation.Treasury) : "—");
            AddKvRow(Loc.Get("Inflation"), Fmt.Percent(nation.Inflation), nation.Inflation > 0.08f ? "shell.state.warning" : "shell.paper");
            AddKvRow(Loc.Get("Manpower"), Fmt.People(nation.Manpower));

            AddSectionHeader(Loc.Get("Neighbours"), Loc.Get("GDP"));
            var ownStates = world.StatesOf(nation).Select(s => s.MapIndex).ToHashSet();
            var neighbors = world.Nations
                .Where(n => n.Exists && n.Id != nation.Id)
                .OrderByDescending(n => n.Region == nation.Region ? 1 : 0)
                .ThenByDescending(n => n.Gdp)
                .Take(6)
                .ToArray();

            foreach (var nb in neighbors)
            {
                var captured = nb;
                var rel = CampaignDiplomacy.Opinion(world, nation.Id, nb.Id);
                AddCountryComparisonRow(nb, Fmt.Money(nb.Gdp), $"{rel:+#;-#;0}",
                    rel < -15 ? "shell.state.negative_text" : (rel > 20 ? "shell.state.positive" : "shell.paper_muted"),
                    () => OpenCountry(captured));
            }
        }
        else if (_activeTab == 1)
        {
            AddSectionHeader(Loc.Get("Society"));
            AddKvRow(Loc.Get("Stability"), Fmt.Percent(nation.Stability));
            AddKvRow(Loc.Get("Standard of living"), Fmt.Percent(nation.StandardOfLiving));
            AddKvRow(Loc.Get("Ideology"), Loc.Get(PoliticsCore.IdeologyOf(nation).Name));
            AddKvRow(Loc.Get("War support"), Fmt.Percent(nation.WarSupport));
        }
        else if (_activeTab == 2)
        {
            AddSectionHeader(Loc.Get("Decisions"));
            AddKvRow(Loc.Get("Political capital"), $"{nation.PoliticalCapital:0.0}");
            var note = CreateNoteLabel(Loc.Get("shell.open_decisions_hint"));
            _bodyRows.AddChild(note);
        }
        else
        {
            AddSectionHeader(Loc.Get("Diplomacy"));
            var wars = Warfare.OpenWars(world).Where(w => !w.Ended && w.Involves(nation.Id)).ToArray();
            AddKvRow(Loc.Get("Wars"), wars.Length.ToString(CultureInfo.InvariantCulture), wars.Length > 0 ? "shell.state.negative_text" : "shell.paper");
            if (!isOwn && world.Player is { } pl)
            {
                var op = CampaignDiplomacy.Opinion(world, pl.Id, nation.Id);
                AddKvRow(Loc.Get("Opinion"), $"{op:+#;-#;0}", op < 0 ? "shell.state.negative_text" : "shell.state.positive");
            }
        }

        if (isOwn)
        {
            AddFooterButton(Loc.Get("Laws"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Laws"); });
            AddFooterButton(Loc.Get("Decisions"), primary: true, () => { Close(); _ctx.OpenScreen?.Invoke("Decisions"); });
        }
        else
        {
            AddFooterButton(Loc.Get("Reports"), primary: false, () => { Close(); _ctx.OpenScreen?.Invoke("Ledger"); });
            AddFooterButton(Loc.Get("Diplomacy"), primary: true, () => { Close(); _ctx.OpenScreen?.Invoke("Diplomacy"); });
        }
    }

    private void PopulateRegion(Campaign world, Nation owner, RegionState state)
    {
        ShowRoundCategoryEmblem("icon_region");
        var isOwn = owner is not null && world.Player?.Id == owner.Id;

        _titleLabel.Text = state.DisplayName;
        var provCount = world.ProvinceMap is not null
            ? Enumerable.Range(1, world.ProvinceMap.ProvinceCount).Count(p => world.ProvinceMap.ProvinceToState[p] == state.MapIndex)
            : 1;
        _subtitleLabel.Text = $"{owner?.DisplayName ?? "—"} · {provCount} {Loc.Get("shell.provinces_count")}";

        var gdpShare = owner is not null && owner.Gdp > 0.01f ? state.LocalGdp / owner.Gdp : 0f;
        SetFigures(
            (Fmt.People(state.Population), Loc.Get("Population")),
            (Fmt.Money(state.LocalGdp), $"{(int)MathF.Round(gdpShare * 100f)}% {Loc.Get("GDP")}"),
            ($"{(int)MathF.Round((1f - state.Unrest) * 100f)}%", Loc.Get("Stability")));

        SetFolderTabs(new[] { "Overview", "Population", "shell.tab_build", "shell.tab_recruit" });
        ClearBodyAndFooter();

        // Clickable owner country link row at top of region dossier (matching state-3.jpg "Сербия -> к стране ›")
        if (owner is not null)
        {
            var capturedOwner = owner;
            AddCountryComparisonRow(owner, "", Loc.Get("shell.to_country_link"), "shell.paper_muted", () => OpenCountry(capturedOwner));
        }

        if (_activeTab == 0 || _activeTab == 1)
        {
            AddSectionHeader(Loc.Get("Region"));
            AddKvRow(Loc.Get("Population"), Fmt.PeopleExact(state.Population));
            AddKvRow(Loc.Get("Unrest"), Fmt.Percent(state.Unrest), state.Unrest > 0.25f ? "shell.state.warning" : "shell.paper");
            AddKvRow(Loc.Get("Infrastructure"), state.InfrastructureLevel.ToString(CultureInfo.InvariantCulture));
            if (_targetProvinceId > 0)
                AddKvRow(Loc.Get("Province"), $"#{_targetProvinceId}");
        }

        if (isOwn && (_activeTab == 0 || _activeTab == 2))
        {
            AddSectionHeader(Loc.Get("shell.tab_build"), Loc.Get("shell.shift_everywhere_hint"));
            ShellRegionBuildTabView.PopulateBuildRows(_ctx, _bodyRows, _feedbackLabel, world, owner, state, () => { _lastBuildSig = ""; Refresh(); });
        }

        if (isOwn && _activeTab == 3)
        {
            AddSectionHeader(Loc.Get("shell.tab_recruit"));
            AddKvRow(Loc.Get("Manpower"), Fmt.People(owner.Manpower));
            _bodyRows.AddChild(CreateNoteLabel(Loc.Get("shell.recruit_workbench_hint")));
        }

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
        foreach (var ch in _tabsRow.GetChildren()) ch.QueueFree();
        _tabButtons.Clear();
        if (_activeTab >= tabKeys.Length) _activeTab = 0;

        var onStyle = _ctx.NineSlice("tab_folder_9s", 10, 8, 5, 8, 5);
        var offStyle = _ctx.PressedWellStyle(padX: 8, padY: 5);

        for (var i = 0; i < tabKeys.Length; i++)
        {
            var idx = i;
            var on = i == _activeTab;
            var b = new Button
            {
                Text = Loc.Get(tabKeys[i]),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, _profile.Compact ? 28 : 32),
                ClipText = true,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            b.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            b.AddThemeFontSizeOverride("font_size", _profile.Compact ? 12 : 14);
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
        foreach (var ch in _bodyRows.GetChildren()) ch.QueueFree();
        foreach (var ch in _footerRow.GetChildren()) ch.QueueFree();
    }

    private void AddSectionHeader(string title, string rightNote = "")
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 6);
        var lbl = new Label { Text = title };
        lbl.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        lbl.AddThemeFontSizeOverride("font_size", 14);
        lbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        h.AddChild(lbl);

        var rule = new ColorRect
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(12, 1),
            Color = Ui.Colour("shell.engrave")
        };
        h.AddChild(rule);

        if (!string.IsNullOrEmpty(rightNote))
        {
            var r = new Label { Text = rightNote };
            r.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
            r.AddThemeFontSizeOverride("font_size", 11);
            r.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
            h.AddChild(r);
        }
        _bodyRows.AddChild(h);
    }

    private void AddKvRow(string key, string value, string valueColorTok = "shell.paper")
    {
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(0, 28) };
        var box = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = Ui.Colour("shell.paper", 0.08f),
            BorderWidthBottom = 1,
            ContentMarginLeft = 4,
            ContentMarginRight = 4
        };
        panel.AddThemeStyleboxOverride("panel", box);

        var row = new HBoxContainer();
        panel.AddChild(row);

        var k = new Label
        {
            Text = key,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true
        };
        k.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        k.AddThemeFontSizeOverride("font_size", 13);
        k.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        row.AddChild(k);

        var v = new Label
        {
            Text = value,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        v.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        v.AddThemeFontSizeOverride("font_size", 13);
        v.AddThemeColorOverride("font_color", Ui.Colour(valueColorTok));
        row.AddChild(v);

        _bodyRows.AddChild(panel);
    }

    private void AddCountryComparisonRow(Nation nation, string midStat, string rightStat, string rightColorTok, Action onClick)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 28),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        var normal = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.paper", 0.03f),
            BorderColor = Ui.Colour("shell.paper", 0.08f),
            BorderWidthBottom = 1
        };
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
            Texture = _ctx.FlagTexture(nation.Tag),
            CustomMinimumSize = new Vector2(21, 14),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(fl);

        var nm = new Label
        {
            Text = nation.DisplayName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
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

    private Label CreateNoteLabel(string text)
    {
        var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        return lbl;
    }

    private void AddFooterButton(string caption, bool primary, Action onClick)
    {
        if (primary && _footerRow.GetChildCount() > 0)
            _footerRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var btn = new Button
        {
            Text = caption,
            CustomMinimumSize = new Vector2(primary ? 136 : 96, primary ? 38 : 34),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        var style = primary
            ? _ctx.NineSlice("button_brass_cta_9s", 10, 14, 5, 14, 5, "shell.plate_top", "shell.brass_hi", 3)
            : _ctx.NineSlice("button_umber_9s", 10, 12, 4, 12, 4, "shell.plate_bottom", "shell.engrave", 3);
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeStyleboxOverride("hover", style);
        btn.AddThemeStyleboxOverride("pressed", _ctx.PressedWellStyle(padX: 12, padY: 4));
        btn.AddThemeFontOverride("font", _ctx.Font(primary ? "shell.serif_semibold" : "shell.sans"));
        btn.AddThemeFontSizeOverride("font_size", primary ? 15 : 13);
        btn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        btn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            onClick();
        };
        _footerRow.AddChild(btn);
    }

    private void UpdatePinButtonState()
    {
        if (_pinBtn is null) return;
        _pinBtn.AddThemeStyleboxOverride("normal", _pinned ? _ctx.PressedWellStyle() : new StyleBoxEmpty());
    }
}
