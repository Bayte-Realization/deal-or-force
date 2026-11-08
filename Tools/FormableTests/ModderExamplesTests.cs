using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Modding;
using DealOrForce.Simulation;

/// <summary>Executable examples referenced by docs/MODDER_CODE_MAP_RU.md.
/// Keep examples here when changing the mod contract; comments alone do not prove it.</summary>
static class ModderExamplesTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("modder example: " + label); Console.WriteLine("PASS modder example: " + label); }

    static ScriptedEffects.Effect Effect(string text, string path = "events/guide.yml")
    {
        using var document = StructuredData.ParseDocument(path, text);
        var errors = new List<string>();
        var result = ScriptedEffects.Parse(document.RootElement, path, errors);
        Check(errors.Count == 0, "example parses without diagnostics: " + path);
        return result;
    }

    public static void Run()
    {
        var (c, nation, other) = TerritorialRecoveryTests.World();
        nation.Treasury = 100; nation.Stability = .5f;
        var effect = Effect("treasury_percent: -0.02\nstability: 0.018\n");
        effect.Apply(c, nation);
        Check(Math.Abs(nation.Treasury - 98) < .00001, "-0.02 percent modifier spends 2 percent of positive treasury");
        Check(Math.Abs(nation.Stability - .518f) < .00001, "bare 0.018 adds 1.8 percentage points, not 1.8 percent");
        nation.Stability = .5f;
        Effect("stability_percent: 0.02\n").Apply(c, nation);
        Check(Math.Abs(nation.Stability - .51f) < .00001, "percent suffix scales the original share");
        Effect("stability_set: 2\n").Apply(c, nation);
        Check(nation.Stability == .98f, "explicit assignment respects the stability effect's 10–98 percent limits");

        foreach (var region in c.States) region.Radicalism = .5f;
        Effect("state.radicalism: -0.01\n").Apply(c, nation);
        Check(c.StatesOf(nation).All(s => Math.Abs(s.Radicalism - .49f) < .00001), "state prefix changes every owned region");
        Check(c.StatesOf(other).All(s => s.Radicalism == .5f), "state prefix never changes the neighbour's regions");

        var root = Path.Combine(Path.GetTempPath(), "dof-guide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "common", "scripted_effects"));
        try
        {
            File.WriteAllText(Path.Combine(root, "common", "scripted_effects", "90_guide.yml"),
                "guide_double:\n  treasury_mult: 2\n");
            var errors = new List<string>(); ScriptLibrary.LoadDirectories(root, errors);
            Check(errors.Count == 0, "named YAML library loads through the real package reader");
            nation.Treasury = 100;
            Effect("use: [guide_double]\ntreasury_set: 5\n").Apply(c, nation);
            Check(nation.Treasury == 5, "use first lets a later set override its numeric result");
            nation.Treasury = 100;
            Effect("treasury_set: 5\nuse: [guide_double]\n").Apply(c, nation);
            Check(nation.Treasury == 10, "use expands in place instead of being silently moved to the front");
            nation.Treasury = 100;
            Effect("{\"treasury_set\":5,\"use\":[\"guide_double\"]}", "events/guide.json").Apply(c, nation);
            Check(nation.Treasury == 10, "JSON and YAML preserve the same effect ordering");
        }
        finally
        {
            ScriptLibrary.ResetCatalog();
            // Exact, owned GUID directory, never a mod directory or a user's workspace.
            Directory.Delete(root, recursive: true);
        }

        using (var document = StructuredData.ParseDocument("events/guide.yml", "min_stability: 0.45\nmax_war_support: 0.8\nat_war: false\n"))
        {
            var errors = new List<string>();
            var trigger = ScriptedTrigger.Parse(document.RootElement, "guide trigger", errors);
            nation.Stability = .5f; nation.WarSupport = .75f;
            var before = CampaignFingerprint.Of(c); var random = c.Random.State;
            Check(errors.Count == 0 && trigger.Matches(c, nation), "all numeric bounds and the peaceful condition hold together");
            Check(before == CampaignFingerprint.Of(c) && random == c.Random.State, "trigger evaluation reads without spending or drawing randomness");
            nation.WarSupport = .9f;
            Check(!trigger.Matches(c, nation), "one failed bound rejects the trigger");
        }
        using (var document = StructuredData.ParseDocument("events/guide.yml", "not_a_real_effect: 1\n"))
        {
            var errors = new List<string>();
            var bad = ScriptedEffects.Parse(document.RootElement, "events/guide.yml", errors);
            Check(bad.IsEmpty && errors.Any(e => e.Contains("events/guide.yml") && e.Contains("not_a_real_effect")),
                "unknown fields report their content path instead of acting as successful examples");
        }
        nation.Treasury = 100;
        var queued = ModOperations.QueueDebt(c, nation.Tag, "borrow", .1);
        Check(queued && c.Commands.Count == 1 && nation.Treasury == 100, "operation true means queued, not already paid or applied");
        Check(ModScalarAccess.SetNationNumber(c, nation.Tag, "treasury", 12) && nation.Treasury == 12,
            "legacy scalar setter is immediate, unlike the queued operation");
        Check(!ModScalarAccess.SetNationNumber(c, nation.Tag, "treasury", double.NaN) && nation.Treasury == 12,
            "a rejected scalar does not partially write an invalid number");
    }
}
