using Gagamba.Execution;
using Penghou.Fuwen;

namespace Penghou.Hufu.Fuwen;

/// <summary>
/// Translates the neutral Fuwen execution-guarantee vocabulary to concrete
/// provider requirements through an explicit, allow-listed table. Unknown
/// capability identifiers or levels fail closed; a plan can never widen the
/// usable vocabulary.
/// </summary>
public interface INeutralGuaranteeMap
{
    bool TryMap(IReadOnlyList<ExecutionGuarantee> guarantees,
        out IReadOnlyList<ExecutionRequirement> requirements, out string error);
}

public sealed class NeutralGuaranteeMap : INeutralGuaranteeMap
{
    private readonly Dictionary<string, ExecutionCapability> _capabilities;
    private static readonly Dictionary<ExecutionGuaranteeLevel, CapabilityLevel> Levels = new()
    {
        [ExecutionGuaranteeLevel.Partial] = CapabilityLevel.Partial,
        [ExecutionGuaranteeLevel.Full] = CapabilityLevel.Full,
    };

    public NeutralGuaranteeMap(IReadOnlyDictionary<string, ExecutionCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = new Dictionary<string, ExecutionCapability>(capabilities, StringComparer.Ordinal);
    }

    /// <summary>The FZ-1 default vocabulary. Native requirements only; no constructed grants.</summary>
    public static NeutralGuaranteeMap Default { get; } = new(new Dictionary<string, ExecutionCapability>(StringComparer.Ordinal)
    {
        ["execution.unit-termination"] = ExecutionCapability.UnitTermination,
        ["execution.root-exit-independence"] = ExecutionCapability.SurvivesRootExit,
        ["execution.owner-death-cleanup"] = ExecutionCapability.OwnerDeathCleanup,
        ["execution.recursive-membership"] = ExecutionCapability.RecursiveMembership,
        ["execution.escape-resistance"] = ExecutionCapability.EscapeResistant,
        ["execution.kernel-owned-lifecycle"] = ExecutionCapability.KernelOwnedLifecycle,
    });

    public bool TryMap(IReadOnlyList<ExecutionGuarantee> guarantees,
        out IReadOnlyList<ExecutionRequirement> requirements, out string error)
    {
        ArgumentNullException.ThrowIfNull(guarantees);
        var mapped = new List<ExecutionRequirement>(guarantees.Count);
        foreach (var guarantee in guarantees)
        {
            if (guarantee is null || !_capabilities.TryGetValue(guarantee.Capability, out var capability))
            {
                requirements = Array.Empty<ExecutionRequirement>();
                error = $"Unknown execution guarantee '{guarantee?.Capability}'.";
                return false;
            }
            if (!Levels.TryGetValue(guarantee.Minimum, out var level))
            {
                requirements = Array.Empty<ExecutionRequirement>();
                error = $"Unknown guarantee level for '{guarantee.Capability}'.";
                return false;
            }
            mapped.Add(ExecutionRequirement.Require(capability, level, allowConstructed: false));
        }
        requirements = mapped;
        error = "";
        return true;
    }
}
