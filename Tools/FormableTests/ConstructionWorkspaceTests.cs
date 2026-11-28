using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

static class ConstructionWorkspaceTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL construction-workspace: " + label);
        Console.WriteLine("PASS construction-workspace: " + label);
    }

    [TestSuite("--construction-workspace-only", Order = 2630, IncludeInDefaultRun = true,
        Description = "Sovereign Brass Construction Workspace, map-click placement wiring, EN/RU parity, and touch-safe hints.")]
    public static void Run()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));
        string ReadRel(string rel) => File.ReadAllText(Path.Combine(repoRoot, rel));

        // 1. Standalone workspace files + .cs.uid sidecars + zero raw Color
        var viewPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellConstructionWorkspaceView.cs");
        var cardsPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellConstructionCards.cs");
        Check(File.Exists(viewPath) && File.Exists(viewPath + ".uid"), "ShellConstructionWorkspaceView.cs and .cs.uid exist");
        Check(File.Exists(cardsPath) && File.Exists(cardsPath + ".uid"), "ShellConstructionCards.cs and .cs.uid exist");

        var viewSrc = File.ReadAllText(viewPath);
        var cardsSrc = File.ReadAllText(cardsPath);
        Check(!viewSrc.Contains("new Color(", StringComparison.Ordinal) && !cardsSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellConstructionWorkspaceView and ShellConstructionCards have zero raw new Color(");
        Check(viewSrc.Contains("ZIndex = 25", StringComparison.Ordinal) &&
              viewSrc.Contains("plate_brass_9s", StringComparison.Ordinal) &&
              viewSrc.Contains("ChromePlateArt.AttachSurfaceTexture(_panel, \"linen\", 0.30f)", StringComparison.Ordinal) &&
              viewSrc.Contains("CloseShellConstructionWorkspace", StringComparison.Ordinal) &&
              viewSrc.Contains("ShellConstructionRegionSelect", StringComparison.Ordinal) &&
              viewSrc.Contains("ShellConstructionSearch", StringComparison.Ordinal) &&
              viewSrc.Contains("ShellConstructionCatalogScroll", StringComparison.Ordinal) &&
              viewSrc.Contains("ShellConstructionQueueScroll", StringComparison.Ordinal) &&
              viewSrc.Contains("_ctx.BeginConstructionPlacement?.Invoke(kind)", StringComparison.Ordinal),
            "ShellConstructionWorkspaceView implements Sovereign Brass layout, controls, scrolls, and map placement CTA");
        Check(cardsSrc.Contains("ShellBuildCard_", StringComparison.Ordinal) &&
              cardsSrc.Contains("ShellPlaceOnMap_", StringComparison.Ordinal) &&
              cardsSrc.Contains("ShellBuildOne_", StringComparison.Ordinal) &&
              cardsSrc.Contains("ShellBuildThree_", StringComparison.Ordinal) &&
              cardsSrc.Contains("ShellDemolishOne_", StringComparison.Ordinal) &&
              cardsSrc.Contains("ShellCancelConstruction_", StringComparison.Ordinal) &&
              cardsSrc.Contains("ConstructionAdmission.Refusal", StringComparison.Ordinal) &&
              cardsSrc.Contains("ConstructionQueue.Describe", StringComparison.Ordinal) &&
              cardsSrc.Contains("ConstructionQueue.When", StringComparison.Ordinal),
            "ShellConstructionCards implements building cards, queue cards, refusal checks, and action buttons");

        // 2. ShellEconomyWorkspaceView regression fix: shell.tab_build -> OpenConstruction, NOT State Affairs
        var ecoSrc = ReadRel("Scripts/ui/Shell/ShellEconomyWorkspaceView.cs");
        Check(ecoSrc.Contains("\"shell.tab_build\", () => { Close(); _ctx.OpenConstruction?.Invoke(); }", StringComparison.Ordinal),
            "ShellEconomyWorkspaceView routes shell.tab_build to _ctx.OpenConstruction");
        Check(!ecoSrc.Contains("\"shell.tab_build\", () => { Close(); _ctx.OpenScreen?.Invoke(\"State Affairs\"); }", StringComparison.Ordinal),
            "ShellEconomyWorkspaceView no longer dumps shell.tab_build into State Affairs");

        // 3. Orchestrator, Context, NewShellUI, CampaignShellUI & ConstructionPlacementUI wiring
        var ctxSrc = ReadRel("Scripts/ui/Shell/ShellContext.cs");
        var orchSrc = ReadRel("Scripts/ui/Shell/ShellOrchestrator.cs");
        var newShellSrc = ReadRel("Scripts/ui/Shell/NewShellUI.cs") + "\n" + ReadRel("Scripts/ui/NewShellConstructionUI.cs");
        var campShellSrc = ReadRel("Scripts/ui/CampaignShellUI.cs");
        var placeSrc = ReadRel("Scripts/ui/ConstructionPlacementUI.cs") + "\n" + ReadRel("Scripts/ui/NewShellConstructionUI.cs");
        Check(ctxSrc.Contains("OpenConstruction", StringComparison.Ordinal) &&
              ctxSrc.Contains("BeginConstructionPlacement", StringComparison.Ordinal) &&
              ctxSrc.Contains("QueueConstructionInState", StringComparison.Ordinal) &&
              ctxSrc.Contains("DemolishBuildingInState", StringComparison.Ordinal) &&
              ctxSrc.Contains("CancelConstruction", StringComparison.Ordinal) &&
              ctxSrc.Contains("PreviewBuildingEffects", StringComparison.Ordinal),
            "ShellContext exposes construction workspace delegates");
        Check(orchSrc.Contains("ConstructionWorkspace", StringComparison.Ordinal) &&
              orchSrc.Contains("PendingView.Construction", StringComparison.Ordinal),
            "ShellOrchestrator owns, lays out, and refreshes ConstructionWorkspace");
        Check(newShellSrc.Contains("IsNewShellConstructionOpen", StringComparison.Ordinal) &&
              newShellSrc.Contains("OpenNewShellConstructionWorkspace", StringComparison.Ordinal) &&
              newShellSrc.Contains("CloseNewShellConstructionWorkspace", StringComparison.Ordinal) &&
              newShellSrc.Contains("QueueConstructionInState", StringComparison.Ordinal) &&
              newShellSrc.Contains("DemolishBuildingInState", StringComparison.Ordinal) &&
              newShellSrc.Contains("RaiseOverlayAboveNewShell", StringComparison.Ordinal) &&
              newShellSrc.Contains("CommitImmediateCampaignWrite", StringComparison.Ordinal),
            "NewShellUI wires construction workspace lifecycle, immediate commands, and overlay raising");
        Check(campShellSrc.Contains("TryOpenNewShellEconomyTab(tab)", StringComparison.Ordinal),
            "CampaignShellUI routes OpenEconomyTab(\"Construction\") to OpenNewShellConstructionWorkspace in NewShell");
        Check(placeSrc.Contains("RaiseOverlayAboveNewShell(panel)", StringComparison.Ordinal) &&
              placeSrc.Contains("OpenConstructionWorkspaceOrLegacy()", StringComparison.Ordinal) &&
              placeSrc.Contains("shell.ws.build.place_feedback_touch", StringComparison.Ordinal) &&
              placeSrc.Contains("shell.ws.build.place_preview_touch", StringComparison.Ordinal) &&
              placeSrc.Contains("shell.ws.build.place_queued_touch_fmt", StringComparison.Ordinal) &&
              placeSrc.Contains("shell.ws.build.place_tap_to_build", StringComparison.Ordinal),
            "ConstructionPlacementUI raises above NewShell, returns to ConstructionWorkspace, and uses touch-safe copy");

        // 3b. ShellRegionBuildTabView shows ALL buildings from BuildingCatalog.All (no hardcoded 6-building QuickBuildKinds)
        var regBuildSrc = ReadRel("Scripts/ui/Shell/ShellRegionBuildTabView.cs");
        Check(regBuildSrc.Contains("BuildingCatalog.All", StringComparison.Ordinal) &&
              !regBuildSrc.Contains("QuickBuildKinds", StringComparison.Ordinal) &&
              regBuildSrc.Contains("ConstructionAdmission.Refusal", StringComparison.Ordinal) &&
              regBuildSrc.Contains("ctx.OpenConstruction?.Invoke()", StringComparison.Ordinal) &&
              regBuildSrc.Contains("ImmediateCampaignCommands.Commit", StringComparison.Ordinal) &&
              !regBuildSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellRegionBuildTabView iterates BuildingCatalog.All (no QuickBuildKinds), uses ConstructionAdmission.Refusal, opens full workspace, and has zero raw Color");

        // 4. SupportScreenUI Russian touch hint fix
        var supportSrc = ReadRel("Scripts/ui/SupportScreenUI.cs");
        const string supportTouchKey = "On the map: tap a formation to select · long-press or use Order mode to move";
        Check(supportSrc.Contains($"Tr(\"{supportTouchKey}\")", StringComparison.Ordinal),
            "SupportScreenUI wraps touch map hint in Tr(...)");
        using var mobEnDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "localization", "english", "zz_449_mobile_orders_ux.json")));
        using var mobRuDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "localization", "russian", "zz_449_mobile_orders_ux.json")));
        Check(mobEnDoc.RootElement.TryGetProperty(supportTouchKey, out var enSup) && !string.IsNullOrWhiteSpace(enSup.GetString()) &&
              mobRuDoc.RootElement.TryGetProperty(supportTouchKey, out var ruSup) && !string.IsNullOrWhiteSpace(ruSup.GetString()) &&
              !string.Equals(enSup.GetString(), ruSup.GetString(), StringComparison.Ordinal),
            "zz_449_mobile_orders_ux.json provides distinct EN and RU translations for SupportScreenUI touch hint");

        // 5. zz_452_construction_workspace.json EN/RU parity, UTF-8 no BOM, zero collisions, zero PC words on touch keys
        var enPath = Path.Combine(dataRoot, "localization", "english", "zz_452_construction_workspace.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "zz_452_construction_workspace.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU zz_452_construction_workspace.json exist");
        Check(!HasUtf8Bom(enPath) && !HasUtf8Bom(ruPath), "zz_452_construction_workspace.json files are UTF-8 without BOM");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 35 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) zz_452_construction_workspace.json have identical key counts");

        var phRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        var pcWordRx = new Regex(@"\b(hover|right-click|escape|pointer|click|лкм|пкм|наведите|курсор|клик)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(k.StartsWith("shell.ws.build.", StringComparison.Ordinal), $"key '{k}' uses shell.ws.build.* namespace");
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN key '{k}' non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU key '{k}' non-empty");
            if (Regex.IsMatch(enVal, "[A-Za-z]"))
                Check(!string.Equals(enVal, ruVal, StringComparison.Ordinal), $"RU key '{k}' translated distinctly");
            var enPh = phRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
            if (k.Contains("touch", StringComparison.OrdinalIgnoreCase))
            {
                Check(!pcWordRx.IsMatch(enVal), $"EN touch key '{k}' has zero PC control words");
                Check(!pcWordRx.IsMatch(ruVal), $"RU touch key '{k}' has zero PC control words");
            }
        }

        foreach (var lang in new[] { "english", "russian" })
        {
            var seenElsewhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Path.Combine(dataRoot, "localization", lang), "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), "zz_452_construction_workspace.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    seenElsewhere.Add(prop.Name);
            }
            var collisions = enMap.Keys.Where(seenElsewhere.Contains).ToArray();
            Check(collisions.Length == 0, $"{lang} zz_452_construction_workspace.json has zero collisions ({string.Join(", ", collisions)})");
        }

        // 6. Paused command sanity: queue, cancel, demolish mutate campaign & fingerprint cleanly
        var campaign = new Campaign(20261127) { IsPaused = true };
        var player = new Nation { Id = 0, Tag = "BLD", MapIndex = 11, IsHuman = true, Treasury = 100f, Gdp = 100f, Population = 1_000_000 };
        var state = new ScenarioState { Id = "bld_state", MapIndex = 101, NationId = player.Tag, NationMapIndex = player.MapIndex, Population = 1_000_000 };
        state.Buildings[BuildingKind.CivilianIndustry] = 1;
        campaign.Nations.Add(player);
        campaign.States.Add(state);
        campaign.SetPlayer(player.Id);
        var fp0 = CampaignFingerprint.Of(campaign);
        var beforeQueue = campaign.Construction.Count(p => p.NationId == player.Id);

        var refusal = ConstructionAdmission.Refusal(campaign, player, state, BuildingKind.CivilianIndustry);
        _ = ConstructionQueue.Schedule(campaign, player);
        _ = ConstructionCost.Of(campaign, player, BuildingKind.CivilianIndustry);
        Check(refusal.Length == 0 && CampaignFingerprint.Of(campaign) == fp0,
            $"fresh paused state admits CivilianIndustry without mutating fingerprint ({refusal})");

        campaign.Commands.Enqueue(new QueueConstructionCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex, Building = BuildingKind.CivilianIndustry });
        campaign.Commands.ApplyAll(campaign);
        var afterQueue = campaign.Construction.Count(p => p.NationId == player.Id);
        Check(afterQueue == beforeQueue + 1, $"paused QueueConstructionCommand queues immediately (before={beforeQueue}, after={afterQueue})");

        var projectId = campaign.Construction.Last(p => p.NationId == player.Id).Id;
        campaign.Commands.Enqueue(new CancelConstructionCommand { IssuerId = player.Id, ProjectId = projectId });
        campaign.Commands.ApplyAll(campaign);
        Check(campaign.Construction.Count(p => p.NationId == player.Id) == beforeQueue,
            "paused CancelConstructionCommand removes queued project immediately");

        var treasuryBeforeDemolish = player.Treasury;
        campaign.Commands.Enqueue(new DemolishBuildingCommand { IssuerId = player.Id, StateMapIndex = state.MapIndex, Building = BuildingKind.CivilianIndustry });
        campaign.Commands.ApplyAll(campaign);
        Check(state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry) == 0 && player.Treasury > treasuryBeforeDemolish,
            "paused DemolishBuildingCommand demolishes standing building and refunds scrap immediately");
    }

    private static bool HasUtf8Bom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }
}
