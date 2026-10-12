using System.Security.Cryptography;
using System.Text;

namespace Penghou.Hufu.Broker;

/// <summary>
/// Closed, bounded worker read request. Tenant, subject, worker identity,
/// audience, delegation and generation never appear here: they come from the
/// authenticated session and reference resolution. <see cref="OperationId"/> is
/// an idempotency/correlation identity, not an authorization capability.
/// </summary>
public sealed record BrokerReadFileRequest
{
    public BrokerReadFileRequest(int protocolVersion, string operationId, string authorityReference,
        string workspaceId, string relativePath, int maxBytes, DateTimeOffset deadline)
    {
        if (!AuthorityValidation.ValidToken(operationId))
            throw new ArgumentException("A bounded operation identity is required.", nameof(operationId));
        if (!AuthorityValidation.ValidToken(authorityReference))
            throw new ArgumentException("A bounded authority reference is required.", nameof(authorityReference));
        if (!AuthorityValidation.ValidToken(workspaceId))
            throw new ArgumentException("A bounded workspace is required.", nameof(workspaceId));
        if (AuthorityValidation.NormalizePath(relativePath) != relativePath)
            throw new ArgumentException("A canonical relative path is required.", nameof(relativePath));
        if (maxBytes is < BrokerProtocol.MinimumMaxBytes or > BrokerProtocol.MaximumMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        ProtocolVersion = protocolVersion;
        OperationId = operationId;
        AuthorityReference = authorityReference;
        WorkspaceId = workspaceId;
        RelativePath = relativePath;
        MaxBytes = maxBytes;
        Deadline = deadline.ToUniversalTime();
    }

    public int ProtocolVersion { get; }
    public string OperationId { get; }
    public string AuthorityReference { get; }
    public string WorkspaceId { get; }
    public string RelativePath { get; }
    public int MaxBytes { get; }
    public DateTimeOffset Deadline { get; }
}

/// <summary>The canonical, fully bound read operation after session and reference resolution.</summary>
public sealed record BrokerReadOperation(
    string OperationId,
    BrokerSessionPrincipal Session,
    BrokerAuthorityReference Reference,
    AuthorityRequest AuthorityRequest,
    string RequestDigest);

/// <summary>Typed mapping outcome; the operation is present only on success.</summary>
public sealed record BrokerReadMapping(BrokerReadOperation? Operation, BrokerFailureKind? Failure)
{
    public bool Succeeded => Operation is not null && Failure is null;
}

/// <summary>
/// Resolves a closed DTO against the authenticated session and the opaque
/// reference into the exact canonical authority request. Caller input cannot
/// select the tenant, subject, worker, audience, delegation, generation, target
/// or wider parent context.
/// </summary>
public static class BrokerReadRequestMapper
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async ValueTask<BrokerReadMapping> MapAsync(BrokerReadFileRequest? request,
        BrokerSessionPrincipal? session, IBrokerAuthorityReferenceStore references, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || session is null || references is null) return Fail(BrokerFailureKind.InvalidRequest);
        if (request.ProtocolVersion != BrokerProtocol.Version) return Fail(BrokerFailureKind.InvalidRequest);
        if (session.ExpiresAt <= now) return Fail(BrokerFailureKind.Unauthorized);
        if (request.Deadline <= now) return Fail(BrokerFailureKind.DeadlineExceeded);
        if (request.Deadline - now > BrokerProtocol.MaximumDeadline) return Fail(BrokerFailureKind.InvalidRequest);

        BrokerAuthorityReference? reference;
        try
        {
            reference = await references.ResolveAsync(request.AuthorityReference, session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Fail(BrokerFailureKind.Unavailable); }
        if (reference is null) return Fail(BrokerFailureKind.Unauthorized);

        if (reference.Action != AuthorityAction.ReadFile) return Fail(BrokerFailureKind.InvalidRequest);
        if (request.WorkspaceId != reference.WorkspaceId || request.RelativePath != reference.RelativePath)
            return Fail(BrokerFailureKind.InvalidRequest);
        if (request.MaxBytes > reference.MaximumBytes) return Fail(BrokerFailureKind.InvalidRequest);

        var digest = Digest(request, reference);
        var authorityRequest = new AuthorityRequest(reference.AuthorityContext, reference.Action,
            reference.WorkspaceId, reference.RelativePath, "broker:" + digest);
        var operation = new BrokerReadOperation(request.OperationId, session, reference, authorityRequest, digest);
        return new(operation, null);
    }

    private static BrokerReadMapping Fail(BrokerFailureKind failure) => new(null, failure);

    private static string Digest(BrokerReadFileRequest request, BrokerAuthorityReference reference)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, true);
        Write(writer, "Penghou.Hufu.Broker.ReadOperation.v1");
        Write(writer, request.OperationId);
        Write(writer, reference.Reference);
        Write(writer, reference.SessionId);
        Write(writer, reference.DelegationId);
        Write(writer, reference.Generation);
        Write(writer, reference.FenceId);
        Write(writer, reference.WorkspaceId);
        Write(writer, reference.RelativePath);
        writer.Write((int)reference.Action);
        writer.Write(request.MaxBytes);
        writer.Write(request.Deadline.UtcTicks);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();

        static void Write(BinaryWriter writer, string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }
}
