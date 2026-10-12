using System.Security.Cryptography;
using System.Text;
using Penghou.Hufu.IO;
using Penghou.IO.Abstractions;

namespace Penghou.Hufu.Broker;

/// <summary>The real provider access for one reference: a workspace provider and its mutation journal.</summary>
public sealed record BrokerWorkspaceSource(IWorkspaceProvider Provider, IResourceMutationJournal Journal);

/// <summary>
/// One broker read outcome. On any non-permit path <see cref="Failure"/> is set,
/// <see cref="Content"/> is empty and nothing protected is disclosed.
/// </summary>
public sealed record BrokerReadResponse(
    BrokerFailureKind? Failure,
    ReadOnlyMemory<byte> Content = default,
    ResourceVersion? Version = null,
    string? StartEvidenceId = null,
    string? ReleaseEvidenceId = null)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// The AE-2.2 enforcement vertical. It maps the request under the authenticated
/// session, runs AE-1 authorization and commits start evidence inside the ordered
/// start boundary, performs the real provider read with no arbiter held, then
/// independently re-authorizes release before any bytes are returned.
/// </summary>
public sealed class BrokerReadHandler
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly BrokerStartArbiter _arbiter;
    private readonly IAuthorityRequestAuthorizer _authority;
    private readonly IBrokerAuthorityReferenceStore _references;
    private readonly IBrokerEvidenceRecorder _evidence;
    private readonly Func<BrokerAuthorityReference, BrokerWorkspaceSource> _workspaceSource;
    private readonly TimeProvider _clock;

    public BrokerReadHandler(BrokerStartArbiter arbiter, IAuthorityRequestAuthorizer authority,
        IBrokerAuthorityReferenceStore references, IBrokerEvidenceRecorder evidence,
        Func<BrokerAuthorityReference, BrokerWorkspaceSource> workspaceSourceFactory, TimeProvider? timeProvider = null)
    {
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _workspaceSource = workspaceSourceFactory ?? throw new ArgumentNullException(nameof(workspaceSourceFactory));
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<BrokerReadResponse> HandleAsync(BrokerReadFileRequest? request,
        BrokerSessionPrincipal? session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mappedAt = _clock.GetUtcNow();
        BrokerReadMapping mapping;
        try
        {
            mapping = await BrokerReadRequestMapper.MapAsync(request, session, _references, mappedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        if (!mapping.Succeeded) return new(mapping.Failure);
        var operation = mapping.Operation!;
        var reference = operation.Reference;
        var binding = BrokerAuthorityBinding.From(reference);

        // Ordered start boundary: AE-1 authorization plus mandatory start evidence.
        BrokerFailureKind? startFailure = null;
        string? startEvidenceId = null;
        await _arbiter.RunStartAsync(binding, async token =>
        {
            AuthorityRequestAuthorization? decision;
            try { decision = await _authority.AuthorizeAsync(operation.AuthorityRequest, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { startFailure = BrokerFailureKind.Unavailable; return; }
            if (decision is null || !decision.IsAuthorized)
            {
                startFailure = decision?.Status == AuthorityStatus.Deny ? BrokerFailureKind.Unauthorized : BrokerFailureKind.Unavailable;
                return;
            }
            var receipt = await _evidence.RecordStartAsync(
                StartEvidence(operation, reference, binding, _clock.GetUtcNow()), token).ConfigureAwait(false);
            if (receipt is null) { startFailure = BrokerFailureKind.Unavailable; return; }
            startEvidenceId = receipt.EvidenceId;
        }, cancellationToken).ConfigureAwait(false);
        if (startFailure is not null) return new(startFailure);

        // Provider read: no arbiter is held across file I/O.
        ReadOnlyMemory<byte> content;
        ResourceVersion? version;
        try
        {
            using var access = CreateAccess(reference);
            var result = await access.ReadFileAsync(BuildReadRequest(reference), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded) return new(MapReadFailure(result.Failure));
            content = result.Value!.Content;
            version = result.Value.Version;
            if (content.Length > reference.MaximumBytes || content.Length > BrokerProtocol.MaximumReadResultBytes)
                return new(BrokerFailureKind.OperationFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(BrokerFailureKind.OperationFailed); }

        // Independent release authorization for the recipient before bytes leave.
        var releaseAt = _clock.GetUtcNow();
        if (session!.ExpiresAt <= releaseAt) return new(BrokerFailureKind.Unauthorized);
        AuthorityRequestAuthorization? release;
        try { release = await _authority.AuthorizeAsync(operation.AuthorityRequest, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(BrokerFailureKind.Unavailable); }
        if (release is null || !release.IsAuthorized)
            return new(release?.Status == AuthorityStatus.Deny ? BrokerFailureKind.Unauthorized : BrokerFailureKind.Unavailable);
        BrokerEvidenceReceipt? releaseReceipt;
        try
        {
            releaseReceipt = await _evidence.RecordReleaseAsync(
                ReleaseEvidence(operation, reference, binding, releaseAt), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(BrokerFailureKind.Unavailable); }
        if (releaseReceipt is null) return new(BrokerFailureKind.Unavailable);

        return new((BrokerFailureKind?)null, content, version, startEvidenceId, releaseReceipt.EvidenceId);
    }

    private HufuWorkspaceAccess CreateAccess(BrokerAuthorityReference reference)
    {
        var (provider, journal) = _workspaceSource(reference);
        var workspace = new WorkspaceId(reference.WorkspaceId);
        var authorizer = new HufuResourceAuthorizer(reference.AuthorityContext, BuildInvocation(reference), workspace, _authority);
        return new HufuWorkspaceAccess(provider, authorizer, journal);
    }

    private static FileReadRequest BuildReadRequest(BrokerAuthorityReference reference)
    {
        var workspace = new WorkspaceId(reference.WorkspaceId);
        var path = new WorkspacePath(reference.RelativePath);
        var maxBytes = Math.Min(reference.MaximumBytes, BrokerProtocol.MaximumReadResultBytes);
        var invocation = BuildInvocation(reference);
        var request = new FileReadRequest(invocation, workspace, path, new IoLimits(maxBytes));
        return request with { Invocation = invocation with { RequestIdentity = ResourceRequestIdentity.Compute(request) } };
    }

    private static HostInvocation BuildInvocation(BrokerAuthorityReference reference) =>
        new("broker-invocation", reference.SubjectId, reference.DelegationId, reference.Generation, reference.FenceId,
            null, new RequestIdentity("broker"));

    private static BrokerFailureKind MapReadFailure(ResourceFailureKind? failure) => failure switch
    {
        ResourceFailureKind.AuthorizationDenied => BrokerFailureKind.Unauthorized,
        ResourceFailureKind.AuthorizationUnavailable => BrokerFailureKind.Unavailable,
        _ => BrokerFailureKind.OperationFailed
    };

    private static BrokerStartEvidence StartEvidence(BrokerReadOperation operation, BrokerAuthorityReference reference,
        BrokerAuthorityBinding binding, DateTimeOffset recordedAt) =>
        new(operation.OperationId, operation.Session.SessionId, operation.Session.WorkerIdentity, operation.Session.Audience,
            ReferenceIdentity(reference.Reference), binding.Key, operation.RequestDigest, recordedAt);

    private static BrokerReleaseEvidence ReleaseEvidence(BrokerReadOperation operation, BrokerAuthorityReference reference,
        BrokerAuthorityBinding binding, DateTimeOffset recordedAt) =>
        new(operation.OperationId, operation.Session.SessionId, operation.Session.Audience,
            ReferenceIdentity(reference.Reference), binding.Key, operation.RequestDigest, recordedAt);

    private static string ReferenceIdentity(string reference) =>
        Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(reference))).ToLowerInvariant();
}
