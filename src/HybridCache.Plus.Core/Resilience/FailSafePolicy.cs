using Microsoft.Extensions.Caching.Hybrid;

namespace HybridCache.Plus.Resilience;

/// <summary>
/// Fail-safe settings of a cache entry: how long a backup of the last good value is kept, how long the factory may run
/// before that backup is served, and how long a backup served after a failure is reused before retrying the factory.
/// </summary>
public sealed class FailSafePolicy
{
    private const HybridCacheEntryFlags StorageFlags =
        HybridCacheEntryFlags.DisableLocalCache | HybridCacheEntryFlags.DisableDistributedCache;

    private readonly HybridCacheEntryOptions _backupOptions;
    private readonly HybridCacheEntryOptions _throttleOptions;

    /// <summary>
    /// Fail-safe turned off.
    /// </summary>
    public static FailSafePolicy Disabled { get; } = new(TimeSpan.Zero);

    /// <summary>
    /// Throttle used when none is specified.
    /// </summary>
    public static TimeSpan DefaultThrottleDuration { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a fail-safe policy.
    /// </summary>
    /// <param name="maxAge">How long the backup of the last good value is kept. Zero or negative disables fail-safe.</param>
    /// <param name="factorySoftTimeout">Optional time after which a slow factory is answered with the backup.</param>
    /// <param name="throttleDuration">How long a backup served after a factory failure is reused locally (default 30 seconds).</param>
    public FailSafePolicy(TimeSpan maxAge, TimeSpan? factorySoftTimeout = null, TimeSpan? throttleDuration = null)
    {
        MaxAge = maxAge > TimeSpan.Zero ? maxAge : TimeSpan.Zero;
        FactorySoftTimeout = factorySoftTimeout > TimeSpan.Zero ? factorySoftTimeout : null;
        ThrottleDuration = throttleDuration > TimeSpan.Zero ? throttleDuration.Value : DefaultThrottleDuration;

        _backupOptions = new HybridCacheEntryOptions { Expiration = MaxAge, LocalCacheExpiration = MaxAge };
        _throttleOptions = new HybridCacheEntryOptions
        {
            Expiration = ThrottleDuration,
            LocalCacheExpiration = ThrottleDuration,
            Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite
        };
    }

    /// <summary>
    /// Creates a policy from the integer settings of <see cref="CacheTemplateAttribute"/> (used by generated code).
    /// </summary>
    public static FailSafePolicy FromSettings(int maxAgeSeconds, int factorySoftTimeoutMs = 0, int throttleSeconds = 0) =>
        maxAgeSeconds <= 0
            ? Disabled
            : new FailSafePolicy(
                TimeSpan.FromSeconds(maxAgeSeconds),
                factorySoftTimeoutMs > 0 ? TimeSpan.FromMilliseconds(factorySoftTimeoutMs) : null,
                throttleSeconds > 0 ? TimeSpan.FromSeconds(throttleSeconds) : null);

    /// <summary>
    /// How long the backup of the last good value is kept after each successful load.
    /// </summary>
    public TimeSpan MaxAge { get; }

    /// <summary>
    /// Time after which a slow factory is answered with the backup value, or null to always wait for the factory.
    /// </summary>
    public TimeSpan? FactorySoftTimeout { get; }

    /// <summary>
    /// How long a backup served after a factory failure is reused from the local cache before the factory is retried.
    /// </summary>
    public TimeSpan ThrottleDuration { get; }

    /// <summary>
    /// Whether fail-safe is active.
    /// </summary>
    public bool IsEnabled => MaxAge > TimeSpan.Zero;

    /// <summary>
    /// Options for the backup entry; it is stored in the same cache layers as the primary entry.
    /// </summary>
    internal HybridCacheEntryOptions GetBackupOptions(HybridCacheEntryOptions? primary)
    {
        var storage = (primary?.Flags ?? HybridCacheEntryFlags.None) & StorageFlags;
        return storage == HybridCacheEntryFlags.None
            ? _backupOptions
            : new HybridCacheEntryOptions { Expiration = MaxAge, LocalCacheExpiration = MaxAge, Flags = storage };
    }

    /// <summary>
    /// Options to keep a backup served after a failure in the local cache only, or null when the entry has no local cache.
    /// The distributed copy is never touched: another instance may already have stored a fresher value there.
    /// </summary>
    internal HybridCacheEntryOptions? GetThrottleOptions(HybridCacheEntryOptions? primary)
    {
        var flags = primary?.Flags ?? HybridCacheEntryFlags.None;
        return (flags & HybridCacheEntryFlags.DisableLocalCacheWrite) != 0 ? null : _throttleOptions;
    }
}
