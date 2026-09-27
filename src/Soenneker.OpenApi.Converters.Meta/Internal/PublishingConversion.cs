using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.OpenApi.Converters.Meta.Internal;

internal static class PublishingConversion
{
    internal static MetaOpenApiConversionResult Run(IReadOnlyDictionary<string, string> input, MetaOpenApiConverterOptions options, CancellationToken token)
    {
        bool facebook = options.Profile == MetaOpenApiProfile.FacebookPublishing;
        string[] nodes = facebook ? FacebookPublishingProfile.Nodes : InstagramPublishingProfile.Nodes;
        HashSet<string> operations = facebook ? FacebookPublishingProfile.Operations : InstagramPublishingProfile.Operations;
        var specifications = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string json) in input)
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(key);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
            JsonNode root;
            try { root = JsonNode.Parse(json) ?? throw new JsonException("Null specification."); }
            catch (JsonException exception) { throw new FormatException($"Invalid Meta JSON in '{key}'.", exception); }
            if (root is JsonObject obj && obj["apis"] is JsonArray apis)
                for (int i = apis.Count - 1; i >= 0; i--)
                    if (!operations.Contains(Operation(name, apis[i]!))) apis.RemoveAt(i);
            if (!specifications.TryAdd(name, root.ToJsonString())) throw new FormatException($"Duplicate specification '{name}'.");
        }
        foreach (string name in nodes)
            if (name != "IGContainer" && !specifications.ContainsKey(name))
                throw new ArgumentException($"Publishing profile requires the '{name}' specification.", nameof(input));
        if (facebook) FacebookPublishingProfile.AdjustSpecifications(specifications);
        else InstagramPublishingProfile.AdjustSpecifications(specifications);

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, string json) in specifications)
        {
            token.ThrowIfCancellationRequested();
            if (JsonNode.Parse(json) is not JsonObject obj || obj["apis"] is not JsonArray apis) continue;
            foreach (JsonNode? api in apis) available.Add(Operation(name, api!));
        }
        string[] missing = operations.Except(available).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Meta specifications are missing required publishing operations: {string.Join(", ", missing)}");

        var responses = new Dictionary<string, JsonObject>(facebook ? FacebookPublishingProfile.Responses : InstagramPublishingProfile.Responses, StringComparer.Ordinal);
        foreach (var response in options.ResponseSchemaOverrides) responses[response.Key] = response.Value;
        var effective = new MetaOpenApiConverterOptions
        {
            GraphApiVersion = options.GraphApiVersion,
            Title = options.Title,
            ServerUrl = options.ServerUrl,
            NodeTypes = options.NodeTypes ?? nodes,
            ThrowOnUnknownTypes = options.ThrowOnUnknownTypes,
            ResponseSchemaOverrides = responses
        };
        MetaOpenApiConversionResult result = new Conversion(effective, token).Run(specifications);
        // A root {id} parameter cannot be named by Kiota; retain a descriptive indexer name.
        var paths = result.Document["paths"]!.AsObject();
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
        PruneSchemas(result.Document);
        result.Document["x-meta-source"] = new JsonObject
        {
            ["repository"] = "https://github.com/facebook/facebook-business-sdk-codegen",
            ["revision"] = options.SourceRevision ?? "local",
            ["profile"] = facebook ? "Facebook publishing" : "Instagram publishing"
        };
        return result;
    }

    private static string Operation(string name, JsonNode api) => $"{name} {api["method"]!.GetValue<string>()} {api["endpoint"]?.GetValue<string>()}".TrimEnd();

    private static void PruneSchemas(JsonObject document)
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
