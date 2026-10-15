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

## Что каждый требует

- **Node.js 18+** в PATH (уже есть — `godot-mcp-toolkit` работает через `npx`).
- Все серверы запускаются через `cmd /c npx ...` (Windows-форма).
- `godot-coding-solo` берёт движок из `GODOT_PATH` (уже прописан путь к 4.8.dev7 mono).
- `context7` и `memory` — официальные MCP-серверы, ключей не требуют.

## Про аддон `godot_mcp_toolkit` (важно)

`addons/godot_mcp_toolkit/ui/mcp_json_sync.gd` владеет `.mcp.json`:
- читает env **только** для своего ключа `godot-mcp-toolkit`;
- пишет файл **только** по явному действию в доке/Tools и с подтверждением;
- сохраняет твои `GODOT_MCP_*` env при перезаписи.

Поэтому другие серверы в `.mcp.json` безопасны — аддон их не удаляет.

## Проверено

- Строгий `ConvertFrom-Json`: `godot-mcp-toolkit, godot-coding-solo, context7, memory` — парсится без ошибок.
- `godot-mcp-toolkit` env (`GODOT_MCP_CONFIG_VERSION=1`) читается аддоном.
- Папка `.fennara/memory/` создана для файла памяти.

## Если что-то не подхватилось

1. Перезапусти окно Cline (MCP поднимается на старте).
2. Проверь Node.js: `node --version` (нужен 18+).
3. Первый `npx` качает пакет — первый старт сервера может быть медленнее.
4. Логи сервера — в панели MCP Cline (иконка «MCP Servers» внизу).
5. Если `godot-coding-solo` не находит Godot — проверь `GODOT_PATH` в `.mcp.json`.

## Откат

`git checkout .mcp.json` или верни из `.mcp.json.bak`.
