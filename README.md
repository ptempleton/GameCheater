# GameCheater

A single-player trainer platform for PC games: one app to pick a game, attach to it, and
toggle cheats on/off — built on your **own** cheat runtime, with a **bring-your-own-table**
loader so it works with tables you supply rather than redistributing anyone's work.

> **Scope:** single-player / offline games only. GameCheater never writes memory in online
> or anti-cheat-protected sessions. See [Legal & scope](#legal--scope).

## Why this exists

Two ways to get "lots of games, one app": author the cheats yourself (bounded, fully
yours) or lean on other people's tables (broad, but stale and not yours to redistribute).
GameCheater does both cleanly by separating the **runtime** (ours) from the **content**
(yours):

- **Author your own trainers** in a simple, fully-owned format — the bounded path where
  "feature-rich" lives (categories, freeze, set-value, code patches, conflict handling).
- **Load `.CT` tables you download yourself** — like a media player opening a file you
  provide. We point you to sources; we don't bundle or rehost them.

Both feed the same runtime: every cheat — authored or loaded — becomes one toggleable
`Cheat` object (a value-freeze or a code-patch).

## Architecture

Independently-testable layers:

| Layer | Project / namespace | What it does |
|-------|--------------------|--------------|
| 1. Memory access | `Core/Memory/ProcessMemory`, `Core/Native` | Attach, read/write typed values & bytes, enumerate modules, change page protection. The only code that touches Win32. |
| 2. Address resolution | `Core/Memory/Signature`, `PointerChain`, `Resolve` | AOB signature scanning and multi-level pointer chains — how a cheat survives ASLR and patches by re-resolving at enable time. |
| 3. Cheat runtime | `Core/Cheats/Cheat`, `FreezeCheat`, `PatchCheat`, `Trainer` | The heart: toggleable cheats with enable/disable/restore, a freeze loop, and clean teardown. `INotifyPropertyChanged` so a UI binds directly. |
| 4. Discovery | `Core/Scanning/ValueScanner` + `PointerScanner`, `Core/Debugging` | Cheat-Engine-style value/pointer scanner so you *find* cheats live instead of looking them up — plus find-what-writes debuggers and struct/anti-debug tooling for hard cases. |
| 5. UI | `GameCheater.App` (Avalonia) | Game picker + Capture/Cheats tabs, a thin view over layers 3–4. Builds/previews on macOS, ships identically to Windows (WPF would be Windows-only). |

## Status

- ✅ Memory-access layer, AOB + pointer resolution, cheat runtime, value scanner + pointer scanner
- ✅ **Avalonia trainer UI** — game picker, Capture (scan) tab, Cheats tab, hotkeys, in-app Refresh
- ✅ **CT loader** — static/pointer entries convert to built-in cheats; Lua/AA entries delegate to an
  optional installed Cheat Engine (see the [CT support matrix](docs/TABLE-SOURCES.md))
- ✅ Console dev harness / RE CLI (`GameCheater.Cli`): scanner, find-what-writes, pointer scan, etc.
- ✅ Per-game scan recipes ([`docs/SCAN-RECIPES.md`](docs/SCAN-RECIPES.md))
- 🔶 Distribution: authored definitions pulled from the `GameCheater-cheats` repo via Refresh

## Build & run

```bash
dotnet run --project src/GameCheater.App       # the trainer UI (run as Administrator on Windows)
.\publish.cmd                                  # -> publish\GameCheater.exe (single-file, win-x64)

dotnet build -c Release                        # compiles on any OS
dotnet format --verify-no-changes              # lint gate (must pass to merge)
dotnet run --project src/GameCheater.Demo -- --selftest   # RE CLI (only attaches on Windows)
```

The core targets `net10.0` so it compiles on macOS/Linux for development; the Win32 memory
APIs only resolve at runtime on **Windows**, which is where a trainer actually runs (as
Administrator).

## Target games

All chosen titles are single-player / co-op survival, sim, and adventure games — the genre
that lives *outside* the kernel-anti-cheat world, so solo memory editing is viable. Used
**strictly offline** this is low-risk, but it is **never risk-free**: several of these games have
online/co-op modes and anti-cheat, and memory editing can violate a game's terms of service. Only
edit in single-player/offline sessions, and never against an EAC/BattlEye-protected session (see
the per-game notes below and the SP-only flags).

| Game | Anti-cheat | Notes |
|------|-----------|-------|
| SnowRunner | none | Mature, stable — the v0 target. |
| The Riftbreaker | none | Mature, stable. |
| No Man's Sky | none | Mature; back up saves before big inventory edits. |
| Enshrouded | none | Early access — addresses churn on patches. |
| Palworld | none | Released 1.0; actively updated. |
| Soulmask | EAC (online only) | **Solo/offline only** — never touch online sessions. |
| Subnautica 2 | none confirmed | Early access; solo/private sessions only. |
| Everwind / StarRupture / Windrose | none/EAC (Windrose) | Early access; unstable addresses. Windrose: offline only. |
| Avatar: Frontiers of Pandora | Denuvo (anti-tamper) | SP only; Denuvo adds friction, no ban. |
| Hogwarts Legacy | Denuvo (anti-tamper) | SP only; online has anti-cheat — offline only. |

### A note on CT tables

Existing tables for these specific games are **overwhelmingly Lua-scripted / AOB-based, not static
pointer lists.** GameCheater's loader converts the **static/pointer** entries to built-in cheats
and runs them itself; **Lua/AA** entries are classified and delegated to an **optional installed
Cheat Engine** backend (there's no embedded Lua/AA interpreter). Because most real tables are
Lua-heavy, the **authored-trainer path is the primary product**. See the full
[CT support matrix and per-game sources](docs/TABLE-SOURCES.md).

Only **FearLess Revolution** (and occasionally GuidedHacking / Nexus) distribute actual `.CT`
files; FLiNG / Cheat Happens / PLITCH ship closed `.exe` trainers that a table loader can't open.

## Legal & scope

- **Single-player / offline only.** No online or anti-cheat-protected memory writes.
- **We don't redistribute content.** GameCheater ships the runtime and loader; you supply
  any `.CT` tables. Table authors retain rights to their work — we link to source threads,
  we do not rehost or bundle their files. `.CT` files are git-ignored.
- Use on games and in modes where it's permitted; respect each game's terms.

## Contributing

`main` is protected — **no merges without owner approval**, all work via PRs, and
`dotnet format --verify-no-changes` must pass. See [CLAUDE.md](CLAUDE.md).
