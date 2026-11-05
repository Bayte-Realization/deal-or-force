# Native Luau example / Пример нативного Luau

This disabled template uses `engine_script_entry: corecode/main.lua`. Native Luau
is experimental and requires the optional adapter. It is not the separate
`luau_worker` protocol. See `docs/MOD_WORKSPACES_0530.md` and the modding guide.

`corecode/modules/war_damage.mod.lua` is a pure **event-design reference**, moved
from the old loose `mods/luau_events/` directory. That directory had no manifest
and was never an independently loaded mod. The module returns proposed event
tables; neither this template's entrypoint nor the campaign automatically calls
it. Its numeric effect keys illustrate proposals, not a promise of a native Luau
event-registration API. Use the game's JSON/YAML `events/` system for supported
campaign events. The effective three functions are unchanged; overwritten
duplicate definitions and the orphaned old filename's UID were removed.

No live user mod, campaign or loader setting is moved/enabled by this cleanup.

---

Этот выключенный шаблон использует `corecode/main.lua` через
`engine_script_entry`. Нативный Luau — экспериментальный, отдельно устанавливаемый
адаптер; это не протокол `luau_worker`.

`corecode/modules/war_damage.mod.lua` — **справочный пример проектирования
событий**, а не самостоятельно подключаемый мод. В старой папке
`mods/luau_events/` не было манифеста; игра её не загружала. Модуль только
возвращает таблицы предложений: автоматического вызова или регистрации этих
событий нет. Для поддерживаемых игровых событий используйте JSON/YAML в
`events/`. Дубли функций удалены без изменения итогового поведения. Существующие
пользовательские моды и сохранения не затрагиваются.
