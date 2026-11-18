using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// Regression tests for AI/Player Parity Wave 1 (П5 casus belli propagation on AI war
/// declarations; П2 signing pending AI peace offers via AnswerPeaceOfferCommand).
/// </summary>
internal static class ParityWave1Tests
{
    private static readonly MethodInfo Warmaking = typeof(AiStatecraft).GetMethod("Warmaking",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo DailyTables = typeof(AiStatecraft).GetMethod("EnsureDailyTables",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("parity wave 1: " + text);
        Console.WriteLine("PASS parity wave 1: " + text);
    }

    internal static void Run()
    {
        _ = Campaign.CreateBootstrap(20261118);
        TestP5AiCasusBelliDeclaration();
        TestP2PlayerPeaceOfferSign();
    }

    private static void TestP5AiCasusBelliDeclaration()
    {
        var justified = BuildAiWarWorld(seed: 20261118, addCause: true, needsCause: true);
        var expectedCause = Statecraft.CasusBelli(justified.C, justified.Us, justified.Them);
        Check(!string.IsNullOrEmpty(expectedCause),
            "opinion memory gives the attacker a non-empty CasusBelli without territorial claim or annexation");
        Check(TerritorialClaims.RecoveryGoal(justified.C, justified.Us, justified.Them) is null &&
              AnnexationAmbition.Aim(justified.C, justified.Us, justified.Them) is null,
            "the П5 fixture isolates non-territorial, non-annexation AI war declarations");

        ThinkWar(justified.C, justified.Us);
        Check(justified.C.Wars.Count == 1, "AI declares war when it holds a non-territorial casus belli");
        var war = justified.C.Wars[0];
        Check(war.Justification == expectedCause && justified.Us.LastWarJustification == expectedCause,
            $"AI declaration resolves and records CasusBelli '{expectedCause}' instead of empty string (got '{war.Justification}')");

        var twin = BuildAiWarWorld(seed: 20261118, addCause: true, needsCause: true);
        ThinkWar(twin.C, twin.Us);
        Check(CampaignFingerprint.Of(justified.C) == CampaignFingerprint.Of(twin.C),
            "П5 AI war declaration is deterministic across identical seeds");

        var blocked = BuildAiWarWorld(seed: 20261118, addCause: false, needsCause: true);
        ThinkWar(blocked.C, blocked.Us);
        Check(blocked.C.Wars.Count == 0,
            "AI still refuses to declare war when AiNeedsCauseForWar=true and no CasusBelli exists");

        var unjustified = BuildAiWarWorld(seed: 20261118, addCause: false, needsCause: false);
        Check(TerritorialClaims.RecoveryGoal(unjustified.C, unjustified.Us, unjustified.Them) is null &&
              AnnexationAmbition.Aim(unjustified.C, unjustified.Us, unjustified.Them) is null &&
              string.IsNullOrEmpty(Statecraft.CasusBelli(unjustified.C, unjustified.Us, unjustified.Them)),
            "unjustified AI fixture truly has no claim, annexation aim, or CasusBelli");
        ThinkWar(unjustified.C, unjustified.Us);
        Check(unjustified.C.Wars.Count == 1,
            "when AiNeedsCauseForWar=false and no cause exists, AI still declares an unjustified war");
        Check(string.IsNullOrEmpty(unjustified.C.Wars[0].Justification) &&
              string.IsNullOrEmpty(unjustified.Us.LastWarJustification),
            "genuinely causeless AI war keeps empty Justification");
        Check(unjustified.Us.Notoriety > justified.Us.Notoriety ||
              unjustified.Us.Stability < justified.Us.Stability ||
              unjustified.Us.WarSupport < justified.Us.WarSupport,
            "resolved CasusBelli avoids unjustified-war penalties compared with a causeless declaration");

        var directCauseless = BuildAiWarWorld(seed: 20261118, addCause: false, needsCause: true);
        directCauseless.Us.IsHuman = true;
        var directWar = CampaignDiplomacy.DeclareWar(directCauseless.C, directCauseless.Us.Id, directCauseless.Them.Id);
        Check(directWar is not null && string.IsNullOrEmpty(directWar.Justification) &&
              directCauseless.Us.Notoriety > justified.Us.Notoriety,
            "causeless direct DeclareWar does not misread declared_war_on_us as its own CasusBelli");
    }

    private static void TestP2PlayerPeaceOfferSign()
    {
        var language = Loc.Language;
        try
        {
            Loc.Language = "english";
            var minDays = PeaceTermCatalog.Of(PeaceTerms.StatusQuo).MinimumDays;
            var minAcceptance = PeaceTermCatalog.Of(PeaceTerms.StatusQuo).MinimumAcceptance;

            // A: Hard-blocked offers (before MinimumDays) cannot be staged on the player.
            if (minDays > 0)
            {
                var tooEarly = BuildPeaceWorld(warAgeDays: Math.Max(0, minDays - 10));
                var stagedEarly = PeaceOffers.Put(tooEarly.C, tooEarly.War, tooEarly.Beaten, tooEarly.Player, PeaceTerms.StatusQuo);
                Check(!stagedEarly && tooEarly.War.PendingOfferFromId < 0,
                    "PeaceOffers.Put rejects hard-blocked terms before MinimumDays even when recipient is human");
            }

            // B: Put succeeds even when AI acceptance formula rejects StatusQuo for the winning responder.
            var age = Math.Max(400, minDays + 30);
            var world = BuildPeaceWorld(warAgeDays: age);
            var acceptance = PeaceNegotiation.Acceptance(world.C, world.War, world.Beaten.Id, PeaceTerms.StatusQuo);
            Check(acceptance < minAcceptance,
                $"reproduces П2 failure mode: winning human acceptance for StatusQuo is {acceptance:0.0} < {minAcceptance:0.0}");

            // AI-to-AI / direct PeaceNegotiation.Resolve still respects acceptance gates.
            var aiResolveCheck = BuildPeaceWorld(warAgeDays: age);
            PeaceNegotiation.Resolve(aiResolveCheck.C, aiResolveCheck.War, aiResolveCheck.Beaten.Id, PeaceTerms.StatusQuo);
            Check(!aiResolveCheck.War.Ended,
                "PeaceNegotiation.Resolve public path preserves AI acceptance gating");

            var beforeOfferFp = CampaignFingerprint.Of(world.C);
            var delivered = PeaceOffers.Put(world.C, world.War, world.Beaten, world.Player, PeaceTerms.StatusQuo);
            Check(delivered && world.War.PendingOfferFromId == world.Beaten.Id &&
                  !world.War.PendingOfferIsWritten && world.War.PendingOfferTerms == PeaceTerms.StatusQuo,
                "PeaceOffers.Put stages AI peace offer to human regardless of low responder AI acceptance");

            var offeredFp = CampaignFingerprint.Of(world.C);
            Check(offeredFp != beforeOfferFp,
                "CampaignFingerprint changes when a pending peace offer is put on the table");

            var bytes = SaveSystem.ExportNetworkSnapshot(world.C, "parity-wave1");
            var imported = SaveSystem.ImportNetworkSnapshot(bytes, "parity-wave1");
            Check(imported.Campaign is not null, "network snapshot with pending peace offer loads: " + imported.Warning);
            imported.Campaign!.AttachProvinceTopology(world.C.ProvinceMap.CloneForOverrides());
            Check(imported.Campaign.Wars[0].PendingOfferFromId == world.Beaten.Id &&
                  imported.Campaign.Wars[0].PendingOfferTerms == PeaceTerms.StatusQuo &&
                  CampaignFingerprint.Of(imported.Campaign) == offeredFp,
                "snapshot roundtrip preserves pending peace offer and CampaignFingerprint");

            // C: AnswerPeaceOfferCommand encodes through NetworkCommandCodec and signs the peace offer.
            var cmd = new AnswerPeaceOfferCommand { IssuerId = world.Player.Id, WarId = world.War.Id, Accept = true };
            ICampaignCommand decoded = null;
            Check(NetworkCommandCodec.TryEncode(cmd, out var typeName, out var json) &&
                  NetworkCommandCodec.TryDecode(typeName, json, out decoded) &&
                  decoded is AnswerPeaceOfferCommand,
                "AnswerPeaceOfferCommand roundtrips cleanly through NetworkCommandCodec");

            world.C.Commands.Enqueue(decoded!);
            world.C.Commands.ApplyAll(world.C);
            Check(world.War.Ended && world.War.PendingOfferFromId < 0 &&
                  world.War.LastNegotiation == "Agreement reached",
                "signing pending AI offer ends the war, clears pending offer, and records Agreement reached");

            var twin = BuildPeaceWorld(warAgeDays: age);
            Check(PeaceOffers.Put(twin.C, twin.War, twin.Beaten, twin.Player, PeaceTerms.StatusQuo),
                "twin fixture stages identical pending peace offer");
            twin.C.Commands.Enqueue(new AnswerPeaceOfferCommand { IssuerId = twin.Player.Id, WarId = twin.War.Id, Accept = true });
            twin.C.Commands.ApplyAll(twin.C);
            Check(CampaignFingerprint.Of(world.C) == CampaignFingerprint.Of(twin.C),
                "signing a pending peace offer produces deterministic CampaignFingerprint across twin runs");

            // D: Refusing a pending peace offer preserves the war, records refusal day, and sets localized feedback.
            var refusedEn = BuildPeaceWorld(warAgeDays: age);
            Check(PeaceOffers.Put(refusedEn.C, refusedEn.War, refusedEn.Beaten, refusedEn.Player, PeaceTerms.StatusQuo),
                "refusal fixture stages pending offer");
            refusedEn.C.Commands.Enqueue(new AnswerPeaceOfferCommand { IssuerId = refusedEn.Player.Id, WarId = refusedEn.War.Id, Accept = false });
            refusedEn.C.Commands.ApplyAll(refusedEn.C);
            Check(!refusedEn.War.Ended && refusedEn.War.PendingOfferFromId < 0 &&
                  refusedEn.War.RefusedOfferDay == refusedEn.C.ElapsedDays &&
                  refusedEn.War.LastNegotiation == "The offer was refused",
                "refusing pending peace offer keeps war active, records refusal, and sets LastNegotiation");

            // E: Hard-blocked pending offer (e.g. loaded from earlier save) refuses signature without silently clearing, in EN & RU.
            if (minDays > 0)
            {
                var blockedEn = BuildPeaceWorld(warAgeDays: Math.Max(0, minDays - 10));
                StageRawPendingOffer(blockedEn);
                blockedEn.C.Commands.Enqueue(new AnswerPeaceOfferCommand { IssuerId = blockedEn.Player.Id, WarId = blockedEn.War.Id, Accept = true });
                blockedEn.C.Commands.ApplyAll(blockedEn.C);
                Check(!blockedEn.War.Ended && blockedEn.War.PendingOfferFromId == blockedEn.Beaten.Id &&
                      !string.IsNullOrEmpty(blockedEn.War.LastNegotiation),
                    "hard-blocked pending offer cannot be signed and preserves pending state + EN reason");

                Loc.Language = "russian";
                var blockedRu = BuildPeaceWorld(warAgeDays: Math.Max(0, minDays - 10));
                StageRawPendingOffer(blockedRu);
                blockedRu.C.Commands.Enqueue(new AnswerPeaceOfferCommand { IssuerId = blockedRu.Player.Id, WarId = blockedRu.War.Id, Accept = true });
                blockedRu.C.Commands.ApplyAll(blockedRu.C);
                Check(!blockedRu.War.Ended && blockedRu.War.PendingOfferFromId == blockedRu.Beaten.Id &&
                      !string.IsNullOrEmpty(blockedRu.War.LastNegotiation) &&
                      blockedRu.War.LastNegotiation != blockedEn.War.LastNegotiation,
                    $"hard-blocked pending offer preserves pending state with RU reason ('{blockedRu.War.LastNegotiation}')");
                Check(Loc.Get("The offer could not be signed.") == "Предложение не удалось подписать.",
                    "Russian fallback feedback for failed signature is translated");
                Check(Loc.Get("The offer was refused. The war continues.") != "The offer was refused. The war continues.",
                    "Russian refusal feedback is translated");
            }
        }
        finally
        {
            Loc.Language = language;
        }
    }

    private sealed record AiWarFixture(Campaign C, Nation Us, Nation Them);

    private static AiWarFixture BuildAiWarWorld(int seed, bool addCause, bool needsCause)
    {
        var c = new Campaign(seed, new Defines
        {
            AiAnnexesWeakNeighbours = false,
            AiNeedsCauseForWar = needsCause,
            AiUltimatumsBeforeWar = false,
            AiWarPreparationDays = 0,
            AiMinimumWarDays = 0,
            AiWarDesireThreshold = 0.05f,
            AiWarChance = 100f
        });
        var us = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "POL", Name = "Attacker", Population = 2_000_000,
            Treasury = 100, Gdp = 80, Stability = .9f, WarSupport = .9f, CapitalStateMapIndex = 1,
            Government = "presidential_republic", Ideology = "nationalism"
        };
        var them = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BLR", Name = "Defender", Population = 1_000_000,
            Treasury = 20, Gdp = 20f, Stability = .7f, WarSupport = .6f, CapitalStateMapIndex = 9
        };
        var third = new Nation
        {
            Id = 2, MapIndex = 3, Tag = "LTU", Name = "Bystander",
            Population = 1_000_000, Treasury = 20, Gdp = 20f, Stability = .7f
        };
        c.Nations.AddRange(new[] { us, them, third });

        const int width = 8, height = 3;
        var regions = new ushort[width * height];
        var owners = new ushort[regions.Length];
        regions[9] = 1; owners[9] = 1;
        regions[10] = 2; owners[10] = 2;
        regions[11] = 9; owners[11] = 2;
        regions[14] = 4; owners[14] = 3;
        var map = ProvinceTopology.Build(regions, owners, width, height, _ => false);
        c.AttachProvinceTopology(map);
        CampaignDiplomacy.ForgetBorders();

        c.States.AddRange(new[]
        {
            new ScenarioState { Id = "home", MapIndex = 1, NationMapIndex = 1, NationId = "POL", Population = 2_000_000 },
            new ScenarioState { Id = "border", MapIndex = 2, NationMapIndex = 2, NationId = "BLR", Population = 400_000 },
            new ScenarioState { Id = "capital", MapIndex = 9, NationMapIndex = 2, NationId = "BLR", Population = 600_000 },
            new ScenarioState { Id = "other", MapIndex = 4, NationMapIndex = 3, NationId = "LTU", Population = 1_000_000 }
        });

        var source = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == 1);
        us.CapitalProvinceId = source;
        them.CapitalProvinceId = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == 9);
        c.Armies.Add(new Army
        {
            Id = 10, NationId = 0, ProvinceId = source, Divisions = 12, Personnel = 120_000,
            MaxPersonnel = 120_000, Strength = 1, Organisation = 1, Equipment = 1, SupplyLevel = 1
        });

        var relation = CampaignDiplomacy.Get(c, us.Id, them.Id);
        relation.Opinion = -100;
        if (addCause) OpinionSystem.Remember(c, relation, "broke_non_aggression_pact");
        SovereignFinance.NormalizeAccounts(c);
        return new(c, us, them);
    }

    private static void ThinkWar(Campaign campaign, Nation nation)
    {
        DailyTables.Invoke(null, new object[] { campaign });
        Warmaking.Invoke(null, new object[] { campaign, nation });
    }

    private sealed record PeaceFixture(Campaign C, Nation Player, Nation Beaten, StrategicWar War);

    private static PeaceFixture BuildPeaceWorld(int warAgeDays)
    {
        var c = new Campaign(20261118) { ScenarioId = "parity_peace_fixture" };
        var player = new Nation
        {
            Tag = "RUS", MapIndex = 501, Population = 3_000_000, Gdp = 100,
            Treasury = 100, OutputScale = 1, Stability = .8f, WarSupport = 1f, IsHuman = true
        };
        var beaten = new Nation
        {
            Tag = "UKR", MapIndex = 502, Population = 2_000_000, Gdp = 60,
            Treasury = 60, OutputScale = 1, Stability = .5f, WarSupport = .3f, IsHuman = false
        };
        c.AddNation(player);
        c.AddNation(beaten);
        c.ConfigureSinglePlayer(player.Id);

        for (var i = 0; i < 5; i++)
            c.States.Add(new ScenarioState
            {
                Id = "peace_fixture_" + i, MapIndex = 1001 + i, Name = "Peace fixture " + i,
                NationId = i < 3 ? player.Tag : beaten.Tag,
                NationMapIndex = i < 3 ? 501 : 502, Population = 1_000_000, Stability = .8f
            });

        var cells = Enumerable.Range(0, 80).Select(i => (ushort)(1001 + i % 20 / 4)).ToArray();
        var owners = Enumerable.Range(0, 80).Select(i => (ushort)(i % 20 < 12 ? 501 : 502)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 20, 4, _ => false));
        player.CapitalProvinceId = 1;
        beaten.CapitalProvinceId = 5;

        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)warAgeDays * 24);
        var war = new StrategicWar
        {
            Id = 0, AttackerId = player.Id, DefenderId = beaten.Id, StartedDay = 0,
            ClaimedStateMapIndex = 1004, WarScore = 95f, FrontDirty = false, LastFrontDay = warAgeDays
        };
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, player.Id, beaten.Id).Stance = DiplomaticStance.AtWar;
        SovereignFinance.NormalizeAccounts(c);
        return new(c, player, beaten, war);
    }

    private static void StageRawPendingOffer(PeaceFixture world)
    {
        world.War.PendingOfferFromId = world.Beaten.Id;
        world.War.PendingOfferIsWritten = false;
        world.War.PendingOfferTerms = PeaceTerms.StatusQuo;
        world.War.PendingOfferDay = world.C.ElapsedDays;
        world.War.PendingOfferExpiresDay = world.C.ElapsedDays + 30;
    }
}
