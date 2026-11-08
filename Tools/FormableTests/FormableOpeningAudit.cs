using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>Inspect every authored project against the actual opening province map.
/// This is an audit, not a substitute for a successful post-conquest formation test.</summary>
static class FormableOpeningAudit
{
    public static void Run(string[] args)
    {
        var c = Campaign.CreateBootstrap(20261105);
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var why))
            throw new Exception(why);
        c.AttachProvinceTopology(topology);
        var before = CampaignFingerprint.Of(c);
        var errors = new List<string>();
        foreach (var document in GameDataLoader.DataDocuments("common", "formable_nations"))
            using (document) FormableNations.Validate(document.RootElement, errors);
        var projects = new List<object>();
        var ready = new List<string>();
        var territoryReady = new List<string>();
        foreach (var d in FormableNations.All.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            var unresolved = d.RequiredTags.Where(tag => FormableRestorations.Authored(c, tag).Length == 0).ToArray();
            foreach (var tag in unresolved) errors.Add(d.Id + ": missing authored territory for " + tag);
            foreach (var region in d.RequiredRegions)
                if (!PeacefulFormation.RegionProvinces(c, d with { RequiredRegions = new[] { region } }).Any())
                    errors.Add(d.Id + ": missing region territory for " + region);
            var candidates = new List<object>();
            foreach (var n in c.Nations.Where(n => n.Movement is null && FormableNations.Eligible(c, n, d)).OrderBy(n => n.Tag))
            {
                var status = FormableNations.Evaluate(c, n, d);
                if (status.CanForm) ready.Add(n.Tag + " -> " + d.Id);
                // Independent oracle for territorial readiness: a war or low stability
                // must not conceal an opening-map shortcut waiting for the first peace.
                var partners = c.Nations.Where(p => PeacefulFormation.Participates(c, n, p, d))
                    .Select(p => p.MapIndex).ToHashSet();
                bool Held(int p) => p > 0 && p <= topology.ProvinceCount && !topology.WaterByProvince[p] &&
                    partners.Contains(c.ProvinceMap.ProvinceToNation[p]) &&
                    c.ProvinceController[p] == c.ProvinceMap.ProvinceToNation[p];
                var mandate = status.Required > 0 && status.Owned >= status.Required &&
                    (d.MinimumComponentShare <= 0 || !string.IsNullOrEmpty(d.Restores) || d.RequiredTags.All(tag =>
                    {
                        var component = FormableRestorations.Authored(c, tag).Where(p => !topology.WaterByProvince[p]).ToArray();
                        return component.Length > 0 && component.Count(Held) >= MathF.Ceiling(component.Length * d.MinimumComponentShare);
                    }));
                if (mandate) territoryReady.Add(n.Tag + " -> " + d.Id);
                candidates.Add(new { tag = n.Tag, canForm = status.CanForm, owned = status.Owned,
                    total = status.Total, required = status.Required, territoryReady = mandate,
                    unmet = status.Requirements.Where(r => !r.Met).Select(r => r.Label).ToArray() });
            }
            projects.Add(new { id = d.Id, tags = d.RequiredTags, regions = d.RequiredRegions,
                requiredShare = d.RequiredShare, componentShare = d.MinimumComponentShare, unresolved, candidates });
        }
        var at = Array.IndexOf(args, "--audit-formable-opening");
        if (at + 1 < args.Length)
        {
            var destination = Path.GetFullPath(args[at + 1]);
            if (File.Exists(destination)) throw new IOException("Audit output already exists; preserve previous evidence.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.WriteAllText(destination, JsonSerializer.Serialize(new { seed = 20261105, provinces = topology.ProvinceCount,
                errors, ready, territoryReady, projects }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"FORMABLE_OPENING_AUDIT projects={projects.Count} ready={ready.Count} errors={errors.Count}");
        foreach (var row in ready) Console.WriteLine("OPENING_READY " + row);
        foreach (var row in territoryReady) Console.WriteLine("OPENING_TERRITORY_READY " + row);
        foreach (var error in errors) Console.WriteLine("CATALOGUE_ERROR " + error);
        if (CampaignFingerprint.Of(c) != before) throw new Exception("Opening audit changed the campaign.");
        Console.WriteLine("PASS opening audit is read-only");
        if (errors.Count > 0 || ready.Count > 0 || territoryReady.Count > 0)
            throw new Exception("Base catalogue contains unresolved territory or a project already secured on the opening map.");
        Console.WriteLine("PASS every authored project still needs territorial progress on the actual opening map");
    }
}
