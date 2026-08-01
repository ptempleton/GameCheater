using System.Text.Json;
using GameCheater.Core.Definitions;

namespace GameCheater.Core.Distribution;

/// <summary>Outcome of a refresh: the definitions, whether they came from cache, and any error.</summary>
public sealed class CheatRefreshResult
{
    public IReadOnlyList<TrainerDefinition> Definitions { get; init; } = Array.Empty<TrainerDefinition>();
    public bool FromCache { get; init; }
    public string? Error { get; init; }
    public int Count => Definitions.Count;
}

/// <summary>
/// Pulls authored trainer definitions from the GameCheater-cheats repo and caches them in
/// app-data, so the app can update its cheats without an app release (and refresh live). On
/// a network failure it falls back to the cache, so it also works offline after a first pull.
/// </summary>
public sealed class CheatRepositoryClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Raw base URL of the cheats repo (trailing slash).</summary>
    public string BaseUrl { get; init; } =
        "https://raw.githubusercontent.com/ptempleton/GameCheater-cheats/main/";

    /// <summary>Local cache directory (default: %AppData%/GameCheater/cheats).</summary>
    public string CacheDir { get; }

    public CheatRepositoryClient(string? cacheDir = null)
    {
        CacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameCheater", "cheats");
        Directory.CreateDirectory(CacheDir);
    }

    /// <summary>Fetch the index and every game file, caching each. Falls back to cache on failure.</summary>
    public async Task<CheatRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var indexJson = await Http.GetStringAsync(BaseUrl + "index.json", ct);
            var index = JsonSerializer.Deserialize(indexJson, DefinitionJsonContext.Default.CheatIndex)
                        ?? new CheatIndex();
            File.WriteAllText(Path.Combine(CacheDir, "index.json"), indexJson);

            var defs = new List<TrainerDefinition>();
            foreach (var entry in index.Games)
            {
                try
                {
                    var gameJson = await Http.GetStringAsync(BaseUrl + entry.File, ct);
                    defs.Add(TrainerDefinitionLoader.Parse(gameJson));

                    var localPath = Path.Combine(CacheDir, entry.File.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                    File.WriteAllText(localPath, gameJson);
                }
                catch
                {
                    // One bad/unavailable game file shouldn't abort the whole refresh.
                }
            }

            return new CheatRefreshResult { Definitions = defs };
        }
        catch (Exception ex)
        {
            // Offline or repo unreachable — serve whatever we cached last time.
            return new CheatRefreshResult { Definitions = LoadCached(), FromCache = true, Error = ex.Message };
        }
    }

    /// <summary>Load definitions previously cached in app-data (used offline and at startup).</summary>
    public IReadOnlyList<TrainerDefinition> LoadCached()
    {
        var defs = new List<TrainerDefinition>();
        var gamesDir = Path.Combine(CacheDir, "games");
        if (!Directory.Exists(gamesDir)) return defs;

        foreach (var file in Directory.EnumerateFiles(gamesDir, "*.json"))
        {
            try { defs.Add(TrainerDefinitionLoader.ParseFile(file)); }
            catch { /* skip a corrupt cache file */ }
        }
        return defs;
    }
}
