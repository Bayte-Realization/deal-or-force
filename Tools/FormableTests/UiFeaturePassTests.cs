using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DealOrForce;
using DealOrForce.Simulation;

static class UiFeaturePassTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL ui-features: " + label);
        Console.WriteLine("PASS ui-features: " + label);
    }

    [TestSuite("--ui-features-only", Order = 2635, IncludeInDefaultRun = true,
        Description = "Controlled territory localization EN/RU, CountryRankings capital focus, Country Panel highlight & shader parity, and MovementUI TryEnterRead fence.")]
    public static void Run()
    {
        var root = FindRepoRoot();
        RunControlledLocalizationTests(root);
        RunRankingCapitalFocusTests(root);
        RunCountryHighlightAndShaderTests(root);
        RunMovementReadFenceTests(root);
        RunPhase2UiStabilityAndConsoleTests(root);
        RunPhase3EventIncomeAndUiTests(root);
    }

    private static void RunControlledLocalizationTests(string root)
    {
        var prevLang = Loc.Language;
        try
        {
            Loc.ResetCatalog();
            var locDir = Path.Combine(root, "game", "localization");
            var errors = new List<string>();
            Loc.LoadDirectory(locDir, errors);
            Check(errors.Count == 0, $"game localization loads with zero errors (got {errors.Count})");

            Loc.Language = "english";
            var enBadge = Loc.Format("shell.ws.reg.occupied_by_fmt", "France");
            var enLease = Loc.Format("shell.ws.reg.leased_by_fmt", "France");
            var enMapTip = Loc.Get("shell.ws.shell.map_tip_political");
            var enForeign = Loc.Get("Foreign control");
            var enLeased = Loc.Get("Leased control");
            var enRegionTip = Loc.Format(
                "{0} of the region is controlled by another state. What is made there does not reach {1}'s market.",
                "45%", "Poland");
            var enHover = Loc.Format(
                "Controlled by {0} · {1:P0} secured\nStill owned by {2}: control is not an annexation. Only a signed peace moves the border.",
                "Germany", 0.5f, "France");
            var enPolHelp = Loc.Get("Countries, who controls what, and every controlled province.");

            Check(enBadge == "Controlled: France", "EN badge uses Controlled: {0}");
            Check(enLease == "Leased: France", "EN lease badge uses Leased: {0}");
            Check(enMapTip.Contains("controlled", StringComparison.OrdinalIgnoreCase) &&
                  !enMapTip.Contains("occupied", StringComparison.OrdinalIgnoreCase),
                "EN map tip political generalizes occupied -> controlled");
            Check(enForeign == "Foreign control" && enLeased == "Leased control",
                "EN build list control labels present");
            Check(!enRegionTip.Contains("{0}") && !enRegionTip.Contains("{1}") &&
                  enRegionTip.Contains("controlled by another state"),
                "EN region build control detail formats cleanly");
            Check(!enHover.Contains("{0}") && !enHover.Contains("{1") && !enHover.Contains("{2}") &&
                  enHover.Contains("Controlled by Germany") && enHover.Contains("control is not an annexation"),
                "EN hover control tooltip formats cleanly without occupied wording");
            Check(enPolHelp.Contains("controlled province"),
                "EN political map help uses controlled province");

            Loc.Language = "russian";
            var ruBadge = Loc.Format("shell.ws.reg.occupied_by_fmt", "Франция");
            var ruLease = Loc.Format("shell.ws.reg.leased_by_fmt", "Франция");
            var ruMapTip = Loc.Get("shell.ws.shell.map_tip_political");
            var ruForeign = Loc.Get("Foreign control");
            var ruLeased = Loc.Get("Leased control");
            var ruRegionTip = Loc.Format(
                "{0} of the region is controlled by another state. What is made there does not reach {1}'s market.",
                "45%", "Польша");
            var ruHover = Loc.Format(
                "Controlled by {0} · {1:P0} secured\nStill owned by {2}: control is not an annexation. Only a signed peace moves the border.",
                "Германия", 0.5f, "Франция");
            var ruPolHelp = Loc.Get("Countries, who controls what, and every controlled province.");

            Check(ruBadge == "Под контролем: Франция", "RU badge uses Под контролем: {0}");
            Check(ruLease == "Аренда: Франция", "RU lease badge uses Аренда: {0}");
            Check(ruMapTip.Contains("контролируем", StringComparison.OrdinalIgnoreCase) &&
                  !ruMapTip.Contains("оккупир", StringComparison.OrdinalIgnoreCase),
                "RU map tip political generalizes оккупированные -> контролируемые");
            Check(ruForeign != enForeign && !string.IsNullOrWhiteSpace(ruForeign) &&
                  ruLeased != enLeased && !string.IsNullOrWhiteSpace(ruLeased),
                "RU build list control labels translated cleanly");
            Check(!ruRegionTip.Contains("{0}") && !ruRegionTip.Contains("{1}") && ruRegionTip.Contains("45%") &&
                  ruRegionTip.Contains("Польша") && ruRegionTip != enRegionTip,
                "RU region build control detail formats placeholders and differs from English");
            Check(!ruHover.Contains("{0}") && !ruHover.Contains("{1") && !ruHover.Contains("{2}") &&
                  ruHover.Contains("Германия") && ruHover.Contains("Франция") &&
                  !ruHover.Contains("Оккупир", StringComparison.OrdinalIgnoreCase),
                "RU hover control tooltip formats placeholders and avoids occupation phrasing");
            Check(ruPolHelp.Contains("контролируемая провинция") && ruPolHelp != enPolHelp,
                "RU political map help translated cleanly");

            // UI source contracts for generalized phrasing and Lease vs war distinction
            var kpiCs = Read(root, "Scripts/ui/Shell/ShellRegionKpiCardsView.cs");
            var buildCs = Read(root, "Scripts/ui/RegionBuildListUI.cs");
            var hoverCs = Read(root, "Scripts/gfx/GlobePresentation.Input.cs");
            var mapCtrlCs = Read(root, "Scripts/ui/MapControlsUI.cs");

            Check(kpiCs.Contains("LandArrangementSystem.Covering(world, state.MapIndex, controller.Id)") &&
                  kpiCs.Contains("shell.ws.reg.leased_by_fmt") &&
                  kpiCs.Contains("LandArrangementSystem.Describe(world, state)"),
                "ShellRegionKpiCardsView distinguishes leased arrangements cleanly");
            Check(buildCs.Contains("LandArrangementSystem.Covering(_campaign, state.MapIndex)") &&
                  buildCs.Contains("\"Leased control\"") && buildCs.Contains("\"Foreign control\"") &&
                  !buildCs.Contains("\"Under occupation\""),
                "RegionBuildListUI uses localized Foreign control / Leased control wording");
            Check(!hoverCs.Contains("Occupied by {0}") &&
                  hoverCs.Contains("Controlled by {0} · {1:P0} secured"),
                "GlobePresentation.Input hover tooltip uses generalized Controlled phrasing");
            Check(!mapCtrlCs.Contains("every occupied province") &&
                  mapCtrlCs.Contains("every controlled province"),
                "MapControlsUI political mode help uses controlled province");
        }
        finally
        {
            Loc.ResetCatalog();
            Loc.Language = prevLang;
        }
    }

    private static void RunRankingCapitalFocusTests(string root)
    {
        var rankingsCs = Read(root, "Scripts/ui/CountryRankingsUI.cs");
        var gameCs = Read(root, "Scripts/DealOrForceGame.cs");

        Check(gameCs.Contains("private bool FocusNationCapital(Nation nation") &&
              gameCs.Contains("CapitalCore.HasSeat(campaign, nation)") &&
              gameCs.Contains("CapitalCore.EffectiveLatitude(campaign, nation)") &&
              gameCs.Contains("CapitalCore.EffectiveLongitude(campaign, nation)") &&
              gameCs.Contains("_globe.FocusOn(lat, lon, dist);"),
            "DealOrForceGame exposes FocusNationCapital helper reusing CapitalCore + GlobePresentation.FocusOn");

        var rowBlockMatch = Regex.Match(rankingsCs,
            @"button\.Name = ""CountryRankingRow_"" \+ slot;[\s\S]{0,200}",
            RegexOptions.Multiline);
        var rowHandlerMatch = Regex.Match(rankingsCs,
            @"var button = ActionButton\("""", \(\) =>\s*\{[\s\S]{0,360}\},\s*0,\s*isTouch \? 46 : 40\);",
            RegexOptions.Multiline);
        Check(rowHandlerMatch.Success &&
              rowHandlerMatch.Value.Contains("FocusAndHighlightRankingNation(row.NationId)") &&
              !rowHandlerMatch.Value.Contains("CloseCountryRankings("),
            "CountryRankingRow click focuses capital + highlights nation without closing modal");
        Check(rowBlockMatch.Success && rowBlockMatch.Value.Contains("MouseFilter = MouseFilterEnum.Pass"),
            "CountryRankingRow uses MouseFilterEnum.Pass and touch-friendly height for touchscreens");
        Check(rankingsCs.Contains("FocusAndHighlightRankingNation(_countryRankingSelected)") &&
              Regex.IsMatch(rankingsCs, @"CloseCountryRankings\(\);\s+FocusNationCapital\(nation\);"),
            "My country and Ranking country dossier actions also focus capital cleanly");
        Check(rankingsCs.Contains("RestoreMapHighlightAfterRankings()"),
            "CloseCountryRankings restores or clears country highlight cleanly");

        // Verify CapitalCore guard + fallback behavior on a pure Campaign object (no Godot types)
        var campaign = new Campaign(77, new Defines());
        var landed = new Nation { Id = 1, MapIndex = 1, Tag = "TST", Name = "Testia", CapitalLatitude = 35.68f, CapitalLongitude = 139.69f };
        var landless = new Nation { Id = 2, MapIndex = 2, Tag = "MVM", Name = "Movement", CapitalLatitude = 0f, CapitalLongitude = 0f };
        campaign.Nations.Add(landed);
        campaign.Nations.Add(landless);
        Check(!CapitalCore.HasSeat(campaign, landed) &&
              float.IsFinite(landed.CapitalLatitude) && MathF.Abs(landed.CapitalLatitude) > 0.001f &&
              MathF.Abs(landless.CapitalLatitude) <= 0.001f,
            "Capital coordinate fallback handles seated, unseated finite, and landless nations without Null Island jumps");
    }

    private static void RunCountryHighlightAndShaderTests(string root)
    {
        var contextCs = Read(root, "Scripts/ui/Shell/ShellContext.cs");
        var newShellCs = Read(root, "Scripts/ui/Shell/NewShellUI.cs");
        var workPanelCs = Read(root, "Scripts/ui/Shell/ShellWorkPanelView.cs");
        var mapSelCs = Read(root, "Scripts/ui/MapSelectionUI.cs");

        var workPanelLines = File.ReadAllLines(Path.Combine(root, "Scripts/ui/Shell/ShellWorkPanelView.cs")).Length;
        Check(workPanelLines < 800, $"ShellWorkPanelView.cs stays under 800 lines ({workPanelLines})");
        Check(contextCs.Contains("public Action<int> HighlightNation { get; set; }"),
            "ShellContext exposes HighlightNation delegate");
        Check(newShellCs.Contains("ctx.HighlightNation = mapIdx => _globe?.HighlightNation(mapIdx);"),
            "NewShellUI wires ctx.HighlightNation to GlobePresentation.HighlightNation");
        Check(workPanelCs.Contains("_ctx.HighlightNation?.Invoke(nation.MapIndex);") &&
              !Regex.IsMatch(workPanelCs, @"OpenCountry[\s\S]{0,300}!=\s*player"),
            "ShellWorkPanelView.OpenCountry highlights both player and foreign countries");
        Check(Regex.IsMatch(workPanelCs, @"OpenRegion\([^)]*\)\s*\{[\s\S]{0,220}_ctx\.HighlightNation\?\.Invoke\(-1\);"),
            "ShellWorkPanelView.OpenRegion clears country highlight when switching to region mode");
        Check(Regex.IsMatch(workPanelCs, @"Close\(\)\s*\{[\s\S]{0,200}DossierMode\.Country[\s\S]{0,60}_ctx\.HighlightNation\?\.Invoke\(-1\);"),
            "ShellWorkPanelView.Close clears country highlight on close");
        Check(!mapSelCs.Contains("var foreign = pick.Nation != _campaign.Player;") &&
              mapSelCs.Contains("_globe?.HighlightNation(pick.Nation.MapIndex);") &&
              mapSelCs.Contains("_mapSelectionHighlights = true;"),
            "MapSelectionUI highlights both player and foreign countries and clears cleanly");

        // Shader visual parity & GLES3 12-sampler invariants
        var shaderPath = Path.Combine(root, "game", "shaders", "globe_map.gdshader");
        var fullShader = File.ReadAllText(shaderPath);
        Check(fullShader.Contains("country_highlighted") &&
              fullShader.Contains("max(region_selected, country_highlighted) * 0.55") &&
              fullShader.Contains("clamp(base * 1.10 + vec3(0.045, 0.042, 0.036)") &&
              fullShader.Contains("selected_region_pixels * selection_width") &&
              fullShader.Contains("highlight_edge)") &&
              fullShader.Contains("ALBEDO = mix(base, selection_ink, selection_edge);") &&
              !fullShader.Contains("highlighted * 0.105"),
            "globe_map.gdshader country highlight matches Selected Region parchment lift and zoom-invariant selection_ink outline");

        var colorCorrected = MapColorSpaceShader.Build(fullShader);
        var packedShader = PackedMapShader.Build(colorCorrected);
        Check(packedShader.Contains("country_highlighted") &&
              packedShader.Contains("max(region_selected, country_highlighted) * 0.55") &&
              Regex.Matches(packedShader, @"uniform\s+sampler\w+\s+").Count == PackedMapShader.TextureUnits,
            "PackedMapShader preserves upgraded country highlight and 12-sampler GLES3 budget");

        var compat = CompatibilityMapShader.Source;
        Check(compat.Contains("uniform vec3 selection_ink") &&
              compat.Contains("clamp(land * 1.10 + vec3(0.045, 0.042, 0.036)") &&
              compat.Contains("highlighted * 0.55") &&
              compat.Contains("mix(land, selection_ink, highlight_edge)") &&
              !compat.Contains("vec3(0.96, 0.82, 0.44)") &&
              Regex.Matches(compat, @"uniform\s+sampler\w+\s+").Count == CompatibilityMapShader.TextureUnits,
            "CompatibilityMapShader matches parchment lift + selection_ink outline within 12 samplers");
    }

    private static void RunMovementReadFenceTests(string root)
    {
        var movementCs = Read(root, "Scripts/ui/MovementUI.cs");
        Check(Regex.IsMatch(movementCs,
                @"RefreshMovementPanel\(\)\s*\{[\s\S]{0,260}campaign\.TryEnterRead\(0, true\)[\s\S]{0,220}RefreshMovementPanelCore\(nation\);[\s\S]{0,120}finally[\s\S]{0,80}campaign\.ExitRead\(\);"),
            "MovementUI.RefreshMovementPanel guards all campaign reads via TryEnterRead/finally ExitRead");

        // Verify read-fence behavior under write contention
        var campaign = new Campaign(91, new Defines());
        Check(campaign.TryEnterRead(0, true), "Campaign allows TryEnterRead when unlocked");
        campaign.ExitRead();

        using var writerEntered = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        var writer = Task.Run(() =>
        {
            campaign.AccessGate.EnterWrite();
            try
            {
                writerEntered.Set();
                releaseWriter.Wait(TimeSpan.FromSeconds(5));
            }
            finally
            {
                campaign.AccessGate.ExitWrite();
            }
        });

        try
        {
            Check(writerEntered.Wait(TimeSpan.FromSeconds(5)), "Background writer entered Campaign write gate");
            Check(!campaign.TryEnterRead(0, true), "TryEnterRead misses immediately without blocking while writer holds gate");
        }
        finally
        {
            releaseWriter.Set();
            writer.Wait(TimeSpan.FromSeconds(5));
        }

        Check(campaign.TryEnterRead(0, true), "Campaign allows TryEnterRead again after writer releases gate");
        campaign.ExitRead();
    }

    private static void RunPhase2UiStabilityAndConsoleTests(string root)
    {
        var workPanelCs = Read(root, "Scripts/ui/Shell/ShellWorkPanelView.cs");
        Check(Regex.IsMatch(workPanelCs,
                @"public void Refresh\(bool priority = false\)\s*\{\s*if \(!IsVisible \|\| _ctx\.World is not \{ \} world\) return;[\s\S]{0,450}var entered = priority\s*\?\s*world\.AccessGate\.TryEnterPriorityRead\(60\)\s*:\s*world\.TryEnterRead\(0, requestWindowOnMiss: false\);\s*if \(!entered\) return;\s*try\s*\{\s*RefreshLocked\(world\);\s*\}\s*finally\s*\{\s*world\.ExitRead\(\);\s*\}"),
            "ShellWorkPanelView.Refresh acquires priority or non-blocking read fence before RefreshLocked");
        Check(workPanelCs.Contains("private (Campaign World, int Day, int Rev, int WarRev) _rankCacheStamp;") &&
              workPanelCs.Contains("_rankCacheStamp.WarRev != world.WarRevision") &&
              Regex.IsMatch(workPanelCs, @"fbSnap\.RankWeek\s*==\s*week\s*&&\s*fbSnap\.NationRanksById"),
            "ShellWorkPanelView.ResolveOverallRank includes WarRevision and checks fbSnap.RankWeek == week");

        var orchCs = Read(root, "Scripts/ui/Shell/ShellOrchestrator.cs");
        Check(orchCs.Contains("world.StrategicRevision") &&
              orchCs.Contains("_refreshStrategicRevision != stratRev") &&
              orchCs.Contains("private readonly Action _skinChangedHandler;") &&
              orchCs.Contains("InterfaceSkin.Changed -= _skinChangedHandler;"),
            "ShellOrchestrator tracks StrategicRevision and stores/unsubscribes InterfaceSkin.Changed handler");

        var minimapCs = Read(root, "Scripts/ui/Shell/ShellMinimapView.cs");
        Check(minimapCs.Contains("if (!world.TryEnterRead(0, false)) return;") &&
              minimapCs.Contains("world.ExitRead();") &&
              !minimapCs.Contains("using var img = Image.CreateFromData") &&
              minimapCs.Contains("var img = Image.CreateFromData"),
            "ShellMinimapView fences fallback world reads with TryEnterRead(0, false) and drops manual Image disposal");

        var rankingsCs = Read(root, "Scripts/ui/CountryRankingsUI.cs");
        Check(Regex.Matches(rankingsCs, @"campaign\.TryEnterRead\(50, true\)").Count >= 4 &&
              Regex.Matches(rankingsCs, @"campaign\.ExitRead\(\);").Count >= 4,
            "CountryRankingsUI fences FocusAndHighlightRankingNation, footer callbacks, and highlight restore");

        var gameCs = Read(root, "Scripts/DealOrForceGame.cs");
        Check(Regex.IsMatch(gameCs, @"Simulation\.SpriteCatalog\.Changed\s*-=\s*ForgetFlags;\s+Simulation\.SpriteCatalog\.Changed\s*\+=\s*ForgetFlags;") &&
              Regex.IsMatch(gameCs, @"Simulation\.FlagDesignCatalog\.Changed\s*-=\s*ForgetFlags;\s+Simulation\.FlagDesignCatalog\.Changed\s*\+=\s*ForgetFlags;") &&
              gameCs.Contains("TreeExiting += OnGameTreeExiting;") &&
              Regex.IsMatch(gameCs, @"private void OnGameTreeExiting\(\)[\s\S]{0,220}ForgetFlags[\s\S]{0,120}_shell\?\.Dispose\(\);"),
            "DealOrForceGame deduplicates and unsubscribes catalog handlers and disposes ShellOrchestrator on TreeExiting");

        var compat = CompatibilityMapShader.Source;
        Check(compat.Contains("fwidth(UV)") &&
              !compat.Contains("fwidth(at)") &&
              compat.Contains("fract(at.x + highlight_step.x)"),
            "CompatibilityMapShader uses seam-safe fwidth(UV) instead of fract(UV.x) derivative spikes");

        var devConsoleCs = Read(root, "Scripts/ui/DeveloperConsoleUI.cs");
        var worldCmdCs = Read(root, "Scripts/ui/Console/ConsoleWorldCommands.cs");
        var debugCmdCs = Read(root, "Scripts/ui/Console/ConsoleDebugCommands.cs");
        Check(devConsoleCs.Contains("_simRunner.RunWrite(action, refreshSnapshot: true)") &&
              devConsoleCs.Contains("ConsoleWorldCommands.BindWriteRunner(_campaign, RunConsoleWrite)") &&
              worldCmdCs.Contains("RunCampaignWrite(campaign, world =>") &&
              Regex.Matches(worldCmdCs, @"RunCampaignWrite\(campaign, world =>").Count >= 2,
            "Console advance and skip run under _simRunner.RunWrite / campaign write gate");
        Check(!debugCmdCs.Contains("cost and blocks ignored") &&
              debugCmdCs.Contains("checks prerequisites and costs"),
            "Console decision command help matches BlockReason enforcement");
    }

    private static void RunPhase3EventIncomeAndUiTests(string root)
    {
        var prevLang = Loc.Language;
        try
        {
            Loc.ResetCatalog();
            ModifierCatalog.ResetCatalog();
            EventCatalog.ResetCatalog();

            // 1. 0 landed-country events use treasury_percent or flat treasury.
            var legacyTreasuryHits = new List<string>();
            var costTotal = 0;
            var ongoingOrGdp = 0;
            foreach (var ev in EventCatalog.All)
            {
                if (string.Equals(ev.For, "movements", StringComparison.OrdinalIgnoreCase)) continue;
                CheckNoLegacyTreasury(ev.Id + ".effects", ev.Effects, legacyTreasuryHits);
                for (var i = 0; i < ev.Options.Length; i++)
                {
                    var eff = ev.Options[i].Effect;
                    CheckNoLegacyTreasury($"{ev.Id}.opt{i}", eff, legacyTreasuryHits);
                    if (eff is null) continue;
                    var hasOngoingOrGdp = eff.IncomePenalties.Count > 0 ||
                        eff.Entries.Any(e => e.Key.Equals("gdp_percent", StringComparison.OrdinalIgnoreCase) && e.Value < 0f);
                    var hasLumpSpot = eff.Entries.Any(e =>
                        (e.Key.Equals("income_share", StringComparison.OrdinalIgnoreCase) ||
                         e.Key.Equals("gdp_share", StringComparison.OrdinalIgnoreCase)) && e.Value < 0f);
                    if (hasOngoingOrGdp || hasLumpSpot)
                    {
                        costTotal++;
                        if (hasOngoingOrGdp) ongoingOrGdp++;
                    }
                }
            }
            Check(legacyTreasuryHits.Count == 0,
                $"0 landed-country events use treasury_percent or flat treasury (found: {string.Join(", ", legacyTreasuryHits.Take(5))})");
            var pctOngoingOrGdp = costTotal > 0 ? ongoingOrGdp * 100.0 / costTotal : 0.0;
            Check(costTotal > 0 && pctOngoingOrGdp >= 65.0,
                $">= 65% of cost-bearing landed-country event options use income_penalty or gdp_percent ({ongoingOrGdp}/{costTotal} = {pctOngoingOrGdp:0.0}%)");

            // 2. PreviewForNation and InlineSummary render real scaled $ / % income / % GDP / Income lines in EN and RU.
            var campaign = new Campaign(123, new Defines());
            var nation = new Nation
            {
                Id = 1,
                MapIndex = 1,
                Tag = "TST",
                Name = "Testia",
                Gdp = 2400f,
                Treasury = 500f,
                WeeklyGrossIncome = 3.5f,
                Population = 50_000_000
            };
            campaign.Nations.Add(nation);

            var incomePenaltyEff = new ScriptedEffects.Effect();
            incomePenaltyEff.IncomePenalty(0.06f, 270, "test_event");
            var incomeShareEff = new ScriptedEffects.Effect();
            incomeShareEff.Add("income_share", -0.05f);
            var gdpShareEff = new ScriptedEffects.Effect();
            gdpShareEff.Add("gdp_share", -0.01f);

            foreach (var lang in new[] { "english", "russian" })
            {
                Loc.Language = lang;
                var penPreview = string.Join("\n", ScriptedEffects.PreviewForNation(campaign, nation, incomePenaltyEff));
                var penInline = ScriptedEffects.InlineSummary(campaign, nation, incomePenaltyEff);
                var incPreview = string.Join("\n", ScriptedEffects.PreviewForNation(campaign, nation, incomeShareEff));
                var incInline = ScriptedEffects.InlineSummary(campaign, nation, incomeShareEff);
                var gdpPreview = string.Join("\n", ScriptedEffects.PreviewForNation(campaign, nation, gdpShareEff));
                var gdpInline = ScriptedEffects.InlineSummary(campaign, nation, gdpShareEff);

                var expWeekly = Fmt.Money(3.5f * 0.06f);
                Check(penPreview.Contains("$") && penPreview.Contains("270") &&
                      penInline.Contains(expWeekly) && penInline.Contains("270"),
                    $"{lang} income_penalty preview & inline summary format scaled weekly $ ({penInline})");
                Check(incPreview.Contains("→") && incPreview.Contains("$") && incPreview.Contains("%") &&
                      incInline.Contains("$") && incInline.Contains("%"),
                    $"{lang} income_share preview & inline summary show before->after $ and % annual income ({incInline})");
                Check(gdpPreview.Contains("→") && gdpPreview.Contains("$") && gdpPreview.Contains("%") &&
                      gdpInline.Contains("$") && gdpInline.Contains("%"),
                    $"{lang} gdp_share preview & inline summary show before->after $ and % GDP ({gdpInline})");
                if (lang == "russian")
                {
                    Check(penInline.Contains("Доход:") && incPreview.Contains("годовых доходов") && gdpPreview.Contains("ВВП"),
                        $"Russian preview/inline summary uses Russian labels ({penInline} | {incPreview})");
                }
            }

            // 3. Every event title, description, option title, and consequence exists in BOTH English and Russian tables.
            var gaps = new List<string>();
            var enKeys = new HashSet<string>(Loc.Keys("english"), StringComparer.Ordinal);
            var ruKeys = new HashSet<string>(Loc.Keys("russian"), StringComparer.Ordinal);
            foreach (var def in EventCatalog.All)
            {
                CheckEventKey(enKeys, ruKeys, def.Id + ".title", def.Title, gaps);
                CheckEventKey(enKeys, ruKeys, def.Id + ".desc", def.Description, gaps);
                foreach (var opt in def.Options)
                {
                    CheckEventKey(enKeys, ruKeys, def.Id + ".opt", opt.Title, gaps);
                    if (!string.IsNullOrWhiteSpace(opt.Consequence))
                        CheckEventKey(enKeys, ruKeys, def.Id + ".con", opt.Consequence, gaps);
                }
            }
            Check(gaps.Count == 0, $"All EventCatalog strings exist in EN and RU tables (gaps={gaps.Count}: {string.Join("; ", gaps.Take(3))})");

            // 4. EventUI & DomesticCrisisUI thread-safety and inline UI contracts.
            var eventUiCs = Read(root, "Scripts/ui/EventUI.cs");
            var domesticCs = Read(root, "Scripts/ui/DomesticCrisisUI.cs");
            Check(eventUiCs.Contains("campaign.TryEnterRead(50, true)") &&
                  eventUiCs.Contains("campaign.ExitRead()") &&
                  eventUiCs.Contains("ScriptedEffects.InlineSummary(campaign, player, option.Effect, option.Modifiers)") &&
                  eventUiCs.Contains("ScriptedEffects.PreviewForNation(campaign, player, option.Effect)") &&
                  eventUiCs.Contains("isTouch ? 64 : 58") &&
                  eventUiCs.Contains("RunConsoleWrite(") &&
                  !eventUiCs.Contains("_campaign.Commands.ApplyAll("),
                "EventUI guards RefreshEventInterface/OptionTooltip with TryEnterRead, shows InlineSummary & PreviewForNation, and executes via RunConsoleWrite");
            Check(!eventUiCs.Contains("AttachEventChoiceLabels") ||
                  !Regex.IsMatch(eventUiCs[eventUiCs.IndexOf("AttachEventChoiceLabels", StringComparison.Ordinal)..], @"new Color\("),
                "EventUI choice labels use Ui.Colour tokens without raw new Color(...) literals");
            Check(Regex.Matches(domesticCs, @"RunConsoleWrite\(").Count == 3 &&
                  !domesticCs.Contains("_campaign.Commands.ApplyAll("),
                "DomesticCrisisUI replaces all 3 ApplyAll calls with RunConsoleWrite");
        }
        finally
        {
            Loc.ResetCatalog();
            Loc.Language = prevLang;
        }
    }

    private static void CheckNoLegacyTreasury(string where, ScriptedEffects.Effect eff, List<string> hits)
    {
        if (eff is null) return;
        foreach (var e in eff.Entries)
        {
            if (e.Key.Equals("treasury_percent", StringComparison.OrdinalIgnoreCase) ||
                e.Key.Equals("treasury", StringComparison.OrdinalIgnoreCase))
                hits.Add($"{where}:{e.Key}");
        }
    }

    private static void CheckEventKey(HashSet<string> enKeys, HashSet<string> ruKeys, string where, string text, List<string> gaps)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!enKeys.Contains(text)) gaps.Add($"EN:{where}:{text[..Math.Min(36, text.Length)]}");
        if (!ruKeys.Contains(text)) gaps.Add($"RU:{where}:{text[..Math.Min(36, text.Length)]}");
    }

    private static string Read(string root, string relPath) =>
        File.ReadAllText(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate DealOrForce.csproj above " + AppContext.BaseDirectory);
    }
}
