using System.Text.Json;
using Gagamba.Execution;
using Penghou.Hufu.Sandbox;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Zhinu.Tests;

public sealed class SandboxExecutionStepTests
{
    private static CancellationToken Ct => CancellationToken.None;

    // ---- acceptance cases ----

    [Fact] // 1
    public async Task NormalExecutionReachesCompleted()
    {
        var (store, db) = NewStore();
        try
        {
            await store.InitializeAsync(Ct);
            var runId = await NewRunAsync(store);
            var provider = new FakeProvider { ExitCode = 0 };
            await using var step = NewStep(provider, new FakeAuthorizer(), store);

            var outcome = await step.ExecuteAsync(Context(runId), Input(), Ct);

            Assert.Equal(SandboxActivityStatus.Completed, outcome.Status);
            var operation = Assert.Single(await store.ListAsync(runId, cancellationToken: Ct));
            Assert.Equal(ExternalOperationStatus.Completed, operation.Status);
        }
        finally { Cleanup(db); }
    }

    [Fact] // 2
    public async Task NonZeroExitBecomesExecutionFailureWithCodePreserved()
    {
        var (store, db) = NewStore();
        try
        {
            await store.InitializeAsync(Ct);
            var runId = await NewRunAsync(store);
            var provider = new FakeProvider { ExitCode = 7 };
            await using var step = NewStep(provider, new FakeAuthorizer(), store);

            var outcome = await step.ExecuteAsync(Context(runId), Input(), Ct);

            Assert.Equal(SandboxActivityStatus.ExecutionFailed, outcome.Status);
            Assert.Equal(7, outcome.RootExitCode);
            var operation = Assert.Single(await store.ListAsync(runId, cancellationToken: Ct));
            Assert.Equal(ExternalOperationStatus.Failed, operation.Status);
        }
        finally { Cleanup(db); }
    }

    [Fact] // 3, 5
    public async Task WorkflowCancellationTerminatesAndAwaitsCompletion()
    {
        var (store, db) = NewStore();
        try
        {
            await store.InitializeAsync(Ct);
            var runId = await NewRunAsync(store);
            var provider = new FakeProvider { BlockUntilTerminated = true };
            await using var step = NewStep(provider, new FakeAuthorizer(), store);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            var task = step.ExecuteAsync(Context(runId), Input(), cts.Token);
            await PollAsync(() => provider.LaunchCalls > 0);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(1, provider.TerminateCalls); // terminated
            var operation = Assert.Single(await store.ListAsync(runId, cancellationToken: Ct));
            Assert.Equal(ExternalOperationStatus.Failed, operation.Status);
            Assert.Contains("WorkflowCancelled", operation.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("AuthorityRevoked", operation.Error, StringComparison.Ordinal);
        }
        finally { Cleanup(db); }
    }

    [Fact] // 4, 5
    public async Task AuthorityRevocationTerminatesIndependentlyAndIsDistinguishable()
    {
        var (store, db) = NewStore();
        try
        {
            await store.InitializeAsync(Ct);
            var runId = await NewRunAsync(store);
            var provider = new FakeProvider { BlockUntilTerminated = true };
            await using var step = NewStep(provider, new FakeAuthorizer(), store);

            var task = step.ExecuteAsync(Context(runId), Input(), Ct);
            await PollAsync(() => provider.LaunchCalls > 0);
            await step.RevokeAndTerminateAsync("sandbox:1", Ct);

            var outcome = await task;
            Assert.Equal(SandboxActivityStatus.Revoked, outcome.Status);
            var operation = Assert.Single(await store.ListAsync(runId, cancellationToken: Ct));
            Assert.Contains("AuthorityRevoked", operation.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkflowCancelled", operation.Error, StringComparison.Ordinal);
        }
        finally { Cleanup(db); }
    }

    [Fact] // 6, 7
    public async Task RetryReceivesFreshAuthorizationAndRejectsStaleReuse()
    {
        var (store, db) = NewStore();
        try
        {
            await store.InitializeAsync(Ct);
            var runId = await NewRunAsync(store);
            var provider = new FakeProvider { ExitCode = 0 };
            var authorizer = new FakeAuthorizer();
            await using var step = NewStep(provider, authorizer, store);
            var stepExecutionId = Guid.NewGuid();

            await step.ExecuteAsync(
                new WorkflowStepContext(runId, stepExecutionId, "sandbox", 1, 1), Input(), Ct);
            await step.ExecuteAsync(
                new WorkflowStepContext(runId, stepExecutionId, "sandbox", 2, 1), Input(), Ct);

            // Two attempts -> two authorization requests each (preflight + pre-launch),
            // all distinct: attempt 2 never reuses attempt 1's authority.
            Assert.Equal(4, authorizer.Requests.Count);
            Assert.Equal(4, authorizer.Requests.Select(r => r.RequestIdentity).Distinct().Count());
            // And two distinct durable operations (fresh idempotency key per attempt).
            Assert.Equal(2, (await store.ListAsync(runId, cancellationToken: Ct)).Count);
        }
        finally { Cleanup(db); }
    }

    [Fact] // 8
    public async Task ProfileAndAuthorityRevisionsAreCorrelatedDurably()
    {
        var (store, db) = NewStore();
        try
        {
            await store.InitializeAsync(Ct);
            var runId = await NewRunAsync(store);
            var provider = new FakeProvider { ExitCode = 0 };
            await using var step = NewStep(provider, new FakeAuthorizer(), store);

            await step.ExecuteAsync(Context(runId), Input(), Ct);

            var operation = Assert.Single(await store.ListAsync(runId, cancellationToken: Ct));
            Assert.Contains("hz1-profile", operation.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("\"profileRevision\":\"r1\"", operation.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("\"authorityRevision\":\"arev-1\"", operation.PayloadJson, StringComparison.Ordinal);
            Assert.Contains("\"authorizationRequestId\"", operation.PayloadJson, StringComparison.Ordinal);
        }
        finally { Cleanup(db); }
    }

    [Theory] // 9, 10, 11
    [InlineData(ExternalOperationStatus.Requested, true, SandboxRecoveryAction.RetryFreshAttempt)]
    [InlineData(ExternalOperationStatus.Requested, false, SandboxRecoveryAction.Abandon)]
    [InlineData(ExternalOperationStatus.Running, true, SandboxRecoveryAction.RetryFreshAttempt)]
    [InlineData(ExternalOperationStatus.Running, false, SandboxRecoveryAction.Abandon)]
    public void RecoveryFollowsNegotiatedOwnerDeathCleanup(
        ExternalOperationStatus status, bool sufficient, SandboxRecoveryAction expected)
    {
        var decisions = SandboxRecovery.Plan(new[] { Operation(status, sufficient) });
        Assert.Equal(expected, Assert.Single(decisions).Action);
    }

    [Fact] // 9 (terminal ops need no decision)
    public void TerminalOperationsAreNotRecovered()
    {
        Assert.Empty(SandboxRecovery.Plan(new[]
        {
            Operation(ExternalOperationStatus.Completed, true),
            Operation(ExternalOperationStatus.Failed, true),
            Operation(ExternalOperationStatus.Cancelled, true),
        }));
    }

    [Fact] // 12
    public void NoProviderHandleIsPersistedInTheCorrelation()
    {
        var offenders = typeof(SandboxExecutionCorrelation).GetProperties()
            .Where(p => p.PropertyType == typeof(IntPtr) || p.PropertyType == typeof(UIntPtr) ||
                p.PropertyType == typeof(ExecutionHandle) ||
                p.PropertyType.FullName?.Contains("Gagamba") == true)
            .Select(p => p.Name)
            .ToArray();
        Assert.Empty(offenders);
    }

    // ---- helpers ----

    private static (SqliteWorkflowStore Store, string Db) NewStore()
    {
        string db = Path.Combine(Path.GetTempPath(), "hz1-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteWorkflowStore(new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = db,
            BusyTimeout = TimeSpan.FromSeconds(5),
            Pooling = false,
        }));
        return (store, db);
    }

    private static void Cleanup(string db)
    {
        try { File.Delete(db); } catch { }
    }

    private static async Task<Guid> NewRunAsync(SqliteWorkflowStore store)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateRunAsync(new WorkflowRun
        {
            Id = id,
            WorkflowName = "hz1",
            WorkflowVersion = "1",
            Status = WorkflowStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
        }, Ct);
        return id;
    }

    private static SandboxExecutionStep NewStep(FakeProvider provider, FakeAuthorizer authorizer,
        IWorkflowExternalOperationRepository operations)
    {
        var invocation = new SandboxApprovedInvocation("echo", "workspace-1", "bin/echo", "/bin/echo",
            "-n hi", "/tmp", Array.Empty<string>(), new[]
            {
                ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full),
                ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Full),
                ExecutionRequirement.Require(ExecutionCapability.OwnerDeathCleanup, CapabilityLevel.Full),
            });
        var profile = new SandboxExecutionProfile("hz1-profile", "r1",
            new Dictionary<string, string>(), new[] { invocation });
        return new SandboxExecutionStep(profile, authorizer, provider, operations);
    }

    private static WorkflowStepContext Context(Guid runId, int attempt = 1, int revision = 1) =>
        new(runId, Guid.NewGuid(), "sandbox", attempt, revision);

    private static SandboxActivityInput Input() => new()
    {
        AuthorityContext = new AuthenticatedAuthorityContext("tenant-1", "subject-1", "run-1", "arev-1", "fence-1"),
        InvocationId = "echo",
        Requirements = new ExecutionRequirements(new[]
        {
            ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Full),
            ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Full),
        }),
    };

    private static WorkflowExternalOperation Operation(ExternalOperationStatus status, bool sufficient)
    {
        var correlation = new SandboxExecutionCorrelation
        {
            WorkflowRunId = Guid.NewGuid(),
            StepExecutionId = Guid.NewGuid(),
            StepKey = "sandbox",
            Attempt = 1,
            Revision = 1,
            AuthorizationRequestId = "auth",
            TenantId = "t",
            SubjectId = "s",
            AuthorityRunId = "r",
            AuthorityRevision = "ar",
            AuthorityFence = "f",
            ProfileId = "p",
            ProfileRevision = "pr",
            RequestedGuarantees = Array.Empty<string>(),
            NegotiatedGuarantees = Array.Empty<string>(),
            OwnerDeathCleanupSufficient = sufficient,
        };
        string payload = JsonSerializer.Serialize(correlation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var now = DateTimeOffset.UtcNow;
        return new WorkflowExternalOperation
        {
            OperationId = Guid.NewGuid(),
            WorkflowRunId = correlation.WorkflowRunId,
            Provider = "hufu-sandbox",
            LeaseGeneration = 1,
            Status = status,
            RecoveryIntent = ExternalOperationRecoveryIntent.Retry,
            PayloadJson = payload,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static async Task PollAsync(Func<bool> condition, int milliseconds = 10000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException("condition not met");
            await Task.Delay(20, Ct);
        }
    }
}
