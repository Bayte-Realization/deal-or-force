using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Research (item 62): points from an economy and its schools, technologies that wait for what
/// they require, finish, and do what they say; a ministry that chooses when a player has not;
/// diffusion; and all of it surviving a save.
/// </summary>
static class TechnologyTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var problems = TechnologyCatalog.Problems();
        Check(TechnologyCatalog.All.Count >= 10 && problems.Count == 0,
            $"{TechnologyCatalog.All.Count} technologies load, every prerequisite and effect naming something real " +
            $"({string.Join("; ", problems)})");

        var c = Campaign.CreateBootstrap(20260927);
        c.AttachProvinceTopology(map);
        var usa = c.NationByTag("USA");
        var mng = c.NationByTag("MNG");
        c.ConfigureSinglePlayer(mng.Id);

        // --- A: what a week of research is ---------------------------------------------------
        var big = ResearchSystem.WeeklyPoints(c, usa);
        var small = ResearchSystem.WeeklyPoints(c, mng);
        Check(big > small * 5 && small >= c.Defines.ResearchBasePerWeek,
            $"the United States researches far more than Mongolia ({big:0.0} against {small:0.00} a week)");
        var schools = usa.EducationSpending;
        usa.EducationSpending = schools * 2f;
        var more = ResearchSystem.WeeklyPoints(c, usa);
        usa.EducationSpending = schools;
        Check(more > big, $"spending more on schools researches more ({more:0.0})");

        // --- B: what a technology needs first ------------------------------------------------
        var root = TechnologyCatalog.All.First(technology => technology.Requires.Count == 0);
        var child = TechnologyCatalog.All.First(technology => technology.Requires.Contains(root.Id));
        Check(ResearchSystem.Available(usa, root) && !ResearchSystem.Available(usa, child),
            $"{child.Id} waits for {root.Id}");

        // --- C: researched, and then it is the country's --------------------------------------
        c.IsPaused = false;
        usa.CurrentResearch = root.Id;
        var weeks = 0;
        while (!usa.HasTechnology(root.Id) && weeks < 80)
        {
            for (var day = 0; day < 7; day++) c.TickDay();
            weeks++;
        }
        Check(usa.HasTechnology(root.Id), $"the United States researched {root.Id} in {weeks} week(s)");
        Check(ResearchSystem.Available(usa, child), $"and {child.Id} can be started now");
        foreach (var (effect, share) in root.Effects)
            Check(MathF.Abs(ResearchSystem.Bonus(usa, effect) - share) < 0.00001f,
                $"and {effect} {share:+0.##;-0.##} is the country's");

        // --- D: a player who chose nothing still researches -----------------------------------
        Check(!string.IsNullOrEmpty(mng.CurrentResearch) && mng.ResearchPerWeek > 0f,
            $"a player's ministry chose {mng.CurrentResearch} when the player had not");

        // --- E: the world having it makes it cheaper --------------------------------------------
        Check(ResearchSystem.CostFor(c, mng, root) < root.Cost,
            $"{root.Id} costs Mongolia {ResearchSystem.CostFor(c, mng, root):0} of {root.Cost:0}, since the United States has it");

        // --- F: choosing, and switching without losing anything ---------------------------------
        var options = ResearchSystem.AvailableTo(mng).Take(2).ToList();
        Check(options.Count == 2, "Mongolia has two things it could start");
        mng.CurrentResearch = options[0].Id;
        mng.ResearchProgress[options[0].Id] = 5f;
        var choose = new SetResearchCommand { IssuerId = mng.Id, TechnologyId = options[1].Id };
        choose.Apply(c);
        Check(choose.Set && mng.CurrentResearch == options[1].Id && MathF.Abs(mng.ResearchProgress[options[0].Id] - 5f) < 0.0001f,
            "switching keeps what was put into the last choice");
        var locked = TechnologyCatalog.All.First(technology => !ResearchSystem.Available(mng, technology) && !mng.HasTechnology(technology.Id));
        var refuse = new SetResearchCommand { IssuerId = mng.Id, TechnologyId = locked.Id };
        refuse.Apply(c);
        Check(!refuse.Set && mng.CurrentResearch == options[1].Id, $"{locked.Id} cannot be chosen before what it requires");

        // --- G: through a save ---------------------------------------------------------------
        var bytes = SaveSystem.ExportNetworkSnapshot(c, "");
        var back = SaveSystem.ImportNetworkSnapshot(bytes, "").Campaign;
        Check(back is not null && back.NationByTag("USA").HasTechnology(root.Id) &&
              back.NationByTag("MNG").CurrentResearch == options[1].Id &&
              MathF.Abs(back.NationByTag("MNG").ResearchProgress.GetValueOrDefault(options[0].Id) - 5f) < 0.0001f,
            "what is known, what is chosen and what is half done all survive a save");
    }
}
