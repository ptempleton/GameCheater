using GameCheater.Core.Cheats;
using GameCheater.Core.Memory;

namespace GameCheater.App.Models;

/// <summary>A selectable game: display name, target process, and how to build its trainer.</summary>
public sealed record GameDef(string Display, string ProcessName, Func<Trainer> Build);

/// <summary>
/// The catalog the game-picker shows. Process names are best-effort defaults — several are
/// UE5 titles whose real process is "&lt;Game&gt;-Win64-Shipping"; adjust to match your install.
/// Only SnowRunner ships example (placeholder) cheats; the rest start empty and get cheats
/// from the scanner or a loaded .CT.
/// </summary>
public static class GameCatalog
{
    public static readonly IReadOnlyList<GameDef> All = new[]
    {
        new GameDef("SnowRunner", "SnowRunner", BuildSnowRunner),
        new GameDef("Palworld", "Palworld-Win64-Shipping", () => Empty("Palworld", "Palworld-Win64-Shipping")),
        new GameDef("No Man's Sky", "NMS", () => Empty("No Man's Sky", "NMS")),
        new GameDef("Enshrouded", "enshrouded", () => Empty("Enshrouded", "enshrouded")),
        new GameDef("The Riftbreaker", "riftbreaker", () => Empty("The Riftbreaker", "riftbreaker")),
        new GameDef("Soulmask", "WS-Win64-Shipping", () => Empty("Soulmask", "WS-Win64-Shipping")),
        new GameDef("Subnautica 2", "SubnauticaZero", () => Empty("Subnautica 2", "SubnauticaZero")),
        new GameDef("Avatar: Frontiers of Pandora", "Avatar", () => Empty("Avatar: Frontiers of Pandora", "Avatar")),
        new GameDef("Hogwarts Legacy", "HogwartsLegacy", () => Empty("Hogwarts Legacy", "HogwartsLegacy")),
        new GameDef("Everwind", "Everwind", () => Empty("Everwind", "Everwind")),
        new GameDef("StarRupture", "StarRupture", () => Empty("StarRupture", "StarRupture")),
        new GameDef("Windrose", "Windrose", () => Empty("Windrose", "Windrose")),
    };

    private static Trainer Empty(string game, string proc) => new(game, proc);

    // Example authored trainer. Signatures are PLACEHOLDERS — see docs/SCAN-RECIPES.md to
    // find real ones; enabling a placeholder cheat simply fails to resolve, it can't corrupt.
    private static Trainer BuildSnowRunner()
    {
        var t = new Trainer("SnowRunner", "SnowRunner");

        t.Add(new FreezeCheat<float>(
            Resolve.Pointer(0x0, 0x0, 0x0), value: 100f, freeze: true, resolveEachTick: true)
        {
            Name = "Infinite Fuel",
            Category = "Vehicle",
            Description = "Holds the active truck's fuel at full (placeholder signature).",
        });

        t.Add(new FreezeCheat<int>(
            Resolve.Pointer(0x2A8EDD8, 0x8, 0x150, 0x38),
            value: 0,
            freeze: true,
            resolveEachTick: true)
        {
            Name = "No Engine Damage",
            Category = "Vehicle",
            Description = "Keeps the active truck's accumulated engine damage at zero.",
        });

        t.Add(new FreezeCheat<int>(
            Resolve.Pointer(0x2A8EDD8, 0x8, 0x148, 0x38),
            value: 0,
            freeze: true,
            resolveEachTick: true)
        {
            Name = "No Transmission Damage",
            Category = "Vehicle",
            Description = "Keeps the active truck's accumulated transmission damage at zero.",
        });

        t.Add(new FreezeCheat<int>(
            Resolve.Pointer(0x2A8EDD8, 0x8, 0x158, 0x38),
            value: 0,
            freeze: true,
            resolveEachTick: true)
        {
            Name = "No Fuel Tank Damage",
            Category = "Vehicle",
            Description = "Keeps the active truck's accumulated fuel-tank damage at zero.",
        });

        t.Add(new FreezeCheat<int>(
            Resolve.Pointer(0x2A8EDD8, 0x8, 0x160, 0x38),
            value: 0,
            freeze: true,
            resolveEachTick: true)
        {
            Name = "No Suspension Damage",
            Category = "Vehicle",
            Description = "Keeps the active truck's accumulated suspension damage at zero.",
        });

        t.Add(new FreezeCheat<int>(
            Resolve.Pointer(0x0, 0x0), value: 9999, freeze: true, resolveEachTick: true,
            minimum: 0, maximum: 9999)
        {
            Name = "Set Repair Points",
            Category = "Vehicle",
            Description = "Freezes repair points — drag the slider to set the amount.",
        });

        return t;
    }
}
