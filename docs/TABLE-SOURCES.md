# Table sourcing reference (bring-your-own-table)

Where to legitimately **download** Cheat Engine `.CT` tables for the target games, so you
can load them through GameCheater's CT loader. GameCheater does **not** rehost or bundle
these — you download them yourself and point the app at your local copy. This file is a
curated link list, not redistributed content.

## Two facts that shape the whole loader

1. **Only FearLess Revolution (FRF)** — and occasionally GuidedHacking / Nexus — distribute
   actual `.CT` files a table loader can open. **FLiNG, Cheat Happens, PLITCH/MegaDev
   ship closed `.exe`/injector trainers** that a `.CT` loader cannot consume. They're listed
   only as "where legit cheats also exist," not as loader inputs.
2. **Almost every table for these games is Lua-scripted / AOB-based, not static pointer
   lists.** Our built-in loader converts the static/pointer entries; Lua/AA entries are handled
   only through the optional Cheat Engine backend (below).

## CT support matrix (what actually runs)

| Entry kind | How GameCheater handles it | Needs external CE? |
|------------|----------------------------|--------------------|
| Static / pointer-chain values (`Core/Tables/CtLoader`) | **Converted to built-in cheats** and run by our own engine | No |
| Lua / auto-assembler (AOB) scripts | **Classified** by the parser, then executed via the optional **Cheat Engine backend** (`Core/Backend`) — it drives an installed CE to run the script | Yes (installed Cheat Engine) |
| Embedded Lua / AA execution inside GameCheater | **Not implemented** — no embedded interpreter or AA emulator | — |

The owner's preference is to *not* require Cheat Engine, so the CE-backed path is a fallback for
Lua/AA tables, not the primary product.

## Redistribution norms (respect these)

FRF has **no blanket "free to redistribute" grant** — the table **author** retains control.
Its rules require crediting the author and keeping discussion in the original thread; some
authors add explicit "do not repost" notices per post. **So: link users to the source
thread; never mirror or bundle the `.CT`.** Nexus-hosted copies fall under Nexus's own
terms. Closed trainers (FLiNG/etc.) forbid redistribution outright.

## Per-game sources (FRF = loadable `.CT`)

Links go to FearLess Revolution threads. **"Maintained" reflects status as last compiled
2026-08; re-verify the thread before relying on it** — tables for live-service/EA titles break on
patches, and thread activity changes.

| Game | Loadable `.CT` source | Scripting | Maintained (as of 2026-08) | Flags |
|------|-----------------------|-----------|----------------------------|-------|
| SnowRunner | [t12273](https://fearlessrevolution.com/viewtopic.php?t=12273), [t27338](https://fearlessrevolution.com/viewtopic.php?t=27338) | Lua/AOB | Active | Money is save/server-side; tables lean on fuel/repair/time |
| Soulmask | [t29494](https://fearlessrevolution.com/viewtopic.php?t=29494), [t29532](https://fearlessrevolution.com/viewtopic.php?t=29532) | Lua | Active (v1.0) | **EAC** — offline/solo only |
| Everwind | [t36910](https://fearlessrevolution.com/viewtopic.php?t=36910), [t38651](https://fearlessrevolution.com/viewtopic.php?t=38651) | Lua | Active (EA-fragile) | EA address churn |
| Subnautica 2 | [t39388](https://fearlessrevolution.com/viewtopic.php?t=39388), [t39420](https://fearlessrevolution.com/viewtopic.php?t=39420) | Lua (AOB confirmed) | Very active | Solo/private sessions only; needs CE 7.0+ |
| Enshrouded | [t27468](https://fearlessrevolution.com/viewtopic.php?t=27468), [t27429](https://fearlessrevolution.com/viewtopic.php?t=27429) | Lua | Long-running | Nexus copy = Nexus terms |
| The Riftbreaker | [t27054](https://fearlessrevolution.com/viewtopic.php?t=27054), [t24283](https://fearlessrevolution.com/viewtopic.php?t=24283) | Lua | Low cadence (stable game) | — |
| StarRupture | [t37895](https://fearlessrevolution.com/viewtopic.php?t=37895) | Lua | Active (EA) | Breaks per EA patch |
| No Man's Sky | [t30442](https://fearlessrevolution.com/viewtopic.php?t=30442) (YoucefHam) | Lua-heavy | Very active | Attribute author; back up saves |
| Avatar: Frontiers of Pandora | [t30885](https://fearlessrevolution.com/viewtopic.php?t=30885) / [t37148](https://fearlessrevolution.com/viewtopic.php?t=37148) (Steam), [t26779](https://fearlessrevolution.com/viewtopic.php?t=26779) (Ubisoft), [t27283](https://fearlessrevolution.com/viewtopic.php?t=27283) (Epic) | Lua | Was active, now stable | **Storefront-specific** — match your build |
| Hogwarts Legacy | [t28682](https://fearlessrevolution.com/viewtopic.php?t=28682); [GuidedHacking 1141](https://guidedhacking.com/resources/1141) | Lua | Low frequency | SP only (online has anti-cheat) |
| Windrose | [t38345](https://fearlessrevolution.com/viewtopic.php?t=38345), [t39873](https://fearlessrevolution.com/viewtopic.php?t=39873) | Lua | Community (EA) | **EAC** — CE reportedly works offline only |
| Palworld | [t35666](https://fearlessrevolution.com/viewtopic.php?t=35666) | Lua (prompts to allow Lua) | Very active | Works Steam/Epic/MS Store |

## Implication for GameCheater

Because the loadable tables are overwhelmingly Lua-scripted, the **authored-trainer path is the
primary product** (bounded, fully ours, no redistribution questions). CT support is scoped
accordingly: **static/pointer entries load and run built-in today**; **Lua/AA entries require the
optional Cheat Engine backend**; an embedded Lua/AA interpreter is intentionally out of scope.
