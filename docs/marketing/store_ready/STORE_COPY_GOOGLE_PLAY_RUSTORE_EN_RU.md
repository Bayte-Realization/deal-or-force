# Deal or Force: store copy (Google Play, RuStore, Steam, itch.io; EN and RU)

> **Checked against the code on 23 November 2026 (`EntertAlpha 0.8.5.3`).** Every number below
> comes from the game data: 34,138 provinces and 1,846 regions (`game/map_data/compiled.json`,
> `definition.csv`), 7,368 cities (`game/map_data/cities`), 238 countries plus 4 de facto states
> (`game/common/countries`), 21 armed movements and de facto governments on the first day
> (`game/history/factions/2024`), 63 goods (`game/common/goods`), 94 recipes that make them
> (`game/common/production`, not counting 37 consumption bills and 24 production methods),
> 16 megaprojects (`game/common/grand_projects`), 7 kinds of dependent state
> (`game/common/subjects/00_subjects.json`), 11 delegable areas of government
> (`Scripts/Simulation/Delegation.cs`). Character counts were measured on the text inside each
> block. Limits: title 30, Google Play and RuStore short description 80, full description 4,000,
> Steam short description 300.
>
> **Removed on 23 November** because the code does not do it: trade blocked by straits (trade is
> reached by great-circle distance, `TradeCore.cs:96-97`; only fleets route around a closed strait),
> 1914 and fantasy as playable eras (they are modder templates in `mods/_templates`), "the AI plays
> by the same rules" without qualification (`docs/PLAYER_AI_PARITY_2026_11_18_RU.md`), and the
> "2,000 trade routes" figure (a count from one October run, not a property of the game).
>
> **Before publishing on Google Play or RuStore:** the Android build is a technical preview
> installed by hand (`docs/WEEK_REVIEW_2026_11_23_RU.md`, "Мобильная версия"). Living leaders,
> armed groups as playable sides and Crimea also need a decision against each store's rules.

---

## 1. Google Play, English (`en-US`)

### App title (`29 / 30`)
```text
Deal or Force: grand strategy
```
*(Short alternative, `13 / 30`: `Deal or Force`)*

### Short description (`76 / 80`)
```text
The world of 1 January 2024, every war already on the map. Play any country.
```

### Full description (`2,388 / 4,000`)
```text
The world as it stood on the morning of 1 January 2024, with every war of that day already on the map. Pick any of more than 200 countries and decide what happens next.

THE WORLD OF 1 JANUARY 2024
• The war in Ukraine, the Sudanese army against the Rapid Support Forces, the Houthis in Yemen, the M23 in eastern Congo, the resistance in Myanmar, Syria and Somalia start from the lines they held that morning.
• 21 armed movements and de facto governments hold their own ground and fight with their own forces.
• 34,138 provinces, 1,846 regions and more than 7,000 cities on a 3D globe or a flat map.
• Observer mode: let the computer run every country and watch the year unfold.

ECONOMY
• 63 goods made by 94 production recipes. Prices come from what is produced and traded.
• An embargo or a war cuts trade between the two countries. Distant partners are less attractive than near ones.
• 16 megaprojects, from high-speed rail to a fusion grid, which allies can co-fund. A hostile supplier of a key good slows the work down.

DIPLOMACY
• Alliances, guarantees, non-aggression pacts, trade agreements and embargoes.
• Seven kinds of dependent state, from protectorate to personal union, each with its own tribute and war duties.
• Peace is written at a conference: every province you keep costs war score, and the screen tells you why a demand cannot go through.
• Hand any of 11 areas of government (budget, trade, army, navy, diplomacy and more) to your own staff, and take them back at any time.

WAR
• Fronts move province by province, and armies need supply.
• Artillery, rocket batteries, aircraft, drones and air defence are formations of their own, based in regions and sent on missions.
• A battle window shows the phase of the fight and each modifier: flanking, river crossings, supply, encirclement.
• Fleets and convoys have to sail around a strait or canal held by an enemy.
• Unstable governments face coups, neglected regions break away, and civil wars are fought by their own sides.

FREE AND OPEN SOURCE
• Free to play. The source code is open under the GNU GPL v3.
• Nearly everything is data: goods, buildings, laws, events and history are JSON files that a mod can change.
• English and Russian.

Built by one person, openly with the help of AI. This is an early build: the phone interface is still being adapted, and some things will break. Bug reports are welcome.
```

### Category, tags and content rating (Google Play)
- **Category**: `Games → Strategy`
- **Tags (up to 5)**: `Strategy`, `Grand Strategy`, `Tactical`, `Offline`, `Single player`
- **Expected age rating** (not yet rated): `PEGI 12` / `ESRB Teen`. Map-level military conflict and
  geopolitics, real countries and armed groups, no gore, no gambling, no loot boxes.

---

## 2. RuStore и Google Play, русский (`ru-RU`)

### Название приложения (`24 / 30`)
```text
Deal or Force: стратегия
```
*(Короткий вариант, `13 / 30`: `Deal or Force`)*

### Краткое описание (`72 / 80`)
```text
Мир 1 января 2024 года, все войны уже на карте. Играйте за любую страну.
```

### Полное описание (`2 440 / 4 000`)
```text
Мир таким, каким он был утром 1 января 2024 года, и все войны того дня уже на карте. Выберите любую из более чем 200 стран и решите, что будет дальше.

МИР 1 ЯНВАРЯ 2024 ГОДА
• Война в Украине, армия Судана против Сил быстрого реагирования, хуситы в Йемене, M23 на востоке Конго, сопротивление в Мьянме, Сирия и Сомали начинаются с тех линий, что были в то утро.
• 21 вооружённое движение и непризнанная власть держат свою землю и воюют своими силами.
• 34 138 провинций, 1 846 регионов и больше 7 000 городов на 3D-глобусе или плоской карте.
• Режим наблюдателя: все страны ведёт компьютер, а вы смотрите, как проходит год.

ЭКОНОМИКА
• 63 товара и 94 производственных рецепта. Цены складываются из того, что произведено и продано.
• Эмбарго или война обрывают торговлю между двумя странами. Дальние партнёры выгодны меньше, чем близкие.
• 16 мегапроектов, от скоростной железной дороги до термоядерной энергосети, которые союзники могут строить вскладчину. Враждебный поставщик ключевого товара замедляет стройку.

ДИПЛОМАТИЯ
• Союзы, гарантии, пакты о ненападении, торговые соглашения и эмбарго.
• Семь видов зависимых государств, от протектората до личной унии, у каждого свои выплаты и обязанности в войне.
• Мир пишется на конференции: каждая провинция, которую вы оставляете себе, стоит военного счёта, а экран объясняет, почему требование не проходит.
• Любое из 11 направлений управления (бюджет, торговля, армия, флот, дипломатия и другие) можно передать своему штабу и забрать обратно в любой момент.

ВОЙНА
• Фронт движется по провинциям, армиям нужно снабжение.
• Артиллерия, ракетные части, авиация, беспилотники и ПВО действуют как отдельные соединения со своими базами и заданиями.
• Окно боя показывает фазу сражения и каждый модификатор: фланги, переправы, снабжение, окружение.
• Флоту и конвоям приходится обходить пролив или канал, которым владеет противник.
• Нестабильным правительствам грозят перевороты, заброшенные регионы откалываются, а в гражданской войне воюют её собственные стороны.

БЕСПЛАТНО И С ОТКРЫТЫМ КОДОМ
• Игра бесплатна. Исходный код открыт по лицензии GNU GPL v3.
• Почти всё в игре хранится в данных: товары, здания, законы, события и история лежат в файлах JSON, которые может изменить мод.
• Русский и английский языки.

Игру делает один человек, открыто и с помощью ИИ. Это ранняя сборка: интерфейс для телефона ещё дорабатывается, и что-то будет ломаться. Сообщения об ошибках приветствуются.
```

### Категория и теги (RuStore)
- **Категория**: `Игры → Стратегии`
- **Ключевые слова**: `гранд-стратегия`, `геополитика`, `военная стратегия`, `карта мира`, `дипломатия`, `экономическая стратегия`, `офлайн стратегия`, `3D глобус`, `моды`
- **Ожидаемый возрастной рейтинг** (ещё не присвоен): `12+`. Военные конфликты на карте мира,
  реальные страны и вооружённые группировки, без сцен натуралистичного насилия.

---

## 3. Steam and itch.io, English and Russian

### Steam short description, English (`237 / 300`)
```text
The world of 1 January 2024, with every war of that morning already on the map: Ukraine, Sudan, Yemen, Myanmar, eastern Congo. Play any of more than 200 countries: trade, make peace or fight. Free and open source, in English and Russian.
```

### Steam short description, русский (`236 / 300`)
```text
Мир 1 января 2024 года, и все войны того утра уже на карте: Украина, Судан, Йемен, Мьянма, восток Конго. Играйте за любую из более чем 200 стран: торгуйте, заключайте мир или воюйте. Бесплатно, с открытым кодом, на русском и английском.
```

### Steam tags (20, in priority order)
`Grand Strategy`, `Strategy`, `Political Sim`, `Diplomacy`, `War`, `Military`, `Real-Time with Pause`, `Simulation`, `Sandbox`, `Economy`, `Management`, `Modern`, `Alternate History`, `Moddable`, `Wargame`, `Tactical`, `Replay Value`, `Singleplayer`, `Realistic`, `Politics`

### AI-generated content disclosure (Steam "AI Generated Content" field)

English:
```text
Pre-generated content. Some of the game's content was made with generative AI tools, then selected, edited and checked by the developer:
• Artwork: many interface pictures, event illustrations, icons and the store art were generated with Nano Banana 2 through the Creative Claw service. A few original flag drawings were made with OpenAI image generation.
• Music: the soundtrack was generated through Creative Claw, mostly with Lyria 3.5 and one loop with ElevenLabs Music v2.5.
• Sound effects: most interface and map sounds were generated with ElevenLabs Sound Effects v2 through Creative Claw.
• Code: written by one developer with the help of AI coding assistants.
The game does not generate any content while you play. The map is built from open data (NASA, ESA GlobCover, Natural Earth) and most national flags come from Wikimedia Commons; their sources are listed in the game files.
```

Русский:
```text
Заранее созданный контент. Часть содержимого игры сделана генеративными ИИ-инструментами, затем отобрана, отредактирована и проверена разработчиком:
• Графика: многие иллюстрации интерфейса и событий, значки и изображения для магазина созданы моделью Nano Banana 2 через сервис Creative Claw. Несколько оригинальных флагов нарисованы генератором изображений OpenAI.
• Музыка: саундтрек создан через Creative Claw, в основном моделью Lyria 3.5, одна петля моделью ElevenLabs Music v2.5.
• Звуки: большинство звуков интерфейса и карты созданы моделью ElevenLabs Sound Effects v2 через Creative Claw.
• Код: пишет один разработчик с помощью ИИ-ассистентов.
Во время игры ничего не генерируется. Карта построена на открытых данных (NASA, ESA GlobCover, Natural Earth), большинство государственных флагов взято с Wikimedia Commons; источники перечислены в файлах игры.
```

Sources: `game/music/audio/README.md`, `docs/ADAPTIVE_SOUNDTRACK_RU.md`, `docs/QUIET_SOUNDTRACK_RU.md`,
`game/sound/audio/README.md`, `game/sound/05_news.json`, `game/sound/06_map_ambience.json`,
`docs/ATLAS_MEDIA_LIBRARY_RU.md`, `game/common/event_pictures/00_event_pictures.json`,
`game/gfx/leaders/anonymous_portrait.source.json`, `docs/art_sources/roman_flags_2026_10_06.md`,
`docs/marketing/CAPSULE_PROMPTS_2026_11_21_RU.md`, `docs/THIRD_PARTY_MEDIA.md`.

---

## 4. Store graphics inventory (`docs/marketing/store_ready/`)

These images are generated art. The week review found glow, invented geography and the wrong
era of tanks in them; plan item 29 replaces them with capsules built from game screenshots.

| Asset | Path | Exact dimensions | Usage |
|---|---|---:|---|
| Steam main capsule | `steam/main_capsule_1232x706.png` | `1232 × 706` | Steam featured and front-page carousel |
| Steam header capsule | `steam/header_capsule_920x430.png` | `920 × 430` | Steam store header and library recent |
| Steam small capsule | `steam/small_capsule_462x174.png` | `462 × 174` | Steam search and discovery lists |
| Steam vertical capsule | `steam/vertical_capsule_748x896.png` | `748 × 896` | Steam seasonal and thematic sales |
| Steam library capsule | `steam/library_capsule_600x900.png` | `600 × 900` | Steam library grid |
| Steam library hero | `steam/library_hero_1920x620.png` | `1920 × 620` | Steam library banner (no text) |
| Steam library logo | `steam/library_logo.png` | `1280 × 958` | Transparent title overlay |
| Google Play / RuStore feature | `mobile/google_play_feature_1024x500.png` | `1024 × 500` | Google Play and RuStore feature banner |
| Mobile store icon | `mobile/store_icon_512x512.png` | `512 × 512` | Google Play and RuStore app icon |
| itch.io cover | `itch/itch_cover_630x500.png` | `630 × 500` | itch.io catalogue cover |
