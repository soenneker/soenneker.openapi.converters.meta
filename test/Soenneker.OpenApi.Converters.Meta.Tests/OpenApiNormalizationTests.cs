using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.OpenApi.Converters.Meta.Tests;

public sealed class OpenApiNormalizationTests
{
    [Test]
    public void NormalizesAndPrunesWithoutAddingPlatformPolicy()
    {
        var input = new Dictionary<string, string>
        {
            ["CustomNode"] = """{"fields":[{"name":"id","type":"string"}],"apis":[{"method":"GET","return":"CustomNode","params":[]}]}""",
            ["Unused"] = """{"fields":[{"name":"name","type":"string"}],"apis":[]}"""
        };
        var result = new MetaOpenApiConverter().Convert(input, new MetaOpenApiConverterOptions
        {
            GraphApiVersion = "v1.0", ServerUrl = "https://graph.threads.com",
            NormalizeForKiota = true, PruneUnusedSchemas = true
        });
        var doc = result.Document;
        if (doc["paths"]!.AsObject().Count != 1 || doc["paths"]!["/{node-id}"]?["get"] is null)
            throw new InvalidOperationException("Normalization must preserve only the supplied operations.");
        if (doc["components"]!["schemas"]!.AsObject().Select(x => x.Key).Single() != "CustomNode")
            throw new InvalidOperationException("Only referenced source schemas should remain.");
        if (doc["x-meta-source"] is not null)
            throw new InvalidOperationException("Source provenance belongs to the caller.");
        result.ValidateOperationCoverage(doc.ToJsonString());
        if (JsonNode.Parse(input["CustomNode"])!["apis"]!.AsArray().Count != 1)
            throw new InvalidOperationException("Source definitions must not be changed.");
    }
}
