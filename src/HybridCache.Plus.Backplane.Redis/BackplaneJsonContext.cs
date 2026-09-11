using System.Text.Json.Serialization;

namespace HybridCache.Plus.Backplane.Redis;

/// <summary>
/// Pre-compiled System.Text.Json source generation context for zero-reflection, Native AOT serialization.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(BackplaneEvictionMessage))]
public sealed partial class BackplaneJsonContext : JsonSerializerContext;
