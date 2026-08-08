using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeStudio.Endfield.Cli.Story.Timeline;

/// <summary>
/// Recovers authored line order from serialized Timeline-like object graphs.
/// This parser deliberately does not infer server-side mission progression;
/// it only reports order encoded in Unity Timeline data.
/// </summary>
internal static class StoryDialogTimelineEvidence
{
    private static readonly Regex LineIdPattern = new(
        @"^dlg_.+_\d+(?:d\d+)?_\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex OptionIdPattern = new(
        @"^option_dlg_.+_\d+(?:d\d+)?_\d+_\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DialogIdPattern = new(
        @"^dlg_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)_\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] ExplicitLineFields =
    {
        "_trunkId", "trunkId", "dialogId", "lineId", "m_LineId", "m_DisplayName",
    };

    private sealed class TimelineCandidate
    {
        public required string Name { get; init; }
        public required string SourceFile { get; init; }
        public required long PathId { get; init; }
        public List<JsonObject> Lines { get; } = new();
        public List<JsonObject> Options { get; } = new();
        public List<JsonObject> RuntimeJumpClips { get; } = new();
        public int DuplicateClipCount { get; set; }
    }

    public static Dictionary<string, JsonArray> Recover(IEnumerable<StoryTimelineRecord> input)
    {
        var records = input.ToDictionary(
            record => Key(record.SourceFile, record.PathId),
            record => record,
            StringComparer.Ordinal);
        var result = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in records.Values
                     .Where(IsTimelineRoot)
                     .OrderBy(record => record.SourceFile, StringComparer.Ordinal)
                     .ThenBy(record => record.PathId))
        {
            var candidate = ReadTimeline(root, records);
            if (candidate is null || candidate.Lines.Count == 0) continue;

            var dialogIds = candidate.Lines
                .Select(line => DialogIdFromLineId(line["id"]?.GetValue<string>()))
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string dialogId in dialogIds)
            {
                var dialogLines = candidate.Lines
                    .Where(line => string.Equals(
                        DialogIdFromLineId(line["id"]?.GetValue<string>()),
                        dialogId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var dialogOptions = candidate.Options
                    .Where(option => string.Equals(
                        DialogIdFromOptionId(option["id"]?.GetValue<string>()),
                        dialogId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var payload = new JsonObject
                {
                    ["timeline"] = candidate.Name,
                    ["source"] = new JsonObject
                    {
                        ["sourceFile"] = candidate.SourceFile,
                        ["pathId"] = candidate.PathId,
                    },
                    ["lines"] = new JsonArray(dialogLines.Select(item => item.DeepClone()).ToArray()),
                    ["options"] = new JsonArray(dialogOptions.Select(item => item.DeepClone()).ToArray()),
                    ["runtimeJumpClips"] = new JsonArray(candidate.RuntimeJumpClips.Select(item => item.DeepClone()).ToArray()),
                    ["duplicateClipCount"] = candidate.DuplicateClipCount,
                };
                if (!result.TryGetValue(dialogId, out var timelines))
                {
                    timelines = new JsonArray();
                    result[dialogId] = timelines;
                }
                timelines.Add(payload);
            }
        }

        return result;
    }

    private static TimelineCandidate? ReadTimeline(
        StoryTimelineRecord root,
        IReadOnlyDictionary<string, StoryTimelineRecord> records)
    {
        var candidate = new TimelineCandidate
        {
            Name = root.Name,
            SourceFile = root.SourceFile,
            PathId = root.PathId,
        };

        var queue = new Queue<(StoryTimelineRecord record, int trackOrder)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        EnqueueReferences(root, "m_Tracks", 0, queue, records);
        EnqueueReferences(root, "m_Children", 0, queue, records);
        EnqueueReferences(root, "m_PlayableAsset", 0, queue, records);

        while (queue.Count > 0)
        {
            var (track, trackOrder) = queue.Dequeue();
            if (!visited.Add(Key(track.SourceFile, track.PathId))) continue;

            var clips = GetArray(track.Payload, "m_Clips");
            long? trackOptionIndex = Int64OrNull(track.Payload["OptionIndex"]);
            for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            {
                if (clips[clipIndex] is not JsonObject clip) continue;
                var asset = ResolveReference(track, clip["m_Asset"], records);
                if (track.Name.StartsWith("Runtime Jump Track", StringComparison.OrdinalIgnoreCase))
                {
                    long? optionIndex = Int64OrNull(clip["optionIndex"]) ?? trackOptionIndex;
                    JsonNode? start = NumberOrNull(clip["m_Start"]);
                    JsonNode? duration = NumberOrNull(clip["m_Duration"]);
                    double durationValue = duration?.GetValue<double>() ?? 0.0;
                    if (optionIndex is not null && durationValue > 0.0)
                    {
                        var jump = new JsonObject
                        {
                            ["kind"] = "runtimeJump",
                            ["optionIndex"] = optionIndex.Value,
                            ["start"] = start,
                            ["duration"] = duration,
                            ["end"] = (start?.GetValue<double>() ?? 0.0) + durationValue,
                            ["track"] = ObjectRef(track),
                            ["clipOrder"] = clipIndex,
                            ["displayName"] = StringValue(clip["m_DisplayName"]),
                            ["asset"] = asset is null ? null : ObjectRef(asset),
                        };
                        foreach (string field in new[]
                        {
                            "isReverseJump", "needChangeOptionAfterJump", "optionIndexAfterJump", "isJumpFirst",
                            "crossFadeDurationAfterJump",
                        })
                        {
                            JsonNode? value = asset?.Payload[field];
                            if (value is not null) jump[field] = value.DeepClone();
                        }
                        candidate.RuntimeJumpClips.Add(jump);
                    }
                }
                string? lineId = FirstMatchingString(clip, ExplicitLineFields)
                    ?? FirstMatchingString(asset?.Payload, ExplicitLineFields)
                    ?? FirstMatchingString(clip, LineIdPattern)
                    ?? FirstMatchingString(asset?.Payload, LineIdPattern);
                string? optionId = FirstMatchingString(clip, OptionIdPattern)
                    ?? FirstMatchingString(asset?.Payload, OptionIdPattern);

                if (lineId is not null)
                {
                    candidate.Lines.Add(new JsonObject
                    {
                        ["id"] = lineId,
                        ["start"] = NumberOrNull(clip["m_Start"]),
                        ["duration"] = NumberOrNull(clip["m_Duration"]),
                        ["trackOrder"] = trackOrder,
                        ["clipOrder"] = clipIndex,
                        ["source"] = ObjectRef(track),
                        ["asset"] = asset is null ? null : ObjectRef(asset),
                    });
                }
                if (optionId is not null)
                {
                    candidate.Options.Add(new JsonObject
                    {
                        ["id"] = optionId,
                        ["start"] = NumberOrNull(clip["m_Start"]),
                        ["duration"] = NumberOrNull(clip["m_Duration"]),
                        ["trackOrder"] = trackOrder,
                        ["clipOrder"] = clipIndex,
                        ["source"] = ObjectRef(track),
                        ["asset"] = asset is null ? null : ObjectRef(asset),
                    });
                }
                if (asset is not null)
                {
                    foreach (StoryTimelineRecord optionAsset in ResolveReferences(asset, "bindingOptionAssets", records))
                    {
                        foreach (string boundOptionId in MatchingStrings(optionAsset.Payload, OptionIdPattern))
                        {
                            candidate.Options.Add(new JsonObject
                            {
                                ["id"] = boundOptionId,
                                ["start"] = NumberOrNull(clip["m_Start"]),
                                ["duration"] = NumberOrNull(clip["m_Duration"]),
                                ["trackOrder"] = trackOrder,
                                ["clipOrder"] = clipIndex,
                                ["source"] = ObjectRef(track),
                                ["asset"] = ObjectRef(optionAsset),
                                ["anchorLineId"] = lineId,
                            });
                        }
                    }
                }

                if (asset is not null)
                {
                    EnqueueReferences(asset, "m_Children", trackOrder, queue, records);
                    EnqueueReferences(asset, "m_Tracks", trackOrder, queue, records);
                }
            }

            EnqueueReferences(track, "m_Children", trackOrder + 1, queue, records);
            EnqueueReferences(track, "m_Tracks", trackOrder + 1, queue, records);
        }

        candidate.Lines.Sort(CompareTimelineItems);
        var selectedLines = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject line in candidate.Lines)
        {
            string? id = StringValue(line["id"]);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!selectedLines.TryGetValue(id, out var previous))
            {
                selectedLines[id] = line;
                continue;
            }

            candidate.DuplicateClipCount++;
            if (CompareLinePreference(line, previous) < 0)
                selectedLines[id] = line;
        }
        candidate.Lines.Clear();
        candidate.Lines.AddRange(selectedLines.Values.OrderBy(item => item, Comparer<JsonObject>.Create(CompareTimelineItems)));
        candidate.Options.Sort(CompareTimelineItems);
        return candidate;
    }

    private static void EnqueueReferences(
        StoryTimelineRecord owner,
        string property,
        int order,
        Queue<(StoryTimelineRecord record, int trackOrder)> queue,
        IReadOnlyDictionary<string, StoryTimelineRecord> records)
    {
        int index = 0;
        foreach (StoryTimelineRecord target in ResolveReferences(owner, property, records))
        {
            queue.Enqueue((target, order + index));
            index++;
        }
    }

    private static IEnumerable<StoryTimelineRecord> ResolveReferences(
        StoryTimelineRecord owner,
        string property,
        IReadOnlyDictionary<string, StoryTimelineRecord> records)
    {
        foreach (JsonObject reference in ReferenceNodes(owner.Payload[property]))
        {
            StoryTimelineRecord? target = ResolveReference(owner, reference, records);
            if (target is not null) yield return target;
        }
    }

    private static IEnumerable<JsonObject> ReferenceNodes(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["m_PathID"] is not null)
                yield return obj;
            foreach (var property in obj)
                foreach (JsonObject nested in ReferenceNodes(property.Value))
                    yield return nested;
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
                foreach (JsonObject nested in ReferenceNodes(child))
                    yield return nested;
        }
    }

    private static StoryTimelineRecord? ResolveReference(
        StoryTimelineRecord owner,
        JsonNode? value,
        IReadOnlyDictionary<string, StoryTimelineRecord> records)
    {
        if (value is not JsonObject reference) return null;
        long? pathId = Int64OrNull(reference["m_PathID"]);
        if (pathId is null) return null;
        int fileId = (int)(Int64OrNull(reference["m_FileID"]) ?? 0);
        if (fileId != 0) return null;
        return records.GetValueOrDefault(Key(owner.SourceFile, pathId.Value));
    }

    private static bool IsTimelineRoot(StoryTimelineRecord record)
    {
        if (record.Name.Contains("dlgtl_", StringComparison.OrdinalIgnoreCase)) return true;
        return GetArray(record.Payload, "m_Tracks").Count > 0;
    }

    private static string? FirstMatchingString(JsonNode? node, IReadOnlyList<string> fields)
    {
        if (node is not JsonObject obj) return null;
        foreach (string field in fields)
        {
            string? value = StringValue(obj[field]);
            if (value is not null && (LineIdPattern.IsMatch(value) || OptionIdPattern.IsMatch(value)))
                return value;
        }
        return null;
    }

    private static string? FirstMatchingString(JsonNode? node, Regex pattern)
    {
        string? found = null;
        VisitStrings(node, value =>
        {
            if (found is null && pattern.IsMatch(value)) found = value;
        });
        return found;
    }

    private static IReadOnlyList<string> MatchingStrings(JsonNode? node, Regex pattern)
    {
        var found = new List<string>();
        VisitStrings(node, value =>
        {
            if (pattern.IsMatch(value) && !found.Contains(value, StringComparer.OrdinalIgnoreCase))
                found.Add(value);
        });
        return found;
    }

    private static void VisitStrings(JsonNode? node, Action<string> visitor)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            visitor(text);
            return;
        }
        if (node is JsonObject obj)
        {
            foreach (var property in obj) VisitStrings(property.Value, visitor);
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array) VisitStrings(item, visitor);
        }
    }

    private static JsonArray GetArray(JsonNode node, string property)
        => node.AsObject()[property] as JsonArray ?? new JsonArray();

    private static JsonObject ObjectRef(StoryTimelineRecord record)
        => new()
        {
            ["sourceFile"] = record.SourceFile,
            ["pathId"] = record.PathId,
            ["name"] = record.Name,
            ["classId"] = record.ClassId,
        };

    private static JsonNode? NumberOrNull(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<double>(out double number))
            return JsonValue.Create(number);
        if (node is JsonValue text && text.TryGetValue<string>(out string? raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return JsonValue.Create(number);
        return null;
    }

    private static long? Int64OrNull(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<long>(out long number)) return number;
        if (node is JsonValue text && text.TryGetValue<string>(out string? raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static string? StringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static int CompareTimelineItems(JsonObject left, JsonObject right)
    {
        double leftStart = left["start"]?.GetValue<double>() ?? double.MaxValue;
        double rightStart = right["start"]?.GetValue<double>() ?? double.MaxValue;
        int compare = leftStart.CompareTo(rightStart);
        if (compare != 0) return compare;
        compare = (left["trackOrder"]?.GetValue<int>() ?? 0).CompareTo(right["trackOrder"]?.GetValue<int>() ?? 0);
        if (compare != 0) return compare;
        return (left["clipOrder"]?.GetValue<int>() ?? 0).CompareTo(right["clipOrder"]?.GetValue<int>() ?? 0);
    }

    private static int CompareLinePreference(JsonObject left, JsonObject right)
    {
        int compare = LineSourcePriority(left).CompareTo(LineSourcePriority(right));
        if (compare != 0) return compare;
        compare = LineDurationPriority(left).CompareTo(LineDurationPriority(right));
        if (compare != 0) return compare;
        return CompareTimelineItems(left, right);
    }

    private static int LineSourcePriority(JsonObject line)
    {
        string assetName = ObjectName(line["asset"]);
        if (assetName.StartsWith("DialogTrunkPlayableAsset", StringComparison.OrdinalIgnoreCase)) return 0;
        if (assetName.StartsWith("DialogLipSyncPlayableAsset", StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    private static int LineDurationPriority(JsonObject line)
    {
        double duration = line["duration"] is JsonValue value && value.TryGetValue<double>(out var number)
            ? number
            : 0.0;
        return duration > 0.0 && duration <= 0.05 ? 1 : 0;
    }

    private static string ObjectName(JsonNode? node)
        => node is JsonObject obj ? StringValue(obj["name"]) ?? string.Empty : string.Empty;

    private static string? DialogIdFromLineId(string? lineId)
    {
        if (lineId is null) return null;
        var match = DialogIdPattern.Match(lineId);
        return match.Success ? lineId[..lineId.LastIndexOf('_')] : null;
    }

    private static string? DialogIdFromOptionId(string? optionId)
    {
        if (optionId is null) return null;
        var match = OptionIdPattern.Match(optionId);
        if (!match.Success) return null;
        int optionSeparator = optionId.LastIndexOf('_');
        if (optionSeparator <= "option_".Length) return null;
        int groupSeparator = optionId.LastIndexOf('_', optionSeparator - 1);
        if (groupSeparator <= "option_".Length) return null;
        return optionId["option_".Length..groupSeparator];
    }

    private static string Key(string sourceFile, long pathId)
        => $"{sourceFile}\u001f{pathId.ToString(CultureInfo.InvariantCulture)}";
}
