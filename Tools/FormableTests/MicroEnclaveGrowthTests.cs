using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Why a city may not become a country.
///
/// A player watching Egypt looked at the map and saw China's whole south-west painted as
/// Macao: Sichuan, Chongqing, Yunnan, a hundred and forty million people under a flag that
/// began as six hundred thousand of them in one city. The route that did it is closed now,
/// and these checks are what keep it closed - because every one of them was a hole somebody
/// could reasonably have written.
///
/// The three holes, in the order they were found:
///   1. the population ceiling was measured against the overlord, so a subject could legally
///      grow to a fifth of a great power regardless of what it had been;
///   2. nothing bounded a micro-territory by region count, only by people;
///   3. the AI could build a package the grant would then refuse, so the refusal was the only
///      thing standing between a city and a province.
/// </summary>
static class MicroEnclaveGrowthTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("micro enclaves: " + label);
        Console.WriteLine("PASS micro enclaves: " + label);
    }

    public static void Run()
    {
        SubjectCatalog.ResetCatalog();
        DemographyCatalog.ResetCatalog();
        GovernmentCatalog.ResetCatalog();
        IdeologyCatalog.ResetCatalog();
        Loc.ResetCatalog();

        var campaign = Campaign.CreateBootstrap(20261019);
        MapRasterCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError),
            "the world map loads: " + mapError);
        campaign.AttachProvinceTopology(map);

        var chn = campaign.NationByTag("CHN");
        var mac = campaign.NationByTag("MAC");
        Check(chn is not null && mac is not null, "China and Macao are both in the world");
        Check(mac.AnswersTo(chn.Id), "and Macao answers to China");

        // ------------------------------------------------------------------ the facts ------
        var macPeople = campaign.StatesOf(mac).Sum(s => s.Population);
        Check(macPeople < 2_000_000, "Macao is a city, not a region: " + macPeople + " people");
        Check(campaign.StatesOf(mac).Length == 1, "and it holds exactly one region");
        Check(RegionalAdministration.IsMicroCityEnclave(mac), "and the game knows that is what it is");
        Check(SubjectCatalog.Of(mac.SubjectType) is { ReceivesLandGrants: false },
            "its kind of dependency cannot receive land at all");

        // Every region China holds that is not its capital, and Sichuan in particular.
        var sichuan = campaign.StatesOf(chn).FirstOrDefault(s => s.Population > 50_000_000);
        Check(sichuan is not null, "China holds a region of more than fifty million people (Sichuan)");
        var bigPackage = campaign.StatesOf(chn)
            .Where(s => s.MapIndex != chn.CapitalStateMapIndex && s.Population > 0)
            .OrderByDescending(s => s.Population)
            .Take(6)
            .Select(s => s.MapIndex)
            .ToArray();
        Check(bigPackage.Length > 0, "and there is a package to be greedy with");

        // ------------------------------------------------------------------ the holes ------
        // 1. The grant is refused outright, and says why in the player's language.
        Check(!RegionalAdministration.MayGrant(campaign, chn, mac, bigPackage, out _, out var refusal),
            "China cannot grant Macao six of its regions: " + refusal);
        Check(refusal.Length > 0, "and the refusal is a sentence, not a silence");
        Check(!RegionalAdministration.Grant(campaign, chn, mac, bigPackage, out var granted),
            "the grant command refuses too (" + granted + ")");
        Check(campaign.StatesOf(mac).Length == 1, "Macao still holds exactly one region afterwards");

        // 2. Even one region of fifty million is too many for a city of six hundred thousand.
        Check(!RegionalAdministration.MayGrant(campaign, chn, mac, new[] { sichuan.MapIndex }, out _, out var oneRefusal),
            "nor can it have Sichuan alone: " + oneRefusal);
        Check(campaign.StatesOf(mac).Sum(s => s.Population) == macPeople,
            "and not one person moved");

        // 3. The package builder will not even propose it, so the AI never tries.
        var proposed = RegionalAdministration.Package(campaign, chn, sichuan, mac);
        Check(proposed.Length == 0 || campaign.StatesOf(mac).Length + proposed.Length <=
            Math.Max(1, campaign.Defines.MicroEnclaveMaxRegions),
            "the package offered to Macao keeps it a micro-territory (" + proposed.Length + " region(s))");
        var quick = RegionalAdministration.QuickGrantPackage(campaign, chn, mac, 10);
        Check(quick.Length == 0, "and the quick-grant path offers it nothing");

        // 4. The growth ceiling is relative to the subject, which is the rule that decides the
        //    size of the thing on the map. A satellite of ten million may grow to eighty; a
        //    city of six hundred thousand may not.
        var factor = campaign.Defines.SubjectLandGrantMaxGrowthFactor;
        Check(factor is > 1f and < 64f, "the growth factor is a sane number (" + factor + ")");
        var satellite = campaign.Nations.FirstOrDefault(n => n.AnswersTo(chn.Id) && n.Id != mac.Id &&
            campaign.StatesOf(n).Sum(s => s.Population) > 5_000_000);
        if (satellite is not null)
        {
            var satellitePeople = campaign.StatesOf(satellite).Sum(s => s.Population);
            var allowed = satellitePeople * factor;
            Check(allowed > satellitePeople, "a real administration may still grow (" + satellitePeople + " -> " + allowed + ")");
        }

        // 5. And the ceiling holds for a subject of any size, not only for Macao: no subject
        //    anywhere in the world may be granted past a multiple of itself.
        foreach (var owner in campaign.Nations.Where(n => !n.IsSubject && campaign.StatesOf(n).Length >= 3).Take(12))
        {
            foreach (var subject in SubjectSystem.SubjectsOf(campaign, owner))
            {
                var people = campaign.StatesOf(subject).Sum(s => s.Population);
                if (people <= 0) continue;
                var greedy = campaign.StatesOf(owner)
                    .Where(s => s.MapIndex != owner.CapitalStateMapIndex)
                    .OrderByDescending(s => s.Population)
                    .Take(4)
                    .Select(s => s.MapIndex)
                    .ToArray();
                if (greedy.Length == 0) continue;
                var ceiling = people * Math.Clamp(campaign.Defines.SubjectLandGrantMaxGrowthFactor, 1f, 64f);
                var moved = campaign.States.Where(s => greedy.Contains(s.MapIndex)).Sum(s => s.Population);
                if (people + moved <= ceiling) continue;
                Check(!RegionalAdministration.MayGrant(campaign, owner, subject, greedy, out _, out _),
                    owner.Tag + " cannot grow " + subject.Tag + " past " + ceiling + " people");
            }
        }

        // 6. What a mod can still do: raise the ceiling, and the game obeys it.
        campaign.Defines.SubjectLandGrantMaxGrowthFactor = 100000f;
        Check(RegionalAdministration.MayGrant(campaign, chn, mac, new[] { sichuan.MapIndex }, out _, out _) ||
              RegionalAdministration.IsMicroCityEnclave(mac),
            "a mod that removes the ceiling is obeyed, and the micro-territory rule still stands");
        campaign.Defines.SubjectLandGrantMaxGrowthFactor = 8f;
    }
}
