# C# для разработчика и моддера

## Зачем здесь C#

Godot.NET позволяет писать производительную детерминированную симуляцию на C#, сохраняя Godot для сцены, ввода и графики. Этот же язык использовался в s&box, поэтому правила переносятся без переписывания формул.

## Типы и объект страны

```csharp
public sealed class Nation
{
    public string Tag = "";              // текст
    public long Population;              // большое целое число
    public float Stability = 0.65f;       // дробь
    public bool IsPlayer;                 // true / false
}
```

Класс — описание типа; экземпляр — конкретная страна. `public` открывает член другим частям программы. `sealed` запрещает наследование и упрощает модель данных.

```csharp
var korea = campaign.NationByTag("KOR");
if (korea is null) return;
korea.Stability = Math.Clamp(korea.Stability + 0.01f, 0f, 1f);
```

`var` просит компилятор вывести тип, `if` проверяет условие, `return` прекращает метод. Суффикс `f` создаёт `float`; `Math.Clamp` удерживает значение между 0 и 1.

## Коллекции и LINQ

```csharp
List<Army> armies = campaign.Armies;
var reserves = armies.Where(a => a.NationId == korea.Id && a.OrderKind == ArmyOrder.ReserveFront);
foreach (var army in reserves)
    army.Organization = Math.Min(1f, army.Organization + 0.02f);
```

`List<T>` — изменяемый список. `Where` отбирает элементы. LINQ читаем, но в расчёте тысяч объектов каждый день обычный цикл и переиспользуемый буфер часто быстрее.

## Методы, свойства и null

```csharp
public int Day => Campaign?.ElapsedDays ?? 0;

public Nation Nation(string tag)
{
    return Campaign?.NationByTag(tag);
}
```

Первая запись — вычисляемое свойство. `?.` прекращает цепочку при `null`, `?? 0` подставляет запасное значение. Вторая — метод с параметром и возвращаемым типом.

## События и partial

`Campaign.Changed` — событие. UI подписывается через `+= Refresh` и отписывается через `-= Refresh`. Симуляция сообщает об изменении, но ничего не знает о панели Godot.

`partial class DealOrForceGame` — один класс, разнесённый по нескольким файлам. Это организация исходников, а не несколько игровых объектов.

## Интерфейсы и моды

```csharp
public sealed class MyMod : IGlobalityMod
{
    public void OnDayAdvanced(ModContext context)
    {
        context.Log($"Day {context.Day}");
    }
}
```

Интерфейс — договор: загрузчик не знает `MyMod`, зато знает callback любого `IGlobalityMod`. `$"..."` — интерполированная строка.

## Команды вместо прямого UI

```text
Button -> new SomeCommand(...) -> Campaign.Commands.Enqueue(...)
-> Campaign.Tick() -> command.Apply(campaign) -> Changed -> UI redraw
```

Команда хранит лишь значения, необходимые для повторения действия. Она не хранит `Button`, `Node` или мышь. Такой код проще тестировать и синхронизировать.

## Детерминированный случай

Не используйте `new Random()` внутри правил: replay и multiplayer разойдутся. Используйте `campaign.Random.NextFloat()`, `Range()` или `Chance()` — состояние генератора принадлежит кампании и воспроизводится от seed.

## C# и Godot

```csharp
public partial class DealOrForceGame : Control
{
    public override void _Ready() { }
    public override void _Process(double delta) { }
}
```

Godot вызывает `_Ready` после появления узла в сцене, `_Process` — каждый кадр. Игровые дни нельзя связывать с FPS; код накапливает `delta` и отдельно вызывает `Campaign.Tick()`.

## Ошибки и безопасность

- Проверяйте результат поиска по тегу/ID на `null`.
- Внешний JSON и модовый код могут быть неверны: исключение ловится на границе одного мода.
- Не скрывайте ошибку пустым `catch`; пишите её в log.
- Не изменяйте список внутри `foreach` по этому списку.
- Не запускайте файловый ввод-вывод или сеть в ежедневном hot path.

## Маршрут обучения

1. Измените define через JSON.
2. Напишите GDScript callback раз в 30 дней.
3. Скопируйте C# шаблон и выведите сообщение в `OnCampaignCreated`.
4. Прочитайте `SimulationCore.cs` и реализуйте маленький `ICampaignCommand`.
5. Затем добавляйте новую систему и UI по схеме из `ARCHITECTURE.md`.
