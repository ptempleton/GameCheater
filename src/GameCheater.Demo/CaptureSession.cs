using GameCheater.Core.Definitions;
using GameCheater.Core.Memory;

namespace GameCheater.Demo;

/// <summary>
/// Accumulates cheats captured during a watch session into a <see cref="TrainerDefinition"/>
/// — naming/describing each one and determining which game it's for — so the result drops
/// straight into a games/&lt;game&gt;.json in the cheats repo.
/// </summary>
public sealed class CaptureSession
{
    private readonly TrainerDefinition _def;

    public string Game => _def.Game;
    public int Count => _def.Cheats.Count;

    private CaptureSession(TrainerDefinition def) => _def = def;

    /// <summary>
    /// Start a session. The game defaults to the process name (no interactive prompt, no
    /// MainModule/version-info access — that could block on a live game). Pass
    /// <paramref name="gameOverride"/> to set a nicer name; it can also be edited in the JSON.
    /// </summary>
    public static CaptureSession Begin(ProcessMemory mem, string? gameOverride = null)
    {
        string game = string.IsNullOrWhiteSpace(gameOverride) ? mem.Process.ProcessName : gameOverride.Trim();
        Console.WriteLine($"Capturing cheats for: {game}   (pass a game name as the last arg to rename)\n");
        return new CaptureSession(new TrainerDefinition
        {
            Game = game,
            Process = mem.Process.ProcessName,
        });
    }

    public void AddPatch(string name, string category, string? description,
        string aobPattern, int aobOffset, byte[] patch) =>
        _def.Cheats.Add(new CheatDefinition
        {
            Name = name,
            Category = category,
            Description = description,
            Type = "patch",
            Patch = Signature.ToPattern(patch),
            Resolve = new ResolveSpec { Kind = "aob", Pattern = aobPattern, AobOffset = aobOffset },
        });

    /// <summary>
    /// Capture a value cheat at a found address. If the address lives in a module we emit a
    /// durable module+offset static resolve; if it's a heap address we emit a clearly-marked
    /// DRAFT (it needs a pointer scan to survive a relaunch).
    /// </summary>
    public void AddValue(string name, string category, string? description,
        string valueType, ProcessMemory mem, ulong address)
    {
        ResolveSpec spec;
        string? note = description;

        if (mem.TryGetModuleContaining(address, out var module, out var offset))
        {
            spec = new ResolveSpec { Kind = "static", Module = module, ModuleOffset = "0x" + offset.ToString("X") };
        }
        else
        {
            spec = new ResolveSpec { Kind = "pointer", ModuleOffset = "0x0", Offsets = new List<string>() };
            note = $"DRAFT — found at absolute 0x{address:X} (heap); needs a pointer scan to be durable. {description}".Trim();
        }

        _def.Cheats.Add(new CheatDefinition
        {
            Name = name,
            Category = category,
            Description = note,
            Type = "freeze",
            ValueType = valueType,
            ResolveEachTick = spec.Kind == "pointer",
            Resolve = spec,
        });
    }

    public string ToJson() => TrainerDefinitionLoader.ToJson(_def);

    public void Write(string path) => File.WriteAllText(path, ToJson());

    /// <summary>Prompt for a cheat's name/category/description. Null = user skipped it.</summary>
    public static (string Name, string Category, string? Description)? Prompt()
    {
        Console.Write("  name (blank to skip): ");
        var name = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        Console.Write("  category [General]: ");
        var category = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(category)) category = "General";

        Console.Write("  description (optional): ");
        var description = Console.ReadLine()?.Trim();

        return (name, category, string.IsNullOrEmpty(description) ? null : description);
    }
}
