using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Verification suite for Wave 6B (`E4`):
/// - Data-driven catalog loading (`game/common/ambitions/00_national_ambitions.json`, 12 ambitions + 6 directives)
/// - `SelectAmbitionCommand`, 2-slot cap, replacement, live progress, Golden Era completion & no re-farming
/// - `ExecuteCabinetDirectiveCommand` treasury/PP costs, cooldowns, immediate & weekly effects
/// - Deterministic AI ambition & directive participation (1-thread == N-thread `CampaignFingerprint`)
/// - Backward-compatible `Nation` save/load round-trip and EN + RU localization parity.
/// </summary>
static class NationalAmbitionsTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("national ambitions: " + label);
        Console.WriteLine("PASS national ambitions: " + label);
    }

    [TestSuite("--national-ambitions-only", Order = 2696, IncludeInDefaultRun = true,
        Description = "NationalAmbitions catalog, Golden Era completion, Cabinet Directives, AI, save/fingerprint & EN+RU loc.")]
    public static void Run()
    {
        TestCatalogAndLocalizationParity();
        TestSelectProgressAndGoldenEraCompletion();
        TestCabinetDirectivesCostsCooldownsAndEffects();
        TestSaveLoadAndFingerprintDeterminism();
        TestAiParticipationAndThreadParity();
    }

    static void TestCatalogAndLocalizationParity()
    {
        NationalAmbitions.ResetCatalog();
        Check(NationalAmbitions.All.Count >= 12,
            $"loaded {NationalAmbitions.All.Count} data-driven national ambitions");
        Check(NationalAmbitions.Directives.Count >= 6,
            $"loaded {NationalAmbitions.Directives.Count} data-driven executive cabinet directives");

        var allLocOk = true;
        foreach (var amb in NationalAmbitions.All)
        {
            if (string.IsNullOrEmpty(Loc.TextIn("english", amb.Name)) ||
                string.IsNullOrEmpty(Loc.TextIn("russian", amb.Name)) ||
                string.IsNullOrEmpty(Loc.TextIn("english", amb.Description)) ||
                string.IsNullOrEmpty(Loc.TextIn("russian", amb.Description)))
            {
                allLocOk = false;
                break;
            }
        }
        foreach (var dir in NationalAmbitions.Directives)
        {
            if (string.IsNullOrEmpty(Loc.TextIn("english", dir.Name)) ||
                string.IsNullOrEmpty(Loc.TextIn("russian", dir.Name)) ||
                string.IsNullOrEmpty(Loc.TextIn("english", dir.Description)) ||
                string.IsNullOrEmpty(Loc.TextIn("russian", dir.Description)))
            {
                allLocOk = false;
                break;
            }
        }
        Check(allLocOk, "every national ambition and cabinet directive has EN + RU name & description localization");
    }

    static void TestSelectProgressAndGoldenEraCompletion()
    {
        NationalAmbitions.ResetCatalog();
        var c = new Campaign(60601);
        var n = c.AddNation(new Nation
        {
            Id = 0,
            MapIndex = 1,
            Tag = "AMB",
            Name = "Ambition Republic",
            Population = 25_000_000,
            Gdp = 1000f,
            Treasury = 150f,
            PoliticalPoints = 50f,
            Stability = 0.72f,
            Productivity = 1.00f
        });
        c.States.Add(new ScenarioState { Id = "amb_1", MapIndex = 1, NationMapIndex = 1, Population = 25_000_000 });
        c.InvalidateStateIndex();
        c.ConfigureSinglePlayer(n.Id);

        // Select 2 ambitions -> 3rd blocked until replacing.
        new SelectAmbitionCommand { IssuerId = n.Id, AmbitionId = "industrial_miracle" }.Apply(c);
        new SelectAmbitionCommand { IssuerId = n.Id, AmbitionId = "tech_hegemon" }.Apply(c);
        Check(NationalAmbitions.ActiveFor(c, n).Count == 2, "player selected 2 active national ambitions");
        Check(!NationalAmbitions.CanSelectAmbition(c, n, "civic_renewal", out var fullReason) && fullReason.Length > 0,
            $"3rd ambition refused when 2 slots full ('{fullReason}')");

        // Replace one active ambition cleanly.
        new SelectAmbitionCommand { IssuerId = n.Id, AmbitionId = "civic_renewal", ReplaceAmbitionId = "tech_hegemon" }.Apply(c);
        Check(NationalAmbitions.HasActive(n, "civic_renewal") && !NationalAmbitions.HasActive(n, "tech_hegemon"),
            "SelectAmbitionCommand replaces an active ambition cleanly");

        // Advance GDP past +15% target -> triggers Golden Era Momentum on weekly tick!
        var ppBefore = n.PoliticalPoints;
        var prodBefore = n.Productivity;
        n.Gdp = 1200f;
        NationalAmbitions.TickWeekly(c, n);
        Check(NationalAmbitions.HasCompleted(n, "industrial_miracle") && !NationalAmbitions.HasActive(n, "industrial_miracle"),
            "reaching GDP target completes industrial_miracle permanently");
        Check(NationalAmbitions.InGoldenEra(c, n) && n.GoldenEraCount == 1 && NationalAmbitions.GoldenEraDaysRemaining(c, n) >= 365,
            $"Golden Era triggered ({NationalAmbitions.GoldenEraDaysRemaining(c, n)}d remaining, count={n.GoldenEraCount})");
        Check(n.PoliticalPoints > ppBefore + 20f && n.Productivity > prodBefore + 0.02f,
            $"Golden Era granted immediate PP ({ppBefore:F1}->{n.PoliticalPoints:F1}) & productivity ({prodBefore:F3}->{n.Productivity:F3})");

        // Permanent completion badge blocks re-farming.
        Check(!NationalAmbitions.CanSelectAmbition(c, n, "industrial_miracle", out _),
            "completed ambition cannot be re-selected (no infinite farming)");
    }

    static void TestCabinetDirectivesCostsCooldownsAndEffects()
    {
        NationalAmbitions.ResetCatalog();
        var c = new Campaign(60602);
        var n = c.AddNation(new Nation
        {
            Id = 0,
            MapIndex = 1,
            Tag = "DIR",
            Name = "Directive State",
            Population = 15_000_000,
            Gdp = 500f,
            Treasury = 80f,
            PoliticalPoints = 40f,
            ConstructionCapacity = 1.0f,
            Stability = 0.65f,
            Productivity = 1.00f
        });
        c.States.Add(new ScenarioState { Id = "dir_1", MapIndex = 1, NationMapIndex = 1, Population = 15_000_000 });
        c.InvalidateStateIndex();
        c.ConfigureSinglePlayer(n.Id);

        var def = NationalAmbitions.FindDirective("directive_industrial_drive");
        Check(def is not null, "directive_industrial_drive exists in catalog");
        var (cashCost, ppCost) = NationalAmbitions.DirectiveCost(n, def!);
        var cashBefore = n.Treasury;
        var ppBefore = n.PoliticalPoints;
        var capBefore = n.ConstructionCapacity;

        new ExecuteCabinetDirectiveCommand { IssuerId = n.Id, DirectiveId = "directive_industrial_drive" }.Apply(c);
        Check(NationalAmbitions.IsDirectiveActive(c, n, "directive_industrial_drive"),
            "ExecuteCabinetDirectiveCommand activates directive_industrial_drive");
        Check(MathF.Abs((cashBefore - n.Treasury) - cashCost) < 0.01f &&
              MathF.Abs((ppBefore - n.PoliticalPoints) - ppCost) < 0.01f,
            $"Directive deducted exact treasury ({cashCost:F2}B) & PP ({ppCost:F0})");
        Check(n.ConstructionCapacity > capBefore + 0.30f,
            $"Emergency Industrial Drive raised ConstructionCapacity ({capBefore:F2} -> {n.ConstructionCapacity:F2})");
        Check(!NationalAmbitions.CanExecuteDirective(c, n, "directive_industrial_drive", out _),
            "Active directive cannot be stacked twice");
        Check(NationalAmbitions.DirectiveCooldownRemaining(c, n, "directive_industrial_drive") == def!.CooldownDays,
            $"Directive cooldown recorded ({def.CooldownDays}d)");

        // Weekly tick applies ongoing productivity boost while active.
        var prodBefore = n.Productivity;
        NationalAmbitions.TickWeekly(c, n);
        Check(n.Productivity > prodBefore + 0.001f,
            $"Active directive lifts weekly productivity ({prodBefore:F4} -> {n.Productivity:F4})");

        // Insufficient PP blocks another directive cleanly.
        n.PoliticalPoints = 2f;
        Check(!NationalAmbitions.CanExecuteDirective(c, n, "directive_anti_corruption", out var ppRefusal) && ppRefusal.Length > 0,
            $"Insufficient PP blocks directive cleanly ('{ppRefusal}')");
    }

    static void TestSaveLoadAndFingerprintDeterminism()
    {
        var c = Campaign.CreateBootstrap(60603);
        var usa = c.NationByTag("USA");
        Check(usa is not null, "USA present in bootstrap");
        var usaState = c.States.First(r => r.NationMapIndex == usa!.MapIndex);
        c.AttachProvinceTopology(ProvinceTopology.Build(
            Enumerable.Repeat((ushort)usaState.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)usa!.MapIndex, 512).ToArray(),
            32, 16, _ => false));
        c.Armies.Clear();
        usa!.PoliticalPoints = 80f;
        var fpBefore = CampaignFingerprint.Of(c);

        new SelectAmbitionCommand { IssuerId = usa.Id, AmbitionId = "industrial_miracle" }.Apply(c);
        new ExecuteCabinetDirectiveCommand { IssuerId = usa.Id, DirectiveId = "directive_industrial_drive" }.Apply(c);
        var fpAfter = CampaignFingerprint.Of(c);
        Check(fpBefore != fpAfter, $"CampaignFingerprint sensitive to ambitions & directives ({fpBefore} -> {fpAfter})");

        var tempDir = Path.Combine(Path.GetTempPath(), "dof-amb-wave6-" + Guid.NewGuid().ToString("N"));
        try
        {
            c.Defines.SaveTextFormat = true;
            SaveSystem.Save(tempDir, "wave6_amb", c, "test");
            var loaded = SaveSystem.Load(tempDir, "wave6_amb", "test");
            Check(loaded.Campaign is not null, $"SaveSystem.Load succeeded ({loaded.Warning ?? "ok"})");
            var loadedUsa = loaded.Campaign!.NationByTag("USA");
            Check(loadedUsa is not null &&
                  NationalAmbitions.HasActive(loadedUsa, "industrial_miracle") &&
                  NationalAmbitions.IsDirectiveActive(loaded.Campaign, loadedUsa, "directive_industrial_drive"),
                "Active ambitions & cabinet directives survive SaveSystem round-trip");
            Check(CampaignFingerprint.Of(loaded.Campaign) == fpAfter,
                "CampaignFingerprint bit-identical after SaveSystem round-trip");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    static void TestAiParticipationAndThreadParity()
    {
        var savedOverride = DeterministicParallel.MaxThreadsOverride;
        var savedSerial = DeterministicParallel.ForceSerial;
        try
        {
            DeterministicParallel.ForceSerial = false;
            ulong Run(int threads, out int aiWithAmbitions, out string issue)
            {
                DeterministicParallel.MaxThreadsOverride = threads;
                var c = Campaign.CreateBootstrap(42);
                c.IsPaused = false;
                c.Defines.HoursPerTick = 24;
                for (var d = 0; d < 63; d++) c.TickDay();
                aiWithAmbitions = c.Nations.Count(n => !n.IsHuman &&
                    ((n.ActiveAmbitions?.Count ?? 0) > 0 || (n.CompletedAmbitions?.Count ?? 0) > 0));
                issue = NumericIntegrity.FirstLiveIssue(c);
                return CampaignFingerprint.Of(c);
            }

            var fp1 = Run(1, out var ai1, out var issue1);
            var fpN = Run(0, out var aiN, out var issueN);
            Check(issue1 is null && issueN is null, "63-day campaign with NationalAmbitions has zero NaN/integrity issues");
            Check(ai1 > 10 && ai1 == aiN, $"AI nations deterministically selected ambitions ({ai1} nations)");
            Check(fp1 == fpN, $"63-day CampaignFingerprint bit-identical 1-thread ({fp1}) == N-thread ({fpN})");
        }
        finally
        {
            DeterministicParallel.MaxThreadsOverride = savedOverride;
            DeterministicParallel.ForceSerial = savedSerial;
        }
    }
}
