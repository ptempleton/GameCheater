using System.ComponentModel;

namespace GameCheater.Core.Cheats;

/// <summary>
/// A master toggle over existing cheats. Enabling is transactional: if any member fails,
/// members enabled by this composite are rolled back. Disabling preserves members that were
/// already enabled before the master toggle was turned on.
/// </summary>
public sealed class CompositeCheat : Cheat
{
    private readonly List<Cheat> _enabledByComposite = new();
    private bool _changingMembers;

    public IReadOnlyList<Cheat> Members { get; }

    public CompositeCheat(IEnumerable<Cheat> members)
    {
        Members = members.Distinct().ToArray();
        if (Members.Count == 0)
            throw new ArgumentException("A composite cheat needs at least one member.", nameof(members));

        foreach (var member in Members)
            member.PropertyChanged += OnMemberPropertyChanged;
    }

    protected override void OnEnable()
    {
        _enabledByComposite.Clear();
        _changingMembers = true;
        try
        {
            foreach (var member in Members)
            {
                if (member.Enabled) continue;
                try
                {
                    member.Enable();
                    _enabledByComposite.Add(member);
                }
                catch (Exception ex)
                {
                    RollBackEnabledMembers();
                    throw new InvalidOperationException(
                        $"Member '{member.Name}' failed to enable: {ex.Message}", ex);
                }
            }
        }
        finally
        {
            _changingMembers = false;
        }
    }

    protected override void OnDisable()
    {
        _changingMembers = true;
        Exception? firstError = null;
        try
        {
            for (int i = _enabledByComposite.Count - 1; i >= 0; i--)
            {
                try { _enabledByComposite[i].Disable(); }
                catch (Exception ex) { firstError ??= ex; }
            }
            _enabledByComposite.Clear();
        }
        finally
        {
            _changingMembers = false;
        }

        if (firstError is not null)
            throw new InvalidOperationException($"A member failed to disable: {firstError.Message}", firstError);
    }

    private void RollBackEnabledMembers()
    {
        for (int i = _enabledByComposite.Count - 1; i >= 0; i--)
        {
            try { _enabledByComposite[i].Disable(); }
            catch { /* Preserve the original enable failure. */ }
        }
        _enabledByComposite.Clear();
    }

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_changingMembers || !Enabled || e.PropertyName != nameof(Enabled) ||
            sender is not Cheat { Enabled: false })
            return;

        if (Owner?.IsAttached != true)
        {
            _enabledByComposite.Clear();
            MarkDisabledExternally();
            return;
        }

        try { Disable(); }
        catch { MarkDisabledExternally(); }
    }
}
