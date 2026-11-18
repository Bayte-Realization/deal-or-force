using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

/// <summary>
/// The owner's sentences of 17 November 2026, pinned: a unified nation owns its peoples; a crushed
/// rising leaves its region quiet for years, and the same region does not rise again the day the
/// protection ends; a conquered people's resentment fades under peaceful rule, faster with
/// autonomy, and the lever has a price the computer pays too. (docs/design/gameplay-2026-11-17/
/// conquest-aftermath.md, section 9.)
/// </summary>
static class ConquestAftermathTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("conquest aftermath: " + note); Console.WriteLine("PASS conquest aftermath: " + note); }

    // Fixture peoples under tags of their own, so declaring them never touches the real Serbia that
    // other groups of the suite read from the same static catalogue.
    const string Serbia = "CQS", Bulgaria = "CQB", Rebels = "CQR", Croatia = "CQH", Slovenia = "CQL",
        Bosnia = "CQO", Montenegro = "CQM", Macedonia = "CQK", Greece = "CQG", Turkey = "CQT";

    static void DeclarePeoples()
    {
        DemographyCatalog.Declare(Serbia, new[] { "serbian" });
        DemographyCatalog.Declare(Bulgaria, new[] { "bulgarian" });
        DemographyCatalog.Declare(Rebels, new[] { "croatian" });
        DemographyCatalog.Declare(Croatia, new[] { "croatian" });
        DemographyCatalog.Declare(Slovenia, new[] { "slovene" });
        DemographyCatalog.Declare(Bosnia, new[] { "bosniak" });
        DemographyCatalog.Declare(Montenegro, new[] { "montenegrin" });
        DemographyCatalog.Declare(Macedonia, new[] { "macedonian" });
        DemographyCatalog.Declare(Greece, new[] { "greek" });
        DemographyCatalog.Declare(Turkey, new[] { "turkish" });
    }

    // Regions, two provinces each, left to right on a strip of map:
    //   5001 Belgrade   serbian    Serbia's capital and homeland
    //   5002 Zagreb     croatian   held by Serbia, taken by war   (kindred, south_slavic)
    //   5003 Sofia      bulgarian  held by Serbia, taken by war   (kindred, south_slavic)
    //   5004 Durres     albanian   held by Serbia, taken by war   (hellenic: not kindred)
    //   5005 Plovdiv    bulgarian  Bulgaria's own
    //   5006 Split      croatian   a breakaway that rose against Serbia
    static readonly (string Name, string Culture, int Owner)[] Layout =
    {
        ("Belgrade", "serbian", 0), ("Zagreb", "croatian", 0), ("Sofia", "bulgarian", 0),
        ("Durres", "albanian", 0), ("Plovdiv", "bulgarian", 1), ("Split", "croatian", 2)
    };

    static Campaign Fixture(int player = 1)
    {
        var c = new Campaign(20261117) { ScenarioId = "conquest_aftermath_fixture" };
        c.AddNation(new Nation { Tag = Serbia, Name = "Serbia", MapIndex = 601, Population = 7_000_000, Gdp = 300,
            Treasury = 100, Stability = .45f, PoliticalPoints = 40, WeeklyTaxIncome = 2f, WeeklyBalance = 1f });
        c.AddNation(new Nation { Tag = Bulgaria, Name = "Bulgaria", MapIndex = 602, Population = 1_000_000, Gdp = 100,
            Treasury = 50, Stability = .6f });
        c.AddNation(new Nation { Tag = Rebels, Name = "Split Republic", MapIndex = 603, Population = 1_000_000,
            Treasury = 10, SecededFromId = 0, RevoltParentId = 0 });
        c.ConfigureSinglePlayer(player);
        for (var i = 0; i < Layout.Length; i++)
        {
            var (name, culture, owner) = Layout[i];
            var region = new ScenarioState { Id = "cq_" + name.ToLowerInvariant(), Name = name, MapIndex = 5001 + i,
                NationMapIndex = 601 + owner, NationId = c.Nations[owner].Tag, Population = i == 1 ? 2_000_000 : 1_000_000,
                Stability = .4f, Radicalism = .1f, StandardOfLiving = .5f, Literacy = .7f };
            region.PopulationGroups.Add(new PopulationGroup { Culture = culture, Size = region.Population });
            c.States.Add(region);
        }
        c.States[0].CoreTags.Add(Serbia);
        c.States[4].CoreTags.Add(Bulgaria);
        c.Homelands[2] = new() { 5006 };
        // Split rose against Serbia: the rising took it from Serbia, and the region's record says so
        // (ConquestAcceptance.ChangedHands keeps it while a breakaway holds the ground).
        c.States[5].AcceptanceOf = c.Nations[0].Id;
        c.Nations[0].CapitalStateMapIndex = 5001;
        c.Nations[1].CapitalStateMapIndex = 5005;
        c.Nations[2].CapitalStateMapIndex = 5006;
        const int w = 192, h = 16;
        var states = new ushort[w * h]; var owners = new ushort[states.Length]; var rgb = new byte[states.Length * 3];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var p = y * w + x; states[p] = (ushort)(5001 + x / 32); owners[p] = (ushort)(601 + Layout[x / 32].Owner);
            rgb[p * 3 + 2] = (byte)(x / 16 + 1);
        }
        c.AttachProvinceTopology(ProvinceTopology.FromColorRaster(states, owners, w, h,
            new RgbDataMap(w, h, rgb), Enumerable.Range(1, 12).ToDictionary(p => p, p => p)));
        return c;
    }

    static int[] Provinces(Campaign c, int region) =>
        Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToState[p] == region).ToArray();

    static void Day(Campaign c, int day) => c.RestoreRuntimeState(c.Date, day, true, c.Random.State,
        c.Player.Id, c.HumanSeats.ToArray(), 0, c.NextBattleId, c.MapRevision, true, null, null, null, null);

    static ScenarioState Region(Campaign c, string name) => c.States.First(s => s.Name == name);

    static float Entry(Breakdown b, string source) =>
        b.Entries.Where(e => e.Source == source || e.Source == Loc.Get(source)).Sum(e => e.Value);

    static FormableNation Union(string id, string[] required, string[] cultures, int[] provinces, string restores = "") =>
        new(id, id, "", new[] { Serbia }, required, provinces, 0f, 0f, 0f, 0f,
            Array.Empty<string>(), Array.Empty<string>(), "") { EligibleCultures = cultures, Restores = restores };

    public static void Run()
    {
        Loc.ResetCatalog(); MovementCatalog.ResetCatalog(); DemographyCatalog.ResetCatalog();
        DeclarePeoples();
        try
        {
            Unions();
            Risings();
            Acceptance();
            Autonomy();
            Persistence();
            Calibration();
        }
        finally
        {
            // The catalogue is the process's; leave it as the next group expects to find it.
            DemographyCatalog.ResetCatalog();
        }
    }

    // ------------------------------------------------------------------ A. unions --

    static void Unions()
    {
        var c = Fixture(); var srb = c.Nations[0];
        var zagreb = Region(c, "Zagreb"); var sofia = Region(c, "Sofia");
        Check(!DemographyCatalog.IsPrimary(Serbia, "croatian") && TerritorialClaims.TitleOf(c, srb, zagreb) != TerritorialTitle.Homeland,
            "before the proclamation Croats are a minority of the conqueror and Zagreb is foreign ground");
        var yugoslavia = Union("cq_yugoslavia", new[] { Serbia, Croatia, Slovenia, Bosnia, Montenegro, Macedonia },
            new[] { "serbian", "croatian", "slovene", "bosniak", "montenegrin", "macedonian" },
            Provinces(c, 5001).Concat(Provinces(c, 5002)).Concat(Provinces(c, 5003)).ToArray());
        FormableNations.DeclareUnionPeoples(c, srb, yugoslavia);
        Check(DemographyCatalog.IsPrimary(Serbia, "croatian") && DemographyCatalog.PrimaryCulture(Serbia) == "serbian" &&
              TerritorialClaims.TitleOf(c, srb, zagreb) == TerritorialTitle.Homeland &&
              EconomySystem.ForeignShare(zagreb, Serbia) < 0.01f,
            "a union proclaimed over conquered land declares its own peoples");
        Check(!DemographyCatalog.IsPrimary(Serbia, "bulgarian") && TerritorialClaims.TitleOf(c, srb, sofia) != TerritorialTitle.Homeland &&
              EconomySystem.ForeignShare(sofia, Serbia) > 0.99f && ConquestAcceptance.Unentitled(c, srb, sofia),
            "a people the union does not name stays foreign, and its ground stays conquered");
        Check(!DemographyCatalog.IsPrimary(Serbia, "slovene"),
            "a people of the union is declared only where the state actually holds a region of it");

        // A union that names a people of another family does not make it the founder's own at once:
        // Albanians named by a Serbian-led union stay a minority, and Durres stays conquered ground.
        c = Fixture(); srb = c.Nations[0];
        DemographyCatalog.Declare(Serbia, new[] { "serbian" });
        var durres = Region(c, "Durres"); zagreb = Region(c, "Zagreb");
        var balkan = Union("cq_balkan", new[] { Serbia, Croatia, Greece },
            new[] { "serbian", "croatian", "albanian" },
            Provinces(c, 5001).Concat(Provinces(c, 5002)).Concat(Provinces(c, 5004)).ToArray());
        FormableNations.DeclareUnionPeoples(c, srb, balkan);
        Check(DemographyCatalog.IsPrimary(Serbia, "croatian") && TerritorialClaims.TitleOf(c, srb, zagreb) == TerritorialTitle.Homeland &&
              !DemographyCatalog.IsPrimary(Serbia, "albanian") && TerritorialClaims.TitleOf(c, srb, durres) != TerritorialTitle.Homeland &&
              ConquestAcceptance.Unentitled(c, srb, durres),
            "a union makes only its founder's own family of peoples its own: a named people of another family stays conquered");
        // But it did promise them a place: the real Balkan Federation names Albanians, and a Serbia
        // that proclaimed it digests Durres at the kindred pace; a plain Serbia does not.
        Check(!ConquestAcceptance.Kindred(srb, durres, c), "without a union an Albanian region is not kindred to Serbia");
        srb.FormedNationId = "balkan_federation";
        Check(FormableNations.Find(c, "balkan_federation") is not null && ConquestAcceptance.Kindred(srb, durres, c),
            "a people the proclaimed union names accepts it at the kindred pace");
        srb.FormedNationId = "";

        DemographyCatalog.Declare(Serbia, new[] { "serbian" });
        var start = UnionCohesion.Start(c, srb, yugoslavia);
        Check(MathF.Abs(start - 0.70f) < 0.001f && start > c.Defines.UnionCalmStability,
            $"a kindred union starts above the trouble line: six South Slav peoples {start:0.000}");
        // The next two are reckoned for a founder that has declared nothing beyond its own people.
        srb.FoundedCultures.Clear();
        var mixed = Union("cq_mixed", new[] { Serbia, Greece, Turkey }, Array.Empty<string>(), Array.Empty<int>());
        var paid = UnionCohesion.Start(c, srb, mixed);
        Check(MathF.Abs(paid - (1f - 2f * c.Defines.UnionCohesionPerPeople)) < 0.0001f,
            $"a mixed union pays the full price: three peoples of three families {paid:0.000}");
        var restoration = Union("cq_restored", new[] { Serbia, Greece }, Array.Empty<string>(), Array.Empty<int>(), restores: Greece);
        var restored = UnionCohesion.Start(c, srb, restoration);
        Check(MathF.Abs(restored - (1f - 0.5f * c.Defines.UnionCohesionPerPeople)) < 0.0001f,
            $"a restoration is unchanged: half of one people's price {restored:0.000}");

        srb.FormedNationId = "cq_yugoslavia";
        srb.UnionCohesion = 0.40f;
        NationalModifiers.Invalidate(srb);
        var forecast = RadicalismCore.Forecast(c, srb, Region(c, "Belgrade"));
        var trouble = (0.6f - 0.40f) * 0.035f;
        Check(MathF.Abs(Entry(forecast, "Laws, spirits and the union") - NationalModifiers.Of(srb, "radicalism_weekly")) < 1e-6f &&
              NationalModifiers.Of(srb, "radicalism_weekly") > trouble * 0.5f,
            $"the forecast shows the union's radicalism, as the daily modifier pass applies it: {Entry(forecast, "Laws, spirits and the union"):0.0000} of {NationalModifiers.Of(srb, "radicalism_weekly"):0.0000} [{string.Join("; ", forecast.Entries.Select(e => e.Source + "=" + e.Value.ToString("0.0000")))}]");
    }

    // ------------------------------------------------------------------ B. risings --

    static void Risings()
    {
        var c = Fixture(); var srb = c.Nations[0]; var split = Region(c, "Split");
        Day(c, 400);
        split.Radicalism = 0.9f; split.Acceptance = 0.4f;
        StatePartition.Annex(c, Provinces(c, 5006), srb, asConquest: true);
        Check(split.NationMapIndex == srb.MapIndex && MathF.Abs(split.Radicalism - 0.45f) < 1e-5f &&
              split.ExhaustedUntilDay == 400 + c.Defines.RisingExhaustionDays && MathF.Abs(split.Acceptance - 0.2f) < 1e-5f &&
              c.Chronicle.Entries.Any(e => e.Kind == "rising_exhausted"),
            "a crushed rising halves radicalism and exhausts the region");
        Check(RevoltRecovery.DaysLeft(c, srb, split) == c.Defines.ReintegrationRecoveryDays,
            "and the protection against a new rising stays the two years it always was");

        // Serbia's own homeland is Serbia's however long the breakaway counted it as its own.
        c = Fixture(); srb = c.Nations[0]; split = Region(c, "Split");
        split.CoreTags.Add(Serbia); split.AcceptanceOf = c.Nations[2].Id; split.Radicalism = 0.8f;
        StatePartition.Annex(c, Provinces(c, 5006), srb, asConquest: true);
        Check(MathF.Abs(split.Radicalism - 0.4f) < 1e-5f && split.ExhaustedUntilDay > 0,
            "the state's own homeland taken back from its breakaway is a rising crushed");

        c = Fixture(); srb = c.Nations[0]; split = Region(c, "Split");
        split.Radicalism = 0.9f;
        StatePartition.Annex(c, Provinces(c, 5006), srb, asConquest: false, byConsent: true);
        Check(split.NationMapIndex == srb.MapIndex && MathF.Abs(split.Radicalism - 0.9f) < 1e-6f && split.ExhaustedUntilDay == -1,
            "a peaceful reunion is not a crushing");

        // Ground the breakaway took from somebody else was never Serbia's: taking it from the
        // breakaway in an ordinary war is a conquest, not a rising put down.
        c = Fixture(); srb = c.Nations[0]; var rebels = c.Nations[2];
        var plovdiv = Region(c, "Plovdiv");
        StatePartition.Annex(c, Provinces(c, 5005), rebels, asConquest: true);
        plovdiv.Radicalism = 0.8f; plovdiv.Acceptance = 0.6f;
        Day(c, 300);
        StatePartition.Annex(c, Provinces(c, 5005), srb, asConquest: true);
        Check(plovdiv.NationMapIndex == srb.MapIndex && MathF.Abs(plovdiv.Radicalism - 0.8f) < 1e-6f &&
              plovdiv.Acceptance == 0f && plovdiv.AcceptanceOf == srb.Id && plovdiv.ExhaustedUntilDay == -1 &&
              !c.Chronicle.Entries.Any(e => e.Kind == "rising_exhausted"),
            "a breakaway's own conquest taken from it is a conquest: no free halving, no exhaustion, acceptance from nothing");

        // A seceded state written back into Serbia as an integrated subject: a peaceful absorption.
        c = Fixture(); srb = c.Nations[0]; split = Region(c, "Split");
        split.Radicalism = 0.9f; split.Acceptance = 0.3f;
        SubjectInteractions.ForceAnnexIntegrated(c, srb, c.Nations[2]);
        Check(split.NationMapIndex == srb.MapIndex && MathF.Abs(split.Radicalism - 0.9f) < 1e-6f &&
              split.ExhaustedUntilDay == -1 && MathF.Abs(split.Acceptance - 0.3f) < 1e-6f,
            "an integrated subject that once seceded is absorbed, not crushed");

        // A third of the grievance: minority and overstretch, with and without exhaustion.
        c = Fixture(); srb = c.Nations[0];
        var zagreb = Region(c, "Zagreb");
        Day(c, 100);
        srb.ImperialOverstretch = 0.5f;
        float Inflow()
        {
            var f = RadicalismCore.Forecast(c, srb, zagreb);
            return Entry(f, "Minority grievances") + Entry(f, "Imperial overstretch") + Entry(f, "Occupation") +
                   Entry(f, "Exhausted after a crushed rising");
        }
        var full = Inflow();
        zagreb.ExhaustedUntilDay = 100 + 365;
        var exhausted = Inflow();
        Check(full > 0f && MathF.Abs(exhausted / full - c.Defines.RisingExhaustionInflow) < 1e-4f,
            $"an exhausted region takes a third of the grievance: {exhausted * 100:0.000} of {full * 100:0.000} pp a week");

        // The owner's sentence: a region crushed at 0.7 is quiet when its protection ends, and the
        // same region left angry - the rule before this - is not.
        float After(bool crushed)
        {
            var w = Fixture(); var owner = w.Nations[0]; var region = Region(w, "Zagreb");
            owner.ImperialOverstretch = 0.6f;
            region.Radicalism = 0.7f;
            if (crushed) RisingAftermath.Crushed(w, owner, region);
            for (var week = 0; week < 730 / 7; week++)
            {
                Day(w, week * 7);
                region.Radicalism += RadicalismCore.WeeklyChange(w, owner, region, false, 0f, 0f);
            }
            return region.Radicalism;
        }
        var quiet = After(true); var angry = After(false);
        Check(quiet < 0.30f && quiet < angry - 0.05f,
            $"a crushed rising leaves its region quiet for the designed years: {quiet:0.000} when the protection ends, {angry:0.000} if it had not been crushed");
        Check(quiet < c.Defines.AcceptanceStallRadicalism,
            "and calm enough by then to start accepting the state rather than rise again");

        // The union fracture's door: the crushed pressure thaws, the protection does not.
        c = Fixture(); srb = c.Nations[0]; split = Region(c, "Split");
        srb.FormedNationId = "cq_yugoslavia";
        StatePartition.Annex(c, Provinces(c, 5006), srb, asConquest: true);
        split.Radicalism = 0.95f; split.SecessionPressure = 0.2f;
        Check(UnionCohesion.AngriestRegion(c, srb) != split, "union fracture respects revolt recovery");
        Day(c, c.Defines.ReintegrationRecoveryDays + 1);
        Check(UnionCohesion.AngriestRegion(c, srb) == split,
            "and only for its two years: no immunity beyond RevoltRecovery");
    }

    // --------------------------------------------------------------- C. acceptance --

    static void Acceptance()
    {
        var c = Fixture(); var srb = c.Nations[0];
        var belgrade = Region(c, "Belgrade"); var zagreb = Region(c, "Zagreb"); var durres = Region(c, "Durres");
        foreach (var s in new[] { belgrade, zagreb, durres }) ConquestAcceptance.Week(c, srb, s);
        var w = c.Defines.AcceptanceWeekly;
        Check(belgrade.Acceptance == 0f && MathF.Abs(durres.Acceptance - w) < 1e-7f &&
              MathF.Abs(zagreb.Acceptance - w * c.Defines.AcceptanceKindredFactor) < 1e-7f,
            "acceptance grows only on unentitled ground");
        Check(MathF.Abs(zagreb.Acceptance / durres.Acceptance - c.Defines.AcceptanceKindredFactor) < 1e-4f,
            "a kindred people accepts faster");

        zagreb.Radicalism = c.Defines.AcceptanceStallRadicalism + 0.01f;
        var stalled = ConquestAcceptance.WeeklyGain(c, srb, zagreb, out var why);
        zagreb.Radicalism = 0.1f;
        foreach (var p in Provinces(c, 5002)) c.SetProvinceController(p, (ushort)602);
        c.MarkMapChanged();
        var occupied = ConquestAcceptance.WeeklyGain(c, srb, zagreb, out var whyArmy);
        Check(stalled == 0f && why == ConquestAcceptance.StalledByRadicalism && occupied == 0f &&
              whyArmy == ConquestAcceptance.StalledByOccupation,
            "acceptance stalls above half radicalism and under a foreign army");

        c = Fixture(); srb = c.Nations[0]; zagreb = Region(c, "Zagreb");
        var before = StabilityCore.RegionStability(c, srb, zagreb);
        var weightBefore = ConquestAcceptance.StrainWeight(c, zagreb);
        zagreb.Acceptance = 1f; zagreb.AcceptanceOf = srb.Id;
        var after = StabilityCore.RegionStability(c, srb, zagreb);
        var weightAfter = ConquestAcceptance.StrainWeight(c, zagreb);
        var foreign = EconomySystem.ForeignShare(zagreb, Serbia);
        Check(MathF.Abs(after - before - foreign * StabilityCore.Weights.Minority * c.Defines.AcceptanceMinorityRelief) < 1e-5f &&
              MathF.Abs(weightAfter / weightBefore - (1f - c.Defines.AcceptanceOverstretchRelief)) < 1e-5f,
            $"an accepted region costs less stability and overstretch: {before:0.000} -> {after:0.000}, weight {weightBefore:0.00} -> {weightAfter:0.00}");

        var strainedBefore = StabilityCore.StrainedWeight(c, srb);
        zagreb.TrackedOwnerMapIndex = zagreb.NationMapIndex; zagreb.OwnerSinceDay = 0;
        for (var day = 1; day <= HomelandIntegration.Cycle + 1; day++) { Day(c, day); HomelandIntegration.Tick(c); }
        Check(TerritorialClaims.TitleOf(c, srb, zagreb) == TerritorialTitle.Homeland &&
              c.Chronicle.Entries.Any(e => e.Kind == "homeland_integrated") &&
              MathF.Abs(strainedBefore - StabilityCore.StrainedWeight(c, srb) - weightAfter) < 1e-5f,
            "full acceptance makes a homeland, through the integration the calendar already used, and it leaves the overstretch");

        c = Fixture(); srb = c.Nations[0]; var bgr = c.Nations[1];
        zagreb = Region(c, "Zagreb"); durres = Region(c, "Durres"); var sofia = Region(c, "Sofia");
        zagreb.Acceptance = 0.7f; zagreb.AcceptanceOf = srb.Id;
        durres.Acceptance = 1f; durres.AcceptanceOf = srb.Id;
        sofia.Acceptance = 0.4f; sofia.AcceptanceOf = srb.Id;
        StatePartition.Annex(c, Provinces(c, 5002), bgr, asConquest: true);
        StatePartition.Annex(c, Provinces(c, 5004), bgr, asConquest: false, byConsent: true);
        StatePartition.Annex(c, Provinces(c, 5003), bgr, asConquest: false);
        Check(zagreb.Acceptance == 0f && zagreb.AcceptanceOf == bgr.Id,
            "conquest resets acceptance, and it is the new holder's to earn");
        Check(MathF.Abs(durres.Acceptance - c.Defines.AcceptancePeacefulStart) < 1e-6f && durres.AcceptanceOf == bgr.Id,
            $"a treaty carries half of how settled the region was: Serbia's fully digested Durres arrives at {durres.Acceptance:0.00}, not 1");
        Check(sofia.Acceptance == 0f, "ground yielded to an ultimatum is ground taken by force: it starts at nothing");
        durres.TrackedOwnerMapIndex = durres.NationMapIndex; durres.OwnerSinceDay = 0;
        durres.Acceptance = 1f; durres.AcceptanceOf = srb.Id;
        for (var day = 1; day <= 2 * HomelandIntegration.Cycle + 1; day++) { Day(c, day); HomelandIntegration.Tick(c); }
        Check(TerritorialClaims.TitleOf(c, bgr, durres) != TerritorialTitle.Homeland &&
              ConquestAcceptance.Of(durres, bgr) == 0f && ConquestAcceptance.MinorityFactor(c.Defines, durres, bgr) == 1f,
            "acceptance earned under another state does not make the region the holder's homeland, nor ease its minority");

        // A fresh conquest passed through a subject and annexed back: still a conquest.
        c = Fixture(); srb = c.Nations[0]; bgr = c.Nations[1]; zagreb = Region(c, "Zagreb");
        StatePartition.Annex(c, Provinces(c, 5002), bgr, asConquest: true);
        StatePartition.Annex(c, Provinces(c, 5002), srb, asConquest: true);
        StatePartition.Annex(c, Provinces(c, 5002), bgr, asConquest: false, byConsent: true);
        StatePartition.Annex(c, Provinces(c, 5002), srb, asConquest: false, byConsent: true);
        Check(zagreb.NationMapIndex == srb.MapIndex && zagreb.Acceptance == 0f && zagreb.AcceptanceOf == srb.Id,
            "a conquest handed to a subject and annexed back keeps nothing it never earned");

        // Resentment fades under peaceful rule, and faster with the lever.
        float Years(bool autonomous)
        {
            var world = Fixture(); var owner = world.Nations[0]; var region = Region(world, "Zagreb");
            region.Radicalism = 0.25f;
            if (autonomous) region.AutonomousSinceDay = 0;
            for (var week = 1; week <= 52 * 40; week++)
            {
                Day(world, week * 7);
                region.Radicalism += RadicalismCore.WeeklyChange(world, owner, region, false, 0f, 0f);
                ConquestAcceptance.Week(world, owner, region);
                if (region.Acceptance >= 1f) return week / 52f;
            }
            return 40f;
        }
        var plain = Years(false); var granted = Years(true);
        Check(plain < 20f && granted < plain * 0.6f,
            $"a conquered people's resentment fades under peaceful rule ({plain:0.0} years) and faster with autonomy ({granted:0.0})");

        c = Fixture(); srb = c.Nations[0]; zagreb = Region(c, "Zagreb");
        zagreb.Acceptance = 0.25f;
        var outlook = ConquestAcceptance.Read(c, srb, zagreb);
        var gain = ConquestAcceptance.WeeklyGain(c, srb, zagreb, out _);
        Check(outlook.Shown && outlook.WeeklyGain == gain && outlook.YearsLeft == (int)Math.Ceiling(0.75f / gain / 52f) &&
              !ConquestAcceptance.Read(c, srb, Region(c, "Belgrade")).Shown,
            "the region card reads the same gain the weekly pass applies, and says nothing of the homeland");
    }

    // ----------------------------------------------------------------- D. autonomy --

    static void Autonomy()
    {
        var c = Fixture(); var srb = c.Nations[0];
        var belgrade = Region(c, "Belgrade"); var zagreb = Region(c, "Zagreb"); var plovdiv = Region(c, "Plovdiv");
        srb.PoliticalPoints = 10;
        Check(RegionalAutonomy.Refusal(c, srb, belgrade).Length > 0 && RegionalAutonomy.Refusal(c, srb, plovdiv).Length > 0 &&
              RegionalAutonomy.Refusal(c, srb, zagreb) == "Not enough political points.",
            "autonomy is refused with a reason: the capital, another country's region, too few points");
        srb.PoliticalPoints = 40;
        new GrantRegionalAutonomyCommand { IssuerId = srb.Id, StateMapIndex = zagreb.MapIndex }.Apply(c);
        Check(RegionalAutonomy.Autonomous(zagreb) && MathF.Abs(srb.PoliticalPoints - (40 - c.Defines.AutonomyPoliticalCost)) < 1e-5f &&
              RegionalAutonomy.Refusal(c, srb, zagreb) == "The region already governs itself.",
            "autonomy costs political points, and a region governs itself once");

        var free = Fixture(); var other = Region(free, "Zagreb");
        srb.ImperialOverstretch = free.Nations[0].ImperialOverstretch = 0.5f;
        var weight = ConquestAcceptance.StrainWeight(c, zagreb) / ConquestAcceptance.StrainWeight(free, other);
        var minority = Entry(RadicalismCore.Forecast(c, srb, zagreb), "Minority grievances") /
                       Entry(RadicalismCore.Forecast(free, free.Nations[0], other), "Minority grievances");
        var strain = Entry(RadicalismCore.Forecast(c, srb, zagreb), "Imperial overstretch") /
                     Entry(RadicalismCore.Forecast(free, free.Nations[0], other), "Imperial overstretch");
        Check(MathF.Abs(weight - c.Defines.AutonomyOverstretchWeight) < 1e-5f &&
              MathF.Abs(minority - c.Defines.AutonomyRadicalismInflow) < 1e-4f && MathF.Abs(strain - c.Defines.AutonomyRadicalismInflow) < 1e-4f,
            "autonomy cuts overstretch weight and radicalism inflow");

        var price = RegionalAutonomy.WeeklyPrice(c, srb, zagreb);
        var treasury = srb.Treasury;
        RegionalAutonomy.PayWeek(c, srb);
        Check(MathF.Abs(price - c.Defines.AutonomyRevenueShare * (2_000_000f / srb.Population) * srb.WeeklyTaxIncome) < 1e-6f &&
              MathF.Abs(treasury - srb.Treasury - price) < 1e-5f && MoneyLedger.FlowCount > (int)MoneyFlow.RegionalAutonomy,
            $"autonomy costs a share of the region's taxes: {price:0.000} a week of {srb.WeeklyTaxIncome:0.00}");

        Day(c, 50);
        zagreb.Radicalism = 0.2f; zagreb.Acceptance = 0.4f;
        new RevokeRegionalAutonomyCommand { IssuerId = srb.Id, StateMapIndex = zagreb.MapIndex }.Apply(c);
        Check(!RegionalAutonomy.Autonomous(zagreb) && MathF.Abs(zagreb.Radicalism - 0.3f) < 1e-5f &&
              MathF.Abs(zagreb.Acceptance - 0.2f) < 1e-5f && zagreb.AutonomyRevokedDay == 50 &&
              RegionalAutonomy.Refusal(c, srb, zagreb) == "Its autonomy was taken back too recently to be granted again.",
            "revoking autonomy angers the region and blocks a regrant");
        Day(c, 50 + c.Defines.AutonomyRegrantDays);
        Check(RegionalAutonomy.Refusal(c, srb, zagreb) == "", "for three years, not for ever");

        c = Fixture(); srb = c.Nations[0]; zagreb = Region(c, "Zagreb");
        new GrantRegionalAutonomyCommand { IssuerId = srb.Id, StateMapIndex = zagreb.MapIndex }.Apply(c);
        StatePartition.Annex(c, Provinces(c, 5002), c.Nations[1], asConquest: true);
        Check(!RegionalAutonomy.Autonomous(zagreb) && zagreb.AutonomyRevokedDay == -1, "autonomy ends when the region changes hands");

        // The computer, by the same rules.
        c = Fixture(player: 1); srb = c.Nations[0]; zagreb = Region(c, "Zagreb"); var durres = Region(c, "Durres");
        srb.ImperialOverstretch = 0.3f;
        zagreb.Radicalism = 0.3f; durres.Radicalism = 0.4f; Region(c, "Sofia").Radicalism = 0.1f;
        var day = 7 - srb.Id % 7;
        Day(c, day);
        RegionalAutonomy.ConsiderGrant(c, srb);
        Check(RegionalAutonomy.Autonomous(zagreb) && !RegionalAutonomy.Autonomous(durres) &&
              srb.DecisionAvailableDay.GetValueOrDefault(RegionalAutonomy.NextKey) == day + c.Defines.AiAutonomyIntervalDays,
            "the computer grants autonomy to its angriest conquered region (most people angry, not the angriest people)");
        Day(c, day + 7);
        RegionalAutonomy.ConsiderGrant(c, srb);
        Check(!RegionalAutonomy.Autonomous(durres), "and not a second one inside the quarter");
        Day(c, day + c.Defines.AiAutonomyIntervalDays);
        srb.WeeklyBalance = -5f; srb.Treasury = 0.01f;
        RegionalAutonomy.ConsiderGrant(c, srb);
        Check(!RegionalAutonomy.Autonomous(durres), "nor one it cannot pay for");

        c = Fixture(player: 0); srb = c.Nations[0]; zagreb = Region(c, "Zagreb");
        srb.ImperialOverstretch = 0.3f; zagreb.Radicalism = 0.4f;
        for (var d = 0; d < 21; d++) { Day(c, d); RegionalAutonomy.ConsiderGrant(c, srb); }
        Check(!c.States.Any(RegionalAutonomy.Autonomous), "nobody grants the player's autonomy");

        // But the player is told where the lever is: the same region the computer would choose, in
        // the situation report and so in the "Should do" alerts, routed to the screen that grants it.
        var notice = SituationReport.Read(c, srb).FirstOrDefault(i => i.Id == ConquestStrainNotice.Id);
        var alert = CampaignAlerts.Read(c, srb).FirstOrDefault(a => a.Id == ConquestStrainNotice.Id);
        Check(notice is not null && notice.TargetId == RegionalAutonomy.Angriest(c, srb).MapIndex &&
              notice.Route == SituationRoute.Crises && alert is not null && alert.Group <= CampaignAlertGroup.Should,
            $"the player sees which conquered region could govern itself, in his list of things to do: {notice?.Title} -> {notice?.Action}");
        srb.PoliticalPoints = 0;
        Check(SituationReport.Read(c, srb).Any(i => i.Id == ConquestStrainNotice.Id &&
                                                    i.Consequences.Contains(Loc.Get(RegionalAutonomy.NoPoints))),
            "and is told even when he cannot pay yet, with the reason");
        srb.ImperialOverstretch = 0f; srb.Stability = 0.6f;
        Check(!SituationReport.Read(c, srb).Any(i => i.Id == ConquestStrainNotice.Id), "and nothing when nothing strains the state");
    }

    // ------------------------------------------------------------ save, load, determinism --

    static void Persistence()
    {
        var c = Fixture(); var srb = c.Nations[0];
        var zagreb = Region(c, "Zagreb"); var sofia = Region(c, "Sofia");
        // A world nobody has conquered anything in: not even the fixture's record of Split's rising.
        Region(c, "Split").AcceptanceOf = -1;
        var snapshot = SaveSystem.ExportNetworkSnapshot(c, "aftermath");
        var json = Decompress(snapshot);
        var loaded = SaveSystem.ImportNetworkSnapshot(snapshot, "aftermath").Campaign;
        // Saves leave default values out, so a world nobody conquered in writes nothing new at all.
        Check(!json.Contains("\"ExhaustedUntilDay\"") && !json.Contains("\"AutonomousSinceDay\"") &&
              !json.Contains("\"AcceptanceOf\"") && loaded is not null &&
              loaded.States.All(s => s.Acceptance == 0f && s.ExhaustedUntilDay == -1 && s.AutonomousSinceDay == -1 && s.AutonomyRevokedDay == -1),
            "an old save without them loads with 0 / -1");
        var plain = CampaignFingerprint.Of(c);

        zagreb.AcceptanceOf = srb.Id;
        var owned = CampaignFingerprint.Of(c);
        Check(owned != plain, "whose rule the acceptance is of participates in fingerprints");
        zagreb.Acceptance = 0.37f; sofia.ExhaustedUntilDay = 900; zagreb.AutonomousSinceDay = 12; sofia.AutonomyRevokedDay = 30;
        Check(CampaignFingerprint.Of(c) != plain && CampaignFingerprint.Of(c) != owned,
            "acceptance, exhaustion and autonomy participate in fingerprints");
        snapshot = SaveSystem.ExportNetworkSnapshot(c, "aftermath");
        loaded = SaveSystem.ImportNetworkSnapshot(snapshot, "aftermath").Campaign;
        var z = Region(loaded, "Zagreb"); var s2 = Region(loaded, "Sofia");
        Check(z.Acceptance == 0.37f && z.AcceptanceOf == srb.Id && s2.ExhaustedUntilDay == 900 && z.AutonomousSinceDay == 12 &&
              s2.AutonomyRevokedDay == 30,
            "acceptance, whose it is, and exhaustion survive save and load");

        // A save written before the fields existed: the same snapshot with them taken out.
        var node = JsonNode.Parse(Decompress(snapshot));
        Strip(node);
        var old = SaveSystem.ImportNetworkSnapshot(Compress(node.ToJsonString()), "aftermath").Campaign;
        Check(old is not null && old.States.All(s => s.Acceptance == 0f && s.ExhaustedUntilDay == -1 && s.AcceptanceOf == -1 &&
                                                     s.AutonomousSinceDay == -1 && s.AutonomyRevokedDay == -1),
            "a save with the fields removed loads the defaults rather than failing");

        // Two worlds from one seed, through conquest, crushing, a grant and a year of weeks.
        string Play()
        {
            var w = Fixture(player: 1); var owner = w.Nations[0];
            owner.ImperialOverstretch = 0.3f;
            Region(w, "Zagreb").Radicalism = 0.35f;
            StatePartition.Annex(w, Provinces(w, 5006), owner, asConquest: true);
            for (var day = 1; day <= 364; day++)
            {
                Day(w, day);
                RegionalAutonomy.ConsiderGrant(w, owner);
                if (day % 7 != 0) continue;
                foreach (var region in w.StatesOf(owner))
                {
                    region.Radicalism += RadicalismCore.WeeklyChange(w, owner, region, false, 0f, 0f);
                    ConquestAcceptance.Week(w, owner, region);
                }
                RegionalAutonomy.PayWeek(w, owner);
            }
            return CampaignFingerprint.Of(w).ToString();
        }
        Check(Play() == Play(), "two worlds from one seed agree");
    }

    static string Decompress(byte[] gz)
    {
        using var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }

    static byte[] Compress(string json)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true))
        using (var writer = new StreamWriter(gzip)) writer.Write(json);
        return memory.ToArray();
    }

    static void Strip(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("PopulationGroups"))
                foreach (var name in new[] { "Acceptance", "AcceptanceOf", "ExhaustedUntilDay", "AutonomousSinceDay", "AutonomyRevokedDay" })
                    obj.Remove(name);
            foreach (var pair in obj.ToList()) Strip(pair.Value);
        }
        else if (node is JsonArray array)
            foreach (var item in array) Strip(item);
    }

    // ----------------------------------------------------------- the 2024 calibration --

    static void Calibration()
    {
        var c = Campaign.CreateBootstrap(20261117);
        var regions = c.States.Count;
        Check(c.States.All(s => s.Acceptance == 0f && s.ExhaustedUntilDay == -1 && !RegionalAutonomy.Autonomous(s)),
            $"the 2024 world starts with nothing accepted, exhausted or self-governing ({regions} regions)");
        // At no acceptance every factor the new rules put into the old formulas is exactly one,
        // and every weight is exactly the expression the overstretch used to write out: so the
        // stability calibration of every country on day 0 is the one it always was, to the bit.
        Check(c.States.All(s => ConquestAcceptance.MinorityFactor(c.Defines, s) == 1f &&
                                ConquestAcceptance.StrainWeight(c, s) == 0.4f + 0.6f * M.Clamp01(s.Radicalism / 0.4f)),
            "the 2024 calibration is unchanged: every new factor is exactly the old one on day 0");
        // And the two formulas the factors enter, written out as they stood before 17 November 2026
        // and compared to the bit for every region and every country of the 2024 world.
        var w = StabilityCore.Weights;
        var regionsDiffer = 0; var countriesDiffer = 0;
        foreach (var nation in c.Nations)
        {
            var legacy = 0f;
            foreach (var state in c.StatesOf(nation))
            {
                var old = M.Clamp(w.Base + (state.StandardOfLiving - w.LivingTolerance) * w.Living - state.Radicalism * w.Radicalism
                    - EconomySystem.ForeignShare(state, nation.Tag) * w.Minority
                    + (state.Literacy + nation.LiteracyOffset - 0.5f) * w.Literacy
                    - StabilityCore.OccupiedShare(c, state, nation) * w.Occupation, 0f, 1f);
                if (BitConverter.SingleToInt32Bits(old) != BitConverter.SingleToInt32Bits(StabilityCore.RegionStability(c, nation, state)))
                    regionsDiffer++;
                if (state.Unclaimed) continue;
                if (TerritorialClaims.TitleOf(c, nation, state) != TerritorialTitle.Homeland && EconomySystem.ForeignShare(state, nation.Tag) >= 0.35f)
                    legacy += 0.4f + 0.6f * M.Clamp01(state.Radicalism / 0.4f);
            }
            var capacity = c.Defines.BaseImperialCapacity;
            var oldOverstretch = legacy <= capacity ? 0f : MathF.Min(1f, (legacy - capacity) * 0.08f);
            if (BitConverter.SingleToInt32Bits(oldOverstretch) != BitConverter.SingleToInt32Bits(StabilityCore.Overstretch(c, nation)))
                countriesDiffer++;
        }
        Check(regionsDiffer == 0 && countriesDiffer == 0,
            $"and the region stability and overstretch of every country on day 0 are the old formulas to the bit ({regionsDiffer} regions, {countriesDiffer} countries differ)");
    }
}
