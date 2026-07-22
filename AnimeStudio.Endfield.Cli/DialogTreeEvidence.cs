using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeStudio.Endfield.Cli;

/// <summary>
/// Reads DialogTree JSON stored in Unity TextAsset objects. The tree is kept
/// as compact graph evidence: line order, option branches, and the source
/// object are retained, while server-side progression is deliberately out of
/// scope.
/// </summary>
internal static class DialogTreeEvidence
{
    private static readonly Regex LineIdPattern = new(
        @"^dlg_.+_\d+(?:d\d+)?_\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex OptionIdPattern = new(
        @"^option_dlg_.+_\d+(?:d\d+)?_\d+_\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static Dictionary<string, JsonArray> Recover(IEnumerable<DialogTimelineRecord> input)
    {
        var result = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        foreach (DialogTimelineRecord record in input)
        {
            if (record.ClassId != 49) continue;
            JsonObject? tree = DecodeTree(record.Payload);
            if (tree is null) continue;

            var parsed = ParseTree(record, tree);
            foreach (string dialogId in parsed.DialogIds)
            {
                var payload = (JsonObject)parsed.Payload.DeepClone();
                payload["dialogId"] = dialogId;
                FilterDialogEvidence(payload, dialogId);
                if (!result.TryGetValue(dialogId, out var trees))
                {
                    trees = new JsonArray();
                    result[dialogId] = trees;
                }
                trees.Add(payload);
            }
        }
        return result;
    }

    private sealed class ParsedTree
    {
        public required JsonObject Payload { get; init; }
        public required IReadOnlyList<string> DialogIds { get; init; }
    }

    private static ParsedTree ParseTree(DialogTimelineRecord record, JsonObject tree)
    {
        var nodes = tree["nodes"] as JsonArray ?? new JsonArray();
        var connections = tree["connections"] as JsonArray ?? new JsonArray();
        var nodeById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var nodeOrder = new List<string>();
        var nodePositions = new Dictionary<string, (double x, double y, int index)>(StringComparer.Ordinal);
        var lineByNode = new Dictionary<string, string>(StringComparer.Ordinal);
        var optionsByNode = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (JsonNode? nodeNode in nodes)
        {
            if (nodeNode is not JsonObject node) continue;
            string nodeId = StringValue(node["$id"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nodeId))
                nodeId = $"__index_{nodeOrder.Count}";
            while (nodeById.ContainsKey(nodeId))
                nodeId = $"{nodeId}_{nodeOrder.Count}";
            nodeById[nodeId] = node;
            nodeOrder.Add(nodeId);
            nodePositions[nodeId] = Position(node, nodeOrder.Count - 1);

            string lineId = FirstMatchingString(node, "_trunkId", LineIdPattern);
            if (lineId.Length > 0) lineByNode[nodeId] = lineId;

            var optionIds = new List<string>();
            if (node["_normalOptions"] is JsonArray optionEntries)
            {
                foreach (JsonNode? optionNode in optionEntries)
                {
                    if (optionNode is not JsonObject option) continue;
                    string optionId = FirstMatchingString(option, "_optionId", OptionIdPattern);
                    if (optionId.Length > 0 && !optionIds.Contains(optionId, StringComparer.OrdinalIgnoreCase))
                        optionIds.Add(optionId);
                }
            }
            if (optionIds.Count > 0) optionsByNode[nodeId] = optionIds;
        }

        var successors = nodeOrder.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var predecessors = nodeOrder.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var compactNodes = new JsonArray();
        foreach (string nodeId in nodeOrder)
        {
            var compact = new JsonObject
            {
                ["id"] = nodeId,
                ["type"] = NodeType(nodeById[nodeId]),
            };
            if (lineByNode.TryGetValue(nodeId, out string? lineId)) compact["lineId"] = lineId;
            if (optionsByNode.TryGetValue(nodeId, out var optionIds))
                compact["optionIds"] = new JsonArray(optionIds.Select(value => JsonValue.Create(value)).ToArray());
            compactNodes.Add(compact);
        }

        var compactEdges = new JsonArray();
        foreach (JsonNode? connectionNode in connections)
        {
            if (connectionNode is not JsonObject connection) continue;
            string source = ReferenceId(connection["_sourceNode"]);
            string target = ReferenceId(connection["_targetNode"]);
            if (source.Length == 0 || target.Length == 0
                || !successors.ContainsKey(source) || !successors.ContainsKey(target))
                continue;
            if (!successors[source].Contains(target, StringComparer.Ordinal))
            {
                successors[source].Add(target);
                predecessors[target].Add(source);
                compactEdges.Add(new JsonObject { ["from"] = source, ["to"] = target });
            }
        }

        var orderedNodeIds = OrderedNodes(nodeOrder, successors, predecessors, nodePositions);
        var orderedLineIds = orderedNodeIds
            .Where(lineByNode.ContainsKey)
            .Select(id => lineByNode[id])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dialogIds = orderedLineIds
            .Select(DialogIdFromLineId)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var branches = new JsonArray();
        foreach (var (optionNodeId, optionIds) in optionsByNode)
        {
            var targets = successors[optionNodeId];
            for (int index = 0; index < optionIds.Count; index++)
            {
                string? targetNodeId = targets.Count == 1
                    ? targets[0]
                    : index < targets.Count ? targets[index] : null;
                var path = targetNodeId is null
                    ? new List<string>()
                    : WalkPath(targetNodeId, successors, lineByNode);
                var branch = new JsonObject
                {
                    ["optionId"] = optionIds[index],
                    ["sourceNodeId"] = optionNodeId,
                    ["pathLineIds"] = new JsonArray(path.Select(value => JsonValue.Create(value)).ToArray()),
                };
                if (targetNodeId is not null) branch["targetNodeId"] = targetNodeId;
                string? after = PreviousLine(optionNodeId, lineByNode, orderedNodeIds);
                if (after is not null) branch["afterLineId"] = after;
                branches.Add(branch);
            }
        }

        var payload = new JsonObject
        {
            ["treeName"] = record.Name,
            ["source"] = new JsonObject
            {
                ["sourceFile"] = record.SourceFile,
                ["pathId"] = record.PathId,
                ["name"] = record.Name,
            },
            ["lineIds"] = new JsonArray(orderedLineIds.Select(value => JsonValue.Create(value)).ToArray()),
            ["nodes"] = compactNodes,
            ["edges"] = compactEdges,
            ["branches"] = branches,
        };

        return new ParsedTree { Payload = payload, DialogIds = dialogIds };
    }

    private static void FilterDialogEvidence(JsonObject payload, string dialogId)
    {
        var lines = (payload["lineIds"] as JsonArray ?? new JsonArray())
            .Where(node => node is JsonValue value && value.TryGetValue<string>(out var id)
                && DialogIdFromLineId(id) is string owner
                && owner.Equals(dialogId, StringComparison.OrdinalIgnoreCase))
            .Select(node => node!.DeepClone())
            .ToArray();
        payload["lineIds"] = new JsonArray(lines);

        if (payload["branches"] is JsonArray branches)
        {
            var filteredBranches = branches.OfType<JsonObject>()
                .Where(branch =>
                {
                    string? optionId = StringValue(branch["optionId"]);
                    if (OptionDialogIdFromOptionId(optionId)?.Equals(dialogId, StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                    return (branch["pathLineIds"] as JsonArray ?? new JsonArray())
                        .OfType<JsonValue>()
                        .Select(value => value.TryGetValue<string>(out var id) ? id : null)
                        .Any(id => DialogIdFromLineId(id)?.Equals(dialogId, StringComparison.OrdinalIgnoreCase) == true);
                })
                .ToArray();
            branches.Clear();
            foreach (JsonObject branch in filteredBranches)
            {
                var path = (branch["pathLineIds"] as JsonArray ?? new JsonArray())
                    .Where(node => node is JsonValue value && value.TryGetValue<string>(out var id)
                        && (DialogIdFromLineId(id)?.Equals(dialogId, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Select(node => node!.DeepClone())
                    .ToArray();
                branch["pathLineIds"] = new JsonArray(path);
            }
        }
    }

    private static JsonObject? DecodeTree(JsonNode payload)
    {
        if (payload is not JsonObject obj) return null;
        string? encoded = StringValue(obj["m_Script"]);
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            string text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
        catch (DecoderFallbackException) { return null; }
    }

    private static List<string> OrderedNodes(
        IReadOnlyList<string> nodeOrder,
        IReadOnlyDictionary<string, List<string>> successors,
        IReadOnlyDictionary<string, List<string>> predecessors,
        IReadOnlyDictionary<string, (double x, double y, int index)> positions)
    {
        (double x, double y, int index) PositionOf(string id)
            => positions.TryGetValue(id, out var position) ? position : (0.0, 0.0, int.MaxValue);

        var cycleCache = new Dictionary<(string source, string target), bool>();
        bool ClosesCycle(string source, string target)
        {
            var key = (source, target);
            if (cycleCache.TryGetValue(key, out bool cached)) return cached;
            if (source == target) return cycleCache[key] = true;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(target);
            while (stack.Count > 0)
            {
                string current = stack.Pop();
                if (!seen.Add(current)) continue;
                if (current == source) return cycleCache[key] = true;
                foreach (string next in successors[current]) stack.Push(next);
            }
            return cycleCache[key] = false;
        }

        bool IsVisualReturn(string source, string target)
        {
            var sourcePosition = PositionOf(source);
            var targetPosition = PositionOf(target);
            const double backEdgeXTolerance = 80.0;
            const double rowWrapYTolerance = 120.0;
            if (targetPosition.y >= sourcePosition.y + rowWrapYTolerance) return false;
            return sourcePosition.x > targetPosition.x + backEdgeXTolerance
                || targetPosition.y < sourcePosition.y - rowWrapYTolerance;
        }

        bool IsForwardPredecessor(string predecessor, string nodeId)
            => !ClosesCycle(predecessor, nodeId) || !IsVisualReturn(predecessor, nodeId);

        var effectivePredecessors = nodeOrder.ToDictionary(
            id => id,
            id => predecessors[id]
                .Where(predecessor => IsForwardPredecessor(predecessor, id))
                .ToList(),
            StringComparer.Ordinal);
        var roots = nodeOrder
            .Where(id => effectivePredecessors[id].Count == 0)
            .OrderBy(id => PositionOf(id).x)
            .ThenBy(id => PositionOf(id).y)
            .ThenBy(id => PositionOf(id).index)
            .ToList();

        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queued = new HashSet<string>(roots, StringComparer.Ordinal);
        var ready = new Stack<string>(roots.AsEnumerable().Reverse());

        void QueueSuccessors(string nodeId)
        {
            var nextNodes = successors[nodeId]
                .Where(next => !visited.Contains(next) && !queued.Contains(next))
                .Reverse()
                .ToArray();
            foreach (string next in nextNodes)
            {
                if (!effectivePredecessors[next].All(visited.Contains)) continue;
                ready.Push(next);
                queued.Add(next);
            }
        }

        void DrainReady()
        {
            while (ready.Count > 0)
            {
                string nodeId = ready.Pop();
                queued.Remove(nodeId);
                if (visited.Contains(nodeId) || !effectivePredecessors[nodeId].All(visited.Contains)) continue;
                visited.Add(nodeId);
                ordered.Add(nodeId);
                QueueSuccessors(nodeId);
            }
        }

        DrainReady();
        while (visited.Count < nodeOrder.Count)
        {
            string next = nodeOrder
                .Where(id => !visited.Contains(id))
                .OrderBy(id => PositionOf(id).x)
                .ThenBy(id => PositionOf(id).y)
                .ThenBy(id => PositionOf(id).index)
                .FirstOrDefault() ?? string.Empty;
            if (next.Length == 0) break;
            queued.Remove(next);
            visited.Add(next);
            ordered.Add(next);
            QueueSuccessors(next);
            DrainReady();
        }

        // The graph order is authoritative only for line-bearing nodes. The
        // non-line nodes remain in the traversal so branch and merge evidence
        // can still be inspected by callers.
        return ordered;
    }

    private static (double x, double y, int index) Position(JsonObject node, int index)
    {
        if (node["_position"] is not JsonObject position)
            return (0.0, 0.0, index);
        return (NumberValue(position["x"]) ?? 0.0, NumberValue(position["y"]) ?? 0.0, index);
    }

    private static List<string> WalkPath(
        string start,
        IReadOnlyDictionary<string, List<string>> successors,
        IReadOnlyDictionary<string, string> lineByNode)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? current = start;
        while (current is not null && visited.Add(current))
        {
            if (lineByNode.TryGetValue(current, out string? lineId)) result.Add(lineId);
            current = successors[current].Count == 1 ? successors[current][0] : null;
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? PreviousLine(
        string nodeId,
        IReadOnlyDictionary<string, string> lineByNode,
        IReadOnlyList<string> orderedNodeIds)
    {
        int index = -1;
        for (int i = 0; i < orderedNodeIds.Count; i++)
        {
            if (orderedNodeIds[i] == nodeId)
            {
                index = i;
                break;
            }
        }
        for (int i = index - 1; i >= 0; i--)
            if (lineByNode.TryGetValue(orderedNodeIds[i], out string? lineId)) return lineId;
        return null;
    }

    private static string NodeType(JsonObject node)
        => StringValue(node["$type"]) ?? StringValue(node["type"]) ?? string.Empty;

    private static string ReferenceId(JsonNode? node)
        => node is JsonObject obj ? StringValue(obj["$ref"]) ?? string.Empty : string.Empty;

    private static string FirstMatchingString(JsonObject node, string explicitField, Regex pattern)
    {
        string? explicitValue = StringValue(node[explicitField]);
        if (explicitValue is not null && pattern.IsMatch(explicitValue)) return explicitValue;
        string? found = null;
        VisitStrings(node, value =>
        {
            if (found is null && pattern.IsMatch(value)) found = value;
        });
        return found ?? string.Empty;
    }

    private static void VisitStrings(JsonNode? node, Action<string> visitor)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            visitor(text);
            return;
        }
        if (node is JsonObject obj)
            foreach (var property in obj) VisitStrings(property.Value, visitor);
        else if (node is JsonArray array)
            foreach (JsonNode? child in array) VisitStrings(child, visitor);
    }

    private static string? StringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? NumberValue(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out double number)) return number;
        if (value.TryGetValue<decimal>(out decimal decimalNumber)) return (double)decimalNumber;
        return null;
    }

    private static string? DialogIdFromLineId(string? lineId)
    {
        if (lineId is null || !LineIdPattern.IsMatch(lineId)) return null;
        int last = lineId.LastIndexOf('_');
        return last > 0 ? lineId[..last] : null;
    }

    private static string? OptionDialogIdFromOptionId(string? optionId)
    {
        if (optionId is null || !OptionIdPattern.IsMatch(optionId)) return null;
        int optionSeparator = optionId.LastIndexOf('_');
        int groupSeparator = optionId.LastIndexOf('_', optionSeparator - 1);
        if (groupSeparator <= "option_".Length) return null;
        return optionId["option_".Length..groupSeparator];
    }
}
