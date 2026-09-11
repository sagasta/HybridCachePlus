using System.Diagnostics;
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

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
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

                if (policy == "UniqueHitPolicy")
                {
                    hitMeasurements.Add((measurement, policy, tenant, template));
                }
            }
        });

        listener.Start();

        // Act
        HybridCachePlusDiagnostics.RecordHit("UniqueHitPolicy", "tenant_alpha", "tenants:{tenantId}:products:{productId}");

        // Assert
        Assert.Single(hitMeasurements);
        var hit = hitMeasurements[0];
        Assert.Equal(1, hit.Value);
        Assert.Equal("UniqueHitPolicy", hit.Policy);
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

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
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

                if (policy == "UniqueMissPolicy")
                {
                    missMeasurements.Add((measurement, policy, tenant, template));
                }
            }
        });

        listener.Start();

        // Act
        HybridCachePlusDiagnostics.RecordMiss("UniqueMissPolicy", "tenant_beta", "tenants:{tenantId}:products:{productId}");

        // Assert
        Assert.Single(missMeasurements);
        var miss = missMeasurements[0];
        Assert.Equal(1, miss.Value);
        Assert.Equal("UniqueMissPolicy", miss.Policy);
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

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
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

    [Fact]
    public void RecordDuration_RecordsMeasurements_ToDurationHistogram()
    {
        HybridCachePlusDiagnostics.IsEnabled = true;
        var durations = new List<(double Value, string? Operation, string? Policy, string? Tenant)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == HybridCachePlusDiagnostics.MeterName && instrument.Name == "hybridcache_plus.duration")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "hybridcache_plus.duration")
            {
                string? op = null;
                string? pol = null;
                string? ten = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "cache.operation") op = tag.Value?.ToString();
                    if (tag.Key == "cache.policy") pol = tag.Value?.ToString();
                    if (tag.Key == "cache.tenant") ten = tag.Value?.ToString();
                }
                if (pol == "UniqueDiagnosticDurationPolicy")
                {
                    durations.Add((measurement, op, pol, ten));
                }
            }
        });

        listener.Start();

        // Act
        HybridCachePlusDiagnostics.RecordDuration(4.5, "GetOrCreate", "UniqueDiagnosticDurationPolicy", "tenant_alpha");

        // Assert
        Assert.Single(durations);
        Assert.Equal(4.5, durations[0].Value);
        Assert.Equal("GetOrCreate", durations[0].Operation);
        Assert.Equal("UniqueDiagnosticDurationPolicy", durations[0].Policy);
        Assert.Equal("tenant_alpha", durations[0].Tenant);
    }

    [Fact]
    public void ActivitySource_WhenListenerAttached_StartsAndEmitsActivity()
    {
        HybridCachePlusDiagnostics.IsEnabled = true;
        Activity? capturedActivity = null;

        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == HybridCachePlusDiagnostics.ActivitySourceName;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStopped = act => capturedActivity = act;
        ActivitySource.AddActivityListener(listener);

        // Act
        using (var act = HybridCachePlusDiagnostics.StartActivity("GetProductAsync", "CatalogProducts", "tenant_beta"))
        {
            Assert.NotNull(act);
            Assert.Equal("CatalogProducts", act.GetTagItem("cache.policy"));
            Assert.Equal("tenant_beta", act.GetTagItem("cache.tenant"));
        }

        // Assert
        Assert.NotNull(capturedActivity);
        Assert.Equal(HybridCachePlusDiagnostics.ActivitySource.Name, capturedActivity.Source.Name);
    }

    [Fact]
    public void Builder_EnableDiagnostics_ConfiguresDiagnosticsFlag()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new HybridCachePlusBuilder(services);

        builder.EnableDiagnostics(false);
        Assert.False(HybridCachePlusDiagnostics.IsEnabled);

        builder.EnableDiagnostics(true);
        Assert.True(HybridCachePlusDiagnostics.IsEnabled);
    }
}
