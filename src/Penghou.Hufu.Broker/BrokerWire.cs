using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Hufu.Broker;

/// <summary>
/// Length-prefixed (4-byte big-endian), one-message-per-frame wire codec for
/// protocol v1. Unknown fields are rejected deterministically.
/// </summary>
internal static class BrokerFrame
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Decode<T>(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<T>(payload, Options);

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > BrokerProtocol.MaximumRequestBodyBytes) throw new InvalidDataException();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > BrokerProtocol.MaximumRequestBodyBytes) throw new InvalidDataException();
        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException();
        return payload;
    }

    private static async ValueTask<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }
}

internal sealed record WireHandshake(string Bootstrap);
internal sealed record WireHandshakeResult(int? Failure);
internal sealed record WireReadRequest(int ProtocolVersion, string OperationId, string AuthorityReference,
    string WorkspaceId, string RelativePath, int MaxBytes, DateTimeOffset Deadline);
internal sealed record WireResponse(int? Failure, string? Content, string? Version, string? Start, string? Release);
