using System;
using System.Linq;
using DealOrForce.Simulation;

static class UnionCohesionMutationTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("union secession: " + text); Console.WriteLine("PASS union secession: " + text); }

    static Campaign Fixture()
    {
        var c = new Campaign(20261018);
        for (var id = 0; id < 2; id++)
            c.AddNation(new Nation { Tag = "UCT" + id, MapIndex = 501 + id,
                Name = "Union " + id, Population = 3000000, Gdp = 30, Treasury = 30,
                ManpowerReserve = 300000, Stability = id == 0 ? .2f : .9f,
                FormedNationId = "union_state", FormedNationDay = 0,
                UnionCohesion = id == 0 ? .1f : .6f });
        var regions = new ushort[240]; var owners = new ushort[240];
        for (var i = 0; i < 6; i++)
        {
            var owner = c.Nations[i / 3];
            var s = new ScenarioState { MapIndex = 3001 + i, Id = "union_mutation_" + i,
                Name = "Region " + i, NationMapIndex = owner.MapIndex, NationId = owner.Tag,
                Population = 1000000, Stability = .8f, Radicalism = i == 1 ? .95f : .1f };
            s.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = s.Population });
            c.States.Add(s);
        }
        for (var y = 0; y < 4; y++)
        for (var x = 1; x < 59; x++)
        {
            var i = Math.Min(5, x / 10);
            regions[y * 60 + x] = (ushort)(3001 + i);
            owners[y * 60 + x] = (ushort)(501 + i / 3);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(regions, owners, 60, 4, state => state == 0));
        c.ConfigureSinglePlayer(0);
        c.Nations[0].CapitalStateMapIndex = 3001;
        c.Nations[1].CapitalStateMapIndex = 3004;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 200L * 24);
        return c;
    }

    public static void Run()
    {
        var c = Fixture(); var parent = c.Nations[0]; var calm = c.Nations[1];
        Check(c.ProvinceMap is not null && c.StatesOf(parent).Length == 3,
            "a real small province map exercises the founding path, not a no-map skip");
        var initialCohesion = calm.UnionCohesion;
        var expectedCalm = initialCohesion +
            (c.Defines.UnionCohesionRecoveryWeekly - .1f * c.Defines.UnionCohesionRadicalismWeekly) / 7f;
        var residents = c.Nations.Sum(n => n.Population);
        var cash = c.Nations.Sum(n => n.Treasury);
        var gdp = c.Nations.Sum(n => n.Gdp);
        UnionCohesion.Tick(c);
        Check(c.Nations.Count == 3, "a federal break founds a new country without invalidating the day iterator");
        var born = c.Nations[2];
        Check(born.SecededFromId == parent.Id && c.StatesOf(born).Any(s => s.MapIndex == 3002),
            "the angriest region actually passes to its new state");
        Check(c.Nations.Sum(n => n.Population) == residents &&
              Math.Abs(c.Nations.Sum(n => n.Treasury) - cash) < .001 &&
              Math.Abs(c.Nations.Sum(n => n.Gdp) - gdp) < .001,
            "the fix retains territorial and financial partition, rather than suppressing the event");
        Check(BitConverter.SingleToInt32Bits(calm.UnionCohesion) == BitConverter.SingleToInt32Bits(expectedCalm),
            "the country after the seceding union is reviewed exactly once");
        Check(parent.UnionLastSecessionDay == c.ElapsedDays && parent.UnionCohesion > .1f,
            "the successful secession completes its cooldown and cohesion adjustment");
        UnionCohesion.Tick(c);
        Check(c.Nations.Count == 3 && c.StatesOf(born).Length == 1,
            "another same-day review respects the cooldown and does not duplicate the break");
        var twin = Fixture(); UnionCohesion.Tick(twin); UnionCohesion.Tick(twin);
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(twin),
            "repeated identical mapped runs preserve deterministic state");
    }
}
