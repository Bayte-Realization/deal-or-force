using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Full-workspace Atlas view for Strategic Megaprojects &amp; Joint International Consortia.
/// Follows the Wave 2 modular shell contract (<see cref="ShellEconomyWorkspaceView"/>):
/// - Uses <c>Ui.Colour</c> tokens only (zero raw <see cref="Color"/> literals).
/// - Uses tabular numerals (<c>tabular: true</c>) for every ticking or tabulated figure.
/// - Reads <see cref="Campaign"/> without ever mutating state or <see cref="CampaignFingerprint"/>.
/// - Submits every player action exclusively through <see cref="ICampaignCommand"/>.
/// </summary>
internal sealed class GrandProjectsView
{
    private enum TabKind { Ours = 0, Commission = 1, World = 2 }

    private readonly ShellContext _ctx;

    private Control _layer;
    private PanelContainer _panel;
    private Label _titleLabel;
    private Label _subtitleLabel;
    private Label _summaryLabel;
    private Label _bannerLabel;
    private readonly Button[] _tabButtons = new Button[3];
    private ScrollContainer _scroll;
    private VBoxContainer _content;

    private TabKind _activeTab = TabKind.Ours;
    private int _selectedStateMapIndex = -1;
    private Campaign _lastWorld;
    private string _lastSignature = "";

    public bool IsVisible => _layer is { Visible: true };

    public GrandProjectsView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _layer = new Control
        {
            Name = "GrandProjectsWorkspaceLayer",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = 26
        };
        _layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _ctx.Root.AddChild(_layer);

        var dim = new ColorRect
        {
            Color = Ui.Colour("shell.well", 0.45f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(dim);

        _panel = new PanelContainer
        {
            Name = "GrandProjectsWorkspacePanel",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _panel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_brass_9s", 16, 18, 12, 18, 12, "shell.body_top", "shell.brass_hi", 3));
        _layer.AddChild(_panel);
        ChromePlateArt.AttachSurfaceTexture(_panel, "linen", 0.30f);

        var rootCol = new VBoxContainer();
        rootCol.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rootCol);

        BuildHeader(rootCol);
        BuildBody(rootCol);
    }

    private void BuildHeader(VBoxContainer parent)
    {
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 12);
        parent.AddChild(head);

        var emb = new PanelContainer
        {
            CustomMinimumSize = new Vector2(44, 44),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        emb.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("round_bezel", 0, 8, 8, 8, 8, "shell.plate_top", "shell.brass", 22));
        var embIco = new TextureRect
        {
            Texture = _ctx.Icon("icon_economy"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.brass_hi")
        };
        emb.AddChild(embIco);
        head.AddChild(emb);

        var titleCol = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(240, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        titleCol.AddThemeConstantOverride("separation", 2);
        head.AddChild(titleCol);

        _titleLabel = new Label { Text = Loc.Get("gp.ui.title") };
        _titleLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        _titleLabel.AddThemeFontSizeOverride("font_size", 22);
        _titleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        titleCol.AddChild(_titleLabel);

        _subtitleLabel = new Label { Text = Loc.Get("gp.ui.subtitle") };
        _subtitleLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _subtitleLabel.AddThemeFontSizeOverride("font_size", 12);
        _subtitleLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        titleCol.AddChild(_subtitleLabel);

        var tabs = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkEnd
        };
        tabs.AddThemeConstantOverride("separation", 4);
        head.AddChild(tabs);

        var tabKeys = new[] { "gp.ui.tab.ours", "gp.ui.tab.commission", "gp.ui.tab.world" };
        for (var i = 0; i < 3; i++)
        {
            var idx = (TabKind)i;
            var btn = new Button
            {
                Text = Loc.Get(tabKeys[i]),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 36),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                FocusMode = Control.FocusModeEnum.None
            };
            btn.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            btn.AddThemeFontSizeOverride("font_size", 14);
            btn.Pressed += () =>
            {
                _ctx.PlayCue?.Invoke("ui_select");
                _activeTab = idx;
                _lastSignature = "";
                Refresh();
            };
            tabs.AddChild(btn);
            _tabButtons[i] = btn;
        }

        var closeBtn = new Button
        {
            Text = "×",
            TooltipText = Loc.Get("gp.ui.close"),
            CustomMinimumSize = new Vector2(36, 36),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        closeBtn.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        closeBtn.AddThemeFontSizeOverride("font_size", 18);
        closeBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        closeBtn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); Close(); };
        head.AddChild(closeBtn);

        _summaryLabel = new Label();
        _summaryLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        _summaryLabel.AddThemeFontSizeOverride("font_size", 12);
        _summaryLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        parent.AddChild(_summaryLabel);

        _bannerLabel = new Label
        {
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _bannerLabel.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        _bannerLabel.AddThemeFontSizeOverride("font_size", 12);
        _bannerLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.brass_hi"));
        parent.AddChild(_bannerLabel);
    }

    private void BuildBody(VBoxContainer parent)
    {
        var bodyBox = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var bodyStyle = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.body_bottom"),
            BorderColor = Ui.Colour("shell.engrave"),
            ContentMarginLeft = 14,
            ContentMarginRight = 18,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        bodyStyle.SetBorderWidthAll(1);
        bodyBox.AddThemeStyleboxOverride("panel", bodyStyle);
        parent.AddChild(bodyBox);

        _scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        bodyBox.AddChild(_scroll);

        _content = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _content.AddThemeConstantOverride("separation", 10);
        _scroll.AddChild(_content);
    }

    public void Open()
    {
        if (_layer is null) return;
        _ctx.PlayCue?.Invoke("shell_drawer_slide");
        _layer.Visible = true;
        _layer.MoveToFront();
        _lastSignature = "";
        Refresh();
    }

    public void Close()
    {
        if (_layer is not null) _layer.Visible = false;
        _ctx.RefreshAll?.Invoke();
    }

    public void Toggle()
    {
        if (IsVisible) Close();
        else Open();
    }

    public void Layout(ShellLayoutProfile profile)
    {
        if (_panel is null || profile is null) return;
        ShellContext.PlaceRect(_panel, profile.FullWorkspace);
    }

    public void Refresh()
    {
        if (!IsVisible) return;
        var world = _ctx.World;
        if (world is null) return;
        var viewer = ResolveViewedNation(world);
        var observer = _ctx.IsObserverMode?.Invoke() ?? false;

        var sig = BuildSignature(world, viewer, observer);
        if (ReferenceEquals(_lastWorld, world) && sig == _lastSignature) return;
        _lastWorld = world;
        _lastSignature = sig;

        // Refresh static localized chrome in case language changed.
        _titleLabel.Text = Loc.Get("gp.ui.title");
        _subtitleLabel.Text = Loc.Get("gp.ui.subtitle");
        var tabKeys = new[] { "gp.ui.tab.ours", "gp.ui.tab.commission", "gp.ui.tab.world" };
        for (var i = 0; i < 3; i++)
        {
            var on = (int)_activeTab == i;
            _tabButtons[i].Text = Loc.Get(tabKeys[i]);
            _tabButtons[i].AddThemeStyleboxOverride("normal", on
                ? _ctx.NineSlice("tab_folder_9s", 10, 10, 5, 10, 5)
                : _ctx.PressedWellStyle(padX: 10, padY: 5));
            _tabButtons[i].AddThemeColorOverride("font_color", Ui.Colour(on ? "shell.paper" : "shell.paper_faint"));
        }

        var enabled = GrandProjects.IsEnabled(world);
        var activeCount = viewer is not null ? GrandProjects.CountActive(world, viewer.Id) : 0;
        var doneCount = viewer is not null ? GrandProjects.CountCompleted(world, viewer.Id) : 0;
        _summaryLabel.Text = Loc.Format("gp.ui.summary_fmt",
            activeCount, Math.Max(1, world.Defines.GrandProjectMaxActivePerNation),
            doneCount, Math.Max(1, world.Defines.GrandProjectMaxCompletedPerNation),
            world.GrandProjects.Count);

        if (!enabled)
        {
            _bannerLabel.Visible = true;
            _bannerLabel.Text = Loc.Get("gp.ui.disabled_banner");
        }
        else if (observer)
        {
            _bannerLabel.Visible = true;
            _bannerLabel.Text = Loc.Format("gp.ui.observer_banner", viewer?.DisplayName ?? "—");
        }
        else
        {
            _bannerLabel.Visible = false;
        }

        ClearChildren(_content);
        if (viewer is null) return;

        switch (_activeTab)
        {
            case TabKind.Ours: BuildOursTab(world, viewer, observer); break;
            case TabKind.Commission: BuildCommissionTab(world, viewer, observer); break;
            case TabKind.World: BuildWorldTab(world, viewer, observer); break;
        }
    }

    private Nation ResolveViewedNation(Campaign world)
    {
        if ((_ctx.IsObserverMode?.Invoke() ?? false) && _ctx.GetSelectedNation?.Invoke() is { } selected)
            return selected;
        return ObserverUiRules.ResolveInspectedNation(world, _ctx.GetSelectedNation?.Invoke(), preferLanded: true);
    }

    private int _lastSigHash;

    private string BuildSignature(Campaign world, Nation viewer, bool observer)
    {
        var hc = new HashCode();
        hc.Add(world.ElapsedDays);
        hc.Add(world.TradeRoutesDay);
        hc.Add(world.MapRevision);
        hc.Add(Loc.Language, StringComparer.Ordinal);
        hc.Add((int)_activeTab);
        hc.Add(_selectedStateMapIndex);
        hc.Add(viewer?.Id ?? -1);
        hc.Add(observer);
        hc.Add(GrandProjects.IsEnabled(world));
        for (var i = 0; i < world.GrandProjects.Count; i++)
        {
            var p = world.GrandProjects[i];
            hc.Add(p.Id); hc.Add(p.HostNationId); hc.Add(p.Stage);
            hc.Add((int)p.ProgressDays); hc.Add((int)p.Status);
            hc.Add(p.PartnerNationId); hc.Add(p.PartnerActive);
        }
        var hash = hc.ToHashCode();
        if (ReferenceEquals(_lastWorld, world) && !string.IsNullOrEmpty(_lastSignature) && _lastSigHash == hash)
            return _lastSignature;
        _lastSigHash = hash;
        return _lastSignature == "a" ? "b" : "a";
    }

    private void BuildOursTab(Campaign world, Nation nation, bool observer)
    {
        AddSectionHeader(Loc.Get("gp.ui.section.active"));
        var active = world.GrandProjects.Where(p => p.HostNationId == nation.Id && p.Status != GrandProjectStatus.Completed).ToList();
        if (active.Count == 0)
        {
            AddMutedNote(Loc.Get("gp.ui.no_active"));
        }
        else
        {
            foreach (var inst in active) AddOwnedProjectCard(world, nation, inst, observer);
        }

        AddSectionHeader(Loc.Get("gp.ui.section.completed"));
        var completed = world.GrandProjects.Where(p => p.HostNationId == nation.Id && p.Status == GrandProjectStatus.Completed).ToList();
        if (completed.Count == 0)
        {
            AddMutedNote(Loc.Get("gp.ui.no_completed"));
        }
        else
        {
            foreach (var inst in completed) AddOwnedProjectCard(world, nation, inst, observer);
        }

        var dividends = world.GrandProjects.Where(p => p.PartnerNationId == nation.Id && p.HostNationId != nation.Id).ToList();
        if (dividends.Count > 0)
        {
            AddSectionHeader(Loc.Get("gp.ui.section.partner_dividends"));
            foreach (var inst in dividends) AddWorldProjectCard(world, nation, inst, observer);
        }

        // Emergent Supply-Chain & Chokepoint Leverage (read-only single-pass TradeRoutes snapshot)
        var exposures = StrategicChokepoints.ReadForNation(world, nation);
        if (exposures.Count > 0)
        {
            AddSectionHeader(Loc.Get("gp.ui.section.chokepoints"));
            var limit = Math.Min(6, exposures.Count);
            for (var i = 0; i < limit; i++) AddChokepointCard(world, exposures[i]);
        }
    }

    private void AddChokepointCard(Campaign world, ChokepointExposure exp)
    {
        var supplier = world.NationById(exp.SupplierNationId);
        var goodName = Loc.Get(CommodityCatalog.Get(exp.Commodity).Name);
        var card = CreateCardContainer();
        card.ClipContents = true;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        card.AddChild(row);

        var statusKey = exp.Status switch
        {
            ChokepointStatus.Squeezed => "gp.choke.status.squeezed",
            ChokepointStatus.HealthyAnchor => "gp.choke.status.anchor",
            _ => "gp.choke.status.concentrated"
        };
        var colorToken = exp.Status switch
        {
            ChokepointStatus.Squeezed => "shell.state.negative",
            ChokepointStatus.HealthyAnchor => "shell.state.positive",
            _ => "shell.brass_hi"
        };

        var mainText = Loc.Format("gp.choke.row_fmt",
            goodName,
            supplier?.DisplayName ?? "—",
            MathF.Round(exp.DependencyShare * 100f),
            MathF.Round(exp.MarketSatisfaction * 100f));
        if (exp.ImpactsActiveMegaproject)
            mainText += "  ·  " + Loc.Get("gp.choke.hits_project");

        var lbl = new Label
        {
            Text = mainText,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        lbl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(lbl);

        var badge = new Label
        {
            Text = Loc.Get(statusKey),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        badge.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        badge.AddThemeFontSizeOverride("font_size", 12);
        badge.AddThemeColorOverride("font_color", Ui.Colour(colorToken));
        row.AddChild(badge);
    }

    private void BuildCommissionTab(Campaign world, Nation nation, bool observer)
    {
        var states = world.StatesOf(nation).OrderByDescending(s => s.Population).ToArray();
        if (states.Length > 0 && !states.Any(s => s.MapIndex == _selectedStateMapIndex))
            _selectedStateMapIndex = states[0].MapIndex;

        var pickerRow = new HBoxContainer();
        pickerRow.AddThemeConstantOverride("separation", 10);
        _content.AddChild(pickerRow);

        var regionLabel = new Label
        {
            Text = Loc.Get("gp.ui.select_region"),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        regionLabel.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        regionLabel.AddThemeFontSizeOverride("font_size", 14);
        regionLabel.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        pickerRow.AddChild(regionLabel);

        var regionPicker = new OptionButton
        {
            CustomMinimumSize = new Vector2(280, 36),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None
        };
        regionPicker.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        regionPicker.AddThemeFontSizeOverride("font_size", 13);
        var selectedIdx = 0;
        for (var i = 0; i < states.Length; i++)
        {
            var st = states[i];
            var occupiedTag = GrandProjects.FindInState(world, st.MapIndex) is not null ? " ★" : "";
            regionPicker.AddItem($"{st.DisplayName} ({Fmt.People(st.Population)}, {MathF.Round(st.Literacy * 100f)}%){occupiedTag}", st.MapIndex);
            if (st.MapIndex == _selectedStateMapIndex) selectedIdx = i;
        }
        if (states.Length > 0) regionPicker.Selected = selectedIdx;
        regionPicker.ItemSelected += idx =>
        {
            var mapIdx = regionPicker.GetItemId((int)idx);
            _selectedStateMapIndex = mapIdx;
            _lastSignature = "";
            Refresh();
        };
        pickerRow.AddChild(regionPicker);

        var selectedState = world.StateByMapIndex(_selectedStateMapIndex);
        foreach (var def in GrandProjectCatalog.All)
            AddBlueprintCard(world, nation, selectedState, def, observer);
    }

    private void BuildWorldTab(Campaign world, Nation viewer, bool observer)
    {
        var foreign = world.GrandProjects
            .Where(p => p.HostNationId != viewer.Id)
            .OrderBy(p => p.Status == GrandProjectStatus.Completed ? 1 : 0)
            .ThenBy(p => p.Id)
            .ToList();
        if (foreign.Count == 0)
        {
            AddMutedNote(Loc.Get("gp.ui.no_world"));
            return;
        }
        foreach (var inst in foreign)
            AddWorldProjectCard(world, viewer, inst, observer);
    }

    private void AddOwnedProjectCard(Campaign world, Nation nation, GrandProjectInstance inst, bool observer)
    {
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        if (def is null) return;
        var state = world.StateByMapIndex(inst.StateMapIndex);
        var card = CreateCardContainer();
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        card.AddChild(col);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 8);
        col.AddChild(top);

        var title = new Label
        {
            Text = $"{def.DisplayName}  ·  {state?.DisplayName ?? "—"}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        title.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        title.AddThemeFontSizeOverride("font_size", 15);
        title.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        top.AddChild(title);

        var statusBadge = new Label
        {
            Text = StatusCaption(world, inst),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        statusBadge.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        statusBadge.AddThemeFontSizeOverride("font_size", 12);
        statusBadge.AddThemeColorOverride("font_color",
            Ui.Colour(inst.Status == GrandProjectStatus.Completed ? "shell.state.positive" : "shell.brass_hi"));
        top.AddChild(statusBadge);

        if (inst.Status != GrandProjectStatus.Completed)
        {
            var pct = inst.StageDurationDays > 0 ? M.Clamp01(inst.ProgressDays / inst.StageDurationDays) : 0f;
            var pace = GrandProjects.DailyBuildPace(world, inst, out var bottleGood, out var bottleSat);
            var remDays = pace > 0.01f ? (int)MathF.Ceiling(MathF.Max(0f, inst.StageDurationDays - inst.ProgressDays) / pace) : 0;

            var prog = new ProgressBar
            {
                MinValue = 0,
                MaxValue = 100,
                Value = pct * 100f,
                ShowPercentage = false,
                CustomMinimumSize = new Vector2(0, 10)
            };
            col.AddChild(prog);

            var stageLine = new Label
            {
                Text = Loc.Format("gp.ui.progress_fmt", Loc.Get($"gp.stage.{inst.Stage}"),
                           MathF.Round(pct * 100f).ToString("0", Loc.Culture), remDays)
                       + "  ·  " + Loc.Format("gp.ui.speed_fmt", pace.ToString("0.00", Loc.Culture))
                       + "  ·  " + Loc.Format("gp.ui.invested_fmt", Fmt.Money(inst.TotalInvestedBillions)),
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            stageLine.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            stageLine.AddThemeFontSizeOverride("font_size", 12);
            stageLine.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
            col.AddChild(stageLine);

            if (!string.IsNullOrEmpty(bottleGood) && bottleSat < 0.90f)
            {
                var warn = new Label
                {
                    Text = Loc.Format("gp.ui.bottleneck_fmt", bottleGood, MathF.Round(bottleSat * 100f)),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                warn.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
                warn.AddThemeFontSizeOverride("font_size", 12);
                warn.AddThemeColorOverride("font_color", Ui.Colour("shell.state.negative"));
                col.AddChild(warn);
            }
        }

        // Consortium status line
        var partner = inst.PartnerNationId >= 0 ? world.NationById(inst.PartnerNationId) : null;
        var partnerText = partner is null
            ? Loc.Get("gp.ui.partner_none")
            : inst.PartnerActive
                ? Loc.Format("gp.ui.partner_active_fmt", partner.DisplayName)
                : Loc.Format("gp.ui.partner_fractured_fmt", partner.DisplayName);
        var partnerLbl = new Label { Text = partnerText, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        partnerLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        partnerLbl.AddThemeFontSizeOverride("font_size", 12);
        partnerLbl.AddThemeColorOverride("font_color", Ui.Colour(inst.PartnerActive ? "shell.state.positive" : "shell.paper_faint"));
        col.AddChild(partnerLbl);

        AddModifierSummary(col, Loc.Get("gp.ui.host_bonuses"), def.HostModifiers);

        // Action strip (Advance Stage / Pause-Resume / Cancel)
        if (inst.Status != GrandProjectStatus.Completed)
        {
            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 8);
            col.AddChild(actions);

            if (inst.Status == GrandProjectStatus.AwaitingStageFunding && inst.Stage < 3)
            {
                var advReason = "";
                var canAdv = !observer && GrandProjects.CanAdvanceStage(world, nation, inst, out advReason);
                var nextCost = GrandProjects.StageTreasuryCost(world, nation, def, inst.Stage + 1, inst.PartnerActive);
                var advBtn = CreateActionButton(
                    $"{Loc.Get("gp.ui.btn.advance")} ({Fmt.Money(nextCost)})",
                    primary: true, enabled: canAdv,
                    tooltip: observer ? Loc.Get("gp.refuse.observer") : (canAdv ? "" : advReason),
                    () => ExecuteCommand(world, new AdvanceGrandProjectStageCommand(nation.Id, inst.Id)));
                actions.AddChild(advBtn);
            }

            var wantPause = !inst.PausedByPlayer;
            var pReason = "";
            var canToggle = !observer && GrandProjects.CanTogglePause(world, nation, inst, wantPause, out pReason);
            var pauseBtn = CreateActionButton(
                Loc.Get(wantPause ? "gp.ui.btn.pause" : "gp.ui.btn.resume"),
                primary: false, enabled: canToggle,
                tooltip: observer ? Loc.Get("gp.refuse.observer") : (canToggle ? "" : pReason),
                () => ExecuteCommand(world, new ToggleGrandProjectPauseCommand(nation.Id, inst.Id, wantPause)));
            actions.AddChild(pauseBtn);

            var cReason = "";
            var canCancel = !observer && GrandProjects.CanCancel(world, nation, inst, out cReason);
            var cancelBtn = CreateActionButton(
                Loc.Get("gp.ui.btn.cancel"),
                primary: false, enabled: canCancel,
                tooltip: observer ? Loc.Get("gp.refuse.observer") : (canCancel ? "" : cReason),
                () => ExecuteCommand(world, new CancelGrandProjectCommand(nation.Id, inst.Id)));
            actions.AddChild(cancelBtn);
        }
    }

    private void AddBlueprintCard(Campaign world, Nation nation, ScenarioState state, GrandProjectDefinition def, bool observer)
    {
        var card = CreateCardContainer();
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        card.AddChild(col);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        col.AddChild(head);

        var name = new Label
        {
            Text = $"{def.DisplayName}  ·  [{def.CategoryName}]" + (def.UniqueGlobal ? $" · {Loc.Get("gp.ui.unique_global")}" : ""),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        name.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        name.AddThemeFontSizeOverride("font_size", 15);
        name.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        head.AddChild(name);

        var desc = new Label
        {
            Text = def.DisplayDescription,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        desc.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        desc.AddThemeFontSizeOverride("font_size", 12);
        desc.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        col.AddChild(desc);

        var stage1Cost = GrandProjects.StageTreasuryCost(world, nation, def, 1, hasActivePartner: false);
        var stage1Pp = GrandProjects.StagePoliticalCost(def, 1);
        var stage1Xp = GrandProjects.StageExperienceCost(def, 1);
        var costText = stage1Xp > 0f && !string.IsNullOrEmpty(def.ExperienceCurrency)
            ? Loc.Format("gp.ui.stage_cost_xp_fmt", Fmt.Money(stage1Cost), MathF.Round(stage1Pp),
                DecisionResources.Name(def.ExperienceCurrency), MathF.Round(stage1Xp))
            : Loc.Format("gp.ui.stage_cost_fmt", Fmt.Money(stage1Cost), MathF.Round(stage1Pp));
        var durText = Loc.Format("gp.ui.duration_fmt", def.DurationForStage(1), def.DurationForStage(2), def.DurationForStage(3));

        var meta = new Label { Text = $"{costText}  ·  {durText}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        meta.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        meta.AddThemeFontSizeOverride("font_size", 12);
        meta.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        col.AddChild(meta);

        AddModifierSummary(col, Loc.Get("gp.ui.host_bonuses"), def.HostModifiers);
        AddModifierSummary(col, Loc.Get("gp.ui.partner_bonuses"), def.PartnerModifiers);

        var refusal = "";
        var canStart = !observer && GrandProjects.CanStart(world, nation, state, def.Id, out refusal);
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 10);
        col.AddChild(foot);

        var startBtn = CreateActionButton(
            Loc.Get("gp.ui.btn.commission"),
            primary: true, enabled: canStart,
            tooltip: observer ? Loc.Get("gp.refuse.observer") : (canStart ? "" : refusal),
            () =>
            {
                if (state is null) return;
                ExecuteCommand(world, new StartGrandProjectCommand(nation.Id, def.Id, state.MapIndex));
                _activeTab = TabKind.Ours;
            });
        foot.AddChild(startBtn);

        if (!canStart)
        {
            var refLbl = new Label
            {
                Text = observer ? Loc.Get("gp.refuse.observer") : refusal,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            refLbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
            refLbl.AddThemeFontSizeOverride("font_size", 12);
            refLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.state.negative"));
            foot.AddChild(refLbl);
        }
    }

    private void AddWorldProjectCard(Campaign world, Nation viewer, GrandProjectInstance inst, bool observer)
    {
        var def = GrandProjectCatalog.Find(inst.DefinitionId);
        var host = world.NationById(inst.HostNationId);
        var state = world.StateByMapIndex(inst.StateMapIndex);
        if (def is null || host is null) return;

        var card = CreateCardContainer();
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        card.AddChild(col);

        var title = new Label
        {
            Text = Loc.Format("gp.ui.world_row_fmt", def.DisplayName, host.DisplayName, state?.DisplayName ?? "—")
                   + $"  ·  {StatusCaption(world, inst)}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        title.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        col.AddChild(title);

        AddModifierSummary(col, Loc.Get("gp.ui.partner_bonuses"), def.PartnerModifiers);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        col.AddChild(actions);

        if (inst.Status != GrandProjectStatus.Completed)
        {
            var joinReason = "";
            var canJoin = !observer && GrandProjects.CanJoinConsortium(world, viewer, inst, out joinReason);
            var joinCost = GrandProjects.PartnerContributionCost(world, host, def, inst.Stage);
            var joinPp = GrandProjects.PartnerPoliticalCost(def);
            var joinBtn = CreateActionButton(
                $"{Loc.Get("gp.ui.btn.join")} ({Fmt.Money(joinCost)})",
                primary: true, enabled: canJoin,
                tooltip: observer
                    ? Loc.Get("gp.refuse.observer")
                    : (canJoin ? Loc.Format("gp.ui.join_cost_fmt", Fmt.Money(joinCost), MathF.Round(joinPp)) : joinReason),
                () => ExecuteCommand(world, new JoinGrandProjectConsortiumCommand(viewer.Id, inst.Id)));
            actions.AddChild(joinBtn);

            var sabReason = "";
            var canSab = !observer && GrandProjects.CanSabotage(world, viewer, inst, out sabReason);
            var intelPct = MathF.Round(IntelNetworks.Strength(world, viewer.Id, host.Id) * 100f);
            var sabCost = GrandProjects.SabotageCost(world, host, def, inst.Stage);
            var sabBtn = CreateActionButton(
                Loc.Get("gp.ui.btn.sabotage"),
                primary: false, enabled: canSab,
                tooltip: observer
                    ? Loc.Get("gp.refuse.observer")
                    : (canSab
                        ? Loc.Format("gp.ui.sabotage_info_fmt", intelPct, Fmt.Money(sabCost), MathF.Round(world.Defines.GrandProjectSabotagePoliticalCost))
                        : sabReason),
                () => ExecuteCommand(world, new SabotageGrandProjectCommand(viewer.Id, inst.Id)));
            actions.AddChild(sabBtn);
        }
    }

    private void AddSectionHeader(string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var label = new Label { Text = text };
        label.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", Ui.Colour("shell.brass_hi"));
        row.AddChild(label);

        var rule = new ColorRect
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(12, 1),
            Color = Ui.Colour("shell.engrave")
        };
        row.AddChild(rule);
        _content.AddChild(row);
    }

    private void AddMutedNote(string text)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_faint"));
        _content.AddChild(label);
    }

    private void AddModifierSummary(VBoxContainer parent, string heading, IReadOnlyDictionary<string, float> mods)
    {
        if (mods is null || mods.Count == 0) return;
        var lines = NationalModifiers.Lines(mods);
        if (lines.Count == 0) return;
        var label = new Label
        {
            Text = $"{heading} {string.Join("  ·  ", lines)}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", Ui.Colour("shell.state.positive"));
        parent.AddChild(label);
    }

    private PanelContainer CreateCardContainer()
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.plate_top"),
            BorderColor = Ui.Colour("shell.border"),
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3
        };
        style.SetBorderWidthAll(1);
        card.AddThemeStyleboxOverride("panel", style);
        _content.AddChild(card);
        return card;
    }

    private Button CreateActionButton(string text, bool primary, bool enabled, string tooltip, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            Disabled = !enabled,
            TooltipText = tooltip ?? "",
            CustomMinimumSize = new Vector2(148, 36),
            MouseDefaultCursorShape = enabled ? Control.CursorShape.PointingHand : Control.CursorShape.Arrow,
            FocusMode = Control.FocusModeEnum.None
        };
        var style = primary
            ? _ctx.NineSlice("button_brass_cta_9s", 10, 14, 5, 14, 5, "shell.plate_top", "shell.brass_hi", 3)
            : _ctx.NineSlice("button_umber_9s", 10, 12, 4, 12, 4, "shell.plate_bottom", "shell.engrave", 3);
        b.AddThemeStyleboxOverride("normal", style);
        b.AddThemeStyleboxOverride("hover", style);
        b.AddThemeFontOverride("font", _ctx.Font(primary ? "shell.serif_semibold" : "shell.sans", tabular: true));
        b.AddThemeFontSizeOverride("font_size", 13);
        b.AddThemeColorOverride("font_color", Ui.Colour(enabled ? "shell.paper" : "shell.paper_faint"));
        b.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            onClick();
        };
        return b;
    }

    private void ExecuteCommand(Campaign world, ICampaignCommand command)
    {
        if (world is null || command is null) return;
        world.Commands.Enqueue(command);
        world.Commands.ApplyAll(world);
        _lastSignature = "";
        Refresh();
        _ctx.RefreshAll?.Invoke();
    }

    private static string StatusCaption(Campaign world, GrandProjectInstance inst)
    {
        if (inst.Status == GrandProjectStatus.Completed)
            return GrandProjects.IsOperationallyActive(world, inst)
                ? Loc.Get("gp.status.completed")
                : Loc.Get("gp.status.completed_damaged");
        return inst.Status switch
        {
            GrandProjectStatus.Active => Loc.Get("gp.status.active"),
            GrandProjectStatus.AwaitingStageFunding => Loc.Get("gp.status.awaiting"),
            GrandProjectStatus.Paused => Loc.Get("gp.status.paused"),
            GrandProjectStatus.Damaged => Loc.Get("gp.status.damaged"),
            _ => Loc.Get("gp.status.active")
        };
    }

    private static void ClearChildren(Node parent)
    {
        if (parent is null) return;
        foreach (var child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }
}
