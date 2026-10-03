namespace Penghou.Hufu;

public enum AuthorityOperationStartStatus
{
    Unavailable, Denied, InvalidRequest, StaleAuthority, StaleRuntime,
    Conflict, CapacityExceeded, Started, AlreadyStarted
}

/// <summary>Exact host-bound operation intent; a stored decision alone is not a start permit.</summary>
public sealed record AuthorityOperationStartCommand(AuthorityStoreActor Actor, string OperationId,
    AuthorityRequest Request, string DecisionCommandId, long ExpectedSequence,
    string BindingIdentity, string BindingJson);

/// <summary>Durable start evidence. Inspection or replay never authorizes a second dispatch.</summary>
public sealed record AuthorityOperationStartRecord(AuthorityOperationStartCommand Command,
    string ParticipantProfile, long SnapshotSequence, DateTimeOffset StartedAt);

public sealed record AuthorityOperationStartResult(AuthorityOperationStartStatus Status,
    AuthorityOperationStartRecord? Record = null);

public interface IAuthorityOperationStartGate
{
    ValueTask<AuthorityOperationStartResult> StartAsync(AuthorityOperationStartCommand command,
        CancellationToken cancellationToken = default);
}
