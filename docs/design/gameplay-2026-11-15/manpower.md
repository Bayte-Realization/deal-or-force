# Людской резерв и мигранты

Проект механики, 15 ноября 2026. Основа — проект A («ясность AoH»), к нему привиты лучшие идеи проекта B («органическая история»). Ниже есть оценка обоих.

## Зачем

Владелец: людской резерв должен быть сильнее связан с остальной игрой и сильнее влиять на население и экономику. Резерва должно быть **меньше**, а механика — сложнее.

Друг владельца: «Как сделать, чтобы ко мне мигранты валили толпами?»

Отсюда одна мысль: люди — это один общий ресурс, который делят армия, заводы, колыбели и границы. Солдат — это рабочий, которого нет на заводе, и отец, у которого не родились дети. Мигрант — это рабочий, которого не пришлось растить 18 лет, и повод для спора в каждом приграничном городе. Действуют три правила:

1. Резерв берётся из настоящих людей и никогда не появляется из ничего.
2. Призыв забирает рабочих, а гибель оставляет рубец в демографии на десятилетия.
3. Миграция устроена как поле притяжения и выталкивания. Игрок управляет им законами и решениями и платит за это интеграцией и нативизмом. ИИ пользуется теми же рычагами.

Короткий ответ другу: будь богаче соседей, живи в мире, открой двери, имей свою диаспору у себя и подпиши трудовой договор с бедным соседом. Каждый из этих рычагов виден одной строкой в карточке «Притяжение».

## Что уже есть

| Что | Где | Как ведёт себя сейчас |
|---|---|---|
| `Nation.ManpowerReserve` | `SimulationCore.cs:405`; около 40 читателей и писателей (MilitaryCore, Recruitment, SupportCore, Attrition, WarEmergency, Escalation, KinStatecraft, ProxyWars, SubjectCore, Movement*, SecessionEvents, Desertion, Demobilisation, WarLosses, ModApi, ScriptedEffects `manpower`, Luau `manpower`, HistoryDataLoader) | Пул, отдельный от населения. Его никогда не вычитают ни из `Population`, ни из `PopulationGroup.Size`, ни из рабочей силы. Нет ни возраста, ни пола, ни региона. |
| Стартовый резерв | `MilitaryCore.cs:1617-1626`; `Defines.cs:754-766` | `max(personnel×0.15, Pop×0.06 − personnel)`, то есть около 6% населения у всех |
| Еженедельный прирост | `WarWeariness.cs:198-225` (`Mobilise`) | `+Pop×0.0015/52` до потолка `Pop×0.05`. Старт (6%) выше потолка (5%), поэтому в мирное время резерв не растёт вовсе. При оккупации ×4×max(0.25, WarSupport). |
| Резерв из ничего | `Escalation.cs:930-943`; `decisions/00_decisions.json:72-81` (`conscription_drive` +180 000); `60_civic_decisions.json:87,97` (+60 000, +90 000); `ProxyWars.cs:335`; `scripted_triggers/00_country.json:29` (`short_of_men` = резерв < 250 000) | Плоские прибавки: Люксембург и Индия получают одинаково, и ни у кого ничего не отнимается |
| Законы о службе | `laws/00_policies.json:6-8`, `15_more_options.json:14-15`, `40_expanded_laws.json:34-55`, `50_more_laws_a.json:26-53` | Меняют только `recruitment_speed/cost`, `reinforcement_rate`, `militia_readiness` и плоский `output` −2…−9%. На размер пула не влияет ни один закон. |
| Потери | `WarLosses.cs` (`Record`, `Surrender`, `Repatriate`, `Release`) | Учитываются только в объекте войны. Убитые остаются в населении. |
| Возрастная структура | `DemographicCycle.cs`, `Rules/DemographyRules.fs`, `defines/79_demography.json` | Три когорты на регион (молодые, рабочие, старые). Нет ни пола, ни «пропавшего поколения». `DemographyFloodShare 0.012` используется только в подсказке. |
| Рождения и рабочие | `HouseholdEconomy.cs:103,157` (`pronatalist_drive`, `demographic_boom`); `MarketCore.cs:610` `WorkersAvailable` (`WorkingAgeShare 0.62 × Employment`) | Рабочие не знают о солдатах |
| Эмиграция | `BrainDrain.cs`: `CalculatePressure` (выталкивание), `BestDestination` (одна лучшая цель с порогом 0.12), `Tick` (потолок приёма `BrainDrainMaxIntakeWeeklyShare`, перенос через `InternalMigration.Move` с культурой и верой) | Притяжение — это `livingGap×1.35`, стабильность, торговля, континент, родство 0.16, `open_migration_doors` 0.35 и нехватка рабочих. `strict_border_controls` полностью исключает страну как цель. |
| Беженцы войны | `WarDamage.cs:238-276, Flee` | Бегут к мирным соседям (`NeighboursAtPeace`), политику соседа не спрашивают |
| Законы о миграции | 11 законов категории `Migration policy`: `migration_regulated` (по умолчанию), `_closed`, `_skilled`, `_open`, `_diaspora`, `_open_settlement`, `_diaspora_return`, `_exit_controls`, `_guest_worker_quotas`, `_border_industrialisation`, `_humanitarian_corridors` | Меняют только `research_speed`, `trade_reach`, `output` и `radicalism_weekly`. Людей не двигает ни один. |
| Модификаторы | `modifiers/43_brain_drain.json`: `open_migration_doors`, `strict_border_controls`, `demographic_boom`, `pronatalist_drive`; `decisions/50_brain_drain.json` | Работают, но без связи с законами |

## Правила

Все числа — значения defines. Пересчёт идёт раз в неделю на экономическом тике, если не сказано иначе.

### 1. Призывной потолок

```
Ceiling = Pop × WorkingShare × SexEligible × Fit × DraftShare × (1 − Deferment)
```

- `WorkingShare` — существующий `Nation.WorkingShare` (DemographyRules).
- `SexEligible = 0.5 × (1 − MissingMenShare) + 0.5 × draft_women`.
- `Fit = clamp(ManpowerFitBase 0.45 + ManpowerFitLiving 0.35 × StandardOfLiving + ManpowerFitHospital 0.05 × доля регионов с больницей − ManpowerFitFamine 0.20 × доля голодающих регионов − InvalidShare, 0.40, 0.88)`. Больница и голод взяты из B.
- `DraftShare` — новый ключ модификатора `draft_share` у законов о службе.
- `Deferment = draft_deferment × Literacy × ManpowerDefermentWeight 0.4`. Это отсрочка для образованных.

| Закон | `draft_share` | `call_up_rate` (доля разрыва в неделю) | `draft_women` | `draft_deferment` |
|---|---|---|---|---|
| service_elite | 0.03 | 0.003 | 0 | 0 |
| service_professional (по умолч.) | 0.04 | 0.004 | 0 | 0 |
| service_contract | 0.05 | 0.006 | 0 | 0 |
| specialist_corps | 0.05 | 0.006 | 0 | 0.3 |
| service_cadre | 0.06 | 0.008 | 0 | 0 |
| service_selective | 0.08 | 0.015 | 0 | 0.5 |
| naval_conscription | 0.09 | 0.012 | 0 | 0.2 |
| service_reserve | 0.12 | 0.020 | 0 | 0.2 |
| territorial_militia | 0.14 | 0.020 | 0 | 0 |
| **service_universal** (новый; Финляндия, Швейцария, Корея) | 0.20 | 0.020 | 0 | 0 |
| **service_universal_both_sexes** (новый; Израиль, Норвегия, Эритрея) | 0.18 | 0.020 | 0.8 | 0 |
| service_mass | 0.22 | 0.040 | 0 | 0.05 |
| service_total_levy (`requires_war`) | 0.35 | 0.080 | 0.3 | 0 |

Сверка с реальностью (Fit около 0.70–0.80):

| Страна | Население | Потолок по формуле | Как в жизни |
|---|---|---|---|
| Германия, professional | 84 млн | 0,81 млн; резерв ≈ потолок − 0,18 млн под ружьём = 0,63 млн | сегодня в игре ≈ 4,9 млн |
| Финляндия, universal | 5,6 млн | 0,29 млн | военный состав 280 тыс. |
| Израиль, both_sexes | 9,9 млн | 0,86 млн | 170 тыс. в строю + 465 тыс. резервистов |
| Россия, selective (с отсрочкой) | 144 млн | 2,0 млн; на mass — 5,6 млн | 1,3 млн в строю, частичная мобилизация 2022 года |
| Украина в войне, total_levy | 37 млн | 2,5 млн | около 1 млн в строю |

Пол для крошечных стран: `Ceiling ≥ Personnel × (1 + ManpowerReserveFloorShare 0.15)`. Иначе Люксембург не удержит собственную столицу.

### 2. Резерв тянется к потолку (одно правило вместо прироста и потолка в 5%)

```
gap = Ceiling − Personnel − ManpowerReserve
если gap > 0:  Reserve += gap × call_up_rate × Youth × Occupied
если gap < 0:  Reserve −= |gap| × ManpowerExcessDecayWeekly 0.02   (люди уходят в рабочую силу)
Youth    = clamp(sqrt(YoungShare / DemographyReferenceYoungShare 0.28), 0.6, 1.4)
Occupied = MobilisationMultiplier 4 × max(0.25, WarSupport), если оккупировано больше 2% стоимости, иначе 1
```

- `Youth` — это след проекта A «восполнение через взросление» в виде одного множителя. Нигер восполняет резерв быстрее Японии.
- Если сменить закон, поднимается потолок, но не сам резерв. Разрыв заполняется призывом за месяцы.
- `MayRaise` и 60 дней удержания для пополнения не меняются.
- Поле `ManpowerReserve` и все его около 40 читателей остаются как есть. Меняются только места, где резерв появляется.
- Старт (MilitaryCore.Seed): `Reserve = max(Personnel × 0.15, (Ceiling − Personnel) × ManpowerOpeningFill 0.8)`. В итоге Германия начинает примерно с 0,5 млн, мир начинает близко к равновесию, и ИИ на первой неделе не голодает.
- `Features.manpower_model` (по умолчанию true). При false работает старая ветка `Mobilise`: это откат без правки кода.

### 3. Резерва из ничего больше нет

| Было | Стало |
|---|---|
| `conscription_drive` +180 000 | эффект `manpower_callup: 0.25`: сразу 25% разрыва до потолка, плюс `output −0.02` на 180 дней |
| `national_labour_service` +60 000 | `manpower_callup: 0.10` |
| `civic_defence_works` +90 000 | `manpower_callup: 0.15` |
| Escalation `TotalWarMobilisation` (0.0002 в неделю до 1.5×) | удваивает `call_up_rate`, но резерва не создаёт |
| ProxyWars: клиент получает `sponsor.Pop × share` | добровольцы вычитаются из резерва спонсора: `min(доля, 10% резерва спонсора)` |
| SubjectCore levy | остаётся как есть (это уже перевод) |
| скриптовый эффект `manpower` | остаётся для модов, но ограничен значением `Ceiling × 1.5` |
| триггер `short_of_men`: резерв < 250 000 | `manpower_ratio < 0.30`, где `manpower_ratio = Reserve / max(1, Ceiling − Personnel)` |

### 4. Люди под ружьём забирают рабочих

- `ScenarioState.UnderArmsShare = Personnel страны / (Pop страны × 0.62)`. Значение одно на все регионы страны и записывается в Manpower.Tick.
- `MarketCore.WorkersAvailable`, одна строка: `workers *= (1 − state.UnderArmsShare) × (1 + labour_participation)`. Резервисты дома работают, забирают только людей, стоящих в формированиях.
- Плоский `output` −2…−9% у законов о службе снижается до 0…−1.5%: остаётся только цена учебной системы, без двойного счёта.
- Решение `women_to_the_factories` (из B; только в войну): `labour_participation +0.10`, пока идёт война, и `+0.03` навсегда; стабильность −0.01. Это США 1942 и СССР 1941.
- Без отсрочек призыв в стране с Literacy > 0.8 даёт `research_speed −UnderArmsShare × 2` через `AddTo/Sources` в DemographicCycle, а не через экономические файлы.
- Ориентир: если 5% рабочей силы в форме, экономика, упирающаяся в людей, теряет 4–7% ВВП.

### 5. Убитые уходят из населения

- `WarLosses.Record(killed)` вызывает `Manpower.Bury(nation, killed)`. Это только накопление в `Nation.PendingDead`; список регионов не обходится на каждый бой.
- Раз в неделю Manpower.Tick разносит накопленных убитых по регионам пропорционально населению. Вызывается `EconomySystem.AdjustStatePopulation(state, −k)`: это вызов, файл не правится. Из групп убитые снимаются только в классах Lower и Middle, поровну. Из когорт берётся рабочая: `Young' = Young × P / (P − k)`, `Old'` аналогично.
- `Nation.MissingMen += killed × KilledMaleShare 0.95`, `MissingMenShare = MissingMen / (Pop × 0.5)`. Это значение снижает `SexEligible`, то есть следующий призыв (Парагвай 1870, СССР 1945).
- `MissingMen` гасится на `MissingMenDecayAnnual 0.04` в год: изувеченная когорта уходит в старость, а ей на смену взрослеет ровная. Это примерно 25 лет.
- Раненые: `WoundedDisabledShare 0.15` переходит в `Nation.InvalidShare`, которая гасится на 0.03 в год. Она снижает `Fit`, а через `AddTo` даёт `living_weekly −InvalidShare × 0.5`.
- Пленные по-прежнему возвращаются в резерв (`Repatriate`).

### 6. Рубец на рождаемости и эхо войны

- `ScenarioState.WarScar += killed_r / Pop_r`, гасится на `WarScarDecayAnnual 0.04`.
- После обзора домохозяйств в DemographicCycle часть рождений возвращается: `unborn = ReviewBirths × min(0.5, WarScar × WarScarFertilityWeight 6)`. Затем `AdjustStatePopulation(−unborn)`, а YoungShare пересчитывается.
- Пока мужчины в строю, рождаемость дополнительно умножается на `(1 − 0.5 × UnderArmsShare)` (из B).
- Эхо через 18–20 лет новым кодом не создаётся. Тощая молодая когорта взрослеет через существующий `DemographyMaturationAnnual`, `WorkingShare` проседает, и вместе с ним проседает `Ceiling`.

### 7. Беби-бум и ветераны (из B)

- `Demobilisation`: если по миру вернулось больше 1% населения, а война выиграна или WarSupport > 0.4, ставится существующий модификатор `demographic_boom` на 6 лет (HouseholdEconomy уже его читает, +0.0085). Так было в США в 1946–64 и в СССР в 1946–50.
- Проигранная война, после которой `Veterans > 1.5%` населения: модификатор `lost_generation` на 10 лет (`radicalism_weekly +0.0003`) и вес реваншистских движений из `movements/` +0.25. Это фрайкоры, Франция 1871, «афганцы». Ветераны убывают на 3% в год.

### 8. Миграция: выталкивание

`BrainDrain.CalculatePressure` остаётся как есть. Новый ключ `emigration` масштабирует отток (`NationalModifiers.Factor`): `migration_exit_controls −0.6`, `migration_closed −0.2`, `migration_diaspora +0.1`.

### 9. Миграция: притяжение (расширяется `BrainDrain.BestDestination`, новой системы нет)

К существующему счёту добавляются пять слагаемых:

| Слагаемое | Формула |
|---|---|
| Заработок | `0.35 × clamp(ln(ВВПдуш_цель / ВВПдуш_источник), 0, 2.3)` (не больше 0.8). Вес `livingGap` при этом снижается с 1.35 до 0.9, иначе богатство учитывается дважды. |
| Безопасность | существующий `stabilityBonus`, плюс −0.4, если у цели WarDamage > 0.05 в любом регионе |
| Свобода | `0.15 × (migration_freedom цели − источника)`; ключ на законах `Civil rights`: `rights_association +0.5`, `rights_digital +0.5`, `rights_ordinary 0`, `rights_oversight −0.3`, `rights_emergency −0.7` |
| Диаспора | `min(0.4, 8 × доля основной культуры источника среди населения цели)`. Таблица `Nation.CultureShares` пересчитывается из `PopulationGroups` раз в 4 недели. Для культуры с диаспорой бонус родства 0.16 заменяется этим слагаемым. Турки в Германии (≈3,5%) дают 0.28, поляки в Британии (≈1,3%) — 0.10. |
| Закон | `migration_pull` (сумма законов). Решение `open_migration_doors` переводится в `migration_pull 0.35`. `strict_border_controls` остаётся жёстким исключением. |

- **Вместо одной лучшей цели — три.** Они выбираются в том же цикле перебора, доли распределяются пропорционально `score^MigrationSplitExponent 2`, у каждой цели свой лимит приёма. Порог 0.12 остаётся.
- **Лимит приёма**: `BrainDrainMaxIntakeWeeklyShare 0.0001 × Factor("migration_intake")`. Защита от «Техаса на 38% ханьского» сохраняется.
- **Возраст уезжающих** одинаков с обеих сторон: 20/75/5 (молодые / рабочие / старые). Источник стареет, цель молодеет и поднимает свой `WorkingShare`.
- **Трудовой договор** (новая сделка `labour_pact` в `game/common/deals`): пара стран получает `+0.30` к притяжению друг к другу. Источник получает клапан безработицы (`radicalism_weekly −0.0002`), а позже и денежные переводы. Это Германия и Турция в 1961 году, Персидский залив и Южная Азия.

### 10. Законы о дверях: существующие 11 законов наконец двигают людей

| Закон | `migration_pull` | `migration_intake` | Особенность |
|---|---|---|---|
| migration_regulated (по умолч.) | 0 | 0 | — |
| migration_closed | −0.30 | −0.80 | `emigration −0.2`; беженцев принимает только закон о беженцах |
| migration_skilled («баллы», Канада) | +0.10 | −0.30 | Literacy прибывших ×1.5, натурализация ×1.2, `migration_upkeep` 0.0006 ВВП |
| migration_open | +0.45 | +2.0 | нативизм ×1.3 |
| migration_open_settlement (Аргентина 1880-х, Канадские прерии) | +0.30 | +1.0 | прибывшие селятся в самых редко населённых регионах (`ReceptionStates` с обратным порядком) |
| migration_guest_worker_quotas (Залив, ФРГ 1961) | +0.35 | +3.0 | прибывшие — не граждане: натурализация ×0.2, в призыв не идут, нативизм ×0.6, пока они не граждане |
| migration_border_industrialisation (макиладоры) | +0.20 | +0.5 | только для соседей по границе, прибывшие идут в приграничные регионы |
| migration_diaspora_return (Закон о возвращении, оралманы, аусзидлеры) | — | +0.5 | своя основная культура за рубежом получает `+0.4` к притяжению домой |
| migration_diaspora | — | — | `emigration +0.1`, `trade_reach` как сейчас |
| migration_exit_controls | — | — | `emigration −0.6` |
| migration_humanitarian_corridors | — | — | приём беженцев ×1.5 (см. п. 11) |

### 11. Закон о беженцах (новая категория `Refugee policy`, из B)

Сегодня `WarDamage.Flee` раздаёт беженцев мирным соседям, не спрашивая их согласия. Закон фильтрует список `abroad` в `NeighboursAtPeace`:

- `refugees_closed`: регионы страны выпадают из `abroad`. Беженцы остаются дома (`WarDamageRefugeesStayHome` растёт) или уходят дальше, и карта их теряет.
- `refugees_temporary` (по умолчанию; Польша и украинцы в 2022 году): принимает их. Прибывшие помечаются `TemporaryShare`. После мира 3% в месяц возвращаются, если их регион отстроен; это та же логика `Displaced`, что и дома.
- `refugees_asylum` (Германия 2015): принимает их, они остаются, натурализация ×1.0, нативизм ×1.2. Цена — `migration_upkeep` 0.0004 ВВП на каждый процент беженцев.

### 12. Новоприбывшие, интеграция, нативизм

- `ScenarioState.NewcomerShare += arrivals / Pop`. Убывает на `NaturalisationAnnual 0.08 × Factor("assimilation")`.
- Ненатурализованные не входят в `SexEligible`: `Ceiling ×= (1 − NewcomerShare × 0.5)`. Исключение — закон или решение `service_for_citizenship` (Франция, Иностранный легион; США): тогда они входят с весом 0.5, а натурализация ускоряется ×1.5.
- **Нативизм** — обещание `DemographyFloodShare`, которое наконец реализовано. Если приток за последние 52 недели превышает `DemographyFloodShare 0.012` населения, коренные группы Lower и Middle (культура ≠ культуре прибывших) получают `Radicalism += (inflow52 / Pop − 0.012) × NativismRadicalismWeight 0.04` в неделю. Множитель — `Factor("nativism")` от законов.
- Если коренной радикализм > 0.35 и `NewcomerShare` > 5%, появляется нативистское движение (новая запись в `game/common/movements`, `nativist_bloc`) с требованием `migration_closed`. Выборы и коалиции у него — через существующие механизмы движений. Так возникает цикл «нехватка рабочих → открытые двери → бум → реакция → закрытие → нехватка» (Швеция 2015→2016, Дания), и сценарием его никто не задаёт.
- Давление на жильё и низовые зарплаты: `living_weekly −inflow52 / Pop × 0.5` через `AddTo` в DemographicCycle. Экономические файлы не затрагиваются.

## Данные

- `game/common/defines/80_manpower.json` (новый): блок `Features { manpower_model, war_scars, migration_pull, nativism }`, затем все числа из п. 1–7: `ManpowerFit*`, `ManpowerDefermentWeight`, `ManpowerOpeningFill`, `ManpowerExcessDecayWeekly`, `KilledMaleShare`, `MissingMenDecayAnnual`, `WoundedDisabledShare`, `WarScar*`, `PostwarBoom*`, `LostGeneration*`. Комментарии на русском, как в `14_mobilisation.json`.
- `game/common/defines/81_migration.json` (новый): `MigrationWage*`, `MigrationDiaspora*`, `MigrationSplitExponent`, `MigrationSplitCount 3`, `NaturalisationAnnual`, `NativismRadicalismWeight`, `NativistMovementThreshold`, возраст уезжающих 20/75/5.
- Новые ключи модификаторов: `draft_share`, `call_up_rate`, `draft_women`, `draft_deferment`, `labour_participation`, `emigration`, `migration_pull`, `migration_intake`, `migration_freedom`, `migration_upkeep`, `assimilation`, `nativism`. Новые ключи эффектов: `manpower_callup`. Новые ключи триггеров: `manpower_ratio`, `newcomer_share`, `missing_men_share`. Всё это доступно модам, ScriptedEffects и Luau (`manpower_ceiling`).
- Законы: правка JSON существующих законов о службе и миграции, плюс новые `service_universal`, `service_universal_both_sexes` и `laws/60_refugee_policy.json`.
- Исторические данные: `HistoryDataLoader` уже читает `manpower`. Добавляются `service_law` и `migration_law` по странам на 2024 год: Израиль both_sexes, Финляндия и Корея universal, Залив guest_workers, Япония closed. Для будущих эпох это данные, а не код: в Средневековье `draft_share` у феодального ополчения, в викторианскую эпоху open_settlement у Нового Света.
- Локализация: `game/localization/english` и `russian` — названия ключей, строки карточек, тексты движения.

## ИИ

Всё по тем же формулам, что у игрока.

- **Закон о службе.** Пересматривается раз в 26 недель. Если `threat > 0.6` или `manpower_ratio < 0.3` в войне, ИИ поднимает закон на ступень. Если мир длится больше 3 лет и `UnderArmsShare × потеря ВВП > выгода безопасности`, опускает на ступень. Профили: фашизм уже выбирает total_levy или reserve (`PoliticalPolicies.cs:200`), малые нейтральные страны выбирают universal.
- **Закон о миграции.** Открывает двери, если `WorkingShare < 0.58` или безработица низкая при росте ВВП, а нативистский радикализм < 0.25. Закрывает, если нативистское движение есть или выиграло выборы. Богатые и малые с `WorkingShare` < 0.55 выбирают guest_workers, а это и есть органический Катар.
- **`labour_pact`.** Предлагается, когда у одной стороны нехватка рабочих, у другой безработица и отношения выше 15. Принимается источником, если выталкивание > 0.2.
- **Беженцы.** temporary по умолчанию. closed при нативизме > 0.3 или если беженцев больше 3%. asylum у демократий с `rights_association` и стабильностью > 0.7.
- **Решения.** `conscription_drive` по триггеру `manpower_ratio < 0.3`. `women_to_the_factories` в войне, если `UnderArmsShare > 0.04`.

## Интерфейс

Портретов нет, всё читается с одного взгляда.

- **Плашка ресурса «Люди»** (рядом с флагом): `резерв / потолок` в виде полосы, под ней `в строю`. Цвет зависит от `manpower_ratio`.
- **Карточка «Резерв»** — четыре строки по формуле: «Трудоспособные 60% × годные 0.78 × мужчины 0.47 (−6% после войны) × закон 4% = 0,81 млн». Каждый множитель снабжён подсказкой «почему», как в `Sources`. Ниже строка «В форме: 3,1% рабочих → −2,4% выпуска».
- **Карточка «Притяжение»**: пять строк (заработок, безопасность, свобода, диаспора, закон) со знаком и мировым местом страны («17-е место в мире по притяжению»), три главных источника и три главных направления оттока. Это и есть ответ другу.
- **Карточка «Новоприбывшие»**: `NewcomerShare`, натурализация в год, нативистский радикализм с порогом 0.35 на шкале.
- **Режим карты «Миграция»**: регионы окрашены по нетто-миграции за год, стрелки показывают три главных потока страны. Режим «Рубцы» — по `WarScar`.
- **Хроника**: «Германия открыла двери: приток 0,9% за год», «Поколение войны: призывников на 22% меньше», «Нативистский блок требует закрыть границы».
- Плашка ресурса сейчас правится другой сессией (`ResourcePlateUI.cs` в рабочем дереве), поэтому интерфейс идёт отдельным срезом после её коммита.

## Как меряем

- **Модульные тесты** (`Tools/FormableTests`): `--manpower-only`. Формула потолка по таблице сверки (Германия 0,6–1,0 млн, Финляндия 0,2–0,35 млн, Израиль 0,6–1,0 млн); Люксембург не ниже пола; при `Features.manpower_model=false` результат совпадает со старым до байта.
- **SimBench `<days> <seed> --manpower`** (новый отчёт): раз в 365 дней — средний мировой `Reserve/Pop` (цель на старте 0,8–1,5%, сегодня около 6%), 10 крупнейших армий с `UnderArmsShare`, `MissingMenShare`, топ-10 получателей и отправителей мигрантов, `NewcomerShare`, нативистские движения. Потоки считаются нарастающим итогом, как в `--kin`, а не по 2048-строчной хронике.
- **Ожидания за 10 лет мира (3 сида)**: среди получателей — США, Германия, Британия, Канада, Залив; самые высокие доли приезжих — у ОАЭ и Катара; Япония с притоком < 0,2% в год; Молдова и Сирия среди отправителей. Если ни разу не возник ни один нативистский блок, механика мертва; если они возникли больше чем в 15 странах, она перегрета.
- **Война**: сценарий с убитыми 2% населения. Рождаемость −8…−15% в первые 3 года, через 18–22 года приток в резерв на 10–25% ниже, к 25-му году `MissingMen` < 40% от пика. Нужен прогон на 25 лет.
- **Экономика**: при `UnderArmsShare` 5% ВВП падает на 4–7%. Отдельно проверяется, что мирный мировой ВВП не сдвинулся больше чем на 1% (сравнение A/B через `dof-balance-ab` с `--define Features.manpower_model=false`).
- **Производительность**: Manpower.Tick не дороже 1 мс в неделю на 200 стран; день в позднем мире дорожает не больше чем на 2%. Цикл `BestDestination` остаётся O(N) на источник.
- **Сейвы**: `--replay-save` на старом сейве: резерв выше потолка тает (2% превышения в неделю), падений нет.

## Файлы

**Новые:**
- `Scripts/Simulation/Manpower.cs` — потолок, призыв, `Bury`, недельный разнос убитых, `InvalidShare`, `MissingMen`, `UnderArmsShare`, `Explain`;
- `Scripts/Simulation/Nativism.cs` — приток за 52 недели, радикализм, порог движения;
- `game/common/defines/80_manpower.json`, `81_migration.json`, `game/common/laws/60_refugee_policy.json`;
- запись `nativist_bloc` в `game/common/movements/`, модификаторы `lost_generation` и `labour_pact` в `game/common/modifiers/`, сделка `labour_pact` в `game/common/deals/`;
- локализация en/ru;
- `Tools/SimBench/ManpowerReport.cs`.

**Правятся (вне экономики):**
- `SimulationCore.cs` — только новые поля `Nation` (`ManpowerCeiling`, `MissingMen`, `InvalidShare`, `Veterans`, `PendingDead`, `CultureShares`) и `ScenarioState` (`WarScar`, `NewcomerShare`, `TemporaryShare`, `UnderArmsShare`), плюс сохранение в `SaveRows.cs`;
- `Defines.cs` — свойства для новых defines;
- `WarWeariness.cs` — тело `Mobilise` уходит в `Manpower.Tick` за флагом;
- `MilitaryCore.cs` — `Seed` 1617-1626;
- `WarLosses.cs` — одна строка в `Record`;
- `Demobilisation.cs`, `Escalation.cs:930`, `ProxyWars.cs:335`;
- `ScriptedEffects.cs` (`manpower_callup`, ограничение для `manpower`) и триггеры;
- `BrainDrain.cs` (`BestDestination`, `Tick`), `WarDamage.cs` (фильтр `abroad`);
- `DemographicCycle.cs` (unborn, `AddTo` для инвалидов, жилья и исследований);
- `PoliticalPolicies.cs` и AI-законы;
- JSON законов о службе и миграции, `decisions/00_decisions.json`, `60_civic_decisions.json`, `scripted_triggers/00_country.json`.

**Экономические файлы (там сейчас работают сессии ВВП и казны) — только в срезе 4, после их коммита:**
- `MarketCore.cs` — одна строка в `WorkersAvailable`;
- плоский `output` у законов о службе (меняет ВВП);
- денежные переводы и `migration_upkeep` в ledger/казне (`EconomyCore.cs`, файлы ledger) — отдельной строкой ledger, по образцу «Money drains».

`HouseholdEconomy.cs` и `EconomyCore.cs` не правятся вовсе: используется существующий `AdjustStatePopulation`, а `demographic_boom` уже читается.

## Срезы

**Срез 1 — одна сессия: «резерв из людей».** Экономических файлов не касается.
1. `80_manpower.json` с `Features.manpower_model`; поля `Nation.ManpowerCeiling`.
2. `Manpower.cs`: `Ceiling` (п. 1) и притяжение к нему (п. 2); `WarWeariness.Mobilise` за флагом вызывает его.
3. `MilitaryCore.Seed` по новой формуле.
4. Ключи `draft_share`, `call_up_rate`, `draft_women`, `draft_deferment` в JSON законов о службе; законы `service_universal` и `service_universal_both_sexes` с локализацией.
5. п. 3: `manpower_callup` вместо плоских прибавок, `short_of_men` через `manpower_ratio`, Escalation и ProxyWars.
6. Строка в существующей подсказке резерва: потолок и четыре множителя (текст, без новой карточки).
7. Проверка: `FormableTests --manpower-only`, сборка, `SimBench 1825 <3 сида> --soak` (войны не должны остановиться из-за голода по людям: сравнить число дивизий и исход войн с базой), `--wars`, замер времени дня.

Срез 2 — рубцы (п. 5, 6, 7). Срез 3 — притяжение и законы о дверях (п. 8–10) и SimBench `--manpower`. Срез 4 — рабочие под ружьём и `women_to_the_factories` (п. 4); согласовать с сессиями ВВП. Срез 5 — беженцы и нативизм (п. 11–12). Срез 6 — интерфейс (карточки, режимы карты), после коммита плашки ресурсов. Срез 7 — ИИ-логика законов и `labour_pact`, затем денежные переводы.

## Риски

- **Войны глохнут.** Резерв станет в 4–8 раз меньше, и ИИ может перестать восполнять фронт. Меры: `ManpowerOpeningFill 0.8`, ИИ повышает закон при `manpower_ratio < 0.3`, оккупация даёт ×4. Мерить это `--wars` до и после.
- **Двойной счёт богатства** в притяжении (`livingGap` и заработок). Мера: снизить вес `livingGap` до 0.9 и проверить сверкой получателей.
- **Столкновение с сессиями ВВП и казны.** Всё, что двигает выпуск или деньги, вынесено в срез 4 и срез 7, после их коммитов.
- **Старые сейвы.** Резерв выше потолка тает плавно. Новые поля имеют значения по умолчанию.
- **Производительность при 1000+ стран.** `BestDestination` идёт O(N) на источник, итого O(N²) с разбивкой по стадиям. Топ-3 не меняет сложность; `CultureShares` раз в 4 недели стоит O(групп). Если станет дорого, кандидатов ограничим соседями, торговыми партнёрами и топ-50 по ВВП на душу.
- **Ассиметрия пола.** Пол хранится одним числом на страну, а не по регионам, как в B. Регионального неравенства пола (Кавказ против центра) не будет. Это сознательная цена за простоту и скорость; регион носит только `WarScar`.
- **Нативизм как наказание без выхода.** Нужен выход: натурализация, `assimilation` от законов об образовании и время. Цикл «открыли → закрыли» должен давать и обратный ход, иначе мир застынет закрытым. Мерить по числу смен законов за 30 лет.

## Оценка проектов

| Критерий (1–5) | A: ясность AoH | B: органическая история |
|---|---|---|
| Интересно и читается с одного взгляда | 5: одна формула, «Притяжение» с местом в мире | 4: богаче, но больше чисел на экране |
| Органичная история | 4 | 5: беби-бум, потерянное поколение, беженцы, гастарбайтеры-неграждане |
| ИИ пользуется тем же | 4 | 4 |
| Моддится данными | 4 | 4 |
| Производительность | 5: одно число пола на страну, накопление убитых | 3: `MaleShare` по регионам, softmax |
| Вписывается в существующее | 5: верно видит 11 существующих законов о миграции | 3: заводит «новую категорию», которая уже есть, и новые имена законов поверх существующих |
| Измеримо | 4: срезы и цифры | 3 |
| **Итого** | **31** | **26** |

## Что откуда взято

- **Из A (основа):** формула потолка `WorkingShare × SexEligible × Fit × draft_share`; поле и его около 40 читателей не трогаются; отмена резерва из ничего и `manpower_callup` вместо плоских прибавок; `short_of_men` через `manpower_ratio`; одна строка в `WorkersAvailable` и снижение плоского `output`; `Bury`, `MissingMen` на уровне страны, `WarScar` по регионам, `InvalidShare`; отсрочка для образованных и штраф к исследованиям; притяжение как расширение `BestDestination` (заработок через логарифм, свобода, диаспора по `CultureShares`, `migration_pull/intake`); `NewcomerShare` и натурализация; нативизм через `DemographyFloodShare`; карточка «Притяжение» с мировым местом; деление на срезы.
- **Из B:** больница и голод в годности; `call_up_rate` по закону вместо одной скорости; `Features.manpower_model` как откат; `ManpowerOpeningFill`; рождаемость, пока мужчины в строю; беби-бум через существующий `demographic_boom` и «потерянное поколение» с реваншистами; `women_to_the_factories`; возраст уезжающих с обеих сторон; закон о беженцах как отдельная категория; гастарбайтеры-неграждане, закон о возвращении и гражданство за службу; трудовой договор с конкретной страной (`labour_pact`); давление на жильё; сверка с Финляндией, Израилем, СССР; Катар, Япония и Молдова как цели проверки.
- **Своё (найдено при проверке кода):** `WarDamage.Flee` уже отправляет беженцев к соседям, поэтому закон о беженцах фильтрует его список `abroad`, а не заводит второй поток. `demographic_boom` уже читается в `HouseholdEconomy.cs:157`, поэтому беби-буму не нужен экономический код. Множитель `Youth` вместо отдельного притока через взросление сводит два правила A к одному. Два новых закона, `service_universal` и `service_universal_both_sexes`, понадобились потому, что Израиль и Финляндия не сходятся с существующей лестницей.
