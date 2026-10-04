using Penghou.IO.Abstractions;
using Penghou.Luban.Execution;

namespace Penghou.Hufu.Luban.Sqlite;

public enum PatchRecoveryState { NotFound, Unavailable, Reserved, Started, Completed, NoMutation, Ambiguous }
public enum PatchJournalOperation { Approve, RevokeApproval, ReadApproval, Reserve, ReadOutcome, Complete, ReconcileAmbiguous }
public enum PatchApprovalStatus { Unavailable, Denied, InvalidRequest, Conflict, CapacityExceeded, Recorded, Replayed, NotFound, Active, Expired, Revoked }

/// <summary>Exact host approval input. The full frozen admission is available to authenticated host policy.</summary>
public sealed record PatchApprovalCommand(string CommandId, AuthorityStoreActor Actor,
    AuthenticatedAuthorityContext Context, PatchAdmissionRequest Admission, DateTimeOffset ExpiresAt);

/// <summary>Closed target facts captured by approval; the provider object identity is supplied only at locked start.</summary>
public sealed record PatchApprovedIntent(string DocumentIdentity, string NodeIdentity, string PlanIdentity,
    HostInvocation Invocation, RequestIdentity RequestIdentity, WorkspaceId Workspace, WorkspacePath Path,
    string ProviderProfile, ResourceVersion OriginalVersion, ResourceVersion ProposedVersion,
    int OriginalByteLength, int ProposedByteLength);

public sealed record PatchApprovalRecord(string CommandId, AuthorityStoreActor Actor,
    AuthenticatedAuthorityContext Context, string OperationId, string AdmissionIdentity,
    DateTimeOffset ExpiresAt, PatchApprovedIntent Intent, bool Revoked = false, string? RevokeCommandId = null, AuthorityStoreActor? RevokedBy = null);
public sealed record PatchApprovalResult(PatchApprovalStatus Status, PatchApprovalRecord? Record = null);

/// <summary>Immutable reserved intent and observed outcome. Payload bytes and credentials are never stored here.</summary>
public sealed record PatchOutcomeRecord(AuthorityStoreActor Actor, AuthenticatedAuthorityContext Context,
    string OperationId, string AdmissionIdentity, string DocumentIdentity, string NodeIdentity, string PlanIdentity,
    MutationStartRequest Start, string DecisionCommandId, long SnapshotSequence,
    string BindingJson, string BindingIdentity, PatchRecoveryState State,
    string? EvidenceId = null, ResourceVersion? ObservedVersion = null);
public sealed record PatchOutcomeRead(PatchRecoveryState State, PatchOutcomeRecord? Record = null);

/// <summary>Required fresh authentication/operation policy for every journal call, including history and replay.</summary>
public sealed record PatchJournalAccessRequest(AuthorityStoreActor Actor, AuthenticatedAuthorityContext Context,
    PatchJournalOperation Operation, string OperationId, string AdmissionIdentity,
    string? CommandId = null, PatchApprovalCommand? Approval = null,
    PatchOutcomeRecord? Outcome = null, MutationCompletion? Completion = null);

public interface IPatchJournalAuthorizer
{
    ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(PatchJournalAccessRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Captures evaluator evidence at the actual evaluated instant. Returning null prevents admission/start.</summary>
public interface IHufuPatchDecisionEvidenceFactory
{
    ValueTask<AuthorityDecisionRecord?> CreateAsync(AuthorityRequest request, AuthorityDecision decision,
        DateTimeOffset evaluatedAt, CancellationToken cancellationToken = default);
}

public sealed record PatchJournalOptions(int MaxEntries = 10_000, long MaxStoredBytes = 67_108_864);
