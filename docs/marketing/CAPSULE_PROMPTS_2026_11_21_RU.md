# ТЗ и промпты на Key Art и капсулы Store (Steam + itch.io) — 21 ноября 2026

> **Готовые 2K-мастера Key Art (`Nano Banana 2` / Creative Claw, `#343`).** Оба текстово-чистых 2K-мастера сгенерированы и сохранены в `docs/marketing/` (папка защищена пустым `.gdignore`, чтобы Godot не импортировал промо-материалы в игру):
> - **Вариант A (`16:9` для Steam Main/Header/Hero)**: [`docs/marketing/capsule_a_globe_16x9.png`](capsule_a_globe_16x9.png) — `2752 × 1536 px` (`6,74 МБ`), чистая левая безопасная зона под логотип.
> - **Вариант B (`4:3` для itch.io Cover / Steam Vertical)**: [`docs/marketing/capsule_b_warroom_cover_4x3.png`](capsule_b_warroom_cover_4x3.png) — `2400 × 1792 px` (`7,34 МБ`), чистая верхняя безопасная зона под логотип.
> - **Официальный Brand Kit для финального композитинга**: `D:\Godot Projects\Bayte Realization Brand Kit\2026-10-27\02_Deal_or_Force\` — содержит прозрачный логотип **`deal_or_force_lettering_on_dark.png`** (накладывается в безопасные зоны Капсулы A и Капсулы B), **`deal_or_force_icon_1024.png`** и **`game_splash_1920x1080.png`**.

---

## 1. Обязательная матрица размеров Steam (2× High-DPI, стандарт 2025–2026) и itch.io

| Площадка / ассет | Точный размер (px) | Соотношение | Правило текста и логотипа |
|---|---:|---|---|
| **Steam Main Capsule** | `1232 × 706` | `1.74 : 1` | Только арт + логотип `DEAL or FORCE`. **Запрещены** оценки, награды, слоганы и кнопки «Wishlist». |
| **Steam Header Capsule** | `920 × 430` | `2.14 : 1` | Главная шапка страницы. Проверить читаемость логотипа при ширине 230 px. |
| **Steam Small Capsule** | `462 × 174` | `2.66 : 1` | **Тест 120×45 px обязателен!** Крупный логотип + 2–3 контрастные цветовые массы, без мелких деталей карты. |
| **Steam Vertical Capsule** | `748 × 896` | `5 : 6` | Распродажи и тематические витрины. Логотип в верхней или нижней трети. |
| **Steam Library Capsule** | `600 × 900` | `2 : 3` | Постер в библиотеке игрока. |
| **Steam Library Hero** | `3840 × 1240` | `3.10 : 1` | **Строго БЕЗ текста и логотипа** (логотип накладывается отдельно прозрачным PNG). |
| **Steam Library Logo** | `>= 1280` ш. / `720` в. | прозрачный PNG | Чистое начертание `DEAL or FORCE` (полированная латунь с тёмной фаской). |
| **itch.io Cover Image** | `630 × 500` (`1260 × 1000` 2×) | `1.26 : 1` | Минимум мелкого текста; читается в каталоге на `315 × 250`. |
| **YouTube / Shorts Key Art Master** | `1920 × 1080` | `16 : 9` | Универсальный мастер-холст, из которого кадрируются Steam Main (`1232×706`) и Header (`920×430`). |

---

## 2. Визуальный код бренда и цветовые токены *Deal or Force*

- **Палитра (в тон реального HUD 0.8.3.5+):**
  - Обсидиановый сланец (фон / безопасная зона логотипа): `#171615` → `#201E1C`
  - Матовая латунь (акценты, рамки, логотип, меридианы): `#C8A15A` / `#E4C37E`
  - Батиметрический глубокий океан (контраст): `#0F2A3A` → `#1E5266`
  - Фронтовой кармин (линии боёв, блокады проливов, напряжение): `#C83E32`
  - Дипломатический изумруд (торговые маршруты и союзы): `#2F8F6B`
- **Композиционная сетка безопасной зоны (для `1920×1080` и `1232×706`):**

```text
+-----------------------------------------------------------------------+
|                                                                       |
|   [ SAFE ZONE ДЛЯ ЛОГОТИПА ]        [ СВЕТОКРИТИЧНЫЙ ФОКУС АРТА ]     |
|   Левая 42% ширины:                 Правая 58% ширины:                |
|   Тёмный сланцевый градиент         Освещённый 3D-глобус Евразии      |
|   без ярких огней — здесь           с рельефом, золотыми торговыми    |
|   идеально читается латунный        дугами и красной дугой блокады    |
|   логотип "DEAL or FORCE"           у пролива (Ормуз / Босфор)        |
|   даже в масштабе 120×45 px         + силуэты флота/штабной карты     |
|                                                                       |
+-----------------------------------------------------------------------+
```

---

## 3. Два готовых промпта Key Art для `creative-claw` (`generate_image`)

### Вариант A — «Штабной глобус и морской узел» (рекомендуется для Steam Main `1920×1080` / `1232×706`)
- **Параметры**: `aspect_ratio: "16:9"`, размер `1920×1080`, `output: docs/marketing/capsule_a_globe_16x9.png`.
- **Промпт (EN, для `generate_image`)**:
  > Mastercrafted AAA grand strategy game key art, dark architectural war-room aesthetic, no text, no letters, no watermark. On the right 60% of the frame: a hyper-detailed physical 3D geopolitical globe of Eurasia, Europe and the Middle East lit by dramatic north-west golden rim light, sculpted topographic mountain relief, deep bathymetric teal-navy oceans, luminous brushed-brass national border filigree, glowing emerald maritime trade arcs converging on a narrow strategic strait, and a vivid crimson frontline tension pulse radiating from a contested maritime chokepoint. Subtle dark-slate studio atmosphere on the left 40% of the frame forming a clean negative-space gradient (#171615) reserved for title typography. Moody brass and obsidian palette, ultra-crisp cartographic craft reminiscent of a museum brass-inlaid geopolitical atlas, dramatic contrast readable at thumbnail size, cinematic volumetric rim glow, 8k editorial illustration.
- **Negative prompt**:
  > text, typography, logo, letters, blurry map, messy UI windows, cartoon style, neon sci-fi cyberpunk, generic medieval swords, cluttered left side, watermark.

### Вариант B — «Стол переговоров или сила» (рекомендуется для itch.io Cover `630×500` / Steam Vertical `748×896`)
- **Параметры**: `aspect_ratio: "4:3"` (под кроп `630×500`) или `"3:4"` (под `600×900` / `748×896`), `output: docs/marketing/capsule_b_warroom_cover.png`.
- **Промпт (EN, для `generate_image`)**:
  > Handcrafted AAA political grand strategy cover illustration, no text, no letters, no logos. Overhead three-quarter view of an obsidian-slate and brushed-brass geopolitical command table bathed in warm desk-lamp chiaroscuro. Centerpiece: an illuminated relief map of Europe and the Mediterranean showing bold contrasting faction territories (deep amber-gold vs crimson-red) with crisp coastal ink lines, brass compass dividers, a stamped diplomatic treaty seal on laid archival paper on the left side, and metallic tactical division markers and miniature radar/naval silhouettes pushing across a glowing crimson frontline on the right side — visual duality of diplomacy versus military force. Upper third kept darker obsidian slate (#171615) as clean title safe zone. High contrast, sharp silhouettes readable at 120px thumbnail scale, painterly realism, museum-grade tactile materials.
- **Negative prompt**:
  > text, words, flags with gibberish text, blurry details, cluttered top third, generic fantasy armor, low contrast, flat vector clipart, watermark.

---

## 4. Чек-лист приёмки готовой капсулы (до загрузки в Steam/itch.io)

1. Уменьшить `Small Capsule` до **`120 × 45 px`** на тёмно-синем фоне клиента Steam (`#1B2838`) — слово **`DEAL or FORCE`** и силуэт глобуса/конфликта должны считываться за **< 1 секунду**.
2. **Тест обложки YouTube (`1280×720`)**: по данным `howtomarketagame.com` (`#332`), креаторы используют капсулу игры как основу для превью своих роликов; слабая или «грязная» капсула напрямую снижает отклик YouTube/Twitch-авторов.
3. Поставить рядом с живыми эталонами Steam (`Age of History 3` — 50/50 split-карта+золотой заголовок, `Dummynation` — жетоны флагов на приглушённой карте, `Terra Invicta`, `Espiocracy`, `Suzerain`) — наша капсула должна отличаться сланцево-латунным контрастом и читаемостью, а не сливаться в «серую карту».
4. Проверить, что `Library Hero` (`3840×1240`) — строго чистый арт без текста, а логотип загружен отдельным прозрачным PNG.
5. Для блока «Об игре» на Steam дополнительно нарезать **4–6 анимированных AVIF/WebP-баннеров** (`616×220`–`616×346 px`, 4–6 сек., по образцу *Terra Invicta* / *Suzerain*): блокада пролива, бой Волны 3, мегапроект-консорциум, смена эпох 2024→1914→Aethelgard, режим наблюдателя.
