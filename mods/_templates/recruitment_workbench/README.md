# Roomier recruitment / Более просторное окно набора

No compiler, engine project or copied base game needed. Only six numeric overrides.

1. Copy this directory outside `_templates` into your game's `mods` folder.
2. Give it a unique folder name and manifest `id`; change `enabled` to `true`.
3. Edit only the numbers in `interface/roomier.json`; restart the game to load the mod.
4. Open Recruit & deploy. Cards, text and targets are larger. At narrow widths
   the catalogue uses one column; it still scrolls and the order actions stay fixed.

The numbers override only the named metrics. They do not change recruitment
costs, training rules, saves, units or the rest of the interface's typography.
`two_card_width_pixels` is the catalogue width needed for two columns, not a card width.
Physical-pixel minima are evaluated before the player's interface-scale preference.
Do not copy a whole base UI file to change a few numbers.

## По-русски

Компилятор, проект движка и копия всей базовой игры не нужны: только шесть чисел.

1. Скопируйте эту папку из `_templates` в пользовательскую папку `mods` игры.
2. Задайте уникальные имя папки и `id` в manifest; поставьте `enabled: true`.
3. Измените числа в `interface/roomier.json`, затем перезапустите игру.
4. Откройте набор войск: карточки, текст и кнопки крупнее. На узком экране
   каталог станет одноколоночным; прокрутка остаётся, кнопки заказа закреплены.

Переопределяются только перечисленные параметры. Стоимость, правила подготовки,
сохранения, типы войск и шрифты остальных экранов не меняются.
`two_card_width_pixels` — ширина каталога для двух колонок, не ширина одной карточки.
Это физические минимумы, поверх которых действует масштаб интерфейса игрока.
Удалите мод из активных — вернутся базовые параметры.
