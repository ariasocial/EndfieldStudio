using System.Globalization;
using System.Text.Json.Nodes;

namespace AnimeStudio.Endfield.Cli.Story.Timeline;

/// <summary>Coordinates the three source-specific evidence decoders.</summary>
internal static class StoryTimelineEvidence
{
    public static void Recover(IEnumerable<StoryTimelineRecord> input, StoryTimelineScanResult result, string? missionFilter)
    {
        StoryTimelineRecord[] records = input.ToArray();

        Dictionary<string, JsonArray> dialogTimelines = StoryDialogTimelineEvidence.Recover(records);
        MarkDialogTimeOrder(dialogTimelines);
        Merge(result.DialogTimelinesByDialogId, dialogTimelines, missionFilter, keepNumericKeys: false);

        Dictionary<string, JsonArray> dialogTrees = StoryDialogTreeEvidence.Recover(records);
        MarkTreeOrder(dialogTrees);
        Merge(result.DialogTreesByDialogId, dialogTrees, missionFilter, keepNumericKeys: false);

        Dictionary<string, JsonArray> cutscenes = StoryCutsceneTimelineEvidence.Recover(records);
        MarkCutsceneTimeOrder(cutscenes);
        // Numeric TextTable IDs cannot be mission-filtered until StoryExporter
        // resolves them, so retain them for the parent integration layer.
        Merge(result.CutscenesBySceneId, cutscenes, missionFilter, keepNumericKeys: true);
    }

    private static void Merge(
        Dictionary<string, JsonArray> destination,
        Dictionary<string, JsonArray> source,
        string? missionFilter,
        bool keepNumericKeys)
    {
        foreach (var (id, values) in source)
        {
            if (!(keepNumericKeys && id.StartsWith('#')) && !MatchesMission(id, missionFilter)) continue;
            if (!destination.TryGetValue(id, out JsonArray? existing))
            {
                destination[id] = values;
                continue;
            }
            foreach (JsonNode? value in values) existing.Add(value?.DeepClone());
        }
    }

    private static void MarkDialogTimeOrder(Dictionary<string, JsonArray> timelinesByDialogId)
    {
        foreach (JsonObject timeline in timelinesByDialogId.Values.SelectMany(value => value.OfType<JsonObject>()))
        {
            JsonObject[] lines = (timeline["lines"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            JsonArray groups = TimeGroups(lines);
            timeline["orderGroups"] = groups;
            timeline["orderStatus"] = HasAmbiguousTime(lines) ? "partial" : "total-by-start-time";
            timeline["orderNote"] = "Equal or missing clip times do not establish an order.";
        }
    }

    private static void MarkCutsceneTimeOrder(Dictionary<string, JsonArray> linesBySceneId)
    {
        foreach (JsonArray lines in linesBySceneId.Values)
        {
            JsonObject[] entries = lines.OfType<JsonObject>().ToArray();
            int group = -1;
            double? previous = null;
            foreach (JsonObject entry in entries)
            {
                double? start = Number(entry["start"]);
                if (start is null || previous is null || start.Value != previous.Value) group++;
                entry["timeOrderGroup"] = start is null ? null : group;
                entry["timeOrderStatus"] = start is null || entries.Count(other => Number(other["start"]) == start) > 1
                    ? "unordered-within-group"
                    : "ordered-by-start-time";
                previous = start;
            }
        }
    }

    private static void MarkTreeOrder(Dictionary<string, JsonArray> treesByDialogId)
    {
        foreach (JsonObject tree in treesByDialogId.Values.SelectMany(value => value.OfType<JsonObject>()))
        {
            DialogLineProjection projection = ProjectDialogLines(tree);
            tree["lineOrderGroups"] = new JsonArray(projection.Groups
                .Select(group => new JsonArray(group.Select(value => JsonValue.Create(value)).ToArray())).ToArray());
            tree["projectedLineEdges"] = new JsonArray(projection.Edges
                .Select(edge => new JsonObject { ["from"] = edge.From, ["to"] = edge.To }).ToArray());
            tree["orderStatus"] = projection.IsTotal ? "total-by-dialog-tree-line-path" : "partial-by-dialog-tree-line-graph";
            tree["orderNote"] = projection.IsTotal
                ? "After non-text control nodes are projected out, dialog lines establish one path."
                : "Dialog-line branches, merges, cycles, or disconnected line components do not establish a total order.";
        }
    }

    private sealed record DialogLineProjection(List<List<string>> Groups, List<(string From, string To)> Edges, bool IsTotal);

    private static DialogLineProjection ProjectDialogLines(JsonObject tree)
    {
        JsonObject[] nodes = (tree["nodes"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var lineByNode = nodes
            .Where(node => Scalar(node["id"]) is not null && Scalar(node["lineId"]) is not null)
            .ToDictionary(node => Scalar(node["id"])!, node => Scalar(node["lineId"])!, StringComparer.Ordinal);
        var adjacency = nodes.Select(node => Scalar(node["id"])).Where(id => id is not null).Cast<string>()
            .ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (JsonObject edge in (tree["edges"] as JsonArray ?? []).OfType<JsonObject>())
            if (Scalar(edge["from"]) is string from && Scalar(edge["to"]) is string to && adjacency.TryGetValue(from, out List<string>? targets))
                targets.Add(to);

        var projected = new HashSet<(string From, string To)>();
        foreach (var (sourceNode, sourceLine) in lineByNode)
        {
            var queue = new Queue<string>(adjacency.GetValueOrDefault(sourceNode) ?? []);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (queue.TryDequeue(out string? current))
            {
                if (!visited.Add(current)) continue;
                if (lineByNode.TryGetValue(current, out string? targetLine))
                {
                    if (!string.Equals(sourceLine, targetLine, StringComparison.OrdinalIgnoreCase)) projected.Add((sourceLine, targetLine));
                    continue;
                }
                foreach (string next in adjacency.GetValueOrDefault(current) ?? []) queue.Enqueue(next);
            }
        }

        string[] authoredOrder = (tree["lineIds"] as JsonArray ?? []).Select(Scalar).Where(id => id is not null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rank = authoredOrder.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        var lines = lineByNode.Values.Concat(authoredOrder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var outgoing = lines.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var incoming = lines.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in projected)
            if (outgoing.ContainsKey(edge.From) && incoming.ContainsKey(edge.To))
            {
                outgoing[edge.From].Add(edge.To);
                incoming[edge.To]++;
            }

        var remaining = new HashSet<string>(lines, StringComparer.OrdinalIgnoreCase);
        var groups = new List<List<string>>();
        while (remaining.Count > 0)
        {
            List<string> roots = remaining.Where(id => incoming[id] == 0)
                .OrderBy(id => rank.GetValueOrDefault(id, int.MaxValue)).ThenBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            if (roots.Count == 0)
            {
                groups.Add(remaining.OrderBy(id => rank.GetValueOrDefault(id, int.MaxValue)).ThenBy(id => id, StringComparer.OrdinalIgnoreCase).ToList());
                break;
            }
            groups.Add(roots);
            foreach (string root in roots)
            {
                remaining.Remove(root);
                foreach (string target in outgoing[root]) incoming[target]--;
            }
        }
        bool total = lines.Length > 0 && groups.All(group => group.Count == 1) && projected.Count == Math.Max(0, lines.Length - 1);
        return new DialogLineProjection(groups, projected.OrderBy(edge => rank.GetValueOrDefault(edge.From, int.MaxValue))
            .ThenBy(edge => rank.GetValueOrDefault(edge.To, int.MaxValue)).ToList(), total);
    }

    private static string? Scalar(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : node?.ToJsonString().Trim('"');

    private static JsonArray TimeGroups(IEnumerable<JsonObject> lines)
    {
        var groups = lines.GroupBy(line => Number(line["start"]), NullableDoubleComparer.Instance)
            .Select(group => new JsonObject
            {
                ["start"] = group.Key,
                ["lineIds"] = new JsonArray(group.Select(line => line["id"]?.DeepClone()).ToArray()),
                ["orderedWithinGroup"] = group.Count() <= 1 && group.Key is not null,
            });
        return new JsonArray(groups.ToArray());
    }

    private static bool HasAmbiguousTime(IReadOnlyCollection<JsonObject> lines)
        => lines.Any(line => Number(line["start"]) is null)
            || lines.GroupBy(line => Number(line["start"]), NullableDoubleComparer.Instance).Any(group => group.Key is not null && group.Count() > 1);

    private static double? Number(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<double>(out double number)) return number;
        return node is JsonValue text && text.TryGetValue<string>(out string? raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static bool MatchesMission(string id, string? mission)
        => string.IsNullOrWhiteSpace(mission)
            || id.Contains("_" + mission + "_", StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("_" + mission, StringComparison.OrdinalIgnoreCase);

    private sealed class NullableDoubleComparer : IEqualityComparer<double?>
    {
        public static NullableDoubleComparer Instance { get; } = new();
        public bool Equals(double? x, double? y) => x.Equals(y);
        public int GetHashCode(double? value) => value.GetHashCode();
    }
}
