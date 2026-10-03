using System.Text.Json;

namespace Penghou.Hufu.Biscuit;

internal static class BiscuitCodec
{
    internal static byte[] Encode(BiscuitCredentialRegistration value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, BiscuitJsonContext.Default.BiscuitCredentialRegistration);
    internal static byte[] Encode(BiscuitDecisionEvidence value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, BiscuitJsonContext.Default.BiscuitDecisionEvidence);
    private static BiscuitCredentialRegistration DeserializeRegistration(byte[] bytes) =>
        JsonSerializer.Deserialize(bytes, BiscuitJsonContext.Default.BiscuitCredentialRegistration) ??
        throw new InvalidDataException("Missing credential registration.");
    private static BiscuitDecisionEvidence DeserializeEvidence(byte[] bytes) =>
        JsonSerializer.Deserialize(bytes, BiscuitJsonContext.Default.BiscuitDecisionEvidence) ??
        throw new InvalidDataException("Missing credential evidence.");
    internal static BiscuitCredentialRegistration DecodeRegistration(byte[] bytes)
    {
        var value = DeserializeRegistration(bytes);
        if (!Encode(value).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Noncanonical registration.");
        return value;
    }
    internal static BiscuitDecisionEvidence DecodeEvidence(byte[] bytes)
    {
        var value = DeserializeEvidence(bytes);
        if (!Encode(value).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Noncanonical evidence.");
        return value;
    }
    internal static string GrantVersionIdentity(BiscuitCredentialRegistration registration)
    {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("profile", BiscuitProfile.Identity);
        writer.WriteString("issuer", registration.IssuerId);
        writer.WriteString("layer", registration.LayerId);
        writer.WriteString("version", registration.GrantVersion);
        writer.WriteString("realm", registration.Binding.Realm);
        writer.WriteString("workflow", registration.Binding.WorkflowId);
        writer.WriteString("activity", registration.Binding.ActivityId);
        writer.WriteString("audience", registration.Binding.Audience);
        var context = registration.Binding.Context;
        writer.WriteString("tenant", context.TenantId); writer.WriteString("subject", context.SubjectId);
        writer.WriteString("run", context.RunId); writer.WriteString("revision", context.RevisionId);
        writer.WriteString("fence", context.FenceId);
        var grant = registration.RootGrant;
        writer.WriteString("grant", grant.Id);
        writer.WriteStartArray("actions"); foreach (var action in grant.Actions.Order()) writer.WriteNumberValue((int)action); writer.WriteEndArray();
        Scope("scope", grant.Scope);
        writer.WriteStartArray("exclusions");
        foreach (var scope in grant.Exclusions.OrderBy(s => s.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(s => s.RelativePath, StringComparer.Ordinal).ThenBy(s => s.Kind)) Scope(null, scope);
        writer.WriteEndArray();
        writer.WriteNumber("notBefore", grant.NotBefore.UtcTicks); writer.WriteNumber("expires", grant.ExpiresAt.UtcTicks);
        writer.WriteEndObject(); writer.Flush();
        return BiscuitProfile.Hash(buffer.ToArray());
        void Scope(string? name, AuthorityScope scope)
        {
            if (name is null) writer.WriteStartObject(); else writer.WriteStartObject(name);
            writer.WriteString("workspace", scope.WorkspaceId); writer.WriteString("path", scope.RelativePath);
            writer.WriteNumber("kind", (int)scope.Kind); writer.WriteEndObject();
        }
    }
}
