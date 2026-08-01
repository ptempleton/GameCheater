# CLAUDE.md — working agreement for this repo

Guidance for Claude (and any contributor) working in GameCheater.

## Hard rules

1. **Never merge to `main` without the owner's (ptempleton) explicit approval.**
   - All work happens on feature branches. Open a PR; do not merge it yourself.
   - `main` is protected. Even trivial changes wait for sign-off.

2. **Everything must pass the linter/formatter before it can merge.**
   - This is a **C# / .NET** project, so the linter is **`dotnet format`**, not ruff.
     (`ruff` is Python-only and cannot parse C#. The owner originally asked for ruff out
     of habit from Python projects; we consciously substituted the C# equivalent.)
   - Gate command: `dotnet format --verify-no-changes` must succeed (CI enforces this).
   - Style is defined by `.editorconfig`; unused usings and non-file-scoped namespaces
     are treated as warnings and should be cleaned up, not suppressed.
   - If any Python helper scripts are ever added, THOSE get linted with `ruff`.

3. **Never commit third-party cheat tables (`.CT`) or trainer binaries.**
   - This project is bring-your-own-table: the user downloads tables themselves and the
     app loads them. We link to sources (e.g. FearLess Revolution threads); we do not
     rehost, bundle, or redistribute other authors' work. `.CT` files are git-ignored.

## Scope / design guardrails

- **Single-player / offline only.** Do not add features that write memory in online or
  anti-cheat-protected (EAC/BattlEye) sessions. A couple of target games (Soulmask,
  Windrose, Hogwarts online) must be flagged SP-only.
- **Resolve addresses at enable time, never store raw addresses across launches** (ASLR).
  Cheats resolve via AOB signature or pointer chain in `OnEnable`.
- **Patches must save original bytes before writing**, and teardown must restore them.

## Build / test

```bash
dotnet build -c Release            # compiles on macOS/Linux/Windows
dotnet format --verify-no-changes  # lint gate
dotnet run --project src/GameCheater.Demo   # dev harness (only attaches on Windows)
```

The core targets plain `net10.0` so it compiles anywhere; the Win32 memory APIs only
resolve at runtime on Windows.

## Releases

- Tagging `v*` triggers `.github/workflows/release.yml`, which publishes a self-contained
  `win-x64` build as a GitHub Release asset. Tags are cut by the owner.
