using System.Diagnostics;
using System.Text.Json;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Penghou.Hufu.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite;

// Private qualification process. This fixture is never a production authentication service.
if (args.Length != 4) return 2;
var input = JsonSerializer.Deserialize<WorkInput>(await File.ReadAllTextAsync(args[0])) ?? throw new InvalidDataException();
var clock = new FixedClock(input.Now);
var owner = new SqliteDatabase(new ZhinuSqliteOptions
{
    DatabasePath = input.DatabasePath, EnableWal = true, Pooling = false,
    BusyTimeout = TimeSpan.FromSeconds(1), TimeProvider = clock
});
var database = new ZhinuSqliteAuthorityDatabase(owner);
var store = new SqliteAuthorityStore(database, new FixtureAuthorizer(input.Command.Actor));
var gate = new SqliteAuthorityOperationStartGate(store, new ZhinuSqlitePatchStartParticipant(database));
await File.WriteAllTextAsync(args[3], "ready");
var elapsed = Stopwatch.StartNew();
while (!File.Exists(args[2]))
{
    if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) return 3;
    await Task.Delay(10);
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
if (args[1] is "start" or "start-drop")
{
    var result = await gate.StartAsync(input.Command, cancellation.Token);
    if (args[1] == "start") Console.WriteLine(result.Status);
    return result.Status == AuthorityOperationStartStatus.Unavailable ? 4 : 0;
}
if (args[1] == "revoke")
{
    var result = await store.RevokeAsync(new("worker-revoke:" + input.Command.OperationId,
        input.Command.Actor, input.Command.Request.Context, input.Command.ExpectedSequence, "test.worker-revoked"), cancellation.Token);
    Console.WriteLine(result.Status);
    return result.Status == AuthorityMutationStatus.Unavailable ? 4 : 0;
}
return 2;

internal sealed record WorkInput(string DatabasePath, DateTimeOffset Now, AuthorityOperationStartCommand Command);
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
internal sealed class FixtureAuthorizer(AuthorityStoreActor actor) : IAuthorityStoreAuthorizer
{
    public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AuthorityStoreAuthorization(request.Actor == actor ? AuthorityStatus.Permit : AuthorityStatus.Deny,
            request.Actor == actor ? actor : null));
    }
}
