# Unclaimed region anchors / Опорные точки ничейных регионов

This is active map data, not an obsolete country list. `UnclaimedLand` reads JSON/YAML
here when attaching province topology. A point can create a missing administrative
region only on an existing **unowned land province**. It cannot take somebody's land.
The base entry identifies Bir Tawil, whose source polygon has no administrative record.

Это действующая часть карты: опорная точка помогает создать карточку региона для
существующей ничейной провинции (в базовой карте — Бир-Тавиль). Координаты не рисуют
землю и не отбирают её у страны. Колонизация использует полученный обычный регион.

```yaml
# map_data/unclaimed_regions/90_my_regions.yml
my_unclaimed_region:
  name: Example region
  latitude: 21.91
  longitude: 33.66
```

`name` is a fallback label; geography and ownership must already agree with the map.
Only add a point when the unowned polygon really lacks a region. JSON and YAML share
the same reader; ship only one format for one entry, not duplicate twins.
