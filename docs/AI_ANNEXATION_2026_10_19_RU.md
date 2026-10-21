# Аннексия и воссоединение: ИИ забирает страну целиком

Дата: 19.10.2026.

Раньше ИИ мог объявить войну за одну пограничную область и на мире просить
несколько провинций. Страну целиком он забирал только в редком «решающем»
мире, и даже тогда цена провинций могла превысить военный счёт: победитель
оставлял противника существовать обрубком. Теперь у ИИ есть отдельная
амбиция — присоединить соседа — и мир, который доводит это до конца.

## Когда ИИ хочет аннексию

`Scripts/Simulation/AnnexationAmbition.cs` оценивает пару стран:

- соотношение сил (`AiAnnexationPowerRatio`, по умолчанию 2.8);
- размер цели: не больше `AiAnnexationMaxRegions` (12) областей и не больше
  `AiAnnexationMaxPopulationShare` (35 %) населения атакующего;
- общая сухопутная граница, цель не субъект, не союзник и не под гарантией;
- культурное родство: общий первоначальный народ делает войну
  **воссоединением** (`reunification`), а не завоеванием;
- правительство без большой сдержанности (`AiAnnexationRestraintMax`) может
  назвать войну **гегемонией** (`hegemony`) даже без формального повода.

`AnnexationAmbition.Appetite` добавляет небольшой бонус к обычной оценке войны
(`AiAnnexationTargetBonus`), чтобы слабый и родственный сосед не проигрывал
сравнение только потому, что он слаб. `Statecraft.CasusBelli` возвращает
`hegemony` для такой пары, поэтому война получает причину, а не штраф за
войну без причины.

При объявлении `AiStatecraft.Warmaking`:

- не тратит ультиматум на одну область, если цель — вся страна;
- передаёт `reunification`/`hegemony`/`irredentism`/`reconquest` в
  `CampaignDiplomacy.DeclareWar`;
- указывает столичную область цели как `ClaimedStateMapIndex`.

## Мир: страна, а не обрубок

`AiPeaceOutcomes.WriteDecisiveSettlement` теперь различает:

- **возврат своего** (родитель против отколовшегося) — порог
  `ReclaimDecisiveScore`/`ReclaimSecuredShare`;
- **воссоединение** — `AiReunificationDecisiveScore` (55) и
  `AiReunificationOccupationShare` (45 %);
- **гражданскую войну** — любая сторона, включая две фракции одного
  родителя, при `AiCivilAnnexationScore` (55) и
  `AiCivilAnnexationOccupationShare` (45 %). Взятие столицы по-прежнему
  даёт более дешёвый путь, но больше не является единственным;
- **завоевание** — `AiAnnexationDecisiveScore` (62) и
  `AiAnnexationOccupationShare` (50 %).

Если порог взят, в `PeaceDemand` ставится `Unconditional`: конференция
подписывает передачу всей страны, даже если суммарная цена провинций больше
военного счёта. Это не бесплатная земля — порог требует разгрома и контроля
над страной. `PeaceConference.Round` при этом не «округляет» страну, оставляя
ей острова и анклавы; `BlockReason` не отклоняет договор из-за бюджета.

Гражданская война не превращается в зависимость: `ConsiderSubjugation`
пропускает мятежников и гражданские пары. Победившая сторона либо берёт
государство, либо воюет дальше.

## Данные

Все числа — обычные `Defines`, их можно переопределить модом:

- `AiAnnexationPowerRatio`, `AiAnnexationMaxRegions`,
  `AiAnnexationMaxPopulationShare`, `AiAnnexationRestraintMax`,
  `AiAnnexationTargetBonus`, `AiHegemonyNotorietyMax`;
- `AiAnnexationDecisiveScore`, `AiAnnexationOccupationShare`;
- `AiReunificationDecisiveScore`, `AiReunificationOccupationShare`;
- `AiCivilAnnexationScore`, `AiCivilAnnexationOccupationShare`.

Причины `hegemony` и `reunification` переведены в
`game/localization/{english,russian}/270_naming.json`:

- «The strong do what they can» / «Сильные делают то, что могут»;
- «One people, one state» / «Один народ — одно государство».

## Проверки

```powershell
dotnet run --project Tools\FormableTests -- --annexation-only
dotnet run --project Tools\FormableTests -- --war-aims-only
```

`--annexation-only` проверяет воссоединение Германии и Австрии, распознавание
двух фракций одной гражданской войны, полный мир при 60 военного счёта и
50 % контроля, а также причину `hegemony` для сильной авторитарной страны
против слабого неродственного соседа.
