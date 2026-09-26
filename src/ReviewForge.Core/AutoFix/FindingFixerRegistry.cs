namespace ReviewForge.Core.AutoFix;

/// <summary>Rule-id → fixer lookup. Duplicate rule ids are a startup error, not a
/// last-registration-wins race.</summary>
public sealed class FindingFixerRegistry
{
    private readonly Dictionary<string, IFindingFixer> _Fixers;

    public FindingFixerRegistry(IEnumerable<IFindingFixer> fixers)
    {
        _Fixers = new Dictionary<string, IFindingFixer>(StringComparer.Ordinal);
        foreach (var fixer in fixers)
        {
            if (!_Fixers.TryAdd(fixer.RuleId, fixer))
            {
                throw new InvalidOperationException(
                    $"duplicate finding fixer for rule '{fixer.RuleId}' — rule ids must be unique");
            }
        }
    }

    public bool TryGet(string ruleId, out IFindingFixer fixer)
        => _Fixers.TryGetValue(ruleId, out fixer!);

    public IReadOnlyCollection<string> RegisteredRuleIds => _Fixers.Keys.ToArray();
}
