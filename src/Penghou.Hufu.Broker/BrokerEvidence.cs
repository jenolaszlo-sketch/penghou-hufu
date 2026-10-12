namespace Penghou.Hufu.Broker;

/// <summary>
/// Start evidence: the operation was admitted under an ordered current decision.
/// It records admission, not the result and not delivery.
/// </summary>
public sealed record BrokerStartEvidence(
    string OperationId,
    string SessionId,
    string WorkerIdentity,
    string Audience,
    string ReferenceIdentity,
    string AuthorityBinding,
    string OperationDigest,
    DateTimeOffset RecordedAt);

/// <summary>
/// Release evidence: a specific result was authorized for disclosure to a
/// recipient. It records disclosure authorization, not delivery.
/// </summary>
public sealed record BrokerReleaseEvidence(
    string OperationId,
    string SessionId,
    string RecipientAudience,
    string ReferenceIdentity,
    string AuthorityBinding,
    string OperationDigest,
    DateTimeOffset RecordedAt);

/// <summary>Durable receipt for one recorded evidence artifact.</summary>
public sealed record BrokerEvidenceReceipt(string EvidenceId);

/// <summary>Host-owned mandatory evidence recorder. A null receipt blocks dispatch/disclosure.</summary>
public interface IBrokerEvidenceRecorder
{
    ValueTask<BrokerEvidenceReceipt?> RecordStartAsync(BrokerStartEvidence evidence, CancellationToken cancellationToken = default);
    ValueTask<BrokerEvidenceReceipt?> RecordReleaseAsync(BrokerReleaseEvidence evidence, CancellationToken cancellationToken = default);
}
