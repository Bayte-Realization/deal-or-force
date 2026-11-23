# Dev diary #1: the world of 1 January 2024 (EN and RU)

> **Editorial note.** Draft of the first dev diary for itch.io, Steam announcements, Telegram and
> strategy communities. Checked against the code at `EntertAlpha 0.8.5.3` on 23 November 2026.
> Numbers come from the game data (sources at the top of `STORE_COPY_GOOGLE_PLAY_RUSTORE_EN_RU.md`).
> No other games are named.

---

# English version

## Dev diary #1: the world of 1 January 2024

This is the first dev diary for **Deal or Force**.

I make it alone, as **Bayte Realization**, openly with the help of AI. The idea is simple to say:
start on the morning of 1 January 2024, with the world as it stood that day, and let you play any
country in it.

Here is what is in the game now, what is still rough, and what I would like to hear from you.

---

### 1. The map of that morning
The campaign opens on **1 January 2024** on **34,138 provinces**, **1,846 regions** and more than
**7,000 cities**, on a 3D globe or a flat map.

The wars of that day are already there. Russia and Ukraine are at war. Sudan's army is fighting the
Rapid Support Forces. The Houthis hold Sanaa, the M23 is advancing in eastern Congo, and Myanmar's
resistance is fighting the junta. **21 armed movements and de facto governments** hold their own
ground and have their own fighters, in the war, truce or peace they had that morning. NATO, the
CSTO and the guarantees of that day are on the map too.

You can play any of more than 200 countries, or switch to **observer mode** and let the computer
run all of them.

### 2. Goods, trade and megaprojects
The economy has **63 goods** made by **94 production recipes**. Prices come from what is produced
and traded. An embargo or a war cuts trade between the two countries, and distance makes far
partners less attractive than near ones.

One thing I want to be clear about, because earlier texts said otherwise: trade does not sail
through straits in the game. Straits and canals matter for **fleets and troop convoys**, which have
to go around one held by an enemy. A hostile supplier hurts you in a different way: if a country
at war with you, or embargoing you, supplied 35% or more of a key good, your megaprojects that need
it build more slowly.

**Megaprojects** are new: 16 kinds, from high-speed rail and a fusion grid to a deep-water port and
a launch complex. They cost money, political capital and goods from the market, and allies can
join a consortium and share the cost.

### 3. Deals at the table
Peace is written at a conference. Every province you keep costs war score, and the screen tells
you why a demand cannot go through. Beside that: alliances, guarantees, pacts, embargoes and
seven kinds of dependent state, from protectorate to personal union.

If you would rather not run every department yourself, you can hand any of **11 areas of
government** (budget, trade, army, navy, diplomacy and others) to your own staff and take them back
at any time.

Does the computer play by your rules? For most actions it uses the same commands you do, but not
for all of them yet. A review on 18 November listed the differences, from peace terms to how often
the computer reconsiders its plans. I am closing them one at a time, starting with peace, and the
list is public in the repository (`docs/PLAYER_AI_PARITY_2026_11_18_RU.md`).

### 4. War you can read
Fronts move province by province and armies need supply. A battle window shows the phase of the
fight and each modifier on it: flanking, river crossings, supply, encirclement. Artillery, rocket
batteries, aircraft, drones and air defence are formations of their own, based in regions and sent
on missions; they pay for every day they fly over a defended front.

### 5. Mods and Android
The game is free and open source under the **GNU GPL v3**, built with **Godot 4.8 and .NET 10**.
Nearly everything is data: goods, buildings, laws, events and the history of the first day are JSON
files, and a mod overrides only the keys it names. The examples in `mods/_templates` include a 1914
starter, which runs on the 2024 map, and a small three-province fantasy map. They are starting
points for modders, not finished eras.

There is also an **early Android build**. It runs the same simulation as the PC version, with touch
controls for the camera and orders. It is a technical preview that you install by hand: the
interface is not yet scaled for phones, and I have no measurements from devices yet.

### Three questions for you
1. When you open another country's diplomacy panel, what do you look for first?
2. Which war of 1 January 2024 would you play first, and from which side?
3. If you play strategy games on a phone or tablet, what makes touch controls on a big map work or
   fail for you?

The Windows alpha is on **itch.io** (`https://bayte-realization.itch.io/deal-or-force`). Leave your
answers in the comments or in Telegram. The next diary is about how the simulation got faster, with
the measurements and what they do and do not show.

---

# Русская версия

## Дневник разработки №1: мир 1 января 2024 года

Это первый дневник разработки **Deal or Force**.

Я делаю игру один, под маркой **Bayte Realization**, открыто и с помощью ИИ. Замысел простой:
начать утром 1 января 2024 года с миром таким, каким он был в тот день, и дать сыграть за любую
страну.

Ниже о том, что в игре есть сейчас, что ещё сыро и что я хочу от вас услышать.

---

### 1. Карта того утра
Кампания начинается **1 января 2024 года**: **34 138 провинций**, **1 846 регионов** и больше
**7 000 городов** на 3D-глобусе или плоской карте.

Войны того дня уже идут. Россия воюет с Украиной. Армия Судана воюет с Силами быстрого
реагирования. Хуситы держат Сану, M23 наступает на востоке Конго, сопротивление Мьянмы воюет с
хунтой. **21 вооружённое движение и непризнанная власть** держат свою землю, у каждой свои бойцы и
та война, перемирие или мир, что были у неё в то утро. НАТО, ОДКБ и гарантии того дня тоже на
карте.

Играть можно за любую из более чем 200 стран, а в **режиме наблюдателя** все страны ведёт
компьютер.

### 2. Товары, торговля и мегапроекты
В экономике **63 товара** и **94 производственных рецепта**. Цены складываются из того, что
произведено и продано. Эмбарго или война обрывают торговлю между двумя странами, а дальние
партнёры выгодны меньше, чем близкие.

Скажу прямо, потому что в прежних текстах было иначе: торговля в игре не идёт через проливы.
Проливы и каналы важны для **флота и морских перевозок войск**, которым приходится обходить пролив,
занятый противником. Враждебный поставщик бьёт по-другому: если страна, которая воюет с вами или
держит против вас эмбарго, давала 35% или больше ключевого товара, ваши мегапроекты, которым он
нужен, строятся медленнее.

**Мегапроекты** появились недавно: 16 видов, от скоростной железной дороги и термоядерной
энергосети до глубоководного порта и космодрома. Они требуют денег, политического капитала и
товаров с рынка, а союзники могут войти в консорциум и разделить расходы.

### 3. Сделка за столом
Мир пишется на конференции. Каждая провинция, которую вы оставляете себе, стоит военного счёта, а
экран объясняет, почему требование не проходит. Кроме этого есть союзы, гарантии, пакты, эмбарго и
семь видов зависимых государств, от протектората до личной унии.

Если не хочется вести каждое ведомство самому, любое из **11 направлений управления** (бюджет,
торговля, армия, флот, дипломатия и другие) можно передать своему штабу и забрать обратно в любой
момент.

Играет ли компьютер по вашим правилам? В большинстве действий он пользуется теми же командами, что
и вы, но пока не во всех. Разбор 18 ноября перечислил расхождения, от условий мира до того, как
часто компьютер пересматривает планы. Я закрываю их по одному, начиная с мира, а список открыт в
репозитории (`docs/PLAYER_AI_PARITY_2026_11_18_RU.md`).

### 4. Война, которую можно прочитать
Фронт движется по провинциям, армиям нужно снабжение. Окно боя показывает фазу сражения и каждый
модификатор: фланги, переправы, снабжение, окружение. Артиллерия, ракетные части, авиация,
беспилотники и ПВО действуют как отдельные соединения со своими базами и заданиями и несут потери
за каждый день над прикрытым фронтом.

### 5. Моды и Android
Игра бесплатна, код открыт по лицензии **GNU GPL v3**, движок **Godot 4.8 и .NET 10**. Почти всё в
игре хранится в данных: товары, здания, законы, события и история первого дня лежат в файлах JSON,
а мод меняет только те ключи, которые называет. Среди примеров в `mods/_templates` есть заготовка
1914 года на карте 2024-го и маленькая фэнтези-карта из трёх провинций. Это отправные точки для
моддеров, а не готовые эпохи.

Есть и **ранняя сборка для Android**. Она считает ту же симуляцию, что и версия для ПК, с сенсорным
управлением камерой и приказами. Это технодемо, которое ставится вручную: интерфейс ещё не
масштабирован под телефон, замеров с устройств пока нет.

### Три вопроса к вам
1. Когда вы открываете дипломатию чужой страны, на что смотрите первым делом?
2. Какую войну 1 января 2024 года вы сыграли бы первой и за какую сторону?
3. Если вы играете в стратегии на телефоне или планшете, что делает сенсорное управление большой
   картой удобным или неудобным?

Альфа для Windows лежит на **itch.io** (`https://bayte-realization.itch.io/deal-or-force`).
Отвечайте в комментариях или в Telegram. В следующем дневнике расскажу, как ускоряли симуляцию: с
замерами и с тем, что они показывают и чего не показывают.
