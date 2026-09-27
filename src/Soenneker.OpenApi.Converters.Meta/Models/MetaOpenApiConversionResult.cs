using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soenneker.OpenApi.Converters.Meta.Models;

/// <summary>A generated OpenAPI document and details requiring review against Meta's endpoint documentation.</summary>
public sealed class MetaOpenApiConversionResult
{
    internal MetaOpenApiConversionResult(JsonObject document, IReadOnlyList<string> diagnostics)
    {
        Document = document;
        Diagnostics = diagnostics;
    }

    /// <summary>The editable OpenAPI 3.1.0 document using the OpenAPI JSON Schema dialect. Callers may apply endpoint-specific corrections before serialization.</summary>
    public JsonObject Document { get; }

    /// <summary>Distinct warnings about inferred response shapes, merged operations, or unresolved types.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Throws if a downstream normalization removed or changed any source operation identity. Graph node provenance must be retained.</summary>
    /// <param name="openApiJson">The normalized OpenAPI JSON document to check before generating a client.</param>
    public void ValidateOperationCoverage(string openApiJson)
    {
        JsonObject target = JsonNode.Parse(openApiJson)?.AsObject() ?? throw new ArgumentException("An OpenAPI document is required.", nameof(openApiJson));
        Dictionary<string, int> expected = Counts(Document);
        Dictionary<string, int> actual = Counts(target);
        foreach (var operation in expected)
            if (!actual.TryGetValue(operation.Key, out int count) || count < operation.Value)
                throw new InvalidOperationException($"OpenAPI normalization removed source operation '{operation.Key}'.");
    }

    private static Dictionary<string, int> Counts(JsonObject document)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (document["paths"] is not JsonObject paths) return counts;
        foreach (JsonObject path in paths.Select(x => x.Value).OfType<JsonObject>())
        foreach (JsonObject operation in path.Select(x => x.Value).OfType<JsonObject>())
        {
            if (operation["x-meta-operations"] is not JsonArray definitions) continue;
            foreach (JsonObject definition in definitions.OfType<JsonObject>())
            {
                string key = string.Join(" ", new[] { "node", "method", "basePath", "endpoint" }.Select(name => definition[name]?.GetValue<string>() ?? ""));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        return counts;
    }
    /// <summary>Serializes the document to OpenAPI JSON.</summary>
    public string ToJson(bool indented = true) => Document.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
}
