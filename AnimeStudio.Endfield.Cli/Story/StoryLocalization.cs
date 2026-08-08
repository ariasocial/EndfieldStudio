using System.Text.Json.Nodes;

namespace AnimeStudio.Endfield.Cli.Story;

internal static class StoryLocalization
{
    public static JsonObject Build(Dictionary<string, JsonObject> scenes, string language, JsonObject i18n, bool languageAvailable)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var texts = new JsonObject();
        foreach (JsonObject scene in scenes.Values)
        {
            foreach (JsonObject line in (scene["lines"] as JsonArray ?? []).OfType<JsonObject>()) Visit(line);
            foreach (JsonObject branch in (scene["branches"] as JsonArray ?? []).OfType<JsonObject>()) Visit(branch);
        }
        var localizedScenes = new JsonArray(scenes.Values
            .OrderBy(scene => Scalar(scene["id"]), StringComparer.OrdinalIgnoreCase)
            .Select(LocalizeScene)
            .ToArray());
        return new JsonObject
        {
            ["language"] = language,
            ["languageAvailable"] = languageAvailable,
            ["missingLocalizationIds"] = new JsonArray(missing.Order(StringComparer.Ordinal).Select(value => JsonValue.Create(value)).ToArray()),
            ["texts"] = texts,
            ["scenes"] = localizedScenes,
        };

        JsonObject LocalizeScene(JsonObject scene)
        {
            string? kind = Scalar(scene["kind"]);
            var localized = new JsonObject
            {
                ["id"] = scene["id"]?.DeepClone(),
                ["kind"] = scene["kind"]?.DeepClone(),
                ["lineOrder"] = scene["lineOrder"]?.DeepClone(),
                ["lines"] = new JsonArray((scene["lines"] as JsonArray ?? []).OfType<JsonObject>().Select(line => LocalizeLine(line, kind)).ToArray()),
                ["branches"] = new JsonArray((scene["branches"] as JsonArray ?? []).OfType<JsonObject>().Select(LocalizeBranch).ToArray()),
            };
            return localized;
        }

        JsonObject LocalizeLine(JsonObject line, string? kind)
        {
            JsonNode? value = line["value"];
            JsonObject? map = value as JsonObject;
            JsonNode? textNode = map?["dialogText"] ?? map?["radioText"] ?? map?["remoteCommText"]
                ?? map?["content"] ?? value;
            JsonNode? speakerNode = map?["actorName"] ?? line["speaker"];
            string? speaker = ResolveText(speakerNode);
            return new JsonObject
            {
                ["id"] = line["id"]?.DeepClone(),
                ["source"] = line["source"]?.DeepClone(),
                ["sourceOrder"] = line["sourceOrder"]?.DeepClone(),
                ["resolvedOrder"] = line["resolvedOrder"]?.DeepClone(),
                ["resolvedOrderGroup"] = line["resolvedOrderGroup"]?.DeepClone(),
                ["speakerId"] = map?["actorNameId"]?.DeepClone(),
                ["speaker"] = speaker,
                ["speakerStatus"] = speaker is not null ? "confirmed-table"
                    : kind is "cutscene" or "cs_video" ? "not-present-in-text-source"
                    : kind == "black" ? "not-applicable-or-not-present"
                    : "unresolved",
                ["textId"] = LocalizationId(textNode),
                ["text"] = ResolveText(textNode),
            };
        }

        JsonObject LocalizeBranch(JsonObject branch)
        {
            JsonObject? value = branch["value"] as JsonObject;
            JsonNode? textNode = value?["optionText"] ?? value?["content"] ?? branch["value"];
            return new JsonObject
            {
                ["id"] = branch["id"]?.DeepClone(),
                ["order"] = branch["order"]?.DeepClone(),
                ["choiceOrder"] = branch["choiceOrder"]?.DeepClone(),
                ["afterLineId"] = branch["afterLineId"]?.DeepClone(),
                ["targetLineId"] = branch["targetLineId"]?.DeepClone(),
                ["responseLineIds"] = branch["responseLineIds"]?.DeepClone(),
                ["flowStatus"] = branch["flowStatus"]?.DeepClone(),
                ["flowWarning"] = branch["flowWarning"]?.DeepClone(),
                ["textId"] = LocalizationId(textNode),
                ["text"] = ResolveText(textNode),
            };
        }

        string? LocalizationId(JsonNode? node)
            => node is JsonObject map && Scalar(map["id"]) is string id && long.TryParse(id, out _) && id != "0" ? id : null;

        string? ResolveText(JsonNode? node)
        {
            if (node is JsonObject map)
            {
                string? inline = Scalar(map["text"]);
                if (!string.IsNullOrEmpty(inline)) return inline;
                if (LocalizationId(map) is string id && texts[id] is JsonValue localized
                    && localized.TryGetValue<string>(out string? text)) return text;
            }
            return node is JsonValue value && value.TryGetValue<string>(out string? direct) ? direct : null;
        }

        void Visit(JsonNode? node)
        {
            if (node is JsonObject map)
            {
                if (Scalar(map["id"]) is string id && long.TryParse(id, out _) && id != "0")
                {
                    string? text = i18n[id] is JsonValue scalar && scalar.TryGetValue<string>(out string? direct)
                        ? direct
                        : i18n[id] is JsonObject row
                            ? new[] { "text", "value", "content", "str" }.Select(field => Scalar(row[field])).FirstOrDefault(candidate => candidate is not null)
                            : null;
                    if (text is not null) texts[id] = text;
                    else missing.Add(id);
                }
                foreach (var pair in map) Visit(pair.Value);
            }
            else if (node is JsonArray array)
                foreach (JsonNode? item in array) Visit(item);
        }
    }

    private static string? Scalar(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : node?.ToJsonString().Trim('"');
}
