namespace Penghou.Hufu;

/// <summary>
/// Delegation control verbs for caller visibility authority. Only verbs with
/// real consumers are granted: <see cref="Observe"/> backs restart-safe
/// historical reads, while <see cref="Cancel"/> and
/// <see cref="Intervene"/> model the delegation's authorized control surface.
/// Granting a control verb authorizes the request; executing it additionally
/// requires a live execution handle/fence owned by the host runtime.
/// </summary>
[Flags]
public enum DelegationVisibilityOperation
{
    /// <summary>No operation.</summary>
    None = 0,

    /// <summary>Observe delegation detail, run linkage, run projection, journal, and durable evidence.</summary>
    Observe = 1,

    /// <summary>Request cancellation of the delegation.</summary>
    Cancel = 2,

    /// <summary>Apply a supervisor intervention to the delegation.</summary>
    Intervene = 4,
}

/// <summary>
/// Durable caller-visibility authority for one
/// <c>(tenant, subject, delegation, generation)</c> slot: the bound subject
/// may exercise the granted operations while the binding is live, unexpired,
/// and unrevoked. The acceptance fingerprint is stored for write-time
/// conflict detection, never as an identity input: the same slot addressed
/// with a different fingerprint is a conflict, even after process loss.
/// Caller authentication stays at the host boundary; identifiers here are
/// not bearer credentials.
/// </summary>
public sealed record DelegationVisibilityBinding(
    string TenantId,
    string SubjectId,
    string DelegationId,
    string Generation,
    string RequestKey,
    string FingerprintVersion,
    string FingerprintHash,
    DelegationVisibilityOperation Operations,
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt,
    string CommandId,
    bool Revoked,
    string RevokeReason,
    DateTimeOffset CreatedAt);

/// <summary>Outcome of publishing one visibility binding.</summary>
public enum DelegationVisibilityPublishStatus
{
    /// <summary>The binding was newly persisted.</summary>
    Published = 0,

    /// <summary>An identical binding already existed; the original is returned.</summary>
    Replayed = 1,

    /// <summary>The slot holds a different binding; nothing was overwritten.</summary>
    Conflict = 2,

    /// <summary>The slot is terminally revoked; restoring access needs a new acceptance.</summary>
    Revoked = 3,
}

/// <summary>Outcome of a visibility authorization check (the read path carries no content).</summary>
public enum DelegationVisibilityCheckStatus
{
    /// <summary>The subject may exercise the required operation.</summary>
    Granted = 0,

    /// <summary>No binding exists for this slot.</summary>
    Unknown = 1,

    /// <summary>The binding is terminally revoked.</summary>
    Revoked = 2,

    /// <summary>The binding is outside its validity window.</summary>
    Expired = 3,

    /// <summary>The binding does not grant the required operation.</summary>
    Forbidden = 4,
}

/// <summary>Outcome of revoking one visibility binding.</summary>
public enum DelegationVisibilityRevokeStatus
{
    /// <summary>The binding is now terminally revoked.</summary>
    Revoked = 0,

    /// <summary>The same revocation command was already applied.</summary>
    Replayed = 1,

    /// <summary>No binding exists for this slot.</summary>
    NotFound = 2,

    /// <summary>The slot is already revoked under a different command.</summary>
    Conflict = 3,
}

/// <summary>
/// Durable delegation-visibility authority: idempotent publish commands,
/// terminal revocation, and validity-window checks over one slot per
/// <c>(tenant, subject, delegation, generation)</c>. Revocation is never
/// undone; restoring access requires a new acceptance identity.
/// </summary>
public interface IDelegationVisibilityStore
{
    /// <summary>
    /// Publishes the binding. An identical replay returns the original; any
    /// difference at an occupied slot is a conflict; a revoked slot stays
    /// revoked. Never overwrites.
    /// </summary>
    ValueTask<(DelegationVisibilityPublishStatus Status, DelegationVisibilityBinding? Binding)> PublishAsync(
        DelegationVisibilityBinding candidate,
        CancellationToken cancellationToken = default);

    /// <summary>Terminally revokes one slot. Revocation is never undone.</summary>
    ValueTask<DelegationVisibilityRevokeStatus> RevokeAsync(
        string tenantId,
        string subjectId,
        string delegationId,
        string generation,
        string commandId,
        string reasonCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorizes one operation at <paramref name="now"/>. Unknown, revoked,
    /// expired, and forbidden are distinct fail-closed outcomes.
    /// </summary>
    ValueTask<DelegationVisibilityCheckStatus> CheckAsync(
        string tenantId,
        string subjectId,
        string delegationId,
        string generation,
        DelegationVisibilityOperation required,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one binding by slot, or null when absent.</summary>
    ValueTask<DelegationVisibilityBinding?> GetAsync(
        string tenantId,
        string subjectId,
        string delegationId,
        string generation,
        CancellationToken cancellationToken = default);
}
