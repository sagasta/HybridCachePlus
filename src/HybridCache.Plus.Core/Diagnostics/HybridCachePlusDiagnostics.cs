using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HybridCache.Plus.Diagnostics;

/// <summary>
/// Provides OpenTelemetry-compatible metrics and activity sources for HybridCache.Plus.
/// </summary>
public static class HybridCachePlusDiagnostics
{
    /// <summary>
    /// The name of the <see cref="Meter"/> used by HybridCache.Plus.
    /// Add this meter name to OpenTelemetry (<c>.AddMeter(HybridCachePlusDiagnostics.MeterName)</c>) to collect metrics.
    /// </summary>
    public const string MeterName = "HybridCache.Plus";

    /// <summary>
    /// The name of the <see cref="ActivitySource"/> used by HybridCache.Plus for distributed tracing.
    /// </summary>
    public const string ActivitySourceName = "HybridCache.Plus";

    private static readonly Meter s_meter = new(MeterName, "1.0.0");
    private static readonly ActivitySource s_activitySource = new(ActivitySourceName, "1.0.0");

    private static readonly Counter<long> s_hitsCounter = s_meter.CreateCounter<long>(
        name: "hybridcache_plus.hits",
        unit: "{hits}",
        description: "Number of cache hits in HybridCache (L1/L2).");

    private static readonly Counter<long> s_missesCounter = s_meter.CreateCounter<long>(
        name: "hybridcache_plus.misses",
        unit: "{misses}",
        description: "Number of cache misses resulting in factory execution.");

    private static readonly Counter<long> s_evictionsCounter = s_meter.CreateCounter<long>(
        name: "hybridcache_plus.evictions",
        unit: "{evictions}",
        description: "Number of cache keys or tags evicted.");

    private static readonly Counter<long> s_backplanePublishedCounter = s_meter.CreateCounter<long>(
        name: "hybridcache_plus.backplane.published",
        unit: "{messages}",
        description: "Number of eviction messages published to the backplane.");

    private static readonly Counter<long> s_backplaneReceivedCounter = s_meter.CreateCounter<long>(
        name: "hybridcache_plus.backplane.received",
        unit: "{messages}",
        description: "Number of eviction messages received from the backplane.");

    private static readonly Histogram<double> s_durationHistogram = s_meter.CreateHistogram<double>(
        name: "hybridcache_plus.duration",
        unit: "ms",
        description: "Duration of HybridCache operations in milliseconds.");

    /// <summary>
    /// Gets or sets whether diagnostics and metrics are globally enabled.
    /// Default is true.
    /// </summary>
    public static bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets whether any active listener is observing hit/miss metrics.
    /// Allows zero-allocation fast-path checks.
    /// </summary>
    public static bool IsMetricsEnabled => IsEnabled && (s_hitsCounter.Enabled || s_missesCounter.Enabled);

    /// <summary>
    /// Gets the shared ActivitySource for distributed tracing.
    /// </summary>
    public static ActivitySource ActivitySource => s_activitySource;

    /// <summary>
    /// Records a cache hit.
    /// </summary>
    public static void RecordHit(string policy, string? tenant, string template)
    {
        if (!IsEnabled || !s_hitsCounter.Enabled) return;

        s_hitsCounter.Add(1,
            new KeyValuePair<string, object?>("cache.policy", policy),
            new KeyValuePair<string, object?>("cache.tenant", tenant ?? "none"),
            new KeyValuePair<string, object?>("cache.template", template));
    }

    /// <summary>
    /// Records a cache miss.
    /// </summary>
    public static void RecordMiss(string policy, string? tenant, string template)
    {
        if (!IsEnabled || !s_missesCounter.Enabled) return;

        s_missesCounter.Add(1,
            new KeyValuePair<string, object?>("cache.policy", policy),
            new KeyValuePair<string, object?>("cache.tenant", tenant ?? "none"),
            new KeyValuePair<string, object?>("cache.template", template));
    }

    /// <summary>
    /// Records a cache eviction.
    /// </summary>
    public static void RecordEviction(string key, string reason, string? tenant = null)
    {
        if (!IsEnabled || !s_evictionsCounter.Enabled) return;

        s_evictionsCounter.Add(1,
            new KeyValuePair<string, object?>("eviction.reason", reason),
            new KeyValuePair<string, object?>("cache.tenant", tenant ?? "none"),
            new KeyValuePair<string, object?>("cache.key", key));
    }

    /// <summary>
    /// Records an eviction message broadcasted via the backplane.
    /// </summary>
    public static void RecordBackplanePublished(string key, string? tenant = null)
    {
        if (!IsEnabled || !s_backplanePublishedCounter.Enabled) return;

        s_backplanePublishedCounter.Add(1,
            new KeyValuePair<string, object?>("cache.tenant", tenant ?? "none"),
            new KeyValuePair<string, object?>("cache.key", key));
    }

    /// <summary>
    /// Records an eviction message received from the backplane.
    /// </summary>
    public static void RecordBackplaneReceived(string key, string? tenant = null)
    {
        if (!IsEnabled || !s_backplaneReceivedCounter.Enabled) return;

        s_backplaneReceivedCounter.Add(1,
            new KeyValuePair<string, object?>("cache.tenant", tenant ?? "none"),
            new KeyValuePair<string, object?>("cache.key", key));
    }

    /// <summary>
    /// Records operation duration in milliseconds.
    /// </summary>
    public static void RecordDuration(double durationMs, string operation, string policy, string? tenant = null)
    {
        if (!IsEnabled || !s_durationHistogram.Enabled) return;

        s_durationHistogram.Record(durationMs,
            new KeyValuePair<string, object?>("cache.operation", operation),
            new KeyValuePair<string, object?>("cache.policy", policy),
            new KeyValuePair<string, object?>("cache.tenant", tenant ?? "none"));
    }
}
