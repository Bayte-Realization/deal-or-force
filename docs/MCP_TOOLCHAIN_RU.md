# Тулчейн MCP для разработки (Windows)

Конфиг — `.mcp.json` в корне проекта. Бэкап исходного — `.mcp.json.bak`.
Cline читает MCP только при старте окна: **после правки перезапусти окно Cline**,
иначе новые серверы не появятся.

## Что подключено

| Сервер | Пакет | Зачем |
| --- | --- | --- |
| `godot-mcp-toolkit` | `@npgamedev/godot-mcp-server` | Исходный мост проекта: команды редактора, сцены, ресурсы, вывод. Идёт через аддон в `addons/godot_mcp_toolkit`. **Не трогаем** — плагин владеет его env-блоком. |
| `godot-coding-solo` | `@coding-solo/godot-mcp` (6.1k★, активно) | Запуск редактора/проекта, дебаг-вывод, создание сцен и нод, UID. Сам находит Godot (переопределяется через `GODOT_PATH`). |
| `context7` | `@upstash/context7-mcp` | Актуальная **версионная** документация Godot 4.8 прямо в контексте. В промпте пиши `use context7`. Без API-ключа. |
| `memory` | `@modelcontextprotocol/server-memory` | Постоянный граф знаний проекта между сессиями. Хранит в `.fennara/memory/project-memory.jsonl`. |
| `fennara` | локальный бинарь `fennara-mcp.exe` | Godot-осознанная инспекция: сцены, свойства, диагностика, сцены-скрипты, скриншоты. Требует открытого редактора с аддоном. |
| `godot-ai` | `godot-ai==4.3.0` через `uvx` | Второй редакторный мост (attach на порт 8016). Не запускать вторую копию, пока работает первая. |
| `creative-claw` | удалённый (`app.creativeclaw.co/mcp`) | Генерация медиа для записей атласа/музыки. Токен в заголовке — обновляется через `Tools/creative_claw_auth.mjs`. |
| `deal-or-force-dev` | `Tools/game-mcp/server.mjs` | Собственный сервер цикла разработки: `build_game`, `run_tests`, `game_verify`, `capture_screenshots`, `list_technologies`, `localization_audit`. Зависимостей нет. |
| `deal-or-force-knowledge` | `Tools/knowledge-mcp/server.mjs` | Собственный сервер памяти репозитория: `search_docs`, `plan_status`, `list_defines`, `backlog_ideas`, `recent_work`, `data_stats`. Зависимостей нет. |
| `ziva-godot` | `zivacode.exe --mcp-bridge` | Ziva-агент Godot (ported из конфига Devin). Мост на `localhost:7012`; бинарь Godot указан 4.8-dev7. |
| `blender` | `uvx mcp-for-blender` | Blender-мост (ported из Claude Code/VS Code/OpenCode/Devin). Телеметрия выключена env-ом. Для арт-конвейера `art/`. |
| `davinci-resolve` | `ResolveMCP.exe` | DaVinci Resolve (ported из Claude Code/OpenCode/Devin). Для VIDEO-01 и медиа-авторинга. |
| `playwright` | `@playwright/mcp@latest` | Автоматизация браузера (ported из OpenCode). Читаемым веб-источникам и проверкам страниц. |
| `gitkraken` | `@gitkraken/gk mcp` | Git-сервер GitKraken (ported из OpenCode). Полезен, когда нужен граф/PR поверх git CLI. |
| `unity-mcp` | `.unity/relay/relay_win.exe --mcp` | Релей Unity. **Выключен** (`disabled: true`) — это Godot-проект; строка сохранена, чтобы конфиг был полным портретом машины. |

## Портирование между харнессами (16 октября 2026)

На машине MCP настроен в ~11 харнессах: Claude Code, Claude Desktop, Cursor, Windsurf, VS Code,
Gemini CLI, OpenCode, Codex CLI, Qwen, Devin, Cline/ZCode. Сверка показала:

- **В проекте не хватало** `ziva-godot`, `blender`, `davinci-resolve` (они были в конфиге Devin),
  `playwright` и `gitkraken` (были только в OpenCode), `unity-mcp` (Claude Code/OpenCode) —
  все перенесены в `.mcp.json` выше.
- **Обратно**: в конфиг Devin (`.devin/mcp_config.local.json`) добавлены собственные серверы
  проекта `deal-or-force-dev`, `deal-or-force-knowledge`, а также `context7` и `memory`;
  в `.devin/config.local.json` добавлены соответствующие права.
- **Скиллы**: в `.devin/skills/` не хватало четырёх скиллов (`dof-balance-ab`,
  `dof-mechanic-block`, `dof-plan-sync`, `dof-verify`) — перенесены скриптом
  `Tools/harness-sync.mjs`. Скрипт переносит **отсутствующие** скиллы целиком, а различающиеся
  копии только сообщает и не трогает: различия намеренные (адаптации под харнесс, например
  fennara-godot в `.agents` — версия с ленивыми инструментами Antigravity).

### Инвентаризация вместо памяти — `Tools/mcp-survey.mjs`

Сверку выше делали вручную и один раз. `Tools/mcp-survey.mjs` делает её постоянной: читает
все конфигурации **только на чтение** и печатает матрицу «сервер × харнесс» (отдельно помечая
выключенные), список конфигураций, различающихся между харнессами, и такую же матрицу для
скиллов. `--emit <харнесс>` печатает готовый к вставке JSON для серверов, которых у него нет,
`--json` отдаёт матрицу скрипту. `--apply` намеренно отсутствует: конфиг вне проекта
принадлежит его владельцу.

Прогон 16 октября 2026: **23 различных сервера, 11 представлений харнессов**. `fennara` —
единственный сервер, который есть во всех одиннадцати. Собственные серверы проекта
(`deal-or-force-dev`, `deal-or-force-knowledge`) и `context7`, `memory`, `godot-coding-solo`,
`godot-mcp-toolkit`, `playwright`, `gitkraken`, `davinci-resolve`, `blender`, `ziva-godot`
присутствуют только в проектных конфигах (`.mcp.json`, `.devin/mcp_config.local.json`) —
в Cursor, Windsurf, Gemini/Antigravity, Kiro, Codex CLI и Claude Code их нет.

Три находки, которые инструмент показал, а руками никто не видел:

- **Скиллы проекта жили вне проекта.** `deal-or-force-architecture` и
  `deal-or-force-bta-crossover` существовали только в `~/.config/opencode/skills`. Причина в
  самом `harness-sync.mjs`: он синхронизирует `.agents` → `.cursor`/`.devin` и не знает про
  пользовательские каталоги скиллов, поэтому написанное туда для него не существует. Оба
  перенесены в `.agents/skills` и размножены; порядок тиков в `deal-or-force-architecture`
  переписан по `SimulationCore.TickStages`, потому что копия в OpenCode перечисляла
  идеализированные пятнадцать шагов, которым код не следует.
- **В конфиге OpenCode серверы вложены в `mcp.servers`** при плоской схеме `mcp.<имя>`, и все
  помечены `disabled: true`. Похоже на блок, вставленный из чужого формата. **Не правилось**:
  нужна сверка со схемой, а не догадка, и файл лежит вне проекта.
- **`creative-claw` включён, но не подключается.** В `.mcp.json` у него `disabled: false`,
  однако из 14 включённых серверов в живую сессию Qwen поднялись 13. Вероятная причина —
  истёкший токен (`Tools/creative_claw_auth.mjs`). Не проверялось: это внешний сервис.


## Что каждый требует

- **Node.js 18+** в PATH (уже есть — `godot-mcp-toolkit` работает через `npx`).
- npm-серверы запускаются через `cmd /c npx ...` (Windows-форма).
- `godot-coding-solo` берёт движок из `GODOT_PATH` (уже прописан путь к 4.8.dev7 mono).
- `godot-ai` и `fennara` требуют открытого редактора Godot с этим проектом.
- `context7` и `memory` — официальные MCP-серверы, ключей не требуют.
- Первый `npx` качает пакет — первый старт сервера может быть медленнее.

## Про аддон `godot_mcp_toolkit` (важно)

`addons/godot_mcp_toolkit/ui/mcp_json_sync.gd` владеет `.mcp.json`:
- читает env **только** для своего ключа `godot-mcp-toolkit`;
- пишет файл **только** по явному действию в доке/Tools и с подтверждением;
- сохраняет твои `GODOT_MCP_*` env при перезаписи.

Поэтому другие серверы в `.mcp.json` безопасны — аддон их не удаляет.

## Проверено

- Строгий `ConvertFrom-Json`: все 15 серверов в `.mcp.json` и 10 в `.devin/mcp_config.local.json`
  парсятся без ошибок (проверено node 16 октября 2026).
- `godot-mcp-toolkit` env (`GODOT_MCP_CONFIG_VERSION=1`) читается аддоном.
- `deal-or-force-knowledge` проверен живым прогоном stdio: initialize, tools/list (6 инструментов),
  `plan_status` (81 строка плана), `list_defines`, `data_stats`, `backlog_ideas`.
- Папка `.fennara/memory/` создана для файла памяти.

## Если что-то не подхватилось

1. Перезапусти окно Cline (MCP поднимается на старте). ZCode — так же: перезапуск окна клиента.
2. Проверь Node.js: `node --version` (нужен 18+).
3. Первый `npx` качает пакет — первый старт сервера может быть медленнее.
4. Логи сервера — в панели MCP Cline (иконка «MCP Servers» внизу).
5. Если `godot-coding-solo` не находит Godot — проверь `GODOT_PATH` в `.mcp.json`.
6. `unity-mcp` выключен сознательно; включать, только если начнёшь Unity-проект.

## Откат

`git checkout .mcp.json` или верни из `.mcp.json.bak`. Конфиги Devin — то же самое в
`.devin/`.
