using Gagamba.Execution;

namespace Penghou.Hufu.Sandbox.Tests;

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

/// <summary>Deterministic authorizer: per-call decisions, with an optional inspector for revision/context cases.</summary>
internal sealed class ScriptedAuthorizer : IAuthorityRequestAuthorizer
{
    private readonly Queue<bool> _decisions = new();
    public List<AuthorityRequest> Requests { get; } = new();
    public bool DefaultDecision { get; set; } = true;

    /// <summary>Returns a forced decision for a request, or null to use the scripted/default decision.</summary>
    public Func<AuthorityRequest, bool?>? Inspect { get; set; }

    /// <summary>When set, the pre-launch evaluation throws (models cancellation between Prepare and Launch).</summary>
    public bool ThrowOnLaunchPhase { get; set; }

    public ScriptedAuthorizer Decide(params bool[] decisions)
    {
        foreach (var decision in decisions) _decisions.Enqueue(decision);
        return this;
    }

    public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (ThrowOnLaunchPhase && request.RequestIdentity.EndsWith(":launch", StringComparison.Ordinal))
            throw new OperationCanceledException();
        bool allow = Inspect?.Invoke(request) ?? (_decisions.Count > 0 ? _decisions.Dequeue() : DefaultDecision);
        return ValueTask.FromResult(allow ? FakeAuthority.Permit(request) : FakeAuthority.Deny(request));
    }
}

internal sealed class FakeProvider : IExecutionProvider
{
    private readonly HashSet<Guid> _preparations = new();
    private readonly object _gate = new();

    public PlatformCapabilities Capabilities { get; set; } = WellKnownPlatforms.Windows;
    public bool AcceptPrepare { get; set; } = true;
    public bool AcceptLaunch { get; set; } = true;
    public int PrepareCalls { get; private set; }
    public int LaunchCalls { get; private set; }
    public int TerminateCalls { get; private set; }
    public int DiscardCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public ExecutionRequirements? LastRequirements { get; private set; }
    public ProcessStartSpec? LastSpec { get; private set; }
    public ExecutionHandle? LastHandle { get; private set; }

    public IReadOnlyCollection<Guid> LivePreparations
    {
        get { lock (_gate) return _preparations.ToArray(); }
    }

    public PlatformCapabilities Describe() => Capabilities;

    public PrepareResult Prepare(ExecutionRequirements requirements)
    {
        PrepareCalls++;
        LastRequirements = requirements;
        if (!AcceptPrepare) return new PrepareResult.Rejected(new[] { "provider.prepare-failed" });
        var prepared = new PreparedExecution(Capabilities.Platform, Guid.NewGuid(), Array.Empty<string>());
        lock (_gate) _preparations.Add(prepared.PreparationId);
        return new PrepareResult.Accepted(prepared);
    }

    public DiscardResult Discard(PreparedExecution preparation)
    {
        DiscardCalls++;
        if (preparation.Provider != Capabilities.Platform)
            return new DiscardResult.Failed(new[] { "unknown preparation: not issued by this provider" });
        lock (_gate) _preparations.Remove(preparation.PreparationId);
        return new DiscardResult.Discarded(preparation);
    }

    public LaunchResult Launch(PreparedExecution prepared, ProcessStartSpec process)
    {
        LaunchCalls++;
        LastSpec = process;
        if (prepared.Provider != Capabilities.Platform)
            return new LaunchResult.Failed(new[] { "unknown preparation: not issued by this provider" });
        bool present;
        lock (_gate) present = _preparations.Remove(prepared.PreparationId);
        if (!present) return new LaunchResult.Failed(new[] { "unknown preparation: not issued by this provider" });
        if (!AcceptLaunch) return new LaunchResult.Failed(new[] { "provider.launch-failed" });
        LastHandle = new ExecutionHandle(Capabilities.Platform, Guid.NewGuid());
        return new LaunchResult.Started(LastHandle);
    }

    public TerminateResult Terminate(ExecutionHandle execution)
    {
        TerminateCalls++;
        return new TerminateResult.Terminated(execution);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        lock (_gate) _preparations.Clear();
        return ValueTask.CompletedTask;
    }
}
