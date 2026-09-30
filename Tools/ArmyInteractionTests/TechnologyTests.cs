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

        // --- H: laboratories, stability and relationships are real research inputs ----------
        var diffusion = Campaign.CreateBootstrap(20260928);
        diffusion.AttachProvinceTopology(map);
        diffusion.Defines.ResearchDiffusion = 0f;
        var france = diffusion.NationByTag("FRA");
        var germany = diffusion.NationByTag("DEU");
        Check(CampaignDiplomacy.SharesLandBorder(diffusion, france.MapIndex, germany.MapIndex),
            "France and Germany are land neighbours in the research fixture");
        germany.Technologies.Add(root.Id);
        var acrossBorder = ResearchSystem.DiffusionFor(diffusion, france, root);
        Check(acrossBorder.World == 0f && acrossBorder.Neighbours > 0f,
            "known technology diffuses across a border even with global diffusion disabled");
        var partner = diffusion.NationByTag("USA");
        partner.Technologies.Add(root.Id);
        var agreement = CampaignDiplomacy.Get(diffusion, france.Id, partner.Id);
        agreement.TradeAgreement = true;
        var withTrade = ResearchSystem.DiffusionFor(diffusion, france, root);
        Check(withTrade.Partners > 0f && withTrade.EffectiveCost < acrossBorder.EffectiveCost,
            "a trading partner's discovery lowers the effective cost");
        if (agreement.A == france.Id) agreement.EmbargoByA = true;
        else agreement.EmbargoByB = true;
        Check(ResearchSystem.DiffusionFor(diffusion, france, root).Partners < withTrade.Partners,
            "an embargo blocks that partner's diffusion without erasing global knowledge");
        diffusion.Defines.ResearchDiffusion = 1f;
        diffusion.Defines.ResearchNeighbourDiffusion = 1f;
        diffusion.Defines.ResearchPartnerDiffusion = 1f;
        diffusion.Defines.ResearchMaximumDiscount = 0.40f;
        Check(ResearchSystem.DiffusionFor(diffusion, france, root).EffectiveCost >= root.Cost * 0.60f - 0.001f,
            "modded diffusion cannot pass the configured cost floor");
        var beforeLab = ResearchSystem.WeeklyPoints(diffusion, france);
        var frenchState = diffusion.StatesOf(france).First();
        frenchState.Buildings[BuildingKind.ResearchLab] = frenchState.Buildings.GetValueOrDefault(BuildingKind.ResearchLab) + 2;
        var afterLab = ResearchSystem.WeeklyPoints(diffusion, france);
        france.Stability = 0f;
        Check(afterLab > beforeLab && ResearchSystem.WeeklyPoints(diffusion, france) < afterLab,
            "local research labs increase pace and instability reduces it");

        // --- I: secession retains local expertise, not a flat copy of the parent tree -------
        var parent = new Nation { Population = 10_000_000 };
        var basic = TechnologyCatalog.Of("industrial_automation");
        var advanced = TechnologyCatalog.Of("advanced_semiconductors");
        var project = TechnologyCatalog.Of("digital_education");
        parent.Technologies.Add(basic.Id);
        parent.Technologies.Add(advanced.Id);
        parent.ResearchProgress[project.Id] = 30f;
        parent.CurrentResearch = project.Id;
        var richState = new ScenarioState { Population = 2_000_000, Literacy = .95f, Urbanization = .85f, Radicalism = .1f };
        richState.Buildings[BuildingKind.University] = 2;
        richState.Buildings[BuildingKind.ResearchLab] = 2;
        richState.Buildings[BuildingKind.CivilianIndustry] = 5;
        var richBreakaway = new Nation { Population = richState.Population };
        SecessionResearch.Inherit(diffusion, parent, richBreakaway, new[] { richState }, parent.Population);
        Check(richBreakaway.HasTechnology(basic.Id) && richBreakaway.HasTechnology(advanced.Id),
            "educated industrial regions keep the parent's working technologies");
        var poorState = new ScenarioState { Population = 2_000_000, Literacy = .2f, Urbanization = .1f, Radicalism = .8f };
        var poorBreakaway = new Nation { Population = poorState.Population };
        SecessionResearch.Inherit(diffusion, parent, poorBreakaway, new[] { poorState }, parent.Population);
        Check(poorBreakaway.HasTechnology(basic.Id) && !poorBreakaway.HasTechnology(advanced.Id) &&
              poorBreakaway.ResearchProgress.GetValueOrDefault(advanced.Id) > 0f,
            "less developed regions retain basic knowledge and partial advanced research");
        Check(poorBreakaway.CurrentResearch == project.Id &&
              poorBreakaway.ResearchProgress.GetValueOrDefault(project.Id) > 0f &&
              parent.ResearchProgress[project.Id] == 30f,
            "an unfinished programme can continue in the new state without deleting the parent's work");
        var inconsistentParent = new Nation { Population = parent.Population };
        inconsistentParent.Technologies.Add(advanced.Id);
        var safeBreakaway = new Nation { Population = richState.Population };
        SecessionResearch.Inherit(diffusion, inconsistentParent, safeBreakaway,
            new[] { richState }, inconsistentParent.Population);
        Check(!safeBreakaway.HasTechnology(advanced.Id) &&
              safeBreakaway.ResearchProgress.GetValueOrDefault(advanced.Id) > 0f,
            "a changed mod or old save cannot give a breakaway an orphaned advanced technology");
        france.Technologies.Add(basic.Id);
        var departingRegion = diffusion.StatesOf(france)
            .Where(state => state.Population > 300_000 && !LakeRegions.IsWaterState(state))
            .OrderByDescending(state => state.Population).First();
        var actualSecession = SecessionEvents.Force(diffusion, departingRegion);
        Check(actualSecession?.Seceded.HasTechnology(basic.Id) == true,
            "the real secession path transfers local research to the new country");
        var secessionSnapshot = SaveSystem.ExportNetworkSnapshot(diffusion, "");
        var restoredSecession = SaveSystem.ImportNetworkSnapshot(secessionSnapshot, "").Campaign;
        Check(restoredSecession?.NationByTag(actualSecession.Seceded.Tag)?.HasTechnology(basic.Id) == true,
            "a breakaway country's inherited discoveries survive a save");
    }
}
