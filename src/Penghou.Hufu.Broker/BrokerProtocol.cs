namespace Penghou.Hufu.Broker;

/// <summary>
/// Frozen protocol bounds for the first worker-to-host broker profile. This is
/// an implementation/qualification choice, not a Hufu authority invariant.
/// </summary>
public static class BrokerProtocol
{
    /// <summary>The single frozen protocol version.</summary>
    public const int Version = 1;

    /// <summary>Maximum accepted encoded request body.</summary>
    public const int MaximumRequestBodyBytes = 16_384;

    /// <summary>Maximum disclosed read result length.</summary>
    public const int MaximumReadResultBytes = 65_536;

    public const int MinimumMaxBytes = 1;
    public const int MaximumMaxBytes = 65_536;

    /// <summary>Maximum concurrent authenticated sessions per logical worker.</summary>
    public const int MaximumSessionsPerWorker = 4;

    /// <summary>Maximum concurrent reads admitted per session.</summary>
    public const int MaximumConcurrentReadsPerSession = 8;

    /// <summary>Hard maximum request deadline the host may grant.</summary>
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(30);

    /// <summary>Hard maximum pending bootstrap lifetime.</summary>
    public static readonly TimeSpan MaximumBootstrapLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Hard maximum session lifetime.</summary>
    public static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromHours(1);
}

/// <summary>
/// Coarse external failure classes. Internal policy reasons, paths, parent
/// identity, content, candidate metadata and store state are never exposed here.
/// </summary>
public enum BrokerFailureKind
{
    InvalidRequest,
    Unauthorized,
    Unavailable,
    DeadlineExceeded,
    CapacityExceeded,
    OperationFailed
}
