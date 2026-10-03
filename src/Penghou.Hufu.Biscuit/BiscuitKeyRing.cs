using System.Runtime.ExceptionServices;
using BiscuitSharp;
using Penghou.Hufu;

namespace Penghou.Hufu.Biscuit;

/// <summary>
/// Host-managed owner of explicitly supplied Biscuit signing keys. It creates
/// no keys and never exports private material. Retired keys remain available for
/// verification until this ring is disposed.
/// </summary>
public sealed class BiscuitKeyRing : IBiscuitKeyProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<KeyIdentity, KeyEntry> _keys = new();
    private readonly Dictionary<string, KeyEntry> _currentByRealm = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Takes ownership of <paramref name="key"/> only when this method returns
    /// successfully. Rejected keys remain owned by the caller.
    /// </summary>
    public void AddSigningKey(string realm, string keyId, BiscuitPrivateKey key, bool makeCurrent = true)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateIdentity(realm, keyId);
        if (key.Algorithm != BiscuitKeyAlgorithm.Ed25519 ||
            key.PublicKey.Algorithm != BiscuitKeyAlgorithm.Ed25519)
        {
            throw new ArgumentException("The Hufu Biscuit profile accepts only Ed25519 signing keys.", nameof(key));
        }

        var identity = new KeyIdentity(realm, keyId);
        var entry = new KeyEntry(identity, key, key.PublicKey);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_keys.ContainsKey(identity))
            {
                throw new ArgumentException("A key with this realm and key ID is already registered.", nameof(keyId));
            }

            // Both mutations happen while locked. Keep a prior selection if a
            // dictionary update fails so ownership remains with the caller.
            KeyEntry? previousCurrent = null;
            bool hadPreviousCurrent = makeCurrent && _currentByRealm.TryGetValue(realm, out previousCurrent);
            _keys.Add(identity, entry);
            try
            {
                if (makeCurrent)
                {
                    _currentByRealm[realm] = entry;
                }
            }
            catch
            {
                _keys.Remove(identity);
                if (makeCurrent)
                {
                    if (hadPreviousCurrent)
                    {
                        _currentByRealm[realm] = previousCurrent!;
                    }
                    else
                    {
                        _currentByRealm.Remove(realm);
                    }
                }

                throw;
            }
        }
    }

    /// <summary>Selects an existing non-retired key for future signing leases.</summary>
    public void SelectSigningKey(string realm, string keyId)
    {
        ValidateIdentity(realm, keyId);
        var identity = new KeyIdentity(realm, keyId);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_keys.TryGetValue(identity, out KeyEntry? entry))
            {
                throw new KeyNotFoundException("No signing key is registered for this realm and key ID.");
            }

            if (entry.Retired || entry.KeyDisposed)
            {
                throw new InvalidOperationException("A retired signing key cannot be selected.");
            }

            _currentByRealm[realm] = entry;
        }
    }

    /// <summary>
    /// Stops future signing with a key while retaining its public key for
    /// verification. Any active leases finish before the private key is disposed.
    /// </summary>
    public void RetireSigningKey(string realm, string keyId)
    {
        ValidateIdentity(realm, keyId);
        var identity = new KeyIdentity(realm, keyId);
        BiscuitPrivateKey? dispose = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_keys.TryGetValue(identity, out KeyEntry? entry))
            {
                throw new KeyNotFoundException("No signing key is registered for this realm and key ID.");
            }

            entry.Retired = true;
            if (_currentByRealm.TryGetValue(realm, out KeyEntry? current) && ReferenceEquals(current, entry))
            {
                _currentByRealm.Remove(realm);
            }

            dispose = TakeKeyForDisposalLocked(entry);
        }

        dispose?.Dispose();
    }

    public ValueTask<IBiscuitSigningKeyLease?> AcquireSigningKeyAsync(
        string realm,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (!_currentByRealm.TryGetValue(realm, out KeyEntry? entry) || entry.Retired || entry.KeyDisposed)
            {
                return ValueTask.FromResult<IBiscuitSigningKeyLease?>(null);
            }

            checked { entry.LeaseCount++; }
            return ValueTask.FromResult<IBiscuitSigningKeyLease?>(new SigningKeyLease(this, entry));
        }
    }

    public ValueTask<BiscuitPublicKey?> FindVerificationKeyAsync(
        string realm,
        string keyId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(keyId);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_disposed || !_keys.TryGetValue(new KeyIdentity(realm, keyId), out KeyEntry? entry))
            {
                return ValueTask.FromResult<BiscuitPublicKey?>(null);
            }

            return ValueTask.FromResult<BiscuitPublicKey?>(entry.PublicKey);
        }
    }

    /// <summary>
    /// Disables the ring and disposes all unleased private keys. Leased keys are
    /// disposed by their final release; public-key lookups return null afterward.
    /// </summary>
    public void Dispose()
    {
        List<BiscuitPrivateKey>? dispose = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _currentByRealm.Clear();
            foreach (KeyEntry entry in _keys.Values)
            {
                BiscuitPrivateKey? key = TakeKeyForDisposalLocked(entry);
                if (key is not null)
                {
                    (dispose ??= new List<BiscuitPrivateKey>()).Add(key);
                }
            }

            _keys.Clear();
        }

        DisposeAll(dispose);
    }

    public override string ToString() => "BiscuitKeyRing (private keys redacted)";

    private static void ValidateIdentity(string realm, string keyId)
    {
        if (!AuthorityValidation.ValidToken(realm))
        {
            throw new ArgumentException("A bounded, non-empty realm is required.", nameof(realm));
        }

        if (!AuthorityValidation.ValidToken(keyId))
        {
            throw new ArgumentException("A bounded, non-empty key ID is required.", nameof(keyId));
        }
    }

    private void ReleaseLease(KeyEntry entry)
    {
        BiscuitPrivateKey? dispose;
        lock (_gate)
        {
            if (entry.LeaseCount <= 0)
            {
                throw new InvalidOperationException("Signing-key lease count underflow.");
            }

            entry.LeaseCount--;
            dispose = TakeKeyForDisposalLocked(entry);
        }

        dispose?.Dispose();
    }

    // Must be called with _gate held. Key disposal runs outside the ring lock.
    private BiscuitPrivateKey? TakeKeyForDisposalLocked(KeyEntry entry)
    {
        if ((!_disposed && !entry.Retired) || entry.LeaseCount != 0 || entry.KeyDisposed)
        {
            return null;
        }

        entry.KeyDisposed = true;
        return entry.SigningKey;
    }

    private static void DisposeAll(List<BiscuitPrivateKey>? keys)
    {
        if (keys is null)
        {
            return;
        }

        ExceptionDispatchInfo? firstFailure = null;
        foreach (BiscuitPrivateKey key in keys)
        {
            try
            {
                key.Dispose();
            }
            catch (Exception exception)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        firstFailure?.Throw();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BiscuitKeyRing));
        }
    }

    private readonly record struct KeyIdentity(string Realm, string KeyId);

    private sealed class KeyEntry(KeyIdentity identity, BiscuitPrivateKey signingKey, BiscuitPublicKey publicKey)
    {
        internal KeyIdentity Identity { get; } = identity;
        internal BiscuitPrivateKey SigningKey { get; } = signingKey;
        internal BiscuitPublicKey PublicKey { get; } = publicKey;
        internal int LeaseCount { get; set; }
        internal bool Retired { get; set; }
        internal bool KeyDisposed { get; set; }
    }

    private sealed class SigningKeyLease : IBiscuitSigningKeyLease
    {
        private readonly object _gate = new();
        private readonly BiscuitKeyRing _owner;
        private readonly KeyEntry _entry;
        private int _activeBuilds;
        private bool _disposed;
        private bool _released;

        internal SigningKeyLease(BiscuitKeyRing owner, KeyEntry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public string Realm => _entry.Identity.Realm;
        public string KeyId => _entry.Identity.KeyId;
        public BiscuitPublicKey PublicKey => _entry.PublicKey;

        public BiscuitToken Build(BiscuitTokenBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(SigningKeyLease));
                }

                checked { _activeBuilds++; }
            }

            try
            {
                return builder.Build(_entry.SigningKey);
            }
            finally
            {
                CompleteBuild();
            }
        }

        public void Dispose()
        {
            bool release;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                release = TakeLeaseReleaseLocked();
            }

            if (release)
            {
                _owner.ReleaseLease(_entry);
            }
        }

        public override string ToString() => "BiscuitSigningKeyLease (private key redacted)";

        private void CompleteBuild()
        {
            bool release;
            lock (_gate)
            {
                _activeBuilds--;
                release = TakeLeaseReleaseLocked();
            }

            if (release)
            {
                _owner.ReleaseLease(_entry);
            }
        }

        // Must be called with _gate held.
        private bool TakeLeaseReleaseLocked()
        {
            if (!_disposed || _activeBuilds != 0 || _released)
            {
                return false;
            }

            _released = true;
            return true;
        }
    }
}
