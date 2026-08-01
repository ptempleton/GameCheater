using GameCheater.Core.Cheats;
using GameCheater.Core.Memory;

namespace GameCheater.Demo;

/// <summary>
/// An example authored trainer definition. This is your OWN format — the bounded,
/// fully-yours path (no CT loading, no redistribution). Adding a cheat is editing this
/// file, not the engine.
///
/// IMPORTANT: every signature / offset below is a PLACEHOLDER. There are no real working
/// SnowRunner addresses baked in, and there can't be — you find them live with the
/// ValueScanner (see the recipes doc), then paste the resulting AOB pattern or pointer
/// chain here. Enabling a cheat with a placeholder signature will simply fail to resolve
/// (by design) rather than corrupt the game.
/// </summary>
public static class SnowRunnerTrainer
{
    public static Trainer Build()
    {
        var t = new Trainer(game: "SnowRunner", processName: "SnowRunner");

        // --- Value-write cheats (freeze) ---------------------------------------------

        // Infinite fuel: hold the fuel float at its current tank max.
        // Replace the pointer chain with one you find via a float scan + pointer scan.
        t.Add(new FreezeCheat<float>(
            Resolve.Pointer(moduleBaseOffset: 0x0 /*PLACEHOLDER*/, 0x0, 0x0),
            value: 100f, freeze: true, resolveEachTick: true)
        {
            Name = "Infinite Fuel",
            Category = "Vehicle",
            Description = "Holds the active truck's fuel at full.",
        });

        // Infinite repair points: hold as an int. Editable Value = the 'set to X' case.
        t.Add(new FreezeCheat<int>(
            Resolve.Pointer(0x0 /*PLACEHOLDER*/, 0x0),
            value: 9999, freeze: true, resolveEachTick: true)
        {
            Name = "Infinite Repair Points",
            Category = "Vehicle",
            Description = "Freezes repair points; edit Value to set the amount.",
        });

        // --- Code-patch cheat --------------------------------------------------------

        // No vehicle damage: NOP the instruction that writes decremented damage.
        // Find it with CE's "find what writes to this address" on the damage value,
        // then paste the surrounding byte pattern here and set the NOP length to the
        // instruction's byte length.
        t.Add(new PatchCheat(
            Resolve.Aob("48 8B 05 ?? ?? ?? ?? /*PLACEHOLDER pattern*/"),
            PatchCheat.Nops(7))
        {
            Name = "No Vehicle Damage",
            Category = "Vehicle",
            Description = "NOPs the instruction that applies vehicle damage.",
        });

        return t;
    }
}
