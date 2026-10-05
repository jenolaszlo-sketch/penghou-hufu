namespace Penghou.Hufu.Sandbox;

/// <summary>
/// Classified outcome of a start attempt. Each value names a distinct failure
/// so the authority decision, the platform guarantee gap, and provider faults
/// stay separately attributable.
/// </summary>
public enum SandboxExecutionStatus
{
    /// <summary>The execution domain is running and associated with the activity.</summary>
    Started,
    /// <summary>Hufu authority denied or was unavailable at prepare or pre-launch.</summary>
    AuthorityDenied,
    /// <summary>The invocation/environment/guarantee request was outside the registered profile.</summary>
    RequirementNotAuthorized,
    /// <summary>The platform cannot provide a required guarantee (fail-closed; never weakened).</summary>
    GuaranteeUnavailable,
    /// <summary>Provider preparation failed (missing resource/delegation); nothing launched.</summary>
    PreparationFailed,
    /// <summary>Provider launch failed after a successful preparation.</summary>
    LaunchFailed,
}

/// <summary>Opaque handle for one running execution. It exposes no provider identity.</summary>
public sealed record SandboxExecutionHandle(Guid ExecutionId);

/// <summary>
/// The exact authority and trusted-profile revision that shaped a launch.
/// Recorded for evidence and queryable while the execution is active.
/// </summary>
public sealed record SandboxExecutionBinding(
    SandboxActivityIdentity Activity,
    string ProfileId,
    string ProfileRevision,
    string AuthorizationRequestId);

public sealed record SandboxStartResult(SandboxExecutionStatus Status,
    SandboxExecutionHandle? Handle = null, string? ReasonCode = null, IReadOnlyList<string>? Reasons = null)
{
    public bool IsStarted => Status == SandboxExecutionStatus.Started && Handle is not null;
}

public enum SandboxTerminateStatus
{
    /// <summary>Every execution associated with the activity was terminated.</summary>
    Terminated,
    /// <summary>No running execution is associated with the activity.</summary>
    UnknownActivity,
    /// <summary>The provider refused termination for at least one execution.</summary>
    Failed,
}

public sealed record SandboxTerminateResult(SandboxTerminateStatus Status,
    string? ReasonCode = null, IReadOnlyList<string>? Reasons = null);
