using System;
using System.Linq;
using DealOrForce.Simulation;

static class FederalFractureOutcomeTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("federal fracture: " + label); Console.WriteLine("PASS federal fracture: " + label); }

    public static void Run()
    {
        try
        {
            PrimaryRegionActuallyLeaves();
            ConditionsStillMatter();
            ConnectivityAndPoliticalIdentityMatter();
            MinorityRouteStillWorks();
            SavedEligibleRegionStillLeaves();
        }
        finally { DemographyCatalog.ResetCatalog(); }
    }

    static void PrimaryRegionActuallyLeaves()
    {
        var (c, parent, region) = Fixture();
        Check(EconomySystem.ForeignShare(region, parent.Tag) == 0f && SecessionCore.IsFederalBreak(c, parent, region),
            "the eligible region consists only of the parent's own people");
        var people = c.Nations.Sum(n => n.Population);
        var groups = c.States.Sum(s => s.PopulationGroups.Sum(g => g.Size));
        var cash = parent.Treasury; var debt = parent.PublicDebt;
        var debtArrears = parent.DebtArrears; var bills = parent.PaymentArrears;
        var output = parent.Gdp; var reserve = parent.ManpowerReserve;
        var random = c.Random.State;
        SecessionEvents.Tick(c);
        Check(c.Nations.Count == 2 && region.NationMapIndex != parent.MapIndex,
            "a ripe primary-culture federal region actually becomes an independent country");
        var born = c.Nations.Single(n => n.Id != parent.Id);
        var report = SecessionEvents.Recent.Single();
        Check(report.Revolt == "federal" && born.FoundedInRevolt == "federal" &&
            report.Pieces.Single().Cause == SecessionCause.Federal,
            "the result reports federal rejection rather than inventing an ethnic minority");
        Check(born.Name != DemographyCatalog.CultureKey("fixture_own") && born.CapitalStateMapIndex == region.MapIndex,
            "a federal state is named for its region and retains its real capital");
        Check(c.Nations.Sum(n => n.Population) == people && c.States.Sum(s => s.PopulationGroups.Sum(g => g.Size)) == groups,
            "the split preserves every resident and population group");
        Check(Near(parent.Treasury + born.Treasury, cash) && Near(parent.PublicDebt + born.PublicDebt, debt) &&
            Near(parent.DebtArrears + born.DebtArrears, debtArrears) && Near(parent.PaymentArrears + born.PaymentArrears, bills),
            "cash, principal and both overdue ledgers are divided rather than duplicated or forgiven");
        Check(Near(parent.Gdp + born.Gdp, output) && Near(parent.ManpowerReserve + born.ManpowerReserve, reserve),
            "productive output and regular manpower reserves survive the split");
        Check(PeaceConference.ProvincesOfState(c.ProvinceMap, region.MapIndex).All(p => c.ProvinceMap.ProvinceToNation[p] == born.MapIndex),
            "the new border changes the actual province ownership");
        Check(c.Random.State == random, "federal founding does not consume a hidden random roll");
        var fingerprint = CampaignFingerprint.Of(c);
        var caption = PoliticalCohorts.StrainCaption(c, born);
        Check(report.Population == region.Population && report.Parent == parent && caption is not null &&
            CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random,
            "reading the outcome and political explanation changes neither world state nor random state");
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "federal"), "federal");
        Check(saved.Success, "the real snapshot codec accepts the founded state");
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        var restored = saved.Campaign.NationByTag(born.Tag);
        Check(saved.Campaign.TradeRoutesDay == c.TradeRoutesDay && saved.Campaign.TradeRoutes.Count == c.TradeRoutes.Count,
            "the saved market week is not recalibrated (expected " + c.TradeRoutesDay + "/" + c.TradeRoutes.Count +
            ", actual " + saved.Campaign.TradeRoutesDay + "/" + saved.Campaign.TradeRoutes.Count + ")");
        Check(restored?.FoundedInRevolt == "federal" && restored.PublicDebt == born.PublicDebt &&
            saved.Campaign.StatesOf(restored).Single().PoliticalDivergence == region.PoliticalDivergence &&
            CampaignFingerprint.Of(saved.Campaign) == fingerprint,
            "identity, obligations, politics and authoritative fingerprint survive save/load");
    }

    static void ConditionsStillMatter()
    {
        foreach (var disabled in new[] { "federal_fracture", "political_cohorts", "secession" })
        {
            var (c, parent, region) = Fixture();
            c.SetFeature(disabled, false);
            SecessionEvents.Tick(c);
            Check(c.Nations.Count == 1 && region.NationMapIndex == parent.MapIndex,
                "disabled " + disabled + " cannot leave through the new core-selection path");
        }
        {
            var (c, parent, region) = Fixture();
            parent.Stability = .85f; parent.ShortageIndex = 0f; parent.DebtCondition = DebtCondition.Current;
            region.StandardOfLiving = parent.StandardOfLiving;
            region.PoliticalDivergence = .1f; region.GovernmentApproval = .85f;
            SecessionEvents.Tick(c);
            Check(c.Nations.Count == 1 && region.NationMapIndex == parent.MapIndex,
                "a healthy federation does not split even with stale accumulated pressure");
        }
        {
            var (c, parent, region) = Fixture();
            region.PoliticalDivergence = -1f; region.LawApproval = -1f; region.GovernmentApproval = -1f;
            SecessionEvents.Tick(c);
            Check(c.Nations.Count == 1 && region.NationMapIndex == parent.MapIndex,
                "an unreviewed legacy region cannot bypass federal eligibility using old pressure");
        }
        {
            var (c, parent, region) = Fixture();
            region.PoliticalDivergence = .1f;
            SecessionEvents.Tick(c);
            Check(c.Nations.Count == 1, "poverty and default alone do not create a federal identity");
        }
    }

    static void MinorityRouteStillWorks()
    {
        var (c, parent, region) = Fixture();
        c.SetFeature("federal_fracture", false);
        c.SetFeature("political_cohorts", false);
        region.PopulationGroups.Single().Culture = "fixture_foreign";
        Check(EconomySystem.ForeignShare(region, parent.Tag) == 1f, "the minority control is genuinely foreign");
        SecessionEvents.Tick(c);
        Check(c.Nations.Count == 2 && region.NationMapIndex != parent.MapIndex &&
            SecessionEvents.Recent.Single().Pieces.Single().Cause == SecessionCause.Nationality,
            "the established minority secession route remains independent of the federal switches");
    }

    static void ConnectivityAndPoliticalIdentityMatter()
    {
        var minimum = SecessionCore.Weights.MinimumRegions;
        try
        {
            SecessionCore.Weights.MinimumRegions = 2;
            {
                var (c, parent, _) = Fixture();
                RejectCentre(c.States[3]);
                SecessionEvents.Tick(c);
                Check(c.Nations.Count == 1,
                    "two disconnected ripe federal regions cannot be bundled to bypass the minimum-region rule");
            }
            {
                var (c, parent, _) = Fixture();
                var neighbour = c.States[2];
                RejectCentre(neighbour);
                neighbour.PopulationGroups.Single().Culture = "fixture_second_primary";
                parent.FoundedCultures.Add("fixture_second_primary");
                DemographyCatalog.Declare(parent.Tag, parent.FoundedCultures);
                SecessionEvents.Tick(c);
                Check(c.Nations.Count == 2 && SecessionEvents.Recent.Single().Pieces.Count == 2 &&
                    SecessionEvents.Recent.Single().Pieces.All(piece => piece.Cause == SecessionCause.Federal),
                    "connected political regions of different primary peoples can found one federal state");
                Check(c.Nations.Last().Population <= 10_000_000 * SecessionCore.Weights.MaximumShare,
                    "the federal bloc uses the existing population cap rather than partitioning the whole country");
            }
        }
        finally { SecessionCore.Weights.MinimumRegions = minimum; }
    }

    static void RejectCentre(ScenarioState state)
    {
        state.Stability = .25f; state.Radicalism = .55f; state.StandardOfLiving = .35f;
        state.PoliticalDivergence = .75f; state.GovernmentApproval = .25f; state.LawApproval = .3f;
        state.SecessionPressure = 1.25f;
    }

    static void SavedEligibleRegionStillLeaves()
    {
        var (c, parent, region) = Fixture();
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "before-federal"), "before-federal");
        Check(saved.Success, "a ripe political region saves before the border moves");
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        SecessionEvents.Tick(saved.Campaign);
        Check(saved.Campaign.Nations.Count == 2 &&
            saved.Campaign.States.Single(s => s.MapIndex == region.MapIndex).NationMapIndex != parent.MapIndex &&
            SecessionEvents.Recent.Single().Revolt == "federal",
            "restoring a ripe primary region neither loses nor invents the federal outcome");
    }

    static (Campaign Campaign, Nation Parent, ScenarioState Region) Fixture()
    {
        var c = new Campaign(613, new Defines()) { ScenarioId = "federal_outcome_fixture" };
        var parent = c.AddNation(new Nation { Tag = "FFX", Name = "Federal fixture", MapIndex = 501,
            // A current campaign has calibrated output; zero requests a legacy market
            // calibration on load, which would deliberately settle a different week.
            Population = 10_000_000, Gdp = 100, OutputScale = 1f, Treasury = 90, PublicDebt = 60,
            DebtArrears = 6, PaymentArrears = 3, DebtInitialized = true, DebtCondition = DebtCondition.Default,
            ManpowerReserve = 300_000, Stability = .32f, StandardOfLiving = .7f, ShortageIndex = .4f,
            CapitalStateMapIndex = 3001, LastSecessionDay = -9999 });
        parent.FoundedCultures.Add("fixture_own");
        DemographyCatalog.Declare(parent.Tag, parent.FoundedCultures);
        for (var i = 0; i < 4; i++)
        {
            var people = i == 0 ? 6_000_000 : i == 1 ? 2_000_000 : 1_000_000;
            var state = new ScenarioState { MapIndex = 3001 + i, NationMapIndex = parent.MapIndex,
                NationId = parent.Tag, Id = "federal_fixture_" + i, Name = "Federal region " + i, Population = people,
                Stability = i == 1 ? .25f : .95f, Radicalism = i == 1 ? .55f : 0f,
                StandardOfLiving = i == 1 ? .35f : .7f, PoliticalDivergence = i == 1 ? .75f : .1f,
                GovernmentApproval = i == 1 ? .25f : .8f, LawApproval = i == 1 ? .3f : .8f,
                SecessionPressure = i == 1 ? 1.25f : 0f };
            state.PopulationGroups.Add(new PopulationGroup { Culture = "fixture_own", Size = people });
            c.States.Add(state);
        }
        var cells = Enumerable.Range(0, 80).Select(i => (ushort)(3001 + i % 20 / 5)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, Enumerable.Repeat((ushort)501, 80).ToArray(), 20, 4, _ => false));
        c.ConfigureSinglePlayer(parent.Id);
        SovereignFinance.NormalizeAccounts(c);
        // Capitals are coordinates on the actual raster, including after save/load.
        var capital = c.ProvinceMap.CenterOf(PeaceConference.ProvincesOfState(c.ProvinceMap, 3001).First());
        parent.CapitalLatitude = capital.Latitude; parent.CapitalLongitude = capital.Longitude;
        c.LocateCapitals();
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 7L * 24);
        return (c, parent, c.States[1]);
    }

    static bool Near(float value, float expected) => Math.Abs(value - expected) < .001f;
}
