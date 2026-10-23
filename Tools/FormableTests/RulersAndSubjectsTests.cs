using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class RulersAndSubjectsTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("rulers/subjects: " + label);
        Console.WriteLine("PASS rulers/subjects: " + label);
    }

    public static void Run()
    {
        Rulers.ResetCatalog();
        SubjectCatalog.ResetCatalog();
        DemographyCatalog.ResetCatalog();
        GovernmentCatalog.ResetCatalog();
        IdeologyCatalog.ResetCatalog();
        Loc.ResetCatalog();
        Check(SubjectCatalog.Knows("special_administrative_region"),
            "special administrative region subject kind is loaded from data");
        Check(SubjectCatalog.Of("dependent_territory")?.ReceivesLandGrants == false &&
              SubjectCatalog.Of("leased_territory")?.ReceivesLandGrants == false,
            "dependent and leased territories cannot receive regional land grants");

        var campaign = Campaign.CreateBootstrap(20261019);
        var usa = campaign.NationByTag("USA");
        var gbr = campaign.NationByTag("GBR");
        var rus = campaign.NationByTag("RUS");
        var yem = campaign.NationByTag("YEM");
        Check(usa is not null && gbr is not null && rus is not null && yem is not null, "bootstrap countries exist");

        // 1. Rulers & dual executive
        var usaHos = Rulers.HeadOfState(campaign, usa);
        Check(usaHos.Name == "Joe Biden" && usaHos.Title == "President" && usaHos.TraitId == "diplomat",
            "authored presidential republic has its head of state and trait");
        Check(!Rulers.HasDualExecutive(usa), "presidential republic without authored PM uses single executive");

        var gbrHos = Rulers.HeadOfState(campaign, gbr);
        var gbrHog = Rulers.HeadOfGovernment(campaign, gbr);
        Check(Rulers.HasDualExecutive(gbr) && gbrHos.Name == "Charles III" && gbrHog.Name == "Rishi Sunak",
            "constitutional monarchy has both Monarch and Prime Minister");

        // Every country in the world has a deterministic ruler
        Check(campaign.Nations.All(n => Rulers.HeadOfState(campaign, n).Name.Length > 0),
            "every country in the world has a head of state");

        // 2. Cabinet appointments, modifiers & promotion
        usa.Treasury = 500f;
        var beforeHash = CampaignFingerprint.Of(campaign);
        var baseTradeReach = NationalModifiers.Of(usa, "trade_reach");
        new AppointMinisterCommand
        {
            IssuerId = usa.Id,
            PortfolioId = "foreign",
            OptionId = "trade_negotiator"
        }.Apply(campaign);
        Check(Rulers.ActiveMinister(usa, "foreign")?.Id == "trade_negotiator", "cabinet minister appointed through command");
        Check(NationalModifiers.Of(usa, "trade_reach") > baseTradeReach, "appointed minister adds national modifier");
        Check(CampaignFingerprint.Of(campaign) != beforeHash, "cabinet change updates CampaignFingerprint");

        // Cooldown blocks immediate second reshuffle
        Check(!Rulers.CanAppointMinister(campaign, usa, "foreign", "coalition_builder", out var cooldownWhy) && cooldownWhy.Length > 0,
            "cabinet cooldown blocks immediate reshuffle");

        // Promoting a minister to national leadership after cooldown
        usa.LastCabinetChangeDay = -9999;
        var ministerPerson = Rulers.MinisterPersonName(usa, "foreign");
        new AppointMinisterCommand
        {
            IssuerId = usa.Id,
            PortfolioId = "foreign",
            PromoteToLeader = true
        }.Apply(campaign);
        Check(Rulers.HeadOfState(campaign, usa).Name == ministerPerson, "promoting cabinet minister makes them national leader");

        // Succession replaces ruler
        var beforeRuler = Rulers.HeadOfState(campaign, rus).Name;
        Rulers.ReplaceHeadOfState(campaign, rus, "ruler_died");
        Check(Rulers.HeadOfState(campaign, rus).Name != beforeRuler, "ruler succession installs a new leader");

        // 3. Arbitrary name & flag cosmetic override
        new SetNationalCosmeticsCommand
        {
            IssuerId = usa.Id,
            TargetNationId = usa.Id,
            CustomName = "United American Commonwealth",
            CustomFlag = "flag_usa_communism"
        }.Apply(campaign);
        Check(usa.DisplayName == "United American Commonwealth", "custom country name overrides DisplayName");
        Check(FlagCatalog.Candidates(campaign, usa).First() == "flag_usa_communism", "custom flag stands first in FlagCatalog candidates");
        new SetNationalCosmeticsCommand { IssuerId = usa.Id, TargetNationId = usa.Id, CustomName = "", CustomFlag = "" }.Apply(campaign);
        Check(usa.DisplayName != "United American Commonwealth", "resetting custom cosmetics restores default name");

        // 4. Subject interactions (soft/hard pacification, autonomy ladder, integration)
        var fra = campaign.NationByTag("FRA");
        var lux = campaign.NationByTag("LUX");
        SubjectSystem.Subjugate(campaign, fra, lux, "puppet", out _);
        fra.Treasury = 100f;
        lux.LibertyDesire = 0.45f;

        new SubjectInteractionCommand
        {
            IssuerId = fra.Id,
            SubjectId = lux.Id,
            Kind = SubjectInteractionKind.GrantSubsidies
        }.Apply(campaign);
        Check(lux.LibertyDesire < 0.35f, "soft pacification (subsidies) lowers subject liberty desire");

        lux.LastSubjectInteractionDay = -9999;
        new SubjectInteractionCommand
        {
            IssuerId = fra.Id,
            SubjectId = lux.Id,
            Kind = SubjectInteractionKind.LoosenAutonomy
        }.Apply(campaign);
        Check(lux.SubjectType == "satellite", "loosening autonomy steps puppet to satellite");

        lux.LastSubjectInteractionDay = -9999;
        new SubjectInteractionCommand
        {
            IssuerId = fra.Id,
            SubjectId = lux.Id,
            Kind = SubjectInteractionKind.TightenAutonomy
        }.Apply(campaign);
        Check(lux.SubjectType == "puppet" && lux.SubjectIntegrationProgress > 0f,
            "tightening autonomy steps satellite back to puppet and advances integration");

        new SubjectInteractionCommand
        {
            IssuerId = fra.Id,
            SubjectId = lux.Id,
            Kind = SubjectInteractionKind.ToggleIntegration
        }.Apply(campaign);
        Check(lux.SubjectIntegrating, "integration toggled on through command");
        SubjectInteractions.TickSubjectWeek(campaign, fra, lux);
        Check(lux.SubjectIntegrationProgress > 0.12f, "weekly subject tick advances active integration");

        // 5. Cultural state naming & stripping administrative suffixes
        Check(SecessionEvents.StripAdministrativeSuffix("Aden Governorate") == "Aden",
            "administrative suffix stripped from governorate/oblast names");
        Check(DemographyCatalog.Culture("scottish")?.Country == "Scotland" &&
              DemographyCatalog.Culture("tatar")?.Country == "Tatarstan",
            "cultural state names catalogue loaded");

        // 6. Dynamic Politician Candidate Pool & RecruitPoliticianCommand
        var candidates = Rulers.CandidatePool(campaign, usa, 4);
        Check(candidates.Count == 4 && candidates.All(c => c.Name.Length > 0 && c.TraitId.Length > 0),
            "CandidatePool generates 4 distinct politician candidates with names and traits");
        usa.LastCabinetChangeDay = -9999;
        usa.Treasury = 500f;
        var chosenCand = candidates[0];
        new RecruitPoliticianCommand
        {
            IssuerId = usa.Id,
            CandidateSlot = chosenCand.SlotIndex,
            AsNationalLeader = false
        }.Apply(campaign);
        Check(Rulers.ActiveMinister(usa, chosenCand.PortfolioId)?.Id == chosenCand.OptionId,
            "RecruitPoliticianCommand appoints generated candidate into cabinet portfolio");

        usa.LastCabinetChangeDay = -9999;
        var leaderCand = Rulers.CandidatePool(campaign, usa, 4)[1];
        new RecruitPoliticianCommand
        {
            IssuerId = usa.Id,
            CandidateSlot = leaderCand.SlotIndex,
            AsNationalLeader = true
        }.Apply(campaign);
        Check(Rulers.HeadOfState(campaign, usa).Name == leaderCand.Name,
            "RecruitPoliticianCommand with AsNationalLeader=true elevates candidate to national leader");

        var poolBefore = Rulers.CandidatePool(campaign, usa, 4).Select(c => c.Name).ToArray();
        new RecruitPoliticianCommand
        {
            IssuerId = usa.Id,
            RefreshPool = true
        }.Apply(campaign);
        var poolAfter = Rulers.CandidatePool(campaign, usa, 4).Select(c => c.Name).ToArray();
        Check(!poolBefore.SequenceEqual(poolAfter),
            "RecruitPoliticianCommand with RefreshPool=true scouts a new slate of candidates");

        // 7. Civil war full annexation (both parent winning and rebel/faction winning + consolidating),
        //    national reunification full annexation, decisive conquest annexation, vassal release & re-annexation,
        //    foreign agitation, and inter-subject conspiracy ("сговор") rebellion.
        RunAnnexationAndVassalConspiracyChecks();
    }

    private static void RunAnnexationAndVassalConspiracyChecks()
    {
        var c = new Campaign(20261019) { ScenarioId = "annexation_vassal_fixture" };
        var gov = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "YEM", Name = "Yemen", Population = 15_000_000,
            Gdp = 25f, Treasury = 10f, CapitalStateMapIndex = 1, Stability = 0.7f, WarSupport = 0.8f,
            Government = "presidential_republic", Ideology = "nationalism"
        };
        var rebel = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "HTH", Name = "Ansar Allah", Population = 12_000_000,
            Gdp = 18f, Treasury = 6f, CapitalStateMapIndex = 3, SecededFromId = gov.Id,
            FactionId = "HTH", Stability = 0.7f, WarSupport = 0.8f,
            Government = "theocracy", Ideology = "theocratic_rule"
        };
        var northK = new Nation
        {
            Id = 2, MapIndex = 3, Tag = "PRK", Name = "North Korea", Population = 26_000_000,
            Gdp = 40f, Treasury = 15f, CapitalStateMapIndex = 4, Stability = 0.8f, WarSupport = 0.85f,
            Government = "one_party_state", Ideology = "communism", DeclaredCulture = "korean"
        };
        var southK = new Nation
        {
            Id = 3, MapIndex = 4, Tag = "KOR", Name = "South Korea", Population = 51_000_000,
            Gdp = 1600f, Treasury = 80f, CapitalStateMapIndex = 5, Stability = 0.3f, WarSupport = 0.1f,
            Government = "presidential_republic", Ideology = "liberal_democracy", DeclaredCulture = "korean"
        };
        var conqueror = new Nation
        {
            Id = 4, MapIndex = 5, Tag = "IRQ", Name = "Iraq", Population = 42_000_000,
            Gdp = 250f, Treasury = 40f, CapitalStateMapIndex = 7, Stability = 0.75f, WarSupport = 0.85f,
            Government = "military_junta", Ideology = "nationalism"
        };
        var target = new Nation
        {
            Id = 5, MapIndex = 6, Tag = "KWT", Name = "Kuwait", Population = 4_200_000,
            Gdp = 160f, Treasury = 20f, CapitalStateMapIndex = 8, Stability = 0.25f, WarSupport = 0.10f,
            Government = "constitutional_monarchy", Ideology = "conservatism"
        };
        var rivalSponsor = new Nation
        {
            Id = 6, MapIndex = 7, Tag = "IRN", Name = "Iran", Population = 88_000_000,
            Gdp = 400f, Treasury = 50f, CapitalStateMapIndex = 10, Stability = 0.70f, WarSupport = 0.75f,
            Government = "theocracy", Ideology = "theocratic_rule"
        };
        c.Nations.AddRange(new[] { gov, rebel, northK, southK, conqueror, target, rivalSponsor });
        gov.FoundedCultures.Add("arab");
        rebel.FoundedCultures.Add("arab");
        northK.FoundedCultures.Add("korean");
        southK.FoundedCultures.Add("korean");
        DemographyCatalog.Declare("PRK", new[] { "korean" });
        DemographyCatalog.Declare("KOR", new[] { "korean" });

        const int width = 16, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var defs = new (int Index, int X, ushort Owner, string Culture, long Pop)[]
        {
            (1, 1, 1, "arab", 8_000_000),
            (2, 2, 1, "arab", 7_000_000),
            (3, 3, 2, "arab", 12_000_000),
            (4, 4, 3, "korean", 26_000_000),
            (5, 5, 4, "korean", 26_000_000),
            (6, 6, 4, "korean", 25_000_000),
            (7, 7, 5, "arab", 22_000_000),
            (8, 8, 6, "arab", 2_200_000),
            (9, 9, 6, "arab", 2_000_000),
            (10, 10, 7, "persian", 88_000_000),
            (11, 11, 5, "kurdish", 4_000_000),
            (12, 12, 5, "assyrian", 4_000_000),
            (13, 13, 5, "kurdish", 3_500_000),
            (14, 14, 5, "kurdish", 3_200_000)
        };
        foreach (var d in defs)
        {
            var cell = (height / 2) * width + d.X;
            states[cell] = (ushort)d.Index;
            owners[cell] = d.Owner;
            var st = new ScenarioState
            {
                MapIndex = d.Index,
                Id = "region_" + d.Index,
                Name = "Region " + d.Index,
                NationMapIndex = d.Owner,
                Population = d.Pop
            };
            st.PopulationGroups.Add(new PopulationGroup { Culture = d.Culture, Religion = "sunni", Size = d.Pop });
            c.States.Add(st);
        }
        var topology = ProvinceTopology.Build(states, owners, width, height, _ => false);
        int ProvOf(int region) => Enumerable.Range(1, topology.ProvinceCount).First(p => topology.ProvinceToState[p] == region);
        foreach (var n in c.Nations)
        {
            var capCentre = topology.CenterOf(ProvOf(n.CapitalStateMapIndex));
            n.CapitalLatitude = capCentre.Latitude;
            n.CapitalLongitude = capCentre.Longitude;
        }
        c.AttachProvinceTopology(topology);
        c.LocateCapitals();

        // 7a. Civil war: rebel/faction (HTH) winning decisively against parent (YEM) -> full annexation & state consolidation
        var civilWar = new StrategicWar
        {
            Id = 1, AttackerId = gov.Id, DefenderId = rebel.Id, CivilConflict = true,
            ClaimedStateMapIndex = 3, WarScore = -85f, AttackerWarSupport = 0.05f, DefenderWarSupport = 0.85f
        };
        c.Wars.Add(civilWar);
        foreach (var r in new[] { 1, 2 })
        {
            var p = ProvOf(r);
            c.SetProvinceController(p, (ushort)rebel.MapIndex);
            c.ProvinceOccupation[p] = 1f;
        }
        Check(Reintegration.ClaimsStateInCivilWar(c, civilWar, rebel, gov),
            "civil-war challenger holding parent capital claims the whole state");
        var rebelLedger = new PeaceLedger(c, civilWar, rebel.Id);
        var rebelDemand = new PeaceDemand { WarId = civilWar.Id, ProposerId = rebel.Id };
        Check(AiStatecraft.WriteDecisiveSettlement(c, rebel, civilWar, rebelLedger, rebelDemand) &&
              rebelDemand.Provinces.Count == 2 &&
              PeaceConference.Resolve(c, civilWar, rebelDemand),
            "winning civil-war rebel writes and resolves full annexation of parent state");
        Check(!c.HasOwnedLand(gov) && c.StatesOf(rebel).Length == 3 && rebel.SecededFromId == -1,
            "victorious rebel annexes all parent regions and consolidates state identity");

        // 7b. National reunification war (PRK vs KOR): shared people / consolidation -> full annexation
        var reunifyWar = new StrategicWar
        {
            Id = 2, AttackerId = northK.Id, DefenderId = southK.Id, CivilConflict = false,
            Justification = "irredentism", ClaimedStateMapIndex = 5, WarScore = 82f,
            AttackerWarSupport = 0.85f, DefenderWarSupport = 0.10f
        };
        c.Wars.Add(reunifyWar);
        foreach (var r in new[] { 5, 6 })
        {
            var p = ProvOf(r);
            c.SetProvinceController(p, (ushort)northK.MapIndex);
            c.ProvinceOccupation[p] = 1f;
        }
        Check(Reintegration.IsReunificationTarget(c, reunifyWar, northK, southK),
            "shared-culture/consolidation war is recognized as a national reunification war");
        var reunifyLedger = new PeaceLedger(c, reunifyWar, northK.Id);
        var reunifyDemand = new PeaceDemand { WarId = reunifyWar.Id, ProposerId = northK.Id };
        Check(AiStatecraft.WriteDecisiveSettlement(c, northK, reunifyWar, reunifyLedger, reunifyDemand) &&
              reunifyDemand.Provinces.Count == 2 &&
              PeaceConference.Resolve(c, reunifyWar, reunifyDemand),
            "reunification victor annexes all regions of kindred state instead of stopping at one region");
        Check(!c.HasOwnedLand(southK) && c.StatesOf(northK).Length == 3,
            "reunified state owns all regions of both halves");

        // 7c. Promising war of conquest (IRQ vs KWT): full occupation & expansionist regime -> full annexation
        var conquestWar = new StrategicWar
        {
            Id = 3, AttackerId = conqueror.Id, DefenderId = target.Id, CivilConflict = false,
            Justification = "conquest", ClaimedStateMapIndex = 8, WarScore = 88f,
            AttackerWarSupport = 0.85f, DefenderWarSupport = 0.08f
        };
        c.Wars.Add(conquestWar);
        foreach (var r in new[] { 8, 9 })
        {
            var p = ProvOf(r);
            c.SetProvinceController(p, (ushort)conqueror.MapIndex);
            c.ProvinceOccupation[p] = 1f;
        }
        Check(Reintegration.WantsFullConquestAnnexation(c, conquestWar, conqueror, target, 88f, 1f, 1f),
            "decisive conqueror wants full annexation of overrun neighbor");
        var conquestLedger = new PeaceLedger(c, conquestWar, conqueror.Id);
        var conquestDemand = new PeaceDemand { WarId = conquestWar.Id, ProposerId = conqueror.Id };
        Check(AiStatecraft.WriteDecisiveSettlement(c, conqueror, conquestWar, conquestLedger, conquestDemand) &&
              conquestDemand.Provinces.Count == 2 && conquestDemand.Subjugate.Length == 0 &&
              PeaceConference.Resolve(c, conquestWar, conquestDemand),
            "decisive war of conquest annexes multi-region target whole");
        Check(!c.HasOwnedLand(target), "conquered target has zero remaining provinces");

        // 7d. Releasing a vassal as a dominion, feeding it adjacent territory ("раскармливание вассала"), and later re-integrating / re-annexing it
        var releasedVas = CountryRelease.Release(c, conqueror, new CountryRelease.ReleaseOptions
        {
            StateMapIndices = new List<int> { 13 },
            AsSubject = true,
            SubjectType = "dominion",
            Culture = "kurdish",
            CustomName = "Autonomous Kurdistan"
        }, out _);
        Check(releasedVas is not null && releasedVas.AnswersTo(conqueror.Id) && releasedVas.ReleasedFromId == conqueror.Id,
            "country released as dominion remembers its parent overlord");

        // A dependent or leased territory is a piece of the sovereign, not a regional
        // administration that should be fed the metropolis. Macau must not become a blob.
        releasedVas.SubjectType = "dependent_territory";
        var territoryPackage = new[] { 14 };
        Check(!RegionalAdministration.MayGrant(c, conqueror, releasedVas, territoryPackage,
                  out _, out var territoryRefusal) && territoryRefusal.Length > 0,
            "dependent territory is refused administrative land grants");
        Check(RegionalAdministration.MatchingSubject(c, conqueror,
                  c.States.Find(s => s.MapIndex == 14)) is null,
            "dependent territory is not chosen as the recipient for a mainland package");
        releasedVas.SubjectType = "dominion";

        // Feed adjacent region 14 to the existing vassal instead of spawning a second tiny vassal
        conqueror.PoliticalPoints = 50f;
        releasedVas.LastSubjectInteractionDay = -9999;
        var matching = RegionalAdministration.MatchingSubject(c, conqueror, c.States.Find(s => s.MapIndex == 14));
        Check(matching == releasedVas, "MatchingSubject finds existing adjacent vassal for bordering kindred region");
        new GrantSubjectRegionsCommand
        {
            IssuerId = conqueror.Id,
            SubjectId = releasedVas.Id,
            StateMapIndices = new[] { 14 }
        }.Apply(c);
        Check(c.StatesOf(releasedVas).Length == 2,
            "GrantSubjectRegionsCommand feeds adjacent region to existing vassal so it grows to a multi-region administration");

        releasedVas.LibertyDesire = 0.20f;
        Check(SubjectInteractions.Execute(c, conqueror, releasedVas, SubjectInteractionKind.ToggleIntegration, out _),
            "overlord can begin integrating a previously released dominion");
        releasedVas.SubjectIntegrationProgress = 1.0f;
        releasedVas.LibertyDesire = 0.25f;
        Check(SubjectInteractions.Execute(c, conqueror, releasedVas, SubjectInteractionKind.CompleteAnnexation, out _),
            "overlord can re-annex its previously released and fed dominion once integrated");
        Check(!c.HasOwnedLand(releasedVas) &&
              c.States.Find(s => s.MapIndex == 13).NationMapIndex == conqueror.MapIndex &&
              c.States.Find(s => s.MapIndex == 14).NationMapIndex == conqueror.MapIndex,
            "re-annexed macro-vassal's territories all return to overlord");

        // 7e. Foreign agitation on subject liberty desire + inter-subject conspiracy ("сговор") + annexation-triggered multi-vassal rebellion
        var vasA = CountryRelease.Release(c, conqueror, new CountryRelease.ReleaseOptions
        {
            StateMapIndices = new List<int> { 11 },
            AsSubject = true,
            SubjectType = "satellite",
            Culture = "kurdish",
            CustomName = "Kurdish Satellite"
        }, out var whyA);
        var vasB = CountryRelease.Release(c, conqueror, new CountryRelease.ReleaseOptions
        {
            StateMapIndices = new List<int> { 12 },
            AsSubject = true,
            SubjectType = "satellite",
            Culture = "assyrian",
            CustomName = "Assyrian Satellite"
        }, out var whyB);
        Check(vasA is not null && vasB is not null, $"two satellites released for conspiracy test ({whyA} / {whyB})");
        vasA.LibertyDesire = 0.35f;
        vasB.LibertyDesire = 0.38f;

        // Foreign rival (IRN) agitates vasA's independence
        var beforeLibA = vasA.LibertyDesire;
        new SubjectInteractionCommand
        {
            IssuerId = rivalSponsor.Id,
            SubjectId = vasA.Id,
            Kind = SubjectInteractionKind.AgitateIndependence
        }.Apply(c);
        Check(vasA.LibertyDesire > beforeLibA + 0.15f && vasA.SubjectForeignBackers.Contains(rivalSponsor.Id),
            "foreign independence agitation sharply increases subject liberty desire and records foreign backer");
        Check(vasA.SubjectConspiring && vasB.SubjectConspiring,
            "agitated subject and disgruntled sibling satellite form an inter-subject conspiracy ('сговор')");
        Check(SubjectSystem.LibertyDesireOf(c, vasA) > 0.50f,
            "LibertyDesireOf accounts for combined conspiracy strength and foreign backers");

        // Overlord attempts to force annexation on conspiring/high-liberty vasA -> triggers coordinated rebellion war of BOTH vasA and vasB!
        vasA.SubjectIntegrationProgress = 1.0f;
        new SubjectInteractionCommand
        {
            IssuerId = conqueror.Id,
            SubjectId = vasA.Id,
            Kind = SubjectInteractionKind.CompleteAnnexation
        }.Apply(c);
        var rebellionWar = c.Wars.LastOrDefault(w => !w.Ended && w.Opposes(conqueror.Id, vasA.Id));
        Check(!vasA.IsSubject && !vasB.IsSubject && rebellionWar is not null &&
              rebellionWar.Opposes(conqueror.Id, vasB.Id),
            "attempting to annex a conspiring/unwilling vassal triggers a coordinated independence war with all fellow conspirators");

        // 7f. Two micro-clients under the same overlord can be consolidated into one
        // administration: the smaller one disappears, its land and forces join the keeper,
        // and the result remains a subject rather than becoming direct metropolitan land.
        conqueror.PoliticalPoints = 200f;
        var mergeA = CountryRelease.Release(c, conqueror, new CountryRelease.ReleaseOptions
        {
            StateMapIndices = new List<int> { 13 }, AsSubject = true, SubjectType = "satellite",
            CustomName = "Merge Test A"
        }, out var mergeWhyA);
        var mergeB = CountryRelease.Release(c, conqueror, new CountryRelease.ReleaseOptions
        {
            StateMapIndices = new List<int> { 14 }, AsSubject = true, SubjectType = "satellite",
            CustomName = "Merge Test B"
        }, out var mergeWhyB);
        Check(mergeA is not null && mergeB is not null,
            $"two neighbouring satellites released for consolidation test ({mergeWhyA} / {mergeWhyB})");
        var mergeKeep = mergeA.Population >= mergeB.Population ? mergeA : mergeB;
        var mergeAbsorb = ReferenceEquals(mergeKeep, mergeA) ? mergeB : mergeA;
        Check(SubjectBlocs.CanMerge(c, conqueror, mergeKeep, mergeAbsorb, out var mergePrice, out var mergeRefusal),
            $"adjacent micro-clients are mergeable ({mergeRefusal})");
        Check(mergePrice > 0f, "consolidating two administrations has a political price");
        var mergeBefore = conqueror.PoliticalPoints;
        Check(SubjectBlocs.Merge(c, conqueror, mergeKeep, mergeAbsorb, out var mergeFeedback),
            $"subject consolidation executes ({mergeFeedback})");
        Check(mergeBefore - conqueror.PoliticalPoints >= mergePrice - 0.01f,
            "subject consolidation deducts the quoted political points");
        Check(!mergeAbsorb.IsSubject && c.Fallen.Contains(mergeAbsorb.Id) && !c.HasOwnedLand(mergeAbsorb),
            "absorbed micro-client is retired and has no land left");
        Check(mergeKeep.IsSubject && mergeKeep.AnswersTo(conqueror.Id) &&
              c.StatesOf(mergeKeep).Length == 2,
            "keeper remains a subject and gains the absorbed region");
        Check(conqueror.DecisionAvailableDay.GetValueOrDefault(
                  SubjectBlocs.MergeKey(mergeKeep, mergeAbsorb)) > c.ElapsedDays,
            "subject consolidation stamps a cooldown on the merged pair");
    }
}
