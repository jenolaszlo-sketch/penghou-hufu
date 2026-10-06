using Gagamba.Execution;

namespace Penghou.Hufu.Zhinu;

/// <summary>
/// One durable executable invocation requested by a workflow activity. The
/// authenticated authority context is supplied by the host; the invocation id
/// must name an approved invocation in the execution profile.
/// </summary>
public sealed record SandboxActivityInput
{
    public required AuthenticatedAuthorityContext AuthorityContext { get; init; }
    public required string InvocationId { get; init; }
    public required ExecutionRequirements Requirements { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>Classified terminal outcome of one durable sandbox activity attempt.</summary>
public enum SandboxActivityStatus
{
    /// <summary>Root exited 0 and the domain completed.</summary>
    Completed,
    /// <summary>Root exited non-zero; the code is preserved.</summary>
    ExecutionFailed,
    /// <summary>Hufu authority denied or was unavailable.</summary>
    AuthorityDenied,
    /// <summary>The request exceeded the trusted profile (invocation/environment/ceiling).</summary>
    RequirementNotAuthorized,
    /// <summary>The platform cannot provide a required guarantee.</summary>
    GuaranteeUnavailable,
    /// <summary>Provider preparation failed.</summary>
    PreparationFailed,
    /// <summary>Provider launch failed.</summary>
    LaunchFailed,
    /// <summary>The execution was terminated because authority was revoked.</summary>
    Revoked,
    /// <summary>The execution was terminated because the workflow was cancelled.</summary>
    WorkflowCancelled,
    /// <summary>Durable state indicates an execution whose outcome is unknown; reconcile.</summary>
    RecoveryRequired,
}

public sealed record SandboxActivityOutcome(SandboxActivityStatus Status,
    int? RootExitCode = null, string? Detail = null);
