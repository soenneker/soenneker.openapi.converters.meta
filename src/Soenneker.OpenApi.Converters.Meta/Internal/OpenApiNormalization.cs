using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Soenneker.OpenApi.Converters.Meta.Internal;

internal static class OpenApiNormalization
{
    internal static void NormalizeForKiota(JsonObject document)
    {
        // A root {id} parameter cannot be named by Kiota; retain a descriptive indexer name.
        var paths = document["paths"]!.AsObject();
        foreach ((string path, JsonNode? item) in paths.ToArray())
        {
            if (!path.Contains("{id}", StringComparison.Ordinal)) continue;
            paths.Remove(path);
            paths[path.Replace("{id}", "{node-id}", StringComparison.Ordinal)] = item;
            foreach (JsonObject operation in item!.AsObject().Select(x => x.Value).OfType<JsonObject>())
                if (operation["parameters"] is JsonArray parameters)
                    foreach (JsonObject parameter in parameters.Cast<JsonObject>())
                        if (parameter["in"]?.GetValue<string>() == "path" && parameter["name"]?.GetValue<string>() == "id")
                            parameter["name"] = "node-id";
        }
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
    internal static void PruneSchemas(JsonObject document)
    {
        var schemas = document["components"]!["schemas"]!.AsObject();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out string? reference) &&
                    reference.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                {
                    string name = reference["#/components/schemas/".Length..];
                    if (keep.Add(name)) pending.Enqueue(name);
                }
                foreach (var property in obj) Visit(property.Value);
            }
            else if (node is JsonArray array)
                foreach (JsonNode? item in array) Visit(item);
        }
        Visit(document["paths"]);
        while (pending.TryDequeue(out string? name))
        {
            if (!schemas.ContainsKey(name)) throw new InvalidOperationException($"Unresolved schema reference: {name}");
            Visit(schemas[name]);
        }
        foreach (string name in schemas.Select(x => x.Key).ToArray())
            if (!keep.Contains(name)) schemas.Remove(name);
    }

}
