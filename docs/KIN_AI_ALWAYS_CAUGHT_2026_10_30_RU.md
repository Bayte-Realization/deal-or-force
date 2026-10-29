# ИИ всегда попадался: одна кость на два разных решения

**Дата:** 30 октября 2026. **Ветка:** `ReimAlpha`. **Статус:** исправлено, проверено тестом и десятью
годами мира.

## Как это обнаружилось

Длинный прогон стенда (`Tools/SimBench` в режиме `--kin`, 3650 дней, seed 1, мира без игрока)
показал картину, которая не могла быть балансом:

```text
DAY 3650: agit=0+26exposed volunteers=0+3exposed proxies=0+1exposed
          offices=0 opened=11 closed=0 shut=11 exposed=11 civilwar=6
```

То есть за десять лет **все тридцать операций ИИ были раскрыты**: 26 агитаций, 3 отправки
добровольцев, 1 основание прокси — и ни одна не прошла тихо. При заявленных 22 % раскрытия
вероятность «поймали все 26» равна 0,22²⁶ ≈ 4·10⁻¹⁸. Это не невезение, это конструкция.

## Причина

Решения принимались **одной и той же костью**:

```csharp
// KinStatecraft.Ai - решает, действовать ли
var roll = MovementRules.Roll(campaign.Seed, country.Id, campaign.ElapsedDays, candidate.Region.MapIndex);
if (roll < defines.AiKinProxyChance)     Do(campaign, country, candidate.Region, "found_proxy");
if (roll < defines.AiKinAdminChance)     Do(campaign, country, candidate.Region, "open_administration");
if (roll < defines.AiKinVolunteersChance) Do(campaign, country, candidate.Region, "volunteers");
if (roll < defines.AiKinAgitationChance) Do(campaign, country, candidate.Region, "agitate");

// KinStatecraft.Do - решает, заметила ли служба хоста
var caught = MovementRules.Roll(campaign.Seed, country.Id, campaign.ElapsedDays, region.MapIndex) < discovery;
```

Аргументы броска совпадали полностью: тот же seed, та же страна, тот же день, тот же регион в
качестве соли. А шансы ИИ (2-10 %) всегда **ниже** порогов раскрытия (15,4-45 %). Значит
«операция состоялась» уже доказывало «бросок ниже порога» — раскрытие было не проверкой, а
следствием. Второй бросок никогда ничего не решал.

Это же объясняет, почему механика выглядела работающей: эффект (рост недовольства, обида хоста)
применяется и при раскрытии, отличается только его размер. Игра не падала и не молчала — она
просто каждый раз объявляла провал.

## Проверка, что дело не в генераторе

Первая гипотеза была «сломан `MovementRules.Roll`». Тест отверг её числом: 40 000 бросков дают
**22,18 %** ниже 0,22 и **45,36 %** ниже 0,45. Генератор равномерный.

## Исправление

У раскрытия своя соль, отдельная от соли решения, — тем же приёмом, что уже применён в
`ParallelAdministration` (`MapIndex + 7` на раскрытие, `+ 13` на закрытие):

```csharp
internal static int DiscoverySalt(ScenarioState region, string action) =>
    region.MapIndex + (action switch
    {
        "agitate" => 3,
        "volunteers" => 5,
        "found_proxy" => 11,
        _ => 17
    });
```

## Что теперь доказано

| Проверка | До | После |
|---|---|---|
| `--kin-only` | 71 PASS | **78 PASS** |
| Доля раскрытий среди операций, на которые ИИ решился | 100 % (по построению) | **23,0 %** (при заявленных 22 %) |
| Эталонные отпечатки (`--living-world-fingerprint-only`, `--movement-power-fingerprint-only`) | — | не сдвинулись |
| `--integrity-only`, `--movement-zone-only`, `--revolt-lineage-only`, `--revolt-recovery-only` | — | exit 0 |

Новый тест «the decision to act and the discovery roll are two different dice» разыгрывает 4000
дней, отбирает те 404, в которые ИИ действовал бы, и требует, чтобы среди них ловилось около
22 %, а не все. Со старой солью он даёт 100 % и падает.

## Чему это учит

Проверять надо не «работает ли механизм», а «меняется ли исход». Механика с одним исходом
(всегда пойман) неотличима от работающей по логам: строки пишутся, состояние меняется, игра идёт.
Отличил её только счётчик исходов за десять лет — и отношение двух чисел, которое человек
посчитал бы в уме, если бы увидел их рядом.
