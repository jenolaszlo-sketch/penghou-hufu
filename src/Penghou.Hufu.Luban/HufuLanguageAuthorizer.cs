using System.Security.Cryptography;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban;
using Penghou.Luban.Language;

namespace Penghou.Hufu.Luban;

/// <summary>
/// Initial read-profile consumer. Requires host-authenticated current snapshots
/// and mandatory decision evidence; it is not an atomic mutation-start service.
/// </summary>
public sealed class HufuLanguageAuthorizer : ILanguageAuthorizer
{
    private readonly AuthenticatedAuthorityContext _context;
    private readonly EffectInvocation _invocation;
    private readonly CompiledDocument _document;
    private readonly IAuthorityRequestAuthorizer _requestAuthorizer;
    private readonly Dictionary<string, CompiledNode> _nodes;

    public HufuLanguageAuthorizer(AuthenticatedAuthorityContext context, EffectInvocation invocation,
        CompiledDocument document, IAuthoritySnapshotSource source, IAuthorityEvaluator evaluator,
        IAuthorityDecisionRecorder recorder, TimeProvider? timeProvider = null)
        : this(context, invocation, document, new CurrentAuthorityRequestAuthorizer(source, evaluator, recorder, timeProvider))
    {
    }

    /// <summary>Uses a host-selected asynchronous verifier while preserving exact semantic and resource projection.</summary>
    public HufuLanguageAuthorizer(AuthenticatedAuthorityContext context, EffectInvocation invocation,
        CompiledDocument document, IAuthorityRequestAuthorizer requestAuthorizer)
    {
        if (!AuthorityValidation.ValidContext(context) || invocation is null || invocation.SubjectId != context.SubjectId ||
            !AuthorityValidation.ValidToken(invocation.EffectId) || !AuthorityValidation.ValidToken(invocation.AttemptId))
            throw new ArgumentException("An exact host-authenticated invocation is required.");
        _context = context; _invocation = invocation;
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _requestAuthorizer = requestAuthorizer ?? throw new ArgumentNullException(nameof(requestAuthorizer));
        if (document.Versions != new LanguageVersions() || document.Statements.Count > 64 ||
            document.Statements.Sum(s => (long)s.Count) > 128 || !AuthorityValidation.ValidToken(document.Workspace.Value))
            throw new ArgumentException("Unsupported compiled read document.");
        _nodes = document.Statements.SelectMany(s => s).Where(n => n.Stage is ReadStage or FindStage or SearchStage)
            .ToDictionary(n => n.Identity, StringComparer.Ordinal);
    }

    public async ValueTask<LanguageAuthorityDecision> AuthorizeAsync(LanguageAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || request.Invocation != _invocation || request.DocumentIdentity != _document.Identity ||
            request.Workspace != _document.Workspace || request.Versions != _document.Versions ||
            request.DescriptorVersion != LanguageProfile.DescriptorVersion || !Enum.IsDefined(request.Phase) || request.NodeIdentity is null ||
            !_nodes.TryGetValue(request.NodeIdentity, out var node) || request.Descriptor != node.Descriptor || request.Stage != node.Stage)
            return new(LanguageAuthorityStatus.Deny);
        if (request.Phase is LanguageAuthorizationPhase.Preflight or LanguageAuthorizationPhase.EffectStart &&
            (request.Action is not null || request.ResourceRequestIdentity is not null))
            return new(LanguageAuthorityStatus.Deny);
        // Dynamic input scopes need their own reviewed mapping, never a root fallback.
        var root = node.Stage switch { ReadStage r => r.Path, FindStage f => f.Root, SearchStage s => s.Root, _ => null };
        if (root is null) return new(LanguageAuthorityStatus.Unavailable);
        var concrete = request.Phase == LanguageAuthorizationPhase.ResourceAccess ||
            request.Action is not null || request.ResourceRequestIdentity is not null;
        if (!concrete && request.ResourcePath is not null && request.ResourcePath != root)
            return new(LanguageAuthorityStatus.Deny);
        List<(AuthorityAction Action, string Path)> requirements;
        try
        {
            var known = Canonical(root);
            requirements = [];
            if (concrete)
            {
                if (request.ResourcePath is null || request.Action is null || request.ResourceRequestIdentity is null ||
                    !AuthorityValidation.ValidToken(request.ResourceRequestIdentity.Value.Value)) return new(LanguageAuthorityStatus.Deny);
                var path = Canonical(request.ResourcePath);
                if (!Within(node.Stage, known, path, request.Action.Value)) return new(LanguageAuthorityStatus.Deny);
                requirements.Add((Map(request.Action.Value), path));
                if (request.Phase == LanguageAuthorizationPhase.Release) requirements.Add((AuthorityAction.Release, path));
            }
            else
            {
                if (request.Action is not null || request.ResourceRequestIdentity is not null) return new(LanguageAuthorityStatus.Deny);
                switch (node.Stage)
                {
                    case ReadStage: requirements.Add((AuthorityAction.ReadFile, known)); break;
                    case FindStage: requirements.Add((AuthorityAction.ListDirectory, known)); break;
                    case SearchStage:
                        requirements.Add((AuthorityAction.ListDirectory, known)); requirements.Add((AuthorityAction.ReadFile, known)); break;
                }
                foreach (var ancestor in Ancestors(known)) requirements.Add((AuthorityAction.ReadMetadata, ancestor));
                requirements.Add((AuthorityAction.Release, known));
            }
        }
        catch { return new(LanguageAuthorityStatus.Deny); }
        foreach (var (action, path) in requirements.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = Digest(request, action, path);
            var authorityRequest = new AuthorityRequest(_context, action, request.Workspace.Value, path, digest);
            AuthorityRequestAuthorization authorization;
            try
            {
                authorization = await _requestAuthorizer.AuthorizeAsync(authorityRequest, cancellationToken)
                    .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { return new(LanguageAuthorityStatus.Unavailable); }
            cancellationToken.ThrowIfCancellationRequested();
            if (authorization is null || authorization.Request != authorityRequest || !Enum.IsDefined(authorization.Status))
                return new(LanguageAuthorityStatus.Unavailable);
            if (authorization.Status != AuthorityStatus.Permit)
                return new(authorization.Status == AuthorityStatus.Deny ? LanguageAuthorityStatus.Deny : LanguageAuthorityStatus.Unavailable);
            if (!authorization.IsAuthorized) return new(LanguageAuthorityStatus.Unavailable);
        }
        return new(LanguageAuthorityStatus.Permit);
    }

    private static string Canonical(string path) => AuthorityValidation.NormalizePath(
        WindowsWorkspacePath.Normalize(new WorkspacePath(path), true).Value);
    private static AuthorityAction Map(ResourceAction action) => action switch
    {
        ResourceAction.ReadFile => AuthorityAction.ReadFile,
        ResourceAction.ListDirectory => AuthorityAction.ListDirectory,
        ResourceAction.ReadMetadata => AuthorityAction.ReadMetadata,
        _ => throw new ArgumentException("Unsupported resource action.")
    };
    private static bool Within(LanguageStage stage, string root, string path, ResourceAction action)
    {
        var ancestor = path.Length == 0 || path == root || root.StartsWith(path + "/", StringComparison.Ordinal);
        var descendant = root.Length == 0 || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
        return stage switch
        {
            ReadStage => action == ResourceAction.ReadFile && path == root || action == ResourceAction.ReadMetadata && ancestor,
            FindStage => action == ResourceAction.ListDirectory && descendant || action == ResourceAction.ReadMetadata && (ancestor || descendant),
            SearchStage => action is ResourceAction.ReadFile or ResourceAction.ListDirectory && descendant ||
                action == ResourceAction.ReadMetadata && (ancestor || descendant),
            _ => false
        };
    }
    private static IEnumerable<string> Ancestors(string path)
    {
        yield return "";
        if (path.Length == 0) yield break;
        var parts = path.Split('/'); var current = "";
        foreach (var part in parts) { current = current.Length == 0 ? part : current + "/" + part; yield return current; }
    }
    private static string Digest(LanguageAuthorizationRequest request, AuthorityAction action, string path)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        Text("Penghou.Hufu.Luban.ReadDecision.v1"); Text(request.Invocation.SubjectId); Text(request.Invocation.EffectId); Text(request.Invocation.AttemptId);
        Text(request.DocumentIdentity); Text(request.NodeIdentity); Text(request.Descriptor); Text(request.Workspace.Value);
        writer.Write((int)request.Phase); writer.Write((int)action); Text(path); Text(request.ResourceRequestIdentity?.Value ?? "");
        writer.Flush(); return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    }
}
