# GameCheater — onboarding / handoff

Pick this up in a fresh session (on the **Windows** machine, in an **Administrator** terminal to
attach to games). The repo is the source of truth. Read `CLAUDE.md` (or `AGENTS.md` for Codex) too.

This document describes the **current** state only. For the deep, blow-by-blow SnowRunner
No-Damage investigation, see **`docs/NO-DAMAGE-FINDINGS.md`**.

## What this is

A **self-contained** single-player game trainer (C# / .NET 10, Avalonia UI): pick a game, attach,
toggle cheats. The owner wants it **standalone — do NOT require installing external tools** (e.g.
Cheat Engine) for the primary path. Cheats come from our own runtime (value freezes + code
patches) and, optionally, `.CT` tables the user supplies. Public repo:
`github.com/ptempleton/GameCheater`.

**Hard rules (`CLAUDE.md`):** single-player/offline only; branch + PR, CI must pass, never merge to
`main` without owner (ptempleton) approval; lint gate is `dotnet format`; never commit `.CT` files
or trainer binaries. Branch protection is on (admin-enforcement relaxed, so `gh pr merge` works
once CI is green).

## What's built (on `main`, CI green)

- **Engine** (`Core/Memory`, `Core/Cheats`): `ProcessMemory` (attach, typed R/W, regions, page
  protection), `Resolve` (AOB / pointer chains / static / absolute), `FreezeCheat`, `PatchCheat`,
  `Trainer` (freeze loop, teardown).
- **Scanner** (`Core/Scanning`): `ValueScanner` — writable-only, snapshot-based unknown scans,
  exact, and range scans. `PointerScanner` (durable pointer chains). `ValueScanSession` facade.
- **Avalonia app** (implemented, not future work): **Capture tab** (first/unknown/range scan,
  +/-/~/= narrow, candidate list, Test Freeze, Copy address, Export candidates, Save as cheat);
  **Cheats tab** (filter chips, groups, On/Off toggles, F1–F12 global hotkeys, sliders, Disable-All
  panic button); game picker with in-app **Refresh** from the `GameCheater-cheats` repo.
  Run: `dotnet run --project src/GameCheater.App` (as Administrator) or `.\publish.cmd`.
- **Hotkeys** (`App/Services/HotkeyManager`): Win32 `RegisterHotKey` on a message thread —
  Windows-only, **still needs live in-game validation** (assign F1, toggle in-game).
- **Distribution**: `CheatRepositoryClient` pulls authored defs from `GameCheater-cheats`.
- **Tables** (`Core/Tables`, `Core/Backend`): see the CT support matrix in `docs/TABLE-SOURCES.md`.
  Static/pointer entries convert to built-in cheats; Lua/AA entries are classified and delegated
  to an **optional installed Cheat Engine** backend; there is no embedded Lua/AA interpreter.
- **Debugger / RE tooling** (`Core/Debugging`): find-what-writes + anti-debug + supporting scan
  tools — see the CLI list below.

## Cheat status tracker

Keep "solved / published / in the app" separate — they're different milestones:

| Cheat | Solved (works live) | Published to cheats repo | In the app |
|---|---|---|---|
| SnowRunner Infinite Fuel | ✅ value freeze + pointer chain | ✅ `games/snowrunner.json` (loads via Refresh) | ✅ via Refresh — the embedded `GameCatalog` entry is still a **placeholder** (`GameCatalog.BuildSnowRunner`), a separate cleanup |
| SnowRunner No Damage | ❌ not cracked (see findings doc) | — | — |

**Infinite Fuel** is a plain value freeze on the durable chain
`SnowRunner.exe+0x2AA17F0 → +0x28 → +0x5E8` (final offset = fuel field in the vehicle struct),
`resolveEachTick` so it follows the relocating struct. Found via the fuel workflow: **range-scan
the float → `--freeze` to confirm it holds the gauge → `--pointer-scan`/`--pointer-verify` across a
relaunch for the durable chain.** It's published and works in the app after Refresh; only the
offline embedded-catalog default is still a placeholder.

## SnowRunner anti-tamper (tested live; Steam, no EAC/BattlEye)

Memory read/write is **completely unguarded** — the scanner, `--poll`, and value freezes never trip
anything. Only *debugger* operations do. Three layers:

1. **PEB attach-detection — BEATEN.** A bare `DebugActiveProcess` self-exits the game (it polls
   `PEB.BeingDebugged`). `Core/Debugging/AntiDebug` clears it on attach + every loop
   (`WriteWatch.clearPebDebugFlags`, default on). The game then survives an attach (verified 20s+).
2. **Hardware-breakpoint detection — NOT beaten.** `--find-writes` (HW breakpoint) self-exits the
   game even with PEB cleared — it reads its own Dr0–Dr7 and exits if any are set. Do NOT use
   `WriteWatch(periodicReArm: true)` here either (thread-suspension reads as tamper).
3. **Page-protection detection — NOT beaten.** `--find-writes-guard` (page-guard, uses no debug
   registers) also self-exits the game (3/3, 0 faults). Works fine on unprotected games;
   SnowRunner appears to detect the page-protection change too.

Both driver-free find-what-writes paths are therefore blocked on SnowRunner. `Core/Debugging/
DebugRegisterHider.cs` (WIP, compiles, untested) is the intended unlock — see the findings doc.

## Debugger / scan CLI (`GameCheater.Cli`, all Administrator, single-player only)

- `--find-writes <pid|proc> <hexAddr> [size] [game]` — HW-breakpoint find-what-writes (detected on
  SnowRunner; works on unprotected games). Live NOP/restore, durable-AOB save.
- `--find-writes-guard <pid|proc> <hexAddr> [size]` — page-guard version (also detected on
  SnowRunner). Dumps writer registers with `r <n>`.
- `--struct-find <pid|proc> <moduleOffset> <derefsCsv> <value> [structWin] [subWin] [--tol <t>]` —
  search the vehicle struct + sub-objects for a value (exact, or `--tol` for fraction matching).
- `--struct-diff` / `--vehicle-scan` — churn-filtered field diff on an in-game event.
- `--freeze-chain <pid|proc> <moduleOffset> <offsetsCsv> <type> <value> [secs]` — freeze via a
  pointer chain re-resolved each tick (follows the relocating struct).
- `--freeze <pid|proc> <hexAddr> <float|double|int|long> <value> [secs]` — value-freeze test.
- `--poll <pid|proc> <hexAddr> [size] [secs]` — read-only value sampler; no attach, safe.
- `--pointer-scan <pid|proc> <hexAddr> [maxDepth=5] [maxOffset=0x800]` / `--pointer-verify …` —
  durable pointer chains (scan, then verify survivors across a relaunch).
- `--bisect <pid|proc> <candidatesFile> <start|stopped|dropping>` — binary-search a candidate
  cluster by freezing halves (pair with the app's **Export** button).
- `--anti-debug-test <pid|proc> [secs] [--no-clear]` — is the anti-debug user-mode or kernel-side?
- `--write-target [secs]` — self-test process (known address + writers) for the debugger tools.
- `--selftest` — x86-64 length-decoder reference tests (no game needed; run after touching
  `X64Decoder`).
- Table/definition helpers: `--ct <path>`, `--load-json <path>`, `--pull`, plus the older
  `--watch-values` / `--watch-code` capture loops and `--ce` CE backend.

## Next steps

1. **SnowRunner No Damage** — the open milestone. Read `docs/NO-DAMAGE-FINDINGS.md`; the
   highest-leverage thread is finishing `DebugRegisterHider` so `find-writes` survives.
2. **Replace the embedded SnowRunner placeholder** in `GameCatalog.BuildSnowRunner` with the real
   fuel chain, so the app has a working fuel cheat offline (not only via Refresh).
3. **Wire find-writes into the Capture tab UI** (a "Find what writes" button → writer list →
   NOP/Save). `WriteWatch.WriterDiscovered` fires on the debug thread while the game is frozen — the
   UI must marshal and do nothing expensive there.
4. **Hotkey live validation** — assign F1, toggle in-game.

## Build / run

```powershell
git clone https://github.com/ptempleton/GameCheater ; cd GameCheater
dotnet run --project src/GameCheater.App     # the trainer UI; run as Administrator to attach
.\publish.cmd                                # -> publish\GameCheater.exe (single file)
dotnet build -c Release -warnaserror         # CI parity
dotnet format --verify-no-changes            # lint gate (CI enforces this)
dotnet run --project src/GameCheater.Demo -- --selftest   # x86-64 decoder reference tests
```

**Lint gate on a Windows checkout:** `.editorconfig` sets `end_of_line = lf`, but git's
`core.autocrlf=true` gives a CRLF working tree, so `dotnet format --verify-no-changes` reports
`ENDOFLINE` on every file locally. CI (Ubuntu, LF checkout) is unaffected. To see only real
findings, filter out that noise — in **Git Bash / WSL**: `dotnet format --verify-no-changes 2>&1 |
grep -v ENDOFLINE`; in **PowerShell**: `dotnet format --verify-no-changes 2>&1 | Select-String -NotMatch ENDOFLINE`.

## Target games

All single-player-safe: SnowRunner, Palworld, No Man's Sky, Enshrouded, The Riftbreaker, Soulmask
(EAC — offline only), Subnautica 2, Avatar (Denuvo), Hogwarts Legacy (Denuvo, SP only), Everwind,
StarRupture, Windrose (EAC — offline).

## Note on trainer `.dll` files

Native trainer DLLs are **not** a supported input (closed code, no common interface, untrusted to
inject). Use the scanner / code patches / `.CT` tables. Never commit them.
