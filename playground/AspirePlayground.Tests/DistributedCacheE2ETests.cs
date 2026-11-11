using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using StackExchange.Redis;
using Xunit;

namespace AspirePlayground.Tests;

public class AspireFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = null!;
    public HttpClient Node1 { get; private set; } = null!;
    public HttpClient Node2 { get; private set; } = null!;
    public HttpClient Standalone { get; private set; } = null!;
    public IConnectionMultiplexer RedisAlpha { get; private set; } = null!;
    public IConnectionMultiplexer RedisBeta { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AspirePlayground_AppHost>();
        App = await appHost.BuildAsync();
        await App.StartAsync();

        Node1 = App.CreateHttpClient("api-node1");
        Node2 = App.CreateHttpClient("api-node2");
        Standalone = App.CreateHttpClient("api-standalone");

        await WaitForHealthyAsync(Node1, "api-node1");
        await WaitForHealthyAsync(Node2, "api-node2");
        await WaitForHealthyAsync(Standalone, "api-standalone");

        var alphaConnStr = await App.GetConnectionStringAsync("redis-tenant-alpha");
        var betaConnStr = await App.GetConnectionStringAsync("redis-tenant-beta");

        if (!string.IsNullOrEmpty(alphaConnStr))
        {
            var config = ConfigurationOptions.Parse(alphaConnStr);
            config.AbortOnConnectFail = false;
            RedisAlpha = await ConnectionMultiplexer.ConnectAsync(config);
        }

        if (!string.IsNullOrEmpty(betaConnStr))
        {
            var config = ConfigurationOptions.Parse(betaConnStr);
            config.AbortOnConnectFail = false;
            RedisBeta = await ConnectionMultiplexer.ConnectAsync(config);
        }
    }

    private static async Task WaitForHealthyAsync(HttpClient client, string name)
    {
        for (var i = 0; i < 40; i++)
        {
            try
            {
                var res = await client.GetAsync("/api/system/status");
                if (res.IsSuccessStatusCode) return;
            }
            catch
            {
            }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Service '{name}' failed to become healthy within 40 seconds.");
    }

    public async ValueTask DisposeAsync()
    {
        Node1?.Dispose();
        Node2?.Dispose();
        Standalone?.Dispose();
        RedisAlpha?.Dispose();
        RedisBeta?.Dispose();
        if (App != null)
        {
            await App.DisposeAsync();
        }
    }
}

public class DistributedCacheE2ETests : IClassFixture<AspireFixture>
{
    private readonly AspireFixture _f;

    public DistributedCacheE2ETests(AspireFixture fixture)
    {
        _f = fixture;
    }

    [Fact]
    public async Task Test01_ColdStart_And_L1_LocalHit()
    {
        var productId = 101L;

        // 1. First GET request should be a MISS from SQLite DB
        var res1 = await _f.Node1.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
        Assert.NotNull(res1);
        Assert.Contains("MISS", res1["source"]?.ToString());
        var queriesAfterMiss = res1["totalDbQueriesAcrossAllCalls"]?.GetValue<int>() ?? 0;

        // 2. Second GET request should be a HIT from in-memory L1
        var res2 = await _f.Node1.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
        Assert.NotNull(res2);
        Assert.Contains("HIT", res2["source"]?.ToString());
        var queriesAfterHit = res2["totalDbQueriesAcrossAllCalls"]?.GetValue<int>() ?? 0;

        // Total DB queries must remain unchanged
        Assert.Equal(queriesAfterMiss, queriesAfterHit);
    }

    [Fact]
    public async Task Test02_L2_Distributed_Sharing_Between_Nodes()
    {
        var productId = 202L;

        // 1. Node 1 fetches product (populates L1 in Node 1 and L2 in Redis Alpha)
        var resNode1 = await _f.Node1.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
        Assert.NotNull(resNode1);
        Assert.Contains("MISS", resNode1["source"]?.ToString());

        // 2. Query Node 2 status before requesting
        var beforeNode2 = (await _f.Node2.GetFromJsonAsync<JsonObject>("/api/system/status"))?["totalDbQueries"]?.GetValue<int>() ?? 0;

        // 3. Node 2 (different process/pod) fetches product
        // It does not have it in its local L1, but gets it from Redis L2
        var resNode2 = await _f.Node2.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
        Assert.NotNull(resNode2);
        Assert.Contains("HIT", resNode2["source"]?.ToString());

        var afterNode2 = (await _f.Node2.GetFromJsonAsync<JsonObject>("/api/system/status"))?["totalDbQueries"]?.GetValue<int>() ?? 0;

        // Zero additional DB queries executed on Node 2 (served 100% from shared Redis L2)
        Assert.Equal(beforeNode2, afterNode2);
    }

    [Fact]
    public async Task Test03_Cache_Stampede_Prevention_100_Concurrent_Requests()
    {
        // Reset DB query counter
        await _f.Node1.PostAsync("/api/system/reset-db-counter", null);

        var productId = 99999L; // fresh uncached key
        var concurrentTasks = new List<Task<HttpResponseMessage>>();

        // Fire 100 concurrent requests simultaneously
        for (int i = 0; i < 100; i++)
        {
            concurrentTasks.Add(_f.Node1.GetAsync($"/api/products/tenant_alpha/{productId}"));
        }

        var responses = await Task.WhenAll(concurrentTasks);

        // All 100 requests must succeed with 200 OK
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        // Verify that database was queried EXACTLY ONCE
        var status = await _f.Node1.GetFromJsonAsync<JsonObject>("/api/system/status");
        var totalQueries = status?["totalDbQueries"]?.GetValue<int>();

        Assert.Equal(1, totalQueries);
    }

    [Fact]
    public async Task Test04_Cross_Pod_Realtime_Backplane_Eviction()
    {
        var productId = 401L;

        // 1. Prime L1 cache in both Node 1 and Node 2
        await _f.Node1.GetAsync($"/api/products/tenant_alpha/{productId}");
        await _f.Node2.GetAsync($"/api/products/tenant_alpha/{productId}");

        // 2. Mutate product on Node 1 via generated decorator
        var updatePayload = new { Name = "Realtime Updated Widget", Price = 149.99m };
        var putRes = await _f.Node1.PutAsJsonAsync($"/api/products/tenant_alpha/{productId}", updatePayload);
        Assert.True(putRes.IsSuccessStatusCode);

        // Small grace period for Redis Pub/Sub backplane propagation to Node 2
        await Task.Delay(500);

        // 3. Immediately read from Node 2
        var resNode2 = await _f.Node2.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
        Assert.NotNull(resNode2);

        var product = resNode2["product"];
        Assert.NotNull(product);
        Assert.Equal("Realtime Updated Widget", product["name"]?.ToString());
        Assert.Equal(149.99m, product["price"]?.GetValue<decimal>());
    }

    [Fact]
    public async Task Test05_MultiTenant_Physical_L2_Isolation()
    {
        var productId = 501L;

        // 1. Write product in Tenant Alpha
        await _f.Node1.GetAsync($"/api/products/tenant_alpha/{productId}");

        // 2. Write product in Tenant Beta
        await _f.Node1.GetAsync($"/api/products/tenant_beta/{productId}");

        // 3. Assert Redis Alpha physical keys
        Assert.NotNull(_f.RedisAlpha);
        Assert.NotNull(_f.RedisBeta);

        var alphaServer = _f.RedisAlpha.GetServer(_f.RedisAlpha.GetEndPoints().First());
        var betaServer = _f.RedisBeta.GetServer(_f.RedisBeta.GetEndPoints().First());

        var alphaKeys = alphaServer.Keys().Select(k => k.ToString()).ToList();
        var betaKeys = betaServer.Keys().Select(k => k.ToString()).ToList();

        // Alpha Redis must contain alpha keys and zero beta keys
        Assert.Contains(alphaKeys, k => k.Contains("tenant_alpha"));
        Assert.DoesNotContain(alphaKeys, k => k.Contains("tenant_beta"));

        // Beta Redis must contain beta keys and zero alpha keys
        Assert.Contains(betaKeys, k => k.Contains("tenant_beta"));
        Assert.DoesNotContain(betaKeys, k => k.Contains("tenant_alpha"));
    }

    [Fact]
    public async Task Test06_CQRS_Complex_Object_Query_And_Command_Eviction()
    {
        var queryPayload = new { TenantId = "tenant_alpha", ProductId = 601L };

        // 1. Initial CQRS query -> MISS
        var qRes1 = await _f.Node1.PostAsJsonAsync("/api/products/query", queryPayload);
        var body1 = await qRes1.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(body1);
        Assert.Contains("MISS", body1["source"]?.ToString());

        // 2. Second CQRS query -> HIT
        var qRes2 = await _f.Node1.PostAsJsonAsync("/api/products/query", queryPayload);
        var body2 = await qRes2.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(body2);
        Assert.Contains("HIT", body2["source"]?.ToString());

        // 3. CQRS Command mutation
        var cmdPayload = new
        {
            TenantId = "tenant_alpha",
            ProductId = 601L,
            Name = "CQRS Item 601",
            Price = 77.77m
        };
        var cmdRes = await _f.Node1.PostAsJsonAsync("/api/products/command", cmdPayload);
        Assert.True(cmdRes.IsSuccessStatusCode);

        // 4. CQRS query after command mutation -> returns new mutated data
        var qRes3 = await _f.Node1.PostAsJsonAsync("/api/products/query", queryPayload);
        var body3 = await qRes3.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(body3);
        var product = body3["product"];
        Assert.NotNull(product);
        Assert.Equal("CQRS Item 601", product["name"]?.ToString());
        Assert.Equal(77.77m, product["price"]?.GetValue<decimal>());
    }

    [Fact]
    public async Task Test07_Standalone_SingleTenant_Topology()
    {
        var productId = 701L;

        // 1. Cold query on api-standalone (no backplane, no Redis)
        var res1 = await _f.Standalone.GetFromJsonAsync<JsonObject>($"/api/products/standalone_tenant/{productId}");
        Assert.NotNull(res1);
        Assert.Contains("MISS", res1["source"]?.ToString());

        // 2. Warm query on api-standalone -> in-memory L1 hit
        var res2 = await _f.Standalone.GetFromJsonAsync<JsonObject>($"/api/products/standalone_tenant/{productId}");
        Assert.NotNull(res2);
        Assert.Contains("HIT", res2["source"]?.ToString());
    }

    [Fact]
    public async Task Test08_Cross_Pod_Tag_Eviction_Via_Backplane()
    {
        var tenant = "tenant_alpha";
        var productA = 801L;
        var productB = 802L;

        // 1. Prime cache for both products on Node 1 and Node 2
        await _f.Node1.GetAsync($"/api/products/{tenant}/{productA}");
        await _f.Node1.GetAsync($"/api/products/{tenant}/{productB}");
        await _f.Node2.GetAsync($"/api/products/{tenant}/{productA}");
        await _f.Node2.GetAsync($"/api/products/{tenant}/{productB}");

        // Assert both are cached in L1 on Node 2 (hits)
        var res1 = await _f.Node2.GetFromJsonAsync<JsonObject>($"/api/products/{tenant}/{productA}");
        Assert.NotNull(res1);
        Assert.Contains("HIT", res1["source"]?.ToString());

        var res2 = await _f.Node2.GetFromJsonAsync<JsonObject>($"/api/products/{tenant}/{productB}");
        Assert.NotNull(res2);
        Assert.Contains("HIT", res2["source"]?.ToString());

        // 2. Query status of Node 2 before eviction
        var beforeNode2 = (await _f.Node2.GetFromJsonAsync<JsonObject>("/api/system/status"))?["totalDbQueries"]?.GetValue<int>() ?? 0;

        // 3. Broadcast Tag Eviction from Node 1
        var evictRes = await _f.Node1.PostAsync($"/api/products/evict-tenant-tag/{tenant}", null);
        Assert.True(evictRes.IsSuccessStatusCode);

        // Small grace period for Redis Pub/Sub propagation
        await Task.Delay(500);

        // 4. Subsequent queries on Node 2 must now reload from DB because Tag was evicted
        var resAfterEvictA = await _f.Node2.GetFromJsonAsync<JsonObject>($"/api/products/{tenant}/{productA}");
        var resAfterEvictB = await _f.Node2.GetFromJsonAsync<JsonObject>($"/api/products/{tenant}/{productB}");

        var afterNode2 = (await _f.Node2.GetFromJsonAsync<JsonObject>("/api/system/status"))?["totalDbQueries"]?.GetValue<int>() ?? 0;

        // Verify that Node 2 reloaded both items from DB
        Assert.True(afterNode2 > beforeNode2, $"Expected Node 2 to reload items from DB because Tag was evicted. Before: {beforeNode2}, After: {afterNode2}");
    }

    // ---------------------------------------------------------------------
    // Fail-safe scenarios. Outage, latency and expiration are simulated per node.
    // ---------------------------------------------------------------------

    private static async Task SetOutageAsync(HttpClient node, bool enabled) =>
        (await node.PostAsync($"/api/system/db-outage?enabled={enabled.ToString().ToLowerInvariant()}", null)).EnsureSuccessStatusCode();

    private static async Task SetLatencyAsync(HttpClient node, int ms) =>
        (await node.PostAsync($"/api/system/db-latency?ms={ms}", null)).EnsureSuccessStatusCode();

    private static async Task ExpireAsync(HttpClient node, string tenant, long productId) =>
        (await node.PostAsync($"/api/system/expire/{tenant}/{productId}", null)).EnsureSuccessStatusCode();

    private static async Task<int> DbQueriesAsync(HttpClient node) =>
        (await node.GetFromJsonAsync<JsonObject>("/api/system/status"))?["totalDbQueries"]?.GetValue<int>() ?? 0;

    [Fact]
    public async Task Test09_FailSafe_DatabaseOutage_ServesLastKnownValue()
    {
        const long productId = 901L;
        var update = await _f.Node1.PutAsJsonAsync($"/api/products/tenant_alpha/{productId}", new { Name = "Fail-safe Lamp", Price = 19.99m });
        update.EnsureSuccessStatusCode();
        await _f.Node1.GetAsync($"/api/products/tenant_alpha/{productId}"); // loads entry + backup

        try
        {
            await SetOutageAsync(_f.Node1, true);
            await ExpireAsync(_f.Node1, "tenant_alpha", productId);

            var res = await _f.Node1.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
            Assert.NotNull(res);
            Assert.Contains("FAIL-SAFE", res["source"]?.ToString());
            Assert.Equal("Fail-safe Lamp", res["product"]?["name"]?.ToString());

            // Throttle: the next reads reuse the backup without hitting the failing database again.
            var before = await DbQueriesAsync(_f.Node1);
            for (var i = 0; i < 5; i++)
            {
                var again = await _f.Node1.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
                Assert.Equal("Fail-safe Lamp", again?["product"]?["name"]?.ToString());
            }
            Assert.Equal(before, await DbQueriesAsync(_f.Node1));
        }
        finally
        {
            await SetOutageAsync(_f.Node1, false);
        }
    }

    [Fact]
    public async Task Test10_FailSafe_OutageWithoutBackup_Returns503()
    {
        try
        {
            await SetOutageAsync(_f.Node1, true);

            var res = await _f.Node1.GetAsync("/api/products/tenant_alpha/902");
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, res.StatusCode);
        }
        finally
        {
            await SetOutageAsync(_f.Node1, false);
        }
    }

    [Fact]
    public async Task Test11_FailSafe_BackupInRedisL2_IsServedByAnotherNode()
    {
        const long productId = 903L;
        var update = await _f.Node1.PutAsJsonAsync($"/api/products/tenant_alpha/{productId}", new { Name = "Shared Backup Chair", Price = 89m });
        update.EnsureSuccessStatusCode();

        // Only Node 1 loads it: the backup reaches Node 2 exclusively through the tenant's Redis L2.
        await _f.Node1.GetAsync($"/api/products/tenant_alpha/{productId}");

        var backupKeys = _f.RedisAlpha.GetServer(_f.RedisAlpha.GetEndPoints().First())
            .Keys(pattern: $"*tenants:tenant_alpha:products:{productId}|hcp:fs*")
            .ToList();
        Assert.NotEmpty(backupKeys);

        try
        {
            await SetOutageAsync(_f.Node2, true);
            await ExpireAsync(_f.Node2, "tenant_alpha", productId);

            var res = await _f.Node2.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
            Assert.NotNull(res);
            Assert.Contains("FAIL-SAFE", res["source"]?.ToString());
            Assert.Equal("Shared Backup Chair", res["product"]?["name"]?.ToString());
        }
        finally
        {
            await SetOutageAsync(_f.Node2, false);
        }
    }

    [Fact]
    public async Task Test12_FailSafe_MutationOnOneNode_RemovesBackupEverywhere()
    {
        const long productId = 904L;

        // Both nodes hold the entry and its backup (L1 on each node, L2 shared).
        await _f.Node1.GetAsync($"/api/products/tenant_alpha/{productId}");
        await _f.Node2.GetAsync($"/api/products/tenant_alpha/{productId}");

        // Node 1 mutates: the old value is now known to be wrong.
        var update = await _f.Node1.PutAsJsonAsync($"/api/products/tenant_alpha/{productId}", new { Name = "Changed Desk", Price = 250m });
        update.EnsureSuccessStatusCode();
        await Task.Delay(500); // backplane propagation

        try
        {
            await SetOutageAsync(_f.Node2, true);

            // Node 2 must not resurrect the pre-update value from its L1 backup or from L2.
            var res = await _f.Node2.GetAsync($"/api/products/tenant_alpha/{productId}");
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, res.StatusCode);
        }
        finally
        {
            await SetOutageAsync(_f.Node2, false);
        }
    }

    [Fact]
    public async Task Test13_FailSafe_SlowDatabase_ServesBackupAndRefreshesInBackground()
    {
        const long productId = 905L;
        var update = await _f.Node1.PutAsJsonAsync($"/api/products/tenant_alpha/{productId}", new { Name = "Slow Sofa v1", Price = 500m });
        update.EnsureSuccessStatusCode();
        await _f.Node1.GetAsync($"/api/products/tenant_alpha/{productId}");

        try
        {
            await SetLatencyAsync(_f.Node1, 3000);
            await ExpireAsync(_f.Node1, "tenant_alpha", productId);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await _f.Node1.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
            sw.Stop();

            Assert.NotNull(res);
            Assert.Contains("FAIL-SAFE", res["source"]?.ToString());
            Assert.Equal("Slow Sofa v1", res["product"]?["name"]?.ToString());
            Assert.True(sw.ElapsedMilliseconds < 2500, $"Expected the soft timeout (1 s) to answer early, took {sw.ElapsedMilliseconds} ms");

            // The background load (~3 s) stores a fresh entry: once it is done, reads are fast hits
            // even though the database is still slow.
            await Task.Delay(3500);
            var before = await DbQueriesAsync(_f.Node1);
            sw.Restart();
            var later = await _f.Node1.GetFromJsonAsync<JsonObject>($"/api/products/tenant_alpha/{productId}");
            sw.Stop();

            Assert.Contains("HIT", later?["source"]?.ToString());
            Assert.Equal(before, await DbQueriesAsync(_f.Node1));
            Assert.True(sw.ElapsedMilliseconds < 500, $"Expected a cached hit after the background refresh, took {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            await SetLatencyAsync(_f.Node1, 0);
        }
    }
}
