using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The Gaza Strip (item 77) and occupations with no war behind them.
///
/// The Strip is its own region with its own people, and in the 2024 bookmark it is Israel's -
/// owned, not merely held - the way the Russian-held regions of Ukraine are Russia's. It used to
/// open as Palestine's, occupied by Israel with no war between them, which is the one kind of
/// occupation the game no longer keeps: a province held by a country not at war with its owner
/// goes back to the owner the next day (Occupations). A scenario may still open a region occupied
/// on purpose ("controller" in its history, HeldGround), and that one stands.
/// </summary>
static class HeldGroundTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL held ground: " + name);
        Console.WriteLine("PASS held ground: " + name);
    }

    public static void Run(Campaign campaign)
    {
        var israel = campaign.NationByTag("ISR");
        var palestine = campaign.NationByTag("PSE");
        var egypt = campaign.NationByTag("EGY");
        var gaza = campaign.States.FirstOrDefault(state => state.Id == "ps_gza");
        var westBank = campaign.States.FirstOrDefault(state => state.Id == "ps_wbk");
        Check(gaza is not null, "the Gaza Strip is a region of its own");
        Check(gaza.NationMapIndex == israel.MapIndex, "and in the 2024 bookmark it is Israel's");
        Check(string.IsNullOrEmpty(gaza.StartController), "owned, not written as held by somebody else");
        Check(Math.Abs(gaza.Population - 2_100_000) < 50_000, $"about two million people live there ({gaza.Population:N0})");
        Check(westBank is not null && westBank.NationMapIndex == palestine.MapIndex && westBank.Population < 3_000_000,
            $"the West Bank is Palestine's, without the Strip's people ({westBank?.Population:N0})");

        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        int[] Of(ScenarioState state) => Enumerable.Range(1, topology.ProvinceCount)
            .Where(province => topology.ProvinceToState[province] == (ushort)state.MapIndex).ToArray();
        var strip = Of(gaza);
        Check(strip.Length > 0, $"it has ground on the map ({strip.Length} province(s))");
        Check(strip.All(province => topology.ProvinceToNation[province] == (ushort)israel.MapIndex &&
                                    controllers[province] == (ushort)israel.MapIndex),
            "every province of it is owned and held by Israel");

        // The world opens with no occupation that has no war behind it.
        var enemies = campaign.Wars.Where(war => !war.Ended).ToList();
        var stray = Enumerable.Range(1, topology.ProvinceCount).Count(province =>
        {
            var owner = NavalOperations.NationAt(campaign, topology.ProvinceToNation[province]);
            var holder = NavalOperations.NationAt(campaign, controllers[province]);
            return owner is not null && holder is not null && owner.Id != holder.Id &&
                   !enemies.Any(war => war.Opposes(owner.Id, holder.Id)) &&
                   // Held by arrangement - a lease - is held without a war on purpose.
                   !LandArrangementSystem.Covers(campaign, province, holder.Id);
        });
        Check(stray == 0, $"no province opens held by a country not at war with its owner ({stray})");
        // And the leases are on the ground from the first look: Baikonur is Kazakh, held by Russia.
        var baikonur = campaign.States.Find(state => state.Id == "99_x05");
        var russia = campaign.NationByTag("RUS");
        if (baikonur is not null && russia is not null)
            Check(Enumerable.Range(1, topology.ProvinceCount).Where(p => topology.ProvinceToState[p] == baikonur.MapIndex)
                    .All(p => controllers[p] == (ushort)russia.MapIndex),
                "a lease is held on the opening map, before the first day");

        // An occupation nobody is at war over goes home the next day...
        var bank = Of(westBank);
        var probe = bank[0];
        campaign.SetProvinceController(probe, (ushort)egypt.MapIndex);
        Check(Occupations.Tick(campaign) >= 1 && controllers[probe] == (ushort)palestine.MapIndex,
            "a province Egypt holds with no war against Palestine goes back to Palestine");

        // ...one a war is being fought over stays where the war left it...
        var war = new StrategicWar { Id = 991, AttackerId = egypt.Id, DefenderId = palestine.Id };
        campaign.Wars.Add(war);
        campaign.SetProvinceController(probe, (ushort)egypt.MapIndex);
        Occupations.Tick(campaign);
        Check(controllers[probe] == (ushort)egypt.MapIndex, "while they are at war, Egypt keeps what it holds");
        war.Ended = true;
        Occupations.Tick(campaign);
        Check(controllers[probe] == (ushort)palestine.MapIndex, "and the day the war is over, it goes back");
        campaign.Wars.Remove(war);

        // ...and one the scenario opens with on purpose stands until a war changes it.
        westBank.StartController = "ISR";
        var written = (ushort[])controllers.Clone();
        Check(HeldGround.ApplyToGround(campaign, topology, written) == bank.Length &&
              bank.All(province => written[province] == (ushort)israel.MapIndex),
            "a region's written controller holds every province of it on the first day");
        campaign.SetProvinceController(probe, (ushort)israel.MapIndex);
        Occupations.Tick(campaign);
        Check(controllers[probe] == (ushort)israel.MapIndex, "and a written occupation is not lifted for want of a war");
        westBank.StartController = "";
        Occupations.Tick(campaign);
        Check(controllers[probe] == (ushort)palestine.MapIndex, "an unwritten one is");
    }
}
