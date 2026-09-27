using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Soenneker.OpenApi.Converters.Meta.Internal;

internal sealed partial class Conversion
{
    private void AddOperation(string nodeName, JsonObject api)
    {
        string method = RequiredString(api, "method", nodeName).ToLowerInvariant();
        if (method is not ("get" or "post" or "put" or "patch" or "delete" or "head" or "options"))
            throw new FormatException($"Unsupported HTTP method '{method}' in '{nodeName}'.");
        string endpoint = Text(api, "endpoint") ?? "";
        string? basePath = Text(api, "basePath");
        string path = "/" + (basePath is null ? "{id}" : basePath.Trim('/'));
        if (endpoint.Length > 0) path += "/" + endpoint.Trim('/');
        if (path.Contains('?') || path.Contains('#')) throw new FormatException($"Invalid endpoint path '{path}'.");
        string key = $"{nodeName} {method.ToUpperInvariant()} {endpoint}".TrimEnd();
        var parameters = new JsonArray();
        foreach (Match match in Regex.Matches(path, @"\{([^{}]+)\}"))
            if (!parameters.Any(x => Text((JsonObject)x!, "name") == match.Groups[1].Value))
                parameters.Add(new JsonObject { ["name"] = match.Groups[1].Value, ["in"] = "path", ["required"] = true,
                    ["schema"] = new JsonObject { ["type"] = "string" } });

        bool bodyMethod = method is "post" or "put" or "patch";
        var bodyProperties = new JsonObject();
        var required = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool file = false;
        foreach (JsonNode? paramNode in Array(api, "params"))
        {
            JsonObject param = Object(paramNode, key);
            string name = RequiredString(param, "name", key);
            if (!seen.Add(name)) throw new FormatException($"Duplicate parameter '{name}' in '{key}'.");
            string type = RequiredString(param, "type", key);
            JsonObject schema = Type(type);
            bool isRequired = param["required"]?.GetValue<bool>() ?? false;
            if (bodyMethod)
            {
                bodyProperties[name] = schema;
                if (isRequired) required.Add(name);
                file |= type == "file";
            }
            else
            {
                var parameter = new JsonObject { ["name"] = name, ["in"] = "query", ["required"] = isRequired };
                // Meta accepts complex values as JSON in a single query value, not exploded objects.
                if (IsComplex(schema)) parameter["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = schema } };
                else parameter["schema"] = schema;
                parameters.Add(parameter);
            }
        }
        if (method == "get")
        {
            AddQuery(parameters, "fields", new JsonObject { ["type"] = "string" });
            if (endpoint.Length > 0 && basePath is null)
            {
                AddQuery(parameters, "limit", new JsonObject { ["type"] = "integer", ["minimum"] = 1 });
                AddQuery(parameters, "after", new JsonObject { ["type"] = "string" });
                AddQuery(parameters, "before", new JsonObject { ["type"] = "string" });
            }
        }

        string returnType = Text(api, "return") ?? "Object";
        JsonObject response;
        if (options.ResponseSchemaOverrides.TryGetValue(key, out JsonObject? responseOverride)) response = (JsonObject)responseOverride.DeepClone();
        else if (method == "get")
        {
            response = Type(returnType);
            if (endpoint.Length > 0 && basePath is null)
            {
                response = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
                {
                    ["data"] = new JsonObject { ["type"] = "array", ["items"] = response },
                    ["paging"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                    ["summary"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
                } };
                _diagnostics.Add("GET edge responses are inferred as data arrays with optional paging and summary; use ResponseSchemaOverrides for exceptions.");
            }
        }
        else
        {
            response = new JsonObject { ["type"] = "object", ["additionalProperties"] = true, ["x-meta-return-type"] = returnType };
            _diagnostics.Add("Mutation responses are open objects: Meta SDK return types do not define HTTP response payloads. Use ResponseSchemaOverrides for precise responses.");
        }

        var operation = new JsonObject
        {
            ["operationId"] = OperationId(method, path),
            ["tags"] = new JsonArray(nodeName), ["parameters"] = parameters,
            ["x-meta-operations"] = new JsonArray(new JsonObject { ["node"] = nodeName, ["name"] = Text(api, "name"),
                ["returnType"] = returnType, ["parameters"] = Array(api, "params").DeepClone() }),
            ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject { ["description"] = "Successful response", ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = response } } },
                ["default"] = new JsonObject { ["description"] = "Graph API error", ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = new JsonObject
                { ["type"] = "object", ["properties"] = new JsonObject { ["error"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true } } } } } }
            }
        };
        if (bodyMethod)
        {
            var bodySchema = new JsonObject { ["type"] = "object", ["properties"] = bodyProperties };
            if (required.Count > 0) bodySchema["required"] = required;
            var encoding = new JsonObject();
            foreach ((string name, JsonNode? schema) in bodyProperties.ToArray())
            {
                if (!IsComplex((JsonObject)schema!)) continue;
                if (file) encoding[name] = new JsonObject { ["contentType"] = "application/json" };
                else
                {
                    // OpenAPI ignores contentType for URL-encoded bodies. Model the actual JSON string on the wire.
                    bodyProperties[name] = new JsonObject { ["type"] = "string", ["description"] = "A JSON-encoded value.",
                        ["contentMediaType"] = "application/json", ["contentSchema"] = schema!.DeepClone() };
                }
            }
            var media = new JsonObject { ["schema"] = bodySchema };
            if (encoding.Count > 0) media["encoding"] = encoding;
            operation["requestBody"] = new JsonObject { ["required"] = required.Count > 0, ["content"] = new JsonObject
                { [file ? "multipart/form-data" : "application/x-www-form-urlencoded"] = media } };
        }

        if (_paths[path] is not JsonObject pathItem) _paths[path] = pathItem = new JsonObject();
        if (pathItem[method] is JsonObject existing)
        {
            MergeOperation(existing, operation);
            _diagnostics.Add($"Merged node-specific operations for {method.ToUpperInvariant()} {path}; requirements may vary by node type.");
        }
        else pathItem[method] = operation;
    }

    private string OperationId(string method, string path)
    {
        string name = method + "_" + Regex.Replace(path, @"[^a-zA-Z0-9]+", "_").Trim('_');
        string identity = method + " " + path;
        if (_operationIds.TryGetValue(name, out string? existing) && existing != identity)
            name += "_" + System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        _operationIds[name] = identity;
        return name;
    }

    private static void MergeOperation(JsonObject target, JsonObject source)
    {
        foreach (string property in new[] { "tags", "x-meta-operations" })
            foreach (JsonNode? item in (JsonArray)source[property]!)
                if (!((JsonArray)target[property]!).Any(x => JsonNode.DeepEquals(x, item))) ((JsonArray)target[property]!).Add(item!.DeepClone());
        var targetParams = (JsonArray)target["parameters"]!;
        var sourceParams = (JsonArray)source["parameters"]!;
        foreach (JsonObject parameter in targetParams.Cast<JsonObject>())
            if (Text(parameter, "in") == "query" && !sourceParams.Any(x => Text((JsonObject)x!, "name") == Text(parameter, "name") && Text((JsonObject)x!, "in") == "query"))
                parameter["required"] = false;
        foreach (JsonObject parameter in sourceParams.Cast<JsonObject>())
        {
            JsonObject? existing = targetParams.Cast<JsonObject>().FirstOrDefault(x => Text(x, "name") == Text(parameter, "name") && Text(x, "in") == Text(parameter, "in"));
            if (existing is null)
            {
                var copy = (JsonObject)parameter.DeepClone();
                if (Text(copy, "in") == "query") copy["required"] = false;
                targetParams.Add(copy);
            }
            else
            {
                existing["required"] = (existing["required"]?.GetValue<bool>() ?? false) && (parameter["required"]?.GetValue<bool>() ?? false);
                JsonObject union = Union(ParameterSchema(existing), ParameterSchema(parameter));
                if (existing.ContainsKey("content") || parameter.ContainsKey("content"))
                {
                    existing.Remove("schema");
                    existing["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = union } };
                }
                else existing["schema"] = union;
            }
        }
        JsonNode targetMedia = target["responses"]!["200"]!["content"]!["application/json"]!;
        targetMedia["schema"] = Union((JsonObject)targetMedia["schema"]!, (JsonObject)source["responses"]!["200"]!["content"]!["application/json"]!["schema"]!);
        if (target["requestBody"] is JsonObject targetBody && source["requestBody"] is JsonObject sourceBody)
        {
            targetBody["required"] = targetBody["required"]!.GetValue<bool>() && sourceBody["required"]!.GetValue<bool>();
            foreach ((string contentType, JsonNode? media) in (JsonObject)sourceBody["content"]!)
            {
                if (targetBody["content"]![contentType] is JsonObject existingMedia)
                {
                    existingMedia["schema"] = Union((JsonObject)existingMedia["schema"]!, (JsonObject)media!["schema"]!);
                    if (media!["encoding"] is JsonObject sourceEncoding)
                    {
                        if (existingMedia["encoding"] is not JsonObject) existingMedia["encoding"] = new JsonObject();
                        foreach ((string name, JsonNode? encoding) in sourceEncoding) existingMedia["encoding"]![name] = encoding!.DeepClone();
                    }
                }
                else targetBody["content"]![contentType] = media!.DeepClone();
            }
        }
    }

    private static JsonObject ParameterSchema(JsonObject parameter) => (JsonObject)(parameter["schema"] ?? parameter["content"]!["application/json"]!["schema"]!);

    private static JsonObject Union(JsonObject a, JsonObject b)
    {
        if (JsonNode.DeepEquals(a, b)) return (JsonObject)a.DeepClone();
        JsonArray values = a["anyOf"] is JsonArray alternatives ? (JsonArray)alternatives.DeepClone() : new JsonArray(a.DeepClone());
        if (!values.Any(x => JsonNode.DeepEquals(x, b))) values.Add(b.DeepClone());
        return new JsonObject { ["anyOf"] = values };
    }

    private static void AddQuery(JsonArray parameters, string name, JsonObject schema)
    {
        if (!parameters.Any(x => Text((JsonObject)x!, "name") == name && Text((JsonObject)x!, "in") == "query"))
            parameters.Add(new JsonObject { ["name"] = name, ["in"] = "query", ["schema"] = schema });
    }
}
