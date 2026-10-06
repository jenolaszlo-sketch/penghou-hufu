using Gagamba.Execution;

namespace Penghou.Hufu.Zhinu.Tests;

internal static class FakeAuthority
{
    public static AuthorityRequestAuthorization Permit(AuthorityRequest request) =>
        new(request, AuthorityStatus.Permit,
            new AuthorityDecision(AuthorityStatus.Permit, "authority.permitted", "snapshot-v1", "evaluator-v1", new string('a', 64)),
            true);

    public static AuthorityRequestAuthorization Deny(AuthorityRequest request, string reason = "authority.denied") =>
        new(request, AuthorityStatus.Deny,
            new AuthorityDecision(AuthorityStatus.Deny, reason, "snapshot-v1", "evaluator-v1", new string('b', 64)),
            true);
}

internal sealed class FakeAuthorizer : IAuthorityRequestAuthorizer
{
    public List<AuthorityRequest> Requests { get; } = new();
    public bool Allow { get; set; } = true;

    public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return ValueTask.FromResult(Allow ? FakeAuthority.Permit(request) : FakeAuthority.Deny(request));
    }
}

/// <summary>Deterministic execution provider for HZ-1A tests.</summary>
internal sealed class FakeProvider : IExecutionProvider
{
    private readonly HashSet<Guid> _preparations = new();
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlatformCapabilities Capabilities { get; set; } = WellKnownPlatforms.Windows;
    public int ExitCode { get; set; }
    public bool BlockUntilTerminated { get; set; }
    public int PrepareCalls { get; private set; }
    public int LaunchCalls { get; private set; }
    public int TerminateCalls { get; private set; }

    public PlatformCapabilities Describe() => Capabilities;

    public PrepareResult Prepare(ExecutionRequirements requirements)
    {
        PrepareCalls++;
        var prepared = new PreparedExecution(Capabilities.Platform, Guid.NewGuid(), Array.Empty<string>());
        lock (_preparations) _preparations.Add(prepared.PreparationId);
        return new PrepareResult.Accepted(prepared);
    }

    public DiscardResult Discard(PreparedExecution preparation)
    {
        if (preparation.Provider != Capabilities.Platform)
            return new DiscardResult.Failed(new[] { "foreign preparation" });
        lock (_preparations) _preparations.Remove(preparation.PreparationId);
        return new DiscardResult.Discarded(preparation);
    }

    public LaunchResult Launch(PreparedExecution prepared, ProcessStartSpec process)
    {
        LaunchCalls++;
        return new LaunchResult.Started(new ExecutionHandle(Capabilities.Platform, Guid.NewGuid()));
    }

    public TerminateResult Terminate(ExecutionHandle execution)
    {
        TerminateCalls++;
        _terminated.TrySetResult();
        return new TerminateResult.Terminated(execution);
    }

    public async ValueTask<CompletionResult> WaitForCompletionAsync(ExecutionHandle execution,
        CancellationToken cancellationToken = default)
    {
        if (BlockUntilTerminated)
        {
            await _terminated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new CompletionResult.Terminated();
        }
        return new CompletionResult.NaturalExit(ExitCode);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
