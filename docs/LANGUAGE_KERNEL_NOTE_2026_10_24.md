# Языковые ядра — рекомендация 24.10.2026

Краткий вывод для вопроса «переписать на Rust/F#/Odin и получить 5×».

**Первое извлекаемое ядро:** ни одного native-ядра в симуляцию — **пока нет профиля**, где горячий цикл остаётся горячим после чистого алгоритма в C#. Ownership BFS (Rust/Odin) уже проиграл медиану C# и hook снят. F# для assault readiness уже в игре; это не ускорение, а граница правил + 624502 oracle.

**Порядок, если появится число:** ещё мелкие чистые правила → F# (in-process, тот же fingerprint/fixture); плотный bulk numeric с C ABI → Rust или Odin только после A/B на карте с marshaling в p50/p95; Luau — модовые формулы на копии; Nim/Kotlin — инструменты, не тик.

**Не делать:** blanket rewrite, Godot fork, process-worker для тика (синхронный хук до 5000 ms), Variant/JNI на провинцию/армию.

Источники: `docs/LANGUAGE_KERNEL_EVALUATION_2026_10_16_RU.md`, `docs/LANGUAGE_BACKENDS_RU.md`, `docs/FSHARP_CORE_REFACTOR_2026_10_18_RU.md`, `docs/PERFORMANCE_AND_SAVE_SIZE_2026_10_18_RU.md`, `LanguageKit/README.md`, `Scripts/Simulation/WarScoring.cs`, `Scripts/gfx/GlobePresentation.cs`, `Scripts/ui/SaveLoadUI.cs`.
