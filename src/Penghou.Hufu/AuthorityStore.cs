namespace Penghou.Hufu;

/// <summary>Host-authenticated actor facts. These identifiers are not bearer credentials.</summary>
public sealed record AuthorityStoreActor(string TenantId, string ActorId, string SessionId);
public sealed record AuthoritySubject(string TenantId, string SubjectId, string RunId)
{
    public static AuthoritySubject From(AuthenticatedAuthorityContext context) =>
        new(context.TenantId, context.SubjectId, context.RunId);
}
public enum AuthorityStoreOperation { ReadCurrent, Publish, Revoke, RecordDecision, ReadHistory, ReadDecisions, StartOperation }
public enum AuthorityChangeKind { Published, Revoked }
public enum AuthorityMutationStatus { Unavailable, Denied, InvalidRequest, Conflict, Applied, Replayed, CapacityExceeded }
public enum AuthorityReadStatus { Unavailable, Denied, InvalidRequest, NotFound, Active, Expired, Revoked, StaleContext }
public enum AuthorityEvidenceStatus { Unavailable, Denied, InvalidRequest, Conflict, StaleAuthority, Recorded, Replayed, CapacityExceeded }

public sealed record AuthorityPublishCommand(string CommandId, AuthorityStoreActor Actor,
    AuthoritySnapshot Snapshot, long ExpectedSequence);
public sealed record AuthorityRevokeCommand(string CommandId, AuthorityStoreActor Actor,
    AuthenticatedAuthorityContext Context, long ExpectedSequence, string ReasonCode);
public sealed record AuthorityDecisionRecord(string CommandId, AuthorityRequest Request,
    AuthorityDecision Decision, DateTimeOffset EvaluatedAt, string EvidenceFormat, string EvidenceJson);
public sealed record AuthorityChangeRecord(long Sequence, AuthorityChangeKind Kind, string CommandId,
    AuthorityStoreActor Actor, AuthenticatedAuthorityContext Context, AuthoritySnapshot? Snapshot,
    string ReasonCode, DateTimeOffset RecordedAt);
public sealed record AuthorityDecisionEntry(AuthorityStoreActor Actor, AuthorityDecisionRecord Record,
    long SnapshotSequence, DateTimeOffset RecordedAt);
public sealed record AuthorityMutationResult(AuthorityMutationStatus Status, AuthorityChangeRecord? Record = null);
public sealed record AuthorityCurrentRead(AuthorityReadStatus Status, AuthorityChangeRecord? Record = null);
public sealed record AuthorityHistoryRead(AuthorityReadStatus Status,
    IReadOnlyList<AuthorityChangeRecord> Records, long? NextBeforeSequence = null);
public sealed record AuthorityDecisionRead(AuthorityReadStatus Status, AuthorityDecisionEntry? Entry = null);
public sealed record AuthorityEvidenceWrite(AuthorityEvidenceStatus Status, AuthorityDecisionEntry? Entry = null);

/// <summary>Exact trusted host gate, including the full proposed snapshot or decision record.</summary>
public sealed record AuthorityStoreAccessRequest(AuthorityStoreActor Actor, AuthorityStoreOperation Operation,
    AuthoritySubject Subject, AuthenticatedAuthorityContext? Context = null,
    string? CommandId = null, long? ExpectedSequence = null, AuthoritySnapshot? ProposedSnapshot = null,
    string? ReasonCode = null, AuthorityDecisionRecord? DecisionRecord = null,
    AuthorityOperationStartCommand? StartCommand = null);
public sealed record AuthorityStoreAuthorization(AuthorityStatus Status, AuthorityStoreActor? Actor = null);

/// <summary>
/// Required host authentication and issuer/read/evidence policy. Reauthenticate
/// every call, including replay; data records or session identifiers alone grant nothing.
/// Publication policy must validate the issuer ceiling and runtime context.
/// </summary>
public interface IAuthorityStoreAuthorizer
{
    ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authority-owned persistence. Commands use expected store sequences, not clocks.
/// Replayed receipts are history, never current permission or operation-start tokens.
/// </summary>
public interface IAuthorityStore
{
    ValueTask<AuthorityMutationResult> PublishAsync(AuthorityPublishCommand command, CancellationToken cancellationToken = default);
    ValueTask<AuthorityMutationResult> RevokeAsync(AuthorityRevokeCommand command, CancellationToken cancellationToken = default);
    ValueTask<AuthorityCurrentRead> ReadCurrentAsync(AuthorityStoreActor actor, AuthenticatedAuthorityContext context,
        CancellationToken cancellationToken = default);
    ValueTask<AuthorityHistoryRead> ReadHistoryAsync(AuthorityStoreActor actor, AuthoritySubject subject,
        int maxEntries = 16, long? beforeSequence = null, CancellationToken cancellationToken = default);
    ValueTask<AuthorityEvidenceWrite> RecordDecisionAsync(AuthorityStoreActor actor, AuthorityDecisionRecord record,
        CancellationToken cancellationToken = default);
    ValueTask<AuthorityDecisionRead> ReadDecisionAsync(AuthorityStoreActor actor, AuthoritySubject subject,
        string commandId, CancellationToken cancellationToken = default);
}

/// <summary>Required host capture of the actual evaluated instant and trusted evaluator evidence.</summary>
public interface IAuthorityDecisionRecordFactory
{
    ValueTask<AuthorityDecisionRecord?> CreateAsync(AuthorityRequest request, AuthorityDecision decision,
        CancellationToken cancellationToken = default);
}

/// <summary>Current snapshot binding with explicit authenticated host actor; no historical fallback.</summary>
public sealed class AuthorityStoreSnapshotSource : IAuthoritySnapshotSource
{
    private readonly IAuthorityStore _store;
    private readonly AuthorityStoreActor _actor;
    public AuthorityStoreSnapshotSource(IAuthorityStore store, AuthorityStoreActor actor)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (!AuthorityStoreValidation.ValidActor(actor)) throw new ArgumentException("A bounded authenticated actor is required.");
        _actor = actor;
    }
    public async ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AuthorityValidation.ValidContext(context)) return null;
            var read = await _store.ReadCurrentAsync(_actor, context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // An expired current snapshot can produce attributable denial; it cannot grant.
            return read.Status is AuthorityReadStatus.Active or AuthorityReadStatus.Expired &&
                read.Record is { Kind: AuthorityChangeKind.Published, Sequence: > 0, Snapshot: { } snapshot } state &&
                state.Context == context && snapshot.Context == context ? snapshot : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}

/// <summary>Required durable recording of exact captured evidence. No synthesized timestamps or permit fallback.</summary>
public sealed class AuthorityStoreDecisionRecorder : IAuthorityDecisionRecorder
{
    private readonly IAuthorityStore _store;
    private readonly AuthorityStoreActor _actor;
    private readonly IAuthorityDecisionRecordFactory _factory;
    public AuthorityStoreDecisionRecorder(IAuthorityStore store, AuthorityStoreActor actor, IAuthorityDecisionRecordFactory factory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        if (!AuthorityStoreValidation.ValidActor(actor)) throw new ArgumentException("A bounded authenticated actor is required.");
        _actor = actor;
    }
    public async ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await _factory.CreateAsync(request, decision, cancellationToken).ConfigureAwait(false);
            if (record is null || record.Request != request || record.Decision != decision) return false;
            var write = await _store.RecordDecisionAsync(_actor, record, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return write.Status is AuthorityEvidenceStatus.Recorded or AuthorityEvidenceStatus.Replayed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}

public static class AuthorityStoreValidation
{
    public static bool ValidActor(AuthorityStoreActor? actor) => actor is not null &&
        AuthorityValidation.ValidToken(actor.TenantId) && AuthorityValidation.ValidToken(actor.ActorId) && AuthorityValidation.ValidToken(actor.SessionId);
    public static bool ValidSubject(AuthoritySubject? subject) => subject is not null &&
        AuthorityValidation.ValidToken(subject.TenantId) && AuthorityValidation.ValidToken(subject.SubjectId) && AuthorityValidation.ValidToken(subject.RunId);
}
