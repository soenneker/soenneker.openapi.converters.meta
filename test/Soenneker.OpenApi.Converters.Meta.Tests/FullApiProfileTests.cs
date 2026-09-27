using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.OpenApi.Converters.Meta.Tests;

public sealed class FullApiProfileTests
{
    [Test]
    public void FacebookIncludesEverySourceOperationAndField()
    {
        var result = Convert(MetaOpenApiProfile.Facebook);
        var doc = result.Document;
        Check(doc["x-meta-source"]!["includedOperationCount"]!.GetValue<int>() == 7, "No source operations excluded");
        Check(Operations(doc).SelectMany(op => op["x-meta-operations"]!.AsArray()).Count() == 8, "All seven source operations plus container polling emitted");
        Check(doc["paths"]!["/{node-id}/campaigns"]?["post"] is not null, "Ads retained");
        Check(doc["paths"]!["/{node-id}/new_endpoint"]?["get"] is not null, "New upstream endpoints included automatically");
        Check(doc["components"]!["schemas"]!["Page"]!["properties"]!["unusual_field"] is not null, "Full fields retained");
        var content = doc["paths"]!["/{node-id}/feed"]!["post"]!["requestBody"]!["content"]!;
        Check(content["multipart/form-data"]!["schema"]!["properties"]!["thumbnail"] is not null, "File uploads retained");
        Check(content["application/x-www-form-urlencoded"]!["schema"]!["properties"]!["message"] is not null, "Shared form includes Page fields");
        Check(content["application/x-www-form-urlencoded"]!["schema"]!["anyOf"] is null, "Request remains Kiota serializable");
        Validate(result);
    }

    [Test]
    public void InstagramIncludesAllInstagramNodesAndRelatedOperations()
    {
        var doc = Convert(MetaOpenApiProfile.Instagram).Document;
        Check(doc["paths"]!["/{node-id}/insights"]?["get"] is not null, "Insights retained");
        Check(doc["paths"]!["/{node-id}/new_endpoint"]?["get"] is not null, "New Instagram nodes and endpoints included automatically");
        Check(doc["paths"]!["/{node-id}/owned_instagram_accounts"]?["get"] is not null, "Cross-platform Instagram edge retained");
        Check(doc["paths"]!["/{node-id}/campaigns"] is null, "Unrelated ad operation excluded from Instagram");
        Check(doc["x-meta-source"]!["includedOperationCount"]!.GetValue<int>() == 4, "Complete Instagram source operation coverage");
    }

    [Test]
    public void FullProfilesRejectSilentNodeRestriction()
    {
        try
        {
            new MetaOpenApiConverter().Convert(Specifications(), new MetaOpenApiConverterOptions { GraphApiVersion = "v26.0", Profile = MetaOpenApiProfile.Facebook, NodeTypes = ["Page"] });
        }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Full profile allowed an endpoint restriction");
    }

    [Test]
    public void DetectsOperationsDroppedByDownstreamNormalization()
    {
        var result = Convert(MetaOpenApiProfile.Facebook);
        var fixedDocument = (JsonObject)result.Document.DeepClone();
        result.ValidateOperationCoverage(fixedDocument.ToJsonString());
        fixedDocument["paths"]!.AsObject().Remove("/{node-id}/campaigns");
        try { result.ValidateOperationCoverage(fixedDocument.ToJsonString()); }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("AdAccount", StringComparison.Ordinal), "Missing source operation identified");
            return;
        }
        throw new InvalidOperationException("Downstream endpoint removal was not detected");
    }
    private static MetaOpenApiConversionResult Convert(MetaOpenApiProfile profile) => new MetaOpenApiConverter().Convert(Specifications(), new MetaOpenApiConverterOptions { GraphApiVersion = "v26.0", Profile = profile });
    private static IEnumerable<JsonObject> Operations(JsonObject doc) => doc["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject().Select(op => op.Value!.AsObject()));
    private static Dictionary<string, string> Specifications() => new()
    {
        ["Page"] = """{"fields":[{"name":"id","type":"string"},{"name":"unusual_field","type":"string"}],"apis":[{"method":"GET","return":"Page","params":[]},{"method":"POST","endpoint":"feed","params":[{"name":"message","type":"string"},{"name":"thumbnail","type":"file"}]}]}""",
        ["User"] = """{"fields":[],"apis":[{"method":"POST","endpoint":"feed","params":[{"name":"link","type":"string"}]}]}""",
        ["AdAccount"] = """{"fields":[],"apis":[{"method":"POST","endpoint":"campaigns","params":[]}]}""",
        ["IGUser"] = """{"fields":[],"apis":[{"method":"GET","endpoint":"insights","return":"IGUser","params":[]}]}""",
        ["IGFutureResource"] = """{"fields":[],"apis":[{"method":"GET","endpoint":"new_endpoint","return":"IGFutureResource","params":[]}]}""",
        ["Business"] = """{"fields":[],"apis":[{"method":"GET","endpoint":"owned_instagram_accounts","return":"IGUser","params":[]}]}"""
    };
    private static void Validate(MetaOpenApiConversionResult result)
    {
        var parsed = OpenApiDocument.Parse(result.ToJson(), "json");
        Check(parsed.Document is not null && parsed.Diagnostic?.Errors.Count == 0, "Valid OpenAPI document");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}