using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

static class PausedRecruitBuildTests
{
    private static void Check(bool condition, string caption)
    {
        if (!condition) throw new Exception("paused recruit/build: " + caption);
        Console.WriteLine("PASS paused recruit/build: " + caption);
    }

    private static (Campaign Campaign, Nation Player, ScenarioState Home, ScenarioState Second) Fixture()
    {
        const int width = 64, height = 32;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var province = x / 16 + 1;
            var index = y * width + x;
            states[index] = (ushort)(4000 + province);
            owners[index] = (ushort)21;
            rgb[index * 3 + 2] = (byte)province;
        }
        var map = ProvinceTopology.FromColorRaster(
            states, owners, width, height,
            new RgbDataMap(width, height, rgb),
            Enumerable.Range(1, 4).ToDictionary(id => id, id => id));

        var campaign = new Campaign(20261127)
        {
            ScenarioId = "paused_recruit_build_fixture",
            IsPaused = true
        };
        var player = new Nation
        {
            Id = 0,
            Tag = "PAU",
            MapIndex = 21,
            IsHuman = true,
            Population = 50_000_000,
            Gdp = 100f,
            Treasury = 200f,
            ManpowerReserve = 5_000_000f,
            Stability = 0.8f
        };
        campaign.Nations.Add(player);
        campaign.SetPlayer(player.Id);
        for (var p = 1; p <= 4; p++)
        {
            var st = new ScenarioState
            {
                Id = "paused_region_" + p,
                Name = "Paused Region " + p,
                MapIndex = 4000 + p,
                NationMapIndex = player.MapIndex,
                NationId = player.Tag,
                Population = 10_000_000
            };
            st.Buildings[BuildingKind.CivilianIndustry] = 1;
            campaign.States.Add(st);
        }
        campaign.AttachProvinceTopology(map);
        return (campaign, player, campaign.States[0], campaign.States[1]);
    }

    [TestSuite("--paused-recruit-build-only", IncludeInDefaultRun = true,
        Description = "Verifies paused recruitment/construction immediate commit, HudSnapshot sync, anti-overspend, MP interceptor safety, and UI call-site contracts.")]
    public static void Run()
    {
        PausedRecruitmentAndSnapshotSync();
        PausedConstructionAndDemolitionSnapshotSync();
        MultiplayerInterceptorSafety();
        UiCallSiteContracts();
    }

    private static void PausedRecruitmentAndSnapshotSync()
    {
        var (c, player, home, second) = Fixture();
        var template = UnitTypeCatalog.Get("line_infantry");
        var singleCost = RecruitmentSystem.TotalBatchCost(c, player, template, 1, MobilisationMode.Standard, out _);
        Check(singleCost > 0f, "line_infantry has positive upfront recruitment cost");

        // Give enough Treasury for exactly 2 batches, not 3.
        player.Treasury = singleCost * 2.15f;
        var initialTreasury = player.Treasury;
        var initialManpower = player.ManpowerReserve;

        using var runner = new BackgroundSimRunner();
        runner.Start(c);
        try
        {
            var snap0 = runner.ReadHudSnapshot(c);
            Check(snap0.Version > 0 && MathF.Abs(snap0.Treasury - initialTreasury) < 0.01f,
                "initial paused HudSnapshot matches starting Treasury");

            // Click region 1 while paused
            var cmd1 = new RaiseArmyCommand
            {
                IssuerId = player.Id,
                StateMapIndex = home.MapIndex,
                TemplateId = template.Id,
                Divisions = 1,
                Mode = MobilisationMode.Standard
            };
            runner.RunWrite(world =>
            {
                world.Commands.Enqueue(cmd1);
                world.Commands.ApplyAll(world);
            }, refreshSnapshot: true);

            var snap1 = runner.ReadHudSnapshot(c);
            Check(cmd1.FailureReason.Length == 0 && c.ElapsedDays == 0 && c.IsPaused,
                "first paused recruitment executes without advancing clock");
            Check(MathF.Abs(player.Treasury - (initialTreasury - singleCost)) < 0.01f,
                "first paused recruitment immediately deducts live player Treasury");
            Check(MathF.Abs(snap1.Treasury - player.Treasury) < 0.01f && snap1.Version > snap0.Version,
                "first paused recruitment immediately updates CampaignHudSnapshot.Treasury");
            Check(MathF.Abs(snap1.ManpowerReserve - (initialManpower - template.RecruitmentPersonnel)) < 1f,
                "first paused recruitment immediately updates CampaignHudSnapshot.ManpowerReserve");

            // Click region 2 while paused
            var cost2 = RecruitmentSystem.TotalBatchCost(c, player, template, 1, MobilisationMode.Standard, out _);
            var cmd2 = new RaiseArmyCommand
            {
                IssuerId = player.Id,
                StateMapIndex = second.MapIndex,
                TemplateId = template.Id,
                Divisions = 1,
                Mode = MobilisationMode.Standard
            };
            runner.RunWrite(world =>
            {
                world.Commands.Enqueue(cmd2);
                world.Commands.ApplyAll(world);
            }, refreshSnapshot: true);

            var snap2 = runner.ReadHudSnapshot(c);
            Check(cmd2.FailureReason.Length == 0 && c.Recruitment.Count == 2,
                "second paused recruitment queues in second region");
            Check(MathF.Abs(snap2.Treasury - player.Treasury) < 0.01f && player.Treasury < cost2,
                "second paused recruitment updates HudSnapshot and leaves less than one batch remaining");

            // Third click while paused must be rejected (anti-overspend)
            var treasuryBeforeOverspend = player.Treasury;
            var cmd3 = new RaiseArmyCommand
            {
                IssuerId = player.Id,
                StateMapIndex = home.MapIndex,
                TemplateId = template.Id,
                Divisions = 1,
                Mode = MobilisationMode.Standard
            };
            runner.RunWrite(world =>
            {
                world.Commands.Enqueue(cmd3);
                world.Commands.ApplyAll(world);
            }, refreshSnapshot: true);

            var snap3 = runner.ReadHudSnapshot(c);
            Check(cmd3.FailureReason.Length > 0 && c.Recruitment.Count == 2 &&
                  MathF.Abs(player.Treasury - treasuryBeforeOverspend) < 0.001f &&
                  MathF.Abs(snap3.Treasury - treasuryBeforeOverspend) < 0.001f,
                "third paused click cannot overspend Treasury");

            // Cancel first recruitment while paused -> Treasury refund reflects immediately in HudSnapshot
            var cancelId = c.Recruitment[0].Id;
            var treasuryBeforeCancel = player.Treasury;
            runner.RunWrite(world =>
            {
                world.Commands.Enqueue(new CancelRecruitmentCommand { IssuerId = player.Id, OrderId = cancelId });
                world.Commands.ApplyAll(world);
            }, refreshSnapshot: true);

            var snapCancel = runner.ReadHudSnapshot(c);
            Check(player.Treasury > treasuryBeforeCancel && MathF.Abs(snapCancel.Treasury - player.Treasury) < 0.01f,
                "paused recruitment cancellation immediately reflects Treasury refund in HudSnapshot");
        }
        finally
        {
            runner.Stop();
        }
    }

    private static void PausedConstructionAndDemolitionSnapshotSync()
    {
        var (c, player, home, _) = Fixture();
        c.Defines.MaxConstructionProjects = 2;
        c.Defines.ConstructionSitesInParallel = false;

        using var runner = new BackgroundSimRunner();
        runner.Start(c);
        try
        {
            // Queue 2 buildings while paused; 3rd must hit slot cap immediately while paused
            for (var i = 0; i < 3; i++)
            {
                runner.RunWrite(world =>
                {
                    world.Commands.Enqueue(new QueueConstructionCommand
                    {
                        IssuerId = player.Id,
                        StateMapIndex = home.MapIndex,
                        Building = BuildingKind.CivilianIndustry
                    });
                    world.Commands.ApplyAll(world);
                }, refreshSnapshot: true);
            }
            Check(c.Construction.Count == 2 && c.ElapsedDays == 0 && c.IsPaused,
                "paused construction commits immediately and enforces national slot cap while paused");

            // Cancel one site while paused
            var projectId = c.Construction[0].Id;
            runner.RunWrite(world =>
            {
                world.Commands.Enqueue(new CancelConstructionCommand { IssuerId = player.Id, ProjectId = projectId });
                world.Commands.ApplyAll(world);
            }, refreshSnapshot: true);
            Check(c.Construction.Count == 1, "paused CancelConstructionCommand removes project immediately");

            // Demolish an existing building while paused -> scrap refund reflects immediately in HudSnapshot
            var treasuryBeforeDemolish = player.Treasury;
            runner.RunWrite(world =>
            {
                world.Commands.Enqueue(new DemolishBuildingCommand
                {
                    IssuerId = player.Id,
                    StateMapIndex = home.MapIndex,
                    Building = BuildingKind.CivilianIndustry
                });
                world.Commands.ApplyAll(world);
            }, refreshSnapshot: true);
            var snapAfterDemolish = runner.ReadHudSnapshot(c);
            Check(player.Treasury > treasuryBeforeDemolish &&
                  MathF.Abs(snapAfterDemolish.Treasury - player.Treasury) < 0.01f,
                "paused DemolishBuildingCommand immediately updates HudSnapshot.Treasury with scrap refund");
        }
        finally
        {
            runner.Stop();
        }
    }

    private static void MultiplayerInterceptorSafety()
    {
        var (c, player, home, _) = Fixture();
        var intercepted = 0;
        c.Commands.SubmitInterceptor = _ => { intercepted++; return true; };
        var treasuryBefore = player.Treasury;

        using var runner = new BackgroundSimRunner();
        runner.Start(c);
        try
        {
            runner.RunWrite(world =>
            {
                world.Commands.Enqueue(new RaiseArmyCommand
                {
                    IssuerId = player.Id,
                    StateMapIndex = home.MapIndex,
                    TemplateId = "line_infantry",
                    Divisions = 1
                });
                world.Commands.ApplyAll(world);
                world.Commands.Enqueue(new QueueConstructionCommand
                {
                    IssuerId = player.Id,
                    StateMapIndex = home.MapIndex,
                    Building = BuildingKind.CivilianIndustry
                });
                world.Commands.ApplyAll(world);
            }, refreshSnapshot: true);

            Check(intercepted == 2 && c.Recruitment.Count == 0 && c.Construction.Count == 0 &&
                  MathF.Abs(player.Treasury - treasuryBefore) < 0.001f,
                "remote MP SubmitInterceptor forwards commands without local double-apply or Treasury deduction");
        }
        finally
        {
            runner.Stop();
        }
    }

    private static void UiCallSiteContracts()
    {
        var root = FindRepoRoot();
        var helperPath = Path.Combine(root, "Scripts", "ui", "ImmediateCampaignCommands.cs");
        Check(File.Exists(helperPath), "ImmediateCampaignCommands.cs exists");
        var helperSrc = File.ReadAllText(helperPath);
        Check(helperSrc.Contains("_simRunner.RunWrite(write, refreshSnapshot: true)"),
            "ImmediateCampaignCommands routes active runner writes through RunWrite(refreshSnapshot: true)");
        Check(helperSrc.Contains("RefreshNewShell(force: true)"),
            "ImmediateCampaignCommands forces NewShell HUD refresh");
        Check(helperSrc.Contains("SyncPausedHudSnapshotIfStale"),
            "ImmediateCampaignCommands provides paused HudSnapshot staleness guard");

        var gameSrc = File.ReadAllText(Path.Combine(root, "Scripts", "DealOrForceGame.cs"));
        Check(gameSrc.Contains("SyncPausedHudSnapshotIfStale();"),
            "DealOrForceGame.Refresh invokes SyncPausedHudSnapshotIfStale()");

        var guardedUiFiles = new[]
        {
            "Scripts/ui/RecruitmentPlacementUI.cs",
            "Scripts/ui/ConstructionPlacementUI.cs",
            "Scripts/ui/Shell/ShellRegionBuildTabView.cs",
            "Scripts/ui/Controls/ControlsIntegrationUI.cs",
            "Scripts/ui/RecruitmentWorkbenchUI.cs",
            "Scripts/ui/RecruitmentUI.cs",
            "Scripts/ui/ContractorsUI.cs",
            "Scripts/ui/EconomyUI.cs",
            "Scripts/ui/RegionInteractionUI.cs",
            "Scripts/ui/RegionGroupBuildUI.cs",
            "Scripts/ui/StateAffairsUI.cs"
        };

        foreach (var rel in guardedUiFiles)
        {
            var src = File.ReadAllText(Path.Combine(root, rel));
            var usesSharedHelper =
                src.Contains("CommitImmediateCampaignCommand(") ||
                src.Contains("CommitImmediateCampaignWrite(") ||
                src.Contains("ImmediateCampaignCommands.Commit(");
            Check(usesSharedHelper, $"{rel} routes mutations through ImmediateCampaignCommands helper");
            Check(!src.Contains(".Commands.Enqueue(new RaiseArmyCommand") &&
                  !src.Contains(".Commands.Enqueue(new QueueConstructionCommand") &&
                  !src.Contains(".Commands.Enqueue(new CancelConstructionCommand") &&
                  !src.Contains(".Commands.Enqueue(new CancelRecruitmentCommand") &&
                  !src.Contains(".Commands.Enqueue(new DemolishBuildingCommand") &&
                  !src.Contains(".Commands.Enqueue(new HireContractorsCommand"),
                $"{rel} contains no bare Commands.Enqueue for recruit/build/cancel/demolish");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
