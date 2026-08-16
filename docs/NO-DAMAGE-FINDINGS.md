# SnowRunner damage investigation

Tested against SnowRunner on Steam in single-player. Memory reads and value freezes work without
an anti-cheat bypass; keep all testing offline.

## Current status

- **Engine, transmission, fuel-tank, and suspension damage are solved.** SnowRunner stores
  accumulated damage as 4-byte integers:
  displayed integrity = component maximum - accumulated damage.
- The built-in **No Engine Damage**, **No Transmission Damage**, **No Fuel Tank Damage**, and
  **No Suspension Damage** cheats freeze their accumulators at zero.
- Clients with composite-cheat support expose one visible **No Vehicle Damage (except tires)**
  toggle. It transactionally controls those four cheats while keeping the component freezes
  loaded but hidden from the cheat list. If a
  member fails to enable, the master rolls back members it already enabled and reports the
  failing name.
- Fuel is also solved separately.
- Tires remain unsolved. Do not describe the present cheats as full
  “No Vehicle Damage.”

## Authored damage chains

    Engine:
    SnowRunner.exe+0x2A8EDD8 -> +0x8 -> +0x150 -> +0x38

    Transmission:
    SnowRunner.exe+0x2A8EDD8 -> +0x8 -> +0x148 -> +0x38

    Fuel tank:
    SnowRunner.exe+0x2A8EDD8 -> +0x8 -> +0x158 -> +0x38

    Suspension:
    SnowRunner.exe+0x2A8EDD8 -> +0x8 -> +0x160 -> +0x38

Type: int32

Frozen value: zero

Runtime behavior: resolve the chain every freeze tick because the active vehicle object can move.

Four equivalent static roots survived the same relaunch checks:

    SnowRunner.exe+0x2A8EDE0 -> +0x8 -> +0x150 -> +0x38
    SnowRunner.exe+0x2A8EDC8 -> +0x20 -> +0x150 -> +0x38
    SnowRunner.exe+0x2AA1508 -> +0x98 -> +0x150 -> +0x38
    SnowRunner.exe+0x2A926F8 -> +0x18 -> +0x18 -> +0x150 -> +0x38

The first chain is authored because it is one of the shortest survivors.

## Live validation

The value was found by searching for **damage increasing**, rather than integrity decreasing:

1. Engine 167/180 means accumulated damage 13.
2. First exact int scan for 13.
3. After a hit, engine 152/180 means damage 28; narrow to exact 28.
4. Set the authoritative survivor to zero; the display immediately returned to 180/180.
5. With it frozen at zero, another collision left the engine at 180/180.

The first authoritative address was 0x24BF48B5BD8. After restarting, a new scan found
0x2876B74A9A8; five pointer paths from the first session resolved to it. After another restart,
all five paths independently converged on 0x2BCE7E95A58, whose int32 value was zero. The raw
addresses changed as expected under ASLR/object relocation; the pointer paths survived.

Transmission followed the same pattern: 177/180, 175/180, and 172/180 narrowed the accumulated
damage integer through 3, 5, and 8 to five candidates. Freezing 0x2BCE734D688 at its current value
stopped further loss; setting it to zero restored 180/180 and another collision left it at
180/180. Its pointer scan found the same durable static roots as engine, with sibling offset
0x148 instead of 0x150.

Suspension required a stricter restart check. In the first session, 100/200 then 35/200 narrowed
damage from 100 to 165; 0x2BCE734D908 held the display and zero restored 200/200. The first
shortlisted paths later resolved to the wrong value and were rejected. In a fresh session,
50/200 then 36/200 narrowed damage from 150 to 164; 0x25CF580F288 held and restored the display.
After another SnowRunner restart the suspension read 184/200, so expected damage was 16. Eleven
saved paths converged on 0x1D86F641F28 and read exactly 16. The authored path uses the same durable
root as engine and transmission, with sibling offset 0x160.

Fuel-tank damage used maximum 50. Readings 45/50, 39/50, and 35/50 narrowed accumulated damage
through 5, 11, and 15 to 0x1D85854BB88. Freezing its current value held 35/50; zero restored
50/50 and blocked another hit. After restart the tank read 42/50, so expected damage was 8.
Four saved paths converged on 0x144F8C2A148 and read exactly 8. The durable sibling offset is
0x158.

## Exact procedure: discover another damage component

### 1. Prepare the session

1. Run SnowRunner in single-player/offline mode.
2. In GameCheater, select **SnowRunner**, click **Start Engine**, and open **Capture**.
3. Click **Reset**, select Type **int**, and make sure the bottom **save value
   (blank=current)** box is empty.
4. Protect already-solved components from collateral damage by enabling their authored cheats.
   Do not enable a cheat for the component currently being measured.
5. Record the target component's maximum and current readings.

### 2. Search accumulated damage

Calculate:

    accumulated damage = maximum - current

For example, transmission 177/180 means accumulated damage 3.

1. Enter the calculated damage in the top value box and click **First Scan**.
2. Damage only the target component again and record its new reading.
3. Recalculate accumulated damage.
4. Enter the new number in **exact or 179-181**, then click **= value**.
5. Repeat the damage, calculation, and **= value** narrowing until only a small candidate list
   remains. The transmission sequence was 3, then 5, then 8, leaving five candidates.

Use exact values rather than **+ increased** whenever the displayed component reading is known.
Exact narrowing discards unrelated values that happened to increase at the same time.

### 3. Identify the authoritative candidate safely

Test one candidate at a time:

1. Confirm **save value (blank=current)** is blank.
2. Select one candidate and click **Test Freeze**. Blank freezes the candidate at its existing
   value; it does not write zero.
3. Damage the target component once.
4. If its displayed integrity decreases, click **Unfreeze** and test the next candidate.
5. If its displayed integrity does not decrease, leave that candidate selected and copy its
   address with **Copy address**.

Selecting another candidate now automatically unfreezes the previous test and clears the value
box. Still use **Unfreeze** explicitly between candidates so the test state is obvious.

### 4. Confirm zero is the correct authored value

Only do this after a candidate has stopped damage while frozen at its current value:

1. Click **Unfreeze**.
2. Keep the confirmed candidate selected.
3. Enter 0 in **save value (blank=current)** and click **Test Freeze**.
4. The component should immediately return to its maximum.
5. Damage it again. It must remain at maximum.

If setting zero does not restore the display, unfreeze immediately and do not pointer-scan that
candidate.

### 5. Find durable pointer paths

Copy the confirmed address and find SnowRunner's PID:

    Get-Process -Name SnowRunner

From the repository root, run:

    dotnet run --project src/GameCheater.Demo -- --pointer-scan <pid> <address> 5 800

The address may be supplied with or without 0x. Pointer scan is read-only, but it replaces
pointer-paths.json; preserve that file first if another unresolved component scan is in progress.

### 6. Verify after relaunch

1. Restart SnowRunner normally.
2. Get its new PID.
3. Resolve every saved path without writing memory:

       dotnet run --project src/GameCheater.Demo -- --pointer-resolve <newPid>

4. Prefer paths that converge on one address and show a plausible accumulated-damage int32.
5. Verify that address and discard paths that do not reach it:

       dotnet run --project src/GameCheater.Demo -- --pointer-verify <newPid> <newAddress>

6. Repeat after another relaunch when possible. Choose a short survivor rooted in
   SnowRunner.exe. Never store the raw heap address from Capture.

If the saved paths do not converge, rediscover the authoritative address with the exact scan and
run pointer verification against it. Do not guess which resolved address is correct.

### 7. Author the built-in cheat

Add a FreezeCheat<int> to GameCatalog.BuildSnowRunner. For transmission:

    t.Add(new FreezeCheat<int>(
        Resolve.Pointer(0x2A8EDD8, 0x8, 0x148, 0x38),
        value: 0,
        freeze: true,
        resolveEachTick: true)
    {
        Name = "No Transmission Damage",
        Category = "Vehicle",
        Description = "Keeps the active truck's accumulated transmission damage at zero.",
    });

Keep resolveEachTick enabled because switching/recovering vehicles can relocate the active
objects. Build with warnings as errors and run the formatter gate before handing off:

    dotnet build GameCheater.slnx -c Release -warnaserror
    dotnet format --verify-no-changes

## Capture safety lesson

The three-candidate scan contained two decoys. A stale zero left in the capture value box was
applied while testing those candidates and SnowRunner crashed. The subtraction was correct; the
unsafe part was writing zero to an unverified decoy.

The capture UI now clears both the temporary freeze and the value box when candidate selection
changes. During manual discovery:

- Leave the value box blank when testing an unknown candidate; blank means freeze its current value.
- Only write a chosen value after the candidate has proved that it controls the display.
- Prefer --pointer-resolve for relaunch validation; it reads saved chains without writing memory.

## Why the earlier integrity search failed

Earlier work searched for the displayed current value or a 0-1 integrity fraction. It found many
UI/cache copies. Freezing representative copies did not affect the display, which led to the
incorrect conclusion that every scannable damage value was a mirror.

The authoritative field uses the opposite representation: accumulated damage starts at zero and
increases on impact. Freezing that value at zero is both simpler and safer than fighting derived
integrity copies.

## Tire investigation: known unsafe dead ends

The tire display is usable tires / total tires, not a normal current/max integrity value.

- Searching the disabled count from 2 through 6 produced one executable-module candidate,
  0x7FF6D18EC1D0. Writing 1 did not repair a tire; it was a display mirror.
- Searching the usable count from 6 through 3 lost every candidate, so the count is derived or
  relocates as individual tire states change.
- An unknown int scan against one tire flattened during the scan stopped at 142 candidates.
  Most were float bit patterns misinterpreted as integers.
- 0x2BB3030C0F8 read as int 51 and had static pointer paths, but writing zero crashed SnowRunner.
  Never retry that address or treat pointer reachability alone as proof that a candidate is safe.

Future tire work must remain read-only until an individual tire field is correlated across
repair and damage states. Prefer a type-aware float scan or vehicle-structure diff; do not write
to candidates selected only because their integer value looks plausible.

## Anti-tamper findings

These findings are still useful for future code tracing:

- Clearing PEB.BeingDebugged allows a debugger attach to survive.
- SnowRunner detects hardware debug registers used by WriteWatch.
- It also detects the page-protection changes used by PageGuardWatch.
- DebugRegisterHider is experimental and explicitly opt-in. Its target-process detour no longer
  crashes the test process, but the visibility probe still sees the registers. Do not use
  --hide-debug-registers against SnowRunner until that assertion passes.

None of that machinery is needed for the solved damage accumulators.

## Next discovery target

Tires are the only remaining vehicle-damage component. They require the separate read-only
approach documented above; do not reuse the accumulated component-damage recipe blindly because
the HUD exposes usable tire count rather than individual tire integrity.

## Composite definition

The cheats repository can define a master toggle without duplicating pointer chains:

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

Member names are case-insensitive and must refer to concrete cheats in the same trainer
definition. When tire damage is solved, add **No Tire Damage** to members and rename the master
to **No Vehicle Damage**.
