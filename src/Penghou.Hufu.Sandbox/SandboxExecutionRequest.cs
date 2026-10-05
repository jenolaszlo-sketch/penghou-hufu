using System.Collections.ObjectModel;
using Gagamba.Execution;

namespace Penghou.Hufu.Sandbox;

/// <summary>
/// The authority revision that permitted one execution. A launched handle is
/// associated with exactly this binding so a revoked or superseded grant can
/// be matched to the executions it authorized.
/// </summary>
public sealed record SandboxActivityIdentity
{
    public SandboxActivityIdentity(string activityId, string grantId, string grantRevision, string policyRevision)
    {
        if (!AuthorityValidation.ValidToken(activityId))
            throw new ArgumentException("A bounded activity id is required.", nameof(activityId));
        if (!AuthorityValidation.ValidToken(grantId))
            throw new ArgumentException("A bounded grant id is required.", nameof(grantId));
        if (!AuthorityValidation.ValidToken(grantRevision))
            throw new ArgumentException("A bounded grant revision is required.", nameof(grantRevision));
        if (!AuthorityValidation.ValidToken(policyRevision))
            throw new ArgumentException("A bounded policy revision is required.", nameof(policyRevision));
        ActivityId = activityId;
        GrantId = grantId;
        GrantRevision = grantRevision;
        PolicyRevision = policyRevision;
    }

    public string ActivityId { get; }
    public string GrantId { get; }
    public string GrantRevision { get; }
    public string PolicyRevision { get; }
}

/// <summary>
/// One activity's request to run one approved invocation. The host supplies
/// the authenticated authority context and a fresh authorization request id
/// per attempt; the requested guarantees and environment values must stay
/// within the invocation's registered ceiling and allowed names.
/// </summary>
public sealed record SandboxExecutionRequest
{
    public SandboxExecutionRequest(AuthenticatedAuthorityContext authorityContext, SandboxActivityIdentity activity,
        string invocationId, IReadOnlyDictionary<string, string> environment,
        ExecutionRequirements requirements, string authorizationRequestId)
    {
        if (!AuthorityValidation.ValidContext(authorityContext))
            throw new ArgumentException("A bounded authenticated authority context is required.", nameof(authorityContext));
        ArgumentNullException.ThrowIfNull(activity);
        if (!AuthorityValidation.ValidToken(invocationId))
            throw new ArgumentException("A bounded invocation id is required.", nameof(invocationId));
        if (!AuthorityValidation.ValidToken(authorizationRequestId))
            throw new ArgumentException("A bounded authorization request id is required.", nameof(authorizationRequestId));
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(requirements);
        var entries = new Dictionary<string, string>(environment, StringComparer.Ordinal);
        foreach (var entry in entries)
            if (!Bounds.EnvironmentName(entry.Key) || entry.Value.Contains('\0'))
                throw new ArgumentException("Invalid environment entry.", nameof(environment));
        AuthorityContext = authorityContext;
        Activity = activity;
        InvocationId = invocationId;
        Environment = new ReadOnlyDictionary<string, string>(entries);
        Requirements = requirements;
        AuthorizationRequestId = authorizationRequestId;
    }

    public AuthenticatedAuthorityContext AuthorityContext { get; }
    public SandboxActivityIdentity Activity { get; }
    public string InvocationId { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }
    public ExecutionRequirements Requirements { get; }
    /// <summary>Fresh per start attempt; reuse is treated as a stale/replayed authority.</summary>
    public string AuthorizationRequestId { get; }
}
