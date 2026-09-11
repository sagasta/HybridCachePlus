using Microsoft.Data.Sqlite;

namespace AspirePlayground.Api;

public class ProductRepository : IProductRepository
{
    private readonly string _connectionString;
    private static int _queryCount;

    public static int TotalDbQueries => _queryCount;

    public static void ResetQueryCount() => Interlocked.Exchange(ref _queryCount, 0);

    public ProductRepository(IConfiguration configuration)
    {
        var dbPath = configuration["DATABASE_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "playground_products.db");
        var fullDbPath = Path.GetFullPath(dbPath);
        _connectionString = $"Data Source={fullDbPath}";

        InitDatabase();
    }

    private void InitDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Products (
                TenantId TEXT NOT NULL,
                ProductId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Price DECIMAL NOT NULL,
                LastUpdated TEXT NOT NULL,
                PRIMARY KEY (TenantId, ProductId)
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<ProductDto?> GetByIdAsync(string tenantId, long productId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _queryCount);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name, Price, LastUpdated FROM Products WHERE TenantId = $tenantId AND ProductId = $productId";
        command.Parameters.AddWithValue("$tenantId", tenantId);
        command.Parameters.AddWithValue("$productId", productId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var price = reader.GetDecimal(1);
            var updated = DateTimeOffset.Parse(reader.GetString(2));
            return new ProductDto(tenantId, productId, name, price, updated);
        }

        // Seed default item if not present so testing is instantaneous
        var defaultProduct = new ProductDto(
            tenantId,
            productId,
            $"Item #{productId} (Tenant: {tenantId})",
            99.99m,
            DateTimeOffset.UtcNow);

        await SaveProductAsync(connection, defaultProduct, cancellationToken);
        return defaultProduct;
    }

    public async Task<ProductDto> UpdateProductAsync(string tenantId, long productId, string name, decimal price, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _queryCount);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var product = new ProductDto(tenantId, productId, name, price, DateTimeOffset.UtcNow);
        await SaveProductAsync(connection, product, cancellationToken);
        return product;
    }

    public Task<ProductDto> UpdateWithCommandAsync(UpdateProductCommand command, CancellationToken cancellationToken = default)
    {
        return UpdateProductAsync(command.TenantId, command.ProductId, command.Name, command.Price, cancellationToken);
    }

    public async Task<bool> DeleteProductAsync(string tenantId, long productId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _queryCount);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Products WHERE TenantId = $tenantId AND ProductId = $productId";
        command.Parameters.AddWithValue("$tenantId", tenantId);
        command.Parameters.AddWithValue("$productId", productId);

        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    private static async Task SaveProductAsync(SqliteConnection connection, ProductDto product, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Products (TenantId, ProductId, Name, Price, LastUpdated)
            VALUES ($tenantId, $productId, $name, $price, $lastUpdated)
            ON CONFLICT(TenantId, ProductId) DO UPDATE SET
                Name = excluded.Name,
                Price = excluded.Price,
                LastUpdated = excluded.LastUpdated;
            """;
        command.Parameters.AddWithValue("$tenantId", product.TenantId);
        command.Parameters.AddWithValue("$productId", product.ProductId);
        command.Parameters.AddWithValue("$name", product.Name);
        command.Parameters.AddWithValue("$price", product.Price);
        command.Parameters.AddWithValue("$lastUpdated", product.LastUpdated.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
