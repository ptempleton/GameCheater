# Per-game scan recipes

How to **find** the cheats yourself with GameCheater's `ValueScanner` (or Cheat Engine),
per target game. Nothing here is a copied table or a real address — addresses only exist
live and move every launch. This is *methodology*: what's worth finding, whether it's a
value-freeze or a code-patch, the likely data type, and how to narrow it down. Once you
have a survivor address, turn it into a durable pointer chain or AOB signature and paste
that into an authored trainer definition.

---

## The general workflow

### 1. Know the two cheat shapes
- **Value-write / freeze** (`FreezeCheat<T>`): money, fuel, health, stamina, item counts.
  You find the address holding the number and hold/overwrite it. Easiest by far.
- **Code-patch** (`PatchCheat`): "no damage", "no reload", "no build cost". You find the
  *instruction* that decrements the value and NOP it. Needed when a value is recomputed
  constantly or when freezing breaks game logic (e.g. regen).

### 2. Pick the data type
- **Integers (4-byte `int`)**: money, currency, item/resource counts, ammo, skill points.
- **Floats (4-byte `float`)**: health, stamina, fuel, durability, hunger/thirst, oxygen,
  time-of-day, position. When a bar looks "smooth", it's almost always a float.
- If an int scan for a known value finds nothing, retry as float (and vice-versa).

### 3. Narrow it down
- **Known value** (e.g. money = 1200): `FirstScan(1200)` → change it in game → `NextScanExact(newValue)` → repeat until a few remain.
- **Unknown value** (e.g. a health bar with no number): `FirstScanUnknown()` → take damage → `NextScanDecreased()` → heal → `NextScanIncreased()` → repeat. "Changed/Unchanged" also work.
- **Floats**: prefer decreased/increased over exact (rounding makes exact matches flaky).

### 4. Make it durable (this is the important part)
A raw survivor address is invalid next launch. Convert it:
- **Pointer chain** — in CE, right-click the address → "Pointer scan for this address",
  restart the game, rescan to filter to a stable path. Paste as `Resolve.Pointer(base, ...offsets)`.
- **AOB signature** — for code patches: on the value, "Find out what writes to this
  address", pick the instruction, copy the surrounding bytes as a pattern with wildcards
  for the address operand. Paste as `Resolve.Aob("...")` and `PatchCheat.Nops(len)`.

### 5. Engine notes that save time
- **Unreal Engine (UE4/UE5)** games — Palworld, Soulmask, Subnautica 2, StarRupture,
  Windrose, Hogwarts, and most survival titles here — commonly store player stats as
  **floats inside a Gameplay Ability System attribute set**. Health/stamina/hunger are
  floats reached through a `PlayerController → Pawn → AttributeSet` pointer chain. Expect
  to pointer-scan; raw statics rarely survive a patch.
- **Denuvo** titles (Avatar, Hogwarts) make **AOB scanning harder** (obfuscated/relocated
  code) but don't affect value scanning. Prefer **value-freeze + pointer chains** over code
  patches on these two.
- **Early-access** titles churn every patch — expect to re-scan after updates. That's the
  argument for AOB/pointer resolution over stored addresses.

---

## Difficulty legend
🟢 easy value scan · 🟡 needs pointer scan / unknown-value narrowing · 🔴 code patch or awkward storage

---

## SnowRunner
Off-road sim. **Gotcha: money is stored save/server-side**, so it's unreliable to freeze —
lean on the vehicle-side values instead.

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Infinite fuel | freeze | float | 🟢 | Drive to burn fuel → `FirstScanUnknown` + `NextScanDecreased`; or read tank size and exact-scan. Freeze at max. `resolveEachTick` (per-truck pointer). |
| Infinite repair points | freeze | int/float | 🟢 | Damage a truck, use repair to change the value, decreased/increased narrowing. |
| Infinite spare tires | freeze | int | 🟢 | Exact-scan the count shown, use one, `NextScanExact`. |
| Freeze time of day | freeze | float | 🟡 | `FirstScanUnknown` → wait → `NextScanIncreased` repeatedly; freeze. |
| No vehicle damage | patch | — | 🔴 | Find-what-writes on the damage/health float, NOP the write. Or just freeze health. |

## No Man's Sky
Very scan-friendly. **Back up your save before large edits** (big inventory writes can corrupt).

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Units (money) | set/freeze | int | 🟢 | Exact-scan units, buy/sell to change, narrow. |
| Nanites / Quicksilver | set/freeze | int | 🟢 | Same as units. |
| Infinite health/shield | freeze | float | 🟡 | Take damage → decreased scan; pointer-scan for stability. |
| Infinite stamina / life support | freeze | float | 🟢 | Let it drain → decreased scan. |
| No-cost building / crafting | patch | — | 🔴 | Find-what-writes on a resource count while crafting; NOP the subtract. |

## Palworld
UE5, released 1.0, actively updated → expect pointer scans and post-patch re-scans.

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Infinite health | freeze | float | 🟡 | UE attribute float; damage → decreased scan → pointer-scan through the pawn. |
| Infinite stamina | freeze | float | 🟢 | Sprint to drain → decreased scan. |
| Money / resources | set/freeze | int | 🟢 | Exact-scan, spend, narrow. |
| No hunger / temperature | freeze | float | 🟢 | Let it drop → decreased scan. |
| 100% capture rate | patch | — | 🔴 | Find-what-writes/reads on capture-chance float; force/branch. Advanced. |
| Crafting speed / build anywhere | patch | — | 🔴 | Find the placement-valid / craft-timer check; NOP or force. Advanced. |

## Enshrouded
Custom engine, early access, no anti-cheat.

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Infinite health/mana/stamina | freeze | float | 🟡 | Drain each → decreased scan; pointer-scan. |
| Freeze shroud timer | freeze | float | 🟡 | Enter shroud → `NextScanDecreased` on the countdown. |
| Infinite durability | freeze | float | 🟢 | Use a tool to drop durability → decreased scan. |
| XP / skill points | set | int | 🟢 | Exact-scan, gain some, `NextScanExact`. |
| Infinite resources | patch/freeze | int | 🟡 | Freeze the stack, or find-what-writes to stop the decrement. |

## The Riftbreaker
Schmetterling engine, stable v1.0 (tables rarely break).

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Resources (energy/carbonium/ironium…) | set/freeze | int/float | 🟢 | Exact-scan each resource shown in the HUD, spend, narrow. |
| Infinite health/shield/energy | freeze | float | 🟡 | Take damage → decreased scan. |
| Infinite ammo | freeze | int | 🟢 | Fire to decrement → decreased/exact. |
| Instant research / build | patch | — | 🔴 | Find-what-writes on the progress/timer value; NOP or set to max. |
| No cooldowns | patch | — | 🔴 | Find-what-writes on a cooldown float; NOP the decrement. |

## Soulmask
UE, v1.0. **EAC — offline / solo only. Never scan in an online session.**

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Infinite health/stamina/hydration/satiety | freeze | float | 🟡 | Drain each → decreased scan; UE pawn pointer chain. |
| Infinite durability | freeze | float | 🟢 | Use item → decreased scan. |
| Item stack / weight | set/freeze | int/float | 🟢 | Exact-scan the count/weight, change it, narrow. |
| Proficiency / mask level | set | int/float | 🟡 | Gain some → increased scan. |
| Enable console | patch | — | 🔴 | Advanced — flip the console-enabled bool/branch (find via the check). |

## Subnautica 2
UE5, early access. Solo/private sessions only. Requires CE 7.0+ for the community tables.

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Infinite oxygen | freeze | float | 🟢 | Dive to drain O2 → decreased scan. |
| Infinite health/food/water/energy | freeze | float | 🟡 | Drain each → decreased scan; pointer-scan. |
| No radiation / perfect temp | freeze/patch | float | 🟡 | Freeze the stat, or NOP the writer. |
| Fast scan / instant craft | patch | — | 🔴 | Find-what-writes on the scan/craft timer; NOP or max. |

## Avatar: Frontiers of Pandora
Snowdrop engine, **Denuvo** → prefer value-freeze + pointer chains; AOB patches are harder.
Storefront matters (Steam/Ubisoft/Epic offsets differ) — match your build.

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Infinite health | freeze | float | 🟡 | Take damage → decreased scan → pointer-scan (avoid code patch under Denuvo). |
| Infinite energy / breath | freeze | float | 🟢 | Drain → decreased scan. |
| Infinite ammo / arrows | freeze | int | 🟢 | Fire → decreased/exact scan. |
| Crafting materials | set/freeze | int | 🟢 | Exact-scan a material count, use some, narrow. |
| No-reload / hack-timer freeze | patch | — | 🔴 | Harder under Denuvo; try freezing the timer float first. |

## Hogwarts Legacy
UE4/5, **Denuvo**, single-player only (online has anti-cheat). Prefer freezes/pointers.

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Galleons (money) | set/freeze | int | 🟢 | Exact-scan, buy/sell, narrow. |
| Infinite health | freeze | float | 🟡 | Take damage → decreased scan → pointer-scan. |
| No spell cooldown | patch/freeze | float | 🔴 | Freeze the cooldown float at 0, or find-what-writes (Denuvo makes AOB harder). |
| Ancient magic / focus | freeze | float | 🟡 | Use it → decreased scan. |
| Inventory / resources | set | int | 🟢 | Exact-scan the count. |

## Everwind / StarRupture / Windrose (UE5, early access)
Newer/smaller titles; addresses churn every patch, so lean on unknown-value narrowing and
re-scan after updates. **Windrose: EAC — offline only.**

| Cheat | Kind | Type | Diff | Recipe |
|-------|------|------|------|--------|
| Infinite health/stamina | freeze | float | 🟡 | Drain → decreased scan; UE pawn pointer chain. |
| Infinite oxygen / hunger / temp | freeze | float | 🟢 | Let it drop → decreased scan. |
| Resources / inventory | set/freeze | int | 🟢 | Exact-scan the count, spend, narrow. |
| Super damage / god mode | patch | — | 🔴 | Find-what-writes on health (god) or enemy health (damage). |
| Movement / fly / no-clip (StarRupture) | patch | — | 🔴 | Find the movement-mode flag or NOP a gravity/collision write. Advanced. |

---

## From survivor address → authored cheat

Once a scan leaves you a stable pointer chain or AOB pattern, add it to a trainer
definition (see `src/GameCheater.Demo/SnowRunnerTrainer.cs`):

```csharp
// value-freeze (float health via pointer chain, re-resolved each tick)
t.Add(new FreezeCheat<float>(
    Resolve.Pointer(moduleBaseOffset: 0x01AB1234, 0x40, 0x18, 0x2C),
    value: 9999f, freeze: true, resolveEachTick: true)
{ Name = "Infinite Health", Category = "Player" });

// set-value (money via pointer chain; edit Value live from the UI)
t.Add(new FreezeCheat<int>(
    Resolve.Pointer(0x01CD5678, 0x10),
    value: 1_000_000, freeze: false)
{ Name = "Set Money", Category = "Economy" });

// code-patch (no-damage: NOP a 7-byte write found via find-what-writes)
t.Add(new PatchCheat(
    Resolve.Aob("F3 0F 11 ?? ?? 48 8B"), PatchCheat.Nops(7))
{ Name = "No Damage", Category = "Player" });
```

Reminder: single-player / offline only, and re-scan after any game patch.
