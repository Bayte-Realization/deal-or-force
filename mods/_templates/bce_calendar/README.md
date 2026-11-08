# Ancient calendar: scenario-author reference

This is an inactive reference folder, not an enabled mod or a replacement ancient world.
See `game/documentation/calendar_eras.md` for the complete English/Russian contract.

1. Create a mod from an in-game editor and copy the current world to a scenario.
2. In Scenario settings choose **Before Common Era (BCE)** and a positive civil year.
3. Replace the modern roster, technologies, army templates, history and economy in
   your own mod. Changing the calendar alone does not do this work for you.
4. Save the draft, enable your mod in the launcher, and select its scenario.
5. Test an ordinary compressed save/load and at least a year of simulation on copies.

A dated event in your mod's `events/` may use `"date": "3500-02-28 BCE"`.
For raw scenario data, `Date.Year = -3499` means 3500 BCE; year `0` means 1 BCE.
The calendar is proleptic Gregorian. Existing elapsed-day timers retain their origin.

Справочник не включается автоматически. Создайте свой мод, выберите эру в редакторе
и отдельно задайте историческое наполнение. Полная инструкция — в документации игры.
