using System.Diagnostics.Metrics;
using HybridCache.Plus.Diagnostics;
using Xunit;

namespace HybridCache.Plus.Tests.Diagnostics;

[Collection("StaticStateTests")]
public class DiagnosticsTests
{
    [Fact]
    public void RecordHit_EmitsMeasurement_WithCorrectTags()
    {
        HybridCachePlusDiagnostics.IsEnabled = true;
        var hitMeasurements = new List<(long Value, string? Policy, string? Tenant, string? Template)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == HybridCachePlusDiagnostics.MeterName && instrument.Name == "hybridcache_plus.hits")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "hybridcache_plus.hits")
            {
                string? policy = null;
                string? tenant = null;
                string? template = null;

                foreach (var tag in tags)
                {
                    if (tag.Key == "cache.policy") policy = tag.Value?.ToString();
                    if (tag.Key == "cache.tenant") tenant = tag.Value?.ToString();
                    if (tag.Key == "cache.template") template = tag.Value?.ToString();
                }

                hitMeasurements.Add((measurement, policy, tenant, template));
            }
        });

        listener.Start();

        // Act
        HybridCachePlusDiagnostics.RecordHit("CatalogProducts", "tenant_alpha", "tenants:{tenantId}:products:{productId}");

        // Assert
        Assert.Single(hitMeasurements);
        var hit = hitMeasurements[0];
        Assert.Equal(1, hit.Value);
        Assert.Equal("CatalogProducts", hit.Policy);
        Assert.Equal("tenant_alpha", hit.Tenant);
        Assert.Equal("tenants:{tenantId}:products:{productId}", hit.Template);
    }

    [Fact]
    public void RecordMiss_EmitsMeasurement_WithCorrectTags()
    {
        HybridCachePlusDiagnostics.IsEnabled = true;
        var missMeasurements = new List<(long Value, string? Policy, string? Tenant, string? Template)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == HybridCachePlusDiagnostics.MeterName && instrument.Name == "hybridcache_plus.misses")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "hybridcache_plus.misses")
            {
                string? policy = null;
                string? tenant = null;
                string? template = null;

                foreach (var tag in tags)
                {
                    if (tag.Key == "cache.policy") policy = tag.Value?.ToString();
                    if (tag.Key == "cache.tenant") tenant = tag.Value?.ToString();
                    if (tag.Key == "cache.template") template = tag.Value?.ToString();
                }

                missMeasurements.Add((measurement, policy, tenant, template));
            }
        });

        listener.Start();

        // Act
        HybridCachePlusDiagnostics.RecordMiss("CatalogProducts", "tenant_beta", "tenants:{tenantId}:products:{productId}");

        // Assert
        Assert.Single(missMeasurements);
        var miss = missMeasurements[0];
        Assert.Equal(1, miss.Value);
        Assert.Equal("CatalogProducts", miss.Policy);
        Assert.Equal("tenant_beta", miss.Tenant);
    }

    [Fact]
    public void RecordEviction_EmitsMeasurement_WithReasonAndKey()
    {
        HybridCachePlusDiagnostics.IsEnabled = true;
        var evictions = new List<(long Value, string? Reason, string? Key)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == HybridCachePlusDiagnostics.MeterName && instrument.Name == "hybridcache_plus.evictions")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "hybridcache_plus.evictions")
            {
                string? reason = null;
                string? key = null;

                foreach (var tag in tags)
                {
                    if (tag.Key == "eviction.reason") reason = tag.Value?.ToString();
                    if (tag.Key == "cache.key") key = tag.Value?.ToString();
                }

                evictions.Add((measurement, reason, key));
            }
        });

        listener.Start();

        // Act
        HybridCachePlusDiagnostics.RecordEviction("tenants:t1:products:99", "DecoratorMutation", "t1");

        // Assert
        Assert.Single(evictions);
        Assert.Equal("DecoratorMutation", evictions[0].Reason);
        Assert.Equal("tenants:t1:products:99", evictions[0].Key);
    }

    [Fact]
    public void WhenDiagnosticsDisabled_NoMeasurementsRecorded()
    {
        HybridCachePlusDiagnostics.IsEnabled = false;
        var count = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == HybridCachePlusDiagnostics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, _, _, _) => count++);
        listener.Start();

        // Act
        HybridCachePlusDiagnostics.RecordHit("Policy", "t1", "template");
        HybridCachePlusDiagnostics.RecordMiss("Policy", "t1", "template");
        HybridCachePlusDiagnostics.RecordEviction("key", "reason");

        // Assert
        Assert.Equal(0, count);

        // Reset for subsequent tests
        HybridCachePlusDiagnostics.IsEnabled = true;
    }
}
