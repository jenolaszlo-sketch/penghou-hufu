using Penghou.Hufu;
using Penghou.Hufu.Workflow;
using Penghou.Workflow.Abstractions;
using Xunit;

namespace Penghou.Hufu.Workflow.Tests;

public sealed class HufuExecutionAuthorizerTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UtcNow;

    public static TheoryData<string, AuthorityAction> SupportedVocabulary => new()
    {
        { "read-file", AuthorityAction.ReadFile },
        { "list-directory", AuthorityAction.ListDirectory },
        { "read-metadata", AuthorityAction.ReadMetadata },
        { "patch-file", AuthorityAction.PatchFile },
        { "release", AuthorityAction.Release },
        { "write-file", AuthorityAction.WriteFile }
    };

    [Theory]
    [MemberData(nameof(SupportedVocabulary))]
    public async Task SupportedVocabularyMapsToOneExactCorrelatedRequest(string capability, AuthorityAction action)
    {
        var req = Requirement(capability);
        var context = Context([req]);
        var f = Harness.Create(context);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Allowed, result.Decision);
        Assert.Equal(context.AuthorizationRequestId, result.AuthorizationRequestId);
        Assert.Equal("provider-v1", result.ProviderId);
        Assert.Equal("evidence-aggregate", result.EvidenceId);
        Assert.Equal(1, f.Bindings.Calls);
        Assert.Equal(new AuthorityRequest(f.AuthenticatedContext, action, "workspace-1", "src/file.txt",
            $"wf:{f.Binding!.Identity}:0"), Assert.Single(f.Authority.Requests));
        Assert.True(f.Authority.Responses[0].IsAuthorized);
        Assert.Equal(f.Authority.Responses[0].Decision, f.Records.Single().RequestAuthorizations.Single().Decision);
        Assert.True(f.Records.Single().RequestAuthorizations.Single().EvidenceRecorded);
        Assert.Equal(f.Authority.Requests[0], f.Records.Single().RequestAuthorizations.Single().Request);
        Assert.Equal(ExecutionAuthorizationDecision.Allowed, f.Records.Single().Result.Decision);
    }

    [Theory]
    [InlineData("delete-file", "resource-v1", 1, "scope-1")]
    [InlineData("read-file", "other-vocabulary", 1, "scope-1")]
    [InlineData("read-file", "penghou.hufu.resource", 2, "scope-1")]
    [InlineData("read-file", "penghou.hufu.resource", 1, null)]
    public async Task UnsupportedOrScopelessDeclarationsDenyBeforeBindingResolution(
        string capability, string schema, int version, string? scope)
    {
        var context = Context([new ExecutionRequirement(schema, version, capability, "resource-a", scope)]);
        var f = Harness.Create(context);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Denied, result.Decision);
        Assert.Equal(0, f.Bindings.Calls);
        Assert.Empty(f.Authority.Requests);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task EmptyRequirementsAreDeniedBeforeBindingResolution()
    {
        var context = Context([]);
        var f = Harness.Create(context);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Denied, result.Decision);
        Assert.Equal(0, f.Bindings.Calls);
        Assert.Empty(f.Authority.Requests);
    }

    [Fact]
    public async Task CallerRequirementCollectionIsSnapshottedBeforeResolution()
    {
        var mutable = new List<ExecutionRequirement> { Requirement("read-file", "first") };
        var context = Context(mutable);
        var f = Harness.Create(context);
        mutable[0] = Requirement("release", "mutated");
        mutable.Add(Requirement("write-file", "added"));

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Allowed, result.Decision);
        Assert.Single(f.Binding!.Targets);
        Assert.Equal("first", f.Binding.Targets[0].Requirement.Resource);
        Assert.Equal(AuthorityAction.ReadFile, f.Authority.Requests.Single().Action);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("plan")]
    [InlineData("revision")]
    [InlineData("attempt")]
    [InlineData("execution-revision")]
    [InlineData("operation-path")]
    [InlineData("request-id")]
    [InlineData("requirements-order")]
    public async Task BindingForChangedFullContextOrRequirementOrderFailsClosed(string change)
    {
        var declared = change == "requirements-order"
            ? new[] { Requirement("read-file", "a"), Requirement("write-file", "b") }
            : new[] { Requirement("read-file") };
        var original = Context(declared);
        var resolvedFor = Change(original, change);
        var binding = Harness.MakeBinding(resolvedFor);
        var bindings = new BindingSource(_ => ValueTask.FromResult<WorkflowAuthorityBinding?>(binding));
        var authority = new AuthorityFake();
        var recorder = new RecorderFake();
        var f = Harness.Create(original, bindings, authority, recorder);

        var result = await f.Authorizer.AuthorizeAsync(original);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Empty(authority.Requests);
        Assert.Null(result.ExpiresAt);
        Assert.True(original.Identity != resolvedFor.Identity ||
            original.AuthorizationRequestId != resolvedFor.AuthorizationRequestId ||
            !original.Requirements.SequenceEqual(resolvedFor.Requirements));
        Assert.NotEqual(Harness.MakeBinding(original).Identity, binding.Identity);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("revision")]
    [InlineData("fence")]
    public void BindingRejectsAuthorityActorThatDoesNotMatchFreshExecutionIdentity(string mismatch)
    {
        var context = Context([Requirement("read-file")]);
        var actor = mismatch switch
        {
            "run" => new AuthenticatedAuthorityContext("tenant-1", "subject-1", "other-exec", "rev-1", "authz-req-1"),
            "revision" => new AuthenticatedAuthorityContext("tenant-1", "subject-1", "exec-1", "other-rev", "authz-req-1"),
            _ => new AuthenticatedAuthorityContext("tenant-1", "subject-1", "exec-1", "rev-1", "other-request")
        };

        Assert.Throws<ArgumentException>(() => Harness.MakeBinding(context, authenticatedContext: actor));
    }
    [Theory]
    [InlineData("host")]
    [InlineData("mapping")]
    public async Task BindingWithDifferentHostOrMappingIdentityFailsClosed(string mismatch)
    {
        var context = Context([Requirement("read-file")]);
        var binding = Harness.MakeBinding(context,
            hostNamespace: mismatch == "host" ? "other-host" : "host-v1",
            mappingId: mismatch == "mapping" ? "other-map" : "mapping-v1");
        var f = Harness.Create(context, new BindingSource(_ => ValueTask.FromResult<WorkflowAuthorityBinding?>(binding)));

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Empty(f.Authority.Requests);
    }

    [Theory]
    [InlineData("SRC/File.txt")]
    [InlineData("src/../file.txt")]
    [InlineData("src\\file.txt")]
    [InlineData("/src/file.txt")]
    public void BindingTargetRejectsNonCanonicalOrUnsafeMappedPaths(string path)
    {
        var context = Context([Requirement("read-file")]);

        Assert.Throws<ArgumentException>(() => new WorkflowAuthorityTarget(
            context.Requirements.ElementAt(0), "workspace-1", path));
    }

    [Fact]
    public async Task AuthorityDenialIsPreservedAndRecordedWithoutParsingReasonText()
    {
        var context = Context([Requirement("read-file")]);
        var denied = new AuthorityDecision(AuthorityStatus.Deny, "reason-says-approved", "snapshot-1", "evaluator-1", new string('a', 64));
        var authority = new AuthorityFake((request, _) => new(request, AuthorityStatus.Deny, denied, true));
        var f = Harness.Create(context, authority: authority);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Denied, result.Decision);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(AuthorityStatus.Deny, Assert.Single(f.Records.Single().RequestAuthorizations).Status);
        Assert.Equal("evidence-aggregate", result.EvidenceId);
    }

    [Theory]
    [InlineData(AuthorityStatus.Unavailable)]
    [InlineData(AuthorityStatus.Permit)]
    public async Task NonPermitOrPermitWithoutValidEvidenceNeverAllows(AuthorityStatus status)
    {
        var context = Context([Requirement("read-file")]);
        var authority = new AuthorityFake((request, _) => status == AuthorityStatus.Permit
            ? new(request, AuthorityStatus.Permit, null, true)
            : new(request, AuthorityStatus.Unavailable));
        var f = Harness.Create(context, authority: authority);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Null(result.ExpiresAt);
        Assert.DoesNotContain(f.Records.Single().RequestAuthorizations, a => a.IsAuthorized);
    }

    [Fact]
    public async Task NullAuthorityResponseIsUnavailableAndNeverAllows()
    {
        var context = Context([Requirement("read-file")]);
        var authority = new AuthorityFake((_, _) => null!);
        var f = Harness.Create(context, authority: authority);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Null(result.ExpiresAt);
        Assert.Empty(f.Records.Single().RequestAuthorizations);
    }

    [Fact]
    public async Task MismatchedAuthorityRequestIsUnavailable()
    {
        var context = Context([Requirement("read-file")]);
        var authority = new AuthorityFake((request, _) => new(request with { WorkspaceId = "another" },
            AuthorityStatus.Permit, Permit(request), true));
        var f = Harness.Create(context, authority: authority);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Null(result.ExpiresAt);
    }

    [Theory]
    [InlineData(WorkflowApprovalDecision.NotRequired, ExecutionAuthorizationDecision.Allowed)]
    [InlineData(WorkflowApprovalDecision.Approved, ExecutionAuthorizationDecision.Allowed)]
    [InlineData(WorkflowApprovalDecision.Required, ExecutionAuthorizationDecision.ApprovalRequired)]
    [InlineData(WorkflowApprovalDecision.Denied, ExecutionAuthorizationDecision.Denied)]
    [InlineData(WorkflowApprovalDecision.Unavailable, ExecutionAuthorizationDecision.Unavailable)]
    public async Task TypedApprovalOutcomesMapByDecision(
        WorkflowApprovalDecision approval, ExecutionAuthorizationDecision expected)
    {
        var context = Context([Requirement("read-file")]);
        var f = Harness.Create(context, approvalDecision: approval);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(expected, result.Decision);
        Assert.Equal(1, f.Approvals.Calls);
        if (approval == WorkflowApprovalDecision.Required)
        {
            Assert.Equal("approval-correlation-1", result.ApprovalRequestId);
            Assert.Equal(ExecutionAuthorizationDecision.ApprovalRequired, f.Records.Single().Result.Decision);
        }
        else Assert.Null(result.ApprovalRequestId);
        if (expected == ExecutionAuthorizationDecision.Allowed)
            Assert.NotNull(result.ExpiresAt);
        else if (expected == ExecutionAuthorizationDecision.ApprovalRequired) Assert.NotNull(result.ExpiresAt);
        else Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task ApprovalBoundToDifferentFullContextFailsClosed()
    {
        var context = Context([Requirement("read-file")]);
        var approvals = new ApprovalFake((b, _) => ValueTask.FromResult<WorkflowApprovalResult?>(
            new(WorkflowApprovalDecision.Approved, new string('f', 64), "approval-evidence", Epoch.AddMinutes(2))));
        var f = Harness.Create(context, approval: approvals);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Equal(1, approvals.Calls);
        Assert.Null(result.ExpiresAt);
    }

    [Fact]
    public async Task MissingBindingNeverFallsBackToAmbientPermit()
    {
        var context = Context([Requirement("read-file")]);
        var f = Harness.Create(context, new BindingSource(_ => ValueTask.FromResult<WorkflowAuthorityBinding?>(null)));

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Empty(f.Authority.Requests);
        Assert.Equal(0, f.Approvals.Calls);
        Assert.Null(result.ExpiresAt);
    }

    [Theory]
    [MemberData(nameof(BadEvidenceIds))]
    public async Task MissingThrowingOrMalformedAggregateEvidenceFailsClosed(string? evidence, bool throws)
    {
        var context = Context([Requirement("read-file")]);
        var recorder = new RecorderFake((record, _) => throws
            ? throw new InvalidOperationException("recorder failed")
            : ValueTask.FromResult(evidence));
        var f = Harness.Create(context, recorder: recorder);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Null(result.EvidenceId);
        Assert.Null(result.ExpiresAt);
        Assert.Single(recorder.Records);
    }

    public static TheoryData<string?, bool> BadEvidenceIds => new()
    {
        { null, false }, { "", false }, { "  ", false }, { new string('x', 257), false }, { null, true }
    };

    [Fact]
    public async Task AuthorityExceptionsFailClosedAndAreIncludedInAggregateEvidence()
    {
        var context = Context([Requirement("read-file")]);
        var authority = new AuthorityFake((_, _) => throw new InvalidOperationException("authority down"));
        var f = Harness.Create(context, authority: authority);

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Equal("evidence-aggregate", result.EvidenceId);
        Assert.Empty(f.Records.Single().RequestAuthorizations);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutRecordingOrPermission()
    {
        var context = Context([Requirement("read-file")]);
        using var cancellation = new CancellationTokenSource();
        var bindings = new BindingSource(_ =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult<WorkflowAuthorityBinding?>(Harness.MakeBinding(context));
        });
        var f = Harness.Create(context, bindings);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await f.Authorizer.AuthorizeAsync(context, cancellation.Token));

        Assert.Empty(f.Records);
        Assert.Empty(f.Authority.Requests);
    }

    [Fact]
    public async Task ExpiryAcrossAuthorityApprovalAndEvidenceAwaitsFailsClosed()
    {
        var context = Context([Requirement("read-file")]);
        var clock = new MutableClock(Epoch);
        var f = Harness.Create(context, clock: clock, maximumValidity: TimeSpan.FromSeconds(2));
        f.Authority.AfterAuthorize = () => clock.Advance(TimeSpan.FromSeconds(1));
        f.Approvals.AfterEvaluate = () => clock.Advance(TimeSpan.FromSeconds(1));

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Null(result.ExpiresAt);
        Assert.Equal(1, f.Approvals.Calls);
    }

    [Fact]
    public async Task ExpiryDuringAggregateEvidenceWriteBlocksAnOtherwiseAllowedResult()
    {
        var context = Context([Requirement("read-file")]);
        var clock = new MutableClock(Epoch);
        var f = Harness.Create(context, clock: clock, maximumValidity: TimeSpan.FromSeconds(2));
        f.Recorder.AfterRecord = () => clock.Advance(TimeSpan.FromSeconds(2));

        var result = await f.Authorizer.AuthorizeAsync(context);

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Null(result.ExpiresAt);
        Assert.Null(f.Records.Single().Result.EvidenceId);
    }

    [Fact]
    public async Task HangingProviderIsBoundedAndDoesNotCancelCallerToken()
    {
        var context = Context([Requirement("read-file")]);
        using var callerCancellation = new CancellationTokenSource();
        var authorizer = new HufuExecutionAuthorizer("provider-v1", "host-v1", "mapping-v1",
            new BindingSource(_ => ValueTask.FromResult<WorkflowAuthorityBinding?>(Harness.MakeBinding(context))),
            new HangingAuthority(), new ApprovalFake(), new RecorderFake(), maximumValidity: TimeSpan.FromMilliseconds(100));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await authorizer.AuthorizeAsync(context, callerCancellation.Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ExecutionAuthorizationDecision.Unavailable, result.Decision);
        Assert.Null(result.ExpiresAt);
        Assert.Null(result.EvidenceId);
        Assert.False(callerCancellation.IsCancellationRequested);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }
    [Fact]
    public void NoPublicDefaultConstructorCanCreateAnImplicitAuthorizer()
    {
        Assert.DoesNotContain(typeof(HufuExecutionAuthorizer).GetConstructors(), c => c.GetParameters().Length == 0);
    }

    private static ExecutionRequirement Requirement(string capability, string resource = "resource-a", string? scope = "scope-1") =>
        new(WorkflowAuthorityRequirements.SchemaId, WorkflowAuthorityRequirements.SchemaVersion, capability, resource, scope);

    private static ExecutionAuthorizationContext Context(IEnumerable<ExecutionRequirement> requirements) =>
        new(new ExecutionIdentity("exec-1", "op-1", 1, "rev-1", "parent-1", "steps/compile", "plan-1", "plan-rev-1"),
            "authz-req-1", requirements.ToArray());

    private static ExecutionAuthorizationContext Change(ExecutionAuthorizationContext context, string change)
    {
        var i = context.Identity;
        ExecutionIdentity identity = change switch
        {
            "parent" => new(i.ExecutionId, i.OperationId, i.Attempt, i.ExecutionRevision, "parent-other", i.OperationPath, i.PlanId, i.PlanRevision),
            "plan" => new(i.ExecutionId, i.OperationId, i.Attempt, i.ExecutionRevision, i.ParentExecutionId, i.OperationPath, "plan-other", "plan-rev-1"),
            "revision" => new(i.ExecutionId, i.OperationId, i.Attempt, i.ExecutionRevision, i.ParentExecutionId, i.OperationPath, i.PlanId, "plan-rev-other"),
            "attempt" => new(i.ExecutionId, i.OperationId, i.Attempt + 1, i.ExecutionRevision, i.ParentExecutionId, i.OperationPath, i.PlanId, i.PlanRevision),
            "execution-revision" => new(i.ExecutionId, i.OperationId, i.Attempt, "rev-other", i.ParentExecutionId, i.OperationPath, i.PlanId, i.PlanRevision),
            "operation-path" => new(i.ExecutionId, i.OperationId, i.Attempt, i.ExecutionRevision, i.ParentExecutionId, "steps/other", i.PlanId, i.PlanRevision),
            _ => i
        };
        var requirements = change == "requirements-order" ? context.Requirements.Reverse().ToArray() : context.Requirements.ToArray();
        return new(identity, change == "request-id" ? "authz-req-other" : context.AuthorizationRequestId, requirements);
    }

    private static AuthorityDecision Permit(AuthorityRequest request) =>
        new(AuthorityStatus.Permit, "authority.permit", "snapshot-1", "evaluator-1", new string('a', 64));

    private sealed class Harness
    {
        public required HufuExecutionAuthorizer Authorizer { get; init; }
        public required BindingSource Bindings { get; init; }
        public required AuthorityFake Authority { get; init; }
        public required ApprovalFake Approvals { get; init; }
        public required RecorderFake Recorder { get; init; }
        public required AuthenticatedAuthorityContext AuthenticatedContext { get; init; }
        public WorkflowAuthorityBinding? Binding { get; init; }
        public List<WorkflowAuthorizationRecord> Records => Recorder.Records;

        public static Harness Create(ExecutionAuthorizationContext context, BindingSource? bindings = null,
            AuthorityFake? authority = null, RecorderFake? recorder = null, ApprovalFake? approval = null,
            WorkflowApprovalDecision approvalDecision = WorkflowApprovalDecision.NotRequired,
            MutableClock? clock = null, TimeSpan? maximumValidity = null)
        {
            var actor = new AuthenticatedAuthorityContext("tenant-1", "subject-1", context.ExecutionId, context.ExecutionRevision, context.AuthorizationRequestId);
            var supported = context.Requirements.Count > 0 &&
                context.Requirements.All(r => WorkflowAuthorityRequirements.TryGetAction(r, out _));
            WorkflowAuthorityBinding? binding = supported ? Harness.MakeBinding(context, authenticatedContext: actor) : null;
            bindings ??= new BindingSource(_ => ValueTask.FromResult(binding));
            authority ??= new AuthorityFake();
            recorder ??= new RecorderFake();
            approval ??= new ApprovalFake((b, _) => ValueTask.FromResult<WorkflowApprovalResult?>(new(
                approvalDecision, b.Identity, "approval-evidence-1", Epoch.AddMinutes(2),
                approvalDecision == WorkflowApprovalDecision.Required ? "approval-correlation-1" : null)));
            var authorizer = new HufuExecutionAuthorizer("provider-v1", "host-v1", "mapping-v1", bindings,
                authority, approval, recorder, clock, maximumValidity);
            return new() { Authorizer = authorizer, Bindings = bindings, Authority = authority,
                Approvals = approval, Recorder = recorder, AuthenticatedContext = actor, Binding = binding };
        }

        public static WorkflowAuthorityBinding MakeBinding(ExecutionAuthorizationContext context,
            IReadOnlyList<WorkflowAuthorityTarget>? targets = null, string hostNamespace = "host-v1",
            string mappingId = "mapping-v1", AuthenticatedAuthorityContext? authenticatedContext = null)
        {
            authenticatedContext ??= new("tenant-1", "subject-1", context.ExecutionId, context.ExecutionRevision, context.AuthorizationRequestId);
            targets ??= context.Requirements.Select(r => new WorkflowAuthorityTarget(r, "workspace-1", "src/file.txt")).ToArray();
            return new(context, hostNamespace, mappingId, authenticatedContext, targets, Epoch.AddMinutes(1));
        }
    }

    private sealed class BindingSource(Func<ExecutionAuthorizationContext, ValueTask<WorkflowAuthorityBinding?>> resolve) : IWorkflowAuthorityBindingSource
    {
        public int Calls { get; private set; }
        public ValueTask<WorkflowAuthorityBinding?> ResolveAsync(ExecutionAuthorizationContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return resolve(context);
        }
    }

    private sealed class AuthorityFake(Func<AuthorityRequest, CancellationToken, AuthorityRequestAuthorization>? authorize = null)
        : IAuthorityRequestAuthorizer
    {
        public List<AuthorityRequest> Requests { get; } = [];
        public List<AuthorityRequestAuthorization> Responses { get; } = [];
        public Action? AfterAuthorize { get; set; }
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            AfterAuthorize?.Invoke();
            var response = authorize is null
                ? new AuthorityRequestAuthorization(request, AuthorityStatus.Permit, Permit(request), true)
                : authorize(request, cancellationToken);
            Responses.Add(response);
            return ValueTask.FromResult(response);
        }
    }

    private sealed class ApprovalFake(Func<WorkflowAuthorityBinding, CancellationToken, ValueTask<WorkflowApprovalResult?>>? evaluate = null)
        : IWorkflowApprovalCoordinator
    {
        public int Calls { get; private set; }
        public Action? AfterEvaluate { get; set; }
        public ValueTask<WorkflowApprovalResult?> EvaluateAsync(WorkflowAuthorityBinding binding, CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            ValueTask<WorkflowApprovalResult?> result = evaluate is null
                ? ValueTask.FromResult<WorkflowApprovalResult?>(new WorkflowApprovalResult(
                    WorkflowApprovalDecision.NotRequired, binding.Identity, "approval-evidence-1", Epoch.AddMinutes(2)))
                : evaluate(binding, cancellationToken);
            AfterEvaluate?.Invoke();
            return result;
        }
    }

    private sealed class RecorderFake(Func<WorkflowAuthorizationRecord, CancellationToken, ValueTask<string?>>? callback = null)
        : IWorkflowAuthorizationRecorder
    {
        public List<WorkflowAuthorizationRecord> Records { get; } = [];
        public Action? AfterRecord { get; set; }
        public ValueTask<string?> RecordAsync(WorkflowAuthorizationRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            var evidence = callback is null
                ? ValueTask.FromResult<string?>("evidence-aggregate")
                : callback(record, cancellationToken);
            AfterRecord?.Invoke();
            return evidence;
        }
    }

    private sealed class HangingAuthority : IAuthorityRequestAuthorizer
    {
        private readonly TaskCompletionSource<AuthorityRequestAuthorization> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default) => new(_never.Task);
    }
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
