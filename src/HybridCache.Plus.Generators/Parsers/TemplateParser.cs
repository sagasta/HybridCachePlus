namespace HybridCache.Plus.Generators.Parsers;

public static class TemplateParser
{
    /// <summary>
    /// Extracts all variable placeholders (e.g. "{tenantId}") from a template string.
    /// </summary>
    public static List<string> ExtractPlaceholders(string? template)
    {
        var placeholders = new List<string>();
        if (string.IsNullOrWhiteSpace(template))
        {
            return placeholders;
        }

        var i = 0;
        while (i < template!.Length)
        {
            var open = template.IndexOf('{', i);
            if (open == -1) break;

            var close = template.IndexOf('}', open + 1);
            if (close == -1) break;

            var name = template.Substring(open + 1, close - open - 1).Trim();
            if (!string.IsNullOrEmpty(name))
            {
                // Normalize placeholder in case it has format specifier like {productId:D}
                var colonIdx = name.IndexOf(':');
                if (colonIdx > 0)
                {
                    name = name.Substring(0, colonIdx).Trim();
                }

                if (!placeholders.Contains(name))
                {
                    placeholders.Add(name);
                }
            }

            i = close + 1;
        }

        return placeholders;
    }

    /// <summary>
    /// Replaces placeholders in a template string with resolved expressions.
    /// </summary>
    public static string ReplacePlaceholders(string template, Func<string, string?> resolver)
    {
        if (string.IsNullOrWhiteSpace(template)) return template;

        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open == -1)
            {
                sb.Append(template.Substring(i));
                break;
            }

            sb.Append(template.Substring(i, open - i));
            var close = template.IndexOf('}', open + 1);
            if (close == -1)
            {
                sb.Append(template.Substring(open));
                break;
            }

            var rawPlaceholder = template.Substring(open + 1, close - open - 1).Trim();
            var formatSpec = "";
            var colonIdx = rawPlaceholder.IndexOf(':');
            var placeholderName = rawPlaceholder;
            if (colonIdx > 0)
            {
                placeholderName = rawPlaceholder.Substring(0, colonIdx).Trim();
                formatSpec = rawPlaceholder.Substring(colonIdx);
            }

            var resolved = resolver(placeholderName);
            if (!string.IsNullOrEmpty(resolved))
            {
                sb.Append('{');
                sb.Append(resolved);
                sb.Append(formatSpec);
                sb.Append('}');
            }
            else
            {
                sb.Append('{');
                sb.Append(rawPlaceholder);
                sb.Append('}');
            }

            i = close + 1;
        }

        return sb.ToString();
    }
}
