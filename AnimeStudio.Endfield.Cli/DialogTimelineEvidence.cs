using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeStudio.Endfield.Cli;

/// <summary>
/// A small, source-backed representation of a Unity object used by a dialog
/// Timeline. Keeping the raw payload out of the final dialog JSON makes the
/// recovery result compact while retaining enough information to audit it.
/// </summary>
internal sealed class DialogTimelineRecord
{
    public required string SourceFile { get; init; }
    public required long PathId { get; init; }
    public required int ClassId { get; init; }
    public required string Name { get; init; }
    public required JsonNode Payload { get; init; }
}

/// <summary>
/// Recovers authored line order from serialized Timeline-like object graphs.
/// This parser deliberately does not infer server-side mission progression;
/// it only reports order encoded in Unity Timeline data.
/// </summary>
internal static class DialogTimelineEvidence
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
    }

    public static Dictionary<string, JsonArray> Recover(IEnumerable<DialogTimelineRecord> input)
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
                var payload = new JsonObject
                {
                    ["timeline"] = candidate.Name,
                    ["source"] = new JsonObject
                    {
                        ["sourceFile"] = candidate.SourceFile,
                        ["pathId"] = candidate.PathId,
                    },
                    ["lines"] = new JsonArray(candidate.Lines.Select(item => item.DeepClone()).ToArray()),
                    ["options"] = new JsonArray(candidate.Options.Select(item => item.DeepClone()).ToArray()),
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
        DialogTimelineRecord root,
        IReadOnlyDictionary<string, DialogTimelineRecord> records)
    {
        var candidate = new TimelineCandidate
        {
            Name = root.Name,
            SourceFile = root.SourceFile,
            PathId = root.PathId,
        };

        var queue = new Queue<(DialogTimelineRecord record, int trackOrder)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        EnqueueReferences(root, "m_Tracks", 0, queue, records);
        EnqueueReferences(root, "m_Children", 0, queue, records);

        while (queue.Count > 0)
        {
            var (track, trackOrder) = queue.Dequeue();
            if (!visited.Add(Key(track.SourceFile, track.PathId))) continue;

            var clips = GetArray(track.Payload, "m_Clips");
            for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            {
                if (clips[clipIndex] is not JsonObject clip) continue;
                var asset = ResolveReference(track, clip["m_Asset"], records);
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
                    EnqueueReferences(asset, "m_Children", trackOrder, queue, records);
                    EnqueueReferences(asset, "m_Tracks", trackOrder, queue, records);
                }
            }

            EnqueueReferences(track, "m_Children", trackOrder + 1, queue, records);
            EnqueueReferences(track, "m_Tracks", trackOrder + 1, queue, records);
        }

        candidate.Lines.Sort(CompareTimelineItems);
        candidate.Options.Sort(CompareTimelineItems);
        return candidate;
    }

    private static void EnqueueReferences(
        DialogTimelineRecord owner,
        string property,
        int order,
        Queue<(DialogTimelineRecord record, int trackOrder)> queue,
        IReadOnlyDictionary<string, DialogTimelineRecord> records)
    {
        JsonArray references = GetArray(owner.Payload, property);
        for (int index = 0; index < references.Count; index++)
        {
            var target = ResolveReference(owner, references[index], records);
            if (target is not null) queue.Enqueue((target, order + index));
        }
    }

    private static DialogTimelineRecord? ResolveReference(
        DialogTimelineRecord owner,
        JsonNode? value,
        IReadOnlyDictionary<string, DialogTimelineRecord> records)
    {
        if (value is not JsonObject reference) return null;
        long? pathId = Int64OrNull(reference["m_PathID"]);
        if (pathId is null) return null;
        int fileId = (int)(Int64OrNull(reference["m_FileID"]) ?? 0);
        if (fileId != 0) return null;
        return records.GetValueOrDefault(Key(owner.SourceFile, pathId.Value));
    }

    private static bool IsTimelineRoot(DialogTimelineRecord record)
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

    private static JsonObject ObjectRef(DialogTimelineRecord record)
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

    private static string? DialogIdFromLineId(string? lineId)
    {
        if (lineId is null) return null;
        var match = DialogIdPattern.Match(lineId);
        return match.Success ? lineId[..lineId.LastIndexOf('_')] : null;
    }

    private static string Key(string sourceFile, long pathId)
        => $"{sourceFile}\u001f{pathId.ToString(CultureInfo.InvariantCulture)}";
}
