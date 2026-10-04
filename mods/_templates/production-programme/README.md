# Production programme / Производственные программы

Copy this folder directly into `mods/production-programme/`, then enable the
package in the launcher. `_templates/` is not scanned as an installed mod.

Скопируйте эту папку в `mods/production-programme/` и включите пакет в лаунчере.
Шаблон сам по себе не активен и не меняет баланс обычной кампании.

The sample assigns 12 units per factory, 50% conversion efficiency and moves
Steel into the military allocation pool. Omitted goods/settings inherit earlier
layers. New goods declared in this mod's `common/goods/` can be named in `lines`.

This changes **allocation**, not a recipe's source factories or output.
For a complete military steel industry, also adjust Steel recipes under
`common/production/`. Two pools remain supported: `civilian` and `military`.
Units per factory must be 1–1000; efficiency must be 0.05–1. Invalid fields are
reported without replacing valid inherited values.

Настройки считываются при загрузке кампании, не через Ctrl+Shift+R:
горячая перезагрузка обновляет только оформление и локализацию.
