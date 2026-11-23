# Dev Diary #1 — One Living Planet: How Deal or Force Simulates Diplomacy, Supply, Warfare, and Mobile Play (EN & RU)

> **Editorial Note.** Written as Launch Dev Diary #1 for itch.io, Steam Announcements, Telegram, and strategy communities. Strictly honest, grounded exclusively in verified implemented systems, with **zero competitor references** and zero fabricated metrics.

---

# English Version

## Dev Diary #1: Deal or Force — Building One Living Planet for PC and Android

Welcome to the first major development diary for **Deal or Force**!

When I started building *Deal or Force* as a solo developer under **Bayte Realization**, I wanted a modern geopolitical grand strategy game driven by a single simple rule: **every nation on Earth should live inside the same real simulation.**

No scripted magic modifiers when you declare an embargo. No black-box combat rolls that hide why a division broke. And no separate "lite" ruleset just to fit on a phone.

Today, after months of open alpha iterations on itch.io, I want to walk through the four core pillars that define *Deal or Force* right now—and where we are heading next for PC and Android.

---

### 1. A 3D Globe Where Geography and Supply Actually Matter
The campaign opens on the morning of **1 January 2024** across more than **35,000 provinces**, **1,850 regions**, and **7,000+ cities**.

Under the hood, the world runs a physical economy of **63 goods**, **114 production chains**, and over **2,000 trade routes**. Those routes pass through real ports and strategic maritime chokepoints. If a war or blockade shuts down a vital strait, your factories—and your allies' factories—feel the missing shipments directly.

To turn peacetime into more than waiting for construction bars, we recently introduced **Strategic Megaprojects & International Consortia**: multi-stage national undertakings (transcontinental high-speed rail, fusion grids, deep-water megaports, semiconductor fabs, orbital complexes) that consume real treasury, political capital, military experience, and market goods. Allies can co-fund a consortium for shared dividends—while rivals can deploy intelligence networks to sabotage construction.

### 2. Deal at the Table—With Transparent Reasoning
Diplomacy should never feel like clicking blind buttons until a hidden number turns green.

In *Deal or Force*, the redesigned **Country Dossier** lays out your bilateral standing, opinion drivers, power balance, active wars, alliances, truces, guarantees, embargoes, subjects, and international blocs in one scannable view. At the peace table, every territory carries a clear war-score price, and the AI explains why it refuses or accepts a deal.

And if you want to focus on grand strategy without micro-managing every domestic slider, our **AI Staff** system lets you delegate up to **11 branches of government**—or step out completely into **Observer Mode** with instant same-frame country switching while paused to watch the entire planet play itself out.

### 3. Force on the Front: Readable Warfare Without Guesswork
A recent overhaul focused squarely on making battles legible at a glance:
- **Live Battle Telemetry** breaks every engagement down into tactical phases, momentum, estimated duration, combat width, and color-coded modifier chips—covering multi-direction flanking entrenchment bypass, river crossings, combined-arms synergy, supply cutoffs, and encircled pocket collapse.
- **Map Readability** pairs dual-bar army counters (organization and strength) with diplomatic stance trims, tactical status pips (encircled, low supply, entrenched, moving), 128×128 battle progress bubbles, and directional assault arrows.
- **Long-Range Forces**—artillery, rocket batteries, drones, air defence, and aviation—operate as real formations from regional bases with range coverage and daily attrition over defended skies.

### 4. Multi-Era Modding & Bringing the Full Simulation to Android
*Deal or Force* is open source under **GNU GPLv3** and built on **Godot 4.8 (.NET 10)**. Our new multi-era modding architecture separates the internal simulation clock from the display calendar, adds declarative `scenario.json` manifests, scoped `replace_paths` / `exclude_paths`, era feature flags (so a 1914 or fantasy scenario disables modern missiles cleanly for both players and AI), and a built-in `ModValidator`—shipped alongside ready-to-clone **1914** and **fantasy total-conversion** templates.

Because our simulation core is pure C# completely decoupled from engine nodes, we are also bringing the **full campaign to Android**—with tiered mobile shaders, safe-area layout support, pinch-to-zoom globe controls, and a dedicated 4-mode touch toolbar (`Select`, `Order`, `Box Select`, `Map Pan`).

### What We Want to Hear From You
Before our upcoming Steam and mobile store launches, I'd love your honest feedback on three concrete questions:
1. When you open a foreign country's diplomacy panel, what is the **first piece of information** you look for?
2. Which **scenario era** would you love to play or mod first—2024 modern geopolitics, 1914, Cold War, near-future, or original fantasy?
3. If you play strategy games on Android or tablets, what makes or breaks touch controls on a dense world map?

Try the latest Windows alpha build on **itch.io** (`https://bayte-realization.itch.io/deal-or-force`), drop your feedback in the comments or Telegram, and stay tuned for Dev Diary #2 on how we optimized a 35,000-province deterministic simulation!

---

# Русская версия

## Дневник разработки №1: Deal or Force — одна живая планета для ПК и Android

Привет! Это первый большой дневник разработки **Deal or Force**.

Когда я начинал делать *Deal or Force* в одиночку под маркой **Bayte Realization**, главной целью было создать геополитическую гранд-стратегию с одним честным принципом: **каждая страна на планете должна жить в одной и той же настоящей симуляции.**

Никаких «нарисованных» процентов при эмбарго. Никаких «чёрных ящиков» в боях, когда непонятно, почему сломалась дивизия. И никакой урезанной «облегчённой» версии правил ради выхода на смартфонах.

Сегодня, после серии открытых альфа-выпусков на itch.io, расскажу о четырёх опорах, на которых стоит *Deal or Force*, и о наших ближайших шагах к релизу на ПК и Android.

---

### 1. 3D-глобус, где география и логистика решают всё
Кампания начинается утром **1 января 2024 года** на карте из более чем **35 000 провинций**, **1 850 регионов** и свыше **7 000 городов**.

Внутри работает физическая экономика: **63 товара**, **114 производственных цепочек** и более **2 000 торговых маршрутов**, проходящих через реальные порты и морские проливы. Если война или блокада перекрывает ключевой пролив, ваши заводы — и заводы ваших союзников — физически лишаются поставок.

Чтобы мирное время было таким же напряжённым, как и война, мы добавили **стратегические мегапроекты и международные консорциумы**: многоэтапные стройки (трансконтинентальные ВСМ, термоядерные сети, мегапорты, фабрики полупроводников, космодромы), которые требуют реальных денег, политкапитала, военного опыта и товаров с рынка. Союзники могут вложиться в консорциум ради постоянных дивидендов — а соперники способны отправить разведсеть на диверсию.

### 2. Сделка за столом — без угадывания скрытых правил
Дипломатия не должна сводиться к слепому перебору кнопок.

В обновлённом **Досье страны** на одной компактной вкладке собраны двусторонние отношения, факторы мнения, сравнение сил, активные войны, союзы, перемирия, гарантии, эмбарго, субъекты и международные блоки. На мирной конференции каждая провинция имеет ясную цену в военном счёте, а ИИ прямо объясняет причины согласия или отказа.

А если хочется сосредоточиться на большой стратегии без рутины, **Штаб ИИ** позволяет делегировать до **11 ветвей управления** — либо полностью переключиться в **Режим наблюдателя** с мгновенной сменой страны прямо на паузе, чтобы наблюдать за живым миром.

### 3. Сила на фронте: наглядная война без «чёрного ящика»
Недавняя крупная переработка боёв сделала фронт читаемым с первого взгляда:
- **Живая телеметрия боя** раскладывает каждое сражение на тактические фазы, импульс, прогноз длительности, ширину фронта и цветные чипы модификаторов: фланговый обход окопов с нескольких направлений, форсирование рек, общевойсковое взаимодействие пехоты и брони, отрезанное снабжение и развал котлов.
- **Читаемость карты**: жетоны армий с двойными шкалами (организация и сила), окантовкой дипломатической стойки, тактическими индикаторами (котёл, дефицит снабжения, окопы, марш), пузырями прогресса боя `128×128` и направленными стрелками штурма.
- **Дальнобойные силы** (артиллерия, ракеты, дроны, ПВО, авиация) действуют со своих региональных баз в реальном радиусе и несут потери над прикрытым небом.

### 4. Моды любых эпох и полноценный порт на Android
*Deal or Force* распространяется с открытым исходным кодом под лицензией **GNU GPLv3** и написана на **Godot 4.8 (.NET 10)**. Новая архитектура межэпохального моддинга отделяет внутренний календарь симуляции от отображаемого, поддерживает декларативные манифесты `scenario.json`, `replace_paths` / `exclude_paths`, флаги эпох (отключение современных ракет в 1914 году убирает их и у игрока, и у ИИ) и встроенный `ModValidator` — вместе с готовыми шаблонами **1914 года** и **фэнтези-конверсии**.

Поскольку симуляция написана на чистом C# без привязки к узлам движка, мы переносим **полноценную кампанию на Android** — с мобильными уровнями качества шейдеров, поддержкой вырезов экрана, жестовым управлением 3D-глобусом и 4-кнопочной панелью режимов касания (`Выбор`, `Приказ`, `Рамка`, `Карта`).

### Три вопроса к вам
Перед подготовкой страниц в Steam, Google Play и RuStore мне особенно важно услышать ваше мнение:
1. Когда вы открываете дипломатию чужой страны, **на какую цифру или блок вы смотрите первым делом**?
2. В какую **эпоху** вам интереснее всего сыграть или сделать мод — современность 2024, 1914 год, холодную войну, будущее или авторское фэнтези?
3. Если вы играете в стратегии на смартфоне или планшете, **что для вас критичнее всего в сенсорном управлении** большой картой?

Актуальную Windows-альфу можно скачать на **itch.io** (`https://bayte-realization.itch.io/deal-or-force`). Пишите отзывы в комментариях и в нашем Telegram — а в Дневнике №2 расскажу, как мы ускоряли детерминированную симуляцию 35 000 провинций и побеждали фризы!
