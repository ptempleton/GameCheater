# GameCheater — session handoff / onboarding

Pick this up in a fresh Claude Code session (e.g. on the **Windows** machine, run in an
**Administrator PowerShell** — not ISE). The repo is the source of truth; there's no session
state to transfer. Read `CLAUDE.md` too.

## What this is

A **self-contained** single-player game trainer (C# / .NET 10, Avalonia UI): pick a game,
attach, toggle cheats. The owner wants it **standalone — do NOT require installing external
tools** (e.g. Cheat Engine) to use it. Cheats come from our own runtime (value freezes +
code patches) and, optionally, `.CT` tables the user supplies. Public repo:
`github.com/ptempleton/GameCheater`.

**Hard rules (`CLAUDE.md`):** single-player/offline only; branch + PR, CI must pass, never
merge to `main` without owner (ptempleton) approval; lint gate is `dotnet format`; never
commit `.CT` files or trainer binaries. Branch protection is on (admin-enforcement relaxed,
so `gh pr merge` works once CI is green).

## What's built (all merged to main, CI green)

- **Engine** (`Core/Memory`, `Core/Cheats`): ProcessMemory (attach, typed R/W, regions,
  page protection), `Resolve` (AOB signatures / pointer chains / static / absolute),
  `FreezeCheat` (value freeze, incl. Min/Max for sliders), `PatchCheat` (byte patch +
  restore), `Trainer` (freeze loop, teardown, Detach).
- **Scanner** (`Core/Scanning`): `ValueScanner` — **writable-only** scanning, snapshot-based
  **unknown** scans, exact, and **range** scans (`FirstScanBetween`/`NextScanBetween`).
  `MemorySnapshot` + `Oracle` (code-diff). `ValueScanSession` = non-generic facade for the UI.
- **Capture tab** (Avalonia): first/unknown/range scan, +/-/~/= narrow, candidate list,
  **Test Freeze** (holds current, or set a value), **Unfreeze**, **Save as cheat** →
  `%AppData%/GameCheater/captured/<game>.json`.
- **Cheats tab**: game header, category **filter chips**, collapsible groups, **On/Off
  toggles**, **hotkey dropdown (F1–F12, global)**, **slider** for ranged value cheats,
  top-bar **Disable All** panic button + attach status.
- **Hotkeys** (`App/Services/HotkeyManager`): Win32 `RegisterHotKey` on a message thread —
  Windows-only, **needs live validation** (assign F1, toggle in-game).
- **Distribution**: `CheatRepositoryClient` pulls authored defs from the public
  `GameCheater-cheats` repo; in-app **Refresh**. Definition format in `Core/Definitions`.
- **Tables**: `.CT` parser/classifier/loader (static+pointer → cheats; Lua/AA → CE backend).
- **CE backend** (`Core/Backend`): exists, but the owner prefers **not** to require Cheat
  Engine — treat as a fallback, not the primary path.
- **Client**: the App builds to `GameCheater.exe` (WinExe). `.\publish.cmd` → single exe;
  or `dotnet run --project src/GameCheater.App` (run as admin to attach). Dev CLI
  (`GameCheater.Cli`, the Demo project) has `--watch-code/--watch-values/--ce/--pull/--load-json/--ct`.

## Key finding — why the next task exists

**SnowRunner fuel is NOT value-freezable.** The gauge value is recomputed every frame from an
internal source; no writable float drives it (confirmed live: the scanner narrowed to a value
that tracks the gauge, but freezing/setting it does nothing to the gauge — it's a mirror).
SnowRunner's real cheats are **code-based** (its community tables are AOB/Lua). Value scanning
works fine for freezable values (money/counts/etc.), but **continuous consumables like fuel
need a CODE PATCH** — stop the consumption instruction.

## NEXT: build the "find what writes to an address" debugger (self-contained code patches)

Goal: make SnowRunner-class code cheats work without external tools.

Design:
1. Attach as a debugger: `DebugActiveProcess(pid)` + a dedicated thread running
   `WaitForDebugEvent` / `ContinueDebugEvent`.
2. Set a **hardware breakpoint** (debug registers Dr0–3, Dr7 = write, length) on the target
   address, on every thread (`GetThreadContext`/`SetThreadContext`).
3. On the write hit (`EXCEPTION_SINGLE_STEP`), read the thread's `Rip` = the instruction that
   wrote the value = the consumption code.
4. Report that instruction (address, bytes, surrounding **AOB**). NOP it → no consumption; and
   emit it as a durable `PatchCheat` (AOB signature) so it survives restarts.
5. Workflow: value-scan to an address that tracks the value (even a mirror), run
   find-what-writes on it to locate the writer; for the *authoritative* value you may need to
   chain (the copy-writer's source → the real value) — work it out live against the game.

Caveats: **Windows-only**; attaching as a debugger pauses the game per event; single-player
only; detach cleanly (`DebugActiveProcessStop`) so the game doesn't die; watch for anti-debug
(SnowRunner is fine). **Build and test this ON Windows** against the live game — the Mac can
only compile it, which makes iteration painfully slow.

## Build / run

```powershell
git clone https://github.com/ptempleton/GameCheater ; cd GameCheater
dotnet run --project src/GameCheater.App     # UI; run as Administrator to attach
.\publish.cmd                                # -> publish\GameCheater.exe
dotnet build -c Release -warnaserror         # CI parity
dotnet format --verify-no-changes            # lint gate
```

Target games (all single-player-safe): SnowRunner, Palworld, No Man's Sky, Enshrouded,
The Riftbreaker, Soulmask (EAC — offline only), Subnautica 2, Avatar (Denuvo), Hogwarts
Legacy (Denuvo, SP only), Everwind, StarRupture, Windrose (EAC — offline).

## Note on trainer `.dll` files

Native trainer DLLs are **not** a supported input (closed code, no common interface, untrusted
to inject). Use the scanner / code patches / `.CT` tables. Never commit them.
