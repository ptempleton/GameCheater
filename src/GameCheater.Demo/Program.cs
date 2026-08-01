using GameCheater.Core.Backend;
using GameCheater.Core.Cheats;
using GameCheater.Core.Definitions;
using GameCheater.Core.Distribution;
using GameCheater.Core.Memory;
using GameCheater.Core.Tables;
using GameCheater.Demo;

// Minimal console harness proving the vertical slice: build a trainer definition,
// attach to the running game, and toggle cheats from the keyboard. This is v0/v1 —
// the polished game-picker + checkbox UI (Avalonia) sits on top of this same runtime.

// Render UTF-8 so dashes/arrows don't garble in the Windows console.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* output redirected */ }

// Table inspection mode: `--ct <path>` parses a .CT and prints the classification report
// (who runs each entry — our engine vs the CE backend). Works on any OS (no attach).
if (args is ["--ct", var ctPath, ..])
{
    Console.WriteLine($"Parsing table: {ctPath}\n");
    var parsed = CtParser.ParseFile(ctPath);
    var report = CtParser.Summarize(parsed);

    foreach (var e in parsed.Flatten())
    {
        string indent = e.Kind == CtEntryKind.Group ? "" : "   ";
        string tag = e.Kind switch
        {
            CtEntryKind.Group => "[group]",
            CtEntryKind.Value => e.IsPointer ? "[value/ptr → our engine]" : "[value → our engine]",
            CtEntryKind.Script => "[script → CE backend]",
            _ => "[unsupported]",
        };
        Console.WriteLine($"{indent}{e.Description,-38} {tag}");
    }

    Console.WriteLine($"\n{report}");
    if (report.ScriptEntries.Count > 0)
        Console.WriteLine($"CE-backed: {string.Join(", ", report.ScriptEntries)}");

    var loaded = CtLoader.Build(parsed);
    Console.WriteLine($"\nRunnable cheats built: {loaded.Cheats.Count}");
    foreach (var c in loaded.Cheats)
        Console.WriteLine($"   • [{c.Category}] {c.Name}  ({c.Description})");
    if (loaded.Unconverted.Count > 0)
        Console.WriteLine($"Unconverted: {string.Join(", ", loaded.Unconverted)}");
    return;
}

// CE backend: `--ce <table.CT> <process>` — drive an installed Cheat Engine to run a
// Lua/AA table and toggle its records from here (Windows + Cheat Engine required).
if (args is ["--ce", var cetbl, var ceproc, ..])
{
    var be = CeBackend.Locate();
    if (be is null) { Console.WriteLine("Cheat Engine not found (install it, or this isn't Windows)."); return; }
    Console.WriteLine($"Cheat Engine: {be.CheatEnginePath}");

    be.InstallBridge();
    Console.WriteLine("Launching Cheat Engine with the table + bridge…");
    be.Launch(cetbl);

    if (!await be.WaitReadyAsync(TimeSpan.FromSeconds(60)))
    {
        Console.WriteLine("Bridge didn't become ready — is the autorun script allowed to run in CE?");
        return;
    }

    await be.LoadTableAsync(cetbl);
    Console.WriteLine($"attach: {await be.AttachAsync(ceproc)}");

    var records = await be.ListRecordsAsync();
    Console.WriteLine($"\n{records.Count} record(s):");
    foreach (var r in records)
        Console.WriteLine($"   [{r.Index}] {(r.Active ? "x" : " ")} {r.Description}");

    Console.WriteLine("\nCommands: 'e <i>' enable, 'd <i>' disable, 'list', 'q' quit");
    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine()?.Trim();
        if (line is null or "q") break;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["e", var ei] && int.TryParse(ei, out int e)) Console.WriteLine(await be.EnableAsync(e));
        else if (parts is ["d", var di] && int.TryParse(di, out int d)) Console.WriteLine(await be.DisableAsync(d));
        else if (line == "list")
            foreach (var r in await be.ListRecordsAsync())
                Console.WriteLine($"   [{r.Index}] {(r.Active ? "x" : " ")} {r.Description}");
    }
    return;
}

// Pull from the cheats repo: `--pull` — exercises the client fetch/cache (needs network).
if (args is ["--pull", ..])
{
    var client = new CheatRepositoryClient();
    var result = await client.RefreshAsync();
    Console.WriteLine($"Fetched {result.Count} definition(s)  (fromCache={result.FromCache})" +
                      $"{(result.Error is null ? "" : $"  error={result.Error}")}");
    foreach (var d in result.Definitions)
        Console.WriteLine($"   • {d.Game}  [{d.Process}.exe]  — {d.Cheats.Count} cheats");
    Console.WriteLine($"cache: {client.CacheDir}");
    return;
}

// Load/verify an authored definition: `--load-json <path>` (works on any OS, no attach).
if (args is ["--load-json", var jpath, ..])
{
    var def = TrainerDefinitionLoader.ParseFile(jpath);
    using var loaded = TrainerDefinitionLoader.ToTrainer(def, out var skipped);
    Console.WriteLine($"{def.Game}  (proc {def.Process}.exe" +
                      $"{(def.GameVersion is null ? "" : $", v{def.GameVersion}")}) — {loaded.Cheats.Count} cheats:");
    foreach (var c in loaded.Cheats)
        Console.WriteLine($"   • [{c.Category}] {c.Name}");
    if (skipped.Count > 0)
        Console.WriteLine($"skipped (unresolvable): {string.Join(", ", skipped)}");
    return;
}

// Value-watch oracle: `--watch-values <process> <int|float|long|short|byte|double>`
// Interactive scan session — find gold / time / item counts by narrowing after each change,
// then 'save' to name and capture them into a game definition.
if (args is ["--watch-values", var vproc, var vtype, ..])
{
    using var mem = ProcessMemory.Attach(vproc);
    if (mem is null) { Console.WriteLine($"{vproc} is not running (Windows only)."); return; }
    Console.WriteLine($"Attached to {vproc}.exe — value watch ({vtype}).\n");
    var session = CaptureSession.Begin(mem, args.Length > 3 ? args[3] : null);
    ValueWatch.Run(mem, vtype, session);
    WriteCaptures(session);
    return;
}

// Code-patch oracle: `--watch-code <process>` — interactive loop: toggle a code cheat in
// your trainer, 'c' to capture what changed, name it, repeat for the next cheat.
if (args is ["--watch-code", var cproc, ..])
{
    using var mem = ProcessMemory.Attach(cproc);
    if (mem is null) { Console.WriteLine($"{cproc} is not running (Windows only)."); return; }
    Console.WriteLine($"Attached to {cproc}.exe.");
    var session = CaptureSession.Begin(mem, args.Length > 2 ? args[2] : null);
    CodeWatch.Run(mem, session);
    WriteCaptures(session);
    return;
}

// Write a capture session to a games/<game>.json-ready file and echo the JSON.
static void WriteCaptures(CaptureSession session)
{
    if (session.Count == 0) { Console.WriteLine("\nNothing captured."); return; }
    string slug = Slug(session.Game);
    string file = $"captured-{slug}.json";
    session.Write(file);
    Console.WriteLine($"\nWrote {session.Count} cheat(s) to {file}:\n");
    Console.WriteLine(session.ToJson());
    Console.WriteLine($"\n→ add to the cheats repo as games/{slug}.json (or merge into the existing file).");
}

static string Slug(string s)
{
    var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
    return new string(chars).Trim('-');
}

Console.WriteLine("GameCheater — dev harness");
Console.WriteLine("=========================\n");

using Trainer trainer = SnowRunnerTrainer.Build();

Console.WriteLine($"Trainer: {trainer.Game}  (process: {trainer.ProcessName}.exe)");
Console.WriteLine($"Cheats defined: {trainer.Cheats.Count}\n");

foreach (var (cheat, i) in trainer.Cheats.Select((c, i) => (c, i)))
    Console.WriteLine($"  [{i + 1}] {cheat.Category,-8} {cheat.Name}  —  {cheat.Description}");

Console.WriteLine("\nAttaching...");
if (!trainer.Attach())
{
    Console.WriteLine($"  {trainer.ProcessName}.exe is not running. Start the game and re-run.");
    Console.WriteLine("  (On macOS this always fails — the Win32 memory APIs only resolve on Windows.)");
    return;
}

Console.WriteLine("  Attached. Press a cheat number to toggle it, Q to quit.\n");

while (true)
{
    var key = Console.ReadKey(intercept: true);
    if (key.Key is ConsoleKey.Q) break;

    if (char.IsDigit(key.KeyChar))
    {
        int idx = key.KeyChar - '1';
        if (idx >= 0 && idx < trainer.Cheats.Count)
        {
            var cheat = trainer.Cheats[idx];
            try
            {
                cheat.Toggle();
                Console.WriteLine($"  {cheat.Name}: {(cheat.Enabled ? "ON" : "off")}");
            }
            catch (Exception ex)
            {
                // Placeholder signatures won't resolve — that's expected until you fill them in.
                Console.WriteLine($"  {cheat.Name}: could not enable — {ex.Message}");
            }
        }
    }
}

// `using` on the trainer restores every patched byte and releases the handle on exit.
Console.WriteLine("\nDisabling all cheats and detaching...");
