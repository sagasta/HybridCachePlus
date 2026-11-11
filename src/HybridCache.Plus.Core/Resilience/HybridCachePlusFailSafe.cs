using HybridCache.Plus.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;

namespace HybridCache.Plus.Resilience;

/// <summary>
/// Fail-safe execution on top of <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/>: each successful load also
/// stores a longer-lived backup of the value, which is served when the factory throws or (optionally) is too slow.
/// </summary>
/// <remarks>
/// The backup lives under <see cref="GetBackupKey"/> with the same tags as the entry, so tag invalidations remove it too.
/// Key invalidations must remove both keys; the generated decorators and Evict methods do that automatically.
/// </remarks>
public static class HybridCachePlusFailSafe
{
    /// <summary>
    /// Suffix appended to a cache key to name its fail-safe backup. A suffix keeps tenant prefixes ("tenants:{id}:...") intact.
    /// </summary>
    public const string BackupKeySuffix = "|hcp:fs";

    private static readonly HybridCacheEntryOptions s_backupLookupOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite
    };

    /// <summary>
    /// Returns the key under which the fail-safe backup of <paramref name="key"/> is stored.
    /// </summary>
    public static string GetBackupKey(string key) => string.Concat(key, BackupKeySuffix);

    /// <summary>
    /// Gets or creates an entry with fail-safe protection.
    /// </summary>
    public static ValueTask<T> GetOrCreateAsync<T>(
        Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options,
        IEnumerable<string>? tags,
        FailSafePolicy failSafe,
        string policyName,
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return GetOrCreateAsync(cache, key, factory, static (f, ct) => f(ct), options, tags, failSafe, policyName, tenantId, cancellationToken);
    }

    /// <summary>
    /// Gets or creates an entry with fail-safe protection, passing <paramref name="state"/> to the factory.
    /// </summary>
    public static async ValueTask<T> GetOrCreateAsync<TState, T>(
        Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
        string key,
        TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options,
        IEnumerable<string>? tags,
        FailSafePolicy failSafe,
        string policyName,
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(failSafe);

        if (!failSafe.IsEnabled)
        {
            return await cache.GetOrCreateAsync(key, state, factory, options, tags, cancellationToken).ConfigureAwait(false);
        }

        // Tags are used by the entry, its backup and the throttled copy: enumerate them only once.
        var tagList = tags is null or IReadOnlyCollection<string> ? (IReadOnlyCollection<string>?)tags : tags.ToArray();
        var backupKey = GetBackupKey(key);
        var load = new LoadContext<TState, T>(cache, backupKey, state, factory, failSafe.GetBackupOptions(options), tagList);

        var pending = cache.GetOrCreateAsync(key, load, static (c, ct) => c.LoadAsync(ct), options, tagList, cancellationToken);
        Task<T>? running = null;

        try
        {
            if (failSafe.FactorySoftTimeout is { } softTimeout && !pending.IsCompleted)
            {
                running = pending.AsTask();
                using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(softTimeout, delayCancellation.Token);

                if (await Task.WhenAny(running, delay).ConfigureAwait(false) != running)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var stale = await TryGetBackupAsync<T>(cache, backupKey, cancellationToken).ConfigureAwait(false);
                    if (stale.Found)
                    {
                        // The load keeps going and refreshes the entry; its outcome must still be observed.
                        Observe(running);
                        Activated("FactoryTimeout", policyName, tenantId);
                        return stale.Value;
                    }
                }

                delayCancellation.Cancel();
                return await running.ConfigureAwait(false);
            }

            return await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            var stale = await TryGetBackupAsync<T>(cache, backupKey, cancellationToken).ConfigureAwait(false);
            if (!stale.Found)
            {
                throw;
            }

            Activated("FactoryError", policyName, tenantId);

            // Reuse the backup locally for a while, so every request does not retry a failing backend.
            if (failSafe.GetThrottleOptions(options) is { } throttle)
            {
                try
                {
                    await cache.SetAsync(key, stale.Value, throttle, tagList, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Throttling is best effort: the value is served either way.
                }
            }

            return stale.Value;
        }
        catch (OperationCanceledException) when (running is not null)
        {
            Observe(running);
            throw;
        }
    }

    private static async ValueTask<(bool Found, T Value)> TryGetBackupAsync<T>(
        Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
        string backupKey,
        CancellationToken cancellationToken)
    {
        try
        {
            // A factory that fails tells a missing backup apart from a stored default value, also for callers
            // that join a concurrent lookup of the same key. Nothing is written: both write paths are disabled.
            var value = await cache.GetOrCreateAsync<object?, T>(
                backupKey,
                null,
                static (_, _) => ValueTask.FromException<T>(new BackupMissingException()),
                s_backupLookupOptions,
                null,
                cancellationToken).ConfigureAwait(false);

            return (true, value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Missing or unreadable backup: the caller gets the original failure.
            return (false, default!);
        }
    }

    private static void Activated(string reason, string policyName, string? tenantId)
    {
        HybridCachePlusDiagnostics.RecordFailSafeActivation(reason, policyName, tenantId);
        System.Diagnostics.Activity.Current?.SetTag("cache.failsafe", reason);
    }

    private static void Observe(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class LoadContext<TState, T>(
        Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
        string backupKey,
        TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions backupOptions,
        IReadOnlyCollection<string>? tags)
    {
        public async ValueTask<T> LoadAsync(CancellationToken cancellationToken)
        {
            var value = await factory(state, cancellationToken).ConfigureAwait(false);

            try
            {
                await cache.SetAsync(backupKey, value, backupOptions, tags, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A missing backup only weakens fail-safe; it must never fail a successful load.
            }

            return value;
        }
    }

    private sealed class BackupMissingException : Exception;
}
