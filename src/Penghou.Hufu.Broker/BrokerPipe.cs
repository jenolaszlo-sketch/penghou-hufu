using System.IO.Pipes;
using Penghou.IO.Abstractions;

namespace Penghou.Hufu.Broker;

/// <summary>
/// Minimal protocol-v1 named-pipe server. Pipe ACLs protect endpoint access; the
/// logical worker identity still comes from the bootstrap/session handshake, so
/// the transport introduces no second identity model.
/// </summary>
public sealed class BrokerPipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly BrokerSessionRegistry _sessions;
    private readonly BrokerReadHandler _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _started;
    private bool _disposed;

    public BrokerPipeServer(string pipeName, BrokerSessionRegistry sessions, BrokerReadHandler handler)
    {
        if (!AuthorityValidation.ValidToken(pipeName)) throw new ArgumentException("A bounded pipe name is required.", nameof(pipeName));
        _pipeName = pipeName;
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The AE-2.2 broker transport is qualified on Windows named pipes.");
        if (_started) throw new InvalidOperationException("The server has already started.");
        _started = true;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { /* a failed connection is not fatal to the endpoint */ }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var connectionIdentity = Guid.NewGuid().ToString("N");
        var payload = await BrokerFrame.ReadAsync(server, cancellationToken).ConfigureAwait(false);
        if (payload is null) return;
        var handshake = BrokerFrame.Decode<WireHandshake>(payload);
        if (handshake is null || string.IsNullOrWhiteSpace(handshake.Bootstrap))
        {
            await WriteAsync(server, new WireHandshakeResult((int)BrokerFailureKind.Unauthorized), cancellationToken).ConfigureAwait(false);
            return;
        }
        var session = await _sessions.AuthenticateAsync(handshake.Bootstrap, connectionIdentity, BrokerProtocol.Version, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            await WriteAsync(server, new WireHandshakeResult((int)BrokerFailureKind.Unauthorized), cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteAsync(server, new WireHandshakeResult(null), cancellationToken).ConfigureAwait(false);

        var requestPayload = await BrokerFrame.ReadAsync(server, cancellationToken).ConfigureAwait(false);
        if (requestPayload is null) return;
        var wire = BrokerFrame.Decode<WireReadRequest>(requestPayload);
        BrokerReadFileRequest request;
        try
        {
            if (wire is null) throw new ArgumentException("invalid");
            request = new BrokerReadFileRequest(wire.ProtocolVersion, wire.OperationId, wire.AuthorityReference,
                wire.WorkspaceId, wire.RelativePath, wire.MaxBytes, wire.Deadline);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            await WriteAsync(server, new WireResponse((int)BrokerFailureKind.InvalidRequest, null, null, null, null), cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        var response = await _handler.HandleAsync(request, session, cancellationToken).ConfigureAwait(false);
        await WriteAsync(server, ToWire(response), cancellationToken).ConfigureAwait(false);
    }

    private static WireResponse ToWire(BrokerReadResponse response) => new(
        response.Failure is null ? null : (int)response.Failure,
        response.Succeeded ? Convert.ToBase64String(response.Content.Span) : null,
        response.Version?.Value,
        response.StartEvidenceId,
        response.ReleaseEvidenceId);

    private static ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken) =>
        BrokerFrame.WriteAsync(stream, BrokerFrame.Encode(value), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
        }
        _cts.Dispose();
    }
}

/// <summary>Minimal protocol-v1 named-pipe client. A null connect result means the handshake was rejected.</summary>
public sealed class BrokerPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;

    private BrokerPipeClient(NamedPipeClientStream stream) => _stream = stream;

    public static async ValueTask<BrokerPipeClient?> ConnectAsync(string pipeName, string bootstrap,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The AE-2.2 broker transport is qualified on Windows named pipes.");
        if (!AuthorityValidation.ValidToken(pipeName) || !AuthorityValidation.ValidToken(bootstrap))
            throw new ArgumentException("A bounded pipe name and bootstrap secret are required.");
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await BrokerFrame.WriteAsync(stream, BrokerFrame.Encode(new WireHandshake(bootstrap)), cancellationToken).ConfigureAwait(false);
            var payload = await BrokerFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            var result = payload is null ? null : BrokerFrame.Decode<WireHandshakeResult>(payload);
            if (result?.Failure is not null) return null;
            return new BrokerPipeClient(stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<BrokerReadResponse> ReadAsync(BrokerReadFileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var wire = new WireReadRequest(request.ProtocolVersion, request.OperationId, request.AuthorityReference,
                request.WorkspaceId, request.RelativePath, request.MaxBytes, request.Deadline);
            await BrokerFrame.WriteAsync(_stream, BrokerFrame.Encode(wire), cancellationToken).ConfigureAwait(false);
            var payload = await BrokerFrame.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
            var response = payload is null ? null : BrokerFrame.Decode<WireResponse>(payload);
            if (response is null) return new(BrokerFailureKind.Unavailable);
            var failure = response.Failure is int value ? (BrokerFailureKind)value : (BrokerFailureKind?)null;
            var content = response.Content is null ? default : Convert.FromBase64String(response.Content);
            ResourceVersion? version = response.Version is null ? null : new ResourceVersion(response.Version);
            return new(failure, content, version, response.Start, response.Release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(BrokerFailureKind.Unavailable); }
    }

    public async ValueTask DisposeAsync() => await _stream.DisposeAsync().ConfigureAwait(false);
}
