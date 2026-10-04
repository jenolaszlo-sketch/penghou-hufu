using BiscuitSharp;
using Microsoft.Data.Sqlite;
using Penghou.Hufu.Biscuit.Sqlite;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;

namespace Penghou.Hufu.Biscuit.Tests;

internal sealed class BiscuitIntegrationFixture : IDisposable, IBiscuitAuthorityHost,
    IBiscuitRegistryAuthorizer, IAuthorityStoreAuthorizer, ISqliteAuthorityDatabase
{
    internal static readonly AuthenticatedAuthorityContext Context = new("tenant","subject","run","revision","fence");
    internal static readonly AuthorityStoreActor Actor = new("tenant","host","session");
    internal static readonly DateTimeOffset Now = new(2026,10,2,12,0,0,TimeSpan.Zero);
    internal const string Realm = "realm";
    private readonly string _directory = Path.Combine(Path.GetTempPath(),"hufu-biscuit-test-"+Guid.NewGuid().ToString("N"));
    internal string DatabasePath { get; }
    internal BiscuitKeyRing Keys { get; } = new();
    internal SqliteAuthorityStore Store { get; }
    internal SqliteBiscuitCredentialRegistry Registry { get; private set; }
    internal CedarAuthorityEvaluator Cedar { get; } = new();
    internal BiscuitAuthorityService Service { get; private set; }
    internal MutableClock Clock { get; } = new();
    public TimeProvider TimeProvider => Clock;
    internal AuthoritySnapshot Snapshot { get; private set; }
    internal BiscuitWorkloadBinding Binding { get; set; } = new(Context,Realm,"workflow","activity","audience");
    internal string GrantVersion { get; set; } = "grant-v1";
    internal bool Authenticate { get; set; } = true;
    internal bool ApproveIssue { get; set; } = true;
    internal bool ApproveDerivation { get; set; } = true;
    internal bool BindResource { get; set; } = true;
    internal Func<AuthorityRequest,BiscuitResourceBinding?>? ResourceBindingFactory { get; set; }
    internal AuthorityStatus RegistryStatus { get; set; } = AuthorityStatus.Permit;
    internal Action<BiscuitRegistryAccess>? OnRegistryAuthorization { get; set; }
    internal bool FailRegistryEvidence { get; set; }
    internal bool FailCoreEvidence { get; set; }
    internal bool CancelAfterBinding { get; set; }
    internal CancellationTokenSource? CancellationSource { get; set; }
    internal long Sequence { get; private set; }

    internal BiscuitIntegrationFixture()
    {
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory,"authority.db");
        Keys.AddSigningKey(Realm,"key-1",BiscuitPrivateKey.Generate());
        Store = new(this,this);
        Registry = new(this,DatabasePath,this);
        Snapshot = CreateSnapshot("v1");
        Service = CreateService();
    }
    internal BiscuitAuthorityService CreateService(IBiscuitDecisionRecorder? recorder = null, BiscuitAuthorizerLimits? limits = null) =>
        new(this,Keys,Registry,new AuthorityStoreSnapshotSource(Store,Actor),Cedar,
            recorder ?? new BiscuitSqliteDecisionRecorder(Store,Registry),
            limits ?? new BiscuitAuthorizerLimits(2000,50,TimeSpan.FromMilliseconds(100)),Clock);
    internal void SetRegistryOptions(SqliteBiscuitRegistryOptions options)
    {
        Registry = new(this,DatabasePath,this,options); Service = CreateService();
    }
    internal AuthoritySnapshot CreateSnapshot(string version, IReadOnlyList<AuthorityLayer>? layers=null,
        IReadOnlyList<AuthorityScope>? denials=null) => new(Context,version,
        layers ?? [new("workflow",[new("grant",[AuthorityAction.ReadFile,AuthorityAction.ListDirectory,AuthorityAction.ReadMetadata,AuthorityAction.PatchFile,AuthorityAction.Release,AuthorityAction.WriteFile],
            new("workspace","src",AuthorityScopeKind.Subtree),
            [new("workspace","src/private",AuthorityScopeKind.Subtree)],Now.AddMinutes(-1),Now.AddHours(1))])],
        denials ?? [],Now.AddHours(1));
    internal async Task PublishAsync(AuthoritySnapshot? snapshot = null)
    {
        var proposed = snapshot ?? Snapshot;
        var result = await Store.PublishAsync(new(Guid.NewGuid().ToString("N"),Actor,proposed,Sequence));
        Assert.Equal(AuthorityMutationStatus.Applied,result.Status);
        Snapshot = proposed; Sequence = result.Record!.Sequence;
    }
    internal AuthorityRequest Request(AuthorityAction action=AuthorityAction.ReadFile,string path="src/file.txt") =>
        new(Context,action,"workspace",path,Guid.NewGuid().ToString("N"));
    internal async Task<BiscuitEnvelope> IssueAsync()
    {
        var current = await Store.ReadCurrentAsync(Actor, Context);
        if (current.Status != AuthorityReadStatus.Active)
        {
            using var connection = await OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA database_list";
            using var reader = await command.ExecuteReaderAsync();
            var databases = new List<string>();
            while (await reader.ReadAsync()) databases.Add(reader.GetString(1) + "=" + reader.GetString(2));
            Assert.Fail($"Current authority fixture status={current.Status}; configured={DatabasePath}; source={connection.DataSource}; databases={string.Join(';', databases)}; now={Clock.GetUtcNow():O}");
        }
        var result = await Service.IssueAsync(new(Context,"workflow","grant"));
        Assert.True(result.IsSuccess,result.FailureCode.ToString());
        return result.Envelope!;
    }
    internal static string Fingerprint(BiscuitEnvelope envelope) => BiscuitProfile.Hash(envelope.GetTokenBytes());
    internal string EvaluatorIdentity(AuthorityRequest request) => BiscuitProfile.GetEvaluatorIdentity(Service.EngineIdentity,
        Cedar.EvaluateDetailed(Snapshot,request,Clock.GetUtcNow()).Decision.EvaluatorIdentity);
    internal SqliteAuthorityOperationStartGate StartGate(AuthorityRequest request, RuntimeParticipant participant,
        string? engine = null, string? evaluator = null) => new(Store,new BiscuitSqliteStartParticipant(
            Registry,participant,engine ?? Service.EngineIdentity,BiscuitProfile.MappingIdentity,evaluator ?? EvaluatorIdentity(request)));
    internal async Task ExecuteAsync(string sql)
    {
        using var connection = await OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText=sql;
        await command.ExecuteNonQueryAsync();
    }
    internal async Task<long> RuntimeCountAsync()
    {
        using var connection = await OpenAsync(); using var command=connection.CreateCommand();
        command.CommandText="SELECT count(*) FROM runtime_starts";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource=DatabasePath,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false,DefaultTimeout=1 }.ToString());
        try
        {
            await connection.OpenAsync(ct);
            using var command=connection.CreateCommand();
            command.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; CREATE TABLE IF NOT EXISTS runtime_starts(id TEXT PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public ValueTask<BiscuitAuthenticatedWorkload?> AuthenticateAsync(AuthenticatedAuthorityContext context,CancellationToken ct=default) =>
        ValueTask.FromResult(Authenticate && context==Context ? new BiscuitAuthenticatedWorkload(Actor,Binding) : null);
    public ValueTask<BiscuitIssuanceApproval?> ApproveIssueAsync(BiscuitIssueRequest request,CancellationToken ct=default) =>
        ValueTask.FromResult(Authenticate && ApproveIssue && request.Context==Context && request.LayerId=="workflow" && request.GrantId=="grant" ?
            new BiscuitIssuanceApproval(new(Actor,Binding),Snapshot,"workflow","grant",GrantVersion) : null);
    public ValueTask<bool> ApproveDerivationAsync(BiscuitAuthenticatedWorkload workload,BiscuitCredentialRegistration parent,
        BiscuitRestriction restriction,CancellationToken ct=default) => ValueTask.FromResult(Authenticate && ApproveDerivation);
    public ValueTask<BiscuitResourceBinding?> BindResourceAsync(BiscuitAuthenticatedWorkload workload,AuthorityRequest request,
        string? startBindingIdentity,CancellationToken ct=default)
    {
        if (CancelAfterBinding) CancellationSource!.Cancel();
        return ValueTask.FromResult(BindResource ? ResourceBindingFactory is null ? new BiscuitResourceBinding(request,"test-provider","object-1","effect-1",startBindingIdentity) : ResourceBindingFactory(request) : null);
    }
    public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(BiscuitRegistryAccess request,CancellationToken ct=default)
    {
        OnRegistryAuthorization?.Invoke(request);
        return ValueTask.FromResult(new AuthorityStoreAuthorization(request.Actor==Actor && request.Realm==Realm &&
            (request.Context is null || request.Context==Context) ?
            FailRegistryEvidence && request.Operation==BiscuitRegistryOperation.RecordVerification ? AuthorityStatus.Unavailable : RegistryStatus :
            AuthorityStatus.Deny,Actor));
    }
    public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,CancellationToken ct=default) =>
        ValueTask.FromResult(new AuthorityStoreAuthorization(request.Actor==Actor && request.Subject==AuthoritySubject.From(Context) ?
            FailCoreEvidence && request.Operation==AuthorityStoreOperation.RecordDecision ? AuthorityStatus.Unavailable : AuthorityStatus.Permit :
            AuthorityStatus.Deny,Actor));
    public void Dispose()
    {
        Keys.Dispose();
        var root=Path.GetFullPath(Path.GetTempPath());
        if (_directory.StartsWith(root,StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(_directory).StartsWith("hufu-biscuit-test-",StringComparison.Ordinal))
            Directory.Delete(_directory,true);
    }

    internal sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    internal sealed class RuntimeParticipant : IAuthoritySqliteStartParticipant
    {
        public string ProfileIdentity => "hufu-biscuit-test-runtime-v1";
        internal int Calls { get; private set; }
        internal AuthorityStatus Status { get; set; } = AuthorityStatus.Permit;
        internal Action? OnStart { get; set; }
        public async ValueTask<SqliteRuntimeStartResult> TryStartAsync(SqliteConnection connection,SqliteTransaction transaction,
            AuthorityOperationStartCommand command,CancellationToken ct=default)
        {
            Calls++;
            using var sql=connection.CreateCommand(); sql.Transaction=transaction;
            sql.CommandText="INSERT INTO runtime_starts VALUES($id)"; sql.Parameters.AddWithValue("$id",command.OperationId);
            await sql.ExecuteNonQueryAsync(ct); OnStart?.Invoke();
            return new(Status,Now.AddHours(1));
        }
    }
}
