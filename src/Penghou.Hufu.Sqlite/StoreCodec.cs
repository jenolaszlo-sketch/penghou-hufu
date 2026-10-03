using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Hufu.Sqlite;

// This codec is a storage boundary, not a caller-controlled polymorphic serializer.
internal static class StoreCodec
{
    internal const int MaxBodyBytes = 2_097_152;
    internal const int MaxEvidenceBytes = 262_144;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    internal sealed record SnapshotData(AuthenticatedAuthorityContext Context, string Version,
        AuthorityLayer[] Layers, AuthorityScope[] MandatoryDenials, DateTimeOffset ValidUntil, string Identity)
    {
        internal static SnapshotData From(AuthoritySnapshot snapshot) => new(snapshot.Context, snapshot.Version,
            snapshot.Layers.ToArray(), snapshot.MandatoryDenials.ToArray(), snapshot.ValidUntil, snapshot.Identity);
        internal AuthoritySnapshot ToSnapshot()
        {
            var snapshot = new AuthoritySnapshot(Context, Version, Layers, MandatoryDenials, ValidUntil);
            if (snapshot.Identity != Identity) throw new InvalidDataException();
            return snapshot;
        }
    }
    internal sealed record ChangeData(long Sequence, AuthorityChangeKind Kind, string CommandId,
        AuthorityStoreActor Actor, AuthenticatedAuthorityContext Context, SnapshotData? Snapshot,
        string ReasonCode, DateTimeOffset RecordedAt)
    {
        internal static ChangeData From(AuthorityChangeRecord record) => new(record.Sequence, record.Kind,
            record.CommandId, record.Actor, record.Context, record.Snapshot is null ? null : SnapshotData.From(record.Snapshot),
            record.ReasonCode, record.RecordedAt);
        internal AuthorityChangeRecord ToRecord()
        {
            var snapshot = Snapshot?.ToSnapshot();
            if (Sequence < 1 || !Enum.IsDefined(Kind) || !AuthorityValidation.ValidToken(CommandId) ||
                !AuthorityStoreValidation.ValidActor(Actor) || !AuthorityValidation.ValidContext(Context) ||
                Actor.TenantId != Context.TenantId || !AuthorityValidation.ValidToken(ReasonCode) ||
                RecordedAt.Offset != TimeSpan.Zero ||
                (Kind == AuthorityChangeKind.Published ? snapshot?.Context != Context : snapshot is not null))
                throw new InvalidDataException();
            return new(Sequence, Kind, CommandId, Actor, Context, snapshot, ReasonCode, RecordedAt);
        }
    }
    internal static byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxBodyBytes) throw new InvalidDataException();
        return bytes;
    }
    internal static T Decode<T>(byte[] bytes, string hash)
    {
        if (bytes.Length > MaxBodyBytes || Hash(bytes) != hash) throw new InvalidDataException();
        var result = JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException();
        // Missing fields, alternate encodings and duplicate envelope fields are not canonical records.
        if (!Encode(result).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException();
        return result;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string SubjectKey(AuthoritySubject subject) => Hash(Encode(new
        { Domain = "Penghou.Hufu.Subject.v1", subject.TenantId, subject.SubjectId, subject.RunId }));
    internal static bool ValidDecision(AuthorityDecisionRecord? record)
    {
        if (record is null || !AuthorityValidation.ValidToken(record.CommandId) ||
            !AuthorityValidation.IsValidRequest(record.Request) || record.Decision is not { } decision ||
            !Enum.IsDefined(decision.Status) || !AuthorityValidation.ValidToken(decision.ReasonCode) ||
            !AuthorityValidation.ValidToken(decision.SnapshotVersion) || !AuthorityValidation.ValidToken(decision.EvaluatorIdentity) ||
            !ValidHash(decision.SnapshotIdentity) || record.EvaluatedAt.Offset != TimeSpan.Zero ||
            !AuthorityValidation.ValidToken(record.EvidenceFormat)) return false;
        return ValidEvidenceJson(record.EvidenceJson);
    }
    internal static bool ValidEvidenceJson(string? json)
    {
        if (json is null || json.Length > MaxEvidenceBytes) return false;
        try
        {
            if (Utf8.GetByteCount(json) > MaxEvidenceBytes) return false;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind == JsonValueKind.Object && UniqueFields(document.RootElement);
        }
        catch { return false; }
    }
    private static bool UniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueFields(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!UniqueFields(item)) return false;
        return true;
    }
}
