using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PoliticalCohortTests
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("political cohorts: " + name);
        Console.WriteLine("PASS political cohorts: " + name);
    }

    public static void Run()
    {
        var c = new Campaign(44);
        Check(c.FeatureEnabled("political_cohorts") && c.FeatureEnabled("federal_fracture"),
            "opinion and the federal road start on, and a mod can switch either off");
        var untouched = CampaignFingerprint.Of(c);
        Check(untouched == CampaignFingerprint.Of(c), "an unread cohort does not change the fingerprint");

        var nation = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "USA", Name = "United States", Population = 30_000_000,
            Stability = 0.32f, StandardOfLiving = 0.70f, ShortageIndex = 0.40f,
            DebtCondition = DebtCondition.Default, CapitalStateMapIndex = 10
        };
        var capital = new ScenarioState
        {
            MapIndex = 10, NationMapIndex = 1, Id = "district", Name = "District",
            Population = 1_000_000, Stability = 0.80f, StandardOfLiving = 0.70f
        };
        var state = new ScenarioState
        {
            MapIndex = 20, NationMapIndex = 1, Id = "california", Name = "California",
            Population = 4_000_000, Stability = 0.35f, Radicalism = 0.55f, StandardOfLiving = 0.40f,
            PoliticalDivergence = 0.72f, LawApproval = 0.30f, GovernmentApproval = 0.28f
        };
        c.Nations.Add(nation);
        c.States.Add(capital);
        c.States.Add(state);
        c.InvalidateStateIndex();

        Check(EconomySystem.ForeignShare(state, nation.Tag) == 0f,
            "a region with no listed minority is the country's own people");
        Check(SecessionCore.IsFederalBreak(c, nation, state) && SecessionCore.Pressure(c, nation, state) > 0f,
            "a rejected, insolvent union accumulates a break in a region of its own");
        Check(!SecessionCore.IsFederalBreak(c, nation, capital), "the capital does not secede from itself");

        nation.Stability = 0.90f;
        nation.ShortageIndex = 0f;
        nation.DebtCondition = DebtCondition.Current;
        state.StandardOfLiving = nation.StandardOfLiving;
        state.PoliticalDivergence = 0.20f;
        state.GovernmentApproval = 0.80f;
        Check(SecessionCore.Pressure(c, nation, state) == 0f, "a legitimate solvent union keeps a content region");

        nation.Stability = 0.32f;
        nation.ShortageIndex = 0.40f;
        nation.DebtCondition = DebtCondition.Default;
        state.StandardOfLiving = 0.40f;
        state.PoliticalDivergence = 0.72f;
        state.GovernmentApproval = 0.28f;
        c.SetFeature("federal_fracture", false);
        Check(SecessionCore.Pressure(c, nation, state) == 0f, "the federal road is a define, not a law of nature");
        c.SetFeature("federal_fracture", true);

        var gulf = new ScenarioState
        {
            MapIndex = 21, NationMapIndex = 1, Id = "gulf", Name = "Gulf",
            Population = 2_000_000, Stability = 0.40f, StandardOfLiving = 0.20f
        };
        c.States.Add(gulf);
        c.InvalidateStateIndex();
        nation.Stability = 0.30f;
        nation.StandardOfLiving = 0.55f;
        nation.ShortageIndex = 0.60f;
        var before = CampaignFingerprint.Of(c);
        PoliticalCohorts.Review(c, nation, 1);
        Check(gulf.PoliticalDivergence >= 0f && gulf.LawApproval >= 0f && gulf.GovernmentApproval >= 0f,
            "the first review records a baseline and does not invent a century of anger");
        Check(CampaignFingerprint.Of(c) != before, "reviewed opinion is part of the saved world");
        var seeded = gulf.PoliticalDivergence;
        PoliticalCohorts.Review(c, nation, 800);
        Check(gulf.LawApproval < 0.40f && gulf.PoliticalDivergence > seeded && gulf.PoliticalDivergence > 0.50f,
            "years of shortage and a poorer region pull it away from the centre");

        c.SetFeature("political_cohorts", false);
        Check(!PoliticalCohorts.Mood(c, nation).Reviewed, "a disabled cohort is not a number");
        Check(!PoliticalCohorts.Notice(c, nation), "a disabled cohort does not announce a scandal");
        c.SetFeature("political_cohorts", true);

        var orderly = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "ORD", Population = 5_000_000, Stability = 0.80f, CapitalStateMapIndex = 30
        };
        var bitter = new ScenarioState
        {
            MapIndex = 30, NationMapIndex = 2, Population = 5_000_000,
            PoliticalDivergence = 0.40f, LawApproval = 0.30f, GovernmentApproval = 0.20f
        };
        c.Nations.Add(orderly);
        c.States.Add(bitter);
        c.InvalidateStateIndex();
        var liked = CampaignFingerprint.Of(c);
        Check(PoliticalCohorts.StrainCaption(c, orderly) == "The laws still hold. The government is losing the room."
            && CampaignFingerprint.Of(c) == liked, "a dossier can say the government is losing the room without changing the world");
        Check(PoliticalCohorts.Notice(c, orderly) && orderly.LastPoliticalScandalDay == c.ElapsedDays,
            "an orderly unloved government becomes news");
        Check(!PoliticalCohorts.Notice(c, orderly), "the same scandal is not news again the next day");
        bitter.GovernmentApproval = 0.8f;
        bitter.PoliticalDivergence = 0.1f;
        Check(PoliticalCohorts.StrainCaption(c, orderly) == "Liked enough to argue in the open.",
            "a country people still like is allowed to sound like it");
        Check(PoliticalCohorts.UnrestHeat(bitter) < 0.15f, "a liked region stays quiet on the unrest map");
        bitter.PoliticalDivergence = 0.8f;
        bitter.GovernmentApproval = 0.2f;
        bitter.Radicalism = 0f;
        bitter.SecessionPressure = 0f;
        Check(PoliticalCohorts.UnrestHeat(bitter) > 0.6f,
            "a region that has left its government shows on the unrest map before it is already leaving");
        Check(Math.Abs(PoliticalCohorts.UnrestHeat(new ScenarioState
        {
            Radicalism = 0.1f, SecessionPressure = -1f, PoliticalDivergence = -1f, GovernmentApproval = -1f
        }) - 0.1f) < 0.001f, "an unread region is not painted as a political revolt");
        orderly.DebtCondition = DebtCondition.Stressed;
        var named = CampaignFingerprint.Of(c);
        var (cause, chip) = PoliticalCohorts.CauseOf(c, orderly, bitter);
        var loudest = PoliticalCohorts.Loudest(c, orderly);
        Check(cause == "The centre is in debt, and this region is paying for it." && chip == "the centre's debt",
            "debt is named as the pressure, in the same words the drift already uses");
        Check(loudest is not null && loudest.MapIndex == bitter.MapIndex && loudest.Cause == cause,
            "the loudest region is the one that left the government");
        Check(CampaignFingerprint.Of(c) == named, "naming the cause does not change the world");

        var options = new JsonSerializerOptions { IncludeFields = true };
        var saved = JsonSerializer.Deserialize<ScenarioState>(JsonSerializer.Serialize(state, options), options);
        Check(saved.PoliticalDivergence == state.PoliticalDivergence && saved.GovernmentApproval == state.GovernmentApproval
            && saved.LawApproval == state.LawApproval, "cohort opinion is saved with the region");
        var old = JsonSerializer.Deserialize<ScenarioState>("{\"MapIndex\":9}", options);
        Check(old.PoliticalDivergence < 0f && old.LawApproval < 0f && old.GovernmentApproval < 0f,
            "an old save is unreviewed, not secretly furious");
        RegionPolitics();
    }

    /// <summary>
    /// The political cut of a single region: not who lives there but what they believe.
    ///
    /// The shares are the same ideologies the government itself is chosen from, and they have
    /// to react to the two things the game already keeps about a region - the laws it lives
    /// under and the form of government it lives in - rather than being a fixed table read off
    /// the region's name.
    /// </summary>
    static void RegionPolitics()
    {
        var c = Campaign.CreateBootstrap(907);
        var nation = c.Nations.First(n => n.Population > 0 && c.StatesOf(n).Length > 0);
        c.ConfigureSinglePlayer(nation.Id);
        c.Wars.Clear();
        var sample = c.StatesOf(nation).OrderByDescending(region => region.Population).First();

        var shares = PoliticalMood.RegionShares(c, sample, nation);
        Check(shares.Count == IdeologyCatalog.All.Count && shares.Count(share => share.Governing) == 1,
            "a region is divided among every ideology the game has, exactly one of them the government's");
        Check(Math.Abs(shares.Sum(share => share.Share) - 1f) < .001f,
            "a region's political shares add up to the whole of it");
        Check(shares.SequenceEqual(PoliticalMood.RegionShares(c, sample, nation)),
            "reading a region's politics is deterministic and remembers nothing");

        var governing = shares.First(share => share.Governing).Ideology;
        float GovernmentShare(float lawApproval)
        {
            sample.LawApproval = lawApproval;
            return PoliticalMood.RegionShares(c, sample, nation).First(share => share.Ideology == governing).Share;
        }
        var accepted = GovernmentShare(0.95f);
        var rejected = GovernmentShare(0.05f);
        Check(accepted > rejected,
            "a region that rejects the laws it lives under leans away from the government that wrote them");

        // The form of government is not decoration either: the same people do not end up nearest
        // the same position in a republic and under an absolute monarchy.
        var before = PoliticalMood.RegionShares(c, sample, nation).ToDictionary(share => share.Ideology, share => share.Share);
        nation.Government = "absolute_monarchy";
        var after = PoliticalMood.RegionShares(c, sample, nation).ToDictionary(share => share.Ideology, share => share.Share);
        Check(before.Keys.Any(id => Math.Abs(before[id] - after[id]) > 1e-4f),
            "the form of government the region lives in moves what it leans towards");
    }
}
