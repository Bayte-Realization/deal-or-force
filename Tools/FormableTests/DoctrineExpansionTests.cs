using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class DoctrineExpansionTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("Doctrine expansion: " + label); Console.WriteLine("PASS doctrine expansion: " + label); }

    public static void Run()
    {
        SparseExtensions();
        AdvancedChoices();
        AiAdvancedChoices();
    }

    static void SparseExtensions()
    {
        var original = TacticalDoctrineCatalog.Find("coast");
        var old = original.Nodes.ToArray(); var revision = TacticalDoctrineCatalog.Revision;
        var errors = new List<string>();
        void Load(string body) { using var doc = JsonDocument.Parse(body); TacticalDoctrineCatalog.Merge(doc.RootElement, errors); }
        try
        {
            Load("{\"coast\":{\"append_nodes\":[{\"id\":\"mod_coast_append\",\"cost\":4,\"days\":20,\"requires\":[\"" + old[0].Id + "\"]}]}}");
            var extended = TacticalDoctrineCatalog.Find("coast");
            Check(errors.Count == 0 && extended.Nodes.Count == old.Length + 1 &&
                extended.Name == original.Name && extended.Description == original.Description &&
                extended.Experience == original.Experience && extended.Slot == original.Slot &&
                extended.AiProfiles.SequenceEqual(original.AiProfiles), "sparse append inherits the entire book metadata");
            Check(old.All(node => extended.Find(node.Id) is { } kept && kept.Name == node.Name &&
                kept.Description == node.Description && kept.Days == node.Days && kept.Cost == node.Cost &&
                kept.RushCost == node.RushCost && kept.Requires.SequenceEqual(node.Requires) &&
                kept.Excludes.SequenceEqual(node.Excludes) && kept.Modifiers.SequenceEqual(node.Modifiers) &&
                kept.AiProfiles.SequenceEqual(node.AiProfiles)), "extension preserves every prior saved article and effect");
            Check(TacticalDoctrineCatalog.Revision == revision + 1 && old.All(node => TacticalDoctrineCatalog.BranchOf(node.Id) == extended) &&
                TacticalDoctrineCatalog.BranchOf("mod_coast_append") == extended, "append commits both indexes and invalidates the retained catalogue");
            foreach (var invalid in new[]
            {
                "{\"coast\":{\"append_nodes\":[{\"id\":\"" + old[0].Id + "\",\"cost\":4,\"days\":20}]}}",
                "{\"coast\":{\"experience\":\"army\",\"append_nodes\":[{\"id\":\"bad_append\",\"cost\":4,\"days\":20}]}}",
                "{\"coast\":{\"append_nodes\":[{\"id\":\"bad_append\",\"cost\":4,\"days\":20,\"requires\":[\"unknown_article\"]}]}}",
                "{\"coast\":{\"nodes\":[],\"append_nodes\":[]}}",
                "{\"coast\":{\"append_nodes\":[]}}",
                "{\"not_a_book\":{\"append_nodes\":[{\"id\":\"bad_append\",\"cost\":4,\"days\":20}]}}",
                "{\"coast\":{\"append_nodes\":[{\"id\":\"bad_append\",\"cost\":4,\"days\":20,\"requires\":[\"bad_append\"]}]}}"
            })
            {
                errors.Clear(); var before = TacticalDoctrineCatalog.Revision;
                Load(invalid);
                Check(errors.Count == 1 && TacticalDoctrineCatalog.Find("coast") == extended &&
                    TacticalDoctrineCatalog.Revision == before && TacticalDoctrineCatalog.BranchOf("bad_append") is null,
                    "invalid sparse append is diagnosed without partial mutation: " + invalid);
            }
        }
        finally { TacticalDoctrineCatalog.ResetCatalog(); }
        Check(TacticalDoctrineCatalog.BranchOf("mod_coast_append") is null, "reset removes temporary mod additions");
    }

    static void AdvancedChoices()
    {
        var branches = TacticalDoctrineCatalog.All.ToArray();
        Check(branches.Length == 18 && branches.All(book => book.Nodes.Count >= 16), "all eighteen books contain at least sixteen articles");
        var additions = branches.SelectMany(book => book.Nodes.Where(node => node.Id.StartsWith(book.Id + "_adv_", StringComparison.Ordinal))).ToArray();
        Check(additions.Length == 144 && additions.Select(node => node.Id).Distinct().Count() == 144, "144 uniquely saved advanced articles extend the original catalogue");
        var c = Campaign.CreateBootstrap(61109); var n = c.NationByTag("POL"); c.ConfigureSinglePlayer(n.Id); c.IsPaused = true;
        var region = c.StatesOf(n).First();
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)region.MapIndex, 8192).ToArray(),
            Enumerable.Repeat((ushort)n.MapIndex, 8192).ToArray(), 128, 64, _ => false));
        var rng = c.Random.State; var clock = c.ElapsedHours;
        foreach (var branch in branches)
        {
            var nodes = branch.Nodes.Where(node => node.Id.StartsWith(branch.Id + "_adv_", StringComparison.Ordinal)).ToArray();
            var choices = nodes.Where(node => node.Excludes.Count > 0).ToArray();
            Check(nodes.Length == 8 && choices.Length == 4 && choices.All(node => node.Excludes.Count == 1 &&
                branch.Find(node.Excludes[0]).Excludes.Contains(node.Id)), branch.Id + " adds two symmetric specialisation pairs");
            foreach (var choice in choices)
            {
                Check(choice.Modifiers.Any(effect => NationalModifiers.IsGood(effect.Key, effect.Value)) &&
                    choice.Modifiers.Any(effect => !NationalModifiers.IsGood(effect.Key, effect.Value)),
                    choice.Id + " carries an actual advantage and cost");
                n.TacticalDoctrine = new TacticalDoctrineMemory();
                var track = n.TacticalDoctrine.Track(branch.Slot); track.Branch = branch.Id;
                var ancestors = new HashSet<string>(StringComparer.Ordinal);
                void Earlier(DoctrineNode node) { foreach (var id in node.Requires) if (ancestors.Add(id)) Earlier(branch.Find(id)); }
                Earlier(choice); track.Known = ancestors.OrderBy(id => id, StringComparer.Ordinal).ToList();
                n.ArmyExperience = n.AirExperience = n.NavalExperience = n.PoliticalPoints = 100000;
                NationalModifiers.Invalidate(n);
                var cap = nodes.Single(node => node.Requires.Contains(choice.Id));
                Check(TacticalDoctrines.Refusal(c, n, branch.Id, cap.Id, true).Length > 0,
                    cap.Id + " waits for its own specialisation");
                var before = choice.Modifiers.ToDictionary(effect => effect.Key, effect => NationalModifiers.Of(n, effect.Key));
                var money = TacticalDoctrines.Experience(n, branch.Experience);
                var command = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branch.Id, NodeId = choice.Id, Rush = true };
                c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
                Check(command.Refusal.Length == 0 && TacticalDoctrines.Known(n, choice.Id) &&
                    MathF.Abs(TacticalDoctrines.Experience(n, branch.Experience) - money + choice.RushCost) < .02f,
                    choice.Id + " is learned through the ordinary paid command");
                Check(choice.Modifiers.All(effect => MathF.Abs(NationalModifiers.Of(n, effect.Key) - before[effect.Key] - effect.Value) < .0001f),
                    choice.Id + " applies every positive and negative national modifier");
                var fingerprint = CampaignFingerprint.Of(c);
                var rejected = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branch.Id, NodeId = choice.Excludes[0], Rush = true };
                c.Commands.Enqueue(rejected); c.Commands.ApplyAll(c);
                Check(rejected.Refusal.Length > 0 && CampaignFingerprint.Of(c) == fingerprint,
                    choice.Id + " blocks its alternative without spending or changing the campaign");
                command = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branch.Id, NodeId = cap.Id, Rush = true };
                c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
                Check(command.Refusal.Length == 0 && TacticalDoctrines.Known(n, cap.Id), cap.Id + " opens and learns after the chosen path");
            }
        }
        Check(c.ElapsedHours == clock && c.Random.State == rng, "expanded choices preserve the paused clock and RNG");
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "advanced-doctrines"), "advanced-doctrines");
        Check(saved.Success && CampaignFingerprint.Of(saved.Campaign) == CampaignFingerprint.Of(c), "advanced article IDs survive real snapshot reload without a new save field");
    }

    static void AiAdvancedChoices()
    {
        var c = Campaign.CreateBootstrap(61110); var n = c.NationByTag("POL");
        foreach (var nation in c.Nations) nation.IsHuman = nation != n;
        // A real existing war makes the ordinary AI use a paid crash course. This
        // fixture only calls the doctrine tick, not combat or fabricated victories.
        var war = c.Wars.First(); var originalAttacker = war.AttackerId;
        war.AttackerId = n.Id;
        var random = c.Random.State;
        try
        {
            foreach (var branch in TacticalDoctrineCatalog.All)
            foreach (var wanted in branch.Nodes.Where(node => node.Id.StartsWith(branch.Id + "_adv_", StringComparison.Ordinal) && node.Excludes.Count > 0))
            {
                var opposite = branch.Find(wanted.Excludes.Single());
                var profile = wanted.AiProfiles.Keys.FirstOrDefault(id => AiStrategyCatalog.Profiles.ContainsKey(id) &&
                    wanted.AiProfiles[id] > opposite.AiProfiles.GetValueOrDefault(id, 1f));
                Check(profile is not null, wanted.Id + " has a real AI character which prefers it to the alternative");
                AiStrategyCatalog.Load("{\"country_profiles\":{\"POL\":\"" + profile + "\"}}", new List<string>());
                n.TacticalDoctrine = new TacticalDoctrineMemory(); n.StrategicDoctrine.Id = "";
                var track = n.TacticalDoctrine.Track(branch.Slot); track.Branch = branch.Id;
                for (var step = 0; step < branch.Nodes.Count; step++)
                {
                    var earlier = TacticalDoctrines.Available(n, branch.Id).FirstOrDefault(node => !node.Id.StartsWith(branch.Id + "_adv_", StringComparison.Ordinal));
                    if (earlier is null) break;
                    track.Known.Add(earlier.Id);
                }
                n.ArmyExperience = n.AirExperience = n.NavalExperience = n.PoliticalPoints = 100000;
                NationalModifiers.Invalidate(n);
                for (var day = 1; day <= 196 && !track.Known.Contains(wanted.Id); day++)
                {
                    typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);
                    c.LastTacticalDoctrineDay = -1;
                    TacticalDoctrines.Tick(c);
                }
                Check(track.Known.Contains(wanted.Id) && !track.Known.Contains(opposite.Id) &&
                    TacticalDoctrines.Experience(n, branch.Experience) < 100000,
                    profile + " actually pays for " + wanted.Id + " through the ordinary AI tick");
            }
            Check(c.Random.State == random, "all advanced AI choices preserve the existing random stream");
        }
        finally { war.AttackerId = originalAttacker; AiStrategyCatalog.ResetCatalog(); }
    }
}
