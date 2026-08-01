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
   lists.** A static-only loader will fail on nearly all of them. Full support needs an
   embedded Lua interpreter + Cheat Engine auto-assembler emulation (roadmap **v4**).

## Redistribution norms (respect these)

FRF has **no blanket "free to redistribute" grant** — the table **author** retains control.
Its rules require crediting the author and keeping discussion in the original thread; some
authors add explicit "do not repost" notices per post. **So: link users to the source
thread; never mirror or bundle the `.CT`.** Nexus-hosted copies fall under Nexus's own
terms. Closed trainers (FLiNG/etc.) forbid redistribution outright.

## Per-game sources (FRF = loadable `.CT`)

| Game | Loadable `.CT` source (FearLess Revolution) | Scripting | Maintained | Flags |
|------|---------------------------------------------|-----------|-----------|-------|
| SnowRunner | viewtopic.php?t=12273 (main), t=27338 | Lua/AOB | Active | Money is save/server-side; tables lean on fuel/repair/time |
| Soulmask | viewtopic.php?t=29494, t=29532 | Lua | Active (v1.0) | **EAC** — offline/solo only |
| Everwind | viewtopic.php?t=36910, t=38651 | Lua | Active (EA-fragile) | EA address churn |
| Subnautica 2 | viewtopic.php?t=39388, t=39420 | Lua (AOB confirmed) | Very active | Solo/private sessions only; needs CE 7.0+ |
| Enshrouded | viewtopic.php?t=27468, t=27429 | Lua | Long-running | Nexus copy = Nexus terms |
| The Riftbreaker | viewtopic.php?t=27054, t=24283 | Lua | Low cadence (stable game) | — |
| StarRupture | viewtopic.php?t=37895 | Lua | Active (EA) | Breaks per EA patch |
| No Man's Sky | viewtopic.php?t=30442 (YoucefHam) | Lua-heavy | Very active | Attribute author; back up saves |
| Avatar: Frontiers of Pandora | t=30885 / t=37148 (Steam), t=26779 (Ubisoft), t=27283 (Epic) | Lua | Was active, now stable | **Storefront-specific** — match your build |
| Hogwarts Legacy | t=28682; guidedhacking.com/resources/…1141 | Lua | Low frequency | SP only (online has anti-cheat) |
| Windrose | viewtopic.php?t=38345, t=39873 | Lua | Community (EA) | **EAC** — CE reportedly works offline only |
| Palworld | viewtopic.php?t=35666 | Lua (prompts to allow Lua) | Very active | Works Steam/Epic/MS Store |

All URLs are on `https://fearlessrevolution.com/` unless noted. Verify the thread matches
your game version before loading; tables for live-service/EA titles break on patches.

## Implication for GameCheater

Because the loadable tables are overwhelmingly Lua-scripted, the **authored-trainer path is
the primary product** (bounded, fully ours, no redistribution questions), and CT-loading is
a deliberately-scoped later bet: static entries first (v3), then incremental Lua/AA support
(v4) covering the common CE API calls real tables use.
