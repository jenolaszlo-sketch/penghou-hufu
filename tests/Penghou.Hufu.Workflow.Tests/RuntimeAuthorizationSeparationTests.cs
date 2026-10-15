using Penghou.Hufu;
using Penghou.Hufu.Workflow;
using Penghou.Workflow.Abstractions;
using Xunit;

namespace Penghou.Hufu.Workflow.Tests;

/// <summary>
/// AE-6.1b: runtime effect authorization is its own decision domain. Neither
/// delegation acceptance, Marang command-plane permits, nor derivation
/// authorizes a runtime effect; capabilities outside the trusted vocabulary
/// deny before binding; narrowed authority denies re-evaluation; evidence
/// failure fails closed.
/// </summary>
public sealed class RuntimeAuthorizationSeparationTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("read-execution-result", AuthorityAction.ReadExecutionResult)]
    [InlineData("cancel-execution", AuthorityAction.CancelExecution)]
    public async Task NewCapabilities_PermitWhenGranted(string capability, AuthorityAction action)
    {
        var context = Context([Requirement(capability)]);
        var f = Harness.Create(context);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Allowed, result.Decision);
        var request = Assert.Single(f.Authority.Requests);
        Assert.Equal(action, request.Action);
        Assert.Equal("workspace-1", request.WorkspaceId);
        Assert.True(f.Authority.Responses[0].IsAuthorized);
    }

    [Theory]
    // Marang command-plane meanings are not runtime capabilities.
    [InlineData("marang.intervene")]
    [InlineData("marang.cancel")]
    [InlineData("marang.delegation.read")]
    // Acceptance and derivation are not capabilities at all.
    [InlineData("accept")]
    [InlineData("derive")]
    [InlineData("delete-file")]
    public async Task UnmappedCapability_DeniesBeforeBindingResolution(string capability)
    {
        var context = Context([Requirement(capability)]);
        var f = Harness.Create(context);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Denied, result.Decision);
        Assert.Equal(0, f.Bindings.Calls);
        Assert.Empty(f.Authority.Requests);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task AcceptancePermit_DoesNotAuthorizeARuntimeEffect()
    {
        // Same subject, same instant: acceptance permits by its own policy
        // while runtime authorization over authority without a covering grant
        // denies. One decision never satisfies the other domain.
        var hufuContext = new AuthenticatedAuthorityContext("tenant-1", "subject-1", "exec-1", "rev-1", "authz-req-1");
        var snapshot = new AuthoritySnapshot(hufuContext, "v1",
            [new AuthorityLayer("run", [
                new AuthorityGrant("g", [AuthorityAction.ReadFile],
                    new AuthorityScope("workspace-1", "other.txt", AuthorityScopeKind.Exact), [],
                    Epoch.AddMinutes(-1), Epoch.AddHours(1))])],
            [], Epoch.AddHours(1));
        var acceptance = new CurrentDelegationAcceptanceAuthorizer(
            new Source(snapshot), new PermitAcceptanceEvaluator(), new AcceptanceRecorder(), new Clock(Epoch));
        var acceptanceResult = await acceptance.AuthorizeAsync(new DelegationAcceptanceRequest(
            hufuContext, "acceptance-1", "workspace-1", "provider", "g0", new string('a', 64)));

        var context = Context([Requirement("read-execution-result")]);
        var f = Harness.Create(context, authority: new AuthorityFake((request, _) =>
            ValueTask.FromResult(new AuthorityRequestAuthorization(request, AuthorityStatus.Deny,
                new AuthorityDecision(AuthorityStatus.Deny, "authority.denied", "snapshot-1", "evaluator-1",
                    new string('b', 64)), true))));
        var runtimeResult = await f.Authorizer.AuthorizeAsync(context);

        Assert.True(acceptanceResult.IsAuthorized);
        Assert.Equal(ExecutionAuthorizationDecision.Denied, runtimeResult.Decision);
    }

    [Fact]
    public async Task DerivedOnlyGrant_WithoutLineageComposition_DeniesAtRuntime()
    {
        // Derivation authority never leaks into a runtime Permit through an
        // incomplete composition: known derived grants deny without the
        // qualified current-parent checker.
        var hufuContext = new AuthenticatedAuthorityContext("tenant-1", "subject-1", "exec-1", "rev-1", "authz-req-1");
        var grant = new AuthorityGrant("derived", [AuthorityAction.ReadExecutionResult],
            new AuthorityScope("workspace-1", "output", AuthorityScopeKind.Subtree), [],
            Epoch.AddMinutes(-1), Epoch.AddHours(1)) with { ParentGrantId = "parent" };
        var snapshot = new AuthoritySnapshot(hufuContext, "v1", [new AuthorityLayer("run", [grant])], [], Epoch.AddHours(1));
        var shell = new CurrentAuthorityRequestAuthorizer(
            new Source(snapshot), new PermitEvaluator(), new DecisionRecorder(), new Clock(Epoch));
        var context = Context([Requirement("read-execution-result", "output/result.txt")]);
        var f = Harness.Create(context, authority: new AuthorityFake((request, ct) => shell.AuthorizeAsync(request, ct)));

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Denied, result.Decision);
    }

    [Fact]
    public async Task WrongRuntimeAction_Denies()
    {
        var context = Context([Requirement("cancel-execution")]);
        var f = Harness.Create(context, authority: new AuthorityFake((request, _) =>
            ValueTask.FromResult(request.Action == AuthorityAction.ReadExecutionResult
                ? new AuthorityRequestAuthorization(request, AuthorityStatus.Permit,
                    new AuthorityDecision(AuthorityStatus.Permit, "authority.permit", "snapshot-1", "evaluator-1",
                        new string('a', 64)), true)
                : new AuthorityRequestAuthorization(request, AuthorityStatus.Deny,
                    new AuthorityDecision(AuthorityStatus.Deny, "authority.denied", "snapshot-1", "evaluator-1",
                        new string('b', 64)), true))));

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Denied, result.Decision);
    }

    [Fact]
    public async Task NarrowedGrant_DeniesReevaluation()
    {
        var context = Context([Requirement("read-execution-result")]);
        var calls = 0;
        var f = Harness.Create(context, authority: new AuthorityFake((request, _) =>
        {
            calls++;
            var permit = calls == 1;
            return ValueTask.FromResult(new AuthorityRequestAuthorization(request,
                permit ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                new AuthorityDecision(permit ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                    permit ? "authority.permit" : "authority.denied", "snapshot-1", "evaluator-1",
                    new string(permit ? 'a' : 'b', 64)), true));
        }));

        Assert.Equal(ExecutionAuthorizationDecision.Allowed, (await f.Authorizer.AuthorizeAsync(context)).Decision);
        Assert.Equal(ExecutionAuthorizationDecision.Denied, (await f.Authorizer.AuthorizeAsync(context)).Decision);
    }

    [Fact]
    public async Task EvidenceFailure_FailsClosed()
    {
        var context = Context([Requirement("read-execution-result")]);
        var f = Harness.Create(context, recorder: new RecorderFake((_, _) => ValueTask.FromResult<string?>(null)));

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Null(result.ExpiresAt);
    }

    private static ExecutionRequirement Requirement(string capability, string resource = "output/result.txt", string? scope = "scope-1") =>
        new(WorkflowAuthorityRequirements.SchemaId, WorkflowAuthorityRequirements.SchemaVersion, capability, resource, scope);

    private static ExecutionAuthorizationContext Context(IEnumerable<ExecutionRequirement> requirements) =>
        new(new ExecutionIdentity("exec-1", "op-1", 1, "rev-1", "parent-1", "steps/run", "plan-1", "plan-rev-1"),
            "authz-req-1", requirements.ToArray());

    private sealed class Harness
    {
        public required HufuExecutionAuthorizer Authorizer { get; init; }
        public required BindingSource Bindings { get; init; }
        public required AuthorityFake Authority { get; init; }

        public static Harness Create(ExecutionAuthorizationContext context, BindingSource? bindings = null,
            AuthorityFake? authority = null, RecorderFake? recorder = null)
        {
            var actor = new AuthenticatedAuthorityContext("tenant-1", "subject-1", context.ExecutionId,
                context.ExecutionRevision, context.AuthorizationRequestId);
            var supported = context.Requirements.Count > 0 &&
                context.Requirements.All(r => WorkflowAuthorityRequirements.TryGetAction(r, out _));
            WorkflowAuthorityBinding? binding = supported ? MakeBinding(context, actor) : null;
            bindings ??= new BindingSource(_ => ValueTask.FromResult(binding));
            authority ??= new AuthorityFake();
            recorder ??= new RecorderFake();
            var approval = new ApprovalFake();
            var authorizer = new HufuExecutionAuthorizer("provider-v1", "host-v1", "mapping-v1", bindings,
                authority, approval, recorder, new Clock(Epoch));
            return new() { Authorizer = authorizer, Bindings = bindings, Authority = authority };
        }

        public static WorkflowAuthorityBinding MakeBinding(ExecutionAuthorizationContext context,
            AuthenticatedAuthorityContext actor)
        {
            var targets = context.Requirements
                .Select(r => new WorkflowAuthorityTarget(r, "workspace-1", "output/result.txt")).ToArray();
            return new(context, "host-v1", "mapping-v1", actor, targets, Epoch.AddMinutes(1));
        }
    }

    private sealed class BindingSource(Func<ExecutionAuthorizationContext, ValueTask<WorkflowAuthorityBinding?>> resolve)
        : IWorkflowAuthorityBindingSource
    {
        public int Calls { get; private set; }
        public ValueTask<WorkflowAuthorityBinding?> ResolveAsync(ExecutionAuthorizationContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return resolve(context);
        }
    }

    private sealed class AuthorityFake(Func<AuthorityRequest, CancellationToken, ValueTask<AuthorityRequestAuthorization>>? authorize = null)
        : IAuthorityRequestAuthorizer
    {
        public List<AuthorityRequest> Requests { get; } = [];
        public List<AuthorityRequestAuthorization> Responses { get; } = [];
        public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var response = authorize is null
                ? new AuthorityRequestAuthorization(request, AuthorityStatus.Permit,
                    new AuthorityDecision(AuthorityStatus.Permit, "authority.permit", "snapshot-1", "evaluator-1",
                        new string('a', 64)), true)
                : await authorize(request, cancellationToken).ConfigureAwait(false);
            Responses.Add(response);
            return response;
        }
    }

    private sealed class ApprovalFake : IWorkflowApprovalCoordinator
    {
        public ValueTask<WorkflowApprovalResult?> EvaluateAsync(WorkflowAuthorityBinding binding,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorkflowApprovalResult?>(new WorkflowApprovalResult(
                WorkflowApprovalDecision.NotRequired, binding.Identity, "approval-evidence-1", Epoch.AddMinutes(2)));
    }

    private sealed class RecorderFake(Func<WorkflowAuthorizationRecord, CancellationToken, ValueTask<string?>>? callback = null)
        : IWorkflowAuthorizationRecorder
    {
        public ValueTask<string?> RecordAsync(WorkflowAuthorizationRecord record, CancellationToken cancellationToken = default) =>
            callback is null ? ValueTask.FromResult<string?>("evidence-aggregate") : callback(record, cancellationToken);
    }

    private sealed class Source(AuthoritySnapshot snapshot) : IAuthoritySnapshotSource
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

    private sealed class PermitEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now) =>
            new(AuthorityStatus.Permit, "authority.permit", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
    }

    private sealed class DecisionRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
