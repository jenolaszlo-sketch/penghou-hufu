namespace Penghou.Hufu;

/// <summary>
/// Typed outcome of evaluating the exact requested operation against the
/// current restrictions of the authority lineage that contributed to a
/// derived child. Positive results never substitute for the child's own
/// Permit; negative results never downgrade into unattributed denials.
/// </summary>
public enum CurrentParentPolicyVerdict
{
    /// <summary>The single bound root parent currently permits the exact operation.</summary>
    Permit = 0,
    /// <summary>A live required parent denies the exact operation under current policy.</summary>
    ParentDenied = 1,
    /// <summary>The required parent is missing, expired, revoked, or otherwise not live.</summary>
    AncestorRevoked = 2,
    /// <summary>Lineage is missing, or a required store lookup/evaluation failed.</summary>
    LineageUnavailable = 3,
    /// <summary>Stored lineage does not match the evaluated derived grant.</summary>
    MalformedLineage = 4,
    /// <summary>The lineage shape is outside the qualified single-root-parent profile.</summary>
    UnsupportedLineage = 5,
    /// <summary>Current parent state loaded, but its policy evaluation is unavailable.</summary>
    EvaluationUnavailable = 6
}

/// <summary>
/// The captured current-parent evaluation for one derived-authority use.
/// Permit and ParentDenied verdicts always carry the exact parent request
/// and decision that were evaluated; other verdicts carry no parent
/// observation. Evidence is captured once, never re-evaluated later.
/// </summary>
public sealed record CurrentParentPolicyResult(
    CurrentParentPolicyVerdict Verdict,
    AuthorityRequest? ParentRequest,
    AuthorityDecision? ParentDecision,
    string? ParentGrantId,
    string? ParentSnapshotVersion,
    string? ParentSnapshotIdentity,
    string? ParentEvaluatorIdentity,
    DateTimeOffset EvaluatedAt);

/// <summary>
/// Evaluates the exact requested operation against every applicable current
/// restriction from the authority lineage that contributed to a derived
/// child. Consulted only after the child's own evaluator would otherwise
/// permit, so existing denial behavior is untouched. Root snapshots never
/// pay for lineage I/O.
/// </summary>
/// <remarks>
/// For the first qualified profile this means the current policy layers of
/// the single bound root parent, resolved from trusted stored lineage and
/// current snapshots. It is never an unbounded re-evaluation of every policy
/// attached to the parent principal.
/// </remarks>
public interface ICurrentParentPolicy
{
    ValueTask<CurrentParentPolicyResult> CheckAsync(
        AuthoritySnapshot snapshot,
        AuthorityRequest request,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken = default);
}
