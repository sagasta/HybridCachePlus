var builder = DistributedApplication.CreateBuilder(args);

// 1. Backplane Redis: Used exclusively for Cross-Pod L1 Eviction Pub/Sub
var redisBackplane = builder.AddRedis("redis-backplane");

// 2. Shared Redis: Used for Single-Tenant L2 caching
var redisShared = builder.AddRedis("redis-shared");

// 3. Multi-Tenant Redis instances: Used for L2 caching isolated per tenant
var redisTenantAlpha = builder.AddRedis("redis-tenant-alpha");
var redisTenantBeta = builder.AddRedis("redis-tenant-beta");

// 4. API Standalone (Single-Tenant, in-memory L1, no backplane)
var apiStandalone = builder.AddProject<Projects.AspirePlayground_Api>("api-standalone")
    .WithHttpEndpoint()
    .WithEnvironment("NODE_ID", "Node-Standalone")
    .WithEnvironment("ENABLE_BACKPLANE", "false")
    .WithEnvironment("ENABLE_MULTITENANT", "false");

// 5. Enterprise Multi-Tenant + Backplane (Nodes 1 & 2 with tenant-alpha, tenant-beta, and backplane)
var apiNode1 = builder.AddProject<Projects.AspirePlayground_Api>("api-node1")
    .WithHttpEndpoint()
    .WithEnvironment("NODE_ID", "Multi-Node-1")
    .WithEnvironment("ENABLE_BACKPLANE", "true")
    .WithEnvironment("ENABLE_MULTITENANT", "true")
    .WithReference(redisBackplane)
    .WithReference(redisShared)
    .WithReference(redisTenantAlpha)
    .WithReference(redisTenantBeta);

var apiNode2 = builder.AddProject<Projects.AspirePlayground_Api>("api-node2")
    .WithHttpEndpoint()
    .WithEnvironment("NODE_ID", "Multi-Node-2")
    .WithEnvironment("ENABLE_BACKPLANE", "true")
    .WithEnvironment("ENABLE_MULTITENANT", "true")
    .WithReference(redisBackplane)
    .WithReference(redisShared)
    .WithReference(redisTenantAlpha)
    .WithReference(redisTenantBeta);

builder.Build().Run();
