using System.Security.Principal;
using System.Text.Json;
using Penghou.Hufu;
using Penghou.Hufu.Luban.Sqlite;
using Penghou.Luban.Execution;

namespace Hufu.LocalHost;

internal enum LocalHostMode { Operator, Worker }

internal sealed record LocalHostIdentityObservation(bool IsWindows, string? ProcessSid, string? CurrentSid,
    bool IsImpersonating = false);

internal interface ILocalHostIdentitySource
{
    LocalHostIdentityObservation Read();
}

internal sealed class WindowsLocalHostIdentitySource : ILocalHostIdentitySource
{
    public LocalHostIdentityObservation Read()
    {
        if (!OperatingSystem.IsWindows()) return new(false, null, null);
        using var processIdentity = WindowsIdentity.GetCurrent(ifImpersonating: false);
        using var impersonationIdentity = WindowsIdentity.GetCurrent(ifImpersonating: true);
        var processSid = processIdentity?.User?.Value;
        var currentSid = impersonationIdentity?.User?.Value ?? processSid;
        return new(true, processSid, currentSid, impersonationIdentity is not null);
    }
}

internal sealed record LocalHostServiceConfiguration(
    AuthorityStoreActor Actor,
    AuthenticatedAuthorityContext Context,
    string OperatorSid,
    LocalHostMode Mode,
    string? BoundOperationId = null)
{
    internal static LocalHostServiceConfiguration Create(string tenantId, string actorId,
        AuthenticatedAuthorityContext context, string operatorSid, LocalHostMode mode,
        string? boundOperationId = null) => new(
            new AuthorityStoreActor(tenantId, actorId, Guid.NewGuid().ToString("N")),
            context, operatorSid, mode, boundOperationId);

    internal bool IsValid => AuthorityStoreValidation.ValidActor(Actor) &&
        AuthorityValidation.ValidContext(Context) && Context.TenantId == Actor.TenantId &&
        AuthorityValidation.ValidToken(OperatorSid) && Enum.IsDefined(Mode) &&
        (BoundOperationId is null || AuthorityValidation.ValidToken(BoundOperationId)) &&
        (Mode == LocalHostMode.Operator || BoundOperationId is not null);
}

internal static class LocalHostAuthentication
{
    internal static bool Matches(LocalHostServiceConfiguration configuration,
        ILocalHostIdentitySource identitySource)
    {
        if (!configuration.IsValid) return false;
        try
        {
            var identity = identitySource.Read();
            return identity is { IsWindows: true, IsImpersonating: false } &&
                string.Equals(identity.ProcessSid, configuration.OperatorSid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(identity.CurrentSid, configuration.OperatorSid, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool IsConfiguredContext(LocalHostServiceConfiguration configuration,
        AuthenticatedAuthorityContext? context) => context == configuration.Context;

    internal static bool IsConfiguredSubject(LocalHostServiceConfiguration configuration,
        AuthoritySubject? subject) => subject == AuthoritySubject.From(configuration.Context);

    internal static bool IsBoundOperation(LocalHostServiceConfiguration configuration, string? operationId) =>
        configuration.BoundOperationId is null || configuration.BoundOperationId == operationId;
}

internal sealed class LocalHostStoreAuthorizer(
    LocalHostServiceConfiguration configuration,
    ILocalHostIdentitySource identitySource) : IAuthorityStoreAuthorizer
{
    public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !LocalHostAuthentication.Matches(configuration, identitySource) ||
            request.Actor != configuration.Actor || !LocalHostAuthentication.IsConfiguredSubject(configuration, request.Subject) ||
            !ContextMatches(request) || !Allowed(request))
            return ValueTask.FromResult(new AuthorityStoreAuthorization(AuthorityStatus.Deny));
        return ValueTask.FromResult(new AuthorityStoreAuthorization(AuthorityStatus.Permit, configuration.Actor));
    }

    private bool ContextMatches(AuthorityStoreAccessRequest request)
    {
        return (request.Context is null || request.Context == configuration.Context) &&
            (request.ProposedSnapshot is null || request.ProposedSnapshot.Context == configuration.Context) &&
            (request.DecisionRecord is null || request.DecisionRecord.Request.Context == configuration.Context) &&
            (request.StartCommand is null || request.StartCommand.Request.Context == configuration.Context);
    }

    private bool Allowed(AuthorityStoreAccessRequest request)
    {
        if (!Enum.IsDefined(request.Operation)) return false;
        if (request.Operation == AuthorityStoreOperation.StartOperation)
            return request.StartCommand is { } start && start.Actor == configuration.Actor &&
                start.Request.Context == configuration.Context &&
                LocalHostAuthentication.IsBoundOperation(configuration, start.OperationId);
        if (configuration.Mode == LocalHostMode.Operator) return true;
        return request.Operation switch
        {
            AuthorityStoreOperation.ReadCurrent => true,
            AuthorityStoreOperation.RecordDecision => request.DecisionRecord?.Request.Context == configuration.Context,
            _ => false
        };
    }
}

internal sealed record LocalHostIssuanceApproval(
    AuthoritySnapshot IssuerCeiling,
    string ApprovedSnapshotIdentity,
    string ApprovedCommandId,
    long ApprovedExpectedSequence,
    DateTimeOffset ApprovalValidUntil);

internal interface ILocalHostIssuanceStateSource
{
    ValueTask<LocalHostIssuanceApproval?> ResolveCurrentAsync(AuthorityStoreActor actor,
        AuthenticatedAuthorityContext context, AuthorityStoreAccessRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class LocalHostIssuanceTrustSource(
    LocalHostServiceConfiguration configuration,
    ILocalHostIdentitySource identitySource,
    ILocalHostIssuanceStateSource stateSource,
    TimeProvider? timeProvider = null) : IAuthorityIssuanceTrustSource
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(AuthorityStoreActor presentedActor,
        AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || presentedActor != configuration.Actor || request.Actor != configuration.Actor ||
            !LocalHostAuthentication.Matches(configuration, identitySource) ||
            !LocalHostAuthentication.IsConfiguredSubject(configuration, request.Subject) ||
            !RequestContextMatches(request)) return null;

        if (request.Operation != AuthorityStoreOperation.Publish)
            return new AuthorityIssuancePrincipal(configuration.Actor);

        var proposed = request.ProposedSnapshot;
        if (configuration.Mode != LocalHostMode.Operator || proposed is null ||
            proposed.Context != configuration.Context)
            return null;

        var approval = await stateSource.ResolveCurrentAsync(configuration.Actor, configuration.Context, request,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!LocalHostAuthentication.Matches(configuration, identitySource) || approval is null ||
            approval.IssuerCeiling is null || approval.IssuerCeiling.Context.TenantId != configuration.Context.TenantId ||
            approval.IssuerCeiling.Identity == proposed.Identity ||
            approval.ApprovedSnapshotIdentity != proposed.Identity ||
            approval.ApprovedCommandId != request.CommandId ||
            approval.ApprovedExpectedSequence != request.ExpectedSequence ||
            approval.ApprovalValidUntil <= _clock.GetUtcNow())
            return null;

        return new AuthorityIssuancePrincipal(configuration.Actor, approval.IssuerCeiling,
            approval.ApprovedSnapshotIdentity, approval.ApprovedExpectedSequence,
            approval.ApprovedCommandId, approval.ApprovalValidUntil);
    }

    private bool RequestContextMatches(AuthorityStoreAccessRequest request) =>
        (request.Context is null || request.Context == configuration.Context) &&
        (request.ProposedSnapshot is null || request.ProposedSnapshot.Context == configuration.Context) &&
        (request.DecisionRecord is null || request.DecisionRecord.Request.Context == configuration.Context) &&
        (request.StartCommand is null || request.StartCommand.Request.Context == configuration.Context);
}

internal sealed record LocalHostPatchApprovalCapability(
    PatchJournalOperation Operation,
    string OperationId,
    string AdmissionIdentity,
    string CommandId,
    DateTimeOffset? ExpiresAt,
    PatchAdmissionRequest? Admission);

internal sealed class LocalHostPatchJournalAuthorizer(
    LocalHostServiceConfiguration configuration,
    ILocalHostIdentitySource identitySource,
    TimeProvider? timeProvider = null) : IPatchJournalAuthorizer
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly AsyncLocal<ScopedCapability?> _capability = new();

    internal IDisposable BeginApproval(PatchApprovalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (configuration.Mode != LocalHostMode.Operator || command.Actor != configuration.Actor ||
            command.Context != configuration.Context || !LocalHostAuthentication.IsBoundOperation(configuration, command.Admission.OperationId) ||
            !AuthorityValidation.ValidToken(command.CommandId) || command.ExpiresAt <= _clock.GetUtcNow())
            throw new InvalidOperationException("An exact, current operator approval command is required.");
        var identity = HufuSinglePatchHost.AdmissionIdentity(configuration.Context, command.Admission);
        return BeginScope(new(PatchJournalOperation.Approve, command.Admission.OperationId, identity,
            command.CommandId, command.ExpiresAt, command.Admission));
    }

    internal IDisposable BeginApprovalRevocation(AuthenticatedAuthorityContext context, string operationId,
        string admissionIdentity, string commandId)
    {
        if (configuration.Mode != LocalHostMode.Operator || context != configuration.Context ||
            !AuthorityValidation.ValidToken(commandId) || !ValidAdmissionIdentity(admissionIdentity) ||
            !LocalHostAuthentication.IsBoundOperation(configuration, operationId))
            throw new InvalidOperationException("An exact operator approval revocation is required.");
        return BeginScope(new(PatchJournalOperation.RevokeApproval, operationId, admissionIdentity,
            commandId, null, null));
    }

    private IDisposable BeginScope(LocalHostPatchApprovalCapability capability)
    {
        if (_capability.Value is not null) throw new InvalidOperationException("Nested approval capabilities are not supported.");
        var scoped = new ScopedCapability(capability);
        _capability.Value = scoped;
        return new CapabilityScope(this, scoped);
    }

    public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(PatchJournalAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !LocalHostAuthentication.Matches(configuration, identitySource) ||
            request.Actor != configuration.Actor || request.Context != configuration.Context ||
            !LocalHostAuthentication.IsBoundOperation(configuration, request.OperationId) ||
            !AuthorityValidation.ValidToken(request.OperationId) || !ValidAdmissionIdentity(request.AdmissionIdentity) ||
            !NestedFactsMatch(request) ||
            !Enum.IsDefined(request.Operation) || !Allowed(request))
            return ValueTask.FromResult(new AuthorityStoreAuthorization(AuthorityStatus.Deny));
        return ValueTask.FromResult(new AuthorityStoreAuthorization(AuthorityStatus.Permit, configuration.Actor));
    }

    private bool Allowed(PatchJournalAccessRequest request) => request.Operation switch
    {
        PatchJournalOperation.Approve => configuration.Mode == LocalHostMode.Operator && MatchesCapability(request, requireExpiry: true),
        PatchJournalOperation.RevokeApproval => configuration.Mode == LocalHostMode.Operator && MatchesCapability(request, requireExpiry: false),
        PatchJournalOperation.ReadApproval or PatchJournalOperation.Reserve or PatchJournalOperation.Complete => true,
        PatchJournalOperation.ReadOutcome => configuration.Mode == LocalHostMode.Operator ||
            LocalHostAuthentication.IsBoundOperation(configuration, request.OperationId),
        PatchJournalOperation.ReconcileAmbiguous => configuration.Mode == LocalHostMode.Operator,
        _ => false
    };

    private bool NestedFactsMatch(PatchJournalAccessRequest request)
    {
        if (request.Approval is { } approval && (approval.Admission is null || approval.Actor != configuration.Actor ||
            approval.Context != configuration.Context || approval.Admission.OperationId != request.OperationId ||
            approval.CommandId != request.CommandId ||
            HufuSinglePatchHost.AdmissionIdentity(configuration.Context, approval.Admission) != request.AdmissionIdentity))
            return false;
        if (request.Outcome is { } outcome && (outcome.Actor != configuration.Actor ||
            outcome.Context != configuration.Context || outcome.OperationId != request.OperationId ||
            outcome.AdmissionIdentity != request.AdmissionIdentity)) return false;
        if (request.Completion is { } completion && (request.Outcome is not { } expected ||
            expected.State != PatchRecoveryState.Started || completion.Start != expected.Start ||
            completion.EvidenceId != expected.EvidenceId)) return false;
        return request.Operation switch
        {
            PatchJournalOperation.Approve => request.Approval is not null && request.Outcome is null && request.Completion is null,
            PatchJournalOperation.Reserve => request.Outcome is { State: PatchRecoveryState.Reserved } &&
                request.Approval is null && request.Completion is null,
            PatchJournalOperation.Complete => request.Outcome is { State: PatchRecoveryState.Started } &&
                request.Completion is not null && request.Approval is null,
            _ => request.Approval is null && request.Outcome is null && request.Completion is null
        };
    }

    private static bool ValidAdmissionIdentity(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool MatchesCapability(PatchJournalAccessRequest request, bool requireExpiry)
    {
        var scope = _capability.Value;
        if (scope is null) return false;
        var capability = scope.Value;
        if (Volatile.Read(ref scope.Consumed) != 0 || capability.Operation != request.Operation ||
            capability.OperationId != request.OperationId || capability.AdmissionIdentity != request.AdmissionIdentity ||
            capability.CommandId != request.CommandId) return false;
        if (requireExpiry)
        {
            var command = request.Approval;
            if (command is null || capability.Admission is null || command.Actor != configuration.Actor || command.Context != configuration.Context ||
                command.Admission != capability.Admission || command.CommandId != capability.CommandId ||
                command.ExpiresAt != capability.ExpiresAt || command.ExpiresAt <= _clock.GetUtcNow() ||
                HufuSinglePatchHost.AdmissionIdentity(configuration.Context, command.Admission) != request.AdmissionIdentity)
                return false;
        }
        return Interlocked.CompareExchange(ref scope.Consumed, 1, 0) == 0;
    }

    private sealed class ScopedCapability(LocalHostPatchApprovalCapability value)
    {
        internal LocalHostPatchApprovalCapability Value { get; } = value;
        internal int Consumed;
    }

    private sealed class CapabilityScope(LocalHostPatchJournalAuthorizer owner, ScopedCapability capability) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && ReferenceEquals(owner._capability.Value, capability))
                owner._capability.Value = null;
        }
    }
}

internal sealed class LocalHostDecisionEvidenceFactory : IHufuPatchDecisionEvidenceFactory
{
    private const string EvidenceFormat = "hufu-local-host-decision-v1";

    public ValueTask<AuthorityDecisionRecord?> CreateAsync(AuthorityRequest request, AuthorityDecision decision,
        DateTimeOffset evaluatedAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AuthorityValidation.IsValidRequest(request) || decision is null || !Enum.IsDefined(decision.Status) ||
            !AuthorityValidation.ValidToken(decision.ReasonCode) || !AuthorityValidation.ValidToken(decision.SnapshotIdentity) ||
            !AuthorityValidation.ValidToken(decision.SnapshotVersion) || !AuthorityValidation.ValidToken(decision.EvaluatorIdentity) ||
            evaluatedAt == default) return ValueTask.FromResult<AuthorityDecisionRecord?>(null);
        var evidence = JsonSerializer.Serialize(new
        {
            format = EvidenceFormat,
            request.Action,
            request.WorkspaceId,
            request.RelativePath,
            request.RequestIdentity,
            request.Context.TenantId,
            request.Context.SubjectId,
            request.Context.RunId,
            request.Context.RevisionId,
            request.Context.FenceId,
            decision.Status,
            decision.ReasonCode,
            decision.SnapshotVersion,
            decision.EvaluatorIdentity,
            decision.SnapshotIdentity,
            evaluatedAt = evaluatedAt.ToUniversalTime()
        });
        var record = new AuthorityDecisionRecord("decision-" + Guid.NewGuid().ToString("N"), request,
            decision, evaluatedAt.ToUniversalTime(), EvidenceFormat, evidence);
        return ValueTask.FromResult<AuthorityDecisionRecord?>(record);
    }
}
