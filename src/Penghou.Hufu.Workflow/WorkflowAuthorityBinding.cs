using System.Security.Cryptography;
using System.Text;
using Penghou.Workflow.Abstractions;

namespace Penghou.Hufu.Workflow;

/// <summary>The finite resource vocabulary supported by this adapter; references are not raw filesystem paths.</summary>
public static class WorkflowAuthorityRequirements
{
    public const string SchemaId = "penghou.hufu.resource";
    public const int SchemaVersion = 1;

    public static bool TryGetAction(ExecutionRequirement requirement, out AuthorityAction action)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        action = requirement.Capability switch
        {
            "read-file" => AuthorityAction.ReadFile,
            "list-directory" => AuthorityAction.ListDirectory,
            "read-metadata" => AuthorityAction.ReadMetadata,
            "patch-file" => AuthorityAction.PatchFile,
            "release" => AuthorityAction.Release,
            "write-file" => AuthorityAction.WriteFile,
            "process.execute" => AuthorityAction.ExecuteProcess,
            _ => (AuthorityAction)(-1)
        };
        return requirement.SchemaId == SchemaId && requirement.SchemaVersion == SchemaVersion &&
            requirement.ScopeReference is not null && Enum.IsDefined(action);
    }
}

/// <summary>A trusted resolution of one logical resource and retained scope to a concrete Hufu target.</summary>
public sealed record WorkflowAuthorityTarget
{
    public WorkflowAuthorityTarget(ExecutionRequirement requirement, string workspaceId, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (!WorkflowAuthorityRequirements.TryGetAction(requirement, out _))
            throw new ArgumentException("Unsupported or unscoped workflow requirement.", nameof(requirement));
        if (!AuthorityValidation.ValidToken(workspaceId)) throw new ArgumentException("Invalid workspace.", nameof(workspaceId));
        if (AuthorityValidation.NormalizePath(relativePath) != relativePath)
            throw new ArgumentException("The trusted mapper must supply a canonical Hufu path.", nameof(relativePath));
        Requirement = requirement;
        WorkspaceId = workspaceId;
        RelativePath = relativePath;
    }

    public ExecutionRequirement Requirement { get; }
    public string WorkspaceId { get; }
    public string RelativePath { get; }
}

/// <summary>A bounded immutable host-authenticated mapping of the full evaluation context, including parent ceilings.</summary>
public sealed class WorkflowAuthorityBinding
{
    public WorkflowAuthorityBinding(ExecutionAuthorizationContext context, string hostNamespace, string mappingId,
        AuthenticatedAuthorityContext authorityContext, IReadOnlyList<WorkflowAuthorityTarget> targets,
        DateTimeOffset validUntil)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targets);
        WorkflowBounds.Token(hostNamespace, nameof(hostNamespace));
        WorkflowBounds.Token(mappingId, nameof(mappingId));
        if (!AuthorityValidation.ValidContext(authorityContext)) throw new ArgumentException("Invalid authenticated context.", nameof(authorityContext));
        if (authorityContext.RunId != context.ExecutionId || authorityContext.RevisionId != context.ExecutionRevision ||
            authorityContext.FenceId != context.AuthorizationRequestId)
            throw new ArgumentException("Authenticated context must bind the exact execution, revision and fresh request.", nameof(authorityContext));
        if (validUntil == default) throw new ArgumentOutOfRangeException(nameof(validUntil));
        if (targets.Count is < 1 or > ExecutionAuthorizationContext.MaximumRequirements || targets.Count != context.Requirements.Count)
            throw new ArgumentException("Exactly one trusted target per nonempty declaration is required.", nameof(targets));
        var frozen = new List<WorkflowAuthorityTarget>(targets.Count);
        foreach (var target in targets)
        {
            if (target is null || frozen.Count >= context.Requirements.Count) throw new ArgumentException("Invalid target collection.", nameof(targets));
            frozen.Add(target);
        }
        if (frozen.Count != targets.Count || !context.Requirements.SequenceEqual(frozen.Select(t => t.Requirement)))
            throw new ArgumentException("Targets must match the complete ordered declaration snapshot.", nameof(targets));
        Context = context;
        HostNamespace = hostNamespace;
        MappingId = mappingId;
        AuthorityContext = authorityContext;
        Targets = Array.AsReadOnly(frozen.ToArray());
        ValidUntil = validUntil.ToUniversalTime();
        Identity = ComputeIdentity();
    }

    public ExecutionAuthorizationContext Context { get; }
    public string HostNamespace { get; }
    public string MappingId { get; }
    public AuthenticatedAuthorityContext AuthorityContext { get; }
    public IReadOnlyList<WorkflowAuthorityTarget> Targets { get; }
    public DateTimeOffset ValidUntil { get; }
    /// <summary>Version-one digest of full context, host mapping, authenticated actor, targets and validity; not a permission.</summary>
    public string Identity { get; }

    internal bool Matches(ExecutionAuthorizationContext context, string hostNamespace, string mappingId) =>
        HostNamespace == hostNamespace && MappingId == mappingId && Context.Identity == context.Identity &&
        Context.SchemaVersion == context.SchemaVersion && Context.AuthorizationRequestId == context.AuthorizationRequestId &&
        Context.Requirements.SequenceEqual(context.Requirements);

    private string ComputeIdentity()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        Write("Penghou.Hufu.Workflow.Binding.v1"); Write(HostNamespace); Write(MappingId);
        writer.Write(Context.SchemaVersion); Write(Context.AuthorizationRequestId);
        var id = Context.Identity;
        Write(id.ExecutionId); Write(id.ParentExecutionId); Write(id.OperationId); Write(id.OperationPath);
        writer.Write(id.Attempt); Write(id.ExecutionRevision); Write(id.PlanId); Write(id.PlanRevision);
        Write(AuthorityContext.TenantId); Write(AuthorityContext.SubjectId); Write(AuthorityContext.RunId);
        Write(AuthorityContext.RevisionId); Write(AuthorityContext.FenceId); writer.Write(ValidUntil.UtcTicks);
        writer.Write(Targets.Count);
        foreach (var target in Targets)
        {
            var r = target.Requirement;
            Write(r.SchemaId); writer.Write(r.SchemaVersion); Write(r.Capability); Write(r.Resource); Write(r.ScopeReference);
            Write(target.WorkspaceId); Write(target.RelativePath);
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();
        void Write(string? value)
        {
            writer.Write(value is not null);
            if (value is not null) writer.Write(value);
        }
    }
}

/// <summary>Authenticate the actor, resolve retained references and preserve parent authority. A null mapping fails closed.</summary>
public interface IWorkflowAuthorityBindingSource
{
    ValueTask<WorkflowAuthorityBinding?> ResolveAsync(ExecutionAuthorizationContext context, CancellationToken cancellationToken = default);
}

internal static class WorkflowBounds
{
    public static void Token(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("A bounded identifier is required.", name);
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > 256) throw new ArgumentException("Identifier exceeds 256 UTF-8 bytes.", name);
        }
        catch (EncoderFallbackException ex) { throw new ArgumentException("Invalid Unicode identifier.", name, ex); }
    }
}
