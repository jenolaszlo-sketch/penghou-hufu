using Penghou.Hufu;
using Penghou.Hufu.Workflow;
using Penghou.Workflow.Abstractions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Workflow.Integration.Tests;

public sealed class WorkflowAuthorizationIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hufu-workflow-integration", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AllowedActivityExecutesAndCompletedReplayDoesNotReauthorize()
    {
        var host = CreateHost("allow-replay");
        var workflow = new ProtectedAndTailWorkflow("read-file");
        await using var engine = host.CreateEngine(workflow);
        var runId = await engine.StartAsync("protected", "1", "input");

        await engine.ExecuteAsync(runId);
        Assert.Equal(1, workflow.ProtectedCalls);
        Assert.Equal(2, host.Policy.Evaluations);
        Assert.Single(host.Policy.Requests, r => r.Action == AuthorityAction.ReadFile);
        Assert.Single(host.Policy.Requests, r => r.Action == AuthorityAction.ReadMetadata);
        Assert.Equal(2, host.WorkflowRecords.Count);
        await engine.RestartStepAsync(runId, "tail");
        await engine.ExecuteAsync(runId);

        Assert.Equal(1, workflow.ProtectedCalls);
        Assert.Equal(2, workflow.TailCalls);
        Assert.Equal(3, host.Policy.Evaluations);
        Assert.Single(host.Policy.Requests, r => r.Action == AuthorityAction.ReadFile);
        Assert.Equal(2, host.Policy.Requests.Count(r => r.Action == AuthorityAction.ReadMetadata));
        Assert.Equal(3, host.WorkflowRecords.Count);
    }

    [Fact]
    public async Task CompleteDeclarationSetIsMappedBeforeEachTypedDecision()
    {
        var host = CreateHost("multiple-requirements");
        var workflow = new MultiRequirementWorkflow();
        await using var engine = host.CreateEngine(workflow);
        var runId = await engine.StartAsync("protected", "1", "input");

        await engine.ExecuteAsync(runId);

        Assert.Equal(1, workflow.Calls);
        Assert.Equal(2, host.Policy.Evaluations);
        Assert.Contains(host.Policy.Requests, r => r.Action == AuthorityAction.ReadFile && r.RelativePath == "src/source.cs");
        Assert.Contains(host.Policy.Requests, r => r.Action == AuthorityAction.ReadMetadata && r.RelativePath == "src/output.cs");
        Assert.Equal(2, Assert.Single(host.WorkflowRecords).RequestAuthorizations.Count);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("error")]
    [InlineData("unavailable")]
    public async Task DenyErrorAndUnavailableNeverInvokeProtectedCallback(string mode)
    {
        var host = CreateHost("deny-" + mode);
        if (mode == "deny") host.Policy.AllowedActions.Clear();
        if (mode == "error") host.Policy.Throw = true;
        if (mode == "unavailable") host.Policy.Unavailable = true;
        var workflow = new ProtectedWorkflow("read-file");
        await using var engine = host.CreateEngine(workflow);
        var runId = await engine.StartAsync("protected", "1", "input");

        await engine.ExecuteAsync(runId);

        Assert.Equal(0, workflow.Calls);
        Assert.Equal(1, host.Policy.Evaluations);
        Assert.Single(host.WorkflowRecords);
        Assert.NotEqual(ExecutionAuthorizationDecision.Allowed, Assert.Single(host.WorkflowRecords).Result.Decision);
    }

    [Fact]
    public async Task ApprovalParksAndExactWakeReauthorizesAfterStoreAndEngineRecreation()
    {
        var host = CreateHost("approval-restart");
        host.Approvals.RequireFirst = true;
        var workflow = new ProtectedWorkflow("read-file");
        var store = host.CreateStore();
        var engine = host.CreateEngine(store, workflow);
        var runId = await engine.StartAsync("protected", "1", "input");
        await engine.ExecuteAsync(runId);
        Assert.Equal(0, workflow.Calls);
        var step = (await engine.GetStepsAsync(runId)).Single(s => s.StepKey == "protected");
        var pending = await store.GetPendingAuthorizationAsync(runId, step.Id, false);
        Assert.NotNull(pending);
        Assert.Single(host.Policy.Requests);
        Assert.Single(host.WorkflowRecords);
        var wake = Wake(pending!);
        await engine.DisposeAsync();
        store = host.CreateStore();
        Assert.False(await store.WakeAuthorizationAsync(wake with { ContextHash = "wrong-context" }));
        Assert.True(await store.WakeAuthorizationAsync(wake));
        Assert.True(await store.WakeAuthorizationAsync(wake));
        host.Approvals.Accept(pending!.Result.ApprovalRequestId!);

        await using var resumed = host.CreateEngine(store, workflow);
        await resumed.ExecuteAsync(runId);

        Assert.Equal(1, workflow.Calls);
        Assert.Equal(2, host.Policy.Requests.Count);
        Assert.NotEqual(host.Policy.Requests[0].RequestIdentity, host.Policy.Requests[1].RequestIdentity);
        Assert.NotEqual(host.WorkflowRecords[0].Context.Identity.ExecutionRevision,
            host.WorkflowRecords[1].Context.Identity.ExecutionRevision);
        Assert.Equal(2, host.WorkflowRecords.Count);
    }

    [Fact]
    public async Task RevocationAfterApprovalWakeBlocksDispatch()
    {
        var host = CreateHost("approval-revoked");
        host.Approvals.RequireFirst = true;
        var workflow = new ProtectedWorkflow("read-file");
        var store = host.CreateStore();
        var engine = host.CreateEngine(store, workflow);
        var runId = await engine.StartAsync("protected", "1", "input");
        await engine.ExecuteAsync(runId);
        var step = (await engine.GetStepsAsync(runId)).Single(s => s.StepKey == "protected");
        var pending = await store.GetPendingAuthorizationAsync(runId, step.Id, false);
        Assert.NotNull(pending);
        Assert.True(await store.WakeAuthorizationAsync(Wake(pending!)));
        host.Approvals.Accept(pending!.Result.ApprovalRequestId!);
        host.Policy.AllowedActions.Clear();
        await engine.DisposeAsync();

        await using var resumed = host.CreateEngine(store, workflow);
        await resumed.ExecuteAsync(runId);

        Assert.Equal(0, workflow.Calls);
        Assert.Equal(2, host.Policy.Requests.Count);
        Assert.Equal(ExecutionAuthorizationDecision.Denied, host.WorkflowRecords.Last().Result.Decision);
    }

    [Fact]
    public async Task RevokedRetryIsDeniedBeforeSecondCallbackAttempt()
    {
        var host = CreateHost("revoked-retry");
        var workflow = new RetryThenRevokeWorkflow(host);
        await using var engine = host.CreateEngine(workflow);
        var runId = await engine.StartAsync("protected", "1", "input");

        await engine.ExecuteAsync(runId);
        if (host.Policy.Requests.Count == 1)
            await engine.ExecuteAsync(runId);

        Assert.Equal(1, workflow.Calls);
        Assert.Equal(2, host.Policy.Requests.Count);
        Assert.Equal(ExecutionAuthorizationDecision.Allowed, host.WorkflowRecords[0].Result.Decision);
        Assert.Equal(ExecutionAuthorizationDecision.Denied, host.WorkflowRecords[1].Result.Decision);
    }

    [Fact]
    public async Task ForwardAndCompensationUseIndependentDeclarationsAndAuthorityActions()
    {
        var host = CreateHost("compensation");
        host.Policy.AllowedActions.Add(AuthorityAction.PatchFile);
        var workflow = new CompensationWorkflow();
        await using var engine = host.CreateEngine(workflow);
        await engine.RunAsync<string, string>("protected", "1", "input");
        await engine.RollbackAsync(workflow.RunId);

        Assert.Equal(1, workflow.CompensationCalls);
        Assert.Contains(host.Policy.Requests, r => r.Action == AuthorityAction.ReadFile);
        Assert.Contains(host.Policy.Requests, r => r.Action == AuthorityAction.ListDirectory);
        Assert.Equal(2, host.WorkflowRecords.Count);
        Assert.All(host.WorkflowRecords, r => Assert.Equal(ExecutionAuthorizationDecision.Allowed, r.Result.Decision));
    }

    [Fact]
    public async Task RestartedStepFencesOldApprovalWakeAndRequiresFreshDecision()
    {
        var host = CreateHost("approval-stale-wake");
        host.Approvals.RequireFirst = true;
        var workflow = new ProtectedWorkflow("read-file");
        var store = host.CreateStore();
        var engine = host.CreateEngine(store, workflow);
        var runId = await engine.StartAsync("protected", "1", "input");
        await engine.ExecuteAsync(runId);
        var step = (await engine.GetStepsAsync(runId)).Single(s => s.StepKey == "protected");
        var pending = await store.GetPendingAuthorizationAsync(runId, step.Id, false);
        Assert.NotNull(pending);

        await engine.RestartStepAsync(runId, "protected");
        Assert.False(await store.WakeAuthorizationAsync(Wake(pending!)));
        await engine.DisposeAsync();
        await using var resumed = host.CreateEngine(store, workflow);
        await resumed.ExecuteAsync(runId);

        Assert.Equal(0, workflow.Calls);
        Assert.Equal(2, host.Policy.Requests.Count);
        Assert.NotEqual(host.Policy.Requests[0].RequestIdentity, host.Policy.Requests[1].RequestIdentity);
        Assert.NotEqual(pending!.Result.ApprovalRequestId, host.WorkflowRecords.Last().Approval!.ApprovalRequestId);
        Assert.NotEqual(pending.Context.Identity.ExecutionRevision, host.WorkflowRecords.Last().Context.Identity.ExecutionRevision);
        Assert.Equal(2, host.WorkflowRecords.Count);
    }

    [Fact]
    public async Task AggregateEvidenceFailureBlocksProtectedCallback()
    {
        var host = CreateHost("evidence-failure");
        host.WorkflowRecorder.Fail = true;
        var workflow = new ProtectedWorkflow("read-file");
        await using var engine = host.CreateEngine(workflow);
        var runId = await engine.StartAsync("protected", "1", "input");

        await engine.ExecuteAsync(runId);

        Assert.Equal(0, workflow.Calls);
        Assert.Single(host.Policy.Requests);
        Assert.Single(host.WorkflowRecorder.Attempts);
        Assert.Empty(host.WorkflowRecords);
    }

    [Fact]
    public async Task RestartWhileAggregateEvidenceIsPendingFencesOldPermitAndRunsFreshGate()
    {
        var host = CreateHost("restart-during-authorization");
        var gate = host.WorkflowRecorder.PauseNext();
        var workflow = new ProtectedWorkflow("read-file");
        await using var engine = host.CreateEngine(workflow);
        var runId = await engine.StartAsync("protected", "1", "input");

        var execution = engine.ExecuteAsync(runId);
        var firstRecord = await gate.Entered.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ExecutionAuthorizationDecision.Allowed, firstRecord.Result.Decision);
        Assert.Single(host.Policy.Requests);

        // Restart changes the durable execution revision while the adapter is still awaiting
        // aggregate evidence for the old claim. The runtime must fence that stale permit.
        await engine.RestartStepAsync(runId, "protected");
        host.Policy.AllowedActions.Clear();
        gate.Release();
        await execution;

        Assert.Equal(0, workflow.Calls);
        Assert.Single(host.Policy.Requests);
        Assert.Equal(ExecutionAuthorizationDecision.Allowed, Assert.Single(host.WorkflowRecorder.Attempts).Result.Decision);
        Assert.Empty(host.WorkflowRecords); // Restart cancelled the stale claim before its evidence append completed.

        // Zhinu returns from the stale claim after fencing it; a later engine turn owns the
        // restarted revision and performs the fresh authorization gate.
        await engine.ExecuteAsync(runId);

        Assert.Equal(0, workflow.Calls);
        Assert.Equal(2, host.Policy.Requests.Count);
        Assert.Equal(ExecutionAuthorizationDecision.Denied, Assert.Single(host.WorkflowRecords).Result.Decision);
        var step = Assert.Single(await engine.GetStepsAsync(runId));
        Assert.NotEqual("Completed", step.Status.ToString());
    }

    private static WorkflowAuthorizationWake Wake(WorkflowAuthorizationPending pending) => new()
    {
        WorkflowRunId = pending.WorkflowRunId,
        AuthorizationRequestId = pending.Context.AuthorizationRequestId,
        ApprovalRequestId = pending.Result.ApprovalRequestId!,
        ProviderId = pending.Result.ProviderId,
        BindingId = pending.BindingId,
        ContextHash = pending.ContextHash,
        Now = DateTimeOffset.UtcNow
    };

    private HufuWorkflowHost CreateHost(string name) => new(Path.Combine(root, name));

    private sealed class HufuWorkflowHost
    {
        private readonly string databasePath;
        public readonly TestAuthorityPolicy Policy = new();
        public readonly TestApprovalCoordinator Approvals = new();
        public readonly TestWorkflowRecorder WorkflowRecorder = new();
        public IReadOnlyList<WorkflowAuthorizationRecord> WorkflowRecords => WorkflowRecorder.Records;
        private readonly HufuExecutionAuthorizer authorizer;

        public HufuWorkflowHost(string databasePath)
        {
            this.databasePath = databasePath;
            Directory.CreateDirectory(databasePath);
            var authority = new CurrentAuthorityRequestAuthorizer(Policy, Policy, Policy);
            authorizer = new HufuExecutionAuthorizer("hufu-provider-v1", "test-host-v1", "test-mapping-v1",
                new TestBindingSource(), authority, Approvals, WorkflowRecorder,
                maximumValidity: TimeSpan.FromMinutes(5));
        }

        public SqliteWorkflowStore CreateStore() => new(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(databasePath, "workflow.db"), Pooling = false,
            BusyTimeout = TimeSpan.FromSeconds(15)
        });

        public WorkflowEngine CreateEngine(IWorkflow<string, string> workflow) => CreateEngine(CreateStore(), workflow);
        public WorkflowEngine CreateEngine(SqliteWorkflowStore store, IWorkflow<string, string> workflow) =>
            new(store, new WorkflowRegistry().Register("protected", "1", workflow), new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10), LeaseDuration = TimeSpan.FromSeconds(30),
                LeaseRenewalInterval = TimeSpan.FromSeconds(2),
                ExecutionAuthorization = new WorkflowExecutionAuthorizationOptions("hufu-provider-v1", "test-host-v1", authorizer)
            });
    }

    private sealed class TestBindingSource : IWorkflowAuthorityBindingSource
    {
        public ValueTask<WorkflowAuthorityBinding?> ResolveAsync(ExecutionAuthorizationContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorityContext = new AuthenticatedAuthorityContext("tenant-test", "subject-test",
                context.Identity.ExecutionId, context.Identity.ExecutionRevision, context.AuthorizationRequestId);
            var targets = context.Requirements.Select(requirement =>
            {
                if (requirement.ScopeReference != "scope:" + requirement.Resource)
                    throw new InvalidOperationException("The test host could not resolve the retained scope reference.");
                var path = requirement.Resource switch
                {
                    "source-file" => "src/source.cs",
                    "output-file" => "src/output.cs",
                    "workspace-root" => "",
                    _ => throw new InvalidOperationException("Unrecognized test logical resource.")
                };
                return new WorkflowAuthorityTarget(requirement, "workspace-test", path);
            }).ToArray();
            return ValueTask.FromResult<WorkflowAuthorityBinding?>(new(context, "test-host-v1", "test-mapping-v1",
                authorityContext, targets, DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class TestAuthorityPolicy : IAuthoritySnapshotSource, IAuthorityEvaluator, IAuthorityDecisionRecorder
    {
        private static readonly DateTimeOffset Never = DateTimeOffset.MaxValue;
        public HashSet<AuthorityAction> AllowedActions { get; } = [AuthorityAction.ReadFile, AuthorityAction.ReadMetadata, AuthorityAction.ListDirectory];
        public List<AuthorityRequest> Requests { get; } = [];
        public int Evaluations { get; private set; }
        public bool Throw { get; set; }
        public bool Unavailable { get; set; }
        private long version;

        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var grants = AllowedActions.Order().Select(action => new AuthorityGrant("grant-" + action, [action],
                new AuthorityScope("workspace-test", "", AuthorityScopeKind.Subtree), [], DateTimeOffset.UnixEpoch, Never)).ToArray();
            return ValueTask.FromResult<AuthoritySnapshot?>(new(context, "v" + Interlocked.Increment(ref version),
                [new AuthorityLayer("test-layer", grants)], [], DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
        {
            Evaluations++;
            if (Throw) throw new InvalidOperationException("test evaluator failure");
            if (Unavailable) return new(AuthorityStatus.Unavailable, "test.unavailable", snapshot.Version,
                "test-evaluator-v1", snapshot.Identity);
            var allowed = AllowedActions.Contains(request.Action);
            return new(allowed ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                allowed ? "test.permit" : "test.deny", snapshot.Version, "test-evaluator-v1", snapshot.Identity);
        }

        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class TestApprovalCoordinator : IWorkflowApprovalCoordinator
    {
        private readonly Dictionary<string, string> approvalsByExecution = new(StringComparer.Ordinal);
        public bool RequireFirst { get; set; }
        private readonly HashSet<string> acceptedApprovals = new(StringComparer.Ordinal);

        public void Accept(string approvalRequestId) => acceptedApprovals.Add(approvalRequestId);

        public ValueTask<WorkflowApprovalResult?> EvaluateAsync(WorkflowAuthorityBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var execution = binding.Context.Identity;
            string? existingApproval = null;
            var key = string.Join("|", execution.ExecutionId, execution.ParentExecutionId ?? "", execution.OperationId,
                execution.OperationPath, execution.Attempt, RevisionIdentity(execution.ExecutionRevision), execution.PlanId ?? "", execution.PlanRevision ?? "",
                string.Join(";", binding.Targets.Select(t => string.Join(":", t.Requirement.SchemaId,
                    t.Requirement.SchemaVersion, t.Requirement.Capability, t.Requirement.Resource,
                    t.Requirement.ScopeReference ?? "", t.WorkspaceId, t.RelativePath))));
            if (RequireFirst && !approvalsByExecution.TryGetValue(key, out existingApproval))
            {
                var approvalId = "approval-" + Guid.NewGuid().ToString("N");
                approvalsByExecution[key] = approvalId;
                return ValueTask.FromResult<WorkflowApprovalResult?>(new(WorkflowApprovalDecision.Required,
                    binding.Identity, "approval-evidence-pending", DateTimeOffset.UtcNow.AddMinutes(5), approvalId));
            }
            var decision = !RequireFirst ? WorkflowApprovalDecision.NotRequired :
                existingApproval is not null && acceptedApprovals.Contains(existingApproval)
                    ? WorkflowApprovalDecision.Approved : WorkflowApprovalDecision.Required;
            if (decision == WorkflowApprovalDecision.Required)
            {
                return ValueTask.FromResult<WorkflowApprovalResult?>(existingApproval is null ? null : new(decision, binding.Identity,
                    "approval-evidence-pending", DateTimeOffset.UtcNow.AddMinutes(5), existingApproval));
            }
            return ValueTask.FromResult<WorkflowApprovalResult?>(new(decision, binding.Identity,
                "approval-evidence-current", DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        private static string RevisionIdentity(string value)
        {
            // Test fixture only: Zhinu prefixes a lease generation to the semantic revision.
            // Retain approval across exact pending-wake replay, but never parse/normalize this
            // field as a production approval rule; production custody must bind its own immutable
            // step revision, attempt, plan and complete declaration to the fresh binding.
            var separator = value.IndexOf(':');
            return separator >= 0 && separator + 1 < value.Length ? value[(separator + 1)..] : value;
        }
    }

    private sealed class TestWorkflowRecorder : IWorkflowAuthorizationRecorder
    {
        public List<WorkflowAuthorizationRecord> Records { get; } = [];
        public List<WorkflowAuthorizationRecord> Attempts { get; } = [];
        public bool Fail { get; set; }
        private RecorderGate? pauseNext;

        public RecorderGate PauseNext()
        {
            var gate = new RecorderGate();
            if (Interlocked.CompareExchange(ref pauseNext, gate, null) is not null)
                throw new InvalidOperationException("A recorder pause is already configured.");
            return gate;
        }

        public ValueTask<string?> RecordAsync(WorkflowAuthorizationRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Add(record);
            if (Fail) return ValueTask.FromResult<string?>(null);
            var gate = Interlocked.Exchange(ref pauseNext, null);
            if (gate is not null) return RecordAfterReleaseAsync(gate, record, cancellationToken);
            Records.Add(record);
            return ValueTask.FromResult<string?>("workflow-evidence-" + Records.Count);
        }

        private async ValueTask<string?> RecordAfterReleaseAsync(RecorderGate gate, WorkflowAuthorizationRecord record,
            CancellationToken cancellationToken)
        {
            gate.Arrive(record);
            await gate.Released.WaitAsync(cancellationToken);
            Records.Add(record);
            return "workflow-evidence-" + Records.Count;
        }
    }

    private sealed class RecorderGate
    {
        private readonly TaskCompletionSource<WorkflowAuthorizationRecord> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WorkflowAuthorizationRecord> Entered => entered.Task;
        public Task Released => released.Task;
        public void Arrive(WorkflowAuthorizationRecord record) => entered.TrySetResult(record);
        public void Release() => released.TrySetResult();
    }

    private abstract class TestWorkflowBase
    {
        protected static WorkflowAuthorizationDeclaration Declaration(params (string Capability, string Resource)[] entries) =>
            new(entries.Select(item => new ExecutionRequirement(WorkflowAuthorityRequirements.SchemaId,
                WorkflowAuthorityRequirements.SchemaVersion, item.Capability, item.Resource, "scope:" + item.Resource)).ToArray());

        protected static StepOptions Options(params (string Capability, string Resource)[] entries) =>
            new() { Authorization = Declaration(entries) };
    }

    private sealed class ProtectedWorkflow(string capability) : TestWorkflowBase, IWorkflow<string, string>
    {
        public int Calls { get; private set; }
        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("protected", input, (value, _) =>
            {
                Calls++;
                return Task.FromResult(value);
            }, Options((capability, "source-file")), cancellationToken);
    }

    private sealed class ProtectedAndTailWorkflow(string capability) : TestWorkflowBase, IWorkflow<string, string>
    {
        public int ProtectedCalls { get; private set; }
        public int TailCalls { get; private set; }
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var output = await context.StepAsync("protected", input, (value, _) =>
            {
                ProtectedCalls++;
                return Task.FromResult(value);
            }, Options((capability, "source-file")), cancellationToken);
            return await context.StepAsync("tail", output, (value, _) =>
            {
                TailCalls++;
                return Task.FromResult(value);
            }, Options(("read-metadata", "output-file")), cancellationToken);
        }
    }

    private sealed class MultiRequirementWorkflow : TestWorkflowBase, IWorkflow<string, string>
    {
        public int Calls { get; private set; }

        public Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync("protected", input, (value, _) =>
            {
                Calls++;
                return Task.FromResult(value);
            }, Options(("read-file", "source-file"), ("read-metadata", "output-file")), cancellationToken);
    }

    private sealed class RetryThenRevokeWorkflow(HufuWorkflowHost host) : TestWorkflowBase, IWorkflow<string, string>
    {
        public int Calls { get; private set; }
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken) =>
            await context.StepAsync("protected", input, (value, _) =>
            {
                Calls++;
                host.Policy.AllowedActions.Clear();
                if (Calls == 1) throw new InvalidOperationException("force retry after authority revocation");
                return Task.FromResult(value);
            }, Options(("read-file", "source-file")) with
            {
                Retry = new RetryPolicy { MaxAttempts = 2 }
            }, cancellationToken);
    }

    private sealed class CompensationWorkflow : TestWorkflowBase, IWorkflow<string, string>
    {
        public int CompensationCalls { get; private set; }
        public Guid RunId { get; private set; }
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            RunId = context.WorkflowRunId;
            var value = await context.StepAsync("mutate", input, (_, _) => Task.FromResult("done"),
                new StepOptions
                {
                    Authorization = Declaration(("read-file", "source-file")),
                    CompensationAuthorization = Declaration(("list-directory", "workspace-root"))
                }, cancellationToken, (_, _, _) =>
                {
                    CompensationCalls++;
                    return Task.CompletedTask;
                });
            return value;
        }
    }
}
