using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
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
        InterfaceSkin.ResetCatalog();
        SettingsCatalog.ResetCatalog();
        FontCatalog.Load();
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();

        // Step 0 & Step 1: Settings, Palette, Fonts, Icons, Qatar 28:11 flag
        Check(SettingsCatalog.Of("ui_shell") is not null && SettingsCatalog.Chosen("ui_shell") == "classic",
            "ui_shell setting defaults cleanly to classic");
        Check(SettingsCatalog.Of("ui_shell_minimap") is not null && !SettingsCatalog.Flag("ui_shell_minimap"),
            "minimap ships OFF by default");
        Check(SettingsCatalog.Of(ShellMessages.PresetSetting) is not null &&
              ShellMessages.ParsePreset(SettingsCatalog.Chosen(ShellMessages.PresetSetting)) == ShellMessagePreset.Calm,
            "message preset defaults to Calm (Спокойно)");
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
            "90_shell.json declares paper, dark plate, and brass tokens");

        Check(File.Exists(Path.Combine(root, "fonts", "SourceSerif4-Regular.ttf")) &&
              File.Exists(Path.Combine(root, "fonts", "SourceSerif4-SemiBold.ttf")) &&
              File.Exists(Path.Combine(root, "fonts", "SourceSerif4-Bold.ttf")),
            "Source Serif 4 regular/semibold/bold TTF assets ship in game/fonts");
        Check(FontCatalog.FileOf("shell.serif_semibold").EndsWith("SourceSerif4-SemiBold.ttf", StringComparison.OrdinalIgnoreCase),
            "90_shell_fonts.json binds SourceSerif4-SemiBold.ttf");

        foreach (var icon in new[] { "icon_shell_pin", "icon_shell_eye", "icon_shell_more", "icon_shell_play",
            "icon_shell_pause", "icon_shell_menu", "icon_shell_tick", "icon_shell_front",
            "icon_shell_capital", "icon_shell_2d", "icon_shell_3d", "icon_shell_scroll" })
            Check(SpriteCatalog.Has(icon), "shell monochrome icon resolves: " + icon);

        // Verify Qatar 28:11 natural aspect ratio
        var qatPath = Path.Combine(root, "gfx", "flags", "flag_qat.png");
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

        // Layout rest census across 1920x1080 and 1280x720 across Peace/Crisis/War x Outliner x News x Undo
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
        }

        // Map counter merge cap <= 20 + CampaignFingerprint invariance on real campaign
        var campaign = Campaign.CreateBootstrap(20260928);
        var fpBefore = CampaignFingerprint.Of(campaign);
        SettingsCatalog.Store("ui_shell", "new");

        var armies = new List<MapCounterInput>();
        for (var i = 0; i < 65; i++)
        {
            var isPlayer = i < 20;
            var isEnemy = i is >= 20 and < 35;
            armies.Add(new MapCounterInput(
                i + 1,
                isPlayer ? 1 : (isEnemy ? 2 : 10 + (i % 12)),
                isPlayer ? "PLR" : (isEnemy ? "ENM" : "NEU"),
                i % 6,
                i % 3,
                40.0 + (i % 10) * 1.2,
                20.0 + (i / 10) * 1.5,
                2.5f + (i % 4),
                isPlayer,
                false,
                isEnemy,
                i == 0,
                false));
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

        // Regression check: classic chrome refresh files must stay suppressed while IsNewShellActive is true
        var uiRoot = Path.Combine(root, "..", "Scripts", "ui");
        foreach (var file in new[] { "ArmyBarUI.cs", "CampaignAlertsUI.cs", "SituationDeskUI.cs", "TeletypeUI.cs", "HeadlinePopupsUI.cs", "TopBarHudUI.cs" })
        {
            var src = File.ReadAllText(Path.Combine(uiRoot, file));
            Check(src.Contains("IsNewShellActive", StringComparison.Ordinal),
                $"classic chrome refresh guards against IsNewShellActive in {file}");
        }
        var stripSrc = File.ReadAllText(Path.Combine(uiRoot, "Shell", "ShellStripUI.cs"));
        Check(!stripSrc.Contains("LayoutPreset.BottomLeft", StringComparison.Ordinal),
            "ShellStripUI anchors strip row inside host rather than BottomLeft downward overflow");
        var cornerSrc = File.ReadAllText(Path.Combine(uiRoot, "Shell", "ShellCornerUI.cs"));
        Check(!cornerSrc.Contains("flagInner.AddChild(rankBadge)", StringComparison.Ordinal),
            "ShellCornerUI keeps rank badge outside flagInner so natural flags stay unobstructed");
    }

    public static void RunAlertDedupe()
    {
        InterfaceSkin.ResetCatalog();
        SettingsCatalog.ResetCatalog();

        var campaign = Campaign.CreateBootstrap(20260928);
        var peaceNation = campaign.NationByTag("BRA") ?? campaign.Nations[0];
        var fpBefore = CampaignFingerprint.Of(campaign);

        // Peace early game (day 0 < 60): "Next" hint is strictly LAST
        var snapPeace = ShellAlerts.BuildSnapshot(campaign, peaceNation, outlinerOpen: false, nextHintsEnabled: true);
        Check(snapPeace.Alerts.Count <= 4, $"peace folded alerts {snapPeace.Alerts.Count} <= 4");
        if (snapPeace.NextHintShown)
            Check(snapPeace.Alerts[^1].IsNextHint && snapPeace.Alerts.Take(snapPeace.Alerts.Count - 1).All(a => !a.IsNextHint),
                "early-game Next hint is strictly LAST in alert column");
        Check(snapPeace.OutlinerGroups.All(g => g.Rows.Count > 0), "peace outliner contains zero empty groups");

        // Crisis: urgent pending event + deadline sorting
        campaign.PendingEvents.Add(new PendingCampaignEvent
        {
            Id = 9901,
            NationId = peaceNation.Id,
            Title = "Border ultimatum",
            Description = "Respond within days.",
            CreatedDay = campaign.ElapsedDays,
            ExpiresDay = campaign.ElapsedDays + 3
        });

        // War scenario: RUS vs UKR from bootstrap
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

        // Verify urgent pending event deadline formats cleanly and sorts ahead of undated alerts
        var crisisSnap = ShellAlerts.BuildSnapshot(campaign, peaceNation, outlinerOpen: false, nextHintsEnabled: false);
        var evtRow = crisisSnap.Alerts.FirstOrDefault(a => a.Id == "event:9901");
        Check(evtRow is not null && !string.IsNullOrEmpty(evtRow.DeadlineLabel) && crisisSnap.Alerts[0].Id == evtRow.Id,
            $"urgent dated event sorts first with non-empty deadline '{evtRow?.DeadlineLabel}'");

        // Viewed mark + snooze persistence without mutating CampaignFingerprint
        viewed.Add("event:9901");
        var snapViewed = ShellAlerts.BuildSnapshot(campaign, peaceNation, outlinerOpen: false, nextHintsEnabled: false, false, viewed, snoozes);
        Check(snapViewed.Alerts.First(a => a.Id == "event:9901").IsViewed, "viewed alert shows pencil-check flag");

        // Fast-forward day > 60 via non-mutating check on new campaign copy? Actually campaign.Tick() mutates campaign; verify deadline cutoff on synthetic snapshot
        // Check CampaignFingerprint before any PendingEvent addition vs restored:
        campaign.PendingEvents.RemoveAll(e => e.Id == 9901);
        Check(CampaignFingerprint.Of(campaign) == fpBefore, "alert dedupe & outliner read leaves CampaignFingerprint invariant");

        // Verify elapsed days >= 60 turns Next hint off cleanly
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

        // Russian ЙЦУКЕН layout mapping
        var ruPairs = new[]
        {
            ("й", ShellHotkeyAction.Country),
            ("у", ShellHotkeyAction.StateAffairs),
            ("к", ShellHotkeyAction.Economy),
            ("е", ShellHotkeyAction.Trade),
            ("н", ShellHotkeyAction.Production),
            ("г", ShellHotkeyAction.Research),
            ("ш", ShellHotkeyAction.Military),
            ("щ", ShellHotkeyAction.Diplomacy),
            ("з", ShellHotkeyAction.Wars),
            ("д", ShellHotkeyAction.Ledger),
            ("я", ShellHotkeyAction.MapPolitical),
            ("ч", ShellHotkeyAction.MapDiplomatic),
            ("с", ShellHotkeyAction.MapEconomy),
            ("м", ShellHotkeyAction.MapResources),
            ("т", ShellHotkeyAction.MapWar),
            ("ь", ShellHotkeyAction.MapMore),
            ("о", ShellHotkeyAction.ToggleOutliner),
        };
        foreach (var (ch, expected) in ruPairs)
        {
            Check(ShellHotkeys.TryResolve(ch, false, false, false, false, false, out var got) && got == expected,
                $"Russian key '{ch}' maps cleanly to {expected}");
        }

        // Modal / TextInput / FreeCamera guards
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

        // Calm (default "Спокойно")
        var nuclearCalm = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.NuclearStrike, false, 1);
        var worldCalm = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.WorldNews, false, 1);
        var peaceCalm = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.PeaceOffer, true, 1);
        var shortageCalm = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.CommodityShortage, true, 1);
        Check(nuclearCalm.PauseClock && nuclearCalm.ShowNewspaperCard, "Calm pauses on nuclear strike");
        Check(!worldCalm.PauseClock && worldCalm.ShowNewspaperCard && !worldCalm.AddAlertRow, "Calm shows non-pausing world news card");
        Check(peaceCalm.PauseClock && peaceCalm.ShowNewspaperCard && peaceCalm.AddAlertRow, "Calm pauses on player peace offer");
        Check(!shortageCalm.PauseClock && !shortageCalm.ShowNewspaperCard && shortageCalm.AddAlertRow, "Calm routes routine shortage to alert row only");

        // Focus (auto-pause)
        var defconFocus = ShellMessages.Resolve(ShellMessagePreset.Focus, ShellMessageEvent.DefconChanged, true, 2);
        var researchFocus = ShellMessages.Resolve(ShellMessagePreset.Focus, ShellMessageEvent.ResearchFinished, true, 2);
        Check(defconFocus.PauseClock && researchFocus.PauseClock, "Focus preset auto-pauses on DEFCON shift and finished research");

        // Dispatch (map feed)
        var peaceDispatch = ShellMessages.Resolve(ShellMessagePreset.Dispatch, ShellMessageEvent.PeaceOffer, true, 2);
        var encirclementSpeed5 = ShellMessages.Resolve(ShellMessagePreset.Calm, ShellMessageEvent.ArmyEncircled, true, 5);
        Check(!peaceDispatch.PauseClock && peaceDispatch.ShowNewspaperCard, "Dispatch keeps flow on peace offer card");
        Check(encirclementSpeed5.PauseClock && encirclementSpeed5.Speed5SafetyPause, "Speed 5 triggers safety pause on player encirclement");

        // Legacy saved preset aliases
        Check(ShellMessages.ParsePreset("focus") == ShellMessagePreset.Focus &&
              ShellMessages.ParsePreset("paradox") == ShellMessagePreset.Focus,
            "Focus preset parses both focus and legacy paradox");
        Check(ShellMessages.ParsePreset("dispatch") == ShellMessagePreset.Dispatch &&
              ShellMessages.ParsePreset("aoh") == ShellMessagePreset.Dispatch,
            "Dispatch preset parses both dispatch and legacy aoh");
    }

    public static void RunDatePlate()
    {
        InterfaceSkin.ResetCatalog();
        SettingsCatalog.ResetCatalog();

        var campaign = Campaign.CreateBootstrap(20260928);
        var fpBefore = CampaignFingerprint.Of(campaign);

        var dateStr = campaign.Date.ToString();
        Check(!string.IsNullOrWhiteSpace(dateStr), "GameDate produces readable date string: " + dateStr);

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
}
