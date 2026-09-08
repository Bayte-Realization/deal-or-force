# Управление армиями и стрелки — 6–7 сентября 2026

## Что исправлено

- Чужие дивизии не выбираются одиночным кликом, рамкой и через публичный метод выбора; не открывают панель управления и подробную подсказку. Клик по чужому счётчику сохраняет своё выделение. ПКМ по противнику остаётся способом атаковать СВОИМИ выделенными дивизиями.
- Выбор очищается при смене страны/места игрока и переходе в наблюдение. Команды проверяют владельца отдельно от UI; даже поле результата приказа у чужой дивизии больше не изменяется при отказе. ИИ и другой сетевой игрок сохраняют управление своими войсками.
- Найден и воспроизведён невидимый запрет входа в пустую вражескую провинцию: соседние сильные части полностью останавливали продвижение. Вместо абсолютного запрета действует ограниченное замедление; реальный защитник в целевой провинции по-прежнему требует боя.
- Прогресс входа считается по дивизии, а не суммируется всеми частями в одном счётчике провинции. Учитываются шаблон, позиция, снабжение, местность и речная переправа.
- Явный приказ ПКМ помечается DirectOrder: обновление фронта не заменяет его автоматически. Признак сохраняется в снимках кампании. Для приказов из старых сохранений без этого поля нужно заново выдать ПКМ.
- Shift+ПКМ продолжает маршрут от последней запланированной точки, включая вражескую территорию. Дружественный участок после атаки не исполняется раньше предыдущего вражеского участка. Добавление точки не сбрасывает текущий прогресс.
- Обычный марш снимает оставшийся режим поддержки атаки. Поддержка не занимает пустую провинцию и остаётся на месте, когда цель уже занята своими.
- Проверен и исправлен исход боя только с поддержкой: защитник может отступить, но контроль провинции не меняется без наступающей дивизии. В смешанном бою наступающие занимают провинцию, поддерживающие остаются на месте.
- Если дружественный участок маршрута захвачен противником, с которым уже идёт война, явный приказ сохраняется и передаётся боевой логике. Если цель стала нейтральной и маршрут больше невозможен, приказ останавливается. В списке дивизий появляется предупреждение с поясняющей подсказкой.
- Автоматическое подкрепление боя не должно уводить дивизию с другим явным маршрутом или с приостановленным планом в соседний бой.
- Режим поддержки включается внутри валидируемого MoveArmyCommand, а не второй командой после клика. Отказ в новом маршруте не отменяет поддержку старого приказа; Ctrl+ПКМ в сетевой игре передаёт режим в том же пакете приказа.
- Нейтральная территория без действующей войны остаётся недоступной. Непроходимый маршрут и очередь после боя отражаются отдельными сообщениями. Это не отмена правил доступа.

## Стрелки

Три новые исходные PNG-текстуры, созданные встроенным imagegen (режим generate, без исходного изображения), а не взятые из чужой игры:

- game/gfx/globe/order_move_ink_v2.png — матовая поверхность маршевой стрелки, жёлтая тонировка.
- game/gfx/globe/order_attack_ink_v2.png — печатная краска атаки, красная тонировка.
- game/gfx/globe/order_support_ink_v2.png — штрихованная поверхность поддержки, оранжевая тонировка.

Файлы содержат материал поверхности, не готовый значок со своим фоном. Shader в Scripts/GlobeOrderRibbons.cs наносит его на маршрут: единый наконечник, тонкая тёмная окантовка, сглаженные края, бледная незавершённая часть и заполнение по прогрессу. Геометрия повторяет коридор маршрута, а на глобусе дополнительно разбивается на небольшие сегменты, прижатые к поверхности. Отрисовываются маршруты всех выделенных собственных дивизий; совпадающие пути могут визуально накладываться.

Исходники остаются полноценными PNG; при загрузке тайлы уменьшаются до 512×512 с mipmaps, чтобы не держать полноразмерные изображения в видеопамяти. Для замены рисунка можно подменить соответствующий PNG. Цвета, ширина и параметры материала пока задаются в C#.

### Точные промпты генерации

#### move

Use case: stylized-concept. Asset type: seamless tileable grayscale surface texture applied along a thin army-order arrow ribbon in a modern grand strategy game. This is a MATERIAL, NOT an arrow icon or a mockup. Square opaque texture, surface fills all four edges, seamless left/right and top/bottom. Neutral silver-gray and off-white only, low contrast. Fine matte printed ink grain, very subtle lengthwise horizontal strokes. No objects, no arrowheads, no borders, no badges, no text, no logos, no glow, no perspective, no 3D lighting, no background scene. Understated contemporary military map graphics. Smooth satin plotting tape, calm clean fine paper-grain surface with delicate horizontal fibers. Uniform subtle detail.

#### attack

Use case: stylized-concept. Asset type: seamless tileable grayscale surface texture applied along a thin army-order arrow ribbon in a modern grand strategy game. This is a MATERIAL, NOT an arrow icon or a mockup. Square opaque texture, surface fills all four edges, seamless left/right and top/bottom. Neutral silver-gray and off-white only, low contrast. Fine matte printed ink grain, very subtle lengthwise horizontal strokes. No objects, no arrowheads, no borders, no badges, no text, no logos, no glow, no perspective, no 3D lighting, no background scene. Understated contemporary military map graphics. Dense matte screen-printed ink, slightly stronger horizontal dry ink grain, restrained precise surface without torn edges or splashes.

#### support

Use case: stylized-concept. Asset type: seamless tileable grayscale surface texture applied along a thin army-order arrow ribbon in a modern grand strategy game. This is a MATERIAL, NOT an arrow icon or a mockup. Square opaque texture, surface fills all four edges, seamless left/right and top/bottom. Neutral silver-gray and off-white only, low contrast. Fine matte printed ink grain, very subtle lengthwise horizontal strokes. No objects, no arrowheads, no borders, no badges, no text, no logos, no glow, no perspective, no 3D lighting, no background scene. Understated contemporary military map graphics. Fine consistent diagonal engraving hatch at 45 degrees over matte ink grain, subtle repeating hatching distinguishes support orders without being loud or futuristic.

## Проверки

- x64 .NET: DealOrForce.csproj собирается без ошибок и предупреждений.
- Tools/ArmyInteractionTests: 45 проверок PASS. Тест использует реальный provinces.png / definition.csv и границу RUS–UKR. До исправления тест входа в пустую провинцию под соседним прикрытием воспроизводимо падал.
- Проверены: 12 запрещённых команд чужой дивизии, смена места/наблюдение, пустая и защищённая цель, неизменность прямого приказа после обновления фронта, очередь, поддержка, дружественный марш, нейтральная территория и снимок кампании.
- Для флотов выполнены базовые проверки отказа чужому владельцу и движения через шов долгот. Переработки морской навигации в этом изменении нет.
- Runtime-проба .fennara/scripts/runtime/army_access_observe.gd запускает настоящую Main.tscn, выбирает Россию через меню и подаёт события мыши/клавиатуры. Проверены чужой/свой клик, публичный выбор, красная атака, Ctrl+ПКМ поддержки и жёлтый марш. Скриншоты просмотрены: .fennara/army-attack-texture.png, army-support-texture.png, army-move-texture.png.
- Fennara: скан скриптов не выявил ошибок, но встроенная C#-сборка использовала x86 dotnet без SDK. Поэтому компиляция проверялась явным x64 dotnet, визуальное поведение — отдельным запуском Godot.
- Один запуск D3D12 прошёл с 60 FPS на паузе. В следующем запуске драйвер не создал command queue, Godot перешёл на Vulkan и показал примерно 6–12 FPS. Причина отказа устройства не установлена и этим патчем не устранена; это не результат полноценного профилирования производительности.

## Воспроизведение тестов

Из корня проекта в PowerShell:

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' run --project Tools/ArmyInteractionTests/ArmyInteractionTests.csproj
& 'C:/Program Files/dotnet/dotnet.exe' build DealOrForce.csproj --no-restore -v minimal
```

Проверки не сохраняют кампанию пользователя и не меняют исходные карты.

## Границы проверки

Это исправление конкретных воспроизведённых причин остановки, а не утверждение, что решены все возможные проблемы армейского ИИ и флотов. Масштабный сетевой матч, все варианты погрузки/высадки и длительная военная кампания отдельно не проверялись. Для LAN обоим игрокам нужна одинаковая обновлённая сборка.
