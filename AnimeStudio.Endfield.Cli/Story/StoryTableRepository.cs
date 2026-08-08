using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;

namespace AnimeStudio.Endfield.Cli.Story;

internal sealed class StoryTableRepository
{
    private static readonly string[] TableNames = ["TextTable", "DialogTextTable", "DialogOptionTable", "DialogSummaryTable", "RadioTable", "RemoteCommonTable", "SNSDialogTable", "SNSDialogOptionTable"];
    public Dictionary<string, JsonObject> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonObject> Missions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StoryEvidence> Evidence { get; } = [];
    public List<string> Warnings { get; } = [];

    public static StoryTableRepository Load(StoryCommand.Options options)
    {
        var repository = new StoryTableRepository();
        // Load base rows first, then overlay primary rows. The primary loader
        // still needs base as a physical chunk fallback because Persistent
        // metadata may intentionally reference unchanged StreamingAssets chunks.
        if (!string.IsNullOrWhiteSpace(options.BaseVfsPath)) repository.LoadSource(options.BaseVfsPath, null, "base", options.Mission);
        repository.LoadSource(options.VfsPath!, options.BaseVfsPath, "primary", options.Mission);
        return repository;
    }

    public JsonObject Table(string name) => Tables.TryGetValue(name, out var table) ? table : new JsonObject();

    private void LoadSource(string path, string? chunkFallbackPath, string source, string? missionFilter)
    {
        var loader = new VfsLoader(path, Keys.ChaCha20Key, chunkFallbackPath);
        LoadTables(loader, source);
        LoadRuntimeAssets(loader, source, missionFilter);
        LoadLevelScripts(loader, source, missionFilter);
    }

    private void LoadTables(VfsLoader loader, string source)
    {
        BlockMainInfo info;
        try { info = loader.LoadBlockInfo(BlockType.Table); }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }
        foreach (ChunkInfo chunk in info.Chunks)
        foreach (AnimeStudio.Endfield.FileInfo file in chunk.Files)
        {
            try
            {
                var (name, json) = SparkBuffer.Parse(loader.ExtractFileToBytes(BlockType.Table, chunk, file));
                if (!IsStoryTable(name) || JsonNode.Parse(json) is not JsonObject rows) continue;
                if (!Tables.TryGetValue(name, out JsonObject? target)) Tables[name] = target = new JsonObject();
                foreach (var row in rows) target[row.Key] = row.Value?.DeepClone();
            }
            catch (Exception ex) { Warn($"Table parse failed ({source}/{file.FileName}): {ex.Message}"); }
        }
    }

    private void LoadRuntimeAssets(VfsLoader loader, string source, string? missionFilter)
    {
        BlockMainInfo info;
        try { info = loader.LoadBlockInfo(BlockType.JsonData); }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }
        var files = info.Chunks
            .SelectMany(chunk => chunk.Files.Select(file => (Chunk: chunk, File: file)))
            .Where(item => item.File.FileName.Contains("MissionRuntimeAsset", StringComparison.OrdinalIgnoreCase)
                || item.File.FileName.Contains("LevelData", StringComparison.OrdinalIgnoreCase)
                || item.File.FileName.Contains("NpcProxyExDataTable", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.File.FileName.Contains("MissionRuntimeAsset", StringComparison.OrdinalIgnoreCase) ? 0
                : item.File.FileName.Contains("NpcProxyExDataTable", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        foreach (var item in files)
        {
            ChunkInfo chunk = item.Chunk;
            AnimeStudio.Endfield.FileInfo file = item.File;
            bool runtime = file.FileName.Contains("MissionRuntimeAsset", StringComparison.OrdinalIgnoreCase);
            bool level = file.FileName.Contains("LevelData", StringComparison.OrdinalIgnoreCase);
            bool npc = file.FileName.Contains("NpcProxyExDataTable", StringComparison.OrdinalIgnoreCase);
            try
            {
                byte[] data = loader.ExtractFileToBytes(BlockType.JsonData, chunk, file);
                AddBinaryStoryReferences(data, source, file.FileName, level ? "level-data-ref" : npc ? "npc-proxy-ref" : "runtime-asset-ref", missionFilter, level);

                // MissionRuntimeAsset is JSON. LevelData uses a binary record
                // format despite its .json suffix, so it is evidence-scanned
                // above instead of being passed to System.Text.Json.
                if (npc)
                {
                    if (JsonNode.Parse(Decode(data)) is JsonObject npcRoot)
                        AddNpcProxyEvidence(npcRoot, source, file.FileName, missionFilter);
                    continue;
                }
                if (level)
                {
                    AddLevelTriggerEvidence(data, source, file.FileName, missionFilter);
                    continue;
                }
                if (JsonNode.Parse(Decode(data)) is not JsonObject obj) continue;
                string key = Path.GetFileNameWithoutExtension(file.FileName);
                Missions[key] = obj; // primary naturally replaces base.
                Evidence.Add(new StoryEvidence("runtime-asset", source, file.FileName, null,
                    new JsonObject { ["asset"] = key, ["missionId"] = key }));
                AddRuntimeEvidence(obj, source, file.FileName, key);
            }
            catch (Exception ex) { Warn($"Runtime asset parse failed ({source}/{file.FileName}): {ex.Message}"); }
        }
    }

    private void AddNpcProxyEvidence(JsonObject root, string source, string file, string? missionFilter)
    {
        if (root["data"] is not JsonObject proxies) return;
        foreach (var proxy in proxies)
        foreach (JsonObject entry in (proxy.Value as JsonArray ?? []).OfType<JsonObject>())
        {
            string? missionId = Scalar(entry["missionId"]);
            string? dialogId = Scalar(entry["dialogId"]);
            if (string.IsNullOrWhiteSpace(dialogId)
                || !string.IsNullOrWhiteSpace(missionFilter) && !string.Equals(missionId, missionFilter, StringComparison.OrdinalIgnoreCase)) continue;
            Evidence.Add(new StoryEvidence("npc-dialog-link", source, file, dialogId,
                new JsonObject { ["npcProxyId"] = proxy.Key, ["missionId"] = missionId }));
        }
    }

    private void AddLevelTriggerEvidence(byte[] data, string source, string file, string? missionFilter)
    {
        string text = Encoding.UTF8.GetString(data);
        var scenes = StoryIds(text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (scenes.Length == 0) return;
        foreach (StoryEvidence condition in Evidence.Where(e => e.Kind == "quest-condition"
                     && (string.IsNullOrWhiteSpace(missionFilter)
                         || string.Equals(Scalar(e.Detail?["missionId"]), missionFilter, StringComparison.OrdinalIgnoreCase))).ToArray())
        {
            string? uniqueId = Scalar(condition.Detail?["uniqueId"]);
            if (string.IsNullOrWhiteSpace(uniqueId) || !text.Contains(uniqueId, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string scene in scenes)
            {
                if (!string.IsNullOrWhiteSpace(missionFilter) && ContainsDifferentMissionId(scene, missionFilter)) continue;
                Evidence.Add(new StoryEvidence("trigger-scene-link", source, file, scene,
                    new JsonObject
                    {
                        ["questId"] = condition.Detail?["questId"]?.DeepClone(),
                        ["uniqueId"] = uniqueId,
                        ["missionId"] = condition.Detail?["missionId"]?.DeepClone(),
                        ["membershipEvidence"] = "file-level co-occurrence of condition uniqueId and scene id in LevelData",
                        ["scope"] = "file; action/record boundary not decoded",
                    }));
            }
        }
    }

    private void LoadLevelScripts(VfsLoader loader, string source, string? missionFilter)
    {
        BlockMainInfo info;
        try { info = loader.LoadBlockInfo(BlockType.JsonData); }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }
        foreach (ChunkInfo chunk in info.Chunks)
        foreach (AnimeStudio.Endfield.FileInfo file in chunk.Files)
        {
            if (!file.FileName.Contains("LevelScriptData", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                byte[] data = loader.ExtractFileToBytes(BlockType.JsonData, chunk, file);
                var decoded = LevelScriptEvidence.Decode(data)
                    .Select(chain => (Chain: chain, Hits: LevelScriptEvidence.SceneKeys(chain, null)))
                    .ToArray();
                bool relatedFile = string.IsNullOrWhiteSpace(missionFilter)
                    || decoded.SelectMany(item => item.Hits).Any(hit => Belongs(hit.SceneId, missionFilter));
                if (!relatedFile) continue;
                AddBinaryStoryReferences(data, source, file.FileName, "level-script-file-ref", missionFilter, groupLevelData: true);
                AddLevelScriptPositionEvidence(data, source, file.FileName, missionFilter);
                int chainIndex = 0;
                foreach (var item in decoded)
                {
                    bool chainBelongs = string.IsNullOrWhiteSpace(missionFilter)
                        || item.Hits.Any(hit => Belongs(hit.SceneId, missionFilter));
                    foreach (var hit in item.Hits)
                    {
                        bool directMission = string.IsNullOrWhiteSpace(missionFilter) || Belongs(hit.SceneId, missionFilter);
                        bool relatedByChain = !directMission && chainBelongs && !ContainsDifferentMissionId(hit.SceneId, missionFilter!);
                        bool relatedByFile = !directMission && !relatedByChain && relatedFile && !ContainsDifferentMissionId(hit.SceneId, missionFilter!);
                        Evidence.Add(new StoryEvidence("level-script", source, file.FileName, hit.SceneId,
                            new JsonObject
                            {
                                ["chain"] = chainIndex,
                                ["offset"] = hit.Offset,
                                ["missionId"] = directMission || relatedByChain || relatedByFile ? missionFilter : null,
                                ["coOccurrenceMissionId"] = directMission || relatedByChain || relatedByFile ? null : missionFilter,
                                ["membershipEvidence"] = directMission
                                    ? "scene id"
                                    : relatedByChain ? "same decoded LevelScript action chain as mission scene"
                                    : relatedByFile ? "same LevelScriptData file; no conflicting explicit mission id"
                                    : "LevelScriptData file co-occurrence only",
                            }));
                    }
                    chainIndex++;
                }
            }
            catch (Exception ex) { Warn($"LevelScriptData read failed ({source}/{file.FileName}): {ex.Message}"); }
        }
    }

    private static bool IsStoryTable(string name) => TableNames.Contains(name, StringComparer.Ordinal) || name.StartsWith("I18nTextTable_", StringComparison.OrdinalIgnoreCase);
    private static string Decode(byte[] data) => Encoding.UTF8.GetString(data.AsSpan(data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0));

    private void AddBinaryStoryReferences(byte[] data, string source, string file, string kind, string? missionFilter, bool groupLevelData)
    {
        string text = Encoding.UTF8.GetString(data);
        var matches = System.Text.RegularExpressions.Regex.Matches(text,
                @"(?:f_|m_|fm_)?(?:dlg|cutscene|cs_video|black|remotecomm|radio|sns)_[A-Za-z0-9_]+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Cast<System.Text.RegularExpressions.Match>()
            .ToArray();
        bool relatedFile = groupLevelData && !string.IsNullOrWhiteSpace(missionFilter)
            && (file.Contains(missionFilter, StringComparison.OrdinalIgnoreCase)
                || matches.Any(match => Belongs(match.Value, missionFilter)));
        foreach (var match in matches)
        {
            bool directMission = !string.IsNullOrWhiteSpace(missionFilter) && Belongs(match.Value, missionFilter);
            Evidence.Add(new StoryEvidence(kind, source, file, match.Value,
                new JsonObject
                {
                    ["offset"] = match.Index,
                    ["missionId"] = directMission ? missionFilter : null,
                    ["coOccurrenceMissionId"] = relatedFile && !directMission ? missionFilter : null,
                    ["membershipEvidence"] = directMission ? "scene id" : relatedFile ? "file co-occurrence only" : null,
                }));
        }
    }

    private static bool Belongs(string id, string mission)
        => id.Contains($"_{mission}_", StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("_" + mission, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsDifferentMissionId(string id, string mission)
        => System.Text.RegularExpressions.Regex.Matches(id, @"(?:^|_)[a-z]\d+m\d+(?:d\d+)?(?:_|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Value.Trim('_'))
            .Any(value => !value.Equals(mission, StringComparison.OrdinalIgnoreCase));

    private void AddLevelScriptPositionEvidence(byte[] data, string source, string file, string? missionFilter)
    {
        string text = Encoding.UTF8.GetString(data);
        string[] scenes = StoryIds(text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (scenes.Length == 0) return;
        foreach (StoryEvidence tracking in Evidence.Where(e => e.Kind == "quest-tracking-position"
                     && (string.IsNullOrWhiteSpace(missionFilter)
                         || string.Equals(Scalar(e.Detail?["missionId"]), missionFilter, StringComparison.OrdinalIgnoreCase))).ToArray())
        {
            if (!Float(tracking.Detail?["x"], out float x) || !Float(tracking.Detail?["y"], out float y) || !Float(tracking.Detail?["z"], out float z)) continue;
            int offset = FindVector3(data, x, y, z);
            if (offset < 0) continue;
            foreach (string scene in scenes)
            {
                if (!string.IsNullOrWhiteSpace(missionFilter) && ContainsDifferentMissionId(scene, missionFilter)) continue;
                Evidence.Add(new StoryEvidence("trigger-scene-link", source, file, scene,
                    new JsonObject
                    {
                        ["questId"] = tracking.Detail?["questId"]?.DeepClone(),
                        ["missionId"] = tracking.Detail?["missionId"]?.DeepClone(),
                        ["position"] = new JsonArray(x, y, z),
                        ["offset"] = offset,
                        ["membershipEvidence"] = "file-level co-occurrence of matching trackingPos and scene id in LevelScriptData",
                        ["scope"] = "file; action/record boundary not decoded",
                    }));
            }
        }
    }

    private static int FindVector3(byte[] data, float x, float y, float z)
    {
        for (int offset = 0; offset + 12 <= data.Length; offset++)
            if (Math.Abs(BitConverter.ToSingle(data, offset) - x) < 0.001f
                && Math.Abs(BitConverter.ToSingle(data, offset + 4) - y) < 0.001f
                && Math.Abs(BitConverter.ToSingle(data, offset + 8) - z) < 0.001f) return offset;
        return -1;
    }

    private static bool Float(JsonNode? node, out float value)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<float>(out value)) return true;
        return float.TryParse(Scalar(node), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private void AddRuntimeEvidence(JsonObject root, string source, string file, string missionId)
    {
        Visit(root, "$", null);
        void Visit(JsonNode? node, string path, string? property)
        {
            if (node is JsonObject map)
            {
                if (map["questDic"] is JsonObject quests)
                    foreach (var quest in quests)
                    {
                        var previous = quest.Value?["prevQuestIdList"] as JsonArray;
                        if (previous is null) continue;
                        foreach (JsonNode? prev in previous)
                            Evidence.Add(new StoryEvidence("quest", source, file, null, new JsonObject
                            {
                                ["fromQuestId"] = Scalar(prev), ["toQuestId"] = quest.Key,
                                ["path"] = path + ".questDic." + quest.Key + ".prevQuestIdList",
                                ["missionId"] = missionId,
                            }));
                        foreach (string uniqueId in PropertyStrings(quest.Value, "uniqueId"))
                            Evidence.Add(new StoryEvidence("quest-condition", source, file, null, new JsonObject
                            {
                                ["questId"] = quest.Key, ["uniqueId"] = uniqueId, ["missionId"] = missionId,
                            }));
                        foreach (string npcProxyId in PropertyStrings(quest.Value, "npcProxyId"))
                            Evidence.Add(new StoryEvidence("quest-tracking-npc", source, file, null, new JsonObject
                            {
                                ["questId"] = quest.Key, ["npcProxyId"] = npcProxyId, ["missionId"] = missionId,
                            }));
                        foreach (JsonObject position in PropertyNodes(quest.Value, "trackingPos").OfType<JsonObject>())
                            Evidence.Add(new StoryEvidence("quest-tracking-position", source, file, null, new JsonObject
                            {
                                ["questId"] = quest.Key,
                                ["x"] = position["x"]?.DeepClone(),
                                ["y"] = position["y"]?.DeepClone(),
                                ["z"] = position["z"]?.DeepClone(),
                                ["missionId"] = missionId,
                            }));
                    }
                foreach (var pair in map) Visit(pair.Value, path + "." + pair.Key, pair.Key);
                return;
            }
            if (node is JsonArray list)
            {
                for (int index = 0; index < list.Count; index++) Visit(list[index], path + "[" + index + "]", property);
                return;
            }
            if (node is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null)
            {
                foreach (var scene in StoryIds(text))
                    Evidence.Add(new StoryEvidence("client-action-story-ref", source, file, scene, new JsonObject
                    {
                        ["path"] = path, ["property"] = property,
                        ["value"] = text,
                        ["missionId"] = missionId,
                    }));
            }
        }
    }

    private static IEnumerable<string> PropertyStrings(JsonNode? node, string property)
    {
        if (node is JsonObject map)
        {
            foreach (var pair in map)
            {
                if (pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase) && Scalar(pair.Value) is string value)
                    yield return value;
                foreach (string nested in PropertyStrings(pair.Value, property)) yield return nested;
            }
        }
        else if (node is JsonArray array)
            foreach (JsonNode? item in array)
                foreach (string nested in PropertyStrings(item, property)) yield return nested;
    }

    private static IEnumerable<JsonNode?> PropertyNodes(JsonNode? node, string property)
    {
        if (node is JsonObject map)
        {
            foreach (var pair in map)
            {
                if (pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase)) yield return pair.Value;
                foreach (JsonNode? nested in PropertyNodes(pair.Value, property)) yield return nested;
            }
        }
        else if (node is JsonArray array)
            foreach (JsonNode? item in array)
                foreach (JsonNode? nested in PropertyNodes(item, property)) yield return nested;
    }

    private static IEnumerable<string> StoryIds(string value)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(value,
            @"(?:dlg|radio|remotecomm|sns|cutscene|cs_video|black)_[A-Za-z0-9_]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            yield return match.Value;
    }
    private static string? Scalar(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString().Trim('"');
    private void Warn(string message) { if (Warnings.Count < 100) Warnings.Add(message); }
}

internal sealed record StoryEvidence(string Kind, string Source, string File, string? SceneId, JsonNode? Detail);
