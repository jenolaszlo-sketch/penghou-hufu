using Penghou.Hufu;
using Penghou.Hufu.Workflow;
using Penghou.Workflow.Abstractions;
using Xunit;

namespace Penghou.Hufu.Workflow.Tests;

/// <summary>
/// AE-6.2: the production binding source resolves durable run identity on
/// every evaluation and fails closed on anything else. No binding, corrupt
/// binding, unmapped requirements, and unknown/revoked subjects never
/// authorize. The stored subject always wins: contexts carry no caller.
/// </summary>
public sealed class DurableBindingSourceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResolvesStoredIdentity_WithExactAuthorityContext()
    {
        var context = Context([Requirement("read-file")]);
        var source = Source(
            _ => ValueTask.FromResult<string?>(WorkflowRunAuthorityBindingCodec.Encode(new("tenant-1", "subject-1", "delegation-1", "workspace-1"))),
            (c, r, i, _) => ValueTask.FromResult<WorkflowAuthorityTarget?>(
                new WorkflowAuthorityTarget(r, i.WorkspaceId, "src/file.txt")));

        var binding = await source.ResolveAsync(context);

        Assert.NotNull(binding);
        Assert.Equal("tenant-1", binding.AuthorityContext.TenantId);
        Assert.Equal("subject-1", binding.AuthorityContext.SubjectId);
        Assert.Equal(context.ExecutionId, binding.AuthorityContext.RunId);
        Assert.Equal(context.ExecutionRevision, binding.AuthorityContext.RevisionId);
        Assert.Equal(context.AuthorizationRequestId, binding.AuthorityContext.FenceId);
        Assert.Equal("workspace-1", binding.Targets[0].WorkspaceId);
        Assert.True(binding.ValidUntil > Epoch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("hufu-workflow-binding-v1|only-two")]
    [InlineData("hufu-workflow-binding-v0|t|s|d|w")]
    [InlineData("hufu-workflow-binding-v1|t|s|d|w|extra")]
    [InlineData("hufu-workflow-binding-v1|t|s|d|")]
    public async Task MissingOrCorruptBinding_ResolvesNull(string? stored)
    {
        var context = Context([Requirement("read-file")]);
        var source = Source(
            _ => ValueTask.FromResult(stored),
            (c, r, i, _) => ValueTask.FromResult<WorkflowAuthorityTarget?>(new WorkflowAuthorityTarget(r, "w", "f.txt")));

        Assert.Null(await source.ResolveAsync(context));
    }

    [Fact]
    public async Task UnmappedRequirement_ResolvesNull()
    {
        var context = Context([Requirement("read-file")]);
        var source = Source(
            _ => ValueTask.FromResult<string?>(WorkflowRunAuthorityBindingCodec.Encode(new("t", "s", "d", "w"))),
            (c, r, i, _) => ValueTask.FromResult<WorkflowAuthorityTarget?>(null));

        Assert.Null(await source.ResolveAsync(context));
    }

    [Fact]
    public async Task EmptyRequirements_ResolveNull()
    {
        var context = Context([]);
        var source = Source(
            _ => ValueTask.FromResult<string?>(WorkflowRunAuthorityBindingCodec.Encode(new("t", "s", "d", "w"))),
            (c, r, i, _) => ValueTask.FromResult<WorkflowAuthorityTarget?>(new WorkflowAuthorityTarget(r, "w", "f.txt")));

        Assert.Null(await source.ResolveAsync(context));
    }

    [Fact]
    public async Task ReaderFailure_ResolvesNull_DoesNotThrow()
    {
        var context = Context([Requirement("read-file")]);
        var source = new DurableWorkflowAuthorityBindingSource(new ThrowingReader(),
            new MapperFake((c, r, i, _) => ValueTask.FromResult<WorkflowAuthorityTarget?>(
                new WorkflowAuthorityTarget(r, "w", "f.txt"))),
            "host-v1", "mapping-v1", new Clock(Epoch));

        Assert.Null(await source.ResolveAsync(context));
    }

    [Fact]
    public async Task UnknownSubject_Denies_EndToEnd()
    {
        var context = Context([Requirement("read-file", "src/file.txt")]);
        var source = Source(
            _ => ValueTask.FromResult<string?>(WorkflowRunAuthorityBindingCodec.Encode(new("tenant-1", "stranger", "d", "workspace-1"))),
            (c, r, i, _) => ValueTask.FromResult<WorkflowAuthorityTarget?>(
                new WorkflowAuthorityTarget(r, i.WorkspaceId, "src/file.txt")));
        // Authority knows only subject-1; the stored stranger has no grant.
        var authority = new AuthorityFake((request, _) =>
            ValueTask.FromResult(request.Context.SubjectId == "subject-1"
                ? Allow(request) : Deny(request, "authority.denied")));
        var authorizer = new HufuExecutionAuthorizer("provider-v1", "host-v1", "mapping-v1", source,
            authority, new ApprovalFake(), new RecorderFake(), new Clock(Epoch));

        var result = await authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Denied, result.Decision);
    }

    [Fact]
    public async Task RestartedSource_RecoversSameIdentity()
    {
        var store = new Dictionary<string, string?>
        {
            ["exec-1"] = WorkflowRunAuthorityBindingCodec.Encode(new("tenant-1", "subject-1", "delegation-1", "workspace-1")),
        };
        WorkflowAuthorityBinding? Resolve()
        {
            var context = Context([Requirement("read-file")]);
            var source = Source(
                id => ValueTask.FromResult(store.TryGetValue(id, out var v) ? v : null),
                (c, r, i, _) => ValueTask.FromResult<WorkflowAuthorityTarget?>(
                    new WorkflowAuthorityTarget(r, i.WorkspaceId, "src/file.txt")));
            // A new source instance over the same store is a process restart.
            return source.ResolveAsync(context).GetAwaiter().GetResult();
        }

        var first = Resolve();
        var second = Resolve();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Identity, second.Identity);
        Assert.Equal("subject-1", second.AuthorityContext.SubjectId);
    }

    [Fact]
    public async Task AcceptPermit_ProvidesNoSubstituteAuthority()
    {
        // An acceptance Permit for the same subject exists, but runtime
        // authorization consults only current resource authority: no grant.
        var hufuContext = new AuthenticatedAuthorityContext("tenant-1", "subject-1", "exec-1", "rev-1", "authz-req-1");
        var snapshot = new AuthoritySnapshot(hufuContext, "v1",
            [new AuthorityLayer("run", [
                new AuthorityGrant("g", [AuthorityAction.ReadFile],
                    new AuthorityScope("workspace-1", "other.txt", AuthorityScopeKind.Exact), [],
                    Epoch.AddMinutes(-1), Epoch.AddHours(1))])],
            [], Epoch.AddHours(1));
        var acceptance = new CurrentDelegationAcceptanceAuthorizer(
            new SnapshotSource(snapshot), new PermitAcceptanceEvaluator(), new AcceptanceRecorder(), new Clock(Epoch));
        var acceptanceResult = await acceptance.AuthorizeAsync(new DelegationAcceptanceRequest(
            hufuContext, "acceptance-1", "workspace-1", "provider", "g0", new string('a', 64)));

        var context = Context([Requirement("read-file", "src/file.txt")]);
        var source = Source(
            _ => ValueTask.FromResult<string?>(WorkflowRunAuthorityBindingCodec.Encode(new("tenant-1", "subject-1", "d", "workspace-1"))),
            (c, r, i, _) => ValueTask.FromResult<WorkflowAuthorityTarget?>(
                new WorkflowAuthorityTarget(r, i.WorkspaceId, "src/file.txt")));
        var authority = new AuthorityFake((request, _) => ValueTask.FromResult(Deny(request, "authority.denied")));
        var authorizer = new HufuExecutionAuthorizer("provider-v1", "host-v1", "mapping-v1", source,
            authority, new ApprovalFake(), new RecorderFake(), new Clock(Epoch));
        var runtimeResult = await authorizer.AuthorizeAsync(context);

        Assert.True(acceptanceResult.IsAuthorized);
        Assert.Equal(ExecutionAuthorizationDecision.Denied, runtimeResult.Decision);
    }

    private static DurableWorkflowAuthorityBindingSource Source(
        Func<string, ValueTask<string?>> read,
        Func<ExecutionAuthorizationContext, ExecutionRequirement, WorkflowRunAuthorityIdentity, CancellationToken, ValueTask<WorkflowAuthorityTarget?>> map) =>
        new(new ReaderFake(read), new MapperFake(map), "host-v1", "mapping-v1", new Clock(Epoch));

    private static ExecutionRequirement Requirement(string capability, string resource = "src/file.txt", string? scope = "scope-1") =>
        new(WorkflowAuthorityRequirements.SchemaId, WorkflowAuthorityRequirements.SchemaVersion, capability, resource, scope);

    private static ExecutionAuthorizationContext Context(IEnumerable<ExecutionRequirement> requirements) =>
        new(new ExecutionIdentity("exec-1", "op-1", 1, "rev-1", "parent-1", "steps/run", "plan-1", "plan-rev-1"),
            "authz-req-1", requirements.ToArray());

    private static AuthorityRequestAuthorization Allow(AuthorityRequest request) =>
        new(request, AuthorityStatus.Permit,
            new AuthorityDecision(AuthorityStatus.Permit, "authority.permit", "snapshot-1", "evaluator-1", new string('a', 64)), true);

    private static AuthorityRequestAuthorization Deny(AuthorityRequest request, string reason) =>
        new(request, AuthorityStatus.Deny,
            new AuthorityDecision(AuthorityStatus.Deny, reason, "snapshot-1", "evaluator-1", new string('b', 64)), true);

    private sealed class ReaderFake(Func<string, ValueTask<string?>> read) : IWorkflowRunBindingReader
    {
        public ValueTask<string?> ReadAuthorityBindingAsync(string executionId, CancellationToken cancellationToken = default) =>
            read(executionId);
    }

    private sealed class ThrowingReader : IWorkflowRunBindingReader
    {
        public ValueTask<string?> ReadAuthorityBindingAsync(string executionId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("store down");
    }

    private sealed class MapperFake(
        Func<ExecutionAuthorizationContext, ExecutionRequirement, WorkflowRunAuthorityIdentity, CancellationToken, ValueTask<WorkflowAuthorityTarget?>> map)
        : IWorkflowAuthorityTargetMapper
    {
        public ValueTask<WorkflowAuthorityTarget?> MapAsync(ExecutionAuthorizationContext context,
            ExecutionRequirement requirement, WorkflowRunAuthorityIdentity identity,
            CancellationToken cancellationToken = default) => map(context, requirement, identity, cancellationToken);
    }

    private sealed class AuthorityFake(Func<AuthorityRequest, CancellationToken, ValueTask<AuthorityRequestAuthorization>> authorize)
        : IAuthorityRequestAuthorizer
    {
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
            CancellationToken cancellationToken = default) => authorize(request, cancellationToken);
    }

    private sealed class ApprovalFake : IWorkflowApprovalCoordinator
    {
        public ValueTask<WorkflowApprovalResult?> EvaluateAsync(WorkflowAuthorityBinding binding,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorkflowApprovalResult?>(new WorkflowApprovalResult(
                WorkflowApprovalDecision.NotRequired, binding.Identity, "approval-evidence-1", Epoch.AddMinutes(2)));
    }

    private sealed class RecorderFake : IWorkflowAuthorizationRecorder
    {
        public ValueTask<string?> RecordAsync(WorkflowAuthorizationRecord record,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("evidence-aggregate");
    }

    private sealed class SnapshotSource(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
    {
        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<AuthoritySnapshot?>(snapshot);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class PermitAcceptanceEvaluator : IDelegationAcceptanceEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, DelegationAcceptanceRequest request, DateTimeOffset now) =>
            new(AuthorityStatus.Permit, "test-accept-permit", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
    }

    private sealed class AcceptanceRecorder : IDelegationAcceptanceRecorder
    {
        public ValueTask<bool> RecordAsync(DelegationAcceptanceRequest request, AuthorityDecision decision,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
