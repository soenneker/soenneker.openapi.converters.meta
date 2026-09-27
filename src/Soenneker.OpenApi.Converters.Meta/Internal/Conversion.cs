using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.OpenApi.Converters.Meta.Internal;

internal sealed partial class Conversion(MetaOpenApiConverterOptions options, CancellationToken cancellationToken)
{
    private readonly SortedDictionary<string, JsonObject> _nodes = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, JsonObject> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fieldEnums = new(StringComparer.Ordinal);
    private readonly JsonObject _schemas = new();
    private readonly JsonObject _paths = new();
    private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _operationIds = new(StringComparer.Ordinal);

    internal MetaOpenApiConversionResult Run(IReadOnlyDictionary<string, string> specifications)
    {
        foreach ((string key, string json) in specifications.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonNode root;
            try { root = JsonNode.Parse(json) ?? throw new JsonException("Null specification."); }
            catch (JsonException e) { throw new FormatException($"Invalid Meta JSON in '{key}'.", e); }
            if (root is JsonArray enums)
            {
                foreach (JsonNode? item in enums)
                {
                    JsonObject definition = Object(item, key);
                    string name = RequiredString(definition, "name", key);
                    ValidateName(name);
                    if (definition["values"] is not JsonArray) throw new FormatException($"Enum '{name}' must have a values array.");
                    if (!_enums.TryAdd(name, definition)) throw new FormatException($"Duplicate enum '{name}'.");
                    if (definition["node"] is JsonValue && definition["field_or_param"] is JsonValue)
                        _fieldEnums.TryAdd($"{Text(definition, "node")}.{Text(definition, "field_or_param")}", name);
                }
            }
            else
            {
                JsonObject node = Object(root, key);
                string name = Path.GetFileName(key);
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
                ValidateName(name);
                if (node["fields"] is not JsonArray && node["apis"] is not JsonArray)
                    throw new FormatException($"'{key}' is not a Meta node specification. Supply the specs directory, not api_specs.");
                if (!_nodes.TryAdd(name, node)) throw new FormatException($"Duplicate node '{name}'.");
            }
        }

        foreach ((string name, JsonObject definition) in _enums)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_nodes.ContainsKey(name)) throw new FormatException($"Schema name collision: '{name}'.");
            var distinct = new JsonArray();
            foreach (JsonNode? value in (JsonArray)definition["values"]!)
            {
                if (value is not JsonValue) throw new FormatException($"Enum '{name}' must contain scalar values.");
                if (!distinct.Any(x => JsonNode.DeepEquals(x, value))) distinct.Add(value.DeepClone());
            }
            var schema = new JsonObject();
            if (distinct.Count > 0)
            {
                schema["enum"] = distinct;
                if (distinct.All(x => x!.GetValueKind() == JsonValueKind.String)) schema["type"] = "string";
                else if (distinct.All(x => x!.GetValueKind() == JsonValueKind.Number)) schema["type"] = "number";
                else if (distinct.All(x => x!.GetValueKind() is JsonValueKind.True or JsonValueKind.False)) schema["type"] = "boolean";
            }
            _schemas[name] = schema;
        }

        foreach ((string name, JsonObject node) in _nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = new JsonObject();
            foreach (JsonNode? fieldNode in Array(node, "fields"))
            {
                JsonObject field = Object(fieldNode, name);
                string fieldName = RequiredString(field, "name", name);
                if (properties.ContainsKey(fieldName)) throw new FormatException($"Duplicate field '{name}.{fieldName}'.");
                string type = RequiredString(field, "type", $"{name}.{fieldName}");
                properties[fieldName] = _fieldEnums.TryGetValue($"{name}.{fieldName}", out string? enumName) && type == "string"
                    ? Reference(enumName) : Type(type);
            }
            _schemas[name] = new JsonObject { ["type"] = "object", ["properties"] = properties };
        }

        HashSet<string>? selected = options.NodeTypes is null ? null : new(options.NodeTypes, StringComparer.Ordinal);
        if (selected is not null)
            foreach (string name in selected)
                if (!_nodes.ContainsKey(name)) throw new ArgumentException($"Requested node '{name}' was not supplied.");
        foreach ((string name, JsonObject node) in _nodes)
        {
            if (selected is not null && !selected.Contains(name)) continue;
            foreach (JsonNode? api in Array(node, "apis"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddOperation(name, Object(api, name));
            }
        }

        var document = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["jsonSchemaDialect"] = "https://spec.openapis.org/oas/3.1/dialect/base",
            ["info"] = new JsonObject { ["title"] = options.Title, ["version"] = options.GraphApiVersion },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = $"{options.ServerUrl.TrimEnd('/')}/{options.GraphApiVersion}" }),
            ["paths"] = _paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = _schemas,
                ["securitySchemes"] = new JsonObject { ["metaAccessToken"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" } }
            },
            ["security"] = new JsonArray(new JsonObject { ["metaAccessToken"] = new JsonArray() })
        };
        _diagnostics.Add("Scopes, permissions and SDKCodegen.json patches are not supplied by the node specifications; review endpoint requirements.");
        return new MetaOpenApiConversionResult(document, System.Array.AsReadOnly(_diagnostics.Order(StringComparer.Ordinal).ToArray()));
    }

    private JsonObject Type(string source, int depth = 0)
    {
        if (depth > 32) throw new FormatException($"Type nesting exceeds 32 levels: '{source}'.");
        string type = source.Trim();
        if (_nodes.ContainsKey(type) || _enums.ContainsKey(type)) return Reference(type);
        switch (type)
        {
            case "string": return new JsonObject { ["type"] = "string" };
            case "bool": case "boolean": return new JsonObject { ["type"] = "boolean" };
            case "int": case "integer": case "long": case "int64": return new JsonObject { ["type"] = "integer", ["format"] = "int64" };
            case "unsigned int": case "unsigned long": return new JsonObject { ["type"] = "integer", ["format"] = "int64", ["minimum"] = 0 };
            case "float": case "double": case "number": return new JsonObject { ["type"] = "number", ["format"] = type == "float" ? "float" : "double" };
            case "datetime": return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
            case "file": return new JsonObject { ["type"] = "string", ["format"] = "binary" };
            case "Object": case "object": case "map": return new JsonObject { ["type"] = "object", ["additionalProperties"] = true };
            case "mixed": return new JsonObject();
        }
        int opening = type.IndexOf('<');
        if (opening >= 0)
        {
            if (!type.EndsWith('>')) throw new FormatException($"Malformed type '{source}'.");
            List<string> args = SplitTypes(type[(opening + 1)..^1]);
            string container = type[..opening].Trim();
            if (container == "list" && args.Count == 1) return new JsonObject { ["type"] = "array", ["items"] = Type(args[0], depth + 1) };
            if (container == "map" && args.Count is 1 or 2)
            {
                if (args.Count == 2 && args[0] != "string") _diagnostics.Add($"Map key type '{args[0]}' is represented as JSON string keys.");
                return new JsonObject { ["type"] = "object", ["additionalProperties"] = Type(args[^1], depth + 1) };
            }
            throw new FormatException($"Unsupported generic type '{source}'.");
        }
        if (options.ThrowOnUnknownTypes) throw new FormatException($"Unresolved Meta type '{type}'.");
        _diagnostics.Add($"Unresolved Meta type '{type}' uses an unconstrained schema.");
        return new JsonObject { ["x-meta-type"] = type };
    }

    private bool IsComplex(JsonObject schema)
    {
        string? reference = Text(schema, "$ref");
        if (reference is not null) return !_enums.ContainsKey(reference["#/components/schemas/".Length..]);
        return Text(schema, "type") is "object" or "array" || schema.ContainsKey("x-meta-type");
    }

    private static List<string> SplitTypes(string input)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '<') depth++;
            else if (input[i] == '>') depth--;
            else if (input[i] == ',' && depth == 0) { result.Add(input[start..i].Trim()); start = i + 1; }
            if (depth < 0) throw new FormatException($"Malformed generic type '{input}'.");
        }
        result.Add(input[start..].Trim());
        if (depth != 0 || result.Any(string.IsNullOrEmpty)) throw new FormatException($"Malformed generic type '{input}'.");
        return result;
    }

    private static JsonObject Reference(string name) => new() { ["$ref"] = "#/components/schemas/" + name };
    private static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, @"^[a-zA-Z0-9._-]+$")) throw new FormatException($"Invalid schema name '{name}'.");
    }
    private static JsonObject Object(JsonNode? node, string context) => node as JsonObject ?? throw new FormatException($"Expected an object in '{context}'.");
    private static JsonArray Array(JsonObject node, string name) => node[name] is null ? new JsonArray() : node[name] as JsonArray ?? throw new FormatException($"'{name}' must be an array.");
    private static string? Text(JsonObject node, string name) => node[name]?.GetValue<string>();
    private static string RequiredString(JsonObject node, string name, string context) =>
        !string.IsNullOrWhiteSpace(Text(node, name)) ? Text(node, name)! : throw new FormatException($"Missing '{name}' in '{context}'.");
}
