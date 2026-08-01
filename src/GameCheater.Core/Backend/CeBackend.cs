using System.Diagnostics;

namespace GameCheater.Core.Backend;

/// <summary>One memory record from a loaded CE table (a Lua/AA cheat CE runs).</summary>
public sealed record CeRecord(int Index, string Description, bool Active);

/// <summary>
/// Drives an installed Cheat Engine as the backend for Lua/AA-scripted .CT tables — the cheats
/// our own runtime can't execute. It installs the <see cref="CeBridge"/> autorun script, launches
/// CE with the user's table, attaches to the game, and enables/disables records on command. CE
/// executes the Lua natively; we just orchestrate and surface the toggles in our UI.
///
/// Communication is file-based (see CeBridge). Windows-only at runtime (needs CE installed);
/// this class compiles anywhere but <see cref="CeLocator.Find"/> returns null off-Windows.
/// </summary>
public sealed class CeBackend
{
    public string CheatEnginePath { get; }
    public string BridgeDir { get; }
    private Process? _ce;

    public CeBackend(string cheatEnginePath, string? bridgeDir = null)
    {
        CheatEnginePath = cheatEnginePath;
        BridgeDir = bridgeDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameCheater", "ce-bridge");
    }

    /// <summary>Locate CE and construct a backend, or null if CE isn't installed.</summary>
    public static CeBackend? Locate(string? overridePath = null) =>
        CeLocator.Find(overridePath) is { } path ? new CeBackend(path) : null;

    /// <summary>Copy the bridge Lua into CE's autorun folder and prepare the IPC directory.</summary>
    public void InstallBridge()
    {
        var autorun = Path.Combine(Path.GetDirectoryName(CheatEnginePath)!, "autorun");
        Directory.CreateDirectory(autorun);
        File.WriteAllText(Path.Combine(autorun, CeBridge.BridgeFileName), CeBridge.Script);

        Directory.CreateDirectory(BridgeDir);
        foreach (var f in new[] { CeBridge.CommandFile, CeBridge.ResponseFile, CeBridge.RecordsFile, CeBridge.StatusFile })
            SafeDelete(Path.Combine(BridgeDir, f));
    }

    /// <summary>Launch Cheat Engine (optionally opening a table). The bridge starts via autorun.</summary>
    public void Launch(string? tablePath = null)
    {
        var psi = new ProcessStartInfo(CheatEnginePath) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(tablePath))
            psi.ArgumentList.Add(tablePath);
        _ce = Process.Start(psi);
    }

    public bool IsRunning => _ce is { HasExited: false };

    /// <summary>Wait until the bridge reports "ready".</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var statusPath = Path.Combine(BridgeDir, CeBridge.StatusFile);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(statusPath) && (await SafeReadAsync(statusPath)).Trim() == "ready")
                return true;
            await Task.Delay(200, ct);
        }
        return false;
    }

    public Task<string> LoadTableAsync(string tablePath, CancellationToken ct = default) => SendAsync($"LOAD {tablePath}", ct);
    public Task<string> AttachAsync(string processName, CancellationToken ct = default) => SendAsync($"ATTACH {processName}", ct);
    public Task<string> EnableAsync(int index, CancellationToken ct = default) => SendAsync($"ENABLE {index}", ct);
    public Task<string> DisableAsync(int index, CancellationToken ct = default) => SendAsync($"DISABLE {index}", ct);

    /// <summary>Ask CE for its current memory-record list.</summary>
    public async Task<IReadOnlyList<CeRecord>> ListRecordsAsync(CancellationToken ct = default)
    {
        await SendAsync("LIST", ct);
        var text = await SafeReadAsync(Path.Combine(BridgeDir, CeBridge.RecordsFile));
        var records = new List<CeRecord>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length >= 3 && int.TryParse(parts[0], out int idx))
                records.Add(new CeRecord(idx, parts[1], parts[2].Trim().Equals("true", StringComparison.OrdinalIgnoreCase)));
        }
        return records;
    }

    /// <summary>Write a command and wait for the bridge's response. Returns the response text.</summary>
    private async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        var cmdPath = Path.Combine(BridgeDir, CeBridge.CommandFile);
        var respPath = Path.Combine(BridgeDir, CeBridge.ResponseFile);
        SafeDelete(respPath);
        await File.WriteAllTextAsync(cmdPath, command, ct);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(respPath))
                return (await SafeReadAsync(respPath)).Trim();
            await Task.Delay(150, ct);
        }
        throw new TimeoutException($"Cheat Engine bridge did not respond to '{command}'.");
    }

    private static async Task<string> SafeReadAsync(string path)
    {
        // The bridge may be mid-write; retry a couple of times on sharing violations.
        for (int i = 0; i < 3; i++)
        {
            try { return await File.ReadAllTextAsync(path); }
            catch (IOException) { await Task.Delay(50); }
        }
        return "";
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
