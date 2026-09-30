using System;
using System.Linq;
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
        var company = c.NationByTag("VSG");
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
    }
}
