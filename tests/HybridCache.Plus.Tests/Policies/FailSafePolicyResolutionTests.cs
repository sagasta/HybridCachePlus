using HybridCache.Plus.Options;
using HybridCache.Plus.Policies;
using HybridCache.Plus.Resilience;
using Xunit;

namespace HybridCache.Plus.Tests.Policies;

public class FailSafePolicyResolutionTests
{
    private static readonly FailSafePolicy Attribute = FailSafePolicy.FromSettings(3600, 200, 10);

    [Fact]
    public void FromSettings_ZeroMaxAge_IsDisabled()
    {
        Assert.Same(FailSafePolicy.Disabled, FailSafePolicy.FromSettings(0, 500));
        Assert.False(FailSafePolicy.Disabled.IsEnabled);
    }

    [Fact]
    public void FromSettings_ZeroThrottle_UsesDefault()
    {
        var policy = FailSafePolicy.FromSettings(60);

        Assert.True(policy.IsEnabled);
        Assert.Null(policy.FactorySoftTimeout);
        Assert.Equal(FailSafePolicy.DefaultThrottleDuration, policy.ThrottleDuration);
    }

    [Fact]
    public void Resolve_NoOverrides_ReturnsAttributePolicy()
    {
        var registry = new HybridCachePlusPolicyRegistry(new HybridCachePlusPolicyOptions());

        Assert.Same(Attribute, registry.ResolveFailSafePolicy("Prices", "t1", Attribute));
    }

    [Fact]
    public void Resolve_GlobalOverride_ReplacesOnlyConfiguredSettings()
    {
        var options = new HybridCachePlusPolicyOptions();
        options.Policies["Prices"] = new CachePolicyOptions { FailSafeMaxAgeSeconds = 120 };
        var registry = new HybridCachePlusPolicyRegistry(options);

        var resolved = registry.ResolveFailSafePolicy("Prices", null, Attribute);

        Assert.Equal(TimeSpan.FromSeconds(120), resolved.MaxAge);
        Assert.Equal(TimeSpan.FromMilliseconds(200), resolved.FactorySoftTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), resolved.ThrottleDuration);
    }

    [Fact]
    public void Resolve_TenantOverride_WinsOverGlobal()
    {
        var options = new HybridCachePlusPolicyOptions();
        options.Policies["Prices"] = new CachePolicyOptions { FailSafeMaxAgeSeconds = 120, FactorySoftTimeoutMs = 50 };
        options.Tenants["vip"] = new TenantPolicyOptions
        {
            Policies = { ["Prices"] = new CachePolicyOptions { FactorySoftTimeoutMs = 0 } }
        };
        var registry = new HybridCachePlusPolicyRegistry(options);

        var vip = registry.ResolveFailSafePolicy("Prices", "vip", Attribute);
        var other = registry.ResolveFailSafePolicy("Prices", "free", Attribute);

        Assert.Equal(TimeSpan.FromSeconds(120), vip.MaxAge);
        Assert.Null(vip.FactorySoftTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(50), other.FactorySoftTimeout);
    }

    [Fact]
    public void Resolve_ZeroMaxAge_DisablesFailSafe()
    {
        var options = new HybridCachePlusPolicyOptions();
        options.Tenants["free"] = new TenantPolicyOptions
        {
            Policies = { ["Prices"] = new CachePolicyOptions { FailSafeMaxAgeSeconds = 0 } }
        };
        var registry = new HybridCachePlusPolicyRegistry(options);

        Assert.False(registry.ResolveFailSafePolicy("Prices", "free", Attribute).IsEnabled);
        Assert.True(registry.ResolveFailSafePolicy("Prices", "other", Attribute).IsEnabled);
    }

    [Fact]
    public void Resolve_IsMemoizedAndRefreshedOnReload()
    {
        var options = new HybridCachePlusPolicyOptions();
        options.Policies["Prices"] = new CachePolicyOptions { FailSafeMaxAgeSeconds = 120 };
        var registry = new HybridCachePlusPolicyRegistry(options);

        var first = registry.ResolveFailSafePolicy("Prices", null, Attribute);
        Assert.Same(first, registry.ResolveFailSafePolicy("Prices", null, Attribute));

        var reloaded = new HybridCachePlusPolicyOptions();
        reloaded.Policies["Prices"] = new CachePolicyOptions { FailSafeMaxAgeSeconds = 30 };
        registry.Reload(reloaded);

        Assert.Equal(TimeSpan.FromSeconds(30), registry.ResolveFailSafePolicy("Prices", null, Attribute).MaxAge);
    }
}
