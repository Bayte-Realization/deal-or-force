# Core-code replacements / Замена штатной логики

Copying a `.cs` file with the same class name into a mod **does not** replace the
compiled game. Data overlays replace declared data; a DLL implementing
`IGlobalityMod` can now also replace four explicit simulation passes:

| `SimulationPass` | Existing call replaced | Timing |
| --- | --- | --- |
| `DiplomaticActs` | `DiplomaticActs.Tick` | Before logistics |
| `Logistics` | `LogisticsSystem.Tick` | Before military movement |
| `Warfare` | `Warfare.Tick` | After movement, before attrition |
| `Construction` | `ConstructionSystem.Tick` | After doctrine advancement |

Each runs once per **simulation step**, which is not necessarily a whole day.
Preserve `campaign.TickScale`, `TickHours` and the built-in system's own daily gates
when implementing rates. No callback runs on a paused paint. There is no new clock,
second economy or parallel battle resolver running beside the original.

Register from `OnLoad` or `OnCampaignCreated`:

```csharp
context.ReplaceSimulationPass(SimulationPass.Construction, (campaign, original) =>
{
    // Before: adjust inputs using the existing world's state and units.
    original(); // Keep the built-in pass, once, on this simulation thread.
    // After: inspect or extend its result.
});
```

Omit `original()` to suppress the built-in pass and supply its complete replacement.
That means owning its feature gates, validation, rates, invariants and balance too.
Do not call the built-in `Tick` separately as well as `original()`. The host guards
the continuation, not arbitrary calls made by trusted .NET code.

- One owner per pass. A conflict names both package IDs and refuses the new owner;
  load order is not an undocumented priority system.
- `ModSimulationPasses.Owner(campaign, pass)` is a read-only diagnostic.
- The returned `IDisposable` lease can be released early. The loader also releases
  every lease on failed initialization and `StopCode`, before unloading its DLL.
- Registration closes after initialization. Repeated, delayed, recursive and
  background-thread continuation calls are refused.
- A callback fault propagates into the normal campaign diagnostic path. There is
  **no automatic fallback after partial writes**, because it could execute costs,
  movement or production twice. Load the last good save after a simulation fault.
- Runtime registrations are not campaign state. Save/load requires the same enabled
  packages to reinstall them. Custom persistent state still needs an explicit save
  and fingerprint contract; this API does not persist arbitrary plugin objects.
- Code mods are trusted, not sandboxed. Same packages/assemblies and deterministic
  code are required on every network peer. Network compatibility of this new API
  has **not** been certified; start with single-player copies of saves.

## По-русски

Это реальные точки замены существующих вызовов, а не подмена любого класса C# или
любого GDScript. Пока поддержаны четыре прохода из таблицы. Оверлеи `game/` по-прежнему
предпочтительнее там, где достаточно данных, условий, событий, YAML и модификаторов.

Вызов `original()` сохраняет штатную работу и позволяет дополнить её до/после.
Без него штатный проход не выполняется. Время — **шаг симуляции**, а не кадр и не
обязательно сутки. Не запускайте собственный таймер для той же механики.

Два мода не могут молча подменить один проход. Регистрация разрешена только при
инициализации и снимается при выгрузке, в том числе после ошибки загрузки. Если
обработчик упал после записи в мир, игра не запускает штатную логику повторно:
это скрывало бы ошибку и могло удвоить последствия. Проверяйте мод на копии сейва.

Пример DLL находится в `mods/_templates/csharp_mod`. Для сборки нужен .NET 10 SDK
и сборка именно установленной версии игры. Инструкции — в README шаблона.

## Executable checks

```powershell
dotnet run -c Release --project Tools/FormableTests -- --mod-simulation-passes-only
```

The checks cover actual `Campaign.Tick` ordering/count, unchanged full campaign
fingerprint when forwarding originals, unload, conflicting owners, failure without
double execution and escaped continuations. The native `--verify-release-0530-only`
probe additionally accepts `--verify-corecode-package <built-template-folder>` to
exercise the production managed loader, real DLL callback and unload.
