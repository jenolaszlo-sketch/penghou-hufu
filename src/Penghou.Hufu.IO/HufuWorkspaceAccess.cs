using Penghou.IO.Abstractions;

namespace Penghou.Hufu.IO;

/// <summary>
/// A per-operation front door that checks the exact request before opening a
/// provider session. The provider receives the same authorizer and journal so
/// it can check discovered candidates and the locked mutation boundary too.
/// </summary>
public sealed class HufuWorkspaceAccess : IWorkspaceReader, IWorkspaceConditionalWriter, IDisposable
{
    private readonly IWorkspaceProvider _provider;
    private readonly WorkspaceId _workspace;
    private readonly WorkspaceProviderCapabilities _capabilities;
    private readonly IResourceAuthorizer _authorizer;
    private readonly IResourceMutationJournal _journal;
    private readonly WorkspaceReaderOptions? _readerOptions;
    private readonly WorkspaceWriterOptions? _writerOptions;
    private readonly object _readerGate = new();
    private IWorkspaceReaderSession? _reader;
    private bool _disposed;

    public HufuWorkspaceAccess(IWorkspaceProvider provider, IResourceAuthorizer authorizer,
        IResourceMutationJournal journal, WorkspaceReaderOptions? readerOptions = null,
        WorkspaceWriterOptions? writerOptions = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _workspace = provider.Workspace;
        _capabilities = provider.Capabilities ?? throw new ArgumentException("Provider capabilities are required.", nameof(provider));
        if (!AuthorityValidation.ValidToken(_workspace.Value))
            throw new ArgumentException("A bounded provider workspace is required.", nameof(provider));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _readerOptions = readerOptions;
        _writerOptions = writerOptions;
    }

    public ValueTask<ResourceResult<FileReadResult>> ReadFileAsync(FileReadRequest request,
        CancellationToken cancellationToken = default) => request is null
        ? ValueTask.FromResult(ResourceResult<FileReadResult>.Failed(ResourceFailureKind.InvalidRequest))
        : GuardAndReadAsync(request, ResourceAction.ReadFile,
            new ResourceBinding.WorkspaceFile(_workspace, request.Path),
            ResourceRequestIdentity.Compute, static (reader, r, ct) => reader.ReadFileAsync(r, ct), cancellationToken);

    public ValueTask<ResourceResult<FileMetadata>> GetFileMetadataAsync(FileMetadataRequest request,
        CancellationToken cancellationToken = default) => request is null
        ? ValueTask.FromResult(ResourceResult<FileMetadata>.Failed(ResourceFailureKind.InvalidRequest))
        : GuardAndReadAsync(request, ResourceAction.ReadMetadata,
            new ResourceBinding.WorkspaceFile(_workspace, request.Path),
            ResourceRequestIdentity.Compute, static (reader, r, ct) => reader.GetFileMetadataAsync(r, ct), cancellationToken);

    public ValueTask<ResourceResult<DirectoryPage>> ListDirectoryAsync(DirectoryListRequest request,
        CancellationToken cancellationToken = default) => request is null
        ? ValueTask.FromResult(ResourceResult<DirectoryPage>.Failed(ResourceFailureKind.InvalidRequest))
        : GuardAndReadAsync(request, ResourceAction.ListDirectory,
            new ResourceBinding.WorkspaceDirectory(_workspace, request.Path),
            ResourceRequestIdentity.Compute, static (reader, r, ct) => reader.ListDirectoryAsync(r, ct), cancellationToken);

    public async ValueTask<ResourceResult<ResourceVersion>> WriteFileAsync(FileWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (IsDisposed) return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.ProviderFailure);
        if (request is null) return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.InvalidRequest);
        if (request.Workspace != _workspace) return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.InvalidRequest);
        if (request.Limits is null || request.Limits.MaxBytes is < 0 or > ResourceRequestIdentity.MaximumPayloadBytes ||
            request.Content.Length > request.Limits.MaxBytes || request.Content.Length > ResourceRequestIdentity.MaximumPayloadBytes)
            return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.InvalidRequest);
        // Freeze caller-owned memory before hashing, checking authority, or dispatching.
        FileWriteRequest frozen;
        try { frozen = request with { Content = request.Content.ToArray() }; }
        catch { return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.InvalidRequest); }
        if (_capabilities is not { SupportsConditionalWrites: true })
            return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.Unsupported);
        RequestIdentity identity;
        try { identity = ResourceRequestIdentity.Compute(frozen); }
        catch { return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.InvalidRequest); }
        var result = await AuthorizeAsync(frozen.Invocation, identity, ResourceAction.WriteFile,
            new ResourceBinding.WorkspaceFile(_workspace, frozen.Path), cancellationToken).ConfigureAwait(false);
        if (!result.Allowed) return ResourceResult<ResourceVersion>.Failed(result.Failure);
        try
        {
            IWorkspaceConditionalWriter writer;
            lock (_readerGate)
            {
                if (_disposed) return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.ProviderFailure);
                writer = _provider.OpenWriter(_authorizer, _journal, _writerOptions);
            }
            return await writer.WriteFileAsync(frozen, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return ResourceResult<ResourceVersion>.Failed(ResourceFailureKind.ProviderFailure); }
    }

    private async ValueTask<ResourceResult<TValue>> GuardAndReadAsync<TRequest, TValue>(TRequest request,
        ResourceAction action, ResourceBinding binding, Func<TRequest, RequestIdentity> identityFactory,
        Func<IWorkspaceReaderSession, TRequest, CancellationToken, ValueTask<ResourceResult<TValue>>> dispatch,
        CancellationToken cancellationToken) where TRequest : class
    {
        if (IsDisposed) return ResourceResult<TValue>.Failed(ResourceFailureKind.ProviderFailure);
        if (request is null) return ResourceResult<TValue>.Failed(ResourceFailureKind.InvalidRequest);
        var requestedWorkspace = request switch
        {
            FileReadRequest r => r.Workspace,
            FileMetadataRequest r => r.Workspace,
            DirectoryListRequest r => r.Workspace,
            _ => default
        };
        if (requestedWorkspace != _workspace) return ResourceResult<TValue>.Failed(ResourceFailureKind.InvalidRequest);
        if (_capabilities is not { SupportsReads: true })
            return ResourceResult<TValue>.Failed(ResourceFailureKind.Unsupported);
        RequestIdentity identity;
        try { identity = identityFactory(request); }
        catch { return ResourceResult<TValue>.Failed(ResourceFailureKind.InvalidRequest); }
        var invocation = request switch
        {
            FileReadRequest r => r.Invocation,
            FileMetadataRequest r => r.Invocation,
            DirectoryListRequest r => r.Invocation,
            _ => null
        };
        if (invocation is null) return ResourceResult<TValue>.Failed(ResourceFailureKind.InvalidRequest);
        var authorization = await AuthorizeAsync(invocation, identity, action, binding, cancellationToken).ConfigureAwait(false);
        if (!authorization.Allowed) return ResourceResult<TValue>.Failed(authorization.Failure);
        try
        {
            var session = GetReaderSession();
            return await dispatch(session, request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return ResourceResult<TValue>.Failed(ResourceFailureKind.ProviderFailure); }
    }

    private async ValueTask<AuthorizationResult> AuthorizeAsync(HostInvocation invocation,
        RequestIdentity identity, ResourceAction action, ResourceBinding binding,
        CancellationToken cancellationToken)
    {
        if (invocation is null || invocation.RequestIdentity != identity)
            return new(false, ResourceFailureKind.InvalidRequest);
        ResourceAuthorizationDecision? decision;
        try
        {
            decision = await _authorizer.AuthorizeAsync(new(invocation, identity, action, binding), cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(false, ResourceFailureKind.AuthorizationUnavailable); }
        cancellationToken.ThrowIfCancellationRequested();
        return decision?.Status switch
        {
            AuthorizationStatus.Permit => new(true, default),
            AuthorizationStatus.Deny => new(false, ResourceFailureKind.AuthorizationDenied),
            _ => new(false, ResourceFailureKind.AuthorizationUnavailable)
        };
    }

    private IWorkspaceReaderSession GetReaderSession()
    {
        lock (_readerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _reader ??= _provider.OpenReader(_authorizer, _readerOptions);
        }
    }

    private bool IsDisposed
    {
        get { lock (_readerGate) return _disposed; }
    }

    /// <summary>Ends this workspace session and releases its provider continuation state.</summary>
    public void Dispose()
    {
        IWorkspaceReaderSession? reader;
        lock (_readerGate)
        {
            if (_disposed) return;
            _disposed = true;
            reader = _reader;
            _reader = null;
        }
        reader?.Dispose();
    }

    private readonly record struct AuthorizationResult(bool Allowed, ResourceFailureKind Failure);
}
