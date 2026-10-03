using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Penghou.Hufu.Zhinu.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Xunit;

namespace Penghou.Hufu.Tests;

public sealed class ZhinuOperationStartTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session");

    [Fact]
    public async Task StartClaimsExactRequestedHandleAndReplayNeverInvokesParticipantAgain()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-first");
        var participant = new CountingParticipant(new ZhinuSqlitePatchStartParticipant(fixture.Owner));
        var gate = new SqliteAuthorityOperationStartGate(fixture.Authority, participant);

        var first = await gate.StartAsync(command);
        var revoked = await fixture.Authority.RevokeAsync(new AuthorityRevokeCommand(
            "revoke-after-start", Actor, command.Request.Context, 1, "test-revocation"));
        var replay = await gate.StartAsync(command);
        var operation = await fixture.Workflows.GetAsync(Guid.ParseExact(command.OperationId, "D"));

        Assert.Equal(AuthorityOperationStartStatus.Started, first.Status);
        Assert.Equal(AuthorityMutationStatus.Applied, revoked.Status);
        Assert.Equal(AuthorityOperationStartStatus.AlreadyStarted, replay.Status);
        Assert.Equal(1, participant.Calls);
        Assert.Equal(ExternalOperationStatus.Running, operation!.Status);
        Assert.Equal("step-owner", operation.OwnerId);
    }

    [Fact]
    public async Task RuntimeLeaseExpiryDuringStartReceiptInsertRollsBackTheZhinuClaim()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-expire");
        var participant = new DeadlineAdvancingParticipant(
            new ZhinuSqlitePatchStartParticipant(fixture.Owner), fixture.Clock);
        var gate = new SqliteAuthorityOperationStartGate(fixture.Authority, participant);

        var result = await gate.StartAsync(command);
        var operation = await fixture.Workflows.GetAsync(Guid.ParseExact(command.OperationId, "D"));

        Assert.Equal(AuthorityOperationStartStatus.StaleRuntime, result.Status);
        Assert.Equal(ExternalOperationStatus.Requested, operation!.Status);
        Assert.Null(operation.OwnerId);
    }

    [Fact]
    public async Task RevokedAuthorityBlocksStartWithoutAcquiringRuntimeHandle()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-revoked");
        var revoked = await fixture.Authority.RevokeAsync(new AuthorityRevokeCommand(
            "revoke-1", Actor, command.Request.Context, 1, "test-revocation"));

        var result = await new SqliteAuthorityOperationStartGate(fixture.Authority,
            new ZhinuSqlitePatchStartParticipant(fixture.Owner)).StartAsync(command);
        var operation = await fixture.Workflows.GetAsync(Guid.ParseExact(command.OperationId, "D"));

        Assert.Equal(AuthorityMutationStatus.Applied, revoked.Status);
        Assert.Equal(AuthorityOperationStartStatus.StaleAuthority, result.Status);
        Assert.Equal(ExternalOperationStatus.Requested, operation!.Status);
    }

    [Fact]
    public async Task InvalidRuntimeBindingLeavesRequestedHandleUntouched()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-binding");
        var binding = AssertBinding(command);
        var invalid = binding with { StepRevision = binding.StepRevision + 1 };
        var json = ZhinuPatchStartBindingCodec.Encode(invalid);
        var altered = command with
        {
            BindingJson = json,
            BindingIdentity = ZhinuPatchStartBindingCodec.Identity(json)
        };

        var result = await new SqliteAuthorityOperationStartGate(fixture.Authority,
            new ZhinuSqlitePatchStartParticipant(fixture.Owner)).StartAsync(altered);
        var operation = await fixture.Workflows.GetAsync(Guid.ParseExact(command.OperationId, "D"));

        Assert.Equal(AuthorityOperationStartStatus.StaleRuntime, result.Status);
        Assert.Equal(ExternalOperationStatus.Requested, operation!.Status);
    }

    [Fact]
    public async Task MissingPermitEvidenceBlocksStart()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-missing-decision");
        var missing = command with { DecisionCommandId = "decision-does-not-exist" };

        var result = await fixture.Gate.StartAsync(missing);

        Assert.Equal(AuthorityOperationStartStatus.StaleAuthority, result.Status);
        Assert.Equal(ExternalOperationStatus.Requested,
            (await fixture.Workflows.GetAsync(Guid.ParseExact(command.OperationId, "D")))!.Status);
    }

    [Fact]
    public async Task HostAuthorizationFailureAndPathOnlyStoreCannotStart()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-authorization");
        fixture.Authorizer.Result = new(AuthorityStatus.Deny, null);

        var denied = await fixture.Gate.StartAsync(command);
        Assert.Equal(AuthorityOperationStartStatus.Denied, denied.Status);
        Assert.Throws<ArgumentException>(() => new SqliteAuthorityOperationStartGate(
            new SqliteAuthorityStore(fixture.DatabasePath, fixture.Authorizer),
            new ZhinuSqlitePatchStartParticipant(fixture.Owner)));
        Assert.Equal(ExternalOperationStatus.Requested,
            (await fixture.Workflows.GetAsync(Guid.ParseExact(command.OperationId, "D")))!.Status);
    }

    [Fact]
    public void SharedOwnerRejectsMemoryDatabaseBeforeOpeningIt()
    {
        var memory = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = ":memory:", EnableWal = true, Pooling = false,
            BusyTimeout = TimeSpan.FromSeconds(1)
        });
        Assert.Throws<ArgumentException>(() => new ZhinuSqliteAuthorityDatabase(memory));
    }

    [Fact]
    public async Task WrongLiveZhinuSchemaFailsClosedWithoutAcquiringHandle()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-schema-mismatch");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath, Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var alter = connection.CreateCommand();
            alter.CommandText = "UPDATE main.zhinu_schema SET version=4 WHERE id=1";
            Assert.Equal(1, await alter.ExecuteNonQueryAsync());
        }

        var participant = new CountingParticipant(new ZhinuSqlitePatchStartParticipant(fixture.Owner));
        var result = await fixture.GateFor(fixture.Authority, participant).StartAsync(command);

        Assert.Equal(AuthorityOperationStartStatus.Unavailable, result.Status);
        Assert.Equal(0, participant.Calls);
        Assert.Equal(ExternalOperationStatus.Requested,
            await ReadOperationStatusAsync(fixture.DatabasePath, command.OperationId));
    }

    [Theory]
    [InlineData("attach")]
    [InlineData("temp-shadow")]
    public async Task DecoratedSharedOwnerFailsClosedBeforeParticipant(string decoration)
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-namespace-" + decoration);
        var decorated = new DecoratingOwner(fixture.Owner, async connection =>
        {
            await using var sql = connection.CreateCommand();
            sql.CommandText = decoration == "attach"
                ? "ATTACH DATABASE $path AS foreign_db"
                : "CREATE TEMP TABLE workflow_runs(id TEXT PRIMARY KEY)";
            if (decoration == "attach")
                sql.Parameters.AddWithValue("$path", Path.Combine(fixture.DirectoryPath, "foreign.db"));
            await sql.ExecuteNonQueryAsync();
        });
        var authority = new SqliteAuthorityStore(decorated, fixture.Authorizer);
        var participant = new CountingParticipant(new ZhinuSqlitePatchStartParticipant(decorated));

        var result = await fixture.GateFor(authority, participant).StartAsync(command);

        Assert.Equal(AuthorityOperationStartStatus.Unavailable, result.Status);
        Assert.Equal(0, participant.Calls);
        Assert.Equal(ExternalOperationStatus.Requested,
            await ReadOperationStatusAsync(fixture.DatabasePath, command.OperationId));
    }

    [Fact]
    public async Task StartLedgerCapacityFailureRollsBackRuntimeClaim()
    {
        using var fixture = await Fixture.CreateAsync(new SqliteAuthorityStoreOptions { MaxDecisionEntries = 1 });
        var first = await fixture.PrepareStartAsync("op-capacity-first");
        Assert.Equal(AuthorityOperationStartStatus.Started, (await fixture.Gate.StartAsync(first)).Status);
        var second = await fixture.CreateSiblingOperationCommandAsync(first, "op-capacity-second");

        var result = await fixture.Gate.StartAsync(second);

        Assert.Equal(AuthorityOperationStartStatus.CapacityExceeded, result.Status);
        Assert.Equal(ExternalOperationStatus.Requested,
            await ReadOperationStatusAsync(fixture.DatabasePath, second.OperationId));
    }

    [Fact]
    public async Task ReusedOperationIdWithChangedBindingIntentConflictsWithoutRedispatch()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-intent-conflict");
        var participant = new CountingParticipant(new ZhinuSqlitePatchStartParticipant(fixture.Owner));
        var gate = fixture.GateFor(fixture.Authority, participant);
        Assert.Equal(AuthorityOperationStartStatus.Started, (await gate.StartAsync(command)).Status);
        var changed = AssertBinding(command) with { EffectId = new('d', 64) };
        var json = ZhinuPatchStartBindingCodec.Encode(changed);

        var conflict = await gate.StartAsync(command with
        {
            BindingJson = json,
            BindingIdentity = ZhinuPatchStartBindingCodec.Identity(json)
        });

        Assert.Equal(AuthorityOperationStartStatus.Conflict, conflict.Status);
        Assert.Equal(1, participant.Calls);
    }

    [Theory]
    [InlineData("run-owner")]
    [InlineData("run-fence")]
    [InlineData("step-owner")]
    [InlineData("step-attempt")]
    [InlineData("step-revision")]
    [InlineData("generation-quiescing")]
    [InlineData("handle-running")]
    public async Task ChangedLiveRuntimeFactsBlockStart(string mutation)
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-runtime-" + mutation);
        var binding = AssertBinding(command);
        await fixture.ChangeRuntimeAsync(mutation, binding);

        var result = await fixture.Gate.StartAsync(command);

        Assert.Equal(AuthorityOperationStartStatus.StaleRuntime, result.Status);
        if (mutation != "handle-running")
        {
            var operation = await fixture.Workflows.GetAsync(binding.ExternalOperationId);
            Assert.Equal(ExternalOperationStatus.Requested, operation!.Status);
        }
    }

    [Fact]
    public async Task FailureAfterParticipantWriteRollsBackAndExactRetryCanStart()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = await fixture.PrepareStartAsync("op-prime");
        var gate = new SqliteAuthorityOperationStartGate(fixture.Authority,
            new ZhinuSqlitePatchStartParticipant(fixture.Owner));
        Assert.Equal(AuthorityOperationStartStatus.Started, (await gate.StartAsync(first)).Status);

        var second = await fixture.PrepareStartAsync("op-retry");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER abort_operation_start BEFORE INSERT ON hufu_operation_starts " +
                                  "WHEN NEW.operation_id='" + second.OperationId + "' BEGIN SELECT RAISE(ABORT,'injected'); END;";
            await trigger.ExecuteNonQueryAsync();
        }

        var failed = await gate.StartAsync(second);
        var afterFailure = await fixture.Workflows.GetAsync(Guid.ParseExact(second.OperationId, "D"));
        Assert.Equal(AuthorityOperationStartStatus.Unavailable, failed.Status);
        Assert.Equal(ExternalOperationStatus.Requested, afterFailure!.Status);
        Assert.Null(afterFailure.OwnerId);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER abort_operation_start";
            await drop.ExecuteNonQueryAsync();
        }

        Assert.Equal(AuthorityOperationStartStatus.Started, (await gate.StartAsync(second)).Status);
        Assert.Equal(ExternalOperationStatus.Running,
            (await fixture.Workflows.GetAsync(Guid.ParseExact(second.OperationId, "D")))!.Status);
    }

    [Fact]
    public async Task SeparateProcessesSerializeStartAndRevocationOnTheSharedDatabase()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-process-race");
        var inputPath = Path.Combine(fixture.DirectoryPath, "worker-input.json");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new WorkerInput(
            fixture.DatabasePath, Epoch, command)));
        var release = Path.Combine(fixture.DirectoryPath, "release.flag");
        var startReady = Path.Combine(fixture.DirectoryPath, "start.ready");
        var revokeReady = Path.Combine(fixture.DirectoryPath, "revoke.ready");
        using var startProcess = StartWorker(inputPath, "start", release, startReady);
        using var revokeProcess = StartWorker(inputPath, "revoke", release, revokeReady);
        try
        {
            await WaitForFileAsync(startReady, TimeSpan.FromSeconds(10));
            await WaitForFileAsync(revokeReady, TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(release, "go");
            var startOutput = await FinishWorkerAsync(startProcess, TimeSpan.FromSeconds(15), allowUnavailable: true);
            var revokeOutput = await FinishWorkerAsync(revokeProcess, TimeSpan.FromSeconds(15));
            var startStatus = Enum.Parse<AuthorityOperationStartStatus>(startOutput.Trim(), ignoreCase: false);
            var revokeStatus = Enum.Parse<AuthorityMutationStatus>(revokeOutput.Trim(), ignoreCase: false);

            if (startStatus == AuthorityOperationStartStatus.Unavailable)
            {
                // A transient SQLite busy result can be retried with the same durable operation
                // identity. AlreadyStarted remains receipt-only and is never converted to dispatch.
                startStatus = (await fixture.Gate.StartAsync(command)).Status;
            }
            Assert.Equal(AuthorityMutationStatus.Applied, revokeStatus);
            Assert.True(startStatus is AuthorityOperationStartStatus.Started or
                AuthorityOperationStartStatus.AlreadyStarted or AuthorityOperationStartStatus.StaleAuthority);
            var operation = await fixture.Workflows.GetAsync(Guid.ParseExact(command.OperationId, "D"));
            Assert.Equal(startStatus is AuthorityOperationStartStatus.Started or AuthorityOperationStartStatus.AlreadyStarted
                    ? ExternalOperationStatus.Running : ExternalOperationStatus.Requested,
                operation!.Status);
        }
        finally { KillIfRunning(startProcess); KillIfRunning(revokeProcess); }
    }

    [Fact]
    public async Task LostWorkerResponseLeavesReceiptAndReplayDoesNotDispatch()
    {
        using var fixture = await Fixture.CreateAsync();
        var command = await fixture.PrepareStartAsync("op-response-loss");
        var inputPath = Path.Combine(fixture.DirectoryPath, "worker-input.json");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(new WorkerInput(
            fixture.DatabasePath, Epoch, command)));
        var release = Path.Combine(fixture.DirectoryPath, "release.flag");
        var ready = Path.Combine(fixture.DirectoryPath, "drop.ready");
        using var process = StartWorker(inputPath, "start-drop", release, ready);
        try
        {
            await WaitForFileAsync(ready, TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(release, "go");
            var output = await FinishWorkerAsync(process, TimeSpan.FromSeconds(15));

            Assert.Empty(output);
            Assert.Equal(ExternalOperationStatus.Running,
                (await fixture.Workflows.GetAsync(Guid.ParseExact(command.OperationId, "D")))!.Status);
            var participant = new CountingParticipant(new ZhinuSqlitePatchStartParticipant(fixture.Owner));
            var replay = await new SqliteAuthorityOperationStartGate(fixture.Authority, participant).StartAsync(command);
            Assert.Equal(AuthorityOperationStartStatus.AlreadyStarted, replay.Status);
            Assert.NotNull(replay.Record);
            Assert.Equal(0, participant.Calls);
        }
        finally { KillIfRunning(process); }
    }

    private static Process StartWorker(string input, string mode, string release, string ready)
    {
        var framework = $"net{Environment.Version.Major}.0";
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var worker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Penghou.Hufu.StartWorker.Tests", "bin", configuration, framework,
            "Penghou.Hufu.StartWorker.Tests.dll"));
        Assert.True(File.Exists(worker), $"Worker fixture was not built: {worker}");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(release);
        start.ArgumentList.Add(ready);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the runtime fixture process.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed >= timeout) throw new TimeoutException($"Process did not reach its gate: {path}");
            await Task.Delay(15);
        }
    }

    private static async Task<string> FinishWorkerAsync(Process process, TimeSpan timeout, bool allowUnavailable = false)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Runtime qualification worker exceeded its deadline.");
        }
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0 || allowUnavailable && process.ExitCode == 4 && output.Trim() == "Unavailable",
            $"Worker exited {process.ExitCode}: {error}");
        return output;
    }

    private static void KillIfRunning(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static ZhinuPatchStartBinding AssertBinding(AuthorityOperationStartCommand command)
    {
        Assert.True(ZhinuPatchStartBindingCodec.TryDecode(command, out var binding));
        return binding;
    }

    private static async Task<ExternalOperationStatus> ReadOperationStatusAsync(string path, string operationId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM main.workflow_external_operations WHERE operation_id=$operation";
        command.Parameters.AddWithValue("$operation", operationId);
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (ExternalOperationStatus)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        public string DatabasePath { get; }
        public string DirectoryPath => _directory;
        public MutableTimeProvider Clock { get; }
        public SqliteDatabase ZhinuDatabase { get; }
        public ZhinuSqliteAuthorityDatabase Owner { get; }
        public SqliteWorkflowStore Workflows { get; }
        public SqliteAuthorityStore Authority { get; }
        public TestAuthorizer Authorizer { get; }
        public SqliteAuthorityOperationStartGate Gate => new(Authority, new ZhinuSqlitePatchStartParticipant(Owner));
        public SqliteAuthorityOperationStartGate GateFor(SqliteAuthorityStore authority, IAuthoritySqliteStartParticipant participant) =>
            new(authority, participant);

        private Fixture(string directory, string path, MutableTimeProvider clock,
            SqliteDatabase database, ZhinuSqliteAuthorityDatabase owner,
            SqliteWorkflowStore workflows, SqliteAuthorityStore authority, TestAuthorizer authorizer)
        {
            _directory = directory;
            DatabasePath = path;
            Clock = clock;
            ZhinuDatabase = database;
            Owner = owner;
            Workflows = workflows;
            Authority = authority;
            Authorizer = authorizer;
        }

        public static Task<Fixture> CreateAsync() => CreateAsync(null);

        public static async Task<Fixture> CreateAsync(SqliteAuthorityStoreOptions? storeOptions)
        {
            var directory = Path.Combine(Path.GetTempPath(), "hufu-zhinu-start-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "shared.db");
            var clock = new MutableTimeProvider(Epoch);
            var database = new SqliteDatabase(new ZhinuSqliteOptions
            {
                DatabasePath = path,
                EnableWal = true,
                Pooling = false,
                BusyTimeout = TimeSpan.FromSeconds(5),
                TimeProvider = clock
            });
            var owner = new ZhinuSqliteAuthorityDatabase(database);
            var workflows = new SqliteWorkflowStore(database);
            await workflows.InitializeAsync();
            var authorizer = new TestAuthorizer(Actor);
            var authority = new SqliteAuthorityStore(owner, authorizer, storeOptions);
            return new(directory, path, clock, database, owner, workflows, authority, authorizer);
        }

        public async Task<AuthorityOperationStartCommand> CreateSiblingOperationCommandAsync(
            AuthorityOperationStartCommand original, string idempotencyKey)
        {
            var binding = AssertBinding(original);
            var handle = await Workflows.RegisterAsync(new ExternalOperationRegistration
            {
                WorkflowRunId = binding.WorkflowRunId,
                StepId = binding.StepId,
                StepKey = binding.StepKey,
                StepRevision = binding.StepRevision,
                Attempt = binding.StepAttempt,
                IdempotencyKey = idempotencyKey,
                Provider = ZhinuPatchStartBindingCodec.WriterProfile,
                RecoveryIntent = ExternalOperationRecoveryIntent.Abandon
            });
            binding = binding with { ExternalOperationId = handle.OperationId, IdempotencyKey = idempotencyKey };
            var json = ZhinuPatchStartBindingCodec.Encode(binding);
            return original with
            {
                OperationId = handle.OperationId.ToString("D"),
                BindingJson = json,
                BindingIdentity = ZhinuPatchStartBindingCodec.Identity(json)
            };
        }

        public async Task<AuthorityOperationStartCommand> PrepareStartAsync(string operationKey)
        {
            var runId = Guid.NewGuid();
            await Workflows.CreateRunAsync(new WorkflowRun
            {
                Id = runId,
                WorkflowName = "test",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Pending,
                CreatedAt = Clock.GetUtcNow(),
                UpdatedAt = Clock.GetUtcNow(),
                DefinitionFingerprint = "definition-fingerprint"
            });
            var now = Clock.GetUtcNow();
            var leaseUntil = now.AddMinutes(5);
            var instance = await Workflows.CreateInstanceAsync("{\"fixture\":true}");
            var workflowGeneration = await Workflows.CreateGenerationAsync(instance.InstanceId, runId,
                "plan-revision-1", "execution-fingerprint", null);
            await Workflows.PrepareGenerationAsync(workflowGeneration.GenerationId);
            workflowGeneration = await Workflows.ActivateGenerationAsync(workflowGeneration.GenerationId, null);
            var generation = await Workflows.TryClaimRunAsync(runId, "run-owner", now, leaseUntil);
            Assert.True(generation.HasValue);
            var claim = await Workflows.ClaimStepAsync(new StepClaimRequest
            {
                WorkflowRunId = runId,
                StepKey = "patch-step",
                OutputType = "text",
                OwnerId = "step-owner",
                Now = now,
                LeaseExpiresAt = leaseUntil,
                LeaseGeneration = generation.Value
            });
            Assert.Equal(StepClaimDisposition.Acquired, claim.Disposition);
            var handle = await Workflows.RegisterAsync(new ExternalOperationRegistration
            {
                WorkflowRunId = runId,
                StepId = claim.Step.Id,
                StepKey = claim.Step.StepKey,
                StepRevision = claim.Step.Revision,
                Attempt = claim.Step.Attempt,
                IdempotencyKey = operationKey,
                Provider = ZhinuPatchStartBindingCodec.WriterProfile,
                ExternalId = null,
                RecoveryIntent = ExternalOperationRecoveryIntent.Abandon
            });

            var operationId = handle.OperationId;

            var revisionId = "plan-revision-1";
            var context = new AuthenticatedAuthorityContext("tenant", "subject", runId.ToString("D"),
                revisionId, generation.Value.ToString(CultureInfo.InvariantCulture));
            var request = new AuthorityRequest(context, AuthorityAction.PatchFile, "workspace", "src/file.txt", new('f', 64));
            var snapshot = new AuthoritySnapshot(context, "snapshot-v1",
            [new AuthorityLayer("run", [new AuthorityGrant("patch-grant",
                [AuthorityAction.PatchFile, AuthorityAction.Release],
                new AuthorityScope("workspace", "src/file.txt", AuthorityScopeKind.Exact), [], now.AddMinutes(-1), now.AddHours(1))])], [], now.AddHours(1));
            var publish = await Authority.PublishAsync(new AuthorityPublishCommand(
                "publish-" + operationKey, Actor, snapshot, 0));
            Assert.Equal(AuthorityMutationStatus.Applied, publish.Status);
            var cedar = new CedarAuthorityEvaluator().EvaluateDetailed(snapshot, request, now);
            Assert.Equal(AuthorityStatus.Permit, cedar.Decision.Status);
            var evidenceJson = JsonSerializer.Serialize(new
            {
                schema = cedar.SchemaDigest,
                entities = cedar.EntityDigest,
                layers = cedar.Layers.Select(layer => new
                {
                    layer.LayerId,
                    layer.SnapshotDigest,
                    layer.SchemaDigest,
                    layer.PolicyDigest,
                    layer.EntityDigest,
                    policyValid = layer.PolicyValidation?.IsValid,
                    requestValid = layer.RequestValidation is not null,
                    cleanAllow = layer.Authorization?.IsCleanAllow
                }).ToArray()
            });
            var decision = new AuthorityDecisionRecord("decision-" + operationKey, request,
                cedar.Decision, now, "cedar-details-v1", evidenceJson);
            Assert.Equal(AuthorityEvidenceStatus.Recorded,
                (await Authority.RecordDecisionAsync(Actor, decision)).Status);

            var binding = new ZhinuPatchStartBinding(
                operationId, runId, workflowGeneration.GenerationId, generation.Value, "run-owner", "definition-fingerprint", "execution-fingerprint",
                leaseUntil.ToString("O", CultureInfo.InvariantCulture), claim.Step.Id, claim.Step.StepKey,
                claim.Step.Revision, claim.Step.Attempt, claim.Step.LeaseGeneration, "step-owner",
                claim.Step.LeaseExpiresAt!.Value.ToString("O", CultureInfo.InvariantCulture), ZhinuPatchStartBindingCodec.WriterProfile, null,
                operationKey, revisionId, context.SubjectId, "effect-1", "attempt-1", new('a', 64), new('b', 64),
                new('c', 64), "workspace", "src/file.txt", request.RequestIdentity,
                ZhinuPatchStartBindingCodec.WriterProfile, ZhinuPatchStartBindingCodec.NamespaceProfile,
                "ntfs:0000000000000001:00000000000000000000000000000001",
                new('D', 64), new('E', 64), 12, 13);
            var json = ZhinuPatchStartBindingCodec.Encode(binding);
            return new(Actor, operationId.ToString("D"), request, decision.CommandId, 1,
                ZhinuPatchStartBindingCodec.Identity(json), json);
        }

        public async Task ChangeRuntimeAsync(string mutation, ZhinuPatchStartBinding binding)
        {
            if (mutation == "generation-quiescing")
            {
                var result = await Workflows.PauseGenerationAsync(binding.WorkflowGenerationId);
                Assert.Equal(WorkflowGenerationStatus.Quiescing, result.Status);
                return;
            }
            if (mutation == "handle-running")
            {
                var result = await Workflows.AcquireAsync(binding.ExternalOperationId, "other-worker", binding.RunLeaseGeneration);
                Assert.Equal(ExternalOperationStatus.Running, result.Status);
                return;
            }
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = mutation switch
            {
                "run-owner" => "UPDATE main.workflow_runs SET lease_owner='other-run-owner' WHERE id=$run",
                "run-fence" => "UPDATE main.workflow_runs SET lease_generation=lease_generation+1 WHERE id=$run",
                "step-owner" => "UPDATE main.workflow_steps SET lease_owner='other-step-owner' WHERE id=$step",
                "step-attempt" => "UPDATE main.workflow_steps SET attempt=attempt+1 WHERE id=$step",
                "step-revision" => "UPDATE main.workflow_steps SET revision=revision+1 WHERE id=$step",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };
            command.Parameters.AddWithValue("$run", binding.WorkflowRunId.ToString("D"));
            command.Parameters.AddWithValue("$step", binding.StepId.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void AdvanceTo(DateTimeOffset value) => Now = value;
    }

    private sealed record WorkerInput(string DatabasePath, DateTimeOffset Now, AuthorityOperationStartCommand Command);

    private sealed class DecoratingOwner(ISqliteAuthorityDatabase inner, Func<SqliteConnection, Task> decorate)
        : ISqliteAuthorityDatabase
    {
        public TimeProvider TimeProvider => inner.TimeProvider;
        public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenAsync(cancellationToken);
            try
            {
                await decorate(connection);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class TestAuthorizer(AuthorityStoreActor actor) : IAuthorityStoreAuthorizer
    {
        public AuthorityStoreAuthorization Result { get; set; } = new(AuthorityStatus.Permit, actor);
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class CountingParticipant(IAuthoritySqliteStartParticipant inner) : IAuthoritySqliteStartParticipant
    {
        public string ProfileIdentity => inner.ProfileIdentity;
        public int Calls { get; private set; }
        public async ValueTask<SqliteRuntimeStartResult> TryStartAsync(SqliteConnection connection,
            SqliteTransaction transaction, AuthorityOperationStartCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return await inner.TryStartAsync(connection, transaction, command, cancellationToken);
        }
    }

    private sealed class DeadlineAdvancingParticipant(IAuthoritySqliteStartParticipant inner, MutableTimeProvider clock)
        : IAuthoritySqliteStartParticipant
    {
        public string ProfileIdentity => inner.ProfileIdentity;
        public async ValueTask<SqliteRuntimeStartResult> TryStartAsync(SqliteConnection connection,
            SqliteTransaction transaction, AuthorityOperationStartCommand command,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.TryStartAsync(connection, transaction, command, cancellationToken);
            if (result.Status == AuthorityStatus.Permit) clock.AdvanceTo(result.ValidUntil.AddTicks(1));
            return result;
        }
    }
}
