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
- **Debugger** (`Core/Debugging`): find-what-writes — see below.
- **Client**: the App builds to `GameCheater.exe` (WinExe). `.\publish.cmd` → single exe;
  or `dotnet run --project src/GameCheater.App` (run as admin to attach). Dev CLI
  (`GameCheater.Cli`, the Demo project) has `--watch-code/--watch-values/--find-writes/`
  `--write-target/--selftest/--ce/--pull/--load-json/--ct`.

## Key finding that drove the debugger

**SnowRunner fuel is NOT value-freezable.** The gauge value is recomputed every frame from an
internal source; no writable float drives it (confirmed live: the scanner narrowed to a value
that tracks the gauge, but freezing/setting it does nothing to the gauge — it's a mirror).
SnowRunner's real cheats are **code-based** (its community tables are AOB/Lua). Value scanning
works fine for freezable values (money/counts/etc.), but **continuous consumables like fuel
need a CODE PATCH** — stop the consumption instruction.

## DONE: the "find what writes to an address" debugger (`Core/Debugging`)

Self-contained; no Cheat Engine. `.\find-writes.cmd <process|pid> <hexAddress> [size] [game]`
(Administrator). It attaches with `DebugActiveProcess`, runs a `WaitForDebugEvent` loop on a
dedicated thread, and arms a **hardware write breakpoint** (Dr0–3 / Dr7) on **every** thread —
including ones the game spawns later, since debug registers are per-thread. On each trap it
reads `Rip`, decodes backwards to the storing instruction, and aggregates by writer with hit
counts. In-session you can NOP a writer live (`n`), restore it (`r`), preview the durable AOB
(`p`), or save it as a `patch` cheat (`s`) straight into a `games/<game>.json`.

Pieces worth knowing:

- `X64Decoder` — a minimal x86-64 **length** decoder (prefixes, REX, VEX/EVEX, all three
  opcode maps, ModRM/SIB/disp/imm). It exists because a data breakpoint is a *trap*: the
  reported `Rip` is the instruction *after* the store, and x86 can't be decoded backwards.
  `FindWriterEndingAt` brute-forces it — decode forward from every earlier byte, keep the
  chains that land exactly on `Rip`, take the one most chains agree on.
  **`GameCheater.Cli --selftest` checks it against 49 reference encodings + 5 backward cases.
  Run it after touching the decoder** — a one-byte length error NOPs into the next instruction.
- `WriterPatch.Build` — generates the AOB, wildcarding **address-sized (≥4 byte) fields only**
  across every instruction in the window (the loader rewrites those when a module rebases;
  disp8/imm8 are stable and keep the pattern unique), then widens context until it matches
  exactly once *in the writer's own module*. Flags writers in JIT/dynamic code as
  session-only, since no AOB can re-find those.
- Safety: `DebugSetProcessKillOnExit(false)` right after attach (so a trainer crash doesn't
  take the game with it), and breakpoints are cleared from every thread **before**
  `DebugActiveProcessStop` — a debug register left armed with no debugger attached raises an
  unhandled exception and kills the game.

Verified on Windows end-to-end against `GameCheater.Cli --write-target` (a built-in test
process with a known address and two known writers, one module-resident and one JIT'd):
correct instructions and lengths found, writes traced on a worker thread, live NOP stopped the
writer, restore worked, target survived detach, and the emitted JSON round-trips through
`--load-json`. **Not yet run against a real game** — that's the next step.

## NEXT

1. **Run it against SnowRunner fuel for real.** Value-scan to the mirror address, `find-writes`
   on it, NOP the busiest writer, see if the drain stops. If the mirror's writer just copies
   from elsewhere, chain: read its source operand, scan for that address, repeat. This is the
   step that turns the tool into an actual shipped cheat.
2. **Wire it into the Capture tab UI.** Right now it's CLI-only. The natural shape is a "Find
   what writes" button on a candidate row → writer list → NOP/Save. Note `WriteWatch`'s
   `WriterDiscovered` fires on the debug thread while the game is frozen, so the UI must
   marshal and do nothing expensive in the handler.
3. Hotkeys still need live validation (assign F1, toggle in-game).

## Build / run

```powershell
git clone https://github.com/ptempleton/GameCheater ; cd GameCheater
dotnet run --project src/GameCheater.App     # UI; run as Administrator to attach
.\publish.cmd                                # -> publish\GameCheater.exe
dotnet build -c Release -warnaserror         # CI parity
dotnet format --verify-no-changes            # lint gate
dotnet run --project src/GameCheater.Demo -- --selftest   # x86-64 decoder reference tests
```

**Lint gate on a Windows checkout:** `.editorconfig` sets `end_of_line = lf`, but git's
`core.autocrlf=true` gives you a CRLF working tree, so `dotnet format --verify-no-changes`
reports `ENDOFLINE` on *every* file locally. CI (Ubuntu, LF checkout) is unaffected. Filter it
to see real findings: `dotnet format --verify-no-changes 2>&1 | grep -v ENDOFLINE`.

Target games (all single-player-safe): SnowRunner, Palworld, No Man's Sky, Enshrouded,
The Riftbreaker, Soulmask (EAC — offline only), Subnautica 2, Avatar (Denuvo), Hogwarts
Legacy (Denuvo, SP only), Everwind, StarRupture, Windrose (EAC — offline).

## Note on trainer `.dll` files

Native trainer DLLs are **not** a supported input (closed code, no common interface, untrusted
to inject). Use the scanner / code patches / `.CT` tables. Never commit them.
