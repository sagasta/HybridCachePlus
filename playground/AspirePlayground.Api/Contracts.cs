using HybridCache.Plus;

namespace AspirePlayground.Api;

public record ProductDto(
    string TenantId,
    long ProductId,
    string Name,
    decimal Price,
    DateTimeOffset LastUpdated);

public record GetProductQuery(string TenantId, long ProductId);
public record UpdateProductCommand(string TenantId, long ProductId, string Name, decimal Price);

public interface IProductRepository
{
    Task<ProductDto?> GetByIdAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
    Task<ProductDto> UpdateProductAsync(string tenantId, long productId, string name, decimal price, CancellationToken cancellationToken = default);
    Task<bool> DeleteProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
    Task<ProductDto> UpdateWithCommandAsync(UpdateProductCommand command, CancellationToken cancellationToken = default);
}

[HybridCacheKeys]
public partial interface ICatalogCache
{
    [CacheTemplate("tenants:{tenantId}:products:{productId}",
        PolicyName = "CatalogProducts",
        LocalTtlSeconds = 120,
        DistributedTtlSeconds = 600,
        Tags = ["tenant:{tenantId}"])]
    [InvalidatedBy<IProductRepository>(
        nameof(IProductRepository.UpdateProductAsync),
        nameof(IProductRepository.DeleteProductAsync))]
    ValueTask<ProductDto> GetProductAsync(string tenantId, long productId);

    [CacheTemplate("tenants:{query.TenantId}:products:{query.ProductId}",
        PolicyName = "CatalogProductsByQuery",
        LocalTtlSeconds = 120,
        DistributedTtlSeconds = 600,
        Tags = ["tenant:{query.TenantId}"])]
    [InvalidatedBy<IProductRepository>(
        nameof(IProductRepository.UpdateWithCommandAsync))]
    ValueTask<ProductDto> GetProductByQueryAsync(GetProductQuery query);
}
