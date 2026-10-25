using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A partition is three allied powers dividing a weak common neighbour by simultaneous
/// demands. The fixture is the smallest world that can be divided: a victim in the middle
/// with one region against each taker's own ground, and a capital behind them that has to
/// survive - a demand must leave the victim something, or it is not a partition but an
/// annexation the ultimatum rules refuse. Built the way every hermetic world here must be:
/// regions by hand, the lazy state index dropped, capitals named after the attach.
/// </summary>
static class PartitionTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("partition: " + label); Console.WriteLine("PASS partition: " + label); }

    public static void Run()
    {
        var c = World(out var a, out _, out _, out _);
        Check(AiStatecraft.TryCoordinatePartition(c, a), "allied powers propose what they can carry");
        Check(c.States.Find(s => s.MapIndex == 3004).NationMapIndex == 501,
            "the first taker's slice follows its own border");
        Check(c.States.Find(s => s.MapIndex == 3007).NationMapIndex == 502,
            "the second taker's slice follows its own border");
        Check(c.States.Find(s => s.MapIndex == 3008).NationMapIndex == 503,
            "the third taker's slice follows its own border");
        Check(c.States.Find(s => s.MapIndex == 3006).NationMapIndex == 504,
            "the victim keeps its capital: a partition is not an execution");

        var off = World(out _, out _, out _, out _);
        off.Defines.AiMayCoordinatePartitions = false;
        Check(!AiStatecraft.TryCoordinatePartition(off, off.Nations[0]),
            "with the rule off the world stays post-UN");

        var strong = World(out _, out _, out _, out var sv);
        sv.Gdp = 2000f;
        Check(!AiStatecraft.TryCoordinatePartition(strong, strong.Nations[0]),
            "a victim the coalition does not overwhelmingly outweigh is not divided");

        var defended = World(out _, out _, out _, out var dv);
        var guard = new Nation { Tag = "PGU", MapIndex = 505, Population = 5_000_000, Gdp = 50, Treasury = 5, OutputScale = 1 };
        defended.AddNation(guard);
        CampaignDiplomacy.Get(defended, guard.Id, dv.Id).Stance = DiplomaticStance.Allied;
        Check(!AiStatecraft.TryCoordinatePartition(defended, defended.Nations[0]),
            "a victim somebody outside the coalition stands behind is not divided");

        var few = World(out _, out _, out _, out _);
        few.Defines.AiPartitionMinimumDemanders = 4;
        Check(!AiStatecraft.TryCoordinatePartition(few, few.Nations[0]),
            "fewer takers than the minimum is acession, not a partition");

        PlayerProposition();
    }

    /// <summary>
    /// The player's half of the same instrument. The gate is the computer's gate - nobody
    /// outside the coalition stands behind the victim, enough takers, overwhelming weight - asked
    /// about a victim the player picked; the allies answer on the same die they throw for their
    /// own plans; and each slice moves through the same AiStatecraft.PartitionDemand. So the
    /// tests here are the ones above with the chairs swapped, which is the whole point.
    /// </summary>
    static void PlayerProposition()
    {
        {
            // The switch that lights the button must also be the switch the command obeys: a
            // hidden button is a courtesy, and a rule that a caller can walk around is not a rule.
            var c = World(out var a, out _, out _, out var victim, 0);
            c.Defines.AiMayCoordinatePartitions = false;
            var command = new ProposePartitionCommand { IssuerId = a.Id, VictimId = victim.Id };
            c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
            Check(!command.Coordinated && command.Slices == 0 && command.Note.Length > 0,
                "with the rule off a player cannot divide a neighbour either");
        }

        {
            var c = World(out var a, out _, out _, out var victim, 0);
            var command = new ProposePartitionCommand { IssuerId = a.Id, VictimId = victim.Id };
            c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
            Check(command.Coordinated && command.Slices == 3,
                "a player proposes the carve-up and the allies answer it on the same day");
            Check(c.States.Find(s => s.MapIndex == 3004).NationMapIndex == 501,
                "the player's own slice follows the player's border");
            Check(c.States.Find(s => s.MapIndex == 3007).NationMapIndex == 502 &&
                c.States.Find(s => s.MapIndex == 3008).NationMapIndex == 503,
                "the allies take theirs on the same day");
            Check(c.States.Find(s => s.MapIndex == 3006).NationMapIndex == 504,
                "the victim keeps its capital: a partition is not an execution");

            var again = new ProposePartitionCommand { IssuerId = a.Id, VictimId = victim.Id };
            c.Commands.Enqueue(again); c.Commands.ApplyAll(c);
            Check(!again.Coordinated && again.Slices == 0 && again.Note.Length > 0,
                "the same government does not carve up the same neighbour again the next morning");
        }

        {
            // The allies are not more eager because a human asked: the die decides, as it does
            // when the computer proposes to itself.
            var c = World(out var a, out _, out _, out var victim, 0);
            c.Defines.AiPartitionAppetite = 0f;
            var command = new ProposePartitionCommand { IssuerId = a.Id, VictimId = victim.Id };
            c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
            Check(!command.Coordinated && command.Slices == 0,
                "with no appetite among the allies a player's proposal is a demand, not a partition");
            Check(c.States.Find(s => s.MapIndex == 3004).NationMapIndex == 504,
                "nothing moved: the refusal is a refusal, not a half-done carve-up");
        }

        {
            var c = World(out var a, out _, out _, out var victim, 0);
            var guard = new Nation { Tag = "PGU", MapIndex = 505, Population = 5_000_000, Gdp = 50, Treasury = 5, OutputScale = 1 };
            c.AddNation(guard);
            CampaignDiplomacy.Get(c, guard.Id, victim.Id).Stance = DiplomaticStance.Allied;
            var command = new ProposePartitionCommand { IssuerId = a.Id, VictimId = victim.Id };
            c.Commands.Enqueue(command); c.Commands.ApplyAll(c);
            Check(!command.Coordinated && c.States.Find(s => s.MapIndex == 3004).NationMapIndex == 504,
                "a player cannot divide a country somebody outside the coalition stands behind");
        }

        {
            // And the other chair: an AI coalition may put the same demands to a player, who
            // answers them. Before this, a victim with a human in it was simply skipped.
            var plays = World(out _, out _, out _, out var human, 3);
            Check(human.IsHuman, "fixture: the victim is the country a player holds");
            Check(AiStatecraft.TryCoordinatePartition(plays, plays.Nations[0]),
                "a coalition proposes the carve-up to a player instead of skipping them");
            Check(plays.UltimatumPapers.Count == 3 && plays.UltimatumPapers.All(paper => paper.Pending),
                "each taker's slice is a paper on the player's desk, not an answer given for them");
            Check(plays.States.Find(s => s.MapIndex == 3004).NationMapIndex == 504 &&
                plays.States.Find(s => s.MapIndex == 3007).NationMapIndex == 504 &&
                plays.States.Find(s => s.MapIndex == 3008).NationMapIndex == 504,
                "land moves only when the player answers, so nothing has moved yet");
        }
    }

    /// <summary>
    /// Three strong allies along the north (A west, B middle, C east), the victim's middle
    /// band in one region against each of them, and the victim's capital band behind that,
    /// touching no taker - the shape Poland had, minus the sea.
    /// </summary>
    static Campaign World(out Nation a, out Nation b, out Nation cc, out Nation victim, int playerId = -1)
    {
        var defines = new Defines { AiMayCoordinatePartitions = true, AiPartitionAppetite = 1f };
        var c = new Campaign(824, defines) { ScenarioId = "partition_fixture" };
        c.AddNation(new Nation { Tag = "PA0", MapIndex = 501, Population = 50_000_000, Gdp = 1000, Treasury = 50, OutputScale = 1 });
        c.AddNation(new Nation { Tag = "PB0", MapIndex = 502, Population = 50_000_000, Gdp = 1000, Treasury = 50, OutputScale = 1 });
        c.AddNation(new Nation { Tag = "PC0", MapIndex = 503, Population = 50_000_000, Gdp = 1000, Treasury = 50, OutputScale = 1 });
        c.AddNation(new Nation { Tag = "PVM", MapIndex = 504, Population = 5_000_000, Gdp = 10, Treasury = 1, OutputScale = 1 });
        a = c.Nations[0]; b = c.Nations[1]; cc = c.Nations[2]; victim = c.Nations[3];

        AddState(c, "partition_a", 3001, 501, "PA0", 20_000_000);
        AddState(c, "partition_b", 3002, 502, "PB0", 20_000_000);
        AddState(c, "partition_c", 3003, 503, "PC0", 20_000_000);
        AddState(c, "partition_west", 3004, 504, "PVM", 1_000_000);
        AddState(c, "partition_middle", 3007, 504, "PVM", 2_000_000);
        AddState(c, "partition_east", 3008, 504, "PVM", 1_500_000);
        AddState(c, "partition_capital", 3006, 504, "PVM", 500_000);
        c.InvalidateStateIndex();

        var cells = Enumerable.Range(0, 1200).Select(CellState).ToArray();
        var owners = Enumerable.Range(0, 1200).Select(CellOwner).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 60, 20, _ => false));
        a.CapitalStateMapIndex = 3001; a.CapitalProvinceId = FirstProvince(c, 3001);
        b.CapitalStateMapIndex = 3002; b.CapitalProvinceId = FirstProvince(c, 3002);
        cc.CapitalStateMapIndex = 3003; cc.CapitalProvinceId = FirstProvince(c, 3003);
        victim.CapitalStateMapIndex = 3006; victim.CapitalProvinceId = FirstProvince(c, 3006);

        CampaignDiplomacy.Get(c, a.Id, b.Id).Stance = DiplomaticStance.Allied;
        CampaignDiplomacy.Get(c, a.Id, cc.Id).Stance = DiplomaticStance.Allied;
        CampaignDiplomacy.Get(c, b.Id, cc.Id).Stance = DiplomaticStance.Allied;
        if (playerId >= 0) c.ConfigureSinglePlayer(playerId);
        SetDay(c, 7);
        return c;
    }

    static void AddState(Campaign c, string id, int mapIndex, int nationMapIndex, string nationId, long population) =>
        c.States.Add(new ScenarioState { Id = id, Name = id, MapIndex = mapIndex,
            NationMapIndex = nationMapIndex, NationId = nationId, Population = population });

    static ushort CellState(int i)
    {
        var x = i % 60;
        var y = i / 60;
        if (y < 7) return (ushort)(x < 20 ? 3001 : x < 40 ? 3002 : 3003);
        if (y < 13) return (ushort)(x < 20 ? 3004 : x < 40 ? 3007 : 3008);
        return 3006;
    }

    static ushort CellOwner(int i) => CellState(i) switch { 3001 => 501, 3002 => 502, 3003 => 503, _ => 504 };

    static int FirstProvince(Campaign c, int stateMapIndex)
    {
        var topology = c.ProvinceMap;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToState[province] == (ushort)stateMapIndex) return province;
        return -1;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
}
