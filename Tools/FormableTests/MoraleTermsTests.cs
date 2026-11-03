using System;
using System.Linq;
using DealOrForce.Simulation;

static class MoraleTermsTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception("morale terms: " + message); Console.WriteLine("PASS morale terms: " + message); }
    public static void Run()
    {
        var language = Loc.Language;
        var keys = new[] { "Organisation", "Organisation recovery", "Organisation of every division",
            "Observed organisation", "Organisation is spent", "Organisation: {0:P0}", "Organisation: {0:P0} of {1:P0}",
            "Organisation {0:P0} · equipment {1:P0}", "Organisation {0:P0} · equipment {1:P0} · supply {2:P0}",
            "Condition bars: organisation · strength · equipment", "Personnel {0:N0} · organisation {1:P0}",
            "Waiting for organisation: {0:N0}", "organisation below a third", "handbook.army.body", "handbook.supply.body",
            "How fast a division out of battle gets its organisation back.", "Divisions weighted by strength, organisation and supply" };
        try
        {
            foreach (var locale in new[] { "english", "russian" })
            {
                Loc.Language = locale;
                foreach (var key in keys)
                {
                    var text = Loc.TextIn(locale, key);
                    Check(text is not null && (locale == "english" ? text.Contains("morale", StringComparison.OrdinalIgnoreCase) : text.Contains("боев", StringComparison.OrdinalIgnoreCase)),
                        "military caption keeps old key but displays morale: " + locale + "/" + key);
                    Check(Loc.Placeholders(key).SetEquals(Loc.Placeholders(text)), "caption keeps formatting arguments: " + key);
                }
                Check(Loc.Get("Shanghai Cooperation Organisation").Contains(locale == "english" ? "Organisation" : "организац", StringComparison.OrdinalIgnoreCase),
                    "international organizations are not relabelled as morale");
                Check(Loc.Get("How fast a division out of battle gets its organisation back.").IndexOf(locale == "english" ? "organisation" : "организац", StringComparison.OrdinalIgnoreCase) < 0,
                    "modifier explanation uses the new vocabulary");
            }
            var (c, n, _) = TerritorialRecoveryTests.World();
            c.ConfigureSinglePlayer(n.Id);
            var army = new Army { Id = 81812, NationId = n.Id, Organisation = .3f, Divisions = 1, ProvinceId = 1 };
            c.Armies.Add(army);
            ScriptedEffects.From(("army.organisation", .1f)).Apply(c, n);
            Check(Math.Abs(army.Organisation - .4f) < .00001, "legacy scripted-effect key still changes the same military field");
            var snapshot = SaveSystem.ExportNetworkSnapshot(c, "morale compatibility");
            var restored = SaveSystem.ImportNetworkSnapshot(snapshot, "morale compatibility");
            Check(restored.Success && Math.Abs(restored.Campaign.Armies.Single(a => a.Id == army.Id).Organisation - army.Organisation) < .00001,
                "legacy saved military state roundtrips without a field rename: " + restored.Warning);
            var before = CampaignFingerprint.Of(c);
            Loc.Language = "english"; _ = Loc.Get("Organisation"); Loc.Language = "russian"; _ = Loc.Get("Organisation");
            Check(CampaignFingerprint.Of(c) == before, "terminology does not alter combat state or simulation fingerprint");
        }
        finally { Loc.Language = language; }
    }
}
