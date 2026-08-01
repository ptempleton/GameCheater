using System.Globalization;
using System.Text.RegularExpressions;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Tables;

/// <summary>
/// Turns a Cheat Engine address expression into an address resolver our runtime can use.
/// Handles the common, ASLR-safe forms; anything more exotic (registered symbols, nested
/// bracket math, arithmetic across modules) returns null so the entry is left for the CE
/// backend instead of being resolved wrongly.
///
/// Handled:
///   "Game.exe"+1A2B3C   → moduleBase("Game.exe") + 0x1A2B3C
///   Game.exe+1A2B3C     → same, quotes optional
///   "Game.exe"          → moduleBase, offset 0
///   7FF6ABCD1234        → absolute address (NOT relaunch-safe, but honored as written)
/// </summary>
public static partial class CtAddress
{
    [GeneratedRegex(@"^""?(?<mod>[\w.\-]+\.(?:exe|dll|bin))""?(?:\s*\+\s*(?<off>[0-9A-Fa-f]+))?$")]
    private static partial Regex ModulePlusOffset();

    [GeneratedRegex(@"^(?:0x)?(?<hex>[0-9A-Fa-f]{6,})$")]
    private static partial Regex BareHex();

    /// <summary>Build a base-address resolver, or null if the expression is too complex to handle ourselves.</summary>
    public static Func<ProcessMemory, ulong?>? BuildResolver(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var a = address.Trim();

        var mod = ModulePlusOffset().Match(a);
        if (mod.Success)
        {
            string module = mod.Groups["mod"].Value;
            ulong offset = mod.Groups["off"].Success
                ? ulong.Parse(mod.Groups["off"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : 0;
            return mem => mem.GetModuleBase(module) is { } b ? b + offset : null;
        }

        var hex = BareHex().Match(a);
        if (hex.Success
            && ulong.TryParse(hex.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var abs))
        {
            return _ => abs; // absolute; last resort, not ASLR-safe
        }

        return null; // complex expression — leave it to the CE backend
    }
}
