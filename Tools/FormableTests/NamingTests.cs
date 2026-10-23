using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// What a founded country is called, and what it is called once it has settled.
///
/// The bug this is written against was on the map for as long as the game had breakaways:
/// a state was named after the city its capital happened to sit in, and kept that label
/// for the rest of the campaign. The checks below are the three questions that replace it -
/// the form politics give a name, the base the ground and people give it, and the right of
/// a player's own name to win over both.
/// </summary>
static class NamingTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("naming: " + label);
        Console.WriteLine("PASS naming: " + label);
    }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20261019);
        // A land is a shape on the map, so the map has to be there: without the topology
        // every region centre is (0, 0) and Siberia, Sloboda and Donbas cannot be recognised.
        MapRasterCatalog.ResetCatalog();
        if (MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out _))
            campaign.AttachProvinceTopology(topology);
        var previous = Loc.Language;
        try
        {
            Loc.Language = "english";

            Check(NationNaming.FormFor("theocracy", "theocratic_rule", "sunni") == "islamic_republic",
                "a Muslim theocracy is an Islamic republic");
            Check(NationNaming.FormFor("absolute_monarchy", "nationalism", "orthodox") == "kingdom",
                "a crown is a kingdom");
            Check(NationNaming.FormFor("one_party_state", "communism", "") == "people_republic",
                "a communist one-party state is a people's republic");
            Check(NationNaming.FormFor("parliamentary_republic", "liberalism", "") == "republic",
                "an ordinary republic is a republic");
            // The faith and the people decide which word a throne or an altar wears.
            Check(NationNaming.FormFor("theocracy", "theocratic_rule", "sunni", "avar") == "imamate",
                "a Muslim theocracy in the Caucasus is an imamate");
            Check(NationNaming.FormFor("absolute_monarchy", "nationalism", "sunni", "kazakh") == "khanate",
                "a Turkic throne is a khanate");
            Check(NationNaming.FormFor("absolute_monarchy", "nationalism", "sunni", "arab_gulf") == "sultanate",
                "an Arab throne is a sultanate");
            Check(NationNaming.FormFor("absolute_monarchy", "nationalism", "orthodox", "russian") == "kingdom",
                "an Orthodox throne is still a kingdom");
            // The adjective the tables hold is feminine; the form decides the ending.
            var khanateNation = new Nation
            {
                Tag = "KZK", FoundNameKind = "culture_adj", FoundNameId = "kazakh",
                FoundNameForm = "khanate"
            };
            var imamateNation = new Nation
            {
                Tag = "IMT", FoundNameKind = "culture_adj", FoundNameId = "avar",
                FoundNameForm = "imamate"
            };
            Loc.Language = "russian";
            Check(NationNaming.Render(campaign, khanateNation, false) == "Казахское ханство",
                "a khanate agrees in the neuter: " + NationNaming.Render(campaign, khanateNation, false));
            Check(NationNaming.Render(campaign, imamateNation, false) == "Аварский имамат",
                "an imamate agrees in the masculine: " + NationNaming.Render(campaign, imamateNation, false));
            Loc.Language = "english";

            var kurdistan = new Nation
            {
                Tag = "KUR", Name = "Kurdistan",
                Government = "provisional_government", Ideology = "nationalism",
                FoundNameKind = "culture", FoundNameId = "kurdish", FoundNameForm = "republic"
            };
            var named = CountryNames.Resolve(kurdistan, false);
            Check(named == "Republic of Kurdistan", "a people with a country of its own is named for it: " + named);
            Check(CountryNames.Resolve(kurdistan, true) == "Kurdistan", "the map keeps the short name: " +
                CountryNames.Resolve(kurdistan, true));

            kurdistan.CustomName = "Rojava";
            Check(CountryNames.Resolve(kurdistan, false) == "Rojava",
                "a name the player typed wins over the generated one");

            kurdistan.CustomName = "";
            kurdistan.Government = "absolute_monarchy";
            NationNaming.Settle(campaign, kurdistan, "test");
            Check(kurdistan.FoundNameForm == "kingdom" &&
                CountryNames.Resolve(kurdistan, false) == "Kingdom of Kurdistan",
                "a state that changes its politics changes its name: " + CountryNames.Resolve(kurdistan, false));

            var yemen = campaign.NationByTag("YEM");
            Check(yemen is not null, "the Yemen fixture is in the scenario");
            var houthi = new Nation
            {
                Tag = "HTH", Name = "Ansar Allah",
                Government = "theocracy", Ideology = "theocratic_rule",
                DeclaredReligion = "shia"
            };
            var victor = NationNaming.TakeoverName(campaign, houthi, yemen);
            Check(victor == "Islamic Republic of Yemen",
                "a theocratic victor names the state it took: " + victor);

            var seceded = SecessionEvents.NameFor(campaign, "kurdish", "tr_diyarbakir", "Diyarbakir", yemen);
            Check(seceded == "Republic of Kurdistan",
                "a breakaway is named for its people, not its capital: " + seceded);

            var ordinary = SecessionEvents.NameFor(campaign, "arab_yemeni", "ye_hd", "Mukalla", yemen);
            Check(ordinary != "Republic of Mukalla" &&
                  (ordinary.Contains("Yemen") || ordinary.Contains("Hadhramaut")),
                "a breakaway with no country of its own is not simply its capital: " + ordinary);

            var shan = new Nation { Id = 9990, Tag = "SHA", Name = "Shan State" };
            var west = new Nation { Id = 9991, Tag = "S07", Name = "Republic of West SHA",
                FoundNameKind = "literal", FoundNameId = "West SHA", FoundNameForm = "republic" };
            var typed = new Nation { Id = 9992, Tag = "S08", CustomName = "West SHA",
                FoundNameKind = "literal", FoundNameId = "West SHA" };
            campaign.Nations.AddRange(new[] { shan, west, typed });
            Check(NationNaming.MigrateLegacy(campaign) == 1, "legacy migration repairs generated names only");
            Check(CountryNames.Resolve(west, false) == "Republic of West Shan State",
                "a dynamic predecessor uses its country name, not SHA");
            Check(typed.CustomName == "West SHA", "migration never rewrites a player's chosen name");
            Check(NationNaming.MigrateLegacy(campaign) == 0, "legacy repair is idempotent");
            Loc.Language = "russian";
            Check(!CountryNames.Resolve(west, false).Contains("SHA") &&
                CountryNames.Resolve(west, false).Contains("Западная"),
                "directional recipe translates after an English save is opened in Russian");
            Loc.Language = "english";
            var before = CampaignFingerprint.Of(campaign);
            _ = CountryNames.Resolve(west, true);
            Check(CampaignFingerprint.Of(campaign) == before, "displaying a repaired name is read-only");
            west.FoundNameKind = "direction"; west.FoundNameId = "north:tag:MMR";
            Check(CountryNames.Resolve(west, true) == "North Myanmar", "direction recipe retains the original country's short identity");
            Check(!NationNaming.TakeoverName(campaign, houthi, west).Contains("S07"),
                "civil-war takeover of a dynamically founded country does not leak its tag");
            var directional = new Campaign(20261019);
            var predecessor = new Nation { Id=0, MapIndex=1, Tag="SHA", Name="Shan State", CapitalLongitude=80 };
            directional.Nations.Add(predecessor);
            var pixels = new ushort[24]; var owners = new ushort[24]; pixels[9]=1; owners[9]=1;
            directional.States.Add(new ScenarioState { MapIndex=1, Id="test_direction", NationMapIndex=1,
                Name="Ayeyarwady", Population=1_000_000 });
            directional.AttachProvinceTopology(ProvinceTopology.Build(pixels,owners,8,3,_=>false));
            predecessor.CapitalLongitude=80;
            var basis = NationNaming.BaseFor(directional,predecessor,new[]{1},"", "auto");
            var born = new Nation { FoundNameKind=basis.Kind, FoundNameId=basis.Id, FoundNameForm="republic" };
            // With nobody to name it for, the composer has two honest answers: the side of the
            // country the ground lies on, or the region's own name. Both are recipes the
            // reader's language applies; neither may freeze the parent's runtime tag.
            var synthetic = NationNaming.Render(directional,born,false);
            Check(basis.Kind is "direction" or "region" && !synthetic.Contains("SHA") &&
                  !NationNaming.LooksLikeKey(synthetic),
                "actual new-country generation stores a translatable name instead of the parent's runtime tag: " +
                basis.Kind + "/" + synthetic);

            // A proclaimed state with no identity row: the catalogue's own name is the answer,
            // never the key. This is the card that read "formable.gulf_union.name".
            var gulf = new Nation
            {
                Tag = "SAU", Name = "formable.gulf_union.name", FormedNationId = "gulf_union",
                Government = "provisional_government", Ideology = "nationalism"
            };
            Check(CountryNames.Resolve(gulf, false) == "Gulf Union",
                "a formed state with no identity row takes the catalogue's name: " + CountryNames.Resolve(gulf, false));
            Check(!NationNaming.LooksLikeKey(CountryNames.Resolve(gulf, false)),
                "a localisation key never reaches the country card");
            Loc.Language = "russian";
            Check(CountryNames.Resolve(gulf, false) == "Союз Залива",
                "the catalogue name is translated, not frozen in English: " + CountryNames.Resolve(gulf, false));
            Loc.Language = "english";

            // "Eastern" is a side of a country, not a country. An old save is repaired on load.
            Check(NationNaming.CompassDirection("Eastern") == "east" &&
                  NationNaming.CompassDirection("Восточная") == "east",
                "a compass word is recognised in both languages");
            var compassCampaign = new Campaign(20261019);
            var cameroon = new Nation { Id = 0, MapIndex = 1, Tag = "CMR", Name = "Cameroon" };
            compassCampaign.Nations.Add(cameroon);
            compassCampaign.States.Add(new ScenarioState
            {
                MapIndex = 1, Id = "cm_es", NationMapIndex = 1, Name = "East", Population = 1_000_000
            });
            compassCampaign.AttachProvinceTopology(ProvinceTopology.Build(pixels, owners, 8, 3, _ => false));
            var compassBorn = new Nation
            {
                Id = 9993, Tag = "S09", Name = "Republic of East", FoundNameKind = "region",
                FoundNameId = "cm_es", FoundNameBase = "East", ReleasedFromId = cameroon.Id
            };
            compassCampaign.Nations.Add(compassBorn);
            Check(NationNaming.MigrateLegacy(compassCampaign) >= 1 && compassBorn.FoundNameKind == "direction",
                "a saved region-only name is repaired into a directional one");
            Check(CountryNames.Resolve(compassBorn, false) == "Republic of East Cameroon",
                "the repaired name is anchored on the country it was a side of: " + CountryNames.Resolve(compassBorn, false));

            // A secession is one country's civil war, not two countries in a row.
            var warCampaign = new Campaign(20261019);
            var union = new Nation { Id = 0, MapIndex = 1, Tag = "SAU", Name = "Gulf Union" };
            var east = new Nation { Id = 1, MapIndex = 2, Tag = "EAS", Name = "Republic of East", SecededFromId = 0 };
            warCampaign.Nations.AddRange(new[] { union, east });
            var secessionWar = new StrategicWar
            {
                Id = 0, Name = "Gulf Union–Republic of East War", AttackerId = 0, DefenderId = 1, CivilConflict = true
            };
            warCampaign.Wars.Add(secessionWar);
            Check(WarNames.Of(warCampaign, secessionWar) == "Saudi Arabia Civil War",
                "a secession reads as one country's civil war: " + WarNames.Of(warCampaign, secessionWar));

            var third = new Nation { Id = 2, MapIndex = 3, Tag = "OMA", Name = "Oman" };
            warCampaign.Nations.Add(third);
            var storedKeyWar = new StrategicWar
            {
                Id = 1, Name = "formable.gulf_union.name–Oman War", AttackerId = 0, DefenderId = 2
            };
            warCampaign.Wars.Add(storedKeyWar);
            Check(!NationNaming.LooksLikeKey(WarNames.Of(warCampaign, storedKeyWar)),
                "a war name stored with a key is rebuilt from the countries: " + WarNames.Of(warCampaign, storedKeyWar));

            // A name that already says what kind of state it is takes no second form.
            Check(NationNaming.CarriesStateForm("United Republic of Tanzania") &&
                  !NationNaming.CarriesStateForm("Kurdistan"),
                "only a name that already carries a form of state is left alone");
            var tanzania = new Nation
            {
                Tag = "TZA", Name = "United Republic of Tanzania", FoundNameKind = "literal",
                FoundNameId = "United Republic of Tanzania", StartGovernment = "presidential_republic",
                StartIdeology = "socialism", Government = "absolute_monarchy", Ideology = "nationalism"
            };
            Check(CountryNames.Resolve(tanzania, false) == "United Republic of Tanzania",
                "a form of state is never applied twice: " + CountryNames.Resolve(tanzania, false));

            // Ukrainian region breaking away from Russia is never called "West Russia" / "South Russia".
            var russia = campaign.NationByTag("RUS");
            var kharkiv = campaign.States.Find(s => s.Id == "ua_63");
            if (russia is not null && kharkiv is not null)
            {
                var uaBase = NationNaming.BaseFor(campaign, russia, new[] { kharkiv.MapIndex }, "ukrainian", "auto");
                var uaBorn = new Nation
                {
                    Tag = "U01", FoundNameKind = uaBase.Kind, FoundNameId = uaBase.Id, FoundNameForm = "republic"
                };
                var enName = CountryNames.Resolve(uaBorn, false);
                Check(!enName.Contains("Russia", StringComparison.OrdinalIgnoreCase) &&
                      (enName.Contains("Sloboda") || enName.Contains("Ukraine")),
                    "Ukrainian region leaving Russia takes its Ukrainian land/national identity, never West/South Russia: " + enName);
                Loc.Language = "russian";
                var ruName = CountryNames.Resolve(uaBorn, false);
                Check(!ruName.Contains("Россия", StringComparison.OrdinalIgnoreCase) &&
                      (ruName.Contains("Слобожан") || ruName.Contains("Украин")),
                    "Russian localization gives idiomatic Sloboda/Ukrainian name: " + ruName);
                Loc.Language = "english";
            }

            // The circumstances of the birth decide the tone: an uprising calls itself free,
            // a region handed over by treaty keeps a plain country name. A synthetic ground in
            // the middle of the ocean makes sure no land name stands in front of the answer.
            var syntheticCampaign = new Campaign(20261019);
            var syntheticRussia = new Nation
            {
                Id = 0, MapIndex = 1, Tag = "RUS", Name = "Russia",
                CapitalLatitude = 60f, CapitalLongitude = 90f
            };
            var syntheticUkraine = new Nation { Id = 1, MapIndex = 2, Tag = "UKR", Name = "Ukraine" };
            syntheticCampaign.Nations.AddRange(new[] { syntheticRussia, syntheticUkraine });
            syntheticCampaign.States.Add(new ScenarioState
            {
                MapIndex = 1, Id = "zz_test", NationMapIndex = 1, Name = "Test Ground",
                Population = 1_000_000, CoreTags = new System.Collections.Generic.List<string> { "UKR" }
            });
            syntheticCampaign.States.Add(new ScenarioState
            {
                MapIndex = 2, Id = "zz_ukr", NationMapIndex = 2, Name = "Ukraine", Population = 1_000_000
            });
            var testPixels = new ushort[24];
            var testOwners = new ushort[24];
            testPixels[9] = 1;
            testOwners[9] = 1;
            syntheticCampaign.AttachProvinceTopology(ProvinceTopology.Build(testPixels, testOwners, 8, 3, _ => false));
            var risingName = NationNaming.BaseFor(syntheticCampaign, syntheticRussia, new[] { 1 },
                "ukrainian", "auto", rising: true);
            var treatyName = NationNaming.BaseFor(syntheticCampaign, syntheticRussia, new[] { 1 },
                "ukrainian", "auto", rising: false);
            Check(risingName.Kind == "free_tag",
                "an uprising calls itself free: " + risingName.Kind);
            Check(treatyName.Kind is "culture_adj" or "culture",
                "a treaty release keeps a country name, not a banner: " + treatyName.Kind);
            var treatyNation = new Nation
            {
                Tag = "U02", FoundNameKind = treatyName.Kind, FoundNameId = treatyName.Id,
                FoundNameForm = "republic"
            };
            Loc.Language = "russian";
            var treatyRendered = NationNaming.Render(syntheticCampaign, treatyNation, false);
            Check(treatyRendered == "Украинская Республика",
                "and it reads as a country rather than a slogan: " + treatyRendered);
            Loc.Language = "english";
            var irkutsk = campaign.States.Find(s => s.Id == "ru_irk");
            if (russia is not null && irkutsk is not null)
            {
                var sibBase = NationNaming.BaseFor(campaign, russia, new[] { irkutsk.MapIndex }, "russian", "auto");
                var sibBorn = new Nation
                {
                    Tag = "SIB", FoundNameKind = sibBase.Kind, FoundNameId = sibBase.Id, FoundNameForm = "republic"
                };
                // A macro-region names a state only when that state is most of the country
                // leaving (see LandAtlas.BestFor), so one Irkutsk oblast is Irkutsk, not
                // Siberia - and never "East Russia" either. A region's name is resolved from
                // the campaign's own region table, so the composer is asked directly.
                var sibMap = NationNaming.Render(campaign, sibBorn, true);
                Check(sibMap == "Irkutsk",
                    "a single Siberian region is named for itself, never East Russia: " + sibMap);
                Loc.Language = "russian";
                var sibRu = NationNaming.Render(campaign, sibBorn, false);
                Check(sibRu == "Иркутская Республика",
                    "and its full Russian name follows the adjective: " + sibRu);
                Loc.Language = "english";
            }
        }
        finally
        {
            Loc.Language = previous;
        }
    }
}
