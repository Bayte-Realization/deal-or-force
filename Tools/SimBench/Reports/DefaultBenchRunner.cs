using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;
using static DealOrForce.Tools.BenchChecks;

namespace DealOrForce.Tools;

internal static class DefaultBenchRunner
{
    public static int Run(SimBenchContext ctx)
    {
        var campaign = ctx.Campaign;
        var days = ctx.Days;
        var mapped = ctx.Mapped;
        var bookmarkTags = ctx.BookmarkTags;
        var authoredStatistics = ctx.AuthoredStatistics;

        var frontBefore = new FrontSnapshot(campaign);

        var failures = 0;
        failures += Check("day-zero GDP reproduces the authored 2024 figures", CalibrationHolds(campaign));
        failures += Check("every nation has an output calibration", NationsCalibrated(campaign));
        failures += Check("state ownership index agrees with a linear scan", IndexAgrees(campaign));
        failures += Check("every province belongs to the country whose ground it is on",
            ProvincesSitWhereTheirOwnerIs(campaign));
        failures += Check("every nation starts the scenario owning ground", EveryNationHasTerritory(campaign));
        if (mapped)
            failures += Check("every authored region correction exists on the map",
                AuthoredRegionsExist(campaign));
        if (mapped)
            failures += Check("a lake is a place armies cannot walk on",
                LakesArePlacesArmiesCannotWalkOn(campaign));
        if (mapped)
            failures += Check("the compiled map matches the code that drew it",
                TheCompiledMapMatchesTheCodeThatDrewIt());
        failures += Check("every region knows who lives in it",
            EveryRegionKnowsWhoLivesInIt(campaign));
        failures += Check("every region of 2024 is written down, once",
            EveryRegionIsWrittenDown(campaign));
        failures += Check("a content pack is locked until it is owned",
            ContentPacksAreLockedUntilOwned());
        failures += Check("every order a player can give crosses the wire",
            EveryOrderCrossesTheWire());
        failures += Check("the same seed gives the same world twice",
            TheSameSeedGivesTheSameWorld(Math.Min(days, 90)));
        failures += Check("two worlds in one process do not read each other's notes",
            TwoWorldsDoNotShareNotes(Math.Min(days, 45)));
        failures += Check("a peer stepping differently is caught, and stepping alike is not",
            PeersMustStepTheSameWay());
        failures += Check("a client sent the world is in the world it was sent",
            ASnapshotCarriesTheWholeWorld());
        failures += Check("a country that states a figure about itself gets that figure",
            authoredStatistics);
        failures += Check("the literacy history writes is the literacy a country starts with",
            StatedLiteracyStands(campaign));
        failures += Check("a battle leaves ruins, and ruins are rebuilt",
            BattlesLeaveRuins());
        failures += Check("a country that has taken all of another has won the war",
            AWonWarScoresAsWon());
        failures += Check("a movement without land lives, moves, rises and can lose its land again",
            MovementsLiveWithoutLand());
        failures += Check("a fallen country goes into exile, and a movement can come to an end",
            AFallenCountryGoesIntoExile());
        failures += Check("the world deals with a movement: lists it, funds it and throws it out",
            TheWorldDealsWithMovements());
        failures += Check("a peace is a border: joined to the winner, with no pocket of the loser cut off",
            APeaceIsABorder());
        failures += Check("no war goes on for ever",
            NoWarGoesOnForEver());
        failures += Check("what the enemy stands on makes nothing for its owner",
            OccupationCostsOutput());
        failures += Check("a country short of a good builds what makes it",
            ShortagesAreBuiltFor());
        failures += Check("the classes and the sectors count the same people",
            TwoAxesOverOnePopulation(campaign));
        failures += Check("a country can answer to another one, and stop",
            ADependencyIsABargainAboutPower());
        failures += Check("a war can end in obedience rather than annexation",
            AWarCanEndInObedienceRatherThanAnnexation());
        failures += Check("answering to somebody has obligations both ways, and an end",
            AnsweringToSomebodyMeansSomething());
        failures += Check("an alliance is asked for, answered and left",
            AnAllianceIsAskedForAndAnswered());
        failures += Check("the war the scenario is about opens with both armies on its front",
            TheFrontIsMannedOnTheFirstMorning());
        failures += Check("an army answers to the situation, not to the day it started",
            AnArmyAnswersToTheSituation());
        failures += Check("a frightened country asks somebody larger to stand behind it",
            BeingAfraidIsNotAPolicy());
        failures += Check("an order the player gave is carried out",
            AnOrderIsCarriedOut());
        failures += Check("every word an event puts on the screen is in both tables",
            AnEventSpeaksTheLanguageTheGameIsIn());
        failures += Check("a wharf is built on water", AWharfIsBuiltOnWater(campaign));
        failures += Check("an alliance fights one war, not thirty", AnAllianceFightsOneWar());
        failures += Check("what is built can be pulled down again", AWorldThatCanBeUnbuilt());
        failures += Check("no economy falls off a cliff in a day", NoEconomyFallsOffACliff());
        failures += Check("a country's books are never blank", TheBooksAreNeverBlank());
        failures += Check("the computer walks into empty ground", TheComputerWalksIntoEmptyGround());
        failures += Check("a decade of wars does not shred the map", ADecadeOfWarsDoesNotShredTheMap());
        failures += Check("a proclaimed state flies its own flag", AProclaimedStateFliesItsOwnFlag(campaign));
        failures += Check("every country opens under its own flag", EveryCountryOpensUnderItsOwnFlag());
        failures += Check("a save row carries every field", ASaveRowCarriesEveryField());
        failures += Check("every folder of data is read by something", EveryFolderOfDataIsRead());
        failures += Check("the interface composes its prose through the table",
            TheInterfaceComposesThroughTheTable());
        failures += Check("prices answer to the budget and to the rate",
            PricesAnswerToTheBudgetAndTheRate());
        failures += Check("the printing press prints real money and real prices",
            ThePressPrintsRealMoneyAndRealPrices());
        failures += Check("a delegated portfolio is governed by the civil service",
            ADelegatedPortfolioIsGoverned());

        Console.WriteLine();
        Console.WriteLine($"Running {days} campaign day(s)…");

        campaign.IsPaused = false;
        var ukraineTrace = new List<int>();
        var ukraineIndex = campaign.NationByTag("UKR")?.MapIndex ?? -1;
        var traceFront = Environment.GetEnvironmentVariable("DOF_TRACE_FRONT") == "1";
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var collectionsBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var run = Stopwatch.StartNew();
        for (var day = 0; day < days; day++)
        {
            campaign.TickDay();
            if (day % 15 == 0 && ukraineIndex >= 0 && campaign.ProvinceController is not null)
                ukraineTrace.Add(HeldBy(campaign, ukraineIndex));
            if (traceFront && day % 30 == 0) TraceFront(campaign, day);
        }
        run.Stop();

        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        var collections = (GC.CollectionCount(0) - collectionsBefore.Item1,
            GC.CollectionCount(1) - collectionsBefore.Item2,
            GC.CollectionCount(2) - collectionsBefore.Item3);
        var perDay = run.Elapsed.TotalMilliseconds / days;
        Console.WriteLine($"  {run.ElapsedMilliseconds} ms total, {perDay:0.###} ms per campaign day.");
        Console.WriteLine($"  {allocated / 1_048_576.0:0.#} MB allocated, " +
                          $"{allocated / days / 1024.0:0.#} kB per day, " +
                          $"{collections.Item1:N0}/{collections.Item2:N0}/{collections.Item3:N0} gen0/1/2 collections, " +
                          $"{GC.GetTotalMemory(true) / 1_048_576.0:0.#} MB live, " +
                          $"{Environment.WorkingSet / 1_048_576.0:0.#} MB resident.");
        Console.WriteLine($"  {campaign.Date} after {campaign.ElapsedDays} day(s).");

        Console.WriteLine();
        Console.WriteLine("Simulation passes, by the garbage they make (average per run):");
        foreach (var sample in SimProfiler.All
                     .OrderByDescending(entry => entry.AllocatedBytes)
                     .ThenByDescending(entry => entry.AverageMilliseconds))
            Console.WriteLine($"  {sample.Name,-16} {sample.AverageMilliseconds,8:0.###} ms  " +
                              $"{sample.AllocatedBytes / 1_048_576.0,8:0.#} MB  " +
                              $"{sample.AllocatedBytes / (double)Math.Max(1, sample.Runs) / 1024.0,8:0.#} kB/run  " +
                              $"×{sample.Runs}");
        Console.WriteLine();

        Console.WriteLine();
        failures += Check("a campaign day does not bury the collector",
            ADayDoesNotBuryTheCollector(allocated / (double)days, campaign.TickHours));
        failures += Check("the economy actually moved", EconomyMoved(campaign));
        failures += Check("every dependency in the world is one a country could be in",
            DependenciesInAWorldNobodySteered(campaign));
        failures += Check("scripted content parses and applies", ScriptedContentWorks(campaign));
        failures += Check("modifiers grant, tick and expire", ModifiersWork(campaign));
        failures += Check("no nation ran to a nonsense GDP", GdpSane(campaign));
        failures += Check("the shipped skin matches the compiled defaults", SkinIsCoherent());
        failures += Check("the shipped country definitions describe the world the game starts in",
            ShippedCountriesMatchTheScenario(campaign, bookmarkTags));
        failures += Check("a category folder is read whole and later files win",
            ACategoryFolderIsReadWhole());
        failures += Check("the shipped defines match the compiled defaults", ShippedDefinesMatchTheCode());
        failures += Check("the English table indexes every string the game shows",
            TheEnglishTableIsAnIndex());
        failures += Check("a mod can add a good and a building the game never had",
            AModCanAddAGood(campaign));
        failures += Check("the budget is data, and still the budget it always was",
            TheBudgetIsData(campaign));
        failures += Check("a country is as settled as its regions and its capital say",
            StabilityIsWorkedOutRatherThanStored());
        failures += Check("people take up arms for a country they think is theirs",
            MilitiaRisesWhereItIsWanted());
        failures += Check("a country can lose a part of itself, and it is a country afterwards",
            RegionsThatAreNotTheirsCanLeave());
        failures += Check("a mod can add a cabinet and a trade stance the game never had",
            PolicyCataloguesTakeNewEntries());
        failures += Check("every country is governed by something, and minds what its neighbours are",
            ThePoliticalWorldHangsTogether(campaign));
        failures += Check("every good is made by something and eaten by something",
            TheChainsHangTogether());
        failures += Check("every mod template names something the game actually has",
            TheTemplatesStillNameRealThings());
        failures += Check("every picture the interface asks for has a name a mod can rebind",
            ArtworkIsNamedRatherThanSpelledOut());
        failures += Check("every sound the game makes is declared where a mod can reach it",
            AudioIsDeclaredRatherThanWiredIn());
        failures += Check("a country flies a flag that answers to what it has become",
            FlagsFollowWhatACountryIs());
        failures += Check("every country knows where its capital is",
            EveryCountryHasACapital(campaign));
        failures += Check("an army sent somewhere gets there more often than it is sent elsewhere",
            OrdersHoldLongEnoughToBeObeyed());
        failures += Check("a country that is failing stops being what it was",
            GovernmentsChangeWithoutBeingTold());
        failures += Check("a second campaign in one session starts in the authored world",
            ASecondCampaignGetsTheMapToo());
        failures += Check("every state a country can proclaim itself is one it could reach",
            FormableStatesNameRealThings(campaign));
        if (mapped)
            failures += Check("the political map is data a mod can override", ThePoliticalMapIsData(campaign));
        failures += Check("every explained number adds up to what the screen shows",
            BreakdownsAddUp(campaign));
        failures += Check("headline figures format the way a player reads them", FiguresReadWell());
        failures += Check("every translation says what it is and passes what the English passes", TranslationsAreSound());

        if (mapped)
            failures += Check("the Russo-Ukrainian front is still a stalemate",
                FrontIsAStalemate(campaign, frontBefore, ukraineTrace, days));

        failures += Check("the world researches, and a technology is a thing somebody can finish",
            TheWorldResearches(campaign, days));
        failures += Check("a war costs formations and an army replaces them",
            WarCostsFormations(campaign, days));
        failures += Check("the world governs itself", TheWorldGovernsItself(campaign, days));
        failures += Check("governments take decisions written in data",
            GovernmentsTakeScriptedDecisions(campaign, days));
        failures += Check("a system that thinks in days runs once a day at any step",
            EverySystemRunsOnItsOwnCadence());
        failures += Check("an army at war holds a line rather than a province",
            ArmiesHoldLinesRatherThanProvinces(campaign));
        failures += Check("a country is named by its tag, not by an English sentence",
            CountriesAreNamedByTag(campaign));
        failures += Check("a country whose name is an argument answers to the option that settles it",
            ANamingOptionRenamesTheCountry(campaign));

        if (mapped)
        {
            Console.WriteLine();
            Console.WriteLine("Second campaign for the checks that redraw the map…");
            var sandbox = Campaign.CreateBootstrap(20260830);
            if (AttachMap(sandbox))
            {
                sandbox.IsPaused = false;
                for (var day = 0; day < 220; day++) sandbox.TickDay();
                failures += Check("the length of a step is a pace, not a difficulty",
                    TheClockRunsAtAnyStep());
                failures += Check("no region belongs to two countries at once",
                    EveryRegionHasOneOwner(sandbox));
                failures += Check("a region cut by a new border becomes two regions",
                    RegionsSplitAlongNewBorders(sandbox));
                failures += Check("a region put back together again is one region",
                    RegionsHealWhenTheBorderGoesAway(sandbox));
                failures += Check("a redrawn border survives a save", BordersSurviveASave(sandbox));
                failures += Check("a save's history comes back week for week", AHistoryComesBackWhole(sandbox));
                failures += Check("the world's count of who holds what is each country's own count",
                    TheWorldTallyIsEachCountrysTally(sandbox));
                failures += Check("a consolidation invents nobody and the railways are paid for",
                    ManoeuvresCostWhatTheySave(sandbox));
                failures += Check("a drawn line is held where it is pressed",
                    ADrawnLineIsHeldWhereItIsPressed(sandbox));
                failures += Check("an attack waiting to go stands along its front, not in one province",
                    AnAttackFormsUpAlongItsFront());
                failures += Check("a battle plan survives a save",
                    ABattlePlanSurvivesASave(sandbox));
                failures += Check("a navy is worth having",
                    ByTheDay(sandbox, () => ANavyIsWorthHaving(sandbox)));
                failures += Check("an army can be put ashore across water",
                    ByTheDay(sandbox, () => AnArmyCanBePutAshore(sandbox)));
                failures += Check("an AI mounts a landing of its own",
                    ByTheDay(sandbox, () => AnAiMountsALanding(sandbox)));
                failures += Check("a convoy caught at sea pays for it",
                    ByTheDay(sandbox, () => AConvoyCaughtAtSeaPaysForIt(sandbox)));
                failures += Check("troops trained for a landing land better",
                    MarinesLandBetter(sandbox));
                failures += Check("a fleet sails round a continent, not across it",
                    AFleetSailsRoundTheLand(sandbox));
                failures += Check("a port that falls sends its fleet to one that has not",
                    ByTheDay(sandbox, () => AFallenPortMovesItsFleet(sandbox)));
                failures += Check("a navy rebuilds what it loses, and the yards are paid for",
                    ByTheDay(sandbox, () => ANavyRebuildsWhatItLoses(sandbox)));
                failures += Check("a drawn line moves the formations put on it",
                    ADrawnLineCommandsTheFormationsOnIt(sandbox));
                failures += Check("a mod can rewrite a country's army, navy and typeface",
                    TheOrderOfBattleIsAModsToRewrite(sandbox));
                failures += Check("every region of the world is a line a mod can edit",
                    TheRegionsOfTheWorldAreData());
                failures += Check("a country remembers why it dislikes another",
                    OpinionIsRememberedAndForgotten(sandbox));
                failures += Check("the peace an AI writes hangs together",
                    ThePeaceAnAiWritesHangsTogether(sandbox));
                failures += Check("a country counts the cost before it declares",
                    ACountryCountsTheCost());
                failures += Check("a promise is answered and a conquest is counted",
                    APromiseIsAnsweredAndAConquestIsCounted());
                failures += Check("trade has names on it, and an embargo has teeth",
                    TradeHasNamesOnIt(sandbox));
                failures += Check("what a country says about its own trade is what its traders do",
                    TradeAnswersToItsGovernment(sandbox));
                failures += Check("the world makes what the world needs",
                    TheWorldMakesWhatItNeeds(sandbox));
                failures += Check("a region holds the people who live in it",
                    RegionsHoldTheirPeople());
                failures += Check("a peace can be written, priced, refused and signed",
                    APeaceCanBeWritten(sandbox));
                failures += Check("a moved border leaves a national minority behind",
                    ConquestLeavesMinorities(sandbox));
                failures += Check("an AI puts terms to a player instead of imposing them",
                    AnAiPutsTermsToAPlayer(sandbox));
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "All checks passed."
            : $"{failures} check(s) FAILED.");
        return failures == 0 ? 0 : 1;
    }
}
