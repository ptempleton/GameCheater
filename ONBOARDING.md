# GameCheater onboarding and handoff

Use this from a fresh session on the Windows development machine. Run an Administrator terminal
when attaching to a game. The repository is the source of truth; read `AGENTS.md` before making
changes.

For SnowRunner, start with [docs/SNOWRUNNER.md](docs/SNOWRUNNER.md). The deeper scan-by-scan lab
record is [docs/NO-DAMAGE-FINDINGS.md](docs/NO-DAMAGE-FINDINGS.md).

## Product

GameCheater is a self-contained C#/.NET 10 single-player trainer with an Avalonia UI. The user
selects a game, attaches, and toggles cheats. The primary path uses the project's own runtime,
scanner, pointer resolver, and authored JSON definitions. User-supplied `.CT` files are optional;
third-party tables and trainer binaries are never redistributed.

The public repositories are:

- `ptempleton/GameCheater`: client, core runtime, UI, scanners, CLI, and documentation.
- `ptempleton/GameCheater-cheats`: remotely refreshed trainer definitions.

## Non-negotiable rules

- Single-player/offline only. Never write memory in online or EAC/BattlEye-protected sessions.
- Work on a feature branch and open a PR.
- Never merge to `main` without explicit approval from `ptempleton`.
- `dotnet format --verify-no-changes` is the lint gate.
- Never commit `.CT` files, trainer binaries, or compiled client artifacts.
- Resolve addresses at enable time. Never persist a raw heap address across launches.
- A code patch must preserve and restore the original bytes.

## Current implementation

- **Memory:** typed read/write, regions, modules, protection changes, AOBs, static resolution, and
  pointer chains.
- **Cheats:** value freezes, set values, restorable patches, transactional composite toggles,
  per-tick pointer re-resolution, and teardown.
- **Discovery:** exact/range/unknown value scans, pointer scan/resolve/verify, struct tools,
  candidate bisection, polling, and experimental writer tracing.
- **App:** game picker, automatic and manual remote Refresh, Cheats and Capture tabs, category
  groups, global hotkeys, Disable All, candidate test freeze, export, and definition authoring.
- **Definitions:** source-generated JSON parsing for freeze, patch, and composite cheats.
  Composites may hide concrete members while retaining them for runtime and teardown.
- **Distribution:** remote definitions are cached in
  `%AppData%\GameCheater\cheats`; cache is used when the repository is unavailable.
- **Tables:** static/pointer `.CT` entries run in the built-in runtime. Lua/Auto Assembler entries
  require an optional installed Cheat Engine backend.

## SnowRunner status

Remote definition: **revision 6**, tested against the Steam build on 2026-08-16.

| Feature | Live result | Published | UI behavior |
|---|---|---|---|
| Infinite Fuel | Confirmed, restart-verified pointer chain | Yes | Separate toggle |
| Engine damage | Confirmed accumulator freeze | Yes | Hidden member of master |
| Transmission damage | Confirmed accumulator freeze | Yes | Hidden member of master |
| Fuel-tank damage | Confirmed and restart-verified | Yes | Hidden member of master |
| Suspension damage | Confirmed and restart-verified | Yes | Hidden member of master |
| No Vehicle Damage | Confirmed for the four components above | Yes | One visible **No Vehicle Damage (except tires)** toggle |
| Tire damage | Unsolved | No | Explicitly excluded |

Damage uses an increasing 4-byte accumulator:

```text
displayed integrity = component maximum - accumulated damage
```

The component chains share `SnowRunner.exe+0x2A8EDD8 -> +0x8`; the sibling offsets are
`0x148` transmission, `0x150` engine, `0x158` fuel tank, and `0x160` suspension, followed
by `+0x38`. Freeze each `int32` at zero and resolve every tick.

Fuel is a `float` frozen at its current value through
`SnowRunner.exe+0x2AA17F0 -> +0x28 -> +0x5E8`.

The embedded catalog still contains a placeholder fuel definition and **Set Repair Points**.
Fetched definitions win by name, so revision 6 replaces fuel and the four damage components.
The embedded-only repair-points placeholder may remain visible and is not validated.

## Client/feed boundary

Definitions are designed to update without rebuilding the executable:

1. Client downloads the cheats repository's `index.json`.
2. It downloads every listed `games/*.json`.
3. It caches them under AppData.
4. It merges fetched cheats over embedded cheats by name.
5. It instantiates a trainer when the game is selected.

Pointer/value/member changes need only a feed revision bump. A new schema/runtime/UI concept
needs a client release first. Composite and `hideMembers` support required both repositories:
client PR 15 was merged before cheat-feed PR 3.

Refresh preserves a currently attached trainer. Reselect the game after Refresh to instantiate
the new definition.

## Validated SnowRunner limitations

- Tires are not represented by the displayed usable/total count. That count is derived.
- `0x7FF6D18EC1D0` was a tire display mirror; writing `1` did not repair anything.
- Writing zero to tire candidate `0x2BB3030C0F8` crashed SnowRunner. Never retry it.
- Future tire work must be read-only until an individual tire field is correlated.
- Normal scans and freezes work, but SnowRunner detects hardware debug registers and page-guard
  tracing.
- `DebugRegisterHider` is experimental. Its visibility assertion still fails; do not use
  `--hide-debug-registers` against SnowRunner.

## CLI map

- `--pointer-scan`, `--pointer-resolve`, `--pointer-verify`: find and relaunch-test durable
  paths.
- `--freeze`, `--freeze-chain`, `--poll`: controlled value testing and read-only sampling.
- `--struct-find`, `--struct-diff`, `--vehicle-scan`: inspect related object storage.
- `--bisect`: narrow an exported candidate cluster by freezing halves.
- `--find-writes`, `--find-writes-guard`: writer tracing; detected by SnowRunner, usable on
  unprotected games.
- `--anti-debug-test`, `--write-target`: debugger diagnostics.
- `--load-json`, `--pull`, `--ct`: definition and table helpers.
- `--selftest`: process-free decoder, backward-resolution, and composite tests.

Run the CLI with:

```powershell
dotnet run --project src/GameCheater.Demo -- <arguments>
```

## Build and verification

```powershell
dotnet run --project src/GameCheater.Demo -c Release -- --selftest
dotnet build -c Release -warnaserror
dotnet format --verify-no-changes
git diff --check
```

On a Windows checkout with `core.autocrlf=true`, the formatter can report only `ENDOFLINE`
noise because `.editorconfig` requires LF. CI runs on an LF checkout. Do not hide any formatter
diagnostic other than verified line-ending-only noise.

Local single-file client:

```powershell
.\publish.cmd
# publish\GameCheater.exe
```

`publish/` and `artifacts/` are ignored. Do not accumulate or commit old build directories.
Official `v*` tags trigger the release workflow and are cut by the owner.

## Next work

1. Identify per-tire state with read-only float/structure correlation across flatten, garage
   repair, recovery, truck change, and relaunch.
2. Only after safe live and restart verification, add **No Tire Damage** to the feed and master,
   then rename the master to **No Vehicle Damage**.
3. Replace/remove the remaining embedded SnowRunner placeholders so offline fallback matches the
   remote definition.
4. Live-validate global hotkeys.
5. Keep debugger-hiding work isolated and experimental until its visibility self-test passes.

## Recent merged work

- GameCheater PR 15: SnowRunner damage authoring, capture safety, pointer workflows, composite
  runtime, and one-visible-toggle presentation.
- GameCheater-cheats PR 1: engine, transmission, and suspension damage.
- GameCheater-cheats PR 2: fuel-tank damage.
- GameCheater-cheats PR 3: revision-6 composite master with hidden component toggles.

The owner explicitly approved those merges. New work still requires a new branch, PR, and
separate merge approval.
