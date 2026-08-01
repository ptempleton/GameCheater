namespace GameCheater.Core.Cheats;

/// <summary>
/// A non-generic view over a value-write cheat's editable value, so UI code can bind a
/// single text box to any <see cref="FreezeCheat{T}"/> without knowing its element type.
/// This is the "adjust settings" surface (set money to X, set health to Y).
/// </summary>
public interface IValueCheat
{
    /// <summary>The cheat's value as a string, for two-way binding. Bad input is ignored.</summary>
    string ValueText { get; set; }
}
