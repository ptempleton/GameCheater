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

Four independently-testable layers:

| Layer | Project / namespace | What it does |
|-------|--------------------|--------------|
| 1. Memory access | `Core/Memory/ProcessMemory`, `Core/Native` | Attach, read/write typed values & bytes, enumerate modules, change page protection. The only code that touches Win32. |
| 2. Address resolution | `Core/Memory/Signature`, `PointerChain`, `Resolve` | AOB signature scanning and multi-level pointer chains — how a cheat survives ASLR and patches by re-resolving at enable time. |
| 3. Cheat runtime | `Core/Cheats/Cheat`, `FreezeCheat`, `PatchCheat`, `Trainer` | The heart: toggleable cheats with enable/disable/restore, a freeze loop, and clean teardown. `INotifyPropertyChanged` so a UI binds directly. |
| 4. Discovery | `Core/Scanning/ValueScanner`, `Core/Debugging/WriteWatch` | Cheat-Engine-style value scanner so you *find* cheats live instead of looking them up — plus a real debugger ("find what writes to this address") that traces a value back to the instruction storing it, for values that can't be frozen. |

The future UI (game picker + checkbox list + overlay) is a thin view over layer 3 —
planned in **Avalonia** (builds and previews on macOS, ships identically to Windows;
WPF would be Windows-only and unbuildable on the dev's Mac).

## Status

- ✅ Memory-access layer, AOB + pointer resolution, cheat runtime, value scanner
- ✅ Console dev harness (`GameCheater.Demo`) with an example SnowRunner definition
- ⬜ CT loader (see the [Lua reality](#a-note-on-ct-tables) below)
- ⬜ Per-game scan recipes
- ⬜ Avalonia UI

See the [milestones](../../milestones) for the phased roadmap (v0 → v4).

## Build & run

```bash
dotnet build -c Release                        # compiles on any OS
dotnet format --verify-no-changes              # lint gate (must pass to merge)
dotnet run --project src/GameCheater.Demo      # dev harness — only attaches on Windows
```

The core targets `net10.0` so it compiles on macOS/Linux for development; the Win32 memory
APIs only resolve at runtime on **Windows**, which is where a trainer actually runs (as
Administrator).

## Target games

All chosen titles are single-player / co-op survival, sim, and adventure games — the genre
that lives *outside* the kernel-anti-cheat world, so solo memory editing is viable and
carries no ban risk.

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

Research into existing tables for these specific games found that **essentially all of
them are Lua-scripted / AOB-based, not static pointer lists.** A loader that only handles
static tables would fail on nearly every real-world table here. Full `.CT` support for this
game list therefore requires embedding a Lua interpreter and emulating Cheat Engine's
auto-assembler API — an open-ended effort. This is why the **authored-trainer path is the
primary product** and CT-loading is a later, deliberately-scoped bet.

Only **FearLess Revolution** (and occasionally GuidedHacking / Nexus) distribute actual
`.CT` files; FLiNG / Cheat Happens / PLITCH ship closed `.exe` trainers that a
table loader can't open at all.

## Legal & scope

- **Single-player / offline only.** No online or anti-cheat-protected memory writes.
- **We don't redistribute content.** GameCheater ships the runtime and loader; you supply
  any `.CT` tables. Table authors retain rights to their work — we link to source threads,
  we do not rehost or bundle their files. `.CT` files are git-ignored.
- Use on games and in modes where it's permitted; respect each game's terms.

## Contributing

`main` is protected — **no merges without owner approval**, all work via PRs, and
`dotnet format --verify-no-changes` must pass. See [CLAUDE.md](CLAUDE.md).
