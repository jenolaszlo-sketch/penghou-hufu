using System.Text.Json;
using Penghou.Hufu.Sqlite;
using Penghou.IO.Abstractions;
using Penghou.Luban.Execution;
using Penghou.Luban.Language;
using Penghou.Luban.Resolution;
using Penghou.Luban;

namespace Penghou.Hufu.Luban.Sqlite;

/// <summary>Opt-in single exact patch host. Trusted policies, evidence capture and controlled storage are required.</summary>
public sealed class HufuSinglePatchHost : IPatchExecutionHost
{
    private readonly AuthorityStoreActor _actor;
    private readonly AuthenticatedAuthorityContext _context;
    private readonly SqliteAuthorityStore _store;
    private readonly IAuthorityEvaluator _evaluator;
    private readonly IHufuPatchDecisionEvidenceFactory _factory;
    private readonly SqlitePatchOutcomeJournal _journal;
    private readonly SqliteAuthorityOperationStartGate _starts;

    public HufuSinglePatchHost(AuthorityStoreActor actor, AuthenticatedAuthorityContext context,
        SqliteAuthorityStore store, IAuthorityEvaluator evaluator,
        IHufuPatchDecisionEvidenceFactory evidenceFactory, SqlitePatchOutcomeJournal journal)
    {
        if (!AuthorityStoreValidation.ValidActor(actor) || !AuthorityValidation.ValidContext(context) || actor.TenantId != context.TenantId)
            throw new ArgumentException("Authenticated matching actor and context are required.");
        _actor = actor; _context = context;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _factory = evidenceFactory ?? throw new ArgumentNullException(nameof(evidenceFactory));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _starts = new(store, journal);
    }

    public static PatchAdmissionRequest PrepareAdmission(AuthenticatedAuthorityContext context,
        CompiledPreviewDocument document, ResolvedEffectPlan plan, string operationId)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(plan);
        if (plan.Nodes.Count != 1 || plan.Nodes[0].Proposals.Count != 1) throw new ArgumentException("One exact captured patch is required.");
        var p = plan.Nodes[0].Proposals[0];
        var invocation = new HostInvocation(operationId, plan.Invocation.SubjectId, plan.Invocation.EffectId,
            plan.Invocation.AttemptId, plan.Identity, document.Identity, default);
        var identity = ResourceRequestIdentity.Compute(new FileWriteRequest(invocation, document.Workspace,
            new(p.RelativePath), p.ProposedContent.ToArray(), new(document.Limits.MaxFileBytes),
            new(WritePreconditionKind.MustMatchVersion, p.OriginalVersion)));
        var admission = new PatchAdmissionRequest(operationId, document, plan, PatchIdentity.WriterProfile,
            invocation with { RequestIdentity = identity }, identity);
        if (!PatchIdentity.Valid(context, admission)) throw new ArgumentException("An exact bounded single-target admission is required.");
        return admission;
    }

    public static string AdmissionIdentity(AuthenticatedAuthorityContext context, PatchAdmissionRequest admission) => PatchIdentity.Admission(context, admission);

    public IPreviewAuthorizer CreatePreviewAuthorizer(EffectInvocation invocation, CompiledPreviewDocument document) =>
        new PatchPreviewAuthorizer(this, invocation, document);

    public async ValueTask<LanguageAuthorityDecision> AdmitAsync(PatchAdmissionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PatchIdentity.Valid(_context, request)) return new(LanguageAuthorityStatus.Deny);
        try
        {
            if (!await Approved(request, cancellationToken).ConfigureAwait(false)) return new(LanguageAuthorityStatus.Deny);
            var prior = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
            if (prior.State != PatchRecoveryState.NotFound) return new(prior.State == PatchRecoveryState.Unavailable ? LanguageAuthorityStatus.Unavailable : LanguageAuthorityStatus.Deny);
            var checks = await CheckAll(request, "admit", false, cancellationToken).ConfigureAwait(false);
            return new(Language(checks.Status));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(LanguageAuthorityStatus.Unavailable); }
    }

    public async ValueTask<ResourceAuthorizationDecision> AuthorizeResourceAsync(PatchResourceCheck request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !PatchIdentity.Valid(_context, request.Admission) || request.Resource is null) return new(AuthorizationStatus.Deny);
        var a = request.Admission; var resource = request.Resource;
        var (workspace, path) = resource.Resource switch
        {
            ResourceBinding.WorkspaceFile f => (f.Workspace, f.Path.Value),
            ResourceBinding.WorkspaceEntry e => (e.Workspace, e.Path.Value),
            _ => (default, null)
        };
        var target = a.Plan.Nodes[0].Proposals[0].RelativePath;
        var action = resource.Action switch
        {
            ResourceAction.ReadFile when path == target && resource.Resource is ResourceBinding.WorkspaceFile => AuthorityAction.ReadFile,
            ResourceAction.PatchFile when path == target && resource.Resource is ResourceBinding.WorkspaceFile => AuthorityAction.PatchFile,
            ResourceAction.WriteFile when path == target && resource.Resource is ResourceBinding.WorkspaceFile => AuthorityAction.WriteFile,
            ResourceAction.ReadMetadata when path is not null && PatchIdentity.Ancestors(target).Contains(path) && resource.Resource is ResourceBinding.WorkspaceEntry => AuthorityAction.ReadMetadata,
            _ => (AuthorityAction?)null
        };
        if (resource.Invocation != a.Invocation || resource.SnapshotRequestIdentity != a.ResourceRequestIdentity || workspace != a.Plan.Workspace || action is null || path is null)
            return new(AuthorizationStatus.Deny);
        try
        {
            if (!await Approved(a, cancellationToken).ConfigureAwait(false)) return new(AuthorizationStatus.Deny);
            var check = await Check(new(_context, action.Value, workspace.Value, path, Digest(a, "resource", action.Value, path)), cancellationToken).ConfigureAwait(false);
            return new(check.Authorization.IsAuthorized ? AuthorizationStatus.Permit : check.Authorization.Status == AuthorityStatus.Deny ? AuthorizationStatus.Deny : AuthorizationStatus.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(AuthorizationStatus.Unavailable); }
    }

    public async ValueTask<MutationStartDecision> StartAsync(PatchStartRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !PatchIdentity.Matches(_context, request.Admission, request.Mutation)) return new(MutationStartStatus.Deny);
        try
        {
            var a = request.Admission;
            var checks = await CheckAll(a, "start:" + PatchIdentity.Hash(JsonSerializer.Serialize(request.Mutation, PatchIdentity.Json)), true, cancellationToken).ConfigureAwait(false);
            if (checks.Status != AuthorityStatus.Permit || checks.Patch is null) return new(checks.Status == AuthorityStatus.Deny ? MutationStartStatus.Deny : MutationStartStatus.Unavailable);
            var r = new PatchOutcomeRecord(_actor, _context, a.OperationId, AdmissionIdentity(_context, a),
                a.Document.Identity, a.Document.Nodes[0].Identity, a.Plan.Identity, request.Mutation,
                checks.Patch.Record.CommandId, checks.Patch.SnapshotSequence, "", "", PatchRecoveryState.Reserved);
            r = r with { BindingJson = PatchIdentity.Binding(r) };
            r = r with { BindingIdentity = PatchIdentity.Hash(r.BindingJson) };
            if (!await _journal.ReserveAsync(r, cancellationToken).ConfigureAwait(false)) return new(MutationStartStatus.Deny);
            var command = PatchIdentity.Command(r);
            var start = await _starts.StartAsync(command, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (start.Status != AuthorityOperationStartStatus.Started || start.Record is not { } committed ||
                committed.Command != command || committed.ParticipantProfile != PatchIdentity.ParticipantProfile || committed.SnapshotSequence != r.SnapshotSequence)
                return new(start.Status == AuthorityOperationStartStatus.AlreadyStarted ? MutationStartStatus.AlreadyStarted : MutationStartStatus.Unavailable);
            var read = await _journal.InspectAsync(_actor, _context, a.OperationId, r.AdmissionIdentity, cancellationToken).ConfigureAwait(false);
            if (read.State != PatchRecoveryState.Started || read.Record != (r with { State = PatchRecoveryState.Started, EvidenceId = PatchIdentity.Evidence(r) }))
                return new(MutationStartStatus.Unavailable);
            return new(MutationStartStatus.Started, read.Record.EvidenceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(MutationStartStatus.Unavailable); }
    }

    public async ValueTask<bool> CompleteAsync(PatchCompletion completion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (completion is null || completion.Mutation is null || !PatchIdentity.Matches(_context, completion.Admission, completion.Mutation.Start)) return false;
        try
        {
            var a = completion.Admission;
            var read = await InspectAsync(a, cancellationToken).ConfigureAwait(false);
            if (read.Record is not { } r || r.Actor != _actor || r.Context != _context || r.DocumentIdentity != a.Document.Identity ||
                r.PlanIdentity != a.Plan.Identity || r.NodeIdentity != a.Document.Nodes[0].Identity || r.Start != completion.Mutation.Start || r.EvidenceId != completion.Mutation.EvidenceId) return false;
            // Completion is evidence of an in-flight operation, even after block-new-starts revocation.
            var expected = r with { State = PatchRecoveryState.Started, ObservedVersion = null };
            if (!await _journal.CompleteAsync(expected, completion.Mutation, cancellationToken).ConfigureAwait(false)) return false;
            var release = await Check(new(_context, AuthorityAction.Release, r.Start.Workspace.Value, r.Start.Path.Value,
                Digest(a, "completion:" + completion.Mutation.Outcome, AuthorityAction.Release, r.Start.Path.Value)), cancellationToken).ConfigureAwait(false);
            return release.Authorization.IsAuthorized;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public ValueTask<PatchOutcomeRead> InspectAsync(PatchAdmissionRequest admission, CancellationToken cancellationToken = default) =>
        _journal.InspectAsync(_actor, _context, admission.OperationId, AdmissionIdentity(_context, admission), cancellationToken);
    public ValueTask<bool> ReconcileAmbiguousAsync(PatchAdmissionRequest admission, CancellationToken cancellationToken = default) =>
        _journal.ReconcileAmbiguousAsync(_actor, _context, admission.OperationId, AdmissionIdentity(_context, admission), cancellationToken);

    private async ValueTask<bool> Approved(PatchAdmissionRequest a, CancellationToken ct)
    {
        var read = await _journal.ReadApprovalAsync(_actor, _context, a.OperationId, AdmissionIdentity(_context, a), ct).ConfigureAwait(false);
        return read.Status == PatchApprovalStatus.Active && read.Record is { Revoked: false } r &&
            r.Context == _context && r.OperationId == a.OperationId && r.AdmissionIdentity == AdmissionIdentity(_context, a);
    }
    private string Digest(PatchAdmissionRequest a, string phase, AuthorityAction action, string path) =>
        PatchIdentity.Hash(JsonSerializer.Serialize(new { Admission = AdmissionIdentity(_context, a), Phase = phase, Action = action, Path = path }, PatchIdentity.Json));

    private async ValueTask<(AuthorityStatus Status, AuthorityDecisionEntry? Patch)> CheckAll(PatchAdmissionRequest a, string phase, bool final, CancellationToken ct)
    {
        var path = a.Plan.Nodes[0].Proposals[0].RelativePath;
        // The core gate rechecks validity from this PatchFile proof's evaluated
        // instant through commit. Capture it first so transitions of ANY mapped
        // grant during the remaining final checks invalidate the whole start.
        var requirements = new List<(AuthorityAction Action, string Path)> { (AuthorityAction.PatchFile, path), (AuthorityAction.ReadFile, path), (AuthorityAction.WriteFile, path), (AuthorityAction.Release, path) };
        requirements.AddRange(PatchIdentity.Ancestors(path).Select(p => (AuthorityAction.ReadMetadata, p)));
        AuthorityDecisionEntry? first = null, patch = null;
        foreach (var required in requirements)
        {
            var id = final && required.Action == AuthorityAction.PatchFile ? a.ResourceRequestIdentity.Value : Digest(a, phase, required.Action, required.Path);
            var result = await Check(new(_context, required.Action, a.Plan.Workspace.Value, required.Path, id), ct).ConfigureAwait(false);
            if (!result.Authorization.IsAuthorized || result.Entry is null) return (result.Authorization.Status == AuthorityStatus.Deny ? AuthorityStatus.Deny : AuthorityStatus.Unavailable, null);
            var entry = result.Entry;
            if (first is not null && (entry.SnapshotSequence != first.SnapshotSequence || entry.Record.Decision.SnapshotIdentity != first.Record.Decision.SnapshotIdentity || entry.Record.Decision.SnapshotVersion != first.Record.Decision.SnapshotVersion))
                return (AuthorityStatus.Unavailable, null);
            first ??= entry;
            if (required.Action == AuthorityAction.PatchFile) patch = entry;
        }
        return (AuthorityStatus.Permit, patch);
    }

    internal async ValueTask<(AuthorityRequestAuthorization Authorization, AuthorityDecisionEntry? Entry)> Check(AuthorityRequest request, CancellationToken ct)
    {
        var clock = new EvaluationClock(_journal.TimeProvider);
        var recorder = new Recorder(this, clock);
        var authorizer = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(_store, _actor), _evaluator, recorder, clock);
        var authorization = await authorizer.AuthorizeAsync(request, ct).ConfigureAwait(false);
        return (authorization, recorder.Entry);
    }
    internal async ValueTask<AuthorityRequestAuthorization> CheckPreview(AuthorityAction action, string workspace, string path,
        string identity, string subjectId, CancellationToken ct)
    {
        var request = new AuthorityRequest(_context, action, workspace, path, identity);
        if (subjectId != _context.SubjectId) return new(request, AuthorityStatus.Deny);
        return (await Check(request, ct).ConfigureAwait(false)).Authorization;
    }
    private static LanguageAuthorityStatus Language(AuthorityStatus status) => status switch { AuthorityStatus.Permit => LanguageAuthorityStatus.Permit, AuthorityStatus.Deny => LanguageAuthorityStatus.Deny, _ => LanguageAuthorityStatus.Unavailable };
    private sealed class EvaluationClock(TimeProvider clock) : TimeProvider
    {
        internal DateTimeOffset? EvaluatedAt { get; private set; }
        public override DateTimeOffset GetUtcNow() { var now = clock.GetUtcNow(); EvaluatedAt ??= now; return now; }
    }
    private sealed class Recorder(HufuSinglePatchHost host, EvaluationClock clock) : IAuthorityDecisionRecorder
    {
        internal AuthorityDecisionEntry? Entry { get; private set; }
        public async ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            if (clock.EvaluatedAt is not { } at) return false;
            var record = await host._factory.CreateAsync(request, decision, at, cancellationToken).ConfigureAwait(false);
            if (record is null || record.Request != request || record.Decision != decision || record.EvaluatedAt != at) return false;
            var write = await host._store.RecordDecisionAsync(host._actor, record, cancellationToken).ConfigureAwait(false);
            if (write.Status is not (AuthorityEvidenceStatus.Recorded or AuthorityEvidenceStatus.Replayed) || write.Entry is not { } entry || entry.Actor != host._actor || entry.Record != record || entry.SnapshotSequence < 1) return false;
            Entry = entry; return true;
        }
    }
}
