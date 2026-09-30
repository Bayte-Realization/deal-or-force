using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A new country is named for its people only when it is mostly theirs (SecessionEvents):
/// Kurds leaving Iraq with Erbil found Kurdistan; Kurds sweeping Baghdad along for its own
/// reasons do not found "Kurdistan" out of Arabs; refugees at the core of an Egyptian revolt do
/// not make "Palestine" of Egypt.
/// </summary>
static class SecessionNameTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL secession name: " + name);
        Console.WriteLine("PASS secession name: " + name);
    }

    static SecessionPiece Piece(ScenarioState state, SecessionCause cause) => new()
    {
        MapIndex = state.MapIndex, Name = state.Name, Cause = cause, Population = state.Population
    };

    public static void Run(Campaign campaign)
    {
        var iraq = campaign.NationByTag("IRQ");
        var egypt = campaign.NationByTag("EGY");
        var erbil = campaign.States.First(state => state.Id == "iq_ar");
        var arab = campaign.StatesOf(iraq)
            .Where(state => EconomySystem.Cultures(state).FirstOrDefault().Culture != "kurdish")
            .OrderByDescending(state => state.Population).First();
        var cairo = campaign.StatesOf(egypt).OrderByDescending(state => state.Population).First();

        var alone = new List<SecessionPiece> { Piece(erbil, SecessionCause.Nationality) };
        Check(SecessionEvents.LeadsTheGround(campaign, "kurdish", alone), "Kurds are the largest people of Erbil");
        Check(SecessionEvents.NamedForItsPeople(campaign, "kurdish", alone), "so Erbil leaving is Kurdistan");

        var swept = new List<SecessionPiece> { Piece(erbil, SecessionCause.Nationality), Piece(arab, SecessionCause.Discontent) };
        Check(!SecessionEvents.NamedForItsPeople(campaign, "kurdish", swept),
            $"Erbil with {arab.Name} swept along is named for its seat, not for the Kurds ({arab.Population:N0} came along)");

        var refugees = new List<SecessionPiece> { Piece(cairo, SecessionCause.Nationality), Piece(cairo, SecessionCause.Discontent) };
        Check(!SecessionEvents.NamedForItsPeople(campaign, "palestinian", refugees),
            "a revolt in Egypt is not Palestine");

        var scripted = new List<SecessionPiece> { Piece(cairo, SecessionCause.Nationality) };
        Check(SecessionEvents.NamedForItsPeople(campaign, "palestinian", scripted),
            "but a scripted secession that names its people keeps their name - the claim is the point");
        Check(!SecessionEvents.NamedForItsPeople(campaign, "", alone), "and a secession of nobody in particular is named for its ground");
    }
}
