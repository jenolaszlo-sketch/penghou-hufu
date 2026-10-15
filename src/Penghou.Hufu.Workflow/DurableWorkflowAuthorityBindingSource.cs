using Penghou.Workflow.Abstractions;

namespace Penghou.Hufu.Workflow;

/// <summary>
/// Opaque durable run authority identity, decoded from the stored binding.
/// Zhinu persists the encoded form verbatim and never interprets it.
/// </summary>
public sealed record WorkflowRunAuthorityIdentity(string TenantId, string SubjectId, string DelegationId, string WorkspaceId);

/// <summary>
/// Versioned codec for the opaque durable binding. The host writes
/// <see cref="Encode"/> output into the run at admission; this source decodes
/// it on every evaluation. Anything malformed decodes to nothing and the
/// evaluation fails closed downstream.
/// </summary>
public static class WorkflowRunAuthorityBindingCodec
{
    public const string Prefix = "hufu-workflow-binding-v1";
    private const int MaximumLength = 1024;

    public static string Encode(WorkflowRunAuthorityIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!AuthorityValidation.ValidToken(identity.TenantId) || !AuthorityValidation.ValidToken(identity.SubjectId) ||
            !AuthorityValidation.ValidToken(identity.DelegationId) || !AuthorityValidation.ValidToken(identity.WorkspaceId))
            throw new ArgumentException("A bounded tenant, subject, delegation, and workspace are required.", nameof(identity));
        return string.Join("|", Prefix, identity.TenantId, identity.SubjectId, identity.DelegationId, identity.WorkspaceId);
    }

    public static bool TryDecode(string? opaque, out WorkflowRunAuthorityIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrEmpty(opaque) || opaque.Length > MaximumLength) return false;
        var parts = opaque.Split('|');
        if (parts.Length != 5 || parts[0] != Prefix) return false;
        if (!AuthorityValidation.ValidToken(parts[1]) || !AuthorityValidation.ValidToken(parts[2]) ||
            !AuthorityValidation.ValidToken(parts[3]) || !AuthorityValidation.ValidToken(parts[4])) return false;
        identity = new WorkflowRunAuthorityIdentity(parts[1], parts[2], parts[3], parts[4]);
        return true;
    }
}

/// <summary>
/// Reads the stored opaque authority binding for one execution. Implemented
/// by the host over its durable run store (for example Zhinu
/// <c>WorkflowRun.AuthoritySubjectBinding</c>). Returns the stored value
/// verbatim; never reconstructs identity from workflow arguments or caller
/// state.
/// </summary>
public interface IWorkflowRunBindingReader
{
    ValueTask<string?> ReadAuthorityBindingAsync(string executionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Maps one declared requirement plus the decoded run identity to a concrete
/// Hufu target. Implemented by the host: the workspace and canonical path
/// are host facts, never caller input, and non-canonical paths must fail
/// (return null) rather than normalize attacker-controlled identity.
/// </summary>
public interface IWorkflowAuthorityTargetMapper
{
    ValueTask<WorkflowAuthorityTarget?> MapAsync(ExecutionAuthorizationContext context,
        ExecutionRequirement requirement, WorkflowRunAuthorityIdentity identity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Production binding source over durable run bindings. Every evaluation
/// re-reads the stored binding (restart-safe: identity, not permission),
/// decodes it strictly, and requires every requirement to map to a trusted
/// target. Anything missing, malformed, or unmapped resolves to null, and the
/// authorizer fails closed. The stored subject always wins: evaluation
/// contexts carry no caller identity to conflict with.
/// </summary>
public sealed class DurableWorkflowAuthorityBindingSource : IWorkflowAuthorityBindingSource
{
    private readonly IWorkflowRunBindingReader _reader;
    private readonly IWorkflowAuthorityTargetMapper _mapper;
    private readonly string _hostNamespace;
    private readonly string _mappingId;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _validFor;

    public DurableWorkflowAuthorityBindingSource(IWorkflowRunBindingReader reader,
        IWorkflowAuthorityTargetMapper mapper, string hostNamespace, string mappingId,
        TimeProvider? clock = null, TimeSpan? validFor = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        WorkflowBounds.Token(hostNamespace, nameof(hostNamespace));
        WorkflowBounds.Token(mappingId, nameof(mappingId));
        _hostNamespace = hostNamespace;
        _mappingId = mappingId;
        _clock = clock ?? TimeProvider.System;
        _validFor = validFor ?? TimeSpan.FromMinutes(5);
        if (_validFor <= TimeSpan.Zero || _validFor > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(validFor));
    }

    public async ValueTask<WorkflowAuthorityBinding?> ResolveAsync(ExecutionAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (context.Requirements.Count == 0) return null;
            var opaque = await _reader.ReadAuthorityBindingAsync(context.ExecutionId, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!WorkflowRunAuthorityBindingCodec.TryDecode(opaque, out var identity)) return null;
            var authorityContext = new AuthenticatedAuthorityContext(identity.TenantId, identity.SubjectId,
                context.ExecutionId, context.ExecutionRevision, context.AuthorizationRequestId);
            var targets = new List<WorkflowAuthorityTarget>(context.Requirements.Count);
            foreach (var requirement in context.Requirements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = await _mapper.MapAsync(context, requirement, identity, cancellationToken)
                    .ConfigureAwait(false);
                if (target is null) return null;
                targets.Add(target);
            }
            var now = _clock.GetUtcNow();
            return new WorkflowAuthorityBinding(context, _hostNamespace, _mappingId, authorityContext,
                targets, now.Add(_validFor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
