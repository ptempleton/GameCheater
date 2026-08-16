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
`--load-json`.

## Anti-debug: SnowRunner is NOT "fine" (correcting the earlier handoff)

Tested live against SnowRunner (Steam, no EAC/BattlEye — Steam-only, still SP-safe). Two layers:

1. **Attach detection (user-mode, BEATEN).** A bare `DebugActiveProcess` makes SnowRunner
   self-exit within seconds — it polls `PEB.BeingDebugged` (`IsDebuggerPresent`-style). Proven
   with `--anti-debug-test <pid>`: `BeingDebugged` goes `0→1` on attach; we force it back to `0`
   and re-scrub on every debug-loop iteration, and the game then ran a full 20s / a full
   find-writes attach. This is now wired into `WriteWatch` (`clearPebDebugFlags`, default **on**)
   via `Core/Debugging/AntiDebug`. It only touches the PEB; a **kernel-side** check
   (`ProcessDebugPort`/`DebugObjectHandle`) would need an ntdll hook inside the target and is NOT
   handled — `AntiDebugProbe.Diagnosis` tells you which kind you hit, for one game restart.
   Reading/writing memory (the scanner) never tripped anything — only the debugger attach does.
2. **Hardware-breakpoint detection (NOT beaten).** Setting a HW *write* breakpoint (find-writes)
   makes the game exit within ~10s even with the PEB cleared, armed on all ~160 threads with 0
   hits — it inspects its own debug registers, which you can't hide a HW breakpoint from. So
   `find-writes` is unusable against SnowRunner until there's a page-guard (VirtualProtect) watch
   that uses no debug registers. Not built. Do NOT enable `WriteWatch(periodicReArm: true)` vs
   SnowRunner either — it suspends every thread every 400ms and the game treats that as tamper.

## FUEL: SOLVED — infinite fuel is a value freeze + durable pointer chain

The earlier handoff's "fuel needs a code patch / it's a mirror" was **wrong**, twice over:
- The earlier session had scanned onto a **display mirror** (a decoy address). The **range scan**
  (float) lands on the real value — confirmed with `--poll`: it drops smoothly as you drive.
- Freezing that real value **holds the in-game gauge** (verified over 60s of driving). Pure
  `WriteProcessMemory`; no debugger, so none of the anti-tamper above applies. `--freeze <pid>
  <addr> <type> <value> <seconds>` is the CLI proof tool.

Durable chain (new **pointer scanner**, `Core/Scanning/PointerScanner` + `--pointer-scan` /
`--pointer-verify`): scan found 60 static chains; **2 survived two ASLR relaunches**, giving:

    SnowRunner.exe + 0x2AA17F0 -> +0x28 -> +0x5E8    (final +0x5E8 = fuel field in the vehicle struct)

Authored as a `freeze`/`float`/`resolveEachTick` cheat (a verified `snowrunner.json` was produced;
it must be published to the **GameCheater-cheats** repo — the app loads defs from there via
Refresh, NOT from local files). The `--pointer-scan` → restart → `--pointer-verify` loop is the
durability workflow; only chains that survive a relaunch are trustworthy.

**Coordination gotcha that cost hours:** the operator reads a "drive now" instruction only AFTER
the tool's collect/poll window has already opened, so early windows caught a parked truck and
looked dead. Correct flow: have them start driving FIRST, confirm, THEN open the window.

## No Damage — investigation status (NOT cracked; likely infeasible driver-free)

Extensive live work. The chain of findings:
- The engine/component integrity shown as `current/max` (e.g. `59/180`) is **not** stored as that
  integer where we can freeze it — every value we find (int `129→…`, fraction `1→0.75`, struct
  sub-object fields) is a **mirror/copy**: freezing it holds in memory but the on-screen readout
  and the damage icon don't change. The authoritative value is upstream.
- Tracing a mirror to its source needs **find-what-writes**. SnowRunner defeats *both* driver-free
  implementations: the **hardware-breakpoint** version (`find-writes`) — the game reads its own
  debug registers and self-exits; and now, on first live test, the **page-guard** version
  (`find-writes-guard`, built this session, `Core/Debugging/PageGuardWatch`) — the game
  self-exited with **0 page-faults and no PEB flag set**, i.e. it appears to detect the
  page-protection change itself. `--write-target` proves the page-guard tool works correctly on
  an unprotected process; it's SnowRunner's layered anti-tamper that blocks it.
- Conclusion: with both user-mode trace techniques defeated, cracking SnowRunner No Damage would
  likely require **kernel/hypervisor tooling** (a driver, EPT breakpoints) — outside this
  project's self-contained, no-external-tools scope. Before concluding for certain, the one thing
  left to try is confirming the page-guard exit cause: guard-watch a *benign* SnowRunner page and
  time survival (is it the page-strip that's detected, or something else?), and double-check
  PageGuardWatch's PEB clearing fires early enough. If page-guard can be made to survive, the
  mirror→source trace is back on.
- The page-guard tool is a real, general win for OTHER games (most don't check page protections):
  `--find-writes-guard <pid> <addr> [size]` finds writers + dumps their registers.

## OTHER NEXT

1. **No Vehicle Damage** — SnowRunner shows **5 damage components: tires,
   suspension, engine, fuel tank, transmission** — so expect up to 5 values, not one. Damage is
   **event-driven** (only changes on impact), so scan unknown-value → take damage → decreased/
   increased, per component. Try **freeze first** (memory writes are unguarded, like fuel); only
   if a component turns out non-freezable do you need the code-patch path — which is blocked by
   SnowRunner's HW-breakpoint detection until a page-guard watch exists.
2. **Publish `snowrunner.json`** (Infinite Fuel) to the GameCheater-cheats repo so it shows up in
   the app's picker after Refresh. Then live-toggle it in the UI as the final end-to-end check.
3. **Wire find-writes into the Capture tab UI.** CLI-only today. Shape: a "Find what writes"
   button on a candidate row → writer list → NOP/Save. `WriteWatch.WriterDiscovered` fires on the
   debug thread while the game is frozen, so the UI must marshal and do nothing expensive there.
4. Hotkeys still need live validation (assign F1, toggle in-game).

## Debugger CLI commands (all Administrator, single-player only)

- `--find-writes <process|pid> <hexAddr> [size] [game]` — the main tool. Anti-debug PEB-clearing
  is on by default; prints per-writer hits, live NOP/restore, durable-AOB save.
- `--anti-debug-test <process|pid> [seconds] [--no-clear]` — one experiment: is the game's
  anti-debug user-mode (beatable) or kernel-side? WARNING: if it wins, the game exits.
- `--poll <process|pid> <hexAddr> [size] [seconds]` — read-only value sampler; no attach, safe.
- `--freeze <process|pid> <hexAddr> <float|double|int|long> <value> [seconds]` — value-freeze test;
  writes-only, no attach. Proves whether a live value is freezable (how fuel was solved).
- `--pointer-scan <process|pid> <hexAddr> [maxDepth=5] [maxOffset=0x800]` — find static pointer
  chains to a heap address; saves candidates to `pointer-paths.json`.
- `--pointer-verify <process|pid> <hexAddr>` — after a restart+rescan, keep only saved chains that
  still resolve. Run twice across restarts; survivors are the durable chain.
- `--write-target [seconds]` — the self-test process for the debugger (known address + writers).
- `--selftest` — x86-64 length-decoder reference tests (no game needed).

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
