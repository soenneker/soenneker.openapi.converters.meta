using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.OpenApi.Converters.Meta.Internal;

internal static class FullApiConversion
{
    internal static MetaOpenApiConversionResult Run(IReadOnlyDictionary<string, string> input, MetaOpenApiConverterOptions options, CancellationToken token)
    {
        var specifications = new SortedDictionary<string, string>(StringComparer.Ordinal);
        int sourceOperations = 0;
        var included = new JsonArray();
        foreach ((string key, string json) in input.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(key);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
            JsonNode root = JsonNode.Parse(json)!;
            if (root is JsonObject obj && obj["apis"] is JsonArray apis)
            {
                sourceOperations += apis.Count;
                for (int i = apis.Count - 1; i >= 0; i--)
                {
                    JsonObject api = apis[i]!.AsObject();
                    if (options.Profile == MetaOpenApiProfile.Instagram && !IsInstagramNode(name) && !IsInstagramOperation(name, api)) apis.RemoveAt(i);
                }
                foreach (JsonObject api in apis.OfType<JsonObject>())
                {
                    // SDK root return aliases can name missing classes; the node defines the complete root fields.
                    if (api["method"]?.GetValue<string>() == "GET" && string.IsNullOrEmpty(api["endpoint"]?.GetValue<string>()) && api["basePath"] is null)
                        api["return"] = name;
                    if (api["params"] is JsonArray parameters)
                        foreach (JsonObject parameter in parameters.OfType<JsonObject>())
                        {
                            if (parameter["name"]?.GetValue<string>() == "creation_id") parameter["type"] = "string";
                            if (name == "Page" && parameter["name"]?.GetValue<string>() == "scheduled_publish_time") parameter["type"] = "unsigned int";
                        }
                }
                foreach (JsonNode? api in apis)
                    included.Add(new JsonObject { ["node"] = name, ["method"] = api!["method"]!.DeepClone(), ["endpoint"] = api["endpoint"]?.DeepClone() });
            }
            if (!specifications.TryAdd(name, root.ToJsonString())) throw new FormatException($"Duplicate specification '{name}'.");
        }
        int supplementalOperations = specifications.ContainsKey("IGContainer") ? 0 : 1;
        // Container polling is documented but absent from the SDK node corpus.
        if (!specifications.ContainsKey("IGContainer"))
            specifications["IGContainer"] = """{"fields":[{"name":"id","type":"string"},{"name":"status_code","type":"string"},{"name":"status","type":"string"}],"apis":[{"method":"GET","return":"IGContainer","params":[]}]}""";
        var effective = new MetaOpenApiConverterOptions
        {
            GraphApiVersion = options.GraphApiVersion, Title = options.Title, ServerUrl = options.ServerUrl,
            ThrowOnUnknownTypes = options.ThrowOnUnknownTypes, ResponseSchemaOverrides = options.ResponseSchemaOverrides
        };
        if (options.NodeTypes is not null) throw new ArgumentException("Full API profiles cannot restrict NodeTypes; use the default profile for custom subsets.", nameof(options));
        MetaOpenApiConversionResult result = new Conversion(effective, token).Run(specifications);
        var paths = result.Document["paths"]!.AsObject();
        foreach ((string path, JsonNode? item) in paths.ToArray())
        {
            if (!path.Contains("{id}", StringComparison.Ordinal)) continue;
            paths.Remove(path);
            paths[path.Replace("{id}", "{node-id}", StringComparison.Ordinal)] = item;
            foreach (JsonObject operation in item!.AsObject().Select(x => x.Value).OfType<JsonObject>())
                if (operation["parameters"] is JsonArray parameters)
                    foreach (JsonObject parameter in parameters.Cast<JsonObject>())
                        if (parameter["in"]?.GetValue<string>() == "path" && parameter["name"]?.GetValue<string>() == "id") parameter["name"] = "node-id";
        }
        int emittedOperations = paths.SelectMany(path => path.Value!.AsObject())
            .Sum(operation => operation.Value?["x-meta-operations"]?.AsArray().Count ?? 0);
        if (emittedOperations != included.Count + supplementalOperations)
            throw new InvalidOperationException($"Coverage mismatch: expected {included.Count + supplementalOperations} source operations, emitted {emittedOperations}.");
        NormalizeForKiota(result.Document);
        if (options.Profile == MetaOpenApiProfile.Instagram) PublishingConversion.PruneSchemas(result.Document);
        result.Document["x-meta-source"] = new JsonObject
        {
            ["repository"] = "https://github.com/facebook/facebook-business-sdk-codegen", ["revision"] = options.SourceRevision ?? "local",
            ["profile"] = options.Profile.ToString(), ["sourceOperationCount"] = sourceOperations,
            ["includedOperationCount"] = included.Count, ["supplementalOperationCount"] = supplementalOperations, ["operations"] = included
        };
        return result;
    }

    private static void NormalizeForKiota(JsonObject document)
    {
        var schemas = document["components"]!["schemas"]!.AsObject();
        foreach (JsonObject path in document["paths"]!.AsObject().Select(pair => pair.Value).OfType<JsonObject>())
        foreach (var entry in path)
        {
            if (entry.Value is not JsonObject operation) continue;
            if (operation["requestBody"]?["content"] is JsonObject content)
            {
                foreach (var media in content)
                    media.Value!["schema"] = MergeShape(media.Value!["schema"]!.AsObject(), schemas);
                if (content["application/x-www-form-urlencoded"]?["schema"] is JsonObject form && content["multipart/form-data"]?["schema"] is JsonObject multipart)
                {
                    // Keep the multipart alternative for files and expose every non-file field in the form alternative.
                    var fields = (JsonObject)multipart.DeepClone();
                    if (fields["properties"] is JsonObject properties)
                        foreach (var property in properties.ToArray())
                        {
                            if (property.Value?["format"]?.GetValue<string>() == "binary") { properties.Remove(property.Key); continue; }
                            if (property.Value?["type"]?.GetValue<string>() is "array" or "object" || property.Value?["$ref"] is not null)
                                properties[property.Key] = new JsonObject { ["type"] = "string", ["contentMediaType"] = "application/json", ["contentSchema"] = property.Value!.DeepClone() };
                        }
                    fields.Remove("required");
                    content["application/x-www-form-urlencoded"]!["schema"] = MergeShape(new JsonObject { ["anyOf"] = new JsonArray(form.DeepClone(), fields) }, schemas);
                }
            }
            if (operation["requestBody"]?["content"]?["application/x-www-form-urlencoded"]?["schema"]?["properties"] is JsonObject formProperties)
                foreach (var property in formProperties.ToArray())
                {
                    var schema = property.Value!.AsObject();
                    var resolved = schema;
                    if (schema["$ref"] is JsonValue reference && schemas[reference.GetValue<string>().Split('/')[^1]] is JsonObject definition) resolved = definition;
                    if (resolved["type"]?.GetValue<string>() is not ("string" or "integer" or "number" or "boolean"))
                        formProperties[property.Key] = new JsonObject { ["type"] = "string", ["contentMediaType"] = "application/json", ["contentSchema"] = schema.DeepClone() };
                }
            if (operation["responses"]?["200"]?["content"]?["application/json"] is JsonObject response)
            {
                var schema = MergeShape(response["schema"]!.AsObject(), schemas);
                if (entry.Key is "post" or "put" or "patch" or "delete")
                {
                    // Mutation payloads are not described by Meta's SDK return types.
                    // Common Graph result fields are optional; additional data remains available.
                    if (!schema.ContainsKey("$ref"))
                    {
                        schema["type"] = "object";
                        if (schema["properties"] is not JsonObject) schema["properties"] = new JsonObject();
                        var properties = schema["properties"]!.AsObject();
                        foreach (string name in new[] { "id", "post_id" })
                            if (!properties.ContainsKey(name)) properties[name] = new JsonObject { ["type"] = "string" };
                        if (!properties.ContainsKey("success")) properties["success"] = new JsonObject { ["type"] = "boolean" };
                        schema["additionalProperties"] = true;
                    }
                }
                response["schema"] = schema;
            }
            if (operation["parameters"] is JsonArray parameters)
                foreach (JsonObject parameter in parameters.OfType<JsonObject>())
                    if (parameter["schema"] is JsonObject schema && schema.ContainsKey("anyOf")) parameter["schema"] = MergeShape(schema, schemas);
        }
    }

    private static JsonObject MergeShape(JsonObject source, JsonObject schemas, int depth = 0)
    {
        if (source["anyOf"] is not JsonArray variants) return (JsonObject)source.DeepClone();
        var choices = variants.Cast<JsonObject>().Select(variant =>
        {
            var value = MergeShape(variant, schemas, depth + 1);
            if (value["$ref"] is JsonValue reference && schemas[reference.GetValue<string>().Split('/')[^1]] is JsonObject definition)
                return definition;
            return value;
        }).ToArray();
        var result = new JsonObject();
        if (choices.All(choice => choice["type"]?.GetValue<string>() == "object") && depth < 3)
        {
            result["type"] = "object";
            var properties = new JsonObject();
            foreach (var choice in choices)
                if (choice["properties"] is JsonObject fields)
                    foreach (var field in fields)
                    {
                        if (properties[field.Key] is JsonObject existing && !JsonNode.DeepEquals(existing, field.Value))
                            properties[field.Key] = MergeShape(new JsonObject { ["anyOf"] = new JsonArray(existing.DeepClone(), field.Value!.DeepClone()) }, schemas, depth + 1);
                        else if (properties[field.Key] is null) properties[field.Key] = field.Value!.DeepClone();
                    }
            result["properties"] = properties;
            result["additionalProperties"] = true;
            var required = choices.Select(choice => (choice["required"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? []).ToArray();
            if (required.Length > 0)
            {
                var common = new HashSet<string>(required[0], StringComparer.Ordinal);
                foreach (var set in required.Skip(1)) common.IntersectWith(set);
                if (common.Count > 0) result["required"] = new JsonArray(common.Order(StringComparer.Ordinal).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            }
        }
        else if (choices.All(choice => choice["type"]?.GetValue<string>() == "array") && depth < 3)
        {
            result["type"] = "array";
            result["items"] = MergeShape(new JsonObject { ["anyOf"] = new JsonArray(choices.Select(choice => choice["items"]!.DeepClone()).ToArray()) }, schemas, depth + 1);
        }
        else
        {
            string? type = choices[0]["type"]?.GetValue<string>();
            if (type is not null && choices.All(choice => choice["type"]?.GetValue<string>() == type)) result["type"] = type;
        }
        // Preserve every original alternative without emitting non-serializable Kiota union wrappers.
        if (depth == 0) result["x-meta-schema-variants"] = variants.DeepClone();
        return result;
    }
    internal static bool IsInstagramNode(string name) => name.Contains("Instagram", StringComparison.OrdinalIgnoreCase)
        || name.Contains("IG", StringComparison.Ordinal) || name is "UnifiedThread" or "UnifiedMessage";

    private static bool IsInstagramOperation(string name, JsonObject api)
    {
        string endpoint = api["endpoint"]?.GetValue<string>() ?? "";
        return endpoint.Contains("instagram", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("ig_", StringComparison.OrdinalIgnoreCase)
            || IsInstagramNode(api["return"]?.GetValue<string>() ?? "")
            || (api["params"]?.ToJsonString().Contains("instagram", StringComparison.OrdinalIgnoreCase) ?? false)
            || (name is "Page" or "User" && (endpoint.Length == 0 || endpoint is "accounts" or "conversations" or "messages" or "subscribed_apps" or "messenger_profile"));
    }
}