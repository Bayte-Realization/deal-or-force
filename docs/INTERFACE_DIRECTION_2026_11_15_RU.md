# Deal or Force: направление интерфейса

*15 ноября 2026. Исследование: EU4, EU5, HoI4, Victoria 3, CK3, Stellaris, Age of History 2/3 (ПК и мобильная), Supremacy 1914, Conflict of Nations. Скриншоты лежат в `tmp_logs/ui-research/<игра>/` (в git они не попадают).*

---

## 0. Почему сейчас «видеомонтажёр», а не игра

Это видно по скриншотам `docs/screenshots/0.6.6/campaign.jpg` и `country-picker.jpg` и по концептам из `docs/design/interface-2026-11-15/`.

**Скелет у нас уже правильный.** Флаг и страна сидят слева вверху, ресурсы идут полосой сверху, дата и скорость справа вверху, сводка справа, карта на весь экран. Так же устроены EU4, HoI4, Vic3 и AoH3. **Не хватает материала.** Монтажное ощущение дают конкретные вещи:

| Что на экране | На что похоже | Как это сделано в жанре |
|---|---|---|
| ▶ 1 2 3 4 5 и нота справа вверху | транспорт плеера | у EU4 латунная табличка с датой, у Vic3 бронзовый циферблат-астролябия с I–V |
| 8 чекбоксов внизу справа (CLOUDS, FOG, EVENTS, TRADE, CITIES, NAMES, BUILDING, PAN) | переключатели дорожек на таймлайне | слои убраны в настройки, на экране только кнопки режимов карты (у EU4 это миниатюры в рамках) |
| карточки армий внизу слева в ряд | клипы на таймлайне | портреты генералов (HoI4) или фишки на самой карте |
| 12 одинаковых ячеек ресурсов, подписи капслоком в 9 px | панель метрик | иконка, крупное число и маленькая дельта; иконки нарисованные, у каждого ресурса свой цвет |
| 16 пунктов левой колонки с крошечными подписями | панель инструментов редактора | у Vic3 и Stellaris рейка из 10–15 круглых медальонов без текста, у HoI4 горячие клавиши |
| все панели: тёмная заливка и рамка в 1 px | интерфейс приложения | рамка с фаской, тень, утопленные «колодцы» для списков, плашка заголовка |
| силуэт вместо портрета в окне выбора страны | заглушка формы | портрет в рамке есть везде: в выборе страны, в дипломатии, в событиях |

Что видно по отклонённым концептам:
- **A, B, AB.** Всё стало крупнее и чище, но язык остался языком SaaS-панели: кнопки «View details →», цветная полоса слева у карточки, синяя CTA-кнопка «Next». Игроки EU5 называют такое «как мобильная игра» и «как современная Civ» (https://steamcommunity.com/app/3450310/discussions/0/604155654554287067, https://steamcommunity.com/app/3450310/discussions/0/667222425710376904/).
- **D, командный центр.** Таблица «Incident log», кнопки F1–F4, ENTER. Это интерфейс военного софта, а не стол стратега. Он плоский точно так же, только темнее.
- **C, кабинет.** Единственный концепт, в котором есть душа: телеграммы, сургуч, перо, карточки армий в ящике. Ошибка в другом: карта превратилась в картину на столе и потеряла примерно 40% экрана. Жанр так никогда не делает. Карта идёт до краёв, а стол появляется только как рамка при самом дальнем зуме (CK3, Vic3). Материалы из C стоит взять, раскладку нет.

**Вывод.** Раскладку менять почти не нужно. Нужен материал: глубина, свет, предметы, лица. Плюс уборка «монтажных» элементов.

---

## 1. Чего ждут любители жанра (это сохраняем)

1. **Слева вверху «я», справа вверху «время».** Флаг или герб, портрет правителя и название страны стоят слева вверху. Рядом полоса ресурсов, где у каждого числа есть месячная дельта, зелёная или красная. Справа вверху дата, пауза и скорости 1–5, пробел ставит паузу. Так у EU4, EU5, HoI4, Vic3, CK3, Stellaris, AoH3, S1914 и CoN. Игрок смотрит туда в первую же секунду.
2. **Карта главная и идёт до краёв.** Интерфейс лежит поверх неё по углам и краям. Слева выезжает одна панель шириной около 30% (EU5: 570 px, Vic3: 560 px, AoH3: 490–580 px) с вкладками, «Назад» и крестиком, остальная карта остаётся живой. На весь экран открываются только деревья: технологии, фокусы, законы (HoI4 steam_03/05, Vic3 steam_04/06).
3. **Справа всегда видна сводка (outliner).** В ней армии, войны, стройки, кризисы. Секции сворачиваются и закрепляются, клик по строке переносит камеру. В Vic3 там звёздочки-пины, в Stellaris вкладки. Слабое место Stellaris: игроки жалуются на прокрутку сводки (https://steamcommunity.com/app/281990/discussions/0/3975051032845761844).
4. **Каждое число объясняет себя.** Подсказка раскладывает значение по источникам. У CK3 и Vic3 подсказки вложенные, с подчёркнутыми терминами. Именно их игроки называют лучшим учебником в жанре (r/EU5, https://www.reddit.com/r/EU5/comments/1kqn6dd/what_is_happening_with_the_uis/).
5. **События с лицом и предметом.** Модальное окно по центру, карта за ним не гаснет. В окне иллюстрация, 2–3 фразы и 1–3 варианта, эффект варианта виден при наведении. У EU4 это пергамент между свитками, у HoI4 газета «World News», у Vic3 картина или портрет в золотом овале.
6. **Карта как атлас.** Крупные названия стран идут разрядкой по форме территории (EU4, HoI4, AoH3 на EB Garamond). Армии показаны фишкой «флаг + число + полоса». Стрелки движения изогнутые. Режимов карты много, они собраны в компактный блок с горячими клавишами.
7. **Война лицом к лицу.** Два портрета друг напротив друга и полоса счёта войны (CK3 steam_04, EU5 steam_13, AoH3 steam_05).

---

## 2. Откуда у Paradox и AoH объём и жизнь, и как это дёшево сделать в Godot

### 2.1. Приёмы

**Единый свет.** Во всех хороших примерах свет падает сверху слева. Поэтому у каждой панели светлая кромка в 1–2 px сверху и тёмная снизу (HoI4: «каждая панель читается как приподнятая пластина»). Списки лежат в утопленных колодцах с внутренней тенью сверху. Свет задаётся одним токеном, и все фаски его слушаются.

**Три уровня высоты и не больше.** Это фон-подложка, приподнятая пластина (панель, кнопка) и колодец (список, поле, слот портрета). Тень под пластиной мягкая, около 8–14 px, смещена вниз и вправо. Плоские концепты A/B/AB держатся на одной высоте плюс цветная обводка.

**Рамка с характером, но только по углам.** Орнамент EU4 (золотое рококо) избыточен и не подходит под утверждённую палитру «чернила и бумага». CK3 показывает другой путь: базовые панели почти плоские, а душу дают один орнаментальный элемент на окно, гербы, портреты и нарисованные иконки (CK3 steam_05). Для нас это четыре угловые накладки (металлический уголок, скрепка, штамп), плашка заголовка и разделители. Орнамент нигде не идёт сплошной полосой.

**Материал подложки с зерном.** Ни один хороший пример не использует ровную заливку. Даже тёмные панели HoI4 и CK3 несут едва заметный шум ткани, кожи или камня. Ровный #111923 и даёт ощущение «приложения».

**Бумага как второй материал.** HoI4 кладёт бумажные вкладыши с машинописью на стальную плиту: дивизии, история страны, разведка. Выходит «досье на стальном столе». Vic3 подписывает договоры: сургуч, подпись, «Not signed yet» (steam_03). AoH3 делает уведомления свитками с рваным краем. У нас палитра «чернила и бумага» уже утверждена, значит бумага у нас основной язык, а не украшение.

**Флаги и портреты как объекты.** У Vic3 флаг слева вверху колышется в 3D. CK3 ставит бюст правителя без рамки, он «ломает» край экрана. В Stellaris правитель сидит в своём кабинете, как на видеозвонке. У нас пока квадратный флаг и силуэт.

**Нарисованные иконки вместо глифов.** CK3, EU4, AoH3 и Vic3 рисуют маленькие миниатюры со своим цветом и тенью: монеты, корона, бронзовые статуэтки законов. Ряд таких иконок «читается как поднос с украшениями» (CK3). Наши монохромные глифы в 16 px сливаются.

**Картинка в каждой большой панели.** В EU5 у каждой панели есть живописная шапка: тронный зал, рынок, панорама города. AoH3 даёт портрет в каждой карточке и фото у каждого здания. Плотность информации от этого не растёт, а души много.

**Шрифт с засечками в заголовках, три роли шрифтов.** AoH3 прямо разделяет UI (Noto Sans), цифры на фишках (Inter) и названия на карте (EB Garamond). У Paradox заголовки почти везде с засечками. HoI4 отличается: разреженные капители и машинопись на бумаге.

**Жизнь в основном даёт карта, а не рамки.** У EU4 сам интерфейс статичен, движутся море, облака, армии и дым. В Vic3 растут города, ходят поезда, горит линия фронта, над городами всплывают «+/−». В HoI4 горит глобус мировой напряжённости. Анимация интерфейса малая и предметная: колышется флаг, крутится циферблат, приезжает телеграмма, тикает дата.

### 2.2. Как сделать это в Godot 4.8 дёшево (и на телефоне тоже)

| Приём | Реализация |
|---|---|
| Рамка, плашка, кнопка | `StyleBoxTexture` с `texture_margin_*` (9-slice). Это прямой аналог `corneredTileSpriteType` из .gfx у Paradox и `spriteborder` в Jomini. Края `axis_stretch = TILE_FIT`, чтобы фактура не растягивалась. Текстуры рисуются в 2x, поэтому на 4K ничего не мылится (главная жалоба на EU4: https://admin-forum.paradoxplaza.com/forum/threads/blurry-ui-and-ultrawide-issues.1583978). Метрика `panel.texture_margin` в `00_skin.json` уже есть. |
| Фаска и зерно без новых текстур | Один `canvas_item`-шейдер на тип панели. Фаска считается от расстояния до края rect: светлый край со стороны токена света, тёмный с противоположной. Зерно берётся из маленькой тайловой `NoiseTexture2D` 256², запечённой заранее. Всего одна выборка и пара `smoothstep`, мобильный GPU это потянет. |
| Тени | `StyleBoxFlat.shadow_size/shadow_offset` или запечённая 9-slice тень отдельным слоем. Никаких `BackBufferCopy` и размытия фона: на телефоне это дорого, а «стекло» нам и не нужно. |
| Колодцы | `StyleBoxFlat` с тёмной верхней и светлой нижней границей в 1 px плюс внутренняя тень из 9-slice. |
| Угловой орнамент | Четыре `TextureRect` по углам, якоря берутся из типа панели. Это отдельный слой, мод может его выключить. |
| Флаг | Уже есть `07_flag_cloth.json`. Флаг в шапке HUD рисуется тем же шейдером ткани, синус по UV плюс затенение складок. Цена: один квад. |
| Портрет | Рамка 9-slice, виньетка шейдером, лёгкий параллакс фона на 2–4 px за курсором. Без 3D-сцены. |
| Живые предметы | `Tween`: телеграмма въезжает с перелётом на 3–4°, число «докручивается» к новому значению 0,3 с, дата перелистывается, игла циферблата качается с затуханием. Всё отключается флагом reduced-motion. |
| Карта | Бумажный и рельефный оверлей с альфой, зависящей от зума, как `Overlays.json` в AoH3 (tile + mask + Scale/AlphaScale). Мягкая виньетка по краям экрана, штриховка оккупации (CoN), дым над боями частицами на GPU, малым числом. |
| Звук | Тихие предметные звуки: тик телетайпа в начале месяца, глухой штамп при приказе, шелест бумаги. Каждый отключается отдельно. Звуки всплывашек игроки EU5 замечают и ругают (тред «Popup sound is annoying»). |

### 2.3. Чего избегать: на что жалуются игроки

- **Мыло на высоких разрешениях** при пиксельной вёрстке (EU4, AoH3 со шкалой 0/1/2 и перезапуском: https://steamcommunity.com/app/2772750/discussions/0/591782690120823242/). Нам нужен непрерывный масштаб и 2x-текстуры.
- **Лавина уведомлений** (EU5 до патча 1.1 Rossbach, где пришлось добавлять постоянное отключение и список отключённых: https://www.gamewatcher.com/news/europa-universalis-5-rossbach-update-1-1-hidden-alerts-army-navy-templates-quality-of-life-changes). Плюс обратная беда: про важное (союзник разорвал союз) не уведомили вовсе (Tobold).
- **Подсказки, которые срабатывают при каждом проходе мыши** (Vic3, исправляли в DD #74: https://www.paradoxinteractive.com/games/victoria-3/news/dev-diary-74-ux-improvements; EU5 «Yet another UI rant»).
- **Нужная цифра на два тултипа вглубь** (Vic3 Pop Needs: https://lr.ggtyler.dev/r/victoria3/comments/yf1gsa/pop_needs_should_probably_be_made_into_its_own).
- **Меню внутри меню, пять экранов до одного действия** (CK3 forum, EU5 Gamewatcher, Vic3 Steam: https://steamcommunity.com/app/529340/discussions/0/595141814498957556).
- **Серое на чёрном** (HoI4). Целую моду «Coloured Interface» сделали, чтобы «интерфейс выглядел живее».
- **Панели, закрывающие карту при большом масштабе UI** (HoI4), и левая стена в 30%, которая почти не закрывается (EU5).
- **ИИ-портреты без обработки.** Это визуальная жалоба номер один на AoH3: «goofy and soulless» (https://steamcommunity.com/app/2772750/reviews/?browsefilter=toprated). Любая сгенерированная картинка у нас проходит de-slop, и в каждом слоте есть нейтральная заглушка.
- **«Стерильно, тёмно-синее, slick, post-modern»** (CK3 против CK2: https://forum.paradoxplaza.com/forum/threads/honest-opinion-from-a-ckii-fan.1452172/). Сюда же наш нынешний вид: тёмная заливка без материала.
- **Яркая пёстрая палитра вместо сдержанной** (EU5 против «тёмного золота и синего» EU4).

---

## 3. Авторская идея: три кандидата

Общее для всех трёх: раскладка жанровая (раздел 5), карта до краёв, палитра «чернила и бумага» без золота, живая планета главного меню перетекает в кампанию.

### Идея 1. «Досье на столе»: бумага на тёмной доске

Каждая панель здесь папка-досье: бумажные вкладыши на тёмной подложке из лакированного дерева или кожи, с тенью и едва заметным поворотом при выезде. Вкладки сделаны как каталожные язычки, списки как карточки картотеки. Уведомления это телеграммы на скрепке, свисающие из-под верхней полосы. Цвет штампа означает министерство, у срочных есть штамп «СРОЧНО». Решение оформлено указом: проект выглядит бледной копией под копирку, принятый закон «проштампован» чернилами (приём AoH3 с наследиями «серое → сепия»). При самом дальнем зуме глобус становится печатной картой брифинга на столе ситуационной комнаты: телефон, папка, чашка у края. Эту рамку дают CK3 и Vic3 при максимальном отдалении, а не на постоянном экране. Идея прямо наследует концепту C, но без рамки вокруг карты.

**Риски.** Ближе всех к HoI4, и не все увидят тут своё. Большие площади бежевой бумаги утомляют глаз и спорят с яркими политическими цветами карты: бумагу нужно давать вкладышами, а не целыми панелями. Скевоморфизм легко перегрузить. Если делать вполсилы, получится «стоковый стол из генератора». На телефоне мелкая машинопись не читается: для цифр нужен отдельный шрифт.

### Идея 2. «Перо и кулак»: два материала, которые спорят

Название игры становится материалом. **Сделка** выглядит как бумага, чернила, сургуч, перо: дипломатия, экономика, законы. **Сила** выглядит как воронёная сталь, трафаретный шрифт, оливковый цвет, красная лента: армия, война, разведка. Панель министерства всегда сделана из своего материала, поэтому раздел виден с первого взгляда без чтения. Главное здесь в другом: общий интерфейс дышит вместе с мировой напряжённостью. В мирное время в рамках больше бумаги. Когда растёт DEFCON, появляются стальные накладки, игла циферблата краснеет, полоса даты получает трафарет. Ультиматум выглядит запечатанной папкой, мир подписанным договором. Это и есть авторская мысль, которой нет ни у Paradox, ни у AoH: интерфейс рассказывает, куда страна катится.

**Риски.** Художественной работы вдвое больше, и моды должны покрывать оба материала (смягчается тем, что каждый тип панели объявляет свой материал, а недостающий слот берёт значение по умолчанию). Если смена материала будет резкой, игрок потеряет ориентиры: она должна касаться только рамок и накладок, а не раскладки и цифр. Есть риск «военного кича».

### Идея 3. «Мир в эфире»: живая планета и её новости

Живая планета главного меню остаётся и в кампании. Дата становится мировыми часами с часом, как в EU5, и с терминатором дня и ночи на глобусе. Мировые события подаются форматами медиа 2020-х: первая полоса газеты (наследник «World News» из HoI4 и газеты S1914), лента телетайпа для мелочей, плашка экстренных новостей для кризисов, пуш-карточка в тикере справа внизу. Каждый формат сохраняет привычную схему «картинка + текст + 1–3 варианта». Формат медиа это данные, моды добавляют свои рамки (например, советская «Правда» для сценария 1985 года). Дипломатия выглядит видеосвязью с кабинетом другого лидера, с окном на его столицу (по образцу дипломатических комнат Stellaris).

**Риски.** Телевизионная графика по природе плоская и «приложенческая»: ровно то, от чего уходим. Без материальной базы из идей 1 или 2 она снова станет монтажом. Экран можно зашумить бегущими строками. Фото реальных СМИ и лиц создают правовые вопросы, поэтому рисунок или стилизация, никаких логотипов.

### Что предлагается

Основа идея 1, потому что материал «досье» лучше всего ложится на утверждённую палитру и легче всего переносится на телефон. Как механика живости берётся из идеи 2 только смена накладок с напряжённостью: пара материалов «бумага/сталь» через токены. Из идеи 3 берутся форматы событий. Окончательный выбор вынесен в вопросы раздела 6.

---

## 4. Модифицируемость: как строить скин

### 4.1. Что есть у других

| | Раскладка | Графика | Слабость |
|---|---|---|---|
| Classic Clausewitz (EU4, HoI4, Stellaris) | `.gui`, абсолютные координаты от `orientation` | `.gfx`: `spriteType`, `corneredTileSpriteType` (9-slice), `.dds` | файл заменяется целиком, моды конфликтуют и ломаются с каждым патчем (поэтому существует гигантский UI Overhaul Dynamic) |
| Jomini (CK3, Vic3, EU5) | `.gui` с настоящим layout-движком: hbox/vbox, `parentanchor`, `layoutpolicy` | `spriteborder`, шаблоны `using =`, типы с `blockoverride`, горячая перезагрузка, `gui.debug` с файлом и строкой | `.skin` и `.shortcuts` в модах игнорируются, культурные скины есть только у Paradox |
| Age of History 3 | раскладка зашита в Java | замена файлов по пути, JSON, 3 роли шрифтов, `Overlays.json` для карты | глубокие правки только патчем `game.jar`, одновременно работает только один такой |

### 4.2. Что у нас уже есть и что достроить

У нас уже лучше, чем у классического Clausewitz. `game/gui/*.json` задаёт якоря и отступы панелей, `game/interface/*.json` задаёт цветовые токены и метрики. Файлы читаются по имени, побеждает позднее значение **по ключу, а не по файлу**. Мод в 20 строк не конфликтует с другим модом в 20 строк. Достроить нужно вот что:

1. **Слоты текстур с полями 9-slice.** Новый файл в `game/interface/`, каждый слот задаётся по роли:
   `slot: panel.dossier.frame → texture, margins [l,t,r,b], content_margins, axis_stretch (stretch|tile|tile_fit), modulate: <токен цвета>`.
   Минимальный набор: `panel.frame`, `panel.header`, `panel.inset`, `panel.paper_insert`, `button.{normal,hover,pressed,disabled,primary}`, `tab.{idle,active}`, `tooltip`, `alert.slip`, `portrait.frame`, `flag.frame`, `corner.{tl,tr,bl,br}`, `divider`, `scrollbar`, `date.plaque`, `dial.face/needle`, `event.frame.<формат>`, `minimap.frame`, `vignette.<панель>`. Если слот пуст, стиль строится из цветовых токенов (как сейчас). Поэтому мод, который только перекрашивает, продолжает работать.
2. **Токены света и материала** рядом с цветами: `light.direction`, `bevel.width`, `bevel.highlight`, `bevel.shade`, `shadow.{color,offset,size}`, `grain.amount`, `ornament.enabled`. Глубина описана один раз, мод её перекрашивает, а не перерисовывает (вывод из Stellaris: первым делом моды перекрашивают нейтральный хром).
3. **Типы панелей как шаблоны**, наш аналог `using =` и `blockoverride` в Jomini. Панели в `game/gui` ссылаются на тип (`style: "dossier"`, `"instrument"`, `"telegram"`), тип описан в `game/interface`. Мод переопределяет тип один раз, и меняются все 20 панелей этого типа.
4. **Контекстные скины, открытые модам.** Слой `skins/<id>` с условием (блок, регион, эра, уровень напряжённости), который перекрывает слоты и токены. Это культурные архетипы EU5 и культурные HUD CK3, только с тем, чего CK3 модам не даёт. Пара «бумага/сталь» из идеи 2 реализуется этим же механизмом.
5. **Крючки картинок**: шапки-виньетки панелей, портреты, иллюстрации событий, медиа-рамки. Это путь к файлу с обязательной нейтральной заглушкой, повторяет `65_leader_portraits.json`.
6. **Шрифты по ролям**: основной текст, заголовки, цифры на фишках и в полосе, названия на карте, машинопись. В `28_ui_font.json` это уже частично есть.
7. **Звук на состояниях анимации** (как `start_sound` в CK3): `alert.arrive`, `order.stamp` и так далее, с путём к файлу и громкостью.
8. **Инструменты моддера**: горячая перезагрузка `game/gui` и `game/interface` в отладочном режиме и наложение «что это за элемент», которое по Alt+клику показывает тип, слот и файл-источник (аналог `gui.debug` в CK3).
9. **Профили раскладки**: `layout.desktop` и `layout.phone` как два набора якорей для тех же панелей. Мод двигает панель на одном профиле, не ломая другой.
10. **Нейтральность базы.** Базовый скин несёт мало орнамента. Орнамент и предметы (скрепки, штампы, уголки) лежат отдельным слоем и выключаются одним токеном. Тогда тотальная конверсия (холодная война, будущее, фэнтези) меняет материал, а раскладка остаётся (у HoI4 так реализованы TNO и Kaiserreich).

Чего по-прежнему не будет: новые экраны модом не придумываются, содержимое панели остаётся кодом. Это честно указано в `00_panels.json`. Следующий шаг по образцу scripted widgets в CK3: регистрация дополнительного окна файлом, а не заменой.

---

## 5. Удобство: правила раскладки

### 5.1. Рабочий стол, 1920×1080

| Место | Что там | Правило |
|---|---|---|
| Слева вверху, ~300×100 | колышущийся флаг ~96×64, портрет лидера ~72 px в рамке, название, ранг | клик открывает панель страны |
| Верх, ~800×56 | **6–8** ключевых ресурсов: нарисованная иконка, крупное число, дельта под ним | остальные в выпадающей строке; тултип у каждого; подписи не капслоком, читаются по иконке и цвету |
| Справа вверху, ~380×90 | табличка даты с часом, пауза и скорости 1–5 как ручка или циферблат, рядом прибор DEFCON / мировой напряжённости | пробел, клавиши 1–5, +/- |
| Под верхней полосой, по центру слева | **телеграммы-уведомления** (сейчас это и полоса «Next: Shortage», и 12 значков сверху) | не больше 6 видимых, цвет по министерству, счётчик; правый клик «больше не показывать этот тип», лоток отключённых; критичные (разрыв союза, объявление войны) отключить нельзя |
| Левый край | рейка из **5–7** медальонов (Страна, Экономика, Армия, Дипломатия, Мир, Отчёты) вместо 16 строк | каждый раскрывает вкладки внутри панели; горячие клавиши как у HoI4 |
| Слева | **одна** выдвижная панель ≤ 560 px (≤ 30%) | вкладки, «Назад»/«Вперёд», булавка, крестик; каждое имя в тексте ссылка (Vic3); второй уровень открывается в той же панели, а не новым окном |
| Справа, 280–320 px | сводка: войны, армии, стройки, кризисы, наблюдение за товарами | секции сворачиваются и закрепляются; любую панель или тултип можно приколоть сюда карточкой; клик переносит камеру |
| Справа внизу | миникарта с блоком режимов карты (политика, рельеф, экономика, союзы, война, ресурсы…) с миниатюрами в рамках | **слои (облака, туман, названия, города, стройки) уходят в выпадающее меню «Слои» или в настройки**; чекбоксов на главном экране не будет |
| Внизу по центру и слева | армии как жетоны с портретами генералов (HoI4), а не карточки-клипы | основное представление армии это фишка на карте |
| Внизу справа, над миникартой | тикер мировых новостей, 1–2 строки | клик открывает газету дня |
| Центр | события, ≤ 640 px, карта не гаснет | на весь экран открываются только деревья |

### 5.2. Плотность: три уровня

1. **С первого взгляда, без наведения.** Верхняя полоса с дельтами, сводка, телеграммы. Правило: если цифра нужна для решения, она видна в панели, а не только в тултипе (урок Vic3 Pop Needs). Строка сводки показывает причину коротким текстом («Никель: дефицит 11,8% — нет импорта»).
2. **Наведение.** Тултип раскладывает число по источникам, зелёным и красным. Задержка около 0,4 с, тултип не срабатывает на быстром проходе мыши. Shift даёт подробную версию (как в Stellaris 4.x).
3. **Вложенные подсказки.** Подчёркнутые термины. По умолчанию блокировка действием (средняя кнопка или Ctrl), таймер блокировки включается в настройках. Так CK3 отвечает на жалобу «фиддли».

### 5.3. Телефон (Android, постоянная цель)

AoH3 переносит на телефон ПК-раскладку с одним масштабом 0/1/2 и потом увеличивает кнопку закрытия (1.0.30/1.0.36), рейтинг падает до 3,1–3,9. S1914 Definitive Edition, наоборот, перестраивает раскладку. Делаем как S1914, на тех же панелях:
- верхняя полоса: флаг, 4 ресурса и дата, остальное по свайпу;
- рейка превращается в нижнюю панель вкладок из 5 пунктов (Страна, Экономика, Армия, Дипломатия, Мир);
- левая панель становится нижним листом: половина экрана или весь экран, свайп вниз закрывает;
- сводка прячется за язычком у правого края;
- телеграммы складываются стопкой сверху, свайп в сторону отключает тип;
- тултип вызывается касанием и показывается листом снизу, долгое нажатие закрепляет его в сводке;
- кнопка режимов карты плавает справа внизу;
- зоны касания ≥ 48 dp, безопасные отступы, чтобы HUD не закрывал фишки (главная жалоба на S1914: https://www.playsupremacy.com/en/news/1914/focus-group-update);
- масштаб интерфейса непрерывный, без перезапуска;
- живость дают шейдеры, 2D и `Tween`, без 3D-толп; глубину дают 9-slice и один шейдер фаски, без размытия.

---

## 6. Вопросы для модели решений (Gemini Decisions)

1. **Выбор из названных вариантов.** Какая авторская идея должна стать основой интерфейса Deal or Force, глобальной стратегии о современном мире с 2024 года, ориентированной на поклонников Paradox и Age of History? Варианты: «Досье на столе» (бумага на тёмной доске), «Перо и кулак» (бумага для сделок, сталь для силы, меняется с напряжённостью), «Мир в эфире» (медиа-форматы и живая планета).
2. **Выбор.** Из какого базового материала делать панели? Варианты: «тёмное лакированное дерево или кожа с бумажными вкладышами», «воронёная сталь с бумажными вкладышами», «тёмно-синее стекло с латунью».
3. **Оценка от 0 до 10.** Сколько орнамента в рамках (0: ровные плашки, 10: золотое рококо EU4) даст ощущение объёма и души и при этом оставит скин нейтральным для перекраски модами?
4. **Да/нет.** Должны ли рамки и накладки интерфейса менять материал (бумага → сталь) с ростом мировой напряжённости, если раскладка и цифры при этом не меняются?
5. **Выбор.** Как показывать уведомления «нужно действовать»? Варианты: «телеграммы на скрепке под верхней полосой», «ряд значков со счётчиком под верхней полосой (HoI4)», «медальон со счётчиком и список по клику (Vic3)».
6. **Выбор.** Какую систему подсказок сделать по умолчанию? Варианты: «плоская раскладка числа (EU4)», «вложенные с блокировкой действием (CK3, настройка)», «вложенные с блокировкой по таймеру».
7. **Да/нет.** Должен ли глобус при самом дальнем зуме превращаться в печатную карту на столе ситуационной комнаты, если при обычном зуме карта остаётся на весь экран без рамки?
8. **Выбор.** Где разместить переключатель режимов карты? Варианты: «блок с миникартой справа внизу (EU4, AoH3)», «круглые «линзы» внизу по центру (Vic3, EU5)», «колонка кнопок у правого нижнего края (HoI4)».

---

## Приложение: вторая волна (другие гранд-стратегии и 4X)

### Power & Revolution / Geo-Political Simulator 5 and 2026 Edition (Eversim)
- Every ministry window carries its own budget slider and shows the result as you drag. In GPS5 (gps5_01, gps5_06) the header of the Service or Commerce window holds the 'Ministry budget' slider, a -15%..+15% scale with the amount in money below it, and a second gauge 'Budget deficit: -4.72% of GDP'. You see the cost and the national consequence in the same strip where you act, before you commit, and no separate budget screen is needed.
- A two-level ministry dock along the bottom edge. Eight icon groups (economy, industry/energy, justice/police/army, health, culture/education, institutions, diplomacy, society) each open a row of named sub-tabs right above them: Police, Secret service, Justice, Immigration, Army, Military operations (gps5_08), or Finance, Farming, Industry, Service, Energy, Environment (gps5_01). All of government fits in a single strip that is two taps deep.
- A pile of people waiting to see you. At top left sits a counter ('2', '3', '34' in pr2023_02) above stacked portraits with a close-x and a dated one-line reason, for example a Director of Communications asking about an MMA championship (gps5_06). These are ministers, lobbyists and leaders asking for a meeting. The pressure on the head of state shows up as faces queueing at the door, not as an alert list.
- Windows can be pinned and the screen arranged by the player. Each window has a pin icon. Several windows stay open over the live 3D map at the same time (company card, ownership org-chart, product dialog in gps5_06). The Steam page says windows can be pre-positioned and that dual screens and 4K are supported (https://store.steampowered.com/app/3107770/).

*Как перенести к нам:* Desktop: put the cost slider inside every ministry or programme card and show its live effect on the national figures right next to it (deficit % of GDP, approval) before the order is signed. This fits 'The Cabinet' concept as a draft order whose margin figures change as you move the slider. Borrow the visitor queue as an antechamber: a stack of portraits at the screen edge (ministers, lobbies, foreign envoys, kin-statecraft agents), each with a dated one-line reason and a count. This makes the simulation's pressure visible as people, and it can be modded as plain data (portrait, reason text, trigger). Borrow pinnable windows, but cap how many can be open and dock them to a rail so you never get GPS5's cramped floating clutter. Phone: the two-level dock maps straight onto a bottom tab bar (8 groups) with a sub-tab chip row above it. The visitor queue becomes a badge that opens a full-screen swipeable stack of petitioners. Do not copy the 9-11 px condensed tables.

### Supreme Ruler Ultimate / Supreme Ruler 2030 / The Great War (BattleGoat)
- The research window shows a technology's whole lineage in three columns (sru_02). On the left, 'Development History' shows its prerequisites as linked cards with progress bars. The centre holds the focused tech with cost, time, year and Project Effects. On the right, 'Leads To Techs...' and 'Leads To Designs...' show what it unlocks. You always see where a thing came from and where it leads without opening a whole tree. Below it sits a compact Research Queue with pause and cancel per row.
- Cabinet ministers you can delegate to, with their actions reported to you. Each area (defence, finance, trade, research and so on) can be handed to an AI minister with a set autonomy level. The top-right ticker reports what the minister did ('Minister Starts Research Project...', sr2030_04), next to dated event rows such as 'Unit Construction Idle' and 'Diplomatic Exchange Successful'. Players credit the 2030 ministers that take over military, finance and trade (Dorian, https://steamcommunity.com/app/2093410/reviews/?browsefilter=toprated). The earlier SR2020 already let you require approval or forbid areas (https://www.gamesradar.com/supreme-ruler-2020-review).
- A vertical alert column on the left edge (sru_05) holds round slots with counts: diplomacy offers, research, defence, a warning triangle. Pending matters live in fixed places, so you learn where each kind of trouble appears.
- A unit spec card (sr2030_04): a big silhouette, a row of icon-plus-number tiles (personnel, range, speed, attack per target class) and a separate Build Stats block (days, cost, goods, uranium, year). It is a spreadsheet laid out as a readable card.

*Как перенести к нам:* The three-column lineage card suits every DoF chain: technologies, laws, kin-statecraft steps and formable-nation requirements. It is a moddable template (prerequisites, focus, unlocks), works one column at a time on phone, and replaces sprawling trees. Ministers with autonomy fit DoF's simulation, where every country is simulated equally. Let the player hand an area to a cabinet member at a set autonomy level (none, ask first, act and report) and show each action in a dated ticker signed by that minister. This also supports thousand-year campaigns, where the player cannot micromanage. Show minister actions as signed telegrams in the ticker, not as anonymous alerts. Learn from the 2030 complaints: give slider fields keyboard entry, show exact values on hover, and keep notifications one click from their cause. A fixed alert column with counts on desktop becomes a badge rail on phone.

### Realpolitiks II (Jujubee)
- A radial command ring around a globe button, bottom-right (rp2_03, rp2_05, rp2_07). Sections circle the globe (diplomacy, military, economy, trade, research, espionage, map views), and the active segment lights up orange. A grid of Province and Country map-mode icons sits beside it (rp2_05, rp2_07). It is the clearest thumb-reachable command pattern in this group.
- Every option on an event card shows its numbers (rp2_05, 'Popular Tourist Destination'). An advisor portrait and a pictogram head the card. Each choice row lists effects with icons, sign colour and duration, for example money income +15, AP -70, relations with everyone -5 for 50 days, plus an edit icon. You decide with the consequences visible.
- Battles play on a tabletop diorama (rp2_01). The battle opens as a 3D board of terrain tiles on a wooden table, with order of battle and reserves on both sides, order cards with cooldowns, and a colour-coded combat log. It turns an abstract fight into a physical tabletop. On Xbox it is replaced by a two-bar summary, so the same battle has a heavy and a light form (https://www.mygamer.com/realpolitiks-ii-xsx-review/).
- A parliament hemicycle with a Lobby button per party (rp2_03). Seats are shown as coloured dots, and each party row shows its share, its cost to lobby, and the button that acts on it. Data and verb sit on one row.

*Как перенести к нам:* Phone: a radial ring around a globe or seal button, anchored bottom-right for the thumb (with a left-hand option), is a strong layout for DoF's top-level verbs and map modes. On desktop the same ring can open from a hotkey or right-click on the map, so modders register a verb once and it appears in both. Copy the event card with priced options as is: each choice shows its effects as icons with sign colour and duration. It fits the 'sealed order' and telegram look if the numbers are printed like a ledger in the margin. Make sure every event has at least one lesser-evil option. Borrow the tabletop diorama in heavy and light forms for DoF's pluggable war model (front 17): a 3D table view when there is time and GPU budget, and a two-bar summary on phone and low-end devices. Avoid the hexagon icon soup along the top. Group notifications by owner, with one badge per minister or desk.

### Rulers of Nations: Geo-Political Simulator 2 (Eversim, 2010/2014 Steam)
- An in-world device as the frame for a secret layer (ron_07, ron_01). The covert 'secret objectives' screen is drawn as a chrome PDA with a fingerprint scanner, a signal-strength bar and a red monochrome screen. The objectives sit on it with point values, Modify buttons and a limited number of allowed changes. The frame tells you this is clandestine before you read a word. Over the red night map of world leaders, the PDA rests in the corner like a phone in your hand.
- Diplomacy as a conversation with a person (ron_02). An event or diplomatic message opens as a rendered head of state in his office with a vignette, his title, and the spoken text below. A world map of leader portraits pinned to their countries (ron_01) works as a who's-who contact sheet.

*Как перенести к нам:* Give each layer of the game its own in-world device as its frame. Covert operations and kin-statecraft agents could live in a different object from the ministries' paper: a field radio, a cipher pad or a burner phone. This distinguishes the covert half of statecraft at a glance and gives it authorial identity. On phone the device is the whole screen, so the metaphor costs nothing. Moddable as a skin per layer (frame texture, typeface, screen tint). The leader contact sheet maps well to DoF's 1000+ countries if it is filtered to neighbours, rivals and allies. Show a diplomatic reply as the leader's face plus their words, but always attach the effect ledger that GameWatcher found missing.

### Shadow Empire (VR Designs / Matrix-Slitherine)
- Every overview is a typewritten memo from a named person (se_07). The REP. screen has a left index (Empire Dashboard, Budget, Urgent Issues, Cabinet, Factions, Demand, Technology, Treasury cashflow, Decision Archive, Trade Report, Spies) and a right pane laid out as a letter. It shows SUBJECT: Factions Overview, a FROM block with the Secretary's portrait and loyalty number, a CALL button, a 'Sir,' salutation, bullet facts in monospace type, and then tabular detail. The data is the same as a spreadsheet, but it has an author, a voice and a mood. It sits on grey paper with a faint REAL TIME rubber stamp.
- Decisions are cards, and a card can be refused with a reason the player is not told (se_02). Stratagems are illustrated cards with a cost pip and a play counter, filtered by category (Majors, Minors, Covert Ops, Nation, Tariffs, HQs...). The right pane shows the target, the leader who will execute it (portrait and skill), and a red line saying it is impossible for a reason the staff will not reveal. Fog of information is shown as a person withholding something, not as a greyed-out button.
- An odds screen before you attack (se_04). Before you commit, a modal shows eligible, attacking and enemy forces, a hex mini-map of the engagement, a list of offensive and defensive modifiers with icons and signed percentages, combat totals, and one large ODDS 8:1 bar above an ATTACK button.
- Illustrated events attributed to a speaker (se_09, the VID mode). Big news takes the screen as a painting, with a paper strip below holding the speaker's portrait and their line in small caps (a governor reporting treasure found). The HIS, VID and MNG tabs separate history, illustrated news and management.

*Как перенести к нам:* The closest fit to DoF's ink-and-paper palette and 'The Cabinet' concept (paper, telegrams, a sealed order). Make every DoF overview a signed memo: an index on the left, and a letter on the right from the responsible minister or desk with portrait, loyalty and a Call button. The opening sentence summarises in words, and the table follows. Template it so modders add a report by data (author role, headline sentence, table query). On phone a memo is a natural scrolling card with the index as a drop-down. Borrow 'impossible, and staff won't say why' as a deliberate fog-of-information voice for intelligence and covert actions, but only where the game wants opacity. Shadow Empire's main failure is unexplained systems, so everywhere else every blocked action must name its reason. Borrow the odds screen as DoF's pre-commit sheet for war declarations, ultimatums and deals: modifiers listed with signed percentages and one big verdict bar above the seal or sign button. Borrow the paper-strip quote for news: illustration (GIF or video per the e84b57f4 design note) plus speaker portrait plus one line. Fix what players hate: a clear active-tab state and no data that only exists in the manual.

**Лучшее для заимствования из этой группы:**
- Make every report a signed memo, as in Shadow Empire's REP screen: an index on the left, a letter from a named minister on the right (portrait, loyalty, Call button), a one-sentence verdict in words, then the table. This fits DoF's ink-and-paper and 'Cabinet' direction exactly, can be templated for mods, and becomes a scrolling card on phone.
- Put the cost slider in the action card and show its live consequence there, as in GPS5's ministry headers: drag the budget and the deficit as % of GDP and the approval change update before you sign.
- Show priced options on every event and decision, as in Realpolitiks II: each choice lists its effects as icons with sign colour and duration, and always include a lesser-evil option. Every reviewer of Rulers of Nations and Shadow Empire faulted missing plus/minus breakdowns.
- Use a radial command ring around a globe or seal button, anchored for the thumb (Realpolitiks II), as the phone-first top-level navigation, with the same ring available from the map on desktop. Each mod-registered verb appears in both.
- Add an antechamber of petitioners, as in GPS5 and Rulers of Nations: portraits of ministers, lobbies and envoys queued at the screen edge with a count and a dated one-line reason. It makes simulation pressure visible as people, and each petitioner is a portrait, a reason line and a trigger in mod data.
- Let the player delegate ministries at a chosen autonomy level, with a signed ticker reporting each action, as in Supreme Ruler's cabinet. This is needed for thousand-year campaigns and 1000+ simulated countries, where the player cannot micromanage everything.
- Show a three-column lineage card for every chain (Supreme Ruler research): where it came from, the focus item, what it unlocks. Use it for techs, laws, kin-statecraft steps and formables instead of sprawling trees, one column at a time on phone.
- Show an odds screen before committing to war, an ultimatum or a deal (Shadow Empire): modifiers with signed percentages, a mini-map of the theatre and one big verdict bar above the seal button.
- Give each layer of play its own in-world object as a frame (Rulers of Nations' PDA for secret objectives): ministries on paper, covert and kin operations on a cipher pad or field radio. This gives an authorial idea at a glance, and the frames are swappable skins for modders.
- Give big moments a heavy form and a light form: the Realpolitiks II tabletop battle diorama on desktop, a two-bar summary on phone and low-end devices (as on its Xbox version). Pair news with an illustration and a paper strip carrying the speaker's portrait and one line (Shadow Empire VID mode).

### Civilization VI (Firaxis, 2016)
- Lenses: one key or button swaps the whole map to a single question (religion, loyalty, appeal, government, settler suitability, tourism). Tile icons, hex tints and labels change together and the 3D world stays underneath. Reviewers name lenses as a standout: the map shows non-physical influences such as government and attractiveness (gamecloud, gamepressure).
- Fog of war as cartography: explored ground you cannot currently see becomes a sepia, hand-drawn parchment map. Terrain, resources and features stay readable through it. Unexplored ground is black. The art director said the parchment-and-compass look came from the Age of Exploration theme and gave the team more control over how fog works (wccftech interview with Busatti). Screenshot steam_06 shows this: the lit 3D homeland sits inside an inked atlas.
- Instrument-like HUD corners: research and civic progress are brass dials with radial fills in the corners (steam_02, steam_03). The diplomacy screen is a fully animated 3D leader in their own set, with a compact intel column of gossip, access level, agendas and grievances on the left (steam_00).

*Как перенести к нам:* Desktop: put Civ VI's fog trick on top of Deal or Force's ink-and-paper palette, keyed to intelligence. Regions with current intel render as the living 3D globe. Regions with stale intel fade to an inked atlas print that shows the last-known date and keeps borders and city names. Regions never surveyed stay blank paper with coastlines only. This gives the 'paper' palette a mechanical meaning and makes intelligence visible. Add a lens bar: one question per lens (alliances, sanctions, unrest, front lines, trade flows, kin ties). Pressing a lens recolours provinces, swaps icon sets and dims everything else. Phone: lenses become a horizontally scrolling chip row above the bottom bar. One tap switches a lens; a second tap clears it. The atlas-print fog works the same on mobile and is cheaper to render than full detail. Lesson from the complaints: lens colours must contrast with the fog colour, and the paper tone should not be reused for 'unknown'.

### Civilization VII (Firaxis, 2025)
- Diplomacy as a staged confrontation over your own map: both leaders stand in the foreground, facing each other, in front of the real campaign terrain where the conflict is happening (steam_00). The dialogue box is small and centred, with two choices (Continue / Open War Details). It works like a cut-scene that is still a menu.
- Victory and Legacy dashboards: one screen with four columns (Cultural, Economic, Military, Scientific). Each column shows the top three rivals, your own rank and your score against the point goal (steam_02). Legacy paths are cards with progress counters such as 0/7 and a Track checkbox that pins the goal to the HUD (steam_01).
- Post-launch fixes worth copying: growth events now show before/after previews, so you can compare a specialist with an improvement before committing (patch 1.2.5). Update 1.5.0 added a radial ruler for measuring distances and improved plot tooltips.

*Как перенести к нам:* Desktop: (1) A 'situation room' victory panel: one column per long-term path (economic weight, alliance bloc, territorial security, prestige or soft power). Each column shows the top three nations, your rank and the threshold, and has a Track pin that docks the goal into the HUD. (2) Show a before/after preview on every consequential decision: budget sliders, decrees, deal terms. Show the delta, then let the player commit. (3) Measuring tools such as a ruler for missile or air range and supply distance. (4) Diplomacy face-offs: two leader portraits (painted 2D is enough; no 3D needed) placed over a snapshot of the actual shared border or contested region. Phone: the victory columns become swipeable cards with the tracked one pinned. Before/after previews fit naturally in a bottom sheet with a Commit button. The ruler is a two-finger hold. Avoid Civ VII's mistakes: every major screen needs its own art motif, and no screen should hide the number that decides the choice.

### Old World (Mohawk Games, 2021-)
- Nested, lockable tooltips: every term inside a tooltip is a link to another tooltip. A tooltip can be frozen with middle-click or by holding Shift, so the player can drill down several levels (the hint is printed on screen in steam_02: Freeze Tooltip [MIDDLE-CLICK] or [Hold SHIFT]). The official description lists nested tooltips as a feature. Players say tooltips are the main way people learn the game (https://forum.quartertothree.com/t/old-world-pka-ten-crowns-from-soren-johnson/134746?page=77).
- The ruler as the HUD anchor: a framed portrait of the current ruler sits bottom-left with the order budget (the per-turn action currency), legitimacy and the turn button. The right side has a Nations column of leader portraits, each with an opinion number and a trend flag. Ambitions with progress counters sit top-centre (steam_02). Characters are the interface.
- Events as illustrated court letters: portraits of the people involved, two or three answers written in the court's voice ('Yes, ring the bells.'), and painted vignettes for story beats and wonders (steam_01, steam_06, steam_10). The inheritance screen is a family-tree card layout with traits and opinion of you (steam_04). Research is a hand of drawn cards with a discard pile, not a fixed tree (steam_07).

*Как перенести к нам:* Desktop: adopt nested tooltips with a freeze gesture (hold Shift or middle-click), capped at about three levels. Every policy, define or law term links to its own card, and the card can open the encyclopedia. Make the head of government the HUD anchor: a portrait bottom-left with approval and political capital (the order-budget equivalent), and a ministers or cabinet strip beside it. Show a nations rail of leader portraits with opinion numbers and trend arrows. Write events as cables or memos signed by a named minister, with that minister's portrait, and phrase the answers in the government's voice. Phone: long-press any term to pin its tooltip as a stacked bottom sheet; tapping a linked term pushes a new sheet and swiping down goes back. That works better with a finger than hover does. The leader portrait doubles as the main menu button. Mitigate the jargon complaint: every new term gets a first-seen highlight and a one-line plain-language summary at the top of each tooltip.

### Humankind (Amplitude, 2021)
- Civic choices as two-sided ideology decisions: each civic is a binary with flavour text, and each side shows how it moves your four ideology sliders (for example, Founding Myths: Natural Right vs Divine Mandate, steam_10). The cost of the choice is shown on the same axes the society panel uses (steam_03), so the player sees which way the country is drifting.
- Diplomacy face-off with war-support meters: your leader stands left, theirs right, and a stylised grey topographic paper map of the shared border sits between them. Both sides' war support bars and ideological proximity are at the top, with tabs for Relations, Trade, Treaties and Crisis (steam_08).
- Era progression as stars and illustrated culture cards: era-star medallions with counters for each category of deed (steam_05). The next culture is chosen from a carousel of painted scene cards with a motto line (steam_07). Narrative events are a painted vignette plus two option cards placed directly over the map (steam_12).

*Как перенести к нам:* Desktop: (1) Decrees and reforms as two-sided cards. Each side previews the movement on Deal or Force's existing political axes (for example, a small slider ghost showing the shift toward centralisation or liberalisation) before you sign. (2) Diplomacy face-off: two portraits, a monochrome inked map of the shared border between them, both war-support or public-opinion meters at the top, and tabs for Relations, Trade, Treaties and Crisis. This matches the ink-and-paper palette directly. (3) Modals blur the globe behind them instead of covering it with an opaque slab. Blur is cheap with Godot's screen-texture shader and can fall back to a dark scrim on low-end phones. Phone: two-sided decree cards fit portrait orientation (stack vertically, tap one to preview, tap Sign). The face-off becomes portraits on top, meters in the middle and tabs as a bottom segmented control. Avoid Humankind's mistakes: yield and status icons must face the camera and never tilt with terrain; terrain and province tooltips need a short delay; the strategic zoom-out must keep national colour, not wash out to grey.

### Millennia (C Prompt Games / Paradox Arc, 2024)
- Domain sidebar: a permanent left column shows the six domain powers (Government, Exploration, Warfare, Engineering, Diplomacy, Arts), each with points and the current national-spirit card. It reads like a vertical ledger of where the country's capacity sits, next to the research and culture meters and the Next Age countdown (steam_00).
- Branching ages shown as painted futures: research is a painted scene of the current age with technology cards along a progress rail. A column of locked future ages sits beside it (Age of Conquest, Intolerance, Discovery, Renaissance), each with the condition that triggers it, for example a crisis ages if intolerance passes 20 (steam_01). The player can see which history they are steering toward.
- The production chain as a hex board of improvement tiles with worker slots and goods icons (steam_03). Government and national-spirit unlock trees are drawn as parchment cards beside a full-height character painting (steam_11, steam_05). Battles have a replay window with step controls, both flags and per-unit stacks (steam_09).

*Как перенести к нам:* Desktop: (1) A capacity ledger sidebar: Deal or Force's state capacities (economy, military, diplomacy, intelligence, society, technology) as a vertical ledger with points, trend and the active doctrine card. (2) Possible futures: for a 2024-start world, show the crisis or era paths the world could enter (for example 'Cold Bloc Era', 'Climate Emergency', 'Multipolar Détente'), each as a painted card with its trigger condition and progress. The world's history becomes visible and moddable: an era is just a card definition with a condition. (3) Supply chains shown as a hex or node board per region, with workers and goods. (4) A battle or operation replay with step controls for the after-action report. Phone: the ledger sidebar collapses into a single bottom tab with six rows; future cards become a horizontal carousel. Avoid Millennia's mistakes: ship a UI-scale slider from day one (essential for phones); never put a core screen behind an unlabelled pictogram; every main function gets a labelled entry.

### Ara: History Untold (Oxide / Microsoft, 2024-)
- Living Strategy zoom (v1.4): instead of reducing armies to icons when the camera pulls back, the 3D armies scale up as you zoom out, and city banners carry turns-left and quality-of-life stats. Main city actions can be taken from the map. The developers' reason: icons cannot carry as much information as a rendered, animated unit, and the map should explain itself (https://www.arahistoryuntold.com/preview-of-the-living-strategy-v1-4-update/).
- Three-state fog: Visible (live), Uncovered (biome plus last-seen resources and improvements, frozen at the time they were seen) and Covered (only big terrain silhouettes such as mountains) (https://www.arahistoryuntold.com/the-map-and-fog-of-war/). Regions follow natural borders, and glowing coloured borders double as the political layer (steam_01, steam_05).
- A battle viewer with an expected-outcome line ('Victory in 4') and per-arm strength bars for infantry, cavalry, anti-cavalry, ranged and siege on both sides, plus skip and details buttons (steam_07). Diplomacy and statistics screens put a full-height 3D leader on one side and a GDP line chart against all nations on the other (steam_08). A gift-proposal panel shows the outcome if accepted before you send (steam_06).

*Как перенести к нам:* Desktop: (1) Do not let armies become ever-larger markers on zoom-out. Scale a small representative unit model or silhouette (one tank, one ship, one aircraft per formation) and aggregate icons by front, so a threat stays visible at globe scale. Deal or Force's globe is especially at risk of Ara's 'hidden army near the capital' failure. (2) Three-state intelligence fog that matches Ara's semantics: live, last-seen (frozen with a date stamp) and silhouette-only. This combines naturally with the Civ VI atlas-print treatment. (3) An operation or battle preview with an expected-outcome line and per-arm comparison bars before you commit. (4) Statistics: a GDP, population or power line chart against all nations, beside the leader portrait. (5) Emissive border ribbons for the political layer. Phone: render UI at native resolution with a scale setting; never upscale a low-resolution UI buffer, which was Ara's blur bug and matters even more on high-DPI phones. Late-game marker density needs clustering with a count badge, plus a single-tap threat filter.

**Лучшее для заимствования из этой группы:**
- Intelligence fog as cartography (Civ VI parchment fog plus Ara's three fog states): live regions render as the 3D globe; stale regions become an inked atlas print with a last-seen date stamp; never-seen regions are blank paper with coastlines. This gives the existing ink-and-paper palette a mechanical meaning and is cheap on phones.
- Nested, freezable tooltips (Old World): every term in a tooltip links to another; freeze with Shift or middle-click on desktop; on phone, long-press pins a bottom sheet and tapping a term pushes the next one. Cap at about three levels, and put a plain-language one-liner at the top of each card.
- Lens bar (Civ VI): one question per lens (alliances, sanctions, unrest, fronts, trade, kin). Recolour provinces, swap icon sets and dim the rest. On phone, a chip row above the bottom bar.
- Characters as the interface (Old World, Humankind): the head-of-government portrait is the HUD anchor, with approval and political capital; a nations rail of leader portraits shows opinion and trend; events arrive as memos signed by a named minister with a portrait, and answers are written in-voice.
- Before/after preview on every consequential choice (Civ VII 1.2.5 growth previews, Humankind civic sliders, Ara gift outcome): decrees as two-sided cards showing the ghost shift on the political axes, budget or deal deltas, and the expected outcome before commit.
- Diplomacy face-off (Humankind, Civ VII): two portraits over an inked map of the shared border or contested region, both sides' war-support or public-opinion meters at the top, and tabs for Relations, Trade, Treaties and Crisis.
- Possible-futures panel (Millennia ages, Civ VII legacy paths): painted cards for world eras or crisis paths with visible trigger conditions and progress, plus a victory or standing panel (top three plus you per path) with a Track pin into the HUD. Eras as moddable card definitions.
- Do not shrink the world into ever-larger icons (Ara Living Strategy): on zoom-out, scale representative unit silhouettes, aggregate by front, and cluster markers with count badges, so threats near the capital never disappear under markers.
- Blur the living world behind modals instead of covering it with grey slabs (Humankind frosted glass; the Civ VII 'grey slab' criticism), with a dark-scrim fallback on low-end phones. Give every major screen its own art motif or figure (Millennia's full-height painted figure in front of card grids).
- Hygiene lessons from complaints: a UI-scale slider from day one (Millennia); UI text rendered at native resolution, never upscaled (Ara's blur until v2.0); status icons that face the camera and never tilt with terrain (Humankind); no core function behind an unlabelled pictogram (Millennia's flag-for-diplomacy); lens colours that contrast with the fog colour (Civ VI).

### Imperator: Rome (Paradox, 2019; UI rework in 2.0 'Marius')
- The map changes with zoom. Up close it is lit 3D terrain with roads and real city models whose size follows population. Province names are engraved into the land. Pulled back, it fades into a flat painted political atlas with large serif country names and a paper-coloured sea. Players single this out: an r/EU5 thread titled 'I miss Imperator's terrain map' drew about 1.5k upvotes. The game ships two political map modes, a terrain one and a flat one, and the fade between them is tied to zoom (screenshots steam_859580_04 and _05).
- Risk is surfaced in the Outliner. Next to armies and navies, the always-open right-hand Outliner lists 'Disloyal characters' with a coloured percentage each, plus Great Wonders and a 'Queued events' tray. The player never opens a separate screen to learn who is about to rebel (steam_859580_03).
- Research is done by people. Each of the four advance tracks (Martial, Civic, Oratory, Religious) has a portrait of the character researching it, with a 'change researcher' button. The tree sits on aged paper with a coffee-ring stain and a 'Planning mode' toggle that shows the path ahead (steam_859580_01). Abstract progress is tied to a face.
- For bulk work there is a Macro Builder map mode. You act on whole territories from the map, for example right-click a territory to cancel or demolish buildings, instead of opening each settlement (PCGamesN on the UI rework). The same idea applies to recruiting.

*Как перенести к нам:* Desktop: make the globe show two layers by zoom. Close in, real terrain: night lights, roads, city footprints scaled by actual 2024 population. Far out, it fades into the ink-and-paper political atlas with engraved country names. That fits the existing ink-and-paper palette and the 'Cabinet' concept. Add a 'Disloyal / at risk' section to the existing outliner (ministers, generals, regions with crisis risk, each with a coloured %), so danger is visible without opening a screen. Tie research and policy tracks to a named minister portrait. Add a map-paint bulk-action mode (build, tax, garrison or mobilise across many regions with one drag) for 1000+ countries. Phone: the zoom-driven atlas↔terrain fade is free on touch (pinch). The outliner becomes a swipe-up sheet with the same 'at risk' rows. Map-paint bulk actions suit fingers better than per-province dialogs. Modding: expose map-mode definitions and the zoom thresholds for the terrain-to-atlas fade as data.

### Terra Invicta (Pavonis Interactive, 2022; 1.0 later)
- Event cards teach once, then step aside. The 'Mission Control Reports' card has art, a headline and plain text, and then says that future notices of this kind will not alert or pause the game; they will go to the left-side news feed and the event summary instead. Each card has three exits: Continue (resume), Close (stay paused) and Take Me There (camera jumps, stays paused) (steam_1176470_09). Patch 10 made all 137 notification types configurable, with defaults in a template file.
- A nation is one compact sheet on the globe. The nation panel over a real-time 3D Earth shows Overview / Military / Development / People blocks. Every figure has a red or green trend arrow, a donut shows public opinion, and control-point icons sit at the top. Tabs for Priorities, Policies, Regions and Relations run along the bottom (steam_1176470_06, _01). Assets on the globe are faction-coloured icons rather than models, and the councilor card in the corner shows the selected agent's stats and status ('Awaiting next mission phase', 'Automate missions').

*Как перенести к нам:* Borrow the notification model completely. The first time a kind of event occurs, show a full illustrated card (telegram or cable in the Cabinet style) that explains what it is and offers Continue / Hold / Take me there. After that, that kind of event becomes a feed line, and the player can promote or demote it per category, from a moddable defaults file. Borrow trend arrows on every national figure and a public-opinion donut on the nation sheet. Avoid TI's mistakes: no low-contrast slab panels, no small type, no seconds-long panel opening. Keep Deal or Force's ink-on-paper, which reads better than dark glass. Phone: TI shows that a desktop-dense nation sheet fails on a handheld. Put the nation sheet on a bottom sheet with the four headline blocks as swipeable cards, and give event cards full-width thumb-sized buttons. 'Take me there' should be the primary button on phone.

### Total War campaign map (Three Kingdoms 2019; Warhammer III 2022)
- Three Kingdoms diplomacy is a stage, not a form. A full-screen ink-wash map of China is coloured by attitude to you, with a legend from Best Friends to Hostile. Your ruler stands painted at the left and the counterpart at the right, under their own banners. The AI answers in a speech bubble ('Not you again.'). A 'You give / Common deals / They give' tray and Propose / Cancel sit at the bottom, alongside the faction list and previously signed deals (steam_779340_04). Each deal shows a running numeric sentiment and needs at least +1 to be accepted, so you see how close you are before you propose.
- Warhammer III's End Turn hub. A round cluster in the bottom-right puts End Turn at the centre and a scrolling strip of pending items under it ('Outpost available', 'Garrisoned Lord not moved'). Arrows step through the items, which can be dismissed, and missions, diplomacy and tech sit around the ring (gamepressure_wh3_1564766015). Zooming out turns the 3D world into a paper-like tactical map. Lords become portrait pins, and an Overlays side panel switches Ownership, Diplomatic status, Attitude, Control, Growth, Corruption and so on, with checkboxes for floating names and settlement bars (gamepressure_wh3_1564766062).
- Three Kingdoms' ink HUD. The UI has no hard panels: frames are brush strokes and ink bleeding into the 3D map. Settlement labels are paper slips with a portrait. The Reforms tree is a blossoming plum branch drawn in ink, which reviewers call 'beautiful' (steam_779340_07, _12).

*Как перенести к нам:* 1) Diplomacy as a meeting: both leaders' portraits (painted or photo-styled, moddable) face each other across a mini-world map coloured by attitude. A live acceptance meter ('needs +1') and a one-line reply from the other side ('Not you again', 'We remember Crimea') give character cheaply; reply lines are data, so modders can add them. 2) Pending-actions ring around the end-turn or advance-time button: idle army, empty factory slot, unanswered ultimatum, with arrows to cycle and a 'snooze'. Never block time on dismissed advice. 3) At far zoom, swap unit models for leader or army portrait pins, and put the overlays in a side list of switches. 4) Draw frames as ink and torn paper bleeding into the globe rather than boxed rectangles; this fits the existing ink-and-paper palette and gives the requested volume without gold chrome. Phone: the round End Turn cluster is a natural thumb control in the bottom-right corner. The diplomacy stage collapses to two stacked portraits with the offer tray as a bottom sheet. Portrait pins at far zoom are much easier tap targets than 3D models.

### Hegemony III: Clash of the Ancients (Longbow Games, 2015)
- The series' 'strategic zoom'. One continuous zoom runs from the 3D tactical map (villages, soldiers, rivers) into a stylised strategic map drawn like Greek black-figure and red-figure pottery: terracotta orange land, black terrain glyphs for trees and hills, cities as pottery-white game pieces on red discs, Latin place names lettered across territories. There is no loading screen and real time keeps running (steam_308173_14, _02). Reviewers single out the zoom as the standout feature.
- Logistics are drawn as moving lines. Supply routes are animated dashed lines and yellow arrows from farms and camps to cities and armies; supplied areas are shaded green, and the meander-pattern border glows when you select a city's zone (steam_308173_14, _12). The economy can be read straight off the map.
- Explanatory tooltips with a verdict. The 'Rebellion level' tooltip explains the mechanic in a plain paragraph, adds a red sentence about this particular city ('extremely high… could be seized at any time'), gives the live numbers with the weekly delta, and offers 'F1 More information'. The screen edge also gets a red danger vignette (steam_308173_12).

*Как перенести к нам:* Make the far zoom a stylised 'map room', not just a flat colour fill. For 2024, the analogue is a mid-century cartographic print: ink hatching for terrain, cities as small paper or wood tokens standing on the globe, borders as printed rules. Draw the economy and war as moving lines: pipelines, sea lanes, rail and supply corridors as animated dashes whose speed and colour show flow and shortage. A cut corridor visibly stops. Write every important tooltip as explanation, plus a verdict for this case in red, plus live numbers and deltas, plus a 'more' link to the Codex. Use a subtle screen-edge vignette as a crisis signal. Avoid Hegemony's failures: small type and no automation. Phone: an animated line is readable at phone size where a number table is not. Collapse tooltips to tap-and-hold cards, with the red verdict sentence first. Modding: line styles (colour, dash, speed) and token meshes as data.

### Field of Glory: Kingdoms (Ageod/Slitherine, 2024)
- Event art drawn in the period's own idiom. Events open with a wide illustration in the manner of a medieval manuscript or tapestry (flat colour, stylised horses and hounds, decorative border), so a hunting invitation looks like a page of a chronicle. Choices are short in-character lines ('What an excellent idea!' / 'Alas, I must say no'); hovering a choice shows a flavour sentence describing what you will actually do (steam_1985050_05). The publisher says the more than 900 illustrations each take their character from the period or place in which they happen.
- Heraldry as the identity system. The player's coat of arms sits in a gold medallion at the top centre of the bar, every army on the map carries a standing banner of its realm's arms, and the selected region's arms are pinned on the lower panel (steam_1985050_07, _01). The turn and season line has a weather icon ('Fair season'), and the event log at the bottom colours each realm's name in its own colour.

*Как перенести к нам:* Give every event family its own period-true illustration idiom for 2024–2100: press-agency photo and caption, propaganda poster, satellite image, leaked document with redactions, TV news lower-third, social-media screenshot. Event choices stay as short in-voice lines, and hovering or long-pressing shows what the choice concretely does. That is the 'soul with an authored idea' the owner asks for, and modders can add illustration packs by file. Use flags and emblems as the identity system: player emblem at the top centre, a small standing flag on every army and fleet pin on the globe, nation names coloured by their own colour in the event log. Keep body text in a plain, high-contrast face; the readability complaints are the warning. Phone: a wide illustration plus 2–3 full-width choice buttons is already a phone-native card layout. Put hover outcome text on a long-press or a second line under each button.

### Distant Worlds 2 (CodeForce/Slitherine, 2022)
- Automation per object, with a guided middle ground. Each character, colony, ship, research queue and design has its own automation switch (the circular 'A' badge on every row in the character list, steam_1531540_02). You manually run only the parts you care about and the AI runs the rest. Setting something by hand switches off that object's automation (for example, manual troop garrisons turn off auto-garrison for the colony). The developers' stated goal is 'guided automation' between fully manual and fully automatic play. Players rate automation as the most praised feature.
- A map-clutter control with presets. A grid of toggleable map-icon categories (All / None, 'Preset: Default', 'General 33/38') decides what the galaxy map draws. Empire screens (Characters, Colonies, Resources) open as filtered, sortable lists down the left ('Filter: Production shortage'), each row carrying a status line ('Waiting at Skogoy', 'Researching at…'). Selecting a row frames it on the map and updates a persistent selection card at the bottom-left. The Resources view also draws supply and shortage routes over the map (steam_1531540_07). Messages stack on the right as titled cards with an icon and can be filtered by category.

*Как перенести к нам:* This is the model for 1000+ countries and thousands of regions. 1) An automation switch on every governable object: region, ministry, army, budget line, research track, diplomatic relationship. An 'A' badge on each row, cleared automatically when the player edits by hand, plus empire-wide policy presets ('cautious', 'expansionist'). This keeps the 'all countries simulate equally' rule while the player manages only what they choose. 2) A map-layer grid with saveable presets (War room, Economy, Diplomacy, Clean) instead of dozens of always-on markers. 3) Every empire screen as a filtered, sortable list with a status line per row. Clicking a row frames the globe and fills a persistent selection card. 4) Message cards on the right, filterable by category, with configurable sounds. Phone: lists with filters are the most phone-friendly pattern there is; layer presets become a single chip row; automation badges mean a phone player can play a whole country through a few manual levers. Modding: layer categories, presets and automation policies as data files.

**Лучшее для заимствования из этой группы:**
- Teach a notification once, then let the player choose its weight (Terra Invicta). The first event of a kind is a full illustrated card (telegram or cable) with Continue / Hold / Take me there. Afterwards it is a feed line, and each category can be set to pause, alert, feed or silent, from a moddable defaults file. This works the same on desktop and phone.
- An automation badge on every governable object (Distant Worlds 2). Regions, ministries, armies, budget lines, research tracks and relationships each carry an 'A' switch that clears when the player edits by hand, plus empire-wide presets. This is how 1000+ countries stay playable, and on phone without a mouse.
- A pending-actions ring around the advance-time or end-turn control (Warhammer III). Idle armies, empty slots and unanswered ultimatums scroll under the button, with cycle and snooze. Time is never blocked on dismissed advice. It suits the thumb on phone.
- Two map layers by zoom, both with volume (Imperator, Hegemony III). Close in: real terrain, night lights and city footprints scaled by population. Far out: a stylised ink-and-paper atlas with engraved names and standing tokens or portrait pins instead of 3D models, so even the abstract layer has depth. Pinch-native.
- Draw logistics and the economy as moving lines (Hegemony III, DW2 resources). Animated dashes for pipelines, sea lanes, rail and supply corridors; colour or speed shows flow; a cut corridor visibly stops; a 'shortage' filter lights up the problem routes.
- Diplomacy as a meeting (Three Kingdoms). Two leader portraits face each other over an attitude-coloured map, with a give/take tray, a live acceptance meter ('needs +1') and one-line in-character replies stored as moddable data.
- Event art in each event family's own idiom (Field of Glory: Kingdoms). For 2024+: wire photo, propaganda poster, satellite still, redacted leak, TV lower-third. Short in-voice choice lines, with the concrete effect on hover or long-press. Ornament goes on art and headings only; body text and numbers stay plain and high-contrast.
- Put risk in the always-open outliner (Imperator's 'Disloyal characters' %). At-risk ministers, generals and regions are listed with coloured percentages, so danger is visible without opening a screen.
- Explain tooltips, then give a verdict (Hegemony III). Plain mechanic paragraph, then a red sentence about this specific case, then live numbers with deltas, then a 'more' link to the Codex.
- A map-layer grid with saveable presets, and lists that frame the globe (Distant Worlds 2). One chip row on phone, and modders can add categories.
- A frame that bleeds into the world instead of boxes on top of it (Three Kingdoms' ink HUD). Ink and torn-paper edges that dissolve into the globe give the requested volume and authored look while staying in the existing ink-and-paper palette.


---

## Ответы модели решений (Gemini Decisions, 15 ноября 2026, $0,00065)

| Вопрос | Ответ | Уверенность |
|---|---|---|
| Авторская идея | «Досье на столе» | 69 % (перо и кулак 24 %, мир в эфире 7 %) |
| Материал панелей | воронёная сталь с бумагой ≈ тёмное дерево/кожа с бумагой | 48 % / 45 % |
| Орнамент (0–10) | ≈ 4 | 27 % |
| Накладки «бумага → сталь» с ростом напряжённости | да | 76 % |
| Уведомления | телеграммы на скрепке под верхней полосой | 51 % (ряд значков 30 %) |
| Подсказки | вложенные, закрепляются действием (CK3) | 62 % |
| Дальний зум — печатная карта на столе ситуационной комнаты | да | 87 % |
| Режимы карты | блок с миникартой справа внизу | 41 % (слабо) |

**Решение (принято по ответам и исследованию):** основа — «Досье на столе»: бумажные
вкладыши на тёмной доске (дерево для мирных разделов, воронёная сталь для военных — модель
сама разделилась почти поровну), орнамент около 4 из 10 (уголки, плашка заголовка,
разделители), свет сверху слева, три уровня высоты. Из «Пера и кулака» — только «дыхание»:
с ростом напряжённости бумажные накладки сменяются стальными, раскладка и цифры не меняются.
Телеграммы на скрепке для срочного, вложенные подсказки, при дальнем зуме — карта брифинга
на столе. Режимы карты — блок с миникартой справа внизу, проверить на людях.
