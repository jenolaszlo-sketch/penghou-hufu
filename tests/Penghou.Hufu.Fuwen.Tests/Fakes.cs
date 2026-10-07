using Gagamba.Execution;
using Penghou.Fuwen;

namespace Penghou.Hufu.Fuwen.Tests;

internal sealed class FixedAuthority : IActivityAuthorityContextSource
{
    public AuthenticatedAuthorityContext ContextFor(Penghou.Fuwen.ActivityExecutionRequest request) =>
        new("tenant-1", "subject-1", "run-1", "arev-1", "fence-1");
}

internal sealed class UnusedContext : IContextProvider
{
    public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class UnusedInference : IInferenceExecutor
{
    public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeAuthorizer : IAuthorityRequestAuthorizer
{
    public List<AuthorityRequest> Requests { get; } = new();

    public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return ValueTask.FromResult(new AuthorityRequestAuthorization(request, AuthorityStatus.Permit,
            new AuthorityDecision(AuthorityStatus.Permit, "authority.permitted", "snapshot-v1", "evaluator-v1", new string('a', 64)),
            true));
    }
}

internal sealed class FakeProvider : IExecutionProvider
{
    private readonly HashSet<Guid> _preparations = new();

    public PlatformCapabilities Capabilities { get; set; } = WellKnownPlatforms.Windows;
    public int ExitCode { get; set; }
    public int PrepareCalls { get; private set; }
    public int LaunchCalls { get; private set; }

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

    public TerminateResult Terminate(ExecutionHandle execution) => new TerminateResult.Terminated(execution);

    public ValueTask<CompletionResult> WaitForCompletionAsync(ExecutionHandle execution,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<CompletionResult>(new CompletionResult.NaturalExit(ExitCode));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
