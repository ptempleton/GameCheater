using System.Text.Json.Serialization;

namespace GameCheater.Core.Definitions;

/// <summary>
/// The authored trainer format — the data an app ships/fetches instead of compiling cheats
/// in. One of these per game becomes a <see cref="Cheats.Trainer"/> at load time. It's plain
/// JSON so it can be embedded in the exe, hosted in the GameCheater-cheats repo, saved to
/// app-data, and emitted by the watcher.
/// </summary>
public sealed class TrainerDefinition
{
    /// <summary>Display name, e.g. "SnowRunner".</summary>
    public string Game { get; set; } = "";

    /// <summary>Target process name without ".exe", e.g. "SnowRunner".</summary>
    public string Process { get; set; } = "";

    /// <summary>Informational: which game build these signatures were found against.</summary>
    public string? GameVersion { get; set; }

    /// <summary>Bump when the definition changes so clients know to refresh.</summary>
    public int Revision { get; set; }

    public List<CheatDefinition> Cheats { get; set; } = new();
}

/// <summary>One cheat: a value freeze/set, code patch, or composite of other named cheats.</summary>
public sealed class CheatDefinition
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public string? Description { get; set; }

    /// <summary>"freeze" (value write), "patch" (code bytes), or "composite" (member toggles).</summary>
    public string Type { get; set; } = "freeze";

    /// <summary>Names of cheats toggled by a composite definition.</summary>
    public List<string>? Members { get; set; }

    // --- freeze fields ---
    /// <summary>byte | short | int | long | float | double (freeze only).</summary>
    public string? ValueType { get; set; }

    /// <summary>Initial value as a string (parsed to ValueType). Omit to freeze at the game's current value.</summary>
    public string? Value { get; set; }

    /// <summary>Re-walk the resolver every freeze tick (true for moving pointer targets).</summary>
    public bool ResolveEachTick { get; set; }

    /// <summary>Optional slider bounds (freeze only) — when both are set, the UI shows a slider.</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }

    // --- patch fields ---
    /// <summary>Hex bytes to write, e.g. "90 90 90 90 90" (patch only).</summary>
    public string? Patch { get; set; }

    /// <summary>How to locate the target address at enable time.</summary>
    public ResolveSpec Resolve { get; set; } = new();
}

/// <summary>Serializable form of an address resolver (see <see cref="Memory.Resolve"/>).</summary>
public sealed class ResolveSpec
{
    /// <summary>"pointer" | "aob" | "aobPointer" | "static".</summary>
    public string Kind { get; set; } = "pointer";

    /// <summary>Hex offset from the module base, e.g. "0x1A2B3C" (pointer/static).</summary>
    public string? ModuleOffset { get; set; }

    /// <summary>Named module for a static resolve, e.g. "Game.exe" (optional; defaults to main module).</summary>
    public string? Module { get; set; }

    /// <summary>Pointer-chain offsets in resolution order, hex, e.g. ["0x40","0x18"].</summary>
    public List<string>? Offsets { get; set; }

    /// <summary>AOB pattern, e.g. "48 8B 05 ?? ?? ?? ??" (aob/aobPointer).</summary>
    public string? Pattern { get; set; }

    /// <summary>Offset added to the AOB match (aob/aobPointer).</summary>
    public int AobOffset { get; set; }
}

/// <summary>Top-level index the client pulls first: which games exist and where their files are.</summary>
public sealed class CheatIndex
{
    public int Version { get; set; } = 1;
    public List<CheatIndexEntry> Games { get; set; } = new();
}

public sealed class CheatIndexEntry
{
    public string Game { get; set; } = "";
    public string File { get; set; } = "";       // path under games/, e.g. "games/snowrunner.json"
    public int Revision { get; set; }
    public string? GameVersion { get; set; }
}

/// <summary>Source-generated JSON context (trim/AOT-friendly, and faster).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TrainerDefinition))]
[JsonSerializable(typeof(CheatIndex))]
public partial class DefinitionJsonContext : JsonSerializerContext;
