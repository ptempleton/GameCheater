using System.Globalization;
using System.Text.Json;
using GameCheater.Core.Cheats;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Definitions;

/// <summary>
/// Turns a <see cref="TrainerDefinition"/> (JSON data) into a live <see cref="Trainer"/>, and
/// back. This is the bridge between the shipped/fetched cheat data and the runtime objects —
/// the single place JSON becomes FreezeCheat/PatchCheat via <see cref="Resolve"/>.
/// </summary>
public static class TrainerDefinitionLoader
{
    public static TrainerDefinition Parse(string json) =>
        JsonSerializer.Deserialize(json, DefinitionJsonContext.Default.TrainerDefinition)
            ?? throw new FormatException("Empty or invalid trainer definition JSON.");

    public static TrainerDefinition ParseFile(string path) => Parse(File.ReadAllText(path));

    public static string ToJson(TrainerDefinition def) =>
        JsonSerializer.Serialize(def, DefinitionJsonContext.Default.TrainerDefinition);

    /// <summary>Build a Trainer, skipping any cheat whose definition can't be resolved into a builder.</summary>
    public static Trainer ToTrainer(TrainerDefinition def, out List<string> skipped)
    {
        skipped = new List<string>();
        var trainer = new Trainer(def.Game, def.Process);
        foreach (var c in def.Cheats)
        {
            var cheat = TryBuild(c);
            if (cheat is not null) trainer.Add(cheat);
            else skipped.Add(c.Name);
        }
        return trainer;
    }

    public static Trainer ToTrainer(TrainerDefinition def) => ToTrainer(def, out _);

    private static Cheat? TryBuild(CheatDefinition c)
    {
        var resolver = BuildResolver(c.Resolve);
        if (resolver is null) return null;

        if (string.Equals(c.Type, "patch", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = ParseBytes(c.Patch);
            return bytes.Length == 0
                ? null
                : new PatchCheat(resolver, bytes) { Name = c.Name, Category = c.Category, Description = c.Description };
        }

        // freeze / set-value
        bool atCurrent = string.IsNullOrWhiteSpace(c.Value);
        return c.ValueType?.ToLowerInvariant() switch
        {
            "byte" => Make<byte>(),
            "short" => Make<short>(),
            "int" => Make<int>(),
            "long" => Make<long>(),
            "float" => Make<float>(),
            "double" => Make<double>(),
            _ => null,
        };

        Cheat Make<T>() where T : unmanaged
        {
            T value = default;
            if (!atCurrent)
                value = (T)Convert.ChangeType(c.Value!, typeof(T), CultureInfo.InvariantCulture);
            return new FreezeCheat<T>(resolver, value, freeze: true,
                resolveEachTick: c.ResolveEachTick, freezeAtCurrentValue: atCurrent,
                minimum: c.Min, maximum: c.Max)
            {
                Name = c.Name,
                Category = c.Category,
                Description = c.Description,
            };
        }
    }

    private static Func<ProcessMemory, ulong?>? BuildResolver(ResolveSpec r)
    {
        int[] offsets = ParseOffsets(r.Offsets);
        switch (r.Kind?.ToLowerInvariant())
        {
            case "static":
                if (!TryHexUlong(r.ModuleOffset, out var so)) return null;
                return r.Module is { Length: > 0 } module ? Resolve.Static(module, so) : Resolve.Static(so);

            case "pointer":
                if (!TryHexUlong(r.ModuleOffset, out var po)) return null;
                return Resolve.Pointer(po, offsets);

            case "aob":
                return string.IsNullOrWhiteSpace(r.Pattern) ? null : Resolve.Aob(r.Pattern, r.AobOffset);

            case "aobpointer":
                return string.IsNullOrWhiteSpace(r.Pattern)
                    ? null
                    : Resolve.Pointer(Resolve.Aob(r.Pattern, r.AobOffset), offsets);

            default:
                return null;
        }
    }

    // --- hex helpers ---

    private static bool TryHexUlong(string? s, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static int[] ParseOffsets(List<string>? offsets)
    {
        if (offsets is null || offsets.Count == 0) return Array.Empty<int>();
        var result = new List<int>(offsets.Count);
        foreach (var o in offsets)
        {
            var s = o.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                result.Add(v);
        }
        return result.ToArray();
    }

    private static byte[] ParseBytes(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        var tokens = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>(tokens.Length);
        foreach (var tk in tokens)
        {
            var s = tk.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tk[2..] : tk;
            if (byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                bytes.Add(b);
        }
        return bytes.ToArray();
    }
}
