using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

static class RegionActionsShellTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL region-actions-shell: " + label);
        Console.WriteLine("PASS region-actions-shell: " + label);
    }

    [TestSuite("--region-actions-shell-only", Order = 2645, IncludeInDefaultRun = true,
        Description = "Wave-2 Region Actions card, Allied Occupation Request/Transfer simulation, NetworkCommandCodec, and EN/RU parity.")]
    public static void Run()
    {
        RunOccupationTransferSimulationTests();
        RunSourceAndLocalizationContractTests();
    }

    private static (Campaign World, Nation Player, Nation Ally, Nation Enemy, Nation Neutral, ScenarioState EnemyState) BuildOccupationFixture()
    {
        // 32x16 tiny synthetic raster: 4 horizontal strips for 4 states (1=Player, 2=Ally, 3=Enemy, 4=Neutral).
        const int w = 32, h = 16;
        var statesRaster = new ushort[w * h];
        var controlRaster = new ushort[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                ushort idx = x < 8 ? (ushort)1 : x < 16 ? (ushort)2 : x < 24 ? (ushort)3 : (ushort)4;
                statesRaster[y * w + x] = idx;
                controlRaster[y * w + x] = idx;
            }
        }
        var topo = ProvinceTopology.Build(statesRaster, controlRaster, w, h, _ => false,
            new ProvinceBuildProfile(CellsPerProvince: 32, MinimumProvinceCells: 4));

        var player = new Nation { Id = 0, MapIndex = 1, Tag = "PLY", Name = "Playeria", IsHuman = true, Gdp = 500f };
        var ally = new Nation { Id = 1, MapIndex = 2, Tag = "ALY", Name = "Allyland", IsHuman = false, Gdp = 300f };
        var enemy = new Nation { Id = 2, MapIndex = 3, Tag = "ENM", Name = "Enemia", IsHuman = false, Gdp = 400f };
        var neutral = new Nation { Id = 3, MapIndex = 4, Tag = "NEU", Name = "Neutralia", IsHuman = false, Gdp = 200f };

        var sPlayer = new ScenarioState { MapIndex = 1, NationId = "PLY", NationMapIndex = 1, Name = "Playeria Home", Population = 5_000_000 };
        var sAlly = new ScenarioState { MapIndex = 2, NationId = "ALY", NationMapIndex = 2, Name = "Allyland Home", Population = 4_000_000 };
        var sEnemy = new ScenarioState { MapIndex = 3, NationId = "ENM", NationMapIndex = 3, Name = "Eastern Kherson", Population = 3_000_000 };
        var sNeutral = new ScenarioState { MapIndex = 4, NationId = "NEU", NationMapIndex = 4, Name = "Neutralia Home", Population = 2_000_000 };

        var campaign = new Campaign(42);
        campaign.Nations.AddRange(new[] { player, ally, enemy, neutral });
        campaign.States.AddRange(new[] { sPlayer, sAlly, sEnemy, sNeutral });
        campaign.SetPlayer(player.Id);
        campaign.AttachProvinceTopology(topo);

        // Active war: Player + Ally vs Enemy
        var war = new StrategicWar { Id = 1, AttackerId = player.Id, DefenderId = enemy.Id };
        war.AttackerAllies.Add(ally.Id);
        campaign.Wars.Add(war);

        // Set Player occupying Enemy's state (sEnemy)
        var enemyProvs = topo.ProvincesOfState(sEnemy.MapIndex);
        Check(enemyProvs.Length > 0, "synthetic fixture created enemy provinces");
        for (var i = 0; i < enemyProvs.Length; i++)
        {
            var p = enemyProvs[i];
            campaign.ProvinceController[p] = (ushort)player.MapIndex;
            campaign.ProvinceOccupation[p] = 0.25f; // below secure threshold to verify preservation >= secureThreshold
        }
        sEnemy.HeldById = player.Id;
        sEnemy.HeldSinceDay = 100;
        campaign.MarkMapChanged();
        HeldRegions.Refresh(campaign);

        return (campaign, player, ally, enemy, neutral, sEnemy);
    }

    private static void RunOccupationTransferSimulationTests()
    {
        var (world, player, ally, enemy, neutral, enemyState) = BuildOccupationFixture();

        // 1. Read-only checks keep CampaignFingerprint bit-identical
        var fpBeforeRead = CampaignFingerprint.Of(world);
        var transferRefusal = OccupationTransfer.TransferRefusal(world, player, ally, enemyState);
        var neutralRefusal = OccupationTransfer.TransferRefusal(world, player, neutral, enemyState);
        var allies = OccupationTransfer.WarAlliesForTransfer(world, player, enemyState);
        var fpAfterRead = CampaignFingerprint.Of(world);
        Check(fpBeforeRead == fpAfterRead, "read-only OccupationTransfer checks keep CampaignFingerprint bit-identical");
        Check(transferRefusal.Length == 0, $"TransferRefusal allows transfer to war ally (got: '{transferRefusal}')");
        Check(neutralRefusal.Length > 0, "TransferRefusal refuses transfer to non-ally neutral nation");
        Check(allies.Count == 1 && allies[0].Id == ally.Id, "WarAlliesForTransfer returns only war ally");

        // 2. NetworkCommandCodec roundtrip for TransferOccupationCommand & RequestOccupationCommand
        var rawTransfer = new TransferOccupationCommand { IssuerId = player.Id, RecipientId = ally.Id, StateMapIndex = enemyState.MapIndex };
        ICampaignCommand decodedTransfer = null;
        Check(NetworkCommandCodec.TryEncode(rawTransfer, out var tType, out var tJson) &&
              NetworkCommandCodec.TryDecode(tType, tJson, out decodedTransfer) &&
              decodedTransfer is TransferOccupationCommand dtDecoded &&
              dtDecoded.IssuerId == player.Id && dtDecoded.RecipientId == ally.Id && dtDecoded.StateMapIndex == enemyState.MapIndex,
            "NetworkCommandCodec round-trips TransferOccupationCommand");
        var dt = (TransferOccupationCommand)decodedTransfer;

        var rawRequest = new RequestOccupationCommand { IssuerId = player.Id, StateMapIndex = enemyState.MapIndex };
        Check(NetworkCommandCodec.TryEncode(rawRequest, out var rType, out var rJson) &&
              NetworkCommandCodec.TryDecode(rType, rJson, out var decodedRequest) &&
              decodedRequest is RequestOccupationCommand dr &&
              dr.IssuerId == player.Id && dr.StateMapIndex == enemyState.MapIndex,
            "NetworkCommandCodec round-trips RequestOccupationCommand");

        // 3. Execute TransferOccupationCommand (Player -> Ally)
        var revBefore = world.MapRevision;
        world.Commands.Enqueue(dt);
        world.Commands.ApplyAll(world);
        var fpAfterTransfer = CampaignFingerprint.Of(world);
        var p0 = world.ProvinceMap.ProvincesOfState(enemyState.MapIndex)[0];
        Check(dt.Done && dt.Refusal.Length == 0,
            $"TransferOccupationCommand succeeded (done={dt.Done}, refusal='{dt.Refusal}', from={world.NationById(dt.IssuerId)?.MapIndex}, to={world.NationById(dt.RecipientId)?.MapIndex}, st={world.StateByMapIndex(dt.StateMapIndex)?.MapIndex}, ctrl={world.ProvinceController[p0]}, own={world.ProvinceMap.ProvinceToNation[p0]})");
        Check(world.MapRevision > revBefore, "TransferOccupationCommand bumped MapRevision");
        Check(fpAfterTransfer != fpBeforeRead, "TransferOccupationCommand changed CampaignFingerprint");
        var provs = world.ProvinceMap.ProvincesOfState(enemyState.MapIndex);
        for (var i = 0; i < provs.Length; i++)
        {
            Check(world.ProvinceController[provs[i]] == ally.MapIndex, $"province {provs[i]} transferred to ally");
            Check(world.ProvinceOccupation[provs[i]] >= world.Defines.OccupationSecureThreshold - 1e-4f,
                $"province {provs[i]} occupation preserved >= OccupationSecureThreshold");
        }
        Check(enemyState.HeldById == ally.Id, "ScenarioState.HeldById updated to recipient ally");
        Check(world.Chronicle.Entries.Any(e => e.Kind == "occupation_transferred" && e.ActorId == player.Id && e.TargetId == ally.Id),
            "Chronicle recorded occupation_transferred");

        // 4. Homeland refusal: Ally has homeland claim on enemyState while Player does NOT -> RequestOccupationCommand refuses
        enemyState.CoreTags.Add(ally.Tag);
        var homelandScore = OccupationTransfer.RequestAcceptance(world, player, ally, enemyState);
        Check(homelandScore < 0, "RequestAcceptance refuses when AI holder has homeland claim and requester does not");
        var reqRefused = new RequestOccupationCommand { IssuerId = player.Id, StateMapIndex = enemyState.MapIndex };
        world.Commands.Enqueue(reqRefused);
        world.Commands.ApplyAll(world);
        Check(!reqRefused.Done && reqRefused.Refusal.Length > 0, "RequestOccupationCommand refused on holder homeland");
        Check(world.ProvinceController[provs[0]] == ally.MapIndex, "provinces remained with ally after homeland refusal");

        // 5. Acceptance: once Player also has homeland/claim (or Ally has no homeland), RequestOccupationCommand succeeds
        enemyState.CoreTags.Remove(ally.Tag);
        enemyState.Claims.Add(player.Tag);
        var acceptScore = OccupationTransfer.RequestAcceptance(world, player, ally, enemyState);
        Check(acceptScore >= 0, "RequestAcceptance accepts when requester has claim and holder has no homeland");
        var reqAccepted = new RequestOccupationCommand { IssuerId = player.Id, StateMapIndex = enemyState.MapIndex };
        world.Commands.Enqueue(reqAccepted);
        world.Commands.ApplyAll(world);
        Check(reqAccepted.Done && reqAccepted.Refusal.Length == 0, "RequestOccupationCommand succeeded and transferred occupation back to player");
        Check(world.ProvinceController[provs[0]] == player.MapIndex, "provinces transferred back to player");
        Check(enemyState.HeldById == player.Id, "ScenarioState.HeldById updated back to player");

        // 6. Human holder refusal: RequestRefusal refuses when holder is human
        world.Commands.Enqueue(new TransferOccupationCommand { IssuerId = player.Id, RecipientId = ally.Id, StateMapIndex = enemyState.MapIndex });
        world.Commands.ApplyAll(world);
        ally.IsHuman = true;
        var humanRefusal = OccupationTransfer.RequestRefusal(world, player, enemyState, out var detectedHolder);
        Check(detectedHolder == ally && humanRefusal.Length > 0, "RequestRefusal refuses when allied holder is human");
    }

    private static void RunSourceAndLocalizationContractTests()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));
        string ReadRel(string rel) => File.ReadAllText(Path.Combine(repoRoot, rel));

        // 1. Files + .cs.uid sidecars + zero static mutable fields in OccupationTransfer + zero Godot in sim
        var occPath = Path.Combine(repoRoot, "Scripts", "Simulation", "OccupationTransfer.cs");
        var viewPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellRegionActionsCardView.cs");
        Check(File.Exists(occPath) && File.Exists(occPath + ".uid"), "OccupationTransfer.cs and .cs.uid exist");
        Check(File.Exists(viewPath) && File.Exists(viewPath + ".uid"), "ShellRegionActionsCardView.cs and .cs.uid exist");

        var occSrc = File.ReadAllText(occPath);
        Check(!occSrc.Contains("using Godot", StringComparison.Ordinal) && !occSrc.Contains("Godot.", StringComparison.Ordinal),
            "OccupationTransfer.cs has zero Godot types");
        var staticFields = typeof(OccupationTransfer).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(staticFields.Length == 0, "OccupationTransfer has zero static fields");

        // 2. ShellRegionActionsCardView contracts: zero raw Color, all restored regional actions present
        var viewSrc = File.ReadAllText(viewPath);
        Check(!viewSrc.Contains("new Color(", StringComparison.Ordinal), "ShellRegionActionsCardView has zero raw new Color(");
        Check(viewSrc.Contains("HeldRegions.ReferendumRefusal", StringComparison.Ordinal) &&
              viewSrc.Contains("\"held_referendum\"", StringComparison.Ordinal) &&
              viewSrc.Contains("RequestOccupationCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("TransferOccupationCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("OccupationTransfer.WarAlliesForTransfer", StringComparison.Ordinal) &&
              viewSrc.Contains("LandArrangementCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("\"request_lease\"", StringComparison.Ordinal) &&
              viewSrc.Contains("\"send_troops\"", StringComparison.Ordinal) &&
              viewSrc.Contains("\"end_lease\"", StringComparison.Ordinal) &&
              viewSrc.Contains("\"withdraw\"", StringComparison.Ordinal) &&
              viewSrc.Contains("KinStatecraft.Kinds", StringComparison.Ordinal) &&
              viewSrc.Contains("KinStatecraftCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("ColonisationCore.IsUnclaimed", StringComparison.Ordinal) &&
              viewSrc.Contains("ColoniseCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("AbandonColonyCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("RebuildRegionCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("GrantRegionalAutonomyCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("RevokeRegionalAutonomyCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("ConciliateRegionCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("OpenCapitalChooserForState", StringComparison.Ordinal) &&
              viewSrc.Contains("MovementDealingCommand", StringComparison.Ordinal) &&
              viewSrc.Contains("OpenRegionalLawsForState", StringComparison.Ordinal) &&
              viewSrc.Contains("shell.reg_act.confirm_touch", StringComparison.Ordinal),
            "ShellRegionActionsCardView exposes every restored regional action and touch-safe confirm hint");

        // 3. Wiring in ShellRegionVisualsView, ShellContext, NewShellConstructionUI, NewShellUI
        var visSrc = ReadRel("Scripts/ui/Shell/ShellRegionVisualsView.cs");
        var ctxSrc = ReadRel("Scripts/ui/Shell/ShellContext.cs");
        var constrSrc = ReadRel("Scripts/ui/NewShellConstructionUI.cs");
        var shellSrc = ReadRel("Scripts/ui/Shell/NewShellUI.cs");
        Check(visSrc.Contains("ShellRegionActionsCardView.AppendRegionActionsCard", StringComparison.Ordinal),
            "ShellRegionVisualsView renders ShellRegionActionsCardView on Overview tab");
        Check(ctxSrc.Contains("CommitImmediateCommand", StringComparison.Ordinal) &&
              ctxSrc.Contains("OpenCapitalChooserForState", StringComparison.Ordinal) &&
              ctxSrc.Contains("OpenRegionalLawsForState", StringComparison.Ordinal) &&
              ctxSrc.Contains("PendingRegionActionKey", StringComparison.Ordinal),
            "ShellContext exposes region action delegates");
        Check(constrSrc.Contains("BindRegionShellContextActions", StringComparison.Ordinal) &&
              constrSrc.Contains("OpenCapitalChooserForShell", StringComparison.Ordinal) &&
              constrSrc.Contains("RaiseOverlayAboveNewShell(_capitalCityChooser)", StringComparison.Ordinal),
            "NewShellConstructionUI binds region action dispatchers and raises capital chooser above NewShell");
        Check(shellSrc.Contains("RaiseOverlayAboveNewShell(_capitalCityChooser)", StringComparison.Ordinal),
            "NewShellUI keeps _capitalCityChooser raised above NewShell");

        // 4. EN & RU localization parity
        var enPath = Path.Combine(repoRoot, "game", "localization", "english", "zz_454_region_actions_shell.json");
        var ruPath = Path.Combine(repoRoot, "game", "localization", "russian", "zz_454_region_actions_shell.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU zz_454_region_actions_shell.json exist");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath));
        var ru = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ruPath));
        Check(en is not null && ru is not null && en.Count >= 20, "zz_454_region_actions_shell.json has >= 20 entries");
        Check(en.Count == ru.Count && en.Keys.All(ru.ContainsKey), "EN and RU zz_454_region_actions_shell.json have identical keys");
        var phRx = new Regex(@"\{[0-9]+(?::[^}]*)?\}");
        foreach (var (k, enVal) in en)
        {
            var ruVal = ru[k];
            Check(!string.IsNullOrWhiteSpace(enVal) && !string.IsNullOrWhiteSpace(ruVal), $"non-empty loc value for {k}");
            var enPh = phRx.Matches(enVal).Select(m => m.Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"matching format placeholders for {k}");
        }
    }
}
