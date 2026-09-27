using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Soenneker.OpenApi.Converters.Meta.Internal;

internal static class FacebookPublishingProfile
{
    internal static readonly string[] Nodes = ["Page", "PagePost", "Photo", "User"];
    internal static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "Page GET",
        "Page GET feed",
        "Page POST feed",
        "Page GET photos",
        "Page POST photos",
        "Page GET posts",
        "Page GET published_posts",
        "Page GET scheduled_posts",
        "PagePost GET",
        "PagePost POST",
        "PagePost DELETE",
        "Photo GET",
        "Photo DELETE",
        "User GET accounts"
    };
    private static readonly Dictionary<string, string[]> Fields = new(StringComparer.Ordinal)
    {
        ["Page"] = ["id", "name", "access_token", "link"],
        ["PagePost"] = ["id", "message", "permalink_url", "created_time", "updated_time", "is_published", "picture", "full_picture", "status_type"],
        ["Photo"] = ["id", "name", "link", "created_time", "images", "width", "height", "picture"],
        ["User"] = ["id", "name"]
    };
    internal static readonly IReadOnlyDictionary<string, JsonObject> Responses = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
    {
        ["Page GET"] = new JsonObject { ["$ref"] = "#/components/schemas/PublishingNode" },
        ["PagePost GET"] = new JsonObject { ["$ref"] = "#/components/schemas/PublishingNode" },
        ["Photo GET"] = new JsonObject { ["$ref"] = "#/components/schemas/PublishingNode" },
        ["Page POST feed"] = Published(),
        ["Page POST photos"] = Published(),
        ["PagePost POST"] = Published(),
        ["PagePost DELETE"] = Published(),
        ["Photo DELETE"] = Published()
    };

    internal static void AdjustSpecifications(IDictionary<string, string> specifications)
    {
        foreach ((string name, string[] names) in Fields)
        {
            var node = JsonNode.Parse(specifications[name])!.AsObject();
            var fields = node["fields"]!.AsArray();
            for (int i = fields.Count - 1; i >= 0; i--)
                if (!names.Contains(fields[i]!["name"]!.GetValue<string>(), StringComparer.Ordinal)) fields.RemoveAt(i);
            specifications[name] = node.ToJsonString();
        }
        specifications["PublishingResult"] = """{"fields":[{"name":"id","type":"string"},{"name":"post_id","type":"string"},{"name":"success","type":"bool"}]}""";
        var rootFields = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in Nodes)
        {
            if (!specifications.TryGetValue(name, out string? json)) continue;
            foreach (JsonNode? field in JsonNode.Parse(json)!["fields"]!.AsArray())
                if (seen.Add(field!["name"]!.GetValue<string>())) rootFields.Add(field.DeepClone());
        }
        specifications["PublishingNode"] = new JsonObject { ["fields"] = rootFields }.ToJsonString();
        var page = JsonNode.Parse(specifications["Page"])!.AsObject();
        foreach (JsonObject api in page["apis"]!.AsArray().Cast<JsonObject>())
        {
            if (api["method"]!.GetValue<string>() != "POST") continue;
            string[] allowed = api["endpoint"]!.GetValue<string>() == "feed"
                ? ["message", "link", "attached_media", "published", "scheduled_publish_time", "unpublished_content_type", "targeting", "feed_targeting"]
                : ["url", "message", "caption", "published", "scheduled_publish_time", "alt_text_custom", "temporary"];
            var parameters = api["params"]!.AsArray();
            for (int i = parameters.Count - 1; i >= 0; i--)
            {
                string name = parameters[i]!["name"]!.GetValue<string>();
                if (!allowed.Contains(name, StringComparer.Ordinal)) parameters.RemoveAt(i);
                else if (name == "scheduled_publish_time") parameters[i]!["type"] = "unsigned int";
            }
        }
        specifications["Page"] = page.ToJsonString();

    }

    private static JsonObject Published() => new() { ["$ref"] = "#/components/schemas/PublishingResult" };
}
