namespace GameCheater.Core.Backend;

/// <summary>
/// Finds an installed Cheat Engine on disk (we detect it, we don't bundle it — CE is GPL and
/// the user installs it themselves). Filesystem-based so it compiles cross-platform; registry
/// lookup can be layered on later behind an OS guard.
/// </summary>
public static class CeLocator
{
    private static readonly string[] ExeNames =
    {
        "cheatengine-x86_64.exe",
        "cheatengine-i386.exe",
        "Cheat Engine.exe",
    };

    /// <summary>Return the path to a Cheat Engine executable, or null if none found.</summary>
    public static string? Find(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (var root in ProgramFilesRoots())
        {
            // CE installs into a versioned folder, e.g. "Cheat Engine 7.5".
            foreach (var dir in SafeEnumerate(root, "Cheat Engine*"))
            {
                var hit = FirstExeIn(dir);
                if (hit is not null) return hit;
            }
            // Also check the root itself just in case.
            var direct = FirstExeIn(root);
            if (direct is not null) return direct;
        }
        return null;
    }

    private static string? FirstExeIn(string dir)
    {
        foreach (var name in ExeNames)
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        foreach (var var in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var v = Environment.GetEnvironmentVariable(var);
            if (!string.IsNullOrWhiteSpace(v) && Directory.Exists(v))
                yield return v;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try { return Directory.EnumerateDirectories(root, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
