# SnowRunner "No Vehicle Damage" — full investigation findings

Handoff for whoever picks this up next. **Fuel is solved and published to the cheats repo (works
in the app via Refresh; the embedded `GameCatalog` default is still a placeholder). No Damage is
not solved.** This documents everything learned so far so you don't repeat the dead ends. Tested
live against SnowRunner (Steam, no EAC/BattlEye) over a long session.

## The goal

Freeze or patch vehicle component integrity so the truck takes no damage. SnowRunner shows **5
components** as `current/max`: **tires (x/6), fuel tank (x/50), engine (x/180), transmission
(x/180), suspension (x/200)**. WeMod/WAND ship this as a user-mode trainer, so **it is
user-mode-doable** — no kernel driver required.

## What works (context you can rely on)

- **Memory read/write is completely unguarded.** The value scanner, `--poll`, and value-freeze
  (`--freeze`, the app's Test Freeze) all work fine and never trip anything. Only *debugger*
  operations trip anti-tamper. This is why fuel = a plain value freeze.
- **Fuel cheat (solved + published):** range-scan the fuel float → freeze. Durable pointer chain
  `SnowRunner.exe+0x2AA17F0 → +0x28 → +0x5E8` (final offset = fuel field in the vehicle struct).
  Published to the GameCheater-cheats repo as `games/snowrunner.json`; confirmed working in the app
  after Refresh. (The app's embedded `GameCatalog.BuildSnowRunner` default is still a placeholder.)
- **The "vehicle struct":** `SnowRunner.exe+0x2AA17F0` is a static pointer; `+0x28` derefs to the
  **current active vehicle struct**. Fuel is at `+0x5E8` inside it. This struct **RELOCATES**
  during play (definitely on recover-to-garage; that's why fuel needs `resolveEachTick`). Its
  base changed across observations: `0x2537BA38350`, `0x2537C184210`, `0x2538703D810`,
  `0x24C7AD…` (different pids/recovers). Component-damage-correlated values live in this struct
  and in **sub-objects it points to** (pointer fields at struct `+0x58/+0x68/+0xA0/+0xE8/+0x110/
  +0x200/+0x208/+0x210/+0x218/+0x220/+0x248/+0x250/+0x258/+0x260/+0x268/…`).

## The core problem: every findable value is a MIRROR

The single blocking finding: **we can find dozens of values that equal the displayed integrity
and track it as the engine is damaged, but freezing ANY of them does not change the on-screen
readout or stop damage.** They are all display/mirror copies; the authoritative value the game
reads is upstream and not reachable by scanning the fuel struct.

Concretely, with the engine at various levels we froze all of these — **readout never budged**:

| Representation | Example (engine level) | Chain / address | Result |
|---|---|---|---|
| 0–1 fraction | `1→0.75`, `1→0.6` seen in vehicle-scan | struct sub-objects | mirror |
| fraction `0.349` (63/180) | `[0x28, 0x58, 0xA4]` | struct sub-object | froze→1.0, readout stayed 63 |
| fraction `0.6039` (108/180) | `[0x28, 0x1030]` (main struct, has `1.0` neighbors) | mirror, stayed 108 |
| int `108` | `[0x28, 0x560, 0x610]` | froze→180, stayed 108 |
| int mirror (129→…) | whole-mem scan, one survivor | froze→180, stayed 63 |

Also note a **red-herring cluster**: struct-find for `~0.6` returns dozens of chains all pointing
at the same few addresses with identical neighbor ramp `0.2988, 0.4482, 0.5977, 0.7471, 0.8964`
(multiples of ~0.1494) — that's a shared **UV/gradient lookup table**, not integrity. Ignore it.

### Why whole-memory scanning fails for damage (but worked for fuel)

Component integrity lives in the **relocating** struct/sub-objects. The app's value scanner
tracks **fixed absolute addresses**; when the struct moves, those addresses go stale and the
value "disappears" from the candidate set. So:

- Exact/range narrowing across a damage event → **0 candidates** (the address that held the old
  value now holds unrelated data after the struct moved).
- Unknown → decreased/unchanged narrowing → converges to **noise** (physics floats that happened
  to decrease and stay), never the integrity value. A 168-candidate export had **no value near
  the integrity fraction (0.35) or the raw number (63)** at all.

Fuel worked because — apparently — fuel's authoritative field is genuinely in this struct at a
stable offset. Damage's authoritative field is not (or the struct is a UI/cache copy for damage).

### Storage representation notes

- Integrity is a **0–1 fraction internally**, displayed as `round(fraction × maxInt)` where
  maxInt is 180 (engine/transmission), 200 (suspension), 50 (fuel tank), 6 (tires). E.g. engine
  63/180 stored as `0.34895` (≈ 62.8/180 — sub-integer precision, display rounds up to 63).
- Raw ints matching the displayed number DO appear (`160`, `25`, `108`) but inconsistently
  (searching `152` found nothing while `160` did), and freezing them doesn't drive the display.

## Anti-tamper: what's beaten and what isn't

Three layers, tested live:

1. **PEB attach-detection — BEATEN.** A bare `DebugActiveProcess` self-exits the game (it polls
   `PEB.BeingDebugged`). `Core/Debugging/AntiDebug` clears it on attach + every loop; the game
   then survives a debugger attach indefinitely (`--anti-debug-test` ran 20s clean).
2. **Hardware-breakpoint detection — NOT beaten (yet).** Setting a HW write breakpoint (`WriteWatch`
   / `--find-writes`) self-exits the game within ~10s even with PEB cleared. The game reads its
   own Dr0–Dr7 (via `GetThreadContext`/`NtGetContextThread`) and exits if any are set.
3. **Page-protection detection — NOT beaten.** The page-guard find-what-writes
   (`PageGuardWatch` / `--find-writes-guard`, which uses NO debug registers) ALSO self-exits the
   game — **3/3 times, with 0 page-faults captured and no PEB flag set**. So SnowRunner appears
   to detect the page-protection change on its own memory too (a separate integrity check).

Net: **both driver-free find-what-writes techniques are currently detected.** Tracing a mirror to
its authoritative source (the whole point) is therefore blocked until one of them is made stealthy.

## The path forward (what I was mid-build on)

WeMod does it user-mode, so a driver-free route exists. The two live options:

1. **Debug-register hider (ScyllaHide technique) — HALF BUILT, see `Core/Debugging/
   DebugRegisterHider.cs`.** Inline-hooks `ntdll!NtGetContextThread` INSIDE SnowRunner with an
   injected detour that runs the real syscall then zeroes Dr0–Dr7 in the returned CONTEXT when
   `CONTEXT_DEBUG_REGISTERS` was requested. Our own reads (through our un-hooked ntdll) still see
   the real registers, so `WriteWatch` keeps working; the game's self-check sees clean. **Status:
   compiles, NOT wired into `WriteWatch`, NOT tested.** Next steps:
   - Test the hook on `--write-target` first (install, confirm the process still runs and
     `GetThreadContext` still returns; the .NET CLR calls `NtGetContextThread`, so a bad detour
     will crash it — good canary).
   - Wire it into `WriteWatch.Start` (install hider right after `DebugActiveProcess` + PEB clear,
     before arming breakpoints; dispose in teardown before detach).
   - Then run `--find-writes` on a damage **mirror** (e.g. resolve `[0x28, 0x1030]` to an absolute
     address and watch it). The writer instruction's **register snapshot** (or the instruction it
     copies from) points at the **authoritative** value. `GuardHit.DescribeRegisters()` /
     `WriterHit` already capture context.
   - CAVEAT the hook doesn't cover: `NtQueryInformationProcess(ProcessDebugPort/DebugObjectHandle)`
     (kernel-truth debug port). If SnowRunner also checks that, you'd need to hook that too (same
     technique, different stub) — but it tolerated our attach, so it may not.
   - Page-protection detection is separate and does NOT affect the HW-breakpoint path (no page is
     stripped), so the DR hider + HW `find-writes` is the cleaner bet than fixing page-guard.

2. **Find the damage subsystem's root pointer.** The authoritative integrity is in a different
   object hierarchy than the fuel struct. If you can find a static pointer to the "vehicle
   simulation / damage manager" object, pointer-scan the authoritative value directly and freeze
   it (no debugger needed → ships clean). Unknown how to find that root without find-writes.

## Once you have the authoritative value/instruction

- If it's a freezable value: author it like fuel (freeze + `resolveEachTick` pointer chain,
  pointer-scan for durability). WeMod likely does exactly this.
- If it's a code patch: NOP the damage-apply instruction, ship as a durable AOB `PatchCheat`.
  Runtime never attaches a debugger, so no anti-tamper at play — only *finding* it needs the hook.
- Repeat for all 5 components (they're likely siblings in the same structure at adjacent offsets).

## Tools available (all `GameCheater.Cli`, Administrator, single-player)

- `--find-writes <pid> <addr> [size]` — HW-breakpoint find-what-writes (detected on SnowRunner).
- `--find-writes-guard <pid> <addr> [size]` — page-guard version (also detected on SnowRunner;
  works on unprotected games). Dumps writer registers with `r <n>`.
- `--struct-find <pid> <moduleOffset> <derefsCsv> <value> [structWin] [subWin] [--tol <t>]` —
  search the vehicle struct + sub-objects for a value (exact, or `--tol` for fraction matching),
  prints each hit's chain + neighbors. THE most useful tool for this problem.
- `--struct-diff` / `--vehicle-scan` — churn-filtered field diff on an in-game event.
- `--freeze-chain <pid> <moduleOffset> <offsetsCsv> <type> <value> [secs]` — freeze via a pointer
  chain re-resolved each tick (follows the moving struct). THE way to test a struct-find hit.
- `--freeze <pid> <addr> <type> <value> [secs]`, `--poll <pid> <addr> <size> [secs]`.
- `--bisect <pid> <candidatesFile> <start|stopped|dropping>` + the app's **Export** button —
  binary-search a candidate cluster by freezing halves (safety-filters non-integrity values).
  Not useful here since whole-mem scans only find mirrors, but solid for moving-address-free values.
- `--pointer-scan` / `--pointer-verify` — durable pointer chains.

## Key takeaways for the next attempt

1. Don't bother re-scanning whole memory for damage — it only finds mirrors (moving struct).
2. Use `--struct-find` with the fraction (`displayed ÷ maxInt`) to enumerate copies in the struct,
   and `--freeze-chain … 1.0` to test each. All tested so far are mirrors → the authoritative one
   is NOT in the fuel struct. You likely need find-writes (via the DR hider) or the damage root.
3. The DR hider (`DebugRegisterHider.cs`) is the highest-leverage unfinished work.
