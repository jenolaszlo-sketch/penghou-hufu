using System.Text.Json;
using Gagamba.Execution;
using Penghou.Fuwen;
using Penghou.Hufu.Sandbox;

namespace Penghou.Hufu.Fuwen;

/// <summary>
/// Supplies the host-authenticated authority context for one activity. Identity
/// and authority are host facts; the plan never supplies them.
/// </summary>
public interface IActivityAuthorityContextSource
{
    AuthenticatedAuthorityContext ContextFor(ActivityExecutionRequest request);
}

/// <summary>
/// FZ-1 host activity executor: a Fuwen activity's neutral execution intent is
/// resolved to a trusted sandbox invocation and executed through the frozen
/// HG-1 authority path. The plan contributes only intent and ordinary inputs.
/// Executable, arguments, working directory, environment allow-list, guarantee
/// ceiling, authority request identity, grant revision, trusted profile
/// revision, and provider handles remain downstream-controlled.
/// </summary>
public sealed class SandboxActivityExecutor : IActivityExecutor
{
    private readonly SandboxExecutionHost _host;
    private readonly Dictionary<string, string> _profileToInvocation;
    private readonly INeutralGuaranteeMap _guaranteeMap;
    private readonly IActivityAuthorityContextSource _authority;

    public SandboxActivityExecutor(SandboxExecutionHost host,
        IReadOnlyDictionary<string, string> profileToInvocation,
        INeutralGuaranteeMap guaranteeMap,
        IActivityAuthorityContextSource authority)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentNullException.ThrowIfNull(profileToInvocation);
        _profileToInvocation = new Dictionary<string, string>(profileToInvocation, StringComparer.Ordinal);
        _guaranteeMap = guaranteeMap ?? throw new ArgumentNullException(nameof(guaranteeMap));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var intent = request.ExecutionIntent;
        if (intent is null)
            return Refuse("The activity requires a neutral execution intent.");
        if (!_profileToInvocation.TryGetValue(intent.Profile, out string? invocationId))
            return Refuse($"No trusted profile is registered for '{intent.Profile}'.");
        if (!_guaranteeMap.TryMap(intent.Required, out var required, out string error))
            return Refuse(error);
        if (!_guaranteeMap.TryMap(intent.Preferred, out var preferred, out error))
            return Refuse(error);

        // The logical profile is resolved here; the plan never names the trusted
        // ProfileId/ProfileRevision or the authority request identity.
        var identity = new SandboxActivityIdentity(
            request.Invocation.OperationKey,
            request.Invocation.ExecutionFingerprint,
            request.Invocation.StepRevision,
            request.Invocation.EffectiveRequestFingerprint);
        var sandbox = new SandboxExecutionRequest(
            _authority.ContextFor(request),
            identity,
            invocationId,
            new Dictionary<string, string>(),
            new ExecutionRequirements(required, preferred),
            request.Invocation.OperationKey);

        var start = await _host.StartAsync(sandbox, cancellationToken).ConfigureAwait(false);
        if (!start.IsStarted)
            return FailureFor(start);

        var completion = await _host.WaitForCompletionAsync(start.Handle!, cancellationToken).ConfigureAwait(false);
        return completion.Status switch
        {
            SandboxCompletionStatus.NaturalExit when completion.RootExitCode == 0 =>
                Succeeded(request, invocationId, 0),
            SandboxCompletionStatus.NaturalExit =>
                Provider("Execution failed.", $"RootExitCode={completion.RootExitCode}"),
            SandboxCompletionStatus.Terminated => new ActivityExecutionResult(
                new ExecutionFailure(ExecutionFailureKind.Cancelled, ExecutionFailureCode.Cancelled,
                    "Execution was terminated (workflow cancellation or authority revocation).")),
            _ => Provider("Execution completion failed.", null),
        };
    }

    private static ActivityExecutionResult Succeeded(ActivityExecutionRequest request, string invocationId, int rootExitCode)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new { invocation = invocationId, rootExitCode }));
        return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(document.RootElement.Clone()));
    }

    private static ActivityExecutionResult Refuse(string reason) =>
        new(new ExecutionFailure(ExecutionFailureKind.Admission, ExecutionFailureCode.PolicyRejected, reason));

    private static ActivityExecutionResult Provider(string message, string? providerCode) =>
        new(new ExecutionFailure(ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError,
            message, providerCode: providerCode));

    private static ActivityExecutionResult FailureFor(SandboxStartResult start) => start.Status switch
    {
        SandboxExecutionStatus.RequirementNotAuthorized => new ActivityExecutionResult(
            new ExecutionFailure(ExecutionFailureKind.Admission, ExecutionFailureCode.PolicyRejected,
                "Requirement not authorized: " + Describe(start))),
        SandboxExecutionStatus.AuthorityDenied => new ActivityExecutionResult(
            new ExecutionFailure(ExecutionFailureKind.Admission, ExecutionFailureCode.PolicyRejected,
                "Authority denied: " + Describe(start))),
        SandboxExecutionStatus.GuaranteeUnavailable => new ActivityExecutionResult(
            new ExecutionFailure(ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError,
                "Guarantee unavailable: " + Describe(start))),
        _ => new ActivityExecutionResult(
            new ExecutionFailure(ExecutionFailureKind.Provider, ExecutionFailureCode.ProviderError,
                "Sandbox failed: " + Describe(start))),
    };

    private static string Describe(SandboxStartResult start) =>
        start.ReasonCode ?? string.Join("; ", start.Reasons ?? Array.Empty<string>());
}
