# HybridCache.Plus

[![NuGet](https://img.shields.io/nuget/v/HybridCache.Plus.svg)](https://www.nuget.org/packages/HybridCache.Plus/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen)](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

**HybridCache.Plus** es una extensión de alto rendimiento para `Microsoft.Extensions.Caching.HybridCache` en **.NET 10**, potenciada por **Roslyn Source Generators**.

Elimina por completo las cadenas mágicas en las claves de caché, automatiza la invalidación cruzada mediante decoradores de repositorios/servicios e implementa ensamblado de claves zero-allocation listo para Native AOT.

---

## ⚡ Características Principales

1. **Contratos Tipados**: Define contratos de caché con interfaces y atributos declarativos (`[HybridCacheKeys]`, `[CacheTemplate]`).
2. **Zero-Allocation**: Interpola plantillas de claves en tiempo de compilación con `DefaultInterpolatedStringHandler` y spans sin boxing ni allocations superfluas.
3. **Invalidación Cruzada Declarativa (`[InvalidatedBy]`)**: Los métodos de lectura indican qué mutaciones (ej. `IProductRepository.UpdateProductAsync`) invalidan la entrada. Roslyn genera automáticamente el decorador que intercepta las mutaciones y purga la clave y tags en `HybridCache`.
4. **Soporte L1 + L2 Nativo**: Define TTLs independientes para memoria local en proceso (`LocalTtlSeconds`) y distribuida (`DistributedTtlSeconds`).
5. **Native AOT Ready**: Código 100% libre de reflexión dinámica pesada, compatible con trimming y compilación AOT estricta.

---

## 🚀 Inicio Rápido

### 1. Definición del Contrato de Caché

```csharp
using HybridCache.Plus;

[HybridCacheKeys]
public partial interface ICatalogCache
{
    [CacheTemplate("tenants:{tenantId}:products:{productId}", 
        LocalTtlSeconds = 60, 
        DistributedTtlSeconds = 600, 
        Tags = ["tenant:{tenantId}"])]
    [InvalidatedBy<IProductRepository>(
        nameof(IProductRepository.UpdateProductAsync),
        nameof(IProductRepository.DeleteProductAsync))]
    ValueTask<ProductDetailDto> GetProductAsync(string tenantId, long productId);
}
```

### 2. Registro en Inyección de Dependencias
 
 ```csharp
 // Configuración estándar de HybridCache
 services.AddHybridCache();
 
 // (Opcional) Sincronización multi-instancia L1 en tiempo real con Redis Pub/Sub
 services.AddHybridCachePlus(options =>
 {
     options.UseRedisBackplane(redis =>
     {
         redis.ChannelName = "hybridcache:evictions";
         redis.Configuration = "localhost:6379"; // o inyecta IConnectionMultiplexer
     });
 });
 
 // Registra tu repositorio habitual y añade el decorador generado con una sola llamada
 services.AddScoped<IProductRepository, ProductRepository>();
 services.DecorateProductRepositoryWithCache();
 ```
 
 ### 3. Consumo en tu Código
 
 ```csharp
 // Lectura tipada y cacheada
 var product = await cache.GetProductAsync(
     tenantId: "tenant_1", 
     productId: 101, 
     factory: async ct => await LoadProductFromDatabase(tenantId, productId, ct));
 
 // Mutación en el repositorio: el decorador invalida automáticamente la clave y los tags localmente y en el backplane Redis
 await repository.UpdateProductAsync("tenant_1", 101, newPrice: 49.99m);
 
 // La siguiente lectura en CUALQUIER pod detectará la invalidación instantánea y recargará el dato actualizado
 var freshProduct = await cache.GetProductAsync("tenant_1", 101, factory);
 ```
 
 ---
 
 ## 🌐 Redis Eviction Backplane (Sincronización Multi-Instancia L1)
 
 Cuando múltiples réplicas (pods) ejecutan `HybridCache`, una expulsión en la Instancia A purga su L1 y Redis L2, pero las Instancias B, C y D mantienen el dato obsoleto en su L1 hasta el vencimiento del TTL local.
 
 Con el **Redis Eviction Backplane**:
 - Cada mutación o llamada a `Evict...Async` publica un mensaje compacto (`BackplaneEvictionMessage`) en Redis Pub/Sub.
 - Serialización zero-reflection 100% compatible con **Native AOT** (`JsonSerializerContext`).
 - Descarte automático del eco propio (`OriginInstanceId == CurrentInstanceId`).
 - Invocación en segundo plano (`RedisEvictionBackplaneWorker`) para purgar inmediatamente la L1 en todas las réplicas receptoras.

---

## 🏢 Multi-Tenant L2 Router (Aislamiento Dinámico de Redis por Tenant)

Permite que diferentes tenants residan en instancias o clústeres independientes de Redis manteniendo la protección anti-estampida nativa de `HybridCache`:

```csharp
services.AddHybridCachePlus(options =>
{
    options.UseMultiTenantRedisL2(tenant =>
    {
        tenant.ResolveConnectionString(tenantId => 
            configuration.GetConnectionString($"Redis_{tenantId}") 
            ?? configuration.GetConnectionString("Redis_Default")!);
        tenant.EnableKeyPrefixTenantExtraction = true; // Extrae el tenant de "tenants:{tenantId}:..." con ReadOnlySpan
    });
});
```

- **Aislamiento en L1 y L2**: Claves separadas físicamente por tenant (`tenants:{tenantId}:...`), garantizando que la L1 no sufra colisiones entre tenants.
- **Pass-through asíncrono**: Implementa `IDistributedCache` delegando transparentemente al pool lazy de conexiones sin romper los semáforos anti-estampida de `HybridCache`.
- **Zero-Allocation**: Extracción ultra-rápida del tenant con `ReadOnlySpan<char>` o resolución contextual vía `ITenantContextAccessor` (`AsyncLocal`).

---

## ⏱️ TTLs Configurables y Sobreescritura por Tenant

Permite que los tiempos de expiración (`LocalTtlSeconds` y `DistributedTtlSeconds`) se ajusten dinámicamente desde `appsettings.json` o Inyección de Dependencias, sin recompilar, con soporte jerárquico por tenant:

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

O programáticamente en `AddHybridCachePlus`:

```csharp
services.AddHybridCachePlus(builder =>
{
    builder.ConfigurePolicy("CatalogProducts", p => p.LocalTtlSeconds = 120);
    builder.ConfigureTenantPolicy("tenant_vip", "CatalogProducts", p => p.LocalTtlSeconds = 10);
});
```

- **Cascada**: Si un tenant no tiene configuración específica, hereda la política global. Si la política global no existe, recurre a los valores de `[CacheTemplate]`.
- **Zero-Allocation**: Opciones precomputadas con lookup $O(1)$ en el hot path.

---
 
 ## 🛠️ Diagnósticos de Compilación (Roslyn Analyzers)
 
 HybridCache.Plus previene errores en tiempo de compilación:
 
 | Código | Severidad | Descripción |
 |---|---|---|
 | **`HCP001`** | Error | Un placeholder en la plantilla de clave (`{param}`) no existe en la firma del método. |
 | **`HCP002`** | Error | El método o interfaz especificado en `[InvalidatedBy]` no existe o no es accesible. |
 | **`HCP003`** | Error | El método decorado con `[CacheTemplate]` no devuelve `ValueTask<T>` o `Task<T>`. |
 | **`HCP004`** | Advertencia | El método tiene un parámetro `tenantId` pero la plantilla omite `{tenantId}`, arriesgando colisiones L1 entre tenants. |
 
 ---
 
 ## 📦 Estructura Modular de Paquetes NuGet

Para mantener las dependencias al mínimo estricto, la biblioteca se distribuye en 3 paquetes independientes:

| Paquete | Propósito | Dependencias Principales |
|---|---|---|
| **`HybridCache.Plus`** | **Core**: Contratos tipados, Source Generator, interpolación de claves en spans zero-allocation, decoradores automáticos de invalidación. | `Microsoft.Extensions.Caching.Hybrid` |
| **`HybridCache.Plus.Backplane.Redis`** | **Sincronización L1**: Invalidation Backplane en tiempo real entre múltiples instancias/pods vía Redis Pub/Sub. | `HybridCache.Plus`, `StackExchange.Redis` |
| **`HybridCache.Plus.Tenancy.Redis`** | **Multi-Tenancy L2**: Router dinámico y Connection Pool de Redis aislado por tenant. | `HybridCache.Plus`, `Microsoft.Extensions.Caching.StackExchangeRedis` |

```bash
# Instalación del núcleo liviano (sin dependencias de Redis)
dotnet add package HybridCache.Plus

# (Opcional) Si requieres sincronización L1 multi-pod con Redis Pub/Sub
dotnet add package HybridCache.Plus.Backplane.Redis

# (Opcional) Si requieres enrutamiento multi-tenant dinámico en L2
dotnet add package HybridCache.Plus.Tenancy.Redis
```

---

## 📄 Licencia

Licencia MIT.
