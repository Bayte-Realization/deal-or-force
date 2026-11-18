using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

static class NewShellTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        Console.WriteLine("PASS new shell: " + message);
    }

    public static void RunAll()
    {
        RunRestLayout();
        RunAlertDedupe();
        RunHotkeyMap();
        RunMessages();
        RunDatePlate();
    }

    public static void RunRestLayout()
    {
        var tempSettingsDir = Path.Combine(Path.GetTempPath(), "dof-newshell-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempSettingsDir);
        try
        {
            PlayerSettings.UseDirectory(tempSettingsDir);
            InterfaceSkin.ResetCatalog();
            SettingsCatalog.ResetCatalog();
            FontCatalog.Load();
            SpriteCatalog.Load();
            FlagCatalog.ResetCatalog();

            // Wave 2 defaults: ui_shell == "new", ui_shell_minimap == true, message preset == Calm
            Check(SettingsCatalog.Of("ui_shell") is not null && SettingsCatalog.Chosen("ui_shell") == "new",
                "ui_shell setting defaults cleanly to new");
            Check(SettingsCatalog.Of("ui_shell_minimap") is not null && SettingsCatalog.Flag("ui_shell_minimap"),
                "live political minimap ships ON by default");
            Check(SettingsCatalog.Of(ShellMessages.PresetSetting) is not null &&
                  ShellMessages.ParsePreset(SettingsCatalog.Chosen(ShellMessages.PresetSetting)) == ShellMessagePreset.Calm,
                "message preset defaults to Calm");

            // Verify one-time migration upgrades unmigrated classic -> new once, then respects manual classic choice
            PlayerSettings.Set("ui_shell", "classic");
            PlayerSettings.Set("ui_shell_minimap", false);
            PlayerSettings.Set(ShellMessages.PresetSetting, "paradox");
            ShellSettingsMigration.ApplyOnce();
            Check(SettingsCatalog.Chosen("ui_shell") == "new" &&
                  SettingsCatalog.Flag("ui_shell_minimap") &&
                  SettingsCatalog.Chosen(ShellMessages.PresetSetting) == "focus",
                "ShellSettingsMigration upgrades pre-Wave-2 settings once and normalizes legacy preset");
            SettingsCatalog.Store("ui_shell", "classic");
            ShellSettingsMigration.ApplyOnce();
            Check(SettingsCatalog.Chosen("ui_shell") == "classic",
                "ShellSettingsMigration preserves explicit user switch back to classic");
            SettingsCatalog.Store("ui_shell", "new");

            var root = GameDataLoader.DataFolder();
            var shellSettingsText = File.ReadAllText(Path.Combine(root, "common", "settings", "46_ui_shell.json"));
            using (var shellDoc = System.Text.Json.JsonDocument.Parse(shellSettingsText))
            {
                var shellKeys = shellDoc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                Check(!shellKeys.Contains("mouse_scheme") && !shellKeys.Contains("rmb_everywhere"),
                    "46_ui_shell.json avoids overlapping controls-ux setting IDs");
            }
            Check(InterfaceSkin.Metric(InterfaceSkin.MaterialSwitch, 0f) < 0.5f,
                "90_shell.json leaves classic ui.materials = 0 intact");
            Check(InterfaceSkin.Has("shell.paper") && InterfaceSkin.Has("shell.plate_top") && InterfaceSkin.Has("shell.brass_hi"),
                "90_shell.json declares neutral alabaster, charcoal-graphite plate, and brushed titanium tokens");

            // De-yellow & zero-blue palette guardrail
            var skinText = File.ReadAllText(Path.Combine(root, "interface", "00_skin.json"));
            Check(!skinText.Contains("#1d4477", StringComparison.OrdinalIgnoreCase),
                "00_skin.json contains zero #1d4477 cold blue button/tab hexes");
            var (pr, _, pb, _) = InterfaceSkin.Rgba("shell.paper");
            Check(MathF.Abs(pr - pb) <= 0.05f, $"shell.paper is crisp neutral alabaster without yellow cast (R={pr:0.00}, B={pb:0.00})");

            Check(File.Exists(Path.Combine(root, "fonts", "SourceSerif4-Regular.ttf")) &&
                  File.Exists(Path.Combine(root, "fonts", "SourceSerif4-SemiBold.ttf")) &&
                  File.Exists(Path.Combine(root, "fonts", "SourceSerif4-Bold.ttf")),
                "Source Serif 4 regular/semibold/bold TTF assets ship in game/fonts");
            Check(FontCatalog.FileOf("shell.serif_semibold").EndsWith("SourceSerif4-SemiBold.ttf", StringComparison.OrdinalIgnoreCase),
                "90_shell_fonts.json binds SourceSerif4-SemiBold.ttf");

            // Verify Blender-baked 9-slice shell frames + paper.jpg + .import sidecars (< 1.5 MB)
            var shellArtDir = Path.Combine(root, "gfx", "interface", "shell");
            Check(Directory.Exists(shellArtDir) && File.Exists(Path.Combine(shellArtDir, "paper.jpg")) &&
                  File.Exists(Path.Combine(shellArtDir, "paper.jpg.import")),
                "Blender-baked shell directory and paper.jpg + .import exist");
            var artBytes = 0L;
            foreach (var f in Directory.GetFiles(shellArtDir))
            {
                if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                {
                    artBytes += new FileInfo(f).Length;
                    Check(File.Exists(f + ".import"), "import sidecar exists for " + Path.GetFileName(f));
                }
            }
            Check(artBytes > 20_000 && artBytes <= 1_500_000, $"shell baked textures total {artBytes / 1024.0:0.1} KB <= 1500 KB");

            var allShellIcons = new[]
            {
                "icon_shell_2d", "icon_shell_3d", "icon_shell_back", "icon_shell_capital", "icon_shell_close",
                "icon_shell_crane", "icon_shell_detach", "icon_shell_eye", "icon_shell_fight", "icon_shell_front",
                "icon_shell_gather", "icon_shell_march", "icon_shell_menu", "icon_shell_minimap", "icon_shell_more",
                "icon_shell_news", "icon_shell_nickel", "icon_shell_pause", "icon_shell_pin", "icon_shell_play",
                "icon_shell_political", "icon_shell_scroll", "icon_shell_stand", "icon_shell_tension", "icon_shell_tick",
                "icon_shell_treaty", "icon_shell_undo", "icon_shell_warmode", "icon_shell_warning"
            };
            foreach (var icon in allShellIcons)
            {
                Check(SpriteCatalog.Has(icon), "shell monochrome 128x128 icon resolves: " + icon);
                var iconDisk = Path.Combine(root, "gfx", "interface", "icons", icon + ".png");
                Check(File.Exists(iconDisk) && File.Exists(iconDisk + ".import"), "icon PNG + .import exist: " + icon);
            }

            // Verify Qatar 28:11 natural aspect ratio
            var qatPath = Path.Combine(root, "gfx", "interface", "flags") is var _
                ? Path.Combine(root, "gfx", "flags", "flag_qat.png")
                : "";
            Check(File.Exists(qatPath), "flag_qat.png exists");
            using (var fs = File.OpenRead(qatPath))
            {
                Span<byte> hdr = stackalloc byte[24];
                Check(fs.Read(hdr) == 24, "flag_qat.png header readable");
                var qw = BinaryPrimitives.ReadInt32BigEndian(hdr[16..20]);
                var qh = BinaryPrimitives.ReadInt32BigEndian(hdr[20..24]);
                var qRatio = (float)qw / qh;
                Check(MathF.Abs(qRatio - 28f / 11f) < 0.02f, $"flag_qat.png has natural 28:11 ratio ({qw}x{qh} = {qRatio:0.000})");
            }

            // Verify natural-aspect fitting (no shield crop, no stretching)
            var fit23 = ShellLayout.FitFlag(768, 512, 96, 48, "SRB");
            var fit11 = ShellLayout.FitFlag(512, 512, 96, 48, "CHE");
            var fitQat = ShellLayout.FitFlag(1303, 512, 96, 48, "QAT");
            var fitNpl = ShellLayout.FitFlag(420, 512, 96, 48, "NPL");
            Check(MathF.Abs(fit23.Width / fit23.Height - 1.5f) < 0.02f &&
                  MathF.Abs(fit11.Width / fit11.Height - 1.0f) < 0.02f &&
                  MathF.Abs(fitQat.Width / fitQat.Height - 28f / 11f) < 0.03f &&
                  fitNpl.CutoutSilhouette && !fit23.CutoutSilhouette,
                "ShellLayout.FitFlag preserves 2:3, 1:1, 28:11, and Nepal cutout silhouette");

            // News card top-center placement (§11 item 11: 700,82-1240,196 at 1080p; 460,56-830,132 at 720p)
            var p1080 = ShellLayout.ForViewport(1920f, 1080f, false);
            var p720 = ShellLayout.ForViewport(1280f, 720f, false);
            Check(MathF.Abs(p1080.NewsCard.X - 700f) < 1f && MathF.Abs(p1080.NewsCard.Y - 82f) < 1f &&
                  MathF.Abs(p720.NewsCard.X - 460f) < 1f && MathF.Abs(p720.NewsCard.Y - 56f) < 1f,
                $"News clipping sits top-center at 1080p ({p1080.NewsCard.X},{p1080.NewsCard.Y}) and 720p ({p720.NewsCard.X},{p720.NewsCard.Y})");

            // Rest HUD & Dock anti-overlap census across 1920x1080 and 1280x720
            foreach (var (w, h) in new[] { (1920f, 1080f), (1280f, 720f) })
            {
                foreach (ShellCampaignState state in Enum.GetValues(typeof(ShellCampaignState)))
                {
                    foreach (var outlinerOpen in new[] { false, true })
                    {
                        foreach (var newsVisible in new[] { false, true })
                        {
                            foreach (var undoVisible in new[] { false, true })
                            {
                                var census = ShellLayout.AuditRestHud(w, h, state, outlinerOpen, newsVisible, undoVisible);
                                Check(census.Buttons <= ShellLayout.MaxRestButtons,
                                    $"{w}x{h} {state} out={outlinerOpen} news={newsVisible} undo={undoVisible}: buttons={census.Buttons} <= {ShellLayout.MaxRestButtons}");
                                Check(census.Panels <= ShellLayout.MaxRestPanels,
                                    $"{w}x{h} {state} out={outlinerOpen} news={newsVisible}: panels={census.Panels} <= {ShellLayout.MaxRestPanels}");
                                Check(census.Overlaps.Count == 0,
                                    $"{w}x{h} {state} out={outlinerOpen} news={newsVisible}: zero overlaps ({string.Join(", ", census.Overlaps)})");
                                Check(census.Offscreen.Count == 0,
                                    $"{w}x{h} {state} out={outlinerOpen} news={newsVisible}: zero offscreen ({string.Join(", ", census.Offscreen)})");
                            }
                        }
                    }
                }

                // Pairwise dock scenarios: ArmyPanel + PlanningBar + BottomStrip, LeftDossier + BottomStrip, FullWorkspace (DoctrineScreen/Economy)
                foreach (var atWar in new[] { false, true })
                {
                    var (_, ovArmy, offArmy) = ShellDockRules.AuditDockScenario(w, h, leftPanelOpen: true, planningBarOpen: true, atWar: atWar);
                    Check(ovArmy.Count == 0 && offArmy.Count == 0,
                        $"{w}x{h} war={atWar} ArmyPanel+PlanningBar+BottomStrip: overlaps={ovArmy.Count} ({string.Join(", ", ovArmy)}), offscreen={offArmy.Count}");

                    var (_, ovFull, offFull) = ShellDockRules.AuditDockScenario(w, h, leftPanelOpen: false, planningBarOpen: false, atWar: atWar, fullWorkspaceOpen: true);
                    Check(ovFull.Count == 0 && offFull.Count == 0,
                        $"{w}x{h} war={atWar} FullWorkspace (Doctrine/Economy)+BottomStrip+TopPlate: overlaps={ovFull.Count} ({string.Join(", ", ovFull)}), offscreen={offFull.Count}");
                }
            }

            // Map counter merge + CampaignFingerprint invariance
            var campaign = Campaign.CreateBootstrap(20260928);
            var fpBefore = CampaignFingerprint.Of(campaign);
            SettingsCatalog.Store("ui_shell", "new");

            var armies = new List<MapCounterInput>();
            for (var i = 0; i < 65; i++)
            {
                var isPlayer = i < 20;
                var isEnemy = i is >= 20 and < 35;
                armies.Add(new MapCounterInput(
                    i + 1, isPlayer ? 1 : (isEnemy ? 2 : 10 + (i % 12)),
                    isPlayer ? "PLR" : (isEnemy ? "ENM" : "NEU"),
                    i % 6, i % 3, 40.0 + (i % 10) * 1.2, 20.0 + (i / 10) * 1.5,
                    2.5f + (i % 4), isPlayer, false, isEnemy, i == 0, false));
            }
            foreach (MapZoomTier tier in Enum.GetValues(typeof(MapZoomTier)))
            {
                var clusters = MapCounterMerge.Merge(armies, tier, warMapMode: tier == MapZoomTier.TacticalClose, alertPinsCount: 4, maxSigns: 20);
                Check(clusters.Count + 4 <= 20, $"zoom tier {tier} merges counters cleanly to {clusters.Count} + 4 alerts <= 20 map signs");
                Check(clusters.Any(c => c.ContainsSelection), $"zoom tier {tier} preserves player selected division");
                if (tier != MapZoomTier.TacticalClose)
                    Check(clusters.All(c => c.NationId is 1 or 2), $"zoom tier {tier} hides idle peacetime neutral garrisons");
            }

            SettingsCatalog.Store("ui_shell", "classic");
            Check(CampaignFingerprint.Of(campaign) == fpBefore,
                "switching ui_shell and running layout/map audits leaves CampaignFingerprint identical");

            // Architectural Rule #88 + classic chrome guards + division visibility checks
            var uiRoot = Path.Combine(root, "..", "Scripts", "ui");
            foreach (var file in new[] { "ArmyBarUI.cs", "CampaignAlertsUI.cs", "SituationDeskUI.cs", "TeletypeUI.cs", "HeadlinePopupsUI.cs", "TopBarHudUI.cs" })
            {
                var src = File.ReadAllText(Path.Combine(uiRoot, file));
                Check(src.Contains("IsNewShellActive", StringComparison.Ordinal),
                    $"classic chrome refresh guards against IsNewShellActive in {file}");
            }

            var shellDir = Path.Combine(uiRoot, "Shell");
            var shellCsFiles = Directory.GetFiles(shellDir, "*.cs").OrderBy(f => f).ToArray();
            var partialFiles = shellCsFiles.Where(f => File.ReadAllText(f).Contains("partial class DealOrForceGame", StringComparison.Ordinal)).Select(Path.GetFileName).ToArray();
            Check(partialFiles.Length == 1 && partialFiles[0] == "NewShellUI.cs",
                $"Rule #88: only NewShellUI.cs is partial class DealOrForceGame in Scripts/ui/Shell ({string.Join(", ", partialFiles)})");
            foreach (var csFile in shellCsFiles)
            {
                var lineCount = File.ReadAllLines(csFile).Length;
                Check(lineCount < 800, $"Rule #88: {Path.GetFileName(csFile)} has {lineCount} lines < 800");
            }

            var stripSrc = File.ReadAllText(Path.Combine(shellDir, "ShellStripView.cs"));
            Check(!stripSrc.Contains("LayoutPreset.BottomLeft", StringComparison.Ordinal) &&
                  stripSrc.Contains("SetMergedVisibleArmies?.Invoke(null)", StringComparison.Ordinal),
                "ShellStripView anchors inside host and keeps all divisions visible at combat/working zoom");
            var cornerSrc = File.ReadAllText(Path.Combine(shellDir, "ShellCornerView.cs"));
            Check(!cornerSrc.Contains("flagInner.AddChild(rankBadge)", StringComparison.Ordinal),
                "ShellCornerView keeps rank badge outside flagInner so natural flags stay unobstructed");
        }
        finally
        {
            try { Directory.Delete(tempSettingsDir, recursive: true); } catch { }
        }
    }

    public static void RunAlertDedupe()
    {
        InterfaceSkin.ResetCatalog();
        SettingsCatalog.ResetCatalog();

        var campaign = Campaign.CreateBootstrap(20260928);
        var peaceNation = campaign.NationByTag("BRA") ?? campaign.Nations[0];
        var fpBefore = CampaignFingerprint.Of(campaign);

        var snapPeace = ShellAlerts.BuildSnapshot(campaign, peaceNation, outlinerOpen: false, nextHintsEnabled: true);
        Check(snapPeace.Alerts.Count <= 4, $"peace folded alerts {snapPeace.Alerts.Count} <= 4");
        if (snapPeace.NextHintShown)
            Check(snapPeace.Alerts[^1].IsNextHint && snapPeace.Alerts.Take(snapPeace.Alerts.Count - 1).All(a => !a.IsNextHint),
                "early-game Next hint is strictly LAST in alert column");
        Check(snapPeace.OutlinerGroups.All(g => g.Rows.Count > 0), "peace outliner contains zero empty groups");

        campaign.PendingEvents.Add(new PendingCampaignEvent
        {
            Id = 9901,
            NationId = peaceNation.Id,
            Title = "Border ultimatum",
            Description = "Respond within days.",
            CreatedDay = campaign.ElapsedDays,
            ExpiresDay = campaign.ElapsedDays + 3
        });

        var warNation = campaign.NationByTag("UKR") ?? campaign.NationByTag("RUS") ?? peaceNation;
        var viewed = new HashSet<string>(StringComparer.Ordinal);
        var snoozes = new CampaignAlertSnoozes();
        var watched = new HashSet<Commodity> { Commodity.Energy, Commodity.Food };

        var warFolded = ShellAlerts.BuildSnapshot(campaign, warNation, outlinerOpen: false, nextHintsEnabled: true, false, viewed, snoozes, watched);
        var warOutliner = ShellAlerts.BuildSnapshot(campaign, warNation, outlinerOpen: true, nextHintsEnabled: true, false, viewed, snoozes, watched);

        Check(warFolded.Alerts.Count <= 4, $"war folded alerts {warFolded.Alerts.Count} <= 4");
        Check(warOutliner.Alerts.Count <= 3, $"war open-outliner alerts {warOutliner.Alerts.Count} <= 3");
        Check(warOutliner.Alerts.Select(a => a.TopicKey).Distinct(StringComparer.Ordinal).Count() == warOutliner.Alerts.Count,
            "zero duplicate topics within alert rows");

        var alertTopics = warOutliner.Alerts.Select(a => a.TopicKey).ToHashSet(StringComparer.Ordinal);
        var outlinerTopics = warOutliner.OutlinerGroups.SelectMany(g => g.Rows).Select(r => r.TopicKey).ToArray();
        Check(outlinerTopics.All(t => !alertTopics.Contains(t)),
            "zero topic duplication between visible alert rows and outliner rows");
        Check(warOutliner.Alerts.All(a => a.TopicKey != "world_strip"),
            "alert column excludes duplicate World/DEFCON strip entry");
        Check(warOutliner.OutlinerGroups.All(g => g.Rows.Count > 0),
            "open outliner hides empty categories cleanly");

        var crisisSnap = ShellAlerts.BuildSnapshot(campaign, peaceNation, outlinerOpen: false, nextHintsEnabled: false);
        var evtRow = crisisSnap.Alerts.FirstOrDefault(a => a.Id == "event:9901");
        Check(evtRow is not null && !string.IsNullOrEmpty(evtRow.DeadlineLabel) && crisisSnap.Alerts[0].Id == evtRow.Id,
            $"urgent dated event sorts first with non-empty deadline '{evtRow?.DeadlineLabel}'");

        viewed.Add("event:9901");
        var snapViewed = ShellAlerts.BuildSnapshot(campaign, peaceNation, outlinerOpen: false, nextHintsEnabled: false, false, viewed, snoozes);
        Check(snapViewed.Alerts.First(a => a.Id == "event:9901").IsViewed, "viewed alert shows pencil-check flag");

        campaign.PendingEvents.RemoveAll(e => e.Id == 9901);
        Check(CampaignFingerprint.Of(campaign) == fpBefore, "alert dedupe & outliner read leaves CampaignFingerprint invariant");

        var lateCampaign = Campaign.CreateBootstrap(20260928);
        for (var d = 0; d < 62; d++) lateCampaign.Tick();
        var lateNation = lateCampaign.NationByTag("BRA") ?? lateCampaign.Nations[0];
        var snapLate = ShellAlerts.BuildSnapshot(lateCampaign, lateNation, outlinerOpen: false, nextHintsEnabled: true);
        Check(!snapLate.NextHintShown && snapLate.Alerts.All(a => !a.IsNextHint),
            "early-game Next guidance disappears after 60 days");
    }

    public static void RunHotkeyMap()
    {
        var collisions = ShellHotkeys.FindCollisions();
        Check(collisions.Count == 0, "zero collisions inside ShellHotkeys or against Controls bindings");

        Check(ShellHotkeys.TryResolve("Q", false, false, false, false, false, out var a) && a == ShellHotkeyAction.Country, "Q resolves Country");
        Check(ShellHotkeys.TryResolve("N", false, false, false, false, false, out a) && a == ShellHotkeyAction.MapWar, "N resolves MapWar");
        Check(ShellHotkeys.TryResolve("K", true, false, false, false, false, out a) && a == ShellHotkeyAction.Search, "Ctrl+K resolves Search");
        Check(ShellHotkeys.TryResolve("/", false, false, false, false, false, out a) && a == ShellHotkeyAction.Search, "/ resolves Search");
        Check(ShellHotkeys.TryResolve("J", false, false, false, false, false, out a) && a == ShellHotkeyAction.ToggleOutliner, "J resolves Outliner");

        var ruPairs = new[]
        {
            ("й", ShellHotkeyAction.Country), ("у", ShellHotkeyAction.StateAffairs), ("к", ShellHotkeyAction.Economy),
            ("е", ShellHotkeyAction.Trade), ("н", ShellHotkeyAction.Production), ("г", ShellHotkeyAction.Research),
            ("ш", ShellHotkeyAction.Military), ("щ", ShellHotkeyAction.Diplomacy), ("з", ShellHotkeyAction.Wars),
            ("д", ShellHotkeyAction.Ledger), ("я", ShellHotkeyAction.MapPolitical), ("ч", ShellHotkeyAction.MapDiplomatic),
            ("с", ShellHotkeyAction.MapEconomy), ("м", ShellHotkeyAction.MapResources), ("т", ShellHotkeyAction.MapWar),
            ("ь", ShellHotkeyAction.MapMore), ("о", ShellHotkeyAction.ToggleOutliner),
        };
        foreach (var (ch, expected) in ruPairs)
            Check(ShellHotkeys.TryResolve(ch, false, false, false, false, false, out var got) && got == expected,
                $"Russian key '{ch}' maps cleanly to {expected}");

        Check(!ShellHotkeys.TryResolve("N", false, false, textInputFocused: true, false, false, out _), "text input suppresses hotkeys");
        Check(!ShellHotkeys.TryResolve("Q", false, false, false, freeCameraActive: true, false, out _), "FreeCamera suppresses Q");
        Check(!ShellHotkeys.TryResolve("R", false, false, false, false, modalBlocking: true, out _), "modal suppresses screen hotkeys");
        Check(ShellHotkeys.TryResolve("Esc", false, false, false, false, modalBlocking: true, out var esc) && esc == ShellHotkeyAction.MenuOrClose,
            "Escape still resolves inside modals/popovers");
    }

    public static void RunMessages()
    {
        var events = ShellMessages.AllEvents;
        Check(events.Count == 12, "all 12 campaign message event classes declared");

        var nuclearCalm = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.NuclearStrike, false, 1);
        var worldCalm = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.WorldNews, false, 1);
        var peaceCalm = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.PeaceOffer, true, 1);
        var shortageCalm = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.CommodityShortage, true, 1);
        Check(nuclearCalm.PauseClock && nuclearCalm.ShowNewspaperCard, "Calm pauses on nuclear strike");
        Check(!worldCalm.PauseClock && worldCalm.ShowNewspaperCard && !worldCalm.AddAlertRow, "Calm shows non-pausing world news card");
        Check(peaceCalm.PauseClock && peaceCalm.ShowNewspaperCard && peaceCalm.AddAlertRow, "Calm pauses on player peace offer");
        Check(!shortageCalm.PauseClock && !shortageCalm.ShowNewspaperCard && shortageCalm.AddAlertRow, "Calm routes routine shortage to alert row only");

        var defconFocus = ShellMessages.Resolve(ShellMessagePreset.Focus, ShellMessageEvent.DefconChanged, true, 2);
        var researchFocus = ShellMessages.Resolve(ShellMessagePreset.Focus, ShellMessageEvent.ResearchFinished, true, 2);
        Check(defconFocus.PauseClock && researchFocus.PauseClock, "Focus preset auto-pauses on DEFCON shift and finished research");

        var peaceDispatch = ShellMessages.Resolve(ShellMessagePreset.Dispatch, ShellMessageEvent.PeaceOffer, true, 2);
        var encirclementSpeed5 = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.ArmyEncircled, true, 5);
        Check(!peaceDispatch.PauseClock && peaceDispatch.ShowNewspaperCard, "Dispatch keeps flow on peace offer card");
        Check(encirclementSpeed5.PauseClock && encirclementSpeed5.Speed5SafetyPause, "Speed 5 triggers safety pause on player encirclement");

        Check(ShellMessages.ParsePreset("focus") == ShellMessagePreset.Focus &&
              ShellMessages.ParsePreset("paradox") == ShellMessagePreset.Focus,
            "Focus preset parses both focus and legacy paradox");
        Check(ShellMessages.ParsePreset("dispatch") == ShellMessagePreset.Dispatch &&
              ShellMessages.ParsePreset("aoh") == ShellMessagePreset.Dispatch,
            "Dispatch preset parses both dispatch and legacy aoh");
    }

    public static void RunDatePlate()
    {
        var tempSettingsDir = Path.Combine(Path.GetTempPath(), "dof-dateplate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempSettingsDir);
        try
        {
            PlayerSettings.UseDirectory(tempSettingsDir);
            InterfaceSkin.ResetCatalog();
            SettingsCatalog.ResetCatalog();

            var campaign = Campaign.CreateBootstrap(20260928);
            var fpBefore = CampaignFingerprint.Of(campaign);

            var withHours = campaign.Date.Display(withTime: true);
            Check(withHours.Contains(":00", StringComparison.Ordinal), "GameDate.Display(withTime: true) formats HH:00 hours: " + withHours);

            var root = GameDataLoader.DataFolder();
            var shellDateSrc = File.ReadAllText(Path.Combine(root, "..", "Scripts", "ui", "Shell", "ShellDatePlateView.cs"));
            var classicDateSrc = File.ReadAllText(Path.Combine(root, "..", "Scripts", "ui", "DatePlateUI.cs"));
            Check(shellDateSrc.Contains("Display(withTime: true)", StringComparison.Ordinal) &&
                  classicDateSrc.Contains("Display(withTime: true)", StringComparison.Ordinal),
                "both ShellDatePlateView and fallback DatePlateUI render campaign hours (HH:00)");

            var defcon = DefconSystem.CurrentLevel(campaign);
            Check(defcon is >= 1 and <= 5, $"DEFCON level stays bounded 1..5 ({defcon})");

            var tension = WorldWar.Tension(campaign);
            Check(float.IsFinite(tension) && tension >= 0f && tension <= 100f, $"WorldWar tension is bounded 0..100 ({tension:0.0})");

            SettingsCatalog.Store("ui_shell", "new");
            SettingsCatalog.Store("ui_shell_message_preset", "focus");
            Check(string.Equals(SettingsCatalog.Chosen("ui_shell_message_preset"), "focus", StringComparison.Ordinal),
                "ui_shell_message_preset stores focus cleanly");
            PlayerSettings.Set("ui_shell_message_preset", "paradox");
            Check(string.Equals(SettingsCatalog.Chosen("ui_shell_message_preset"), "focus", StringComparison.Ordinal),
                "legacy ui_shell_message_preset=paradox migrates cleanly to focus");
            PlayerSettings.Set("ui_shell_message_preset", "aoh");
            Check(string.Equals(SettingsCatalog.Chosen("ui_shell_message_preset"), "dispatch", StringComparison.Ordinal),
                "legacy ui_shell_message_preset=aoh migrates cleanly to dispatch");
            SettingsCatalog.Store("ui_shell_minimap", true);
            SettingsCatalog.Store("ui_shell", "classic");
            SettingsCatalog.Store("ui_shell_message_preset", "calm");
            SettingsCatalog.Store("ui_shell_minimap", false);

            Check(CampaignFingerprint.Of(campaign) == fpBefore,
                "live ui_shell / date plate / world strip toggles leave CampaignFingerprint identical");
        }
        finally
        {
            try { Directory.Delete(tempSettingsDir, recursive: true); } catch { }
        }
    }
}
