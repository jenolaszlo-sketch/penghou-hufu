using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Penghou.Hufu.Broker;

// Private cross-process qualification worker. Never a production client.
// Commands:
//   read <pipe> <bootstrap> <reference> <workspace> <path> <maxBytes> <deadlineUtcTicks> <operationId>
//   raw  <pipe> <bootstrap> <hexReadPayload>
if (args.Length < 1) return 2;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
try
{
    if (args[0] is "read" && args.Length == 9)
    {
        var request = new BrokerReadFileRequest(BrokerProtocol.Version, args[8], args[3], args[4], args[5],
            int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture),
            new DateTimeOffset(long.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture), TimeSpan.Zero));
        await using var client = await BrokerPipeClient.ConnectAsync(args[1], args[2], timeout.Token)
            .ConfigureAwait(false);
        if (client is null)
        {
            Console.WriteLine("FAIL HandshakeRejected");
            return 1;
        }
        var response = await client.ReadAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.Succeeded)
        {
            Console.WriteLine($"OK {response.Content.Length}");
            return 0;
        }
        Console.WriteLine($"FAIL {response.Failure}");
        return 1;
    }
    if (args[0] is "raw" && args.Length == 4)
    {
        using var stream = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(timeout.Token).ConfigureAwait(false);
        // Mirror the server's wire handshake shape to reach the read stage with
        // an authenticated session, then send arbitrary read-path bytes.
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new { Bootstrap = args[2] })).ConfigureAwait(false);
        var handshakeBytes = await ReadFrameAsync(stream).ConfigureAwait(false);
        var handshakeResult = handshakeBytes is null ? null : JsonSerializer.Deserialize<HandshakeResult>(handshakeBytes);
        if (handshakeBytes is null || handshakeResult?.Failure is not null)
        {
            Console.WriteLine("FAIL HandshakeRejected");
            return 1;
        }
        await WriteFrameAsync(stream, Convert.FromHexString(args[3])).ConfigureAwait(false);
        var response = await ReadFrameAsync(stream).ConfigureAwait(false);
        if (response is null)
        {
            Console.WriteLine("FAIL ConnectionLost");
            return 1;
        }
        Console.WriteLine($"RAW {Convert.ToHexString(response)}");
        return 0;
    }
    return 2;
}
catch (Exception exception)
{
    Console.WriteLine($"FAIL {exception.GetType().Name}");
    return 1;
}

static async Task WriteFrameAsync(Stream stream, byte[] payload)
{
    using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var header = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
    await stream.WriteAsync(header, guard.Token).ConfigureAwait(false);
    await stream.WriteAsync(payload, guard.Token).ConfigureAwait(false);
    await stream.FlushAsync(guard.Token).ConfigureAwait(false);
}

static async Task<byte[]?> ReadFrameAsync(Stream stream)
{
    using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var header = new byte[4];
    if (!await ReadExactAsync(stream, header, guard.Token).ConfigureAwait(false)) return null;
    var length = BinaryPrimitives.ReadInt32BigEndian(header);
    if (length is < 0 or > BrokerProtocol.MaximumRequestBodyBytes)
    {
        Console.WriteLine("FAIL OversizedResponse");
        return null;
    }
    var payload = new byte[length];
    if (!await ReadExactAsync(stream, payload, guard.Token).ConfigureAwait(false)) return null;
    return payload;
}

static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
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

internal sealed record HandshakeResult(int? Failure);
