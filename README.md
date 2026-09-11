# HybridCache.Plus

[![NuGet](https://img.shields.io/nuget/v/HybridCache.Plus.svg)](https://www.nuget.org/packages/HybridCache.Plus/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen)](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**HybridCache.Plus** is a high-performance extension library for `Microsoft.Extensions.Caching.HybridCache` in **.NET 10**, powered by **Roslyn Source Generators**.

It completely eliminates magic strings in cache keys, automates cross-interface cache invalidation via repository decorators, and delivers zero-allocation span-based key formatting with full **Native AOT** compatibility.

---

## ⚡ Key Features

1. **Strongly-Typed Contracts**: Define declarative cache contracts using interfaces and attributes (`[HybridCacheKeys]`, `[CacheTemplate]`).
2. **Zero-Allocation Execution**: Formats key templates at compile time using `DefaultInterpolatedStringHandler` and `ReadOnlySpan<char>` with zero boxing and zero heap allocations.
3. **Automated Cross-Interface Invalidation (`[InvalidatedBy]`)**: Reader methods declare which mutator operations (e.g., `IProductRepository.UpdateProductAsync`) invalidate entries. Roslyn automatically generates decorators that intercept updates and purge keys/tags from `HybridCache`.
4. **Independent L1 + L2 TTLs**: Configure distinct expiration windows for local in-process memory (`LocalTtlSeconds`) and distributed caching (`DistributedTtlSeconds`).
5. **Configurable TTLs & Multi-Tenant Overrides**: Dynamically adjust or override TTLs at runtime via `appsettings.json` or Dependency Injection, with hierarchical per-tenant overrides (Free vs VIP tiers).
6. **Multi-Instance Real-Time Backplane**: Synchronizes L1 invalidations across pods/replicas in real time via Redis Pub/Sub with automatic local echo cancellation.
7. **Multi-Tenant L2 Redis Router**: Dynamically routes L2 cache operations to dedicated Redis instances per tenant while preserving HybridCache's native anti-stampede concurrency semaphores.
8. **Native AOT Ready**: 100% free of heavy runtime reflection; fully compatible with trimming and ahead-of-time compilation.

---

## 🚀 Quick Start

### 1. Define Your Typed Cache Contract

```csharp
using HybridCache.Plus;

[HybridCacheKeys]
public partial interface ICatalogCache
{
    [CacheTemplate("tenants:{tenantId}:products:{productId}", 
        PolicyName = "CatalogProducts",
        LocalTtlSeconds = 60, 
        DistributedTtlSeconds = 600, 
        Tags = ["tenant:{tenantId}"])]
    [InvalidatedBy<IProductRepository>(
        nameof(IProductRepository.UpdateProductAsync),
        nameof(IProductRepository.DeleteProductAsync))]
    ValueTask<ProductDetailDto> GetProductAsync(string tenantId, long productId);
}
```

### 2. Register Services in Dependency Injection

```csharp
// Standard Microsoft HybridCache registration
services.AddHybridCache();

// Register HybridCache.Plus Core
services.AddHybridCachePlus(builder =>
{
    // (Optional) Configure custom policy TTLs
    builder.ConfigurePolicy("CatalogProducts", p => p.LocalTtlSeconds = 120);

    // (Optional) Real-time multi-instance L1 synchronization via Redis Pub/Sub
    builder.UseRedisBackplane(redis =>
    {
        redis.ChannelName = "hybridcache:evictions";
        redis.Configuration = "localhost:6379"; // or provide IConnectionMultiplexer
    });

    // (Optional) Multi-tenant L2 Redis routing
    builder.UseMultiTenantRedisL2(tenancy =>
    {
        tenancy.ResolveConnectionString(tenantId => 
            configuration.GetConnectionString($"Redis_{tenantId}"));
    });
});

// Register your regular repository and attach the generated decorator in one line
services.AddScoped<IProductRepository, ProductRepository>();
services.DecorateProductRepositoryWithCache();
```

### 3. Consume in Application Code

```csharp
// 1. Strongly-typed cached read
var product = await cache.GetProductAsync(
    tenantId: "tenant_1", 
    productId: 101, 
    factory: async ct => await LoadProductFromDatabase(tenantId, productId, ct));

// 2. Repository mutation: the decorator automatically purges the key locally and publishes eviction across Redis
await repository.UpdateProductAsync("tenant_1", 101, newPrice: 49.99m);

// 3. Next read on ANY pod instantly detects invalidation and re-executes the factory
var updatedProduct = await cache.GetProductAsync("tenant_1", 101, factory);
```

---

## 🌐 Redis Eviction Backplane (Multi-Instance L1 Sync)

When multiple application replicas (pods) run `HybridCache`, an eviction on Instance A purges its local L1 and Redis L2, but Instances B, C, and D retain stale entries in their local L1 until their local TTL expires.

With **`HybridCache.Plus.Backplane.Redis`**:
- Every mutation or `Evict...Async` call publishes a compact message (`BackplaneEvictionMessage`) via Redis Pub/Sub.
- Reflection-free serialization using pre-compiled **Native AOT** `JsonSerializerContext`.
- Automatic echo cancellation (`OriginInstanceId == CurrentInstanceId`).
- Background worker (`RedisEvictionBackplaneWorker`) instantly purges the local L1 cache on all receiving replicas.

---

## 🏢 Multi-Tenant L2 Router (Isolated Redis per Tenant)

Allows different tenants to reside in physically isolated Redis clusters (for compliance, data sovereignty, or performance) while preserving `HybridCache` anti-stampede concurrency protection:

```csharp
services.AddHybridCachePlus(builder =>
{
    builder.UseMultiTenantRedisL2(redis =>
    {
        redis.ResolveConnectionString(tenantId => 
            configuration.GetConnectionString($"Redis_{tenantId}") 
            ?? configuration.GetConnectionString("Redis_Default")!);
        
        redis.EnableKeyPrefixTenantExtraction = true; // Extracts tenant from "tenants:{tenantId}:..." using Spans
    });
});
```

- **L1 and L2 Isolation**: Physical key separation (`tenants:{tenantId}:...`) prevents cross-tenant L1 cache key collisions.
- **Async Pass-Through**: Implements `IDistributedCache` as a lightweight pass-through to avoid breaking HybridCache's native concurrency semaphores.
- **Zero-Allocation**: Extracts tenant IDs via `ReadOnlySpan<char>` or ambient context via `ITenantContextAccessor` (`AsyncLocal`).

---

## ⏱️ Configurable TTLs & Multi-Tenant Overrides

Adjust or override `LocalTtlSeconds` and `DistributedTtlSeconds` dynamically from `appsettings.json` or DI without recompilation, with cascading multi-tenant rules:

```json
{
  "HybridCachePlus": {
    "Policies": {
      "CatalogProducts": {
        "LocalTtlSeconds": 60,
        "DistributedTtlSeconds": 600
      }
    },
    "Tenants": {
      "tenant_vip": {
        "Policies": {
          "CatalogProducts": {
            "LocalTtlSeconds": 10,
            "DistributedTtlSeconds": 60
          }
        }
      },
      "tenant_free": {
        "Policies": {
          "CatalogProducts": {
            "LocalTtlSeconds": 600,
            "DistributedTtlSeconds": 86400
          }
        }
      }
    }
  }
}
```

Or programmatically in `AddHybridCachePlus`:

```csharp
services.AddHybridCachePlus(builder =>
{
    builder.ConfigurePolicy("CatalogProducts", p => p.LocalTtlSeconds = 120);
    builder.ConfigureTenantPolicy("tenant_vip", "CatalogProducts", p => p.LocalTtlSeconds = 10);
});
```

- **Cascading Fallback**: Tenant-specific policy ➔ Tenant default ➔ Global policy ➔ Global default ➔ Attribute values.
- **Zero-Allocation Hot Path**: Pre-computes `HybridCacheEntryOptions` instances for $O(1)$ lookups during cache access.

---

## 🛠️ Compile-Time Roslyn Diagnostics

HybridCache.Plus enforces best practices at compile time:

| Code | Severity | Description |
|---|---|---|
| **`HCP001`** | Error | A key template placeholder (`{param}`) does not exist in the method parameter list. |
| **`HCP002`** | Error | The interface or method specified in `[InvalidatedBy]` does not exist or is inaccessible. |
| **`HCP003`** | Error | The contract method decorated with `[CacheTemplate]` does not return `ValueTask<T>` or `Task<T>`. |
| **`HCP004`** | Warning | The method declares a `tenantId` parameter but the key template omits `{tenantId}`, risking cross-tenant L1 collisions. |

---

## 📦 Modular NuGet Packages

To keep dependencies strictly minimal, HybridCache.Plus is distributed across 3 independent packages:

| Package | Purpose | Dependencies |
|---|---|---|
| **`HybridCache.Plus`** | **Core**: Typed contracts, Source Generator, zero-allocation span formatting, automated invalidation decorators, policy registry. | `Microsoft.Extensions.Caching.Hybrid` |
| **`HybridCache.Plus.Backplane.Redis`** | **L1 Sync**: Real-time multi-pod L1 invalidation synchronization via Redis Pub/Sub. | `HybridCache.Plus`, `StackExchange.Redis` |
| **`HybridCache.Plus.Tenancy.Redis`** | **L2 Multi-Tenancy**: Dynamic Redis routing and isolated connection pool per tenant. | `HybridCache.Plus`, `Microsoft.Extensions.Caching.StackExchangeRedis` |

```bash
# Install lightweight core (Zero Redis dependencies)
dotnet add package HybridCache.Plus

# (Optional) For real-time multi-pod L1 invalidation backplane
dotnet add package HybridCache.Plus.Backplane.Redis

# (Optional) For dynamic multi-tenant L2 Redis routing
dotnet add package HybridCache.Plus.Tenancy.Redis
```

---

## 📄 License

Licensed under the [MIT License](LICENSE).
