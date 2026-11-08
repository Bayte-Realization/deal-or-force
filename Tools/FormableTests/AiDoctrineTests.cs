using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A country's character: how readily it attacks and how readily it makes friends.
///
/// Both are read from the same profile the operational doctrine comes from, so a mod that
/// moves one country from one character to another moves all of its behaviour at once - and
/// both have to be real numbers the simulation reads, not a label in a table.
/// </summary>
static class AiDoctrineTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("ai doctrine: " + label); Console.WriteLine("PASS ai doctrine: " + label); }

    public static void Run()
    {
        AiStrategyCatalog.ResetCatalog();

        // The shipped files have to load without a single complaint, because a profile that
        // quietly failed to parse is a country that quietly lost its character.
        var errors = new List<string>();
        foreach (var file in GameDataLoader.DataFiles("common", "ai_strategy"))
            AiStrategyCatalog.Load(StructuredData.ReadJson(file), errors);
        Check(errors.Count == 0, "every shipped AI profile loads: " + string.Join(" | ", errors));

        var profiles = AiStrategyCatalog.Profiles;
        var expected = new[]
        {
            "default", "major_offensive", "defensive", "minor_power",
            "blitzkrieg", "attrition", "fortress", "expeditionary",
            "garrison_state", "trading_state", "irredentist", "isolated"
        };
        Check(expected.All(profiles.ContainsKey),
            "the shipped doctrines and the new characters all exist: " +
            string.Join(", ", expected.Where(id => !profiles.ContainsKey(id))));

        // The two new fields are ordered the way the profiles describe themselves. If they were
        // decoration, every one of these comparisons would be a coin toss.
        Check(profiles["fortress"].Aggression < profiles["default"].Aggression &&
              profiles["default"].Aggression < profiles["blitzkrieg"].Aggression,
            "aggression runs cautious, ordinary, reckless in that order");
        Check(profiles["isolated"].Sociability < profiles["default"].Sociability &&
              profiles["default"].Sociability < profiles["trading_state"].Sociability,
            "sociability runs hermit, ordinary, trading state in that order");
        Check(profiles["trading_state"].Aggression < 1f && profiles["irredentist"].Aggression > 1f,
            "a trading state is more reluctant than the ordinary case and an irredentist less");

        // The force shares still have to be a plan rather than a pile, for every profile.
        Check(profiles.Values.All(p => p.SpearheadShare <= p.AdvanceShareAttacker &&
            p.AdvanceShareAttacker + p.DefendShare <= 1.05f),
            "every profile's force shares remain a plan the commands can execute");

        // A country's character is read by tag, and a tag nothing assigned keeps its posture.
        Check(AiStrategyCatalog.ProfileOf("CHE") == "trading_state" &&
              AiStrategyCatalog.ProfileOf("PRK") == "fortress" &&
              AiStrategyCatalog.ProfileOf("ZZZ") is null,
            "a country's character is read by tag, and an unlisted one is left to its army");

        // And it is what the simulation actually answers for that country, not only a table.
        var campaign = Campaign.CreateBootstrap(311);
        var swiss = campaign.NationByTag("CHE");
        Check(swiss is not null && AiStrategyCatalog.Of(campaign, swiss).Id == "trading_state",
            "the character a country was assigned is what the simulation resolves for it");
        Check(AiStrategyCatalog.Of(campaign, null) is not null,
            "asking about no country at all answers the ordinary case rather than nothing");

        // Malformed data is diagnosed rather than silently ignored, and the profile it was
        // trying to change keeps the value it had.
        var before = profiles["blitzkrieg"].Aggression;
        var bad = new List<string>();
        AiStrategyCatalog.Load(
            "{\"profiles\":{\"blitzkrieg\":{\"aggression\":\"loud\"}},\"country_profiles\":{\"ZZZ\":\"no_such_profile\"}}", bad);
        Check(bad.Count == 1 && bad[0].Contains("no_such_profile") && bad[0].Contains("ZZZ") &&
              Math.Abs(profiles["blitzkrieg"].Aggression - before) < 0.0001f,
            "an unknown profile reference is reported by country and by name, and the profile is left as it was");

        // Out-of-range numbers are clamped rather than trusted: a mod cannot make a country
        // that never stops attacking, or one that never speaks to anybody.
        AiStrategyCatalog.Load("{\"profiles\":{\"blitzkrieg\":{\"aggression\":99,\"sociability\":0}}}", bad);
        Check(profiles["blitzkrieg"].Aggression <= 2.5f && profiles["blitzkrieg"].Sociability >= 0.25f,
            "a character's numbers are clamped to a range the simulation can live with");

        AiStrategyCatalog.ResetCatalog();
        OperationalBooks();
    }

    static void OperationalBooks()
    {
        var choices = new[]
        {
            ("defensive", "elastic_defence", "elastic_mobile"),
            ("minor_power", "dispersed_warfare", "dispersed_partisans"),
            ("fortress", "air_shield", "shield_front"),
            ("trading_state", "convoy_service", "convoy_assembly"),
            ("blitzkrieg", "mission_command", "mission_initiative"),
            ("major_offensive", "joint_logistics", "joint_lean")
        };
        try
        {
            foreach (var (profile, id, specialisation) in choices)
            {
                // The same country can use every book by changing authored character data:
                // the production selector has no country/era allowlist.
                AiStrategyCatalog.Load("{\"country_profiles\":{\"ROU\":\"" + profile + "\"}}", new List<string>());
                var c = Campaign.CreateBootstrap(1730);
                var n = c.NationByTag("ROU");
                foreach (var other in c.Nations) other.IsHuman = other != n;
                n.ArmyExperience = n.AirExperience = n.NavalExperience = n.PoliticalPoints = 5000;
                n.StrategicDoctrine.Id = "";
                var branch = TacticalDoctrineCatalog.Find(id);
                Check(TacticalDoctrines.PreferredBranch(c, n, branch.Slot) == id,
                    profile + " selects the data-authored " + id + " book");
                var random = c.Random.State;
                for (var day = 1; day <= 500; day++)
                {
                    typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);
                    TacticalDoctrines.Tick(c);
                }
                var track = TacticalDoctrines.TrackFor(n, branch);
                Check(track is not null && track.Branch == id && track.Known.Contains(specialisation),
                    profile + " actually pays for and learns " + specialisation + " through the normal AI tick");
                var selected = branch.Find(specialisation);
                Check(selected.Excludes.All(alternative => !track.Known.Contains(alternative)),
                    profile + " never learns the mutually exclusive alternative");
                Check(c.Random.State == random && TacticalDoctrines.Experience(n, branch.Experience) < 5000,
                    id + " spends experience but consumes no extra RNG");
            }

            // A grand strategy remains authoritative for the primary book, but completing it
            // must not make authored new books unreachable to a normal AI campaign.
            AiStrategyCatalog.Load("{\"country_profiles\":{\"ROU\":\"defensive\"}}", new List<string>());
            var campaign = Campaign.CreateBootstrap(1731);
            var nation = campaign.NationByTag("ROU");
            foreach (var other in campaign.Nations) other.IsHuman = other != nation;
            nation.ArmyExperience = 5000;
            nation.PoliticalPoints = 5000;
            nation.StrategicDoctrine.Id = "attrition_war";
            var first = TacticalDoctrineCatalog.Find("fire");
            var primary = nation.TacticalDoctrine.Track(DoctrineSlots.Ground);
            primary.Branch = first.Id;
            primary.Known = new List<string>();
            // A complete reachable path, not contradictory knowledge of both sides of
            // a new data-authored specialisation. The AI may then open a companion.
            for (var step = 0; step < first.Nodes.Count && TacticalDoctrines.Available(nation, first.Id).FirstOrDefault() is { } next; step++)
                primary.Known.Add(next.Id);
            var completedArticles = primary.Known.Count;
            Check(TacticalDoctrines.PreferredBranch(campaign, nation) == "fire",
                "the written strategic bias still wins for the primary ground book");
            for (var day = 1; day <= 500; day++)
            {
                typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);
                TacticalDoctrines.Tick(campaign);
            }
            Check(primary.Branch == "fire" && primary.Known.Count == completedArticles &&
                  primary.Companion is { Branch: "elastic_defence" } second && second.Known.Contains("elastic_mobile"),
                "an AI completes its strategic book then learns an authored companion without deleting prior knowledge");
        }
        finally { AiStrategyCatalog.ResetCatalog(); }
    }
}
