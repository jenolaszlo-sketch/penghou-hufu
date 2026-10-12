namespace Penghou.Hufu.Broker;

/// <summary>
/// The narrow execution/authority binding that keys the broker-local start
/// arbiter. Derived from the resolved reference; never caller-supplied.
/// </summary>
public sealed record BrokerAuthorityBinding(
    string TenantId, string SubjectId, string DelegationId, string Generation, string FenceId)
{
    public static BrokerAuthorityBinding From(BrokerAuthorityReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new(reference.TenantId, reference.SubjectId, reference.DelegationId, reference.Generation, reference.FenceId);
    }

    internal string Key => string.Join('\u001f', TenantId, SubjectId, DelegationId, Generation, FenceId);
}

/// <summary>
/// Concrete, broker-local start arbiter. It orders the authoritative start
/// decision and start-evidence commit against participating lifecycle/revocation
/// mutations for the same binding. It never spans the provider read. This is not
/// a general public ordering abstraction.
/// </summary>
public sealed class BrokerStartArbiter : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Run the ordered start boundary (AE-1 authorization + start evidence) alone for the binding.</summary>
    public async ValueTask RunStartAsync(BrokerAuthorityBinding binding,
        Func<CancellationToken, ValueTask> start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(start);
        using var lease = await AcquireAsync(binding.Key, cancellationToken).ConfigureAwait(false);
        await start(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run the ordered start boundary and return a result for the binding.</summary>
    public async ValueTask<TResult> RunStartAsync<TResult>(BrokerAuthorityBinding binding,
        Func<CancellationToken, ValueTask<TResult>> start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(start);
        using var lease = await AcquireAsync(binding.Key, cancellationToken).ConfigureAwait(false);
        return await start(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run a participating lifecycle/revocation transition alone for the binding.</summary>
    public async ValueTask RunMutationAsync(BrokerAuthorityBinding binding,
        Func<CancellationToken, ValueTask> mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(mutation);
        using var lease = await AcquireAsync(binding.Key, cancellationToken).ConfigureAwait(false);
        await mutation(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Lease> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }
            entry.RefCount++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseRef(key, entry);
            throw;
        }
        return new Lease(this, key, entry);
    }

    private void Release(string key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseRef(key, entry);
    }

    private void ReleaseRef(string key, Entry entry)
    {
        lock (_gate)
        {
            entry.RefCount--;
            if (entry.RefCount <= 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var entry in _entries.Values) entry.Semaphore.Dispose();
            _entries.Clear();
        }
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int RefCount;
    }

    private readonly struct Lease(BrokerStartArbiter owner, string key, Entry entry) : IDisposable
    {
        public void Dispose() => owner.Release(key, entry);
    }
}
