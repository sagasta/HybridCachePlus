namespace HybridCache.Plus.Generators.Emitters;

internal static class EmitterHelpers
{
    /// <summary>
    /// Removes the conventional interface "I" prefix ("IProductCache" -> "ProductCache", "IInventory" -> "Inventory"),
    /// leaving names that merely start with an 'I' untouched ("Item" -> "Item").
    /// </summary>
    public static string StripInterfacePrefix(string interfaceName)
    {
        return interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1])
            ? interfaceName.Substring(1)
            : interfaceName;
    }

    /// <summary>
    /// Escapes text so it can be embedded inside a regular C# string literal.
    /// </summary>
    public static string EscapeLiteral(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Builds a culture-invariant interpolated string expression for a resolved template,
    /// so cache keys and tags are identical across nodes regardless of their current culture.
    /// </summary>
    public static string InvariantInterpolation(string resolvedTemplate)
    {
        return $"string.Create(global::System.Globalization.CultureInfo.InvariantCulture, $\"{EscapeLiteral(resolvedTemplate)}\")";
    }
}
