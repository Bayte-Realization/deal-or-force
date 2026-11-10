# Сдача войск в котлах при 0% морали и выпуск EntertAlpha 0.6.3.1

11 ноября 2026 года · отчет о фиксе бесконечной обороны в котлах и обновлении версии

## 1. Контекст проблемы

В закрытых котлах (провинциях, откуда нет путей отступления: `SpreadRetreatProvince <= 0`) обороняющиеся войска зависали в бесконечных сражениях при 0% морали (организация <= 0.005f), как это было видно на скриншоте (4 дивизии РФ с 44 250 бойцами сидели в бою #1065 с 0% морали бесконечно).

### Причина бага
1. В `PocketResistance.CanContinue` проверялись только `Strength > strength` и наличие снабжения/припасов (`PocketStores > 0f || SupplyLevel > supply`), но условие на организацию/мораль полностью отсутствовало.
2. В тесте `HoldsUntilTheDefenceActuallyFails` стояло ошибочное утверждение: `d.Organisation = .001f;` и ожидание, что гарнизон будет держаться до падения численности ниже 25%, даже с нулевой моралью.
3. В `CombatCore.cs` проверка `defendersSpent && !pocketResists` никогда не срабатывала для снабжённых сил, из-за чего захват провинции (`Capture`) не вызывался, войска не капитулировали и бой не завершался.

## 2. Реализованные изменения

1. **`Scripts/Simulation/PocketResistance.cs`**:
   - В метод `CanContinue` добавлено условие `if (army.Organisation <= 0.005f) continue;`. Если у подразделения мораль упала до 0%, оно больше не считается способным удерживать котел.

2. **`Scripts/Simulation/CombatCore.cs`**:
   - Перед началом каждого раунда боя и сразу после нанесения урона организации/численности обороняющимся добавлен проход: любые окруженные войска без путей отступления (`SpreadRetreatProvince <= 0`), чья мораль опустилась до 0% (`Organisation <= 0.005f`), немедленно капитулируют (`WarLosses.Surrender`) и уничтожаются (`Attrition.Destroy`), убираясь из состава участников боя.
   - Если все защитники капитулировали или выбиты, вызывается `Capture` и бой завершается со статусом "Defenders broke".

3. **`Tools/ArmyInteractionTests/PocketResistanceTests.cs`**:
   - Обновлен сценарий `HoldsUntilTheDefenceActuallyFails`: при организациях выше нуля, но ниже стандартного порога срыва фронта (например, 2-8%), войска ведут отчаянное сопротивление последнего рубежа. При падении организации до 0% (`0.001f`) они гарантированно капитулируют и исчезают, бой закрывается, а провинция переходит атакующему.

4. **Обновление версии до EntertAlpha 0.6.3.1**:
   - `GameVersion.props`: `<Version>0.6.3.1</Version>`
   - `Scripts/Simulation/GameBuildInfo.cs`: `Version = "0.6.3.1"`
   - `project.godot`: `config/version="0.6.3.1"`
   - `export_presets.cfg`: `application/file_version="0.6.3.1"`, `application/product_version="0.6.3.1"`
   - `Tools/FormableTests/VersionLabelTests.cs`: обновлены проверки на "EntertAlpha 0.6.3.1" и "0.6.3.1"
   - `README.md` и `README.ru.md`: версия 0.6.3.1 и ссылки на чейнджлоги
   - `docs/BUILDING.md` и `docs/BUILDING_RU.md`: актуальный выпуск 0.6.3.1
   - Созданы чейнджлоги `docs/CHANGELOG_0.6.3.1_EN.md` и `docs/CHANGELOG_0.6.3.1_RU.md`.

5. **Локализация**:
   - В `game/localization/russian/338_planning_bar.json` устранены 7 конфликтующих дубликатов переводов ключей (`Attack`, `Defend`, `Defensive line`, `Execute`, `Executing`, `Auto-deploy`, `Spread the selection...`).

## 3. Результаты проверок

- `Tools/Test-ReleaseVersion.ps1`: Все 17 проверок релиза пройдены (**PASS**).
- `Tools/audit-localization.ps1 -Fail`: **0/0/0** (0 дубликатов, 0 расхождений плейсхолдеров, 0 пустых значений).
- `dotnet build DealOrForce.csproj`: Сборка успешна, 0 ошибок.
- `dotnet run --project Tools/ArmyInteractionTests/ArmyInteractionTests.csproj -- --pocket-resistance-only`: Все 35 проверок пройдены (**PASS**).
- `dotnet run --project Tools/FormableTests/FormableTests.csproj`: Все наборы тестов пройдены (**PASS**).
- Команда с `--authored-world` проверяет стартовые данные, а не течение 30 дней;
  её нельзя считать длительным прогоном симуляции.

## 4. Независимая повторная проверка перед выпуском

В изолированном выпускном дереве на основе опубликованного `94fa9a1a`:
обновлённый тест воспроизвёл ошибку на прежнем коде, после переноса исправления
прошли 35 pocket resistance, 10 combat/attrition и 38 coalition combat проверок.
Полный FormableTests — 16099 PASS. Сборка игры и стенда — 0 ошибок.
Повторный soak: 365/365 дней, seed555, шаг1час, 0 alerts / 0 invariant failures.
Локализация RU — 0 конфликтов, потерь placeholders и пустых значений.
Fennara: 803 файла, 0 ошибок, одно прежнее предупреждение CS0649 MarchProbe.
В SimBench остаётся прежний CS0458 AgingReport.

Обнаружено, что `project.godot` всё ещё содержал 0.6.3.0: настройка исправлена
и в общем редакторском проекте, и в выпускном дереве. Guard версии — 17 PASS.
Порог `0.005f` соответствует 0,5% организации, а не только точному нулю.
Это не проверка пользовательского сейва или 1000-летней кампании.
