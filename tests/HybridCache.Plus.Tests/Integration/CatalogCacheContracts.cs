namespace HybridCache.Plus.Tests.Integration;

public record ProductDetailDto(string TenantId, long ProductId, string Name, decimal Price);

public interface IProductRepository
{
    Task<ProductDetailDto> UpdateProductAsync(string tenantId, long productId, string newName, decimal newPrice, CancellationToken cancellationToken = default);
    Task DeleteProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
    Task<ProductDetailDto?> GetByIdAsync(string tenantId, long productId, CancellationToken cancellationToken = default);
}

public class ProductRepository : IProductRepository
{
    private readonly Dictionary<(string, long), ProductDetailDto> _store = new();

    public int UpdateCount { get; private set; }
    public int DeleteCount { get; private set; }

    public Task<ProductDetailDto> UpdateProductAsync(string tenantId, long productId, string newName, decimal newPrice, CancellationToken cancellationToken = default)
    {
        UpdateCount++;
        var product = new ProductDetailDto(tenantId, productId, newName, newPrice);
        _store[(tenantId, productId)] = product;
        return Task.FromResult(product);
    }

    public Task DeleteProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default)
    {
        DeleteCount++;
        _store.Remove((tenantId, productId));
        return Task.CompletedTask;
    }

    public Task<ProductDetailDto?> GetByIdAsync(string tenantId, long productId, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue((tenantId, productId), out var product);
        return Task.FromResult(product);
    }
}

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
