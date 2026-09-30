using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class Movement82Tests
{
    public static void Run(Campaign c)
    {
        void Check(bool ok, string label)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
            if (!ok) throw new Exception(label);
        }
        // The base game's world is the real one, and no company in it would be: it ships none.
        Check(c.NationByTag("VSG") is null && MovementCatalog.All.All(m => !m.Has("for_hire")),
            "the base game founds no private military company");
        // The one in mods/_templates, loaded the way a mod is: its movement merged into the
        // catalogue, then founded on a campaign that has not started.
        var file = Path.Combine(Templates(), "private_military_company", "common", "movements",
            "50_private_military_company.json");
        using (var document = JsonDocument.Parse(File.ReadAllText(file),
                   new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
        {
            var errors = new List<string>();
            MovementCatalog.Merge(document.RootElement, errors);
            Check(errors.Count == 0, "the template's company reads without complaint " + string.Join("; ", errors));
        }
        Movements.Found(c);
        var company = c.NationByTag("VSG");
        Check(company?.Movement is not null, "and a new campaign founds it");
        var target = c.States.First(s => s.Id == "ua_14");
        company.Treasury = 100;
        company.Movement.Cadres = 100000;
        target.Stability = 0;
        var owner = target.NationMapIndex;
        var treasury = company.Treasury;
        var cadres = company.Movement.Cadres;
        Console.WriteLine($"VSG rising in {target.DisplayName}: chance={MovementRules.UprisingChance(c, company, target):P0}, refusal='{MovementActions.Refusal(c, company, "rise", target.MapIndex, -1)}'");
        Check(MovementActions.Refusal(c, company, "rise", target.MapIndex, -1).Length > 0,
            "ordinary contractor cannot seize a remote destabilised region");
        Check(MovementRules.UprisingChance(c, company, target) == 0, "preview cannot advertise a forbidden uprising");
        new MovementActionCommand { IssuerId = company.Id, Action = "rise", StateMapIndex = target.MapIndex }.Apply(c);
        Check(target.NationMapIndex == owner && company.Treasury == treasury && company.Movement.Cadres == cadres,
            "forged or stale rise command changes neither territory nor resources");
        Check(!MovementRules.Rise(c, company, target) && company.Treasury == treasury,
            "internal rising entry point also respects contractor restriction");
        Check(MovementActions.For(company).All(action => action.Id != "rise") &&
              MovementActions.For(company).Any(action => action.Id == "contract"),
            "company menu offers contracts, not a dead uprising button");
        var snapshot = (ushort[])c.ProvinceController.Clone();
        c.Defines.MovementAiUprisingAfterDays = 0;
        c.Defines.MovementAiUprisingChance = 0;
        c.Defines.MovementAiUprisingAppetite = 1;
        foreach (var n in c.Nations) n.IsHuman = n.Id != company.Id;
        for (var week = 1; week <= 104; week++)
        {
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)week * c.Defines.EconomyTickDays * 24);
            MovementAi.Tick(c);
        }
        Check(c.StatesOf(company).Length == 0 && snapshot.SequenceEqual(c.ProvinceController),
            "two years of aggressive movement-AI turns cannot give VSG provinces");
        var definition = Movements.DefinitionOf(company);
        try
        {
            definition.Traits.Add("territorial_ambition");
            Check(MovementActions.For(company).Any(action => action.Id == "rise"), "explicit mod trait restores conquest option");
        }
        finally { definition.Traits.Remove("territorial_ambition"); }
        var exile = c.NationByTag("CTA");
        Check(MovementActions.For(exile).Any(action => action.Id == "rise"), "government in exile retains political uprising");
        MovementCatalog.ResetCatalog();
    }

    /// <summary>The mod templates, wherever the tests were started from.</summary>
    static string Templates()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "mods", "_templates");
            if (Directory.Exists(candidate)) return candidate;
        }
        var here = Path.Combine(Directory.GetCurrentDirectory(), "mods", "_templates");
        if (Directory.Exists(here)) return here;
        throw new Exception("mods/_templates not found");
    }
}
