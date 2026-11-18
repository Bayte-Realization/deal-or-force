using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using DealOrForce.Simulation;
using static DealOrForce.Tools.BenchChecks;

namespace DealOrForce.Tools;

internal static class CheckFlagReports
{
    [SimBenchReport("--release-content-only", SimBenchPhase.PreCampaign, Order = 54,
        Description = "Recheck content and refund regressions without full geography preflight")]
    public static int RunReleaseContentOnly(SimBenchContext ctx)
    {
        var contentFailures = Check("every word an event puts on the screen is in both tables",
            AnEventSpeaksTheLanguageTheGameIsIn());
        contentFailures += Check("what is built can be pulled down again", AWorldThatCanBeUnbuilt());
        return contentFailures == 0 ? 0 : 1;
    }

    [SimBenchReport("--borders", SimBenchPhase.BeforeMap, Order = 192,
        Description = "Check a decade of wars does not shred the map")]
    public static int RunBorders(SimBenchContext ctx) =>
        Check("a decade of wars does not shred the map", ADecadeOfWarsDoesNotShredTheMap());

    [SimBenchReport("--flags", SimBenchPhase.BeforeMap, Order = 196,
        Description = "Verify every country opens under its own flag")]
    public static int RunFlags(SimBenchContext ctx) =>
        Check("every country opens under its own flag", EveryCountryOpensUnderItsOwnFlag());

    [SimBenchReport("--movements-only", SimBenchPhase.BeforeMap, Order = 199,
        Description = "Exercise the movement province-map lifecycle")]
    public static int RunMovementsOnly(SimBenchContext ctx) =>
        Check("a movement without land lives, moves, rises and can lose its land again",
            MovementsLiveWithoutLand());

    [SimBenchReport("--alliance-check", SimBenchPhase.BeforeMap, Order = 204,
        Description = "Check an alliance is asked for and answered")]
    public static int RunAllianceCheck(SimBenchContext ctx) =>
        Check("an alliance is asked for and answered", AnAllianceIsAskedForAndAnswered());

    [SimBenchReport("--skin", SimBenchPhase.BeforeMap, Order = 207,
        Description = "Check shipped skin layout and colours against defaults")]
    public static int RunSkin(SimBenchContext ctx) =>
        Check("the shipped skin matches the compiled defaults", SkinIsCoherent());

    [SimBenchReport("--rows", SimBenchPhase.BeforeMap, Order = 209,
        Description = "Verify every save row carries every field")]
    public static int RunRows(SimBenchContext ctx) =>
        Check("a save row carries every field", ASaveRowCarriesEveryField());

    [SimBenchReport("--crash-replay-check", SimBenchPhase.BeforeMap, Order = 213,
        Description = "Replay a crash envelope tape into the same world")]
    public static int RunCrashReplayCheck(SimBenchContext ctx) =>
        Check("a crash envelope replays into the same world", CrashEnvelopeReplays(ctx.Seed));

    [SimBenchReport("--loc", SimBenchPhase.BeforeMap, Order = 216,
        Description = "Check translations for validity and matching format arguments")]
    public static int RunLoc(SimBenchContext ctx) =>
        Check("every translation says what it is and passes what the English passes", TranslationsAreSound());

    [SimBenchReport("--localization-audit", SimBenchPhase.BeforeMap, Order = 219,
        Description = "Report visible UI captions missing from EN or RU tables")]
    public static int RunLocalizationAudit(SimBenchContext ctx)
    {
        var scripts = FindScripts();
        if (scripts is null) return 2;
        var captions = VisibleCaptionIndex(scripts);
        var english = ReadLocalizationTable("english");
        var russian = ReadLocalizationTable("russian");
        var absentEnglish = captions.Where(k => !english.ContainsKey(k)).ToArray();
        var absentRussian = captions.Where(k => !russian.ContainsKey(k)).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { absentEnglish, absentRussian },
            new JsonSerializerOptions { WriteIndented = true }));
        return absentEnglish.Length == 0 && absentRussian.Length == 0 ? 0 : 1;
    }

    [SimBenchReport("--write-localization-index", SimBenchPhase.BeforeMap, Order = 231,
        Description = "Write missing English localization captions to JSON file")]
    public static int? RunWriteLocalizationIndex(SimBenchContext ctx)
    {
        var indexAt = ctx.IndexOf("--write-localization-index");
        if (indexAt < 0 || indexAt + 1 >= ctx.Args.Length) return null;
        var scripts = FindScripts();
        if (scripts is null) return 2;
        var english = ReadLocalizationTable("english");
        var missing = VisibleCaptionIndex(scripts).Where(k => !english.ContainsKey(k)).ToDictionary(k => k, k => k);
        File.WriteAllText(ctx.Args[indexAt + 1], JsonSerializer.Serialize(missing, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n");
        Console.WriteLine($"Indexed {missing.Count} new captions without replacing existing translations.");
        return 0;
    }

    [SimBenchReport("--prepare-front-only", SimBenchPhase.BeforeMap, Order = 243,
        Description = "Check an attack forms up along its assigned front")]
    public static int RunPrepareFrontOnly(SimBenchContext ctx) =>
        Check("an attack forms up along its actual assigned front", AnAttackFormsUpAlongItsFront());

    [SimBenchReport("--templates-only", SimBenchPhase.BeforeMap, Order = 245,
        Description = "Verify mod templates still reference real entities")]
    public static int RunTemplatesOnly(SimBenchContext ctx) =>
        Check("mod templates still name real things", TheTemplatesStillNameRealThings());

    [SimBenchReport("--integrity-only", SimBenchPhase.BeforeMap, Order = 247,
        Description = "Run integrity scanner, skin, defines, English index, and translation checks")]
    public static int RunIntegrityOnly(SimBenchContext ctx) =>
        Check("integrity checks distinguish captions and compare dictionary values", IntegrityScannerSelfTest()) +
        Check("the shipped skin matches the compiled defaults", SkinIsCoherent()) +
        Check("effective shipped defines match the compiled defaults", ShippedDefinesMatchTheCode()) +
        Check("the English table indexes visible captions", TheEnglishTableIsAnIndex()) +
        Check("translations are sound", TranslationsAreSound());

    [SimBenchReport("--stability-only", SimBenchPhase.BeforeMap, Order = 253,
        Description = "Verify national stability derivation")]
    public static int RunStabilityOnly(SimBenchContext ctx) =>
        Check("a country is as settled as its regions and its capital say", StabilityIsWorkedOutRatherThanStored());

    [SimBenchReport("--determinism-only", SimBenchPhase.AfterMap, Order = 282,
        Description = "Verify two runs of one seed match bit-for-bit")]
    public static int RunDeterminismOnly(SimBenchContext ctx)
    {
        var days = Math.Clamp(ctx.IntArg("--determinism-only", 90), 1, 365);
        return Check("the same seed gives the same world twice", TheSameSeedGivesTheSameWorld(days));
    }

    [SimBenchReport("--isolation-only", SimBenchPhase.AfterMap, Order = 287,
        Description = "Verify two campaigns in one process share no static state")]
    public static int RunIsolationOnly(SimBenchContext ctx)
    {
        var days = Math.Clamp(ctx.IntArg("--isolation-only", 45), 1, 400);
        return Check("two worlds in one process do not read each other's notes", TwoWorldsDoNotShareNotes(days));
    }

    [SimBenchReport("--authored-world", SimBenchPhase.AfterMap, Order = 309,
        Description = "Check authored statistics, province ownership, territory and base regions")]
    public static int RunAuthoredWorld(SimBenchContext ctx) =>
        Check("authored national statistics", ctx.AuthoredStatistics) +
        Check("province ownership matches ground and explicit corrections", ProvincesSitWhereTheirOwnerIs(ctx.Campaign)) +
        Check("every non-merged nation starts owning ground", EveryNationHasTerritory(ctx.Campaign)) +
        Check("base regions and resource overlays are valid", EveryRegionIsWrittenDown(ctx.Campaign));

    [SimBenchReport("--data-readers-only", SimBenchPhase.PostOpening, Order = 629,
        Description = "Check every data folder is read by simulation")]
    public static int RunDataReadersOnly(SimBenchContext ctx) =>
        Check("every folder of data is read by something", EveryFolderOfDataIsRead());

    [SimBenchReport("--decisions-only", SimBenchPhase.PostOpening, Order = 631,
        Description = "Run days and check governments take scripted decisions")]
    public static int RunDecisionsOnly(SimBenchContext ctx)
    {
        ctx.Campaign.IsPaused = false;
        for (var day = 0; day < ctx.Days; day++) ctx.Campaign.TickDay();
        return Check("governments take decisions written in data", GovernmentsTakeScriptedDecisions(ctx.Campaign, ctx.Days));
    }

    [SimBenchReport("--identity-regressions-only", SimBenchPhase.PostOpening, Order = 646,
        Description = "Check flags, country names by tag, and opinions after bootstrap tick")]
    public static int RunIdentityRegressionsOnly(SimBenchContext ctx)
    {
        ctx.Campaign.IsPaused = false;
        while (ctx.Campaign.ElapsedDays < 2) ctx.Campaign.TickDay();
        var identityFailures = Check("a country flies a flag that answers to what it has become", FlagsFollowWhatACountryIs());
        identityFailures += Check("a country is named by its tag, not by an English sentence", CountriesAreNamedByTag(ctx.Campaign));
        identityFailures += Check("a country remembers why it dislikes another", OpinionIsRememberedAndForgotten(ctx.Campaign));
        return identityFailures == 0 ? 0 : 1;
    }

    [SimBenchReport("--orders", SimBenchPhase.PostOpening, Order = 665,
        Description = "Check army order persistence until obeyed")]
    public static int RunOrders(SimBenchContext ctx) =>
        Check("an army sent somewhere gets there more often than it is sent elsewhere", OrdersHoldLongEnoughToBeObeyed());

    [SimBenchReport("--militia", SimBenchPhase.PostOpening, Order = 668,
        Description = "Check militia formation rules")]
    public static int RunMilitia(SimBenchContext ctx) =>
        Check("people take up arms for a country they think is theirs", MilitiaRisesWhereItIsWanted());

    [SimBenchReport("--political-map", SimBenchPhase.PostOpening, Order = 672,
        Description = "Check political map data overrides after N ticks")]
    public static int RunPoliticalMap(SimBenchContext ctx)
    {
        if (!ctx.Mapped) return 2;
        ctx.Campaign.IsPaused = false;
        for (var day = 0; day < ctx.Days; day++) ctx.Campaign.TickDay();
        return Check("the political map is data a mod can override", ThePoliticalMapIsData(ctx.Campaign));
    }

    [SimBenchReport("--world-balance", SimBenchPhase.PostOpening, Order = 680,
        Description = "Check global production vs demand balance after N ticks")]
    public static int RunWorldBalance(SimBenchContext ctx)
    {
        ctx.Campaign.IsPaused = false;
        for (var day = 0; day < ctx.Days; day++) ctx.Campaign.TickDay();
        return Check("the world makes what the world needs", TheWorldMakesWhatItNeeds(ctx.Campaign));
    }

    [SimBenchReport("--secession", SimBenchPhase.PostOpening, Order = 687,
        Description = "Check regional secession mechanics")]
    public static int RunSecession(SimBenchContext ctx) =>
        Check("a country can lose a part of itself, and it is a country afterwards", RegionsThatAreNotTheirsCanLeave());

    [SimBenchReport("--manoeuvres", SimBenchPhase.PostOpening, Order = 691,
        Description = "Check unit consolidation and strategic railway costs")]
    public static int RunManoeuvres(SimBenchContext ctx)
    {
        var sandbox = Campaign.CreateBootstrap(20260830);
        if (!AttachMap(sandbox)) return 1;
        sandbox.IsPaused = false;
        for (var day = 0; day < 220; day++) sandbox.TickDay();
        return Check("a consolidation invents nobody and the railways are paid for", ManoeuvresCostWhatTheySave(sandbox));
    }

    [SimBenchReport("--quick-mechanics", SimBenchPhase.PostOpening, Order = 701,
        Description = "Run fast subset of data, UI, economy, alliance and formatting checks")]
    public static int RunQuickMechanics(SimBenchContext ctx) =>
        Check("a proclaimed state flies its own flag", AProclaimedStateFliesItsOwnFlag(ctx.Campaign)) +
        Check("every country opens under its own flag", EveryCountryOpensUnderItsOwnFlag()) +
        Check("a save row carries every field", ASaveRowCarriesEveryField()) +
        Check("every folder of data is read by something", EveryFolderOfDataIsRead()) +
        Check("the interface composes its prose through the table", TheInterfaceComposesThroughTheTable()) +
        Check("prices answer to the budget and to the rate", PricesAnswerToTheBudgetAndTheRate()) +
        Check("the printing press prints real money and real prices", ThePressPrintsRealMoneyAndRealPrices()) +
        Check("a delegated portfolio is governed by the civil service", ADelegatedPortfolioIsGoverned()) +
        Check("an alliance is asked for, answered and left", AnAllianceIsAskedForAndAnswered()) +
        Check("headline figures format the way a player reads them", FiguresReadWell()) +
        Check("every translation says what it is and passes what the English passes", TranslationsAreSound());

    [SimBenchReport("--snapshot-check", SimBenchPhase.PostOpening, Order = 716,
        Description = "Check network snapshot export/import integrity")]
    public static int RunSnapshotCheck(SimBenchContext ctx) =>
        Check("a client sent the world is in the world it was sent",
            ASnapshotCarriesTheWholeWorld(ctx.Args.Length > 1 && int.TryParse(ctx.Args[0], out _) ? ctx.Seed : 20240808));

    [SimBenchReport("--counts-cost", SimBenchPhase.PostOpening, Order = 750,
        Description = "Check war-declaration cost estimation")]
    public static int RunCountsCost(SimBenchContext ctx) =>
        Check("a country counts the cost before it declares", ACountryCountsTheCost());
}
