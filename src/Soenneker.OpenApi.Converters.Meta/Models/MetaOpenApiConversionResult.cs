using System.Collections.Generic;
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

    /// <summary>Serializes the document to OpenAPI JSON.</summary>
    public string ToJson(bool indented = true) => Document.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
}
