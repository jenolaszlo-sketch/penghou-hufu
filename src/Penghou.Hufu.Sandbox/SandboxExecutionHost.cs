using Gagamba.Execution;

namespace Penghou.Hufu.Sandbox;

/// <summary>
/// HG-1 authority-mediated execution host. It joins Hufu authority to a
/// Gagamba execution provider without either system knowing the other:
/// <list type="number">
/// <item>the request is validated against the trusted execution profile;</item>
/// <item>Hufu authority is evaluated for the ExecuteProcess resource;</item>
/// <item>Gagamba negotiation and preparation prove platform capability;</item>
/// <item>authority is re-evaluated immediately before launch (TOCTOU guard);</item>
/// <item>launch binds the handle to the exact activity/grant/profile revision;</item>
/// <item>revocation terminates the associated execution domain.</item>
/// </list>
/// Nothing is launched until both an authorized request and a prepared domain
/// exist. A platform that cannot provide a required guarantee is refused, never
/// silently weakened. A prepared domain that is not launched — because of a
/// pre-launch denial, cancellation, or any fault between Prepare and Launch —
/// is explicitly discarded so provider resources are reclaimed.
/// </summary>
public sealed class SandboxExecutionHost : IAsyncDisposable
{
    private readonly IExecutionProvider _execution;
    private readonly IAuthorityRequestAuthorizer _authority;
    private readonly SandboxExecutionProfile _profile;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _byActivity = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Entry> _byHandle = new();
    private readonly HashSet<string> _authorizationRequests = new(StringComparer.Ordinal);
    private bool _disposed;

    public SandboxExecutionHost(IExecutionProvider execution, IAuthorityRequestAuthorizer authority,
        SandboxExecutionProfile profile)
    {
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public string ProfileId => _profile.ProfileId;
    public string ProfileRevision => _profile.ProfileRevision;

    /// <summary>Opaque handles of executions currently associated with the host.</summary>
    public IReadOnlyList<SandboxExecutionHandle> ActiveHandles
    {
        get { lock (_gate) return _byHandle.Values.Select(entry => entry.Handle).ToArray(); }
    }

    /// <summary>The exact authority/profile revision that shaped one running launch.</summary>
    public bool TryGetBinding(SandboxExecutionHandle handle, out SandboxExecutionBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (_byHandle.TryGetValue(handle.ExecutionId, out var entry))
            {
                binding = entry.Binding;
                return true;
            }
        }
        binding = null;
        return false;
    }

    public async ValueTask<SandboxStartResult> StartAsync(SandboxExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        // Capture the invocation and the exact profile revision once; the
        // whole operation uses this immutable snapshot, so a later profile
        // change cannot silently alter an in-flight authorization.
        if (!_profile.TryGetInvocation(request.InvocationId, out var invocation) || invocation is null)
            return new(SandboxExecutionStatus.RequirementNotAuthorized, ReasonCode: "sandbox.unknown-invocation");
        var profileId = _profile.ProfileId;
        var profileRevision = _profile.ProfileRevision;

        foreach (var name in request.Environment.Keys)
            if (!invocation.AllowedEnvironmentNames.Contains(name, StringComparer.Ordinal))
                return new(SandboxExecutionStatus.RequirementNotAuthorized, ReasonCode: "sandbox.environment-not-authorized");

        // A requirement the profile does not permit is not authorized. This is
        // distinct from a permitted requirement the host cannot provide.
        if (!WithinCeiling(request.Requirements.Required, invocation.GuaranteeCeiling) ||
            !WithinCeiling(request.Requirements.Preferred, invocation.GuaranteeCeiling))
            return new(SandboxExecutionStatus.RequirementNotAuthorized, ReasonCode: "sandbox.requirement-not-authorized");

        lock (_gate)
        {
            if (!_authorizationRequests.Add(request.Activity.ActivityId + "\0" + request.AuthorizationRequestId))
                return new(SandboxExecutionStatus.AuthorityDenied, ReasonCode: "sandbox.stale-authorization-request");
        }

        var preflight = await AuthorizeAsync(request, invocation, profileId, profileRevision, "prepare", cancellationToken)
            .ConfigureAwait(false);
        if (!preflight.IsAuthorized)
            return new(SandboxExecutionStatus.AuthorityDenied,
                ReasonCode: preflight.Decision?.ReasonCode ?? preflight.Status.ToString());

        var negotiation = ExecutionNegotiator.Negotiate(_execution.Describe(),
            request.Requirements.Required, request.Requirements.Preferred);
        if (!negotiation.Accepted)
            return new(SandboxExecutionStatus.GuaranteeUnavailable, Reasons: negotiation.Unmet);

        var prepared = _execution.Prepare(request.Requirements);
        if (prepared is not PrepareResult.Accepted accepted)
            return new(SandboxExecutionStatus.PreparationFailed, Reasons: ((PrepareResult.Rejected)prepared).Reasons);

        // From here the domain exists: every path that does not launch must
        // reclaim it. LaunchFailed already reclaimed via the provider, and
        // Discard is idempotent, so discard on any non-started outcome.
        var launched = false;
        try
        {
            var launchAuthorization = await AuthorizeAsync(request, invocation, profileId, profileRevision, "launch", cancellationToken)
                .ConfigureAwait(false);
            if (!launchAuthorization.IsAuthorized)
                return new(SandboxExecutionStatus.AuthorityDenied,
                    ReasonCode: launchAuthorization.Decision?.ReasonCode ?? launchAuthorization.Status.ToString());

            var spec = new ProcessStartSpec(invocation.Executable, invocation.Arguments,
                invocation.WorkingDirectory, BuildEnvironment(request));
            var launchResult = _execution.Launch(accepted.Prepared, spec);
            if (launchResult is not LaunchResult.Started started)
                return new(SandboxExecutionStatus.LaunchFailed, Reasons: ((LaunchResult.Failed)launchResult).Reasons);

            var handle = new SandboxExecutionHandle(Guid.NewGuid());
            var binding = new SandboxExecutionBinding(request.Activity, profileId, profileRevision, request.AuthorizationRequestId);
            var entry = new Entry(handle, started.Handle, binding);
            lock (_gate)
            {
                _byHandle[handle.ExecutionId] = entry;
                if (!_byActivity.TryGetValue(request.Activity.ActivityId, out var list))
                {
                    list = new List<Entry>();
                    _byActivity[request.Activity.ActivityId] = list;
                }
                list.Add(entry);
            }
            launched = true;
            return new(SandboxExecutionStatus.Started, Handle: handle);
        }
        finally
        {
            if (!launched)
            {
                try { _execution.Discard(accepted.Prepared); } catch { }
            }
        }
    }

    /// <summary>
    /// Terminate every execution associated with the activity. The host calls
    /// this when the authorizing grant is revoked; it is the effect of that
    /// revocation, not a substitute for the authority decision.
    /// </summary>
    public ValueTask<SandboxTerminateResult> TerminateAsync(string activityId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(activityId);
        cancellationToken.ThrowIfCancellationRequested();
        List<Entry> entries;
        lock (_gate)
        {
            if (!_byActivity.TryGetValue(activityId, out var list) || list.Count == 0)
                return ValueTask.FromResult(new SandboxTerminateResult(SandboxTerminateStatus.UnknownActivity));
            entries = new List<Entry>(list);
            _byActivity.Remove(activityId);
            foreach (var entry in entries) _byHandle.Remove(entry.Handle.ExecutionId);
        }
        var failures = new List<string>();
        foreach (var entry in entries)
            if (_execution.Terminate(entry.GagambaHandle) is TerminateResult.Failed failed)
                failures.AddRange(failed.Reasons);
        return ValueTask.FromResult(failures.Count == 0
            ? new SandboxTerminateResult(SandboxTerminateStatus.Terminated)
            : new SandboxTerminateResult(SandboxTerminateStatus.Failed, Reasons: failures));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _byActivity.Clear();
            _byHandle.Clear();
            _authorizationRequests.Clear();
        }
        return _execution.DisposeAsync();
    }

    /// <summary>
    /// Wait until the execution's domain reaches its terminal state and
    /// reclaim the handle. The token cancels the wait only; terminate
    /// explicitly to interrupt a running domain.
    /// </summary>
    public async ValueTask<SandboxCompletionResult> WaitForCompletionAsync(SandboxExecutionHandle handle,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handle);
        Entry? entry;
        lock (_gate)
        {
            if (!_byHandle.TryGetValue(handle.ExecutionId, out entry))
                return new(SandboxCompletionStatus.Failed, Reasons: new[] { "unknown execution" });
        }
        var completion = await _execution.WaitForCompletionAsync(entry.GagambaHandle, cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _byHandle.Remove(handle.ExecutionId);
            if (_byActivity.TryGetValue(entry.Binding.Activity.ActivityId, out var list))
            {
                list.RemoveAll(candidate => candidate.Handle.ExecutionId == handle.ExecutionId);
                if (list.Count == 0) _byActivity.Remove(entry.Binding.Activity.ActivityId);
            }
        }
        return completion switch
        {
            CompletionResult.NaturalExit natural => new(SandboxCompletionStatus.NaturalExit, natural.RootExitCode),
            CompletionResult.Terminated => new(SandboxCompletionStatus.Terminated),
            CompletionResult.Failed failed => new(SandboxCompletionStatus.Failed, Reasons: failed.Reasons),
            _ => new(SandboxCompletionStatus.Failed, Reasons: new[] { "unexpected completion" }),
        };
    }

    private ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(SandboxExecutionRequest request,
        SandboxApprovedInvocation invocation, string profileId, string profileRevision, string phase,
        CancellationToken cancellationToken)
    {
        var identity = $"sandbox:{profileId}:{profileRevision}:{request.Activity.ActivityId}:" +
            $"{request.Activity.GrantRevision}:{request.AuthorizationRequestId}:{phase}";
        var authorityRequest = new AuthorityRequest(request.AuthorityContext, AuthorityAction.ExecuteProcess,
            invocation.WorkspaceId, invocation.ExecutableRelativePath, identity);
        return _authority.AuthorizeAsync(authorityRequest, cancellationToken);
    }

    private IReadOnlyDictionary<string, string> BuildEnvironment(SandboxExecutionRequest request)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in _profile.PlatformEnvironment) environment[entry.Key] = entry.Value;
        foreach (var entry in request.Environment) environment[entry.Key] = entry.Value;
        return environment;
    }

    private static bool WithinCeiling(IReadOnlyList<ExecutionRequirement>? requested,
        IReadOnlyList<ExecutionRequirement> ceiling)
    {
        if (requested is null) return true;
        foreach (var requirement in requested)
            if (!ceiling.Any(candidate => candidate.Capability == requirement.Capability &&
                    candidate.Minimum >= requirement.Minimum &&
                    (candidate.AllowConstructed || !requirement.AllowConstructed)))
                return false;
        return true;
    }

    private sealed record Entry(SandboxExecutionHandle Handle, ExecutionHandle GagambaHandle, SandboxExecutionBinding Binding);
}
