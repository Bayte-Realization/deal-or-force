# Worker mod examples: a foreign language as the game's brain

Two complete workers for **protocol 1** — the door through which any language or framework can
influence the game without touching the simulation. Each reads one JSON request from stdin,
writes one JSON object to stdout and exits 0. Contract: `docs/WORKER_MOD_PROTOCOL_RU.md`.

| Folder | Language | Dependencies |
|---|---|---|
| `worker-go/` | Go | none (standard library only) |
| `worker-rust/` | Rust | `serde`, `serde_json` |

## Build

```powershell
# Go
cd worker-go
go build -o dof-worker.exe .

# Rust
cd worker-rust
cargo build --release        # target\release\worker-rust.exe
```

## Wire it into a mod

A mod is a folder with `manifest.json`; the only fields that matter here are the worker's:

```json
{
  "name": "My Mod Brain",
  "worker_executable": "dof-worker.exe",
  "worker_timeout_ms": 500,
  "worker_interval_days": 7
}
```

Copy the built executable next to the manifest. The game runs it on load, on campaign creation,
and then once every `worker_interval_days` game days.

## Try it without the game

```powershell
'{"protocol":1,"event":"day_advanced","day":10,"seed":1,"nations":[
   {"tag":"FRA","treasury":12.4,"public_debt":210.5},
   {"tag":"ESP","treasury":3.0,"public_debt":42.0}]}' | .\dof-worker.exe
```

Expected answer:

```json
{"protocol":1,"logs":["repaying a token amount of FRA's debt"],
 "commands":[{"type":"manage_debt","issuer":"FRA","action":"repay","amount":0.05}]}
```

## The three things that break a worker

1. **Anything on stdout besides the answer** — a debug print, a warning, a banner. The response
   is parsed whole; one stray character disables the worker for the rest of the campaign.
2. **Taking longer than `worker_timeout_ms`** (500 ms by default, one frame). Put diagnostics in
   stderr — it is read, bounded to 16 KB, and only reported when something fails.
3. **Remembering something between days.** A worker is started fresh per call; state that must
   persist belongs in the world (laws, cooldowns, national course), or two players stop
   agreeing about the same campaign.
