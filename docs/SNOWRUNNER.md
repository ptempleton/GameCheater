# SnowRunner trainer runbook

This is the canonical operator and maintainer guide for SnowRunner. The detailed discovery
record, including every scan sequence and failed approach, is in
[NO-DAMAGE-FINDINGS.md](NO-DAMAGE-FINDINGS.md).

All memory work documented here was performed against the Steam build on 2026-08-16 in
single-player. Do not attach or write memory in an online or anti-cheat-protected session.

## Shipped behavior

The current remote definition is revision 6 in the separate
`ptempleton/GameCheater-cheats` repository. In the Cheats tab, damage protection appears as one
visible toggle:

- **No Vehicle Damage (except tires)** protects the engine, transmission, fuel tank, and
  suspension.
- Tires are deliberately excluded because their authoritative storage has not been identified.
- The four component freezes stay loaded internally but are hidden by the composite definition.
- **Infinite Fuel** is independent and may be toggled separately.
- **Set Repair Points**, if shown from the embedded fallback catalog, is still a placeholder and
  must not be used as a validated SnowRunner cheat.

The label must continue to say **except tires** until tire protection is live- and
restart-verified.

## Using the trainer

1. Start SnowRunner and enter a single-player game with a truck.
2. Start `GameCheater.exe` as Administrator.
3. Select **SnowRunner**.
4. Let the automatic definition refresh finish, or click **Refresh**.
5. If SnowRunner was already attached during Refresh, stop the engine and reselect SnowRunner
   (or select another game and return) so the refreshed definition is instantiated.
6. Click **Start Engine**.
7. Enable **No Vehicle Damage (except tires)** and, if wanted, **Infinite Fuel**.
8. Use **Disable All** before intentionally testing damage or before changing workflows.

Expected damage behavior is one visible damage row, not four. If the four component rows appear,
the client is old, the revision-6 feed was not loaded, or the cached definition is stale. See
[Troubleshooting](#troubleshooting).

## Confirmed cheats and durable addresses

Raw heap addresses are evidence from one process lifetime only. Never author them. Every shipped
cheat resolves a module-relative pointer chain at runtime and re-walks it on every freeze tick so
truck changes, recovery, and object relocation are handled.

| Cheat | Stored value | Frozen value | Durable pointer chain |
|---|---:|---:|---|
| Infinite Fuel | `float` fuel amount | current value at enable | `SnowRunner.exe+0x2AA17F0 -> +0x28 -> +0x5E8` |
| No Transmission Damage | `int32` accumulated damage | `0` | `SnowRunner.exe+0x2A8EDD8 -> +0x8 -> +0x148 -> +0x38` |
| No Engine Damage | `int32` accumulated damage | `0` | `SnowRunner.exe+0x2A8EDD8 -> +0x8 -> +0x150 -> +0x38` |
| No Fuel Tank Damage | `int32` accumulated damage | `0` | `SnowRunner.exe+0x2A8EDD8 -> +0x8 -> +0x158 -> +0x38` |
| No Suspension Damage | `int32` accumulated damage | `0` | `SnowRunner.exe+0x2A8EDD8 -> +0x8 -> +0x160 -> +0x38` |

Damage is stored as an accumulator:

```text
accumulated damage = maximum integrity - displayed current integrity
```

The crucial discovery was to search for this increasing integer, not the decreasing HUD value or
a 0-to-1 integrity fraction.

## Live evidence

| Component | HUD sequence | Damage sequence | Authoritative address(es) observed | Result |
|---|---|---|---|---|
| Engine | `167/180 -> 152/180` | `13 -> 28` | `0x24BF48B5BD8`, then `0x2876B74A9A8`; restart convergence at `0x2BCE7E95A58` | Zero restored `180/180`; freeze blocked another hit |
| Transmission | `177/180 -> 175/180 -> 172/180` | `3 -> 5 -> 8` | `0x2BCE734D688` | Current-value freeze stopped loss; zero restored `180/180` |
| Suspension | `100/200 -> 35/200`; second pass `50/200 -> 36/200` | `100 -> 165`; `150 -> 164` | `0x2BCE734D908`, `0x25CF580F288`; restart convergence at `0x1D86F641F28` reading `16` for `184/200` | Zero restored `200/200`; restart verified |
| Fuel tank | `45/50 -> 39/50 -> 35/50` | `5 -> 11 -> 15` | `0x1D85854BB88`; restart convergence at `0x144F8C2A148` reading `8` for `42/50` | Zero restored `50/50`; freeze blocked another hit; restart verified |

The addresses changed between launches, as expected. The authored pointer chains survived.

## Exact discovery workflow

Use this for another current/max component whose HUD likely derives from accumulated damage:

1. In Capture, reset and choose `int`.
2. Leave **save value (blank=current)** empty.
3. Calculate `maximum - current` and run an exact first scan for that damage value.
4. Damage only the target component, recalculate, and narrow with another exact value.
5. Repeat until a small candidate list remains.
6. Test one candidate at a time by freezing its **current** value. Do not write zero yet.
7. If the HUD still drops, unfreeze and reject it.
8. If the HUD holds, unfreeze, write zero, and verify that the component returns to maximum.
9. Damage it again. It must remain at maximum.
10. Pointer-scan the confirmed address, restart SnowRunner, resolve the saved paths read-only,
    and verify that surviving paths converge on the expected accumulator.
11. Author only a surviving module-relative path and set `resolveEachTick` to `true`.

Commands:

```powershell
Get-Process -Name SnowRunner
dotnet run --project src/GameCheater.Demo -- --pointer-scan <pid> <address> 5 800
# restart the game and obtain the new PID
dotnet run --project src/GameCheater.Demo -- --pointer-resolve <newPid>
dotnet run --project src/GameCheater.Demo -- --pointer-verify <newPid> <newAddress>
```

`--pointer-scan` replaces `pointer-paths.json`. Preserve it before scanning another unresolved
target. Pointer resolution and verification are read-only.

## Composite implementation

The remote definition declares a composite before its concrete members:

```json
{
  "name": "No Vehicle Damage (except tires)",
  "category": "Vehicle",
  "type": "composite",
  "hideMembers": true,
  "members": [
    "No Engine Damage",
    "No Transmission Damage",
    "No Fuel Tank Damage",
    "No Suspension Damage"
  ]
}
```

The loader builds concrete cheats first and composites second, so declaration order is safe.
`hideMembers` affects presentation only; the component cheats remain in the `Trainer`.

Composite lifecycle guarantees:

- Enabling is transactional. If a member fails, members enabled earlier in that operation are
  rolled back and the failing member is reported.
- Disabling turns off only the members that the composite enabled. A member that was already on
  remains on.
- If a member is disabled directly while the master is on, the master turns off the remaining
  members it enabled so the UI cannot falsely report complete protection.
- Trainer teardown still sees all hidden members and disables them.

## Definition delivery and versioning

The executable and cheat definitions are intentionally separate:

```text
GameCheater client
    -> downloads GameCheater-cheats/index.json
    -> downloads each games/*.json definition
    -> caches files in %AppData%\GameCheater\cheats
    -> merges fetched definitions over the embedded catalog by cheat name
    -> builds the selected Trainer
```

A pointer, value, name, description, or member-list correction normally needs only a cheats-repo
revision bump. It does **not** require rebuilding the client. Update both the game definition's
`revision` and its `index.json` entry.

A new definition concept requires a client release first. Composite support and
`hideMembers` were client changes, so the correct deployment order was:

1. Merge and distribute the client with composite parsing and hidden-member presentation.
2. Merge revision 6 of the SnowRunner definition.

Older clients safely skip unsupported composites, but they cannot display the intended master
toggle.

Refresh runs automatically at startup and can also be triggered in the app. On a network
failure, the client loads its last cache. Refresh deliberately preserves an attached session;
the new definition takes effect the next time that game is selected.

## Building, validation, and artifacts

Required gates:

```powershell
dotnet run --project src/GameCheater.Demo -c Release -- --selftest
dotnet build -c Release -warnaserror
dotnet format --verify-no-changes
git diff --check
```

The self-test currently covers 49 x86-64 decoder cases, backward writer resolution, and four
composite lifecycle/loading cases.

For the normal local single-file build:

```powershell
.\publish.cmd
# output: publish\GameCheater.exe
```

The 2026-08-16 working session also used
`artifacts\client-build\GameCheater.exe` as the one canonical local test build. The older
`dr-hider-build`, `transmission-build`, `suspension-build`, `fueltank-build`, and
`review-build` directories were removed. Both `publish/` and `artifacts/` are ignored; never
commit compiled clients, trainer binaries, or third-party `.CT` files.

Official `v*` tags publish self-contained win-x64 client and CLI zip assets through
`.github/workflows/release.yml`. Tags are cut by the owner.

## Tires: unresolved and unsafe candidates

The HUD is **usable tires / total tires**, not a normal component integrity fraction. A six-tire
truck showing `4/6` had two visibly flat tires; later testing reached `1/6`.

- Scanning the disabled count produced one executable-module candidate,
  `0x7FF6D18EC1D0`. Writing `1` did not repair a tire. It is a mirror.
- Scanning usable counts `6 -> 5 -> 4` lost all candidates at `3`, indicating derived or
  relocating state.
- An unknown-int scan while flattening one tire narrowed from 4,344 to 142 candidates.
- Candidate `0x2BB3030C0F8` read as int `51` and had pointer paths, but writing zero crashed
  SnowRunner. **Never retry this address.**

Future tire work must remain read-only until a field correlates with one individual tire across
damage, garage repair, recovery, truck changes, and relaunch. Prefer type-aware float inspection
or a vehicle-structure diff. Pointer reachability is not proof that a write is safe.

## Debugger and anti-tamper status

Normal memory reads, scans, pointer resolution, and freezes work. SnowRunner's resistance is
specific to tracing mechanisms:

- Clearing `PEB.BeingDebugged` lets a debugger attach survive.
- SnowRunner detects hardware debug registers used by `WriteWatch`.
- It detects page-protection changes used by `PageGuardWatch`.
- `DebugRegisterHider` remains experimental. Its target-process detour no longer crashes the
  test process, but the visibility assertion still fails. Do not use
  `--hide-debug-registers` against SnowRunner.

None of this machinery is required for the solved accumulator freezes.

## Troubleshooting

### Four damage toggles still appear

1. Close the old client completely.
2. Run a client that contains composite and `hideMembers` support.
3. Click **Refresh** and wait for the up-to-date status.
4. Reselect SnowRunner so revision 6 is instantiated.
5. If offline, inspect or remove only the SnowRunner cache file under
   `%AppData%\GameCheater\cheats\games`, then refresh when online. Do not delete unrelated
   app data.

### The master fails to enable

The status reports the member that failed. The likely causes are a game update, no active truck,
or a chain that no longer resolves. Test the component chain read-only after a relaunch before
changing the definition.

### A component does not follow a recovered or changed truck

Confirm the definition has `"resolveEachTick": true`. Never replace the pointer chain with the
current raw heap address.

## Change record

- Cheat-feed PRs 1 and 2 published engine, transmission, suspension, and fuel-tank damage
  protection.
- Client PR 15 added the pointer/discovery safety work and composite runtime, then added
  `hideMembers` presentation so the UI exposes one damage toggle.
- Cheat-feed PR 3 published revision 6 with
  **No Vehicle Damage (except tires)** and hidden component members.
- Client PR 15 and cheat-feed PR 3 were merged on 2026-08-16 after the client build/lint CI gate
  passed.

When tires are solved, add **No Tire Damage** as a concrete member, restart-verify it, add it to
the composite, rename the master to **No Vehicle Damage**, and bump both feed revisions.
