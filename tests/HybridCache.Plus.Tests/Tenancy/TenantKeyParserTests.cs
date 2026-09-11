using Xunit;
using HybridCache.Plus.Tenancy;

namespace HybridCache.Plus.Tests.Tenancy;

public class TenantKeyParserTests
{
    [Theory]
    [InlineData("tenants:tenant_1:products:100", "tenants:", "tenant_1")]
    [InlineData("tenants:acme-corp:orders:999:items:1", "tenants:", "acme-corp")]
    [InlineData("tenants:global_tenant", "tenants:", "global_tenant")]
    [InlineData("t:tenant_xyz:items:42", "t:", "tenant_xyz")]
    [InlineData("tenant:alpha_10:catalog:list", "tenant:", "alpha_10")]
    public void TryExtractTenantId_ValidKeys_ExtractsTenantAccurately(string key, string prefix, string expectedTenant)
    {
        var success = TenantKeyParser.TryExtractTenantId(key.AsSpan(), prefix.AsSpan(), out var tenantSpan);

        Assert.True(success);
        Assert.Equal(expectedTenant, tenantSpan.ToString());
    }

    [Theory]
    [InlineData("products:100", "tenants:")]
    [InlineData("users:tenant_1:profile", "tenants:")]
    [InlineData("tenants:", "tenants:")]
    [InlineData("", "tenants:")]
    [InlineData("tenants:tenant_1", "")]
    public void TryExtractTenantId_InvalidOrMismatchedKeys_ReturnsFalse(string key, string prefix)
    {
        var success = TenantKeyParser.TryExtractTenantId(key.AsSpan(), prefix.AsSpan(), out var tenantSpan);

        Assert.False(success);
        Assert.True(tenantSpan.IsEmpty);
    }

    [Fact]
    public void ExtractTenantIdString_ValidAndInvalidKeys_ReturnsExpectedString()
    {
        var tenant = TenantKeyParser.ExtractTenantIdString("tenants:beta_partner:products:55", "tenants:");
        Assert.Equal("beta_partner", tenant);

        var nullTenant = TenantKeyParser.ExtractTenantIdString("products:55", "tenants:");
        Assert.Null(nullTenant);
    }
}
