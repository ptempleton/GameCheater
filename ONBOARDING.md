# GameCheater — onboarding / session handoff

Pick this up in a fresh Claude Code session (e.g. on the Windows machine). Everything the
project needs is in this repo; there is no session state to transfer.

## What this is

A single-player game trainer platform (C# / .NET 10): pick a game, attach to it, toggle
cheats. Two cheat sources feed one runtime:
1. **Authored cheats** — our own format (bounded, fully ours).
2. **Bring-your-own `.CT` tables** — the user downloads Cheat Engine tables themselves; we
   load them. We never rehost/redistribute tables or trainer binaries.

**Hard rules (see `CLAUDE.md`):** single-player/offline only; never merge to `main` without
owner (ptempleton) approval — work on branches, open PRs; lint gate is `dotnet format`
(NOT ruff — this is C#); never commit `.CT` files or trainer binaries.

## Architecture (all in `src/GameCheater.Core`)

1. **Memory** (`Memory/ProcessMemory`, `Native/Win32`) — attach, typed read/write, module
   enum, page protection. Only code that touches Win32.
2. **Resolution** (`Memory/Signature`, `PointerChain`, `Resolve`) — AOB scans + pointer
   chains, re-resolved at enable time (ASLR-safe).
3. **Runtime** (`Cheats/Cheat`, `FreezeCheat`, `PatchCheat`, `Trainer`) — toggleable cheats
   with enable/disable/restore, a freeze loop, clean teardown; `INotifyPropertyChanged` for UI.
4. **Scanning** (`Scanning/ValueScanner`) — Cheat-Engine-style value scanner (find cheats live).
5. **Tables** (`Tables/CtParser`, `CtAddress`, `CtLoader`) — parse a `.CT`, classify each
   entry (value/pointer → our engine; Lua/AA script → CE backend), convert value/pointer
   entries into runnable `FreezeCheat`s.

UI: `src/GameCheater.App` (Avalonia — cross-platform, runs on macOS and Windows).
Console harness: `src/GameCheater.Demo` (`--ct <path>` prints a table's routing report).

## Status — DONE

- Full runtime, resolvers, value scanner, `.CT` parser + classifier + value/pointer→cheat conversion
- Avalonia UI shell: game picker, Start/Stop engine, checkbox cheat list, editable value boxes, Load .CT
- Docs: `docs/TABLE-SOURCES.md` (where to download tables), `docs/SCAN-RECIPES.md` (per-game scan how-to)
- CI (build + `dotnet format`) and Release (self-contained win-x64 on `v*` tag) workflows
- Merged through PR #5. Milestones v0–v4 track the roadmap.

## Status — NEXT (the reason to be on Windows)

**Milestone v4 / task #9 — the Cheat Engine backend.** This is the primary "use others'
tables" path, because tables for the target games are almost all Lua/Auto-Assembler scripted
(a static-only loader can't run them). Plan:
1. **Detect** an installed Cheat Engine (registry / Program Files).
2. **Launch** it with a user-supplied `.CT` and auto-attach to the game.
3. **Bridge** enable/disable of its memory records to our UI via an autorun Lua script +
   local IPC (named pipe / socket), so our checkboxes drive CE's records.
4. We already parse the table to render the cheat list; CE executes the Lua/AA natively.

This needs Windows + Cheat Engine installed + a game running to build against and verify.

Also open / planned:
- **Trainer storage / library** — a per-user app-data store (Windows: `%AppData%/GameCheater/`)
  holding authored trainer definitions (`trainers/*.json`), references + metadata for the
  user's own downloaded `.CT` tables (`tables/`), and per-game profiles. A library manager
  indexes them per game and feeds the picker. (We store the user's OWN files; we never bundle
  or redistribute others' tables/binaries.)
- **Packaging / installer** — currently portable (self-contained `win-x64` zip from the
  release workflow). A proper installer (Inno Setup or MSIX) for Start-Menu shortcut,
  app-data init, and admin elevation is later polish. Near-term: set the app manifest's
  `requestedExecutionLevel` to `requireAdministrator` so it elevates to attach to games.
- Task #10 done (static CT entries convert); pointer **offset ordering** in `CtParser`
  (we reverse CE's XML order) is flagged to spot-check against a couple of real tables.

## Build / run (Windows)

```bash
git clone https://github.com/ptempleton/GameCheater
cd GameCheater
dotnet build -c Release
dotnet format --verify-no-changes            # lint gate
dotnet run --project src/GameCheater.App     # the UI — run as Administrator to attach to games
```

Target games (all single-player-safe): SnowRunner, Palworld, No Man's Sky, Enshrouded,
The Riftbreaker, Soulmask (EAC — offline only), Subnautica 2, Avatar: Frontiers of Pandora
(Denuvo), Hogwarts Legacy (Denuvo, SP only), Everwind, StarRupture, Windrose (EAC — offline).

## Note on trainer `.dll` files

Compiled native trainer DLLs are **not** a supported input — they're closed code, not `.CT`
data, have no common interface to drive from our UI, and are untrusted to inject. Use `.CT`
tables or the scanner instead. Never commit them (rule #3).
