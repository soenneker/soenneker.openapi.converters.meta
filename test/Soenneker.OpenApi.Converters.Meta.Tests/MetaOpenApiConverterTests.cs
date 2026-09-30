using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.OpenApi;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.OpenApi.Converters.Meta.Tests;

public sealed class MetaOpenApiConverterTests
{
    private readonly MetaOpenApiConverter _converter = new();
    private static MetaOpenApiConverterOptions Options => new() { GraphApiVersion = "v25.0" };

    private static Dictionary<string, string> Specs() => new()
    {
        ["Page.json"] = """
        {"fields":[{"name":"id","type":"string"},{"name":"children","type":"list<Page>"},
          {"name":"counts","type":"map<string, list<unsigned int>>"},{"name":"status","type":"string"}],
         "apis":[{"name":"#get","method":"GET","return":"Page","params":[]},
          {"endpoint":"feed","method":"GET","return":"Post","params":[{"name":"filter","type":"map","required":false}]},
          {"endpoint":"feed","method":"POST","return":"Page","params":[{"name":"message","type":"string","required":true},
           {"name":"attached_media","type":"list<Object>","required":false}]},
          {"endpoint":"photos","method":"POST","return":"Photo","params":[{"name":"source","type":"file","required":true}]}]}
        """,
        ["Post"] = """{"fields":[{"name":"message","type":"string"}],"apis":[]}""",
        ["enum_types.json"] = """[{"name":"Page_status","node":"Page","field_or_param":"status","values":["ACTIVE","PAUSED"]}]"""
    };

    [Test]
    public void ConvertsTypesAndPublishingIntoValidOpenApi()
    {
        MetaOpenApiConversionResult result = _converter.Convert(Specs(), Options);
        JsonObject doc = result.Document;
        Check(doc["components"]!["schemas"]!["Page"]!["properties"]!["children"]!["items"]!["$ref"]!.GetValue<string>() == "#/components/schemas/Page", "Recursive reference");
        Check(doc["components"]!["schemas"]!["Page"]!["properties"]!["counts"]!["additionalProperties"]!["items"]!["minimum"]!.GetValue<int>() == 0, "Nested map/list type");
        Check(doc["components"]!["schemas"]!["Page"]!["properties"]!["status"]!["$ref"]!.GetValue<string>() == "#/components/schemas/Page_status", "Field enum");
        JsonNode post = doc["paths"]!["/{id}/feed"]!["post"]!;
        JsonNode body = post["requestBody"]!["content"]!["application/x-www-form-urlencoded"]!;
        Check(body["schema"]!["required"]![0]!.GetValue<string>() == "message", "Required body property");
        Check(body["schema"]!["properties"]!["attached_media"]!["type"]!.GetValue<string>() == "string", "JSON form string");
        Check(body["schema"]!["properties"]!["attached_media"]!["contentMediaType"]!.GetValue<string>() == "application/json", "JSON content media type");
        Check(body["schema"]!["properties"]!["attached_media"]!["contentSchema"]!["type"]!.GetValue<string>() == "array", "Original form schema retained");
        Check(doc["paths"]!["/{id}/photos"]!["post"]!["requestBody"]!["content"]!["multipart/form-data"] is not null, "Multipart upload");
        Check(doc["paths"]!["/{id}/feed"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["data"]!["items"]!["$ref"]!.GetValue<string>() == "#/components/schemas/Post", "Paginated response");
        Validate(result);
    }

    [Test]
    public void MergesSharedPathsWithoutDroppingNodeVariants()
    {
        Dictionary<string, string> specs = Specs();
        specs["User"] = """
        {"fields":[],"apis":[{"method":"GET","endpoint":"feed","return":"User","params":[{"name":"owner","type":"string","required":true}]},
          {"method":"POST","endpoint":"feed","return":"User","params":[{"name":"link","type":"string","required":true}]}]}
        """;
        MetaOpenApiConversionResult result = _converter.Convert(specs, Options);
        JsonNode feed = result.Document["paths"]!["/{id}/feed"]!;
        Check(((JsonArray)feed["get"]!["tags"]!).Count == 2, "Both node tags retained");
        JsonNode owner = ((JsonArray)feed["get"]!["parameters"]!).First(x => x!["name"]!.GetValue<string>() == "owner")!;
        Check(!owner["required"]!.GetValue<bool>(), "Node-specific query requirement must not apply globally");
        Check(((JsonArray)feed["post"]!["requestBody"]!["content"]!["application/x-www-form-urlencoded"]!["schema"]!["anyOf"]!).Count == 2, "Both body alternatives retained");
        Check(!result.Document["paths"]!.AsObject().ContainsKey("/{User-id}/feed"), "No equivalent templated paths");
        Validate(result);
    }

    [Test]
    public void SupportsNodeSelectionAndResponseOverridesWithoutMutatingInput()
    {
        var replacement = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } } };
        MetaOpenApiConversionResult result = _converter.Convert(Specs(), new MetaOpenApiConverterOptions
        {
            GraphApiVersion = "v25.0", NodeTypes = ["Page"],
            ResponseSchemaOverrides = new Dictionary<string, JsonObject> { ["Page POST feed"] = replacement }
        });
        JsonNode response = result.Document["paths"]!["/{id}/feed"]!["post"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        Check(JsonNode.DeepEquals(replacement, response), "Override applied");
        response["description"] = "Changed";
        Check(!replacement.ContainsKey("description"), "Override cloned");
        Check(result.Document["components"]!["schemas"]!["Post"] is not null, "Referenced models retained");
        Validate(result);
    }

    [Test]
    public void PreservesOpenApi31ResponseSchemaKeywords()
    {
        var schema = JsonNode.Parse("""
        {"type":"object","properties":{
          "message":{"type":["string","null"]},
          "score":{"type":"number","exclusiveMinimum":0},
          "kind":{"const":"post"}
        },"unevaluatedProperties":false}
        """)!.AsObject();
        MetaOpenApiConversionResult result = _converter.Convert(Specs(), new MetaOpenApiConverterOptions
        {
            GraphApiVersion = "v25.0",
            ResponseSchemaOverrides = new Dictionary<string, JsonObject> { ["Page POST feed"] = schema }
        });
        JsonNode response = result.Document["paths"]!["/{id}/feed"]!["post"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        Check(JsonNode.DeepEquals(schema, response), "JSON Schema 2020-12 keywords preserved");
        Validate(result);
        var parsed = OpenApiDocument.Parse(result.ToJson(), "json");
        var parsedSchema = parsed.Document!.Paths["/{id}/feed"].Operations![System.Net.Http.HttpMethod.Post].Responses!["200"].Content!["application/json"].Schema!;
        Check(parsedSchema.Properties!["message"].Type == (JsonSchemaType.String | JsonSchemaType.Null), "Reader recognizes nullable type union");
    }

    [Test]
    public void PreservesFixedBasePathsAndDeleteQueryParameters()
    {
        var specs = new Dictionary<string, string> { ["Special"] = """
        {"fields":[],"apis":[{"method":"GET","basePath":"integrity/appeals/ads","endpoint":"status","return":"Special","params":[]},
          {"method":"DELETE","params":[{"name":"ids","type":"list<string>","required":true}]}]}
        """ };
        MetaOpenApiConversionResult result = _converter.Convert(specs, Options);
        Check(result.Document["paths"]!["/integrity/appeals/ads/status"] is not null, "Fixed path");
        JsonNode parameter = ((JsonArray)result.Document["paths"]!["/{id}"]!["delete"]!["parameters"]!).First(x => x!["name"]!.GetValue<string>() == "ids")!;
        Check(parameter["content"]!["application/json"]!["schema"]!["type"]!.GetValue<string>() == "array", "JSON query array");
        Validate(result);
    }

    [Test]
    public void ReportsUnknownTypesOrThrowsInStrictMode()
    {
        var specs = new Dictionary<string, string> { ["Page"] = """{"fields":[{"name":"missing","type":"MissingType"}]}""" };
        MetaOpenApiConversionResult result = _converter.Convert(specs, Options);
        Check(result.Diagnostics.Any(x => x.Contains("MissingType", StringComparison.Ordinal)), "Unknown type diagnostic");
        Check(result.Document["components"]!["schemas"]!["Page"]!["properties"]!["missing"]!["$ref"] is null, "No dangling reference");
        Expect<FormatException>(() => _converter.Convert(specs, new MetaOpenApiConverterOptions { GraphApiVersion = "v25.0", ThrowOnUnknownTypes = true }));
        Validate(result);
    }

    [Test]
    public void RejectsMalformedInputAndUnknownSelections()
    {
        Expect<FormatException>(() => _converter.Convert(new Dictionary<string, string> { ["Page"] = "{" }, Options));
        Expect<FormatException>(() => _converter.Convert(new Dictionary<string, string> { ["Page"] = """{"fields":[{"name":"bad","type":"list<map<string>>"}]}""".Replace("list<map<string>>", "list<map<string>") }, Options));
        Expect<ArgumentException>(() => _converter.Convert(Specs(), new MetaOpenApiConverterOptions { GraphApiVersion = "25" }));
        Expect<ArgumentException>(() => _converter.Convert(Specs(), new MetaOpenApiConverterOptions { GraphApiVersion = "v25.0", NodeTypes = ["Missing"] }));
    }

    [Test]
    public void OutputIsDeterministicAcrossInputOrderAndConcurrentCalls()
    {
        Dictionary<string, string> specs = Specs();
        string first = _converter.Convert(specs, Options).ToJson();
        string second = _converter.Convert(specs.Reverse().ToDictionary(x => x.Key, x => x.Value), Options).ToJson();
        Check(first == second, "Input order must not change output");
        Parallel.For(0, 4, _ => Check(_converter.Convert(specs, Options).ToJson() == first, "Converter must be safe to register as singleton"));
    }

    [Test]
    public async ValueTask ReadsDirectoryWritesJsonAndHonorsCancellation()
    {
        string root = Path.Combine(Path.GetTempPath(), "meta-converter-test-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "specs");
        Directory.CreateDirectory(input);
        try
        {
            foreach ((string key, string value) in Specs()) await File.WriteAllTextAsync(Path.Combine(input, Path.GetFileNameWithoutExtension(key) + ".json"), value);
            string output = Path.Combine(root, "output", "openapi.json");
            MetaOpenApiConversionResult result = await _converter.ConvertToFileAsync(input, output, Options);
            Check(await File.ReadAllTextAsync(output) == result.ToJson(), "Output JSON round trip");
            Validate(result);
            try { await _converter.ConvertDirectoryAsync(input, Options, new CancellationToken(true)); throw new InvalidOperationException("Cancellation not honored"); }
            catch (OperationCanceledException) { }
            try { await _converter.ConvertToFileAsync(input, Path.Combine(input, "Page.json"), Options); throw new InvalidOperationException("Unsafe overwrite allowed"); }
            catch (ArgumentException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Validate(MetaOpenApiConversionResult result)
    {
        Check(result.Document["openapi"]!.GetValue<string>() == "3.1.0", "OpenAPI version");
        Check(result.Document["jsonSchemaDialect"]!.GetValue<string>() == "https://spec.openapis.org/oas/3.1/dialect/base", "JSON Schema dialect");
        var parsed = OpenApiDocument.Parse(result.ToJson(), "json");
        Check(parsed.Document is not null, "OpenAPI document parsed");
        var diagnostic = parsed.Diagnostic ?? throw new InvalidOperationException("OpenAPI reader did not return diagnostics.");
        Check(diagnostic.Errors.Count == 0, "OpenAPI validation: " + string.Join("; ", diagnostic.Errors));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
