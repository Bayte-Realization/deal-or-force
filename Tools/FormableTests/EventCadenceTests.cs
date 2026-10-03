using System;
using System.Linq;
using DealOrForce.Simulation;

static class EventCadenceTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception("Event cadence: " + message);
        Console.WriteLine("PASS event cadence: " + message);
    }

    private static void Day(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(17);
        var country = c.NationByTag("KOR");
        c.ConfigureSinglePlayer(country.Id);
        Check(new Defines().EventIntervalDays == 125 && c.Defines.EventIntervalDays == 125,
            "compiled and shipped intervals agree");
        var randomBefore = c.Random.State;
        var count = 0;
        for (var day = 0; day <= 3750; day++)
        {
            Day(c, day);
            EventSystem.Tick(c);
            count += c.PendingEvents.Count;
            c.PendingEvents.Clear();
        }
        Check(count == 30 && 3750 / 75 == 50, "30 ambient draws instead of 50: exactly 40% fewer");
        Check(c.Random.State == randomBefore, "ambient selection does not consume the simulation RNG");
        foreach (var interval in new[] { 0, -1, int.MinValue })
        {
            c.Defines.EventIntervalDays = interval;
            Day(c, 125);
            EventSystem.Tick(c);
            Check(c.PendingEvents.Count == 0, "non-positive interval disables safely: " + interval);
        }
        c.Defines.EventIntervalDays = 10;
        Day(c, 9); EventSystem.Tick(c);
        Check(c.PendingEvents.Count == 0, "custom interval waits for its boundary");
        Day(c, 10); EventSystem.Tick(c); EventSystem.Tick(c);
        Check(c.PendingEvents.Count == 1, "custom interval draws once without stacking unanswered cards");
        var pending = c.PendingEvents.Single();
        c.Defines.EventIntervalDays = 0;
        Day(c, pending.ExpiresDay); EventSystem.Tick(c);
        Check(c.PendingEvents.Count == 0, "disabled ambient cadence still resolves expired requests");
        Check(EventSystem.Raise(c, country.Id) is not null, "explicit scripted/console events still work");
        c.ObserverMode = true; EventSystem.Tick(c);
        Check(c.PendingEvents.Count == 0, "observer mode clears an existing request");
        c.Defines.EventIntervalDays = 1; Day(c, c.ElapsedDays + 1); EventSystem.Tick(c);
        Check(c.PendingEvents.Count == 0, "observer remains free of ambient popups");

        c.ObserverMode = false;
        // Bootstrap precedes headquarters seeding. Model a landless seat without loading
        // a real world raster; only the event audience/cadence is under test here.
        var movement = c.NationByTag("PRK");
        movement.Movement = new MovementState { Landless = true };
        c.HumanSeats.Add(movement.Id);
        c.Defines.EventIntervalDays = 10;
        c.Defines.MovementEventIntervalDays = 0;
        Day(c, 10); EventSystem.Tick(c);
        Check(c.PendingEvents.Count == 1 && c.PendingEvents[0].NationId == country.Id,
            "movement cadence can be disabled independently in hotseat");
        c.PendingEvents.Clear();
        c.Defines.MovementEventIntervalDays = 3;
        Day(c, 3); EventSystem.Tick(c);
        Check(c.PendingEvents.Count == 1 && c.PendingEvents[0].NationId == movement.Id,
            "landless movement uses its own interval and eligible event pool");
        c.PendingEvents.Clear();
        c.Defines.MovementEventIntervalDays = -1;
        Day(c, 10); EventSystem.Tick(c);
        Check(c.PendingEvents.Count == 2, "negative movement interval inherits the global cadence");
        c.PendingEvents.Clear();
        movement.Movement = null;
        c.HumanSeats.Remove(movement.Id);
        c.ScenarioId = "cadence_test";
        var stateId = (ushort)c.StatesOf(country).First().MapIndex;
        var map = ProvinceTopology.Build(Enumerable.Repeat(stateId, 16).ToArray(),
            Enumerable.Repeat((ushort)country.MapIndex, 16).ToArray(), 4, 4, _ => false);
        c.AttachProvinceTopology(map);
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "cadence"), "cadence");
        Check(restored.Success, "snapshot imports: " + restored.Warning);
        Check(restored.Success && restored.Campaign.Defines.EventIntervalDays == 10 &&
            restored.Campaign.Defines.MovementEventIntervalDays == -1,
            "campaign snapshots retain scenario-specific cadence");
    }
}
