using System.Text.Json.Nodes;

namespace AnimeStudio.Endfield.Cli.Story;

internal static class StoryGraphPostProcessor
{
    public static JsonObject? ApplyOverrides(JsonObject root, string mission, Dictionary<string, JsonObject> scenes, JsonObject graph)
    {
        JsonObject? value = root["missions"]?[mission] as JsonObject ?? root[mission] as JsonObject;
        if (value is null) return null;
        int addedEdges = 0, removedEdges = 0, patchedScenes = 0;
        if (value["graph"] is JsonObject graphOverride)
        {
            JsonArray edges = graph["edges"] as JsonArray ?? [];
            if (graphOverride["removeEdges"] is JsonArray removals)
                foreach (JsonObject selector in removals.OfType<JsonObject>())
                    for (int index = edges.Count - 1; index >= 0; index--)
                        if (edges[index] is JsonObject edge && MatchesSelector(edge, selector))
                        {
                            edges.RemoveAt(index);
                            removedEdges++;
                        }
            if (graphOverride["addEdges"] is JsonArray additions)
                foreach (JsonObject addition in additions.OfType<JsonObject>())
                {
                    JsonObject edge = (JsonObject)addition.DeepClone();
                    edge["source"] ??= "override";
                    edge["status"] ??= "inferred";
                    edge["authority"] = "manual override";
                    edges.Add(edge);
                    addedEdges++;
                }
            if (graphOverride["replaceFields"] is JsonObject replacements) Merge(graph, replacements);
        }
        if (value["scenes"] is JsonObject sceneOverrides)
            foreach (var item in sceneOverrides)
                if (item.Value is JsonObject patch && scenes.TryGetValue(item.Key, out JsonObject? scene))
                {
                    Merge(scene, patch);
                    scene["overrideApplied"] = true;
                    patchedScenes++;
                }
        return new JsonObject { ["addedEdges"] = addedEdges, ["removedEdges"] = removedEdges, ["patchedScenes"] = patchedScenes };
    }

    public static void RefreshDerived(JsonObject graph, IEnumerable<string> sceneIds)
    {
        JsonArray edges = DeduplicateEdges(graph["edges"] as JsonArray ?? []);
        graph["edges"] = edges;
        var connected = edges.OfType<JsonObject>()
            .SelectMany(edge => new[] { Scalar(edge["from"]), Scalar(edge["to"]) })
            .Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        graph["disconnected"] = new JsonArray(sceneIds.Where(id => !connected.Contains(id)).Order(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray());
        JsonArray nodes = graph["nodes"] as JsonArray ?? [];
        graph["unresolved"] = new JsonArray(nodes.OfType<JsonObject>().Where(node => Scalar(node["status"]) == "unresolved").Select(node => node["id"]?.DeepClone()).ToArray());
    }

    private static JsonArray DeduplicateEdges(JsonArray edges)
    {
        var result = new JsonArray();
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonObject edge in edges.OfType<JsonObject>())
        {
            string key = string.Join("\u001f",
                Scalar(edge["from"]) ?? string.Empty,
                Scalar(edge["to"]) ?? string.Empty,
                Scalar(edge["kind"]) ?? string.Empty,
                Scalar(edge["status"]) ?? string.Empty,
                Scalar(edge["source"]) ?? string.Empty,
                Scalar(edge["branch"]) ?? string.Empty);
            if (!byKey.TryGetValue(key, out JsonObject? existing))
            {
                existing = (JsonObject)edge.DeepClone();
                byKey[key] = existing;
                result.Add(existing);
                continue;
            }
            if (edge["evidence"] is not JsonNode evidence) continue;
            JsonArray alternatives = existing["additionalEvidence"] as JsonArray ?? [];
            if (existing["additionalEvidence"] is null) existing["additionalEvidence"] = alternatives;
            string serialized = evidence.ToJsonString();
            if (!alternatives.Any(value => value?.ToJsonString() == serialized)
                && existing["evidence"]?.ToJsonString() != serialized)
                alternatives.Add(evidence.DeepClone());
        }
        return result;
    }

    private static bool MatchesSelector(JsonObject value, JsonObject selector)
        => selector.All(pair => pair.Value is null || string.Equals(Scalar(value[pair.Key]), Scalar(pair.Value), StringComparison.OrdinalIgnoreCase));

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var pair in patch) target[pair.Key] = pair.Value?.DeepClone();
    }

    private static string? Scalar(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : node?.ToJsonString().Trim('"');
}
