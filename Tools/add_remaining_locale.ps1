$path = "D:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian\99_world_polish.json"
$content = Get-Content $path -Raw -Encoding UTF8

# Replace final closing brace with remaining entries + closing brace
$tail = @"
  "The goal must be met at the end and for at least a third of the term.": "Цель должна быть достигнута в конце и не менее чем на треть срока.",
  "In the lead for {0} days.": "В лидерах уже {0} дней.",
  "Targeted social guarantees": "Целевые социальные гарантии",
  "Technical education": "Техническое образование",
  "Term progress": "Прогресс срока",
  "The movement needs greater public standing.": "Движению нужна большая общественная репутация.",
  "The parent must authorise a referendum in the movements panel.": "Родительское государство должно авторизовать референдум в панели движений.",
  "The peace dividend": "Мирный дивиденд",
  "The settlement cannot dissolve the parent state.": "Поселение не может растворить родительское государство.",
  "There is no local network or permission for this operation.": "Нет локальной сети или разрешения для этой операции.",
  "There is no parent state to negotiate with.": "Нет родительского государства для переговоров.",
  "There is no peaceful homeland claim to negotiate.": "Нет мирного претензий на родину для переговоров.",
  "This proclamation has not already been made": "Это провозглашение ещё не было сделано",
  "This region has no urgent need for a field hospital.": "В этом регионе нет срочной необходимости в полевом госпитале.",
  "Time under this course": "Время под этим курсом",
  "Trust grows with every commitment kept.": "Доверие растёт с каждым выполненным обязательством.",
  "Universal access": "Универсальный доступ",
  "Universal social guarantees": "Универсальные социальные гарантии",
  "Use exceptional restrictions to support security. Repression can increase resentment and weaken research.": "Используйте исключительные ограничения для поддержки безопасности. Репрессия может увеличить негодование и ослабить исследования.",
  "Use up to {0} of available cash to repay principal.": "Используйте до {0} доступных денег для погашения основного долга.",
  "Vepsian": "Вепсский",
  "WHY THIS COURSE?": "ЗАЧЕМ ЭТОТ КУРС?",
  "War exhaustion makes reconstruction and reconciliation more important than another escalation.": "Утомление от войны делает восстановление и примирение важнее новой эскалации.",
  "Welsh self-government campaign": "Кампания за самоуправление Уэльса",
  "Western Roman Empire": "Западная Римская империя",
  "Laws, living conditions and great events shape it over the years.": "Законы, условия жизни и grandes события его годами.",
  "regions ready for a vote": "регионы готовы к голосованию",
  "{0} authorises negotiated independence votes for one year.": "{0} авторизует договорённые голоса за независимость на один год.",
  "{0} days": "{0} дней",
  "{0} days · {1} per week · up to {2:P0} earned trust. First payment now; trust arrives weekly. Only your treasury pays.": "{0} дней · {1} в неделю · до {2:P0} заработанного доверия. Первый платёж сейчас; доверие приходит еженедельно. Только ваша казна платит.",
  "{0} withdraws permission for future independence votes.": "{0} отзывает разрешение на будущие голоса за независимость.",
  "{0}: {1} of all residents support independence. {2}": "{0}: {1} всех жителей поддерживают независимость. {2}"
}
"@

$content = $content -replace '\}\s*$', $tail
Set-Content $path -Value $content -Encoding UTF8
Write-Host "Done. Checking JSON validity..."
try {
    $json = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host "JSON is VALID. Keys: $($json.PSObject.Properties.Count)"
} catch {
    Write-Host "JSON ERROR: $($_.Exception.Message)"
}