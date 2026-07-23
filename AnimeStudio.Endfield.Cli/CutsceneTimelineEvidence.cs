using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeStudio.Endfield.Cli;

/// <summary>
/// Recovers subtitle clip order from Unity Timeline graphs. The game stores
/// subtitle references as either a textual cutscene row ID or a numeric
/// TextTable ID, so the result deliberately keeps both forms until the story
/// exporter resolves numeric IDs against TextTable.
/// </summary>
internal static class CutsceneTimelineEvidence
{
    private static readonly Regex SceneLinePattern = new(
        @"^(?<scene>(?:(?:f|m|fm)_)?(?:cutscene|cs_video|black)_.+)_\d+(?:d\d+)?(?:_[fm])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TextIdPropertyPattern = new(
        @"(?:text|subtitle).*(?:id)|(?:id).*(?:text|subtitle)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DirectSceneLinePattern = new(
        @"^(?:(?:f|m|fm)_)?(?:cutscene|cs_video|black)_.+_\d+(?:d\d+)?(?:_[fm])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Dictionary<string, JsonArray> Recover(IEnumerable<DialogTimelineRecord> input)
    {
        var records = input.ToDictionary(
            record => Key(record.SourceFile, record.PathId),
            record => record,
            StringComparer.Ordinal);
        var result = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);

        foreach (DialogTimelineRecord root in records.Values
                     .Where(IsCutsceneRoot)
                     .OrderBy(record => record.SourceFile, StringComparer.Ordinal)
                     .ThenBy(record => record.PathId))
        {
            var queue = new Queue<(DialogTimelineRecord Record, int TrackOrder)>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            EnqueueReferences(root, "m_Tracks", 0, queue, records);
            EnqueueReferences(root, "m_Children", 0, queue, records);
            EnqueueReferences(root, "m_PlayableAsset", 0, queue, records);

            while (queue.Count > 0)
            {
                var (track, trackOrder) = queue.Dequeue();
                if (!visited.Add(Key(track.SourceFile, track.PathId))) continue;
                JsonArray clips = track.Payload["m_Clips"] as JsonArray ?? new JsonArray();
                for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
                {
                    if (clips[clipIndex] is not JsonObject clip) continue;
                    DialogTimelineRecord? asset = ResolveReference(track, clip["m_Asset"], records);
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string id in MatchingIds(clip)) ids.Add(id);
                    if (asset is not null)
                        foreach (string id in MatchingIds(asset.Payload)) ids.Add(id);
                    foreach (string id in ids)
                    {
                        string sceneKey = SceneKey(id);
                        if (sceneKey.Length == 0) continue;
                        if (!result.TryGetValue(sceneKey, out var lines))
                            result[sceneKey] = lines = new JsonArray();
                        lines.Add(new JsonObject
                        {
                            ["id"] = id,
                            ["start"] = NumberOrNull(clip["m_Start"]),
                            ["duration"] = NumberOrNull(clip["m_Duration"]),
                            ["trackOrder"] = trackOrder,
                            ["clipOrder"] = clipIndex,
                            ["track"] = ObjectRef(track),
                            ["asset"] = asset is null ? null : ObjectRef(asset),
                        });
                    }
                    if (asset is not null)
                    {
                        EnqueueReferences(asset, "m_Children", trackOrder + 1, queue, records);
                        EnqueueReferences(asset, "m_Tracks", trackOrder + 1, queue, records);
                    }
                }
                EnqueueReferences(track, "m_Children", trackOrder + 1, queue, records);
                EnqueueReferences(track, "m_Tracks", trackOrder + 1, queue, records);
            }
        }

        foreach (JsonArray lines in result.Values)
        {
            var sorted = lines.OfType<JsonObject>()
                .OrderBy(line => line["start"]?.GetValue<double>() ?? double.MaxValue)
                .ThenBy(line => line["trackOrder"]?.GetValue<int>() ?? int.MaxValue)
                .ThenBy(line => line["clipOrder"]?.GetValue<int>() ?? int.MaxValue)
                .ToArray();
            lines.Clear();
            foreach (JsonObject line in sorted) lines.Add(line);
        }
        return result;
    }

    private static bool IsCutsceneRoot(DialogTimelineRecord record)
        => record.ClassId == 320
            || record.Name.Contains("cutscene", StringComparison.OrdinalIgnoreCase)
            || record.Name.Contains("subtitle", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> MatchingIds(JsonNode node)
    {
        foreach ((string property, JsonNode? value) in EnumerateProperties(node))
        {
            string? text = StringValue(value);
            if (text is not null && DirectSceneLinePattern.IsMatch(text))
                yield return text;
            if (!TextIdPropertyPattern.IsMatch(property)) continue;
            string? scalar = ScalarString(value);
            if (scalar is not null && long.TryParse(scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                yield return "#" + scalar;
        }
    }

    private static string SceneKey(string id)
    {
        if (id.StartsWith("#", StringComparison.Ordinal)) return id;
        Match match = SceneLinePattern.Match(id);
        if (!match.Success) return "";

        string scene = match.Groups["scene"].Value;
        foreach (string prefix in new[] { "f_", "m_", "fm_" })
            if (scene.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                scene = scene[prefix.Length..];
                break;
            }
        if (scene.StartsWith("cs_video_", StringComparison.OrdinalIgnoreCase))
            scene = "cutscene_" + scene[9..];
        return scene;
    }

    private static void EnqueueReferences(
        DialogTimelineRecord owner,
        string property,
        int order,
        Queue<(DialogTimelineRecord Record, int TrackOrder)> queue,
        IReadOnlyDictionary<string, DialogTimelineRecord> records)
    {
        int index = 0;
        foreach (JsonObject reference in ReferenceNodes(owner.Payload[property]))
        {
            DialogTimelineRecord? target = ResolveReference(owner, reference, records);
            if (target is not null) queue.Enqueue((target, order + index++));
        }
    }

    private static DialogTimelineRecord? ResolveReference(
        DialogTimelineRecord owner,
        JsonNode? value,
        IReadOnlyDictionary<string, DialogTimelineRecord> records)
    {
        if (value is not JsonObject reference) return null;
        long? pathId = Int64Value(reference["m_PathID"]);
        if (pathId is null || (Int64Value(reference["m_FileID"]) ?? 0) != 0) return null;
        return records.GetValueOrDefault(Key(owner.SourceFile, pathId.Value));
    }

    private static IEnumerable<JsonObject> ReferenceNodes(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["m_PathID"] is not null) yield return obj;
            foreach (var property in obj)
                foreach (JsonObject nested in ReferenceNodes(property.Value)) yield return nested;
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
                foreach (JsonObject nested in ReferenceNodes(child)) yield return nested;
        }
    }

    private static IEnumerable<(string Property, JsonNode? Value)> EnumerateProperties(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                yield return (property.Key, property.Value);
                foreach (var nested in property.Value is null
                             ? Array.Empty<(string, JsonNode?)>()
                             : EnumerateProperties(property.Value))
                    yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
                if (child is not null)
                    foreach (var nested in EnumerateProperties(child)) yield return nested;
        }
    }

    private static JsonNode? NumberOrNull(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<double>(out double number) ? number : null;

    private static string? StringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static string? ScalarString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out string? text)) return text;
        if (value.TryGetValue<long>(out long integer)) return integer.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out double number)) return number.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static long? Int64Value(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<long>(out long number)) return number;
        if (node is JsonValue text && text.TryGetValue<string>(out string? raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static JsonObject ObjectRef(DialogTimelineRecord record)
        => new()
        {
            ["sourceFile"] = record.SourceFile,
            ["pathId"] = record.PathId,
            ["name"] = record.Name,
        };

    private static string Key(string sourceFile, long pathId) => $"{sourceFile}\u001f{pathId}";
}
