using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Soenneker.OpenApi.Converters.Meta.Models;

/// <summary>Settings for converting Meta's SDK specifications.</summary>
public sealed class MetaOpenApiConverterOptions
{
    /// <summary>The Graph API version, such as v25.0. Must match the intended API and source specification version.</summary>
    public required string GraphApiVersion { get; init; }

    /// <summary>The generated document title.</summary>
    public string Title { get; init; } = "Meta Graph API";

    /// <summary>The HTTPS API origin, without a version or trailing path.</summary>
    public string ServerUrl { get; init; } = "https://graph.facebook.com";

    /// <summary>Optional case-sensitive node names whose operations to include. Null includes all. All supplied schemas remain available.</summary>
    public IReadOnlyCollection<string>? NodeTypes { get; init; }

    /// <summary>Throw instead of producing a permissive schema when a type cannot be resolved.</summary>
    public bool ThrowOnUnknownTypes { get; init; }

    /// <summary>
    /// OpenAPI response schemas keyed by "Node METHOD endpoint", for example "Page POST feed".
    /// For root operations use "Page GET". Values are cloned.
    /// </summary>
    public IReadOnlyDictionary<string, JsonObject> ResponseSchemaOverrides { get; init; }
        = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
}
