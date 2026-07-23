using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;

namespace AnimeStudio.Endfield.Cli;

/// <summary>
/// Builds a unified, table-backed story document. This is intentionally
/// separate from <see cref="DialogExporter"/> so the existing dialog output
/// remains backward compatible while story-specific scene types are added.
/// </summary>
internal static class StoryExporter
{
    private static readonly Regex DialogLinePattern = new(
        @"^dlg_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)_(?<line>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CutsceneLinePattern = new(
        @"^(?<group>cutscene_.+)_(?<line>\d+)(?<sub>d\d+)?(?<gender>_[fm])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BlackLinePattern = new(
        @"^(?<group>black_.+_\d+(?:d\d+)?)_(?<line>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RadioScenePattern = new(
        @"^(?<kind>radio|remotecomm|sns)_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SceneMissionPattern = new(
        @"^(?:dlg|cutscene|black|sns|remotecomm|radio)_(?<mission>.+?)_(?<scene>\d+(?:d\d+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SceneTextPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cutscene_", "black_", "sns_", "remotecomm_", "radio_",
    };

    private sealed class Options
    {
        public string? VfsPath;
        public string? BaseVfsPath;
        public string? OutPath;
        public string Language = "JP";
        public string? MissionId;
        public string? SceneId;
        public bool Snapshot;
        public bool NoTimeline;
        public bool Help;
    }

    private sealed class TableSet
    {
        public Dictionary<string, JsonObject> Tables { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Sources { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> MissionRuntimeAssets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<StoryReference> RuntimeStoryReferences { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    private sealed record StoryReference(string Value, string Source, int Offset, string EvidenceKind);

    public static int Run(ReadOnlySpan<string> args)
    {
        var options = ParseArgs(args);
        if (options.Help) return 0;
        if (options.VfsPath is null) throw new ArgumentException("--vfs is required");
        if (options.OutPath is null) throw new ArgumentException("--out is required");

        options.Language = options.Language.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(options.Language, @"^[A-Z]{2,8}$|^ALL$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"Invalid --language value: {options.Language}");

        var tableSet = LoadTables(options);
        var languages = ResolveLanguages(options.Language, tableSet);
        var missions = CollectMissionIds(tableSet, options);
        if (missions.Count == 0)
            throw new InvalidOperationException("No story scenes were found in TextTable/DialogTextTable/RadioTable/RemoteCommonTable/SNSDialogTable.");

        Directory.CreateDirectory(options.OutPath);
        var cutsceneTimelineEvidence = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        var dialogTimelineEvidence = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        var dialogTreeEvidence = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!options.NoTimeline)
            {
                string scratchPath = Path.Combine(Path.GetTempPath(), "endfield-story-timeline");
                var timelineScan = DialogTimelineScanner.Scan(options.VfsPath!, options.BaseVfsPath, scratchPath);
                cutsceneTimelineEvidence = timelineScan.CutscenesBySceneId;
                dialogTimelineEvidence = timelineScan.ByDialogId;
                dialogTreeEvidence = timelineScan.DialogTreesByDialogId;
                Console.WriteLine($"  Cutscene Timeline evidence: {timelineScan.ScenesWithCutsceneEvidence:N0} scene(s), "
                    + $"{timelineScan.RootsFound:N0} roots, {timelineScan.GraphObjectsRead:N0} graph objects");
            }
        }
        catch (DirectoryNotFoundException)
        {
            tableSet.Warnings.Add("Bundle block was not found; cutscene Timeline evidence was skipped.");
        }
        catch (FileNotFoundException ex)
        {
            tableSet.Warnings.Add($"Cutscene Timeline evidence was skipped: {ex.Message}");
        }
        catch (Exception ex)
        {
            tableSet.Warnings.Add($"Cutscene Timeline scan failed; table/runtime export was retained: {ex.Message}");
        }
        string? snapshotTimestamp = options.Snapshot
            ? DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            : null;
        int written = 0;
        foreach (string language in languages)
        {
            foreach (string missionId in missions)
            {
                JsonObject payload = BuildMission(
                    tableSet,
                    missionId,
                    language,
                    options.SceneId,
                    cutsceneTimelineEvidence,
                    dialogTimelineEvidence,
                    dialogTreeEvidence);
                if (((JsonArray)payload["scenes"]!).Count == 0) continue;
                WriteMission(options.OutPath, missionId, language, payload, snapshotTimestamp);
                written++;
            }
        }

        Console.WriteLine($"  Wrote {written:N0} story file(s) to {Path.GetFullPath(options.OutPath)}");
        if (tableSet.Warnings.Count > 0)
            Console.WriteLine($"  Warnings: {tableSet.Warnings.Count:N0}");
        return 0;
    }

    private static Options ParseArgs(ReadOnlySpan<string> args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--vfs": options.VfsPath = RequireValue(args, ref i, arg); break;
                case "--base-vfs": options.BaseVfsPath = RequireValue(args, ref i, arg); break;
                case "--out":
                case "-o": options.OutPath = RequireValue(args, ref i, arg); break;
                case "--language": options.Language = RequireValue(args, ref i, arg); break;
                case "--mission": options.MissionId = RequireValue(args, ref i, arg); break;
                case "--scene": options.SceneId = RequireValue(args, ref i, arg); break;
                case "--snapshot": options.Snapshot = true; break;
                case "--no-timeline": options.NoTimeline = true; break;
                case "--help":
                case "-h":
                    PrintHelp();
                    options.Help = true;
                    return options;
                default: throw new ArgumentException($"Unknown argument: {arg}");
            }
        }
        return options;
    }

    private static string RequireValue(ReadOnlySpan<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value");
        return args[++index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  endfield-dump story --vfs <path> --out <dir> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --base-vfs <path>    Base VFS used for hot-update fallback");
        Console.WriteLine("  --language <code|all> Localization code (default: JP; all=every available language)");
        Console.WriteLine("  --mission <id>       Export one mission, for example e11m7");
        Console.WriteLine("  --scene <id>         Export one scene within the selected mission");
        Console.WriteLine("  --snapshot           Write <language>_snapshot<timestamp>.json");
        Console.WriteLine("  --no-timeline        Skip expensive Bundle Timeline scanning (table/runtime evidence only)");
    }

    private static TableSet LoadTables(Options options)
    {
        var result = new TableSet();
        var primary = new VfsLoader(options.VfsPath!, Keys.ChaCha20Key, options.BaseVfsPath);
        LoadTableSource(primary, "primary", result);
        LoadMissionRuntimeAssets(primary, "primary", result);
        LoadRuntimeStoryReferences(primary, "primary", result, options.MissionId);
        return result;
    }

    private static void LoadRuntimeStoryReferences(
        VfsLoader loader,
        string sourceName,
        TableSet result,
        string? missionFilter)
    {
        BlockMainInfo info;
        try
        {
            info = loader.LoadBlockInfo(BlockType.JsonData);
        }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }

        foreach (ChunkInfo chunk in info.Chunks)
        foreach (AnimeStudio.Endfield.FileInfo file in chunk.Files)
        {
            string normalizedPath = file.FileName.Replace('\\', '/');
            string evidenceKind = normalizedPath.Contains("LevelScriptData", StringComparison.OrdinalIgnoreCase)
                ? "LevelScriptData"
                : normalizedPath.Contains("LevelData", StringComparison.OrdinalIgnoreCase)
                    ? "LevelData"
                : normalizedPath.Contains("NpcProxyExDataTable", StringComparison.OrdinalIgnoreCase)
                    ? "NpcProxyExDataTable"
                    : "";
            if (evidenceKind.Length == 0) continue;
            try
            {
                string text = System.Text.Encoding.UTF8.GetString(loader.ExtractFileToBytes(BlockType.JsonData, chunk, file));
                foreach (Match match in Regex.Matches(
                             text,
                             @"(?<![A-Za-z0-9_])(?:f_|m_|fm_)?(?:dlg|cutscene|black|remotecomm|radio|sns)_[A-Za-z0-9_]+",
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    string value = NormalizeRuntimeReference(match.Value);
                    if (value.Length == 0) continue;
                    if (!string.IsNullOrWhiteSpace(missionFilter)
                        && !IsMissionReference(value, missionFilter.Trim())) continue;
                    result.RuntimeStoryReferences.Add(new StoryReference(
                        value,
                        $"{sourceName}:{normalizedPath}",
                        match.Index,
                        evidenceKind));
                }
            }
            catch (FileNotFoundException)
            {
                // Persistent metadata can reference chunks that only exist in
                // the base VFS. Missing optional evidence is not a fatal
                // story-export error and should not flood the warning list.
            }
            catch (Exception ex)
            {
                if (result.Warnings.Count < 20)
                    result.Warnings.Add($"Runtime story scan failed ({sourceName}/{file.FileName}): {ex.Message}");
            }
        }
    }

    private static string NormalizeRuntimeReference(string value)
    {
        string normalized = NormalizeStoryRef(value);
        foreach (string suffix in new[] { "Played", "_Played", "_Done", "_done", "_finished", "_Finished" })
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^suffix.Length];
        return normalized;
    }

    private static void LoadMissionRuntimeAssets(VfsLoader loader, string sourceName, TableSet result)
    {
        BlockMainInfo info;
        try
        {
            info = loader.LoadBlockInfo(BlockType.JsonData);
        }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }

        foreach (ChunkInfo chunk in info.Chunks)
        foreach (AnimeStudio.Endfield.FileInfo file in chunk.Files)
        {
            if (!file.FileName.Contains("MissionRuntimeAsset", StringComparison.OrdinalIgnoreCase)
                || !file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                string missionId = Path.GetFileNameWithoutExtension(file.FileName);
                JsonNode? parsed = JsonNode.Parse(DecodeJson(loader.ExtractFileToBytes(BlockType.JsonData, chunk, file)));
                if (parsed is not JsonObject mission) continue;
                // Persistent is the authoritative hot-update source. Do not
                // overwrite it with the base VFS when both contain a mission.
                if (result.MissionRuntimeAssets.ContainsKey(missionId)
                    && sourceName.Equals("base", StringComparison.Ordinal))
                    continue;
                result.MissionRuntimeAssets[missionId] = mission;
            }
            catch (FileNotFoundException)
            {
                // The block index may reference an optional hot-update chunk
                // that is absent from this VFS root.
            }
            catch (Exception ex)
            {
                if (result.Warnings.Count < 20)
                    result.Warnings.Add($"MissionRuntimeAsset parse failed ({sourceName}/{file.FileName}): {ex.Message}");
            }
        }
    }

    private static string DecodeJson(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static void LoadTableSource(VfsLoader loader, string sourceName, TableSet result)
    {
        BlockMainInfo info;
        try
        {
            info = loader.LoadBlockInfo(BlockType.Table);
        }
        catch (DirectoryNotFoundException) { return; }
        catch (FileNotFoundException) { return; }

        foreach (ChunkInfo chunk in info.Chunks)
        foreach (AnimeStudio.Endfield.FileInfo file in chunk.Files)
        {
            try
            {
                var (rootName, json) = SparkBuffer.Parse(loader.ExtractFileToBytes(BlockType.Table, chunk, file));
                if (!IsStoryTable(rootName) || JsonNode.Parse(json) is not JsonObject incoming)
                    continue;

                if (!result.Tables.TryGetValue(rootName, out var target))
                {
                    target = new JsonObject();
                    result.Tables[rootName] = target;
                }
                foreach (var property in incoming)
                    target[property.Key] = property.Value?.DeepClone();

                if (!result.Sources.TryGetValue(rootName, out var sources))
                {
                    sources = new List<string>();
                    result.Sources[rootName] = sources;
                }
                if (!sources.Contains(sourceName, StringComparer.Ordinal))
                    sources.Add(sourceName);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Story table parse failed ({sourceName}): {ex.Message}");
            }
        }
    }

    private static bool IsStoryTable(string rootName)
        => rootName is "TextTable" or "DialogTextTable" or "DialogOptionTable" or "DialogSummaryTable"
            || rootName is "RadioTable" or "RemoteCommonTable" or "SNSDialogTable" or "SNSDialogOptionTable"
            || rootName.StartsWith("I18nTextTable_", StringComparison.Ordinal);

    private static List<string> ResolveLanguages(string requested, TableSet tableSet)
    {
        if (!string.Equals(requested, "ALL", StringComparison.OrdinalIgnoreCase))
            return new List<string> { requested };
        return tableSet.Tables.Keys
            .Where(name => name.StartsWith("I18nTextTable_", StringComparison.Ordinal))
            .Select(name => name["I18nTextTable_".Length..])
            .Where(code => Regex.IsMatch(code, @"^[A-Z]{2,8}$", RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> CollectMissionIds(TableSet tableSet, Options options)
    {
        var missions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in GetTable(tableSet, "TextTable").Keys)
        {
            if (TryParseSceneTextKey(key, out _, out string? mission, out _, out _))
                missions.Add(mission!);
        }
        foreach (string key in GetTable(tableSet, "DialogTextTable").Keys)
        {
            Match match = DialogLinePattern.Match(key);
                if (match.Success) missions.Add(match.Groups["mission"].Value);
        }
        foreach (string tableName in new[] { "RadioTable", "RemoteCommonTable", "SNSDialogTable" })
        foreach (string key in GetTable(tableSet, tableName).Keys)
        {
            Match match = RadioScenePattern.Match(key);
            if (match.Success) missions.Add(match.Groups["mission"].Value);
        }
        if (!string.IsNullOrWhiteSpace(options.MissionId))
            missions.RemoveWhere(mission => !mission.Equals(options.MissionId, StringComparison.OrdinalIgnoreCase));
        return missions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static JsonObject BuildMission(
        TableSet tableSet,
        string missionId,
        string language,
        string? sceneFilter,
        Dictionary<string, JsonArray> cutsceneTimelineEvidence,
        Dictionary<string, JsonArray> dialogTimelineEvidence,
        Dictionary<string, JsonArray> dialogTreeEvidence)
    {
        var textRows = GetTable(tableSet, "TextTable");
        var dialogRows = GetTable(tableSet, "DialogTextTable");
        var optionRows = GetTable(tableSet, "DialogOptionTable");
        var summaryRows = GetTable(tableSet, "DialogSummaryTable");
        var radioRows = GetTable(tableSet, "RadioTable");
        var remoteRows = GetTable(tableSet, "RemoteCommonTable");
        var snsRows = GetTable(tableSet, "SNSDialogTable");
        var snsOptionRows = GetTable(tableSet, "SNSDialogOptionTable");
        var i18nRows = GetTable(tableSet, $"I18nTextTable_{language}");
        var scenes = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rowId, rowNode) in dialogRows)
        {
            Match match = DialogLinePattern.Match(rowId);
            if (!match.Success || !match.Groups["mission"].Value.Equals(missionId, StringComparison.OrdinalIgnoreCase))
                continue;
            string sceneId = $"dlg_{match.Groups["mission"].Value}_{match.Groups["scene"].Value}";
            if (!SceneMatches(sceneId, sceneFilter)) continue;
            JsonObject scene = GetOrCreateScene(scenes, sceneId, "dlg", missionId, match.Groups["scene"].Value);
            if (rowNode is not JsonObject row) continue;
            ((JsonArray)scene["lines"]!).Add(new JsonObject
            {
                ["id"] = rowId,
                ["order"] = ParseInt(match.Groups["line"].Value),
                ["actorId"] = ScalarString(row["actorNameId"]),
                ["actor"] = ResolveText(row["actorName"], i18nRows),
                ["text"] = ResolveText(row["dialogText"], i18nRows),
                ["hint"] = ResolveText(row["hint"], i18nRows),
                ["audio"] = ScalarString(row["audioOverride"]),
                ["emotion"] = row["emotionType"]?.DeepClone(),
                ["source"] = SourceRef("DialogTextTable", rowId, tableSet),
            });
        }

        foreach (var (rowId, rowNode) in optionRows)
        {
            if (rowNode is not JsonObject row) continue;
            Match match = Regex.Match(rowId, @"^option_(?<dialog>dlg_.+_\d+(?:d\d+)?)_(?<group>\d+)_(?<option>\d+)$", RegexOptions.IgnoreCase);
            if (!match.Success || !match.Groups["dialog"].Value.StartsWith($"dlg_{missionId}_", StringComparison.OrdinalIgnoreCase))
                continue;
            string sceneId = match.Groups["dialog"].Value;
            if (!SceneMatches(sceneId, sceneFilter)) continue;
            JsonObject scene = GetOrCreateScene(scenes, sceneId, "dlg", missionId, SceneSuffix(sceneId));
            var groups = (JsonArray)scene["optionGroups"]!;
            int groupOrder = ParseInt(match.Groups["group"].Value);
            JsonObject? group = groups.OfType<JsonObject>().FirstOrDefault(item => item["order"]?.GetValue<int>() == groupOrder);
            if (group is null)
            {
                group = new JsonObject { ["order"] = groupOrder, ["options"] = new JsonArray() };
                groups.Add(group);
            }
            ((JsonArray)group["options"]!).Add(new JsonObject
            {
                ["id"] = rowId,
                ["order"] = ParseInt(match.Groups["option"].Value),
                ["text"] = ResolveText(row["optionText"] ?? row["optionDesc"], i18nRows),
                ["source"] = SourceRef("DialogOptionTable", rowId, tableSet),
            });
        }

        foreach (var (rowId, rowNode) in summaryRows)
        {
            Match match = Regex.Match(rowId, @"^summary_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)_(?<order>\d+)$", RegexOptions.IgnoreCase);
            if (!match.Success || !match.Groups["mission"].Value.Equals(missionId, StringComparison.OrdinalIgnoreCase))
                continue;
            string sceneId = $"dlg_{missionId}_{match.Groups["scene"].Value}";
            if (!SceneMatches(sceneId, sceneFilter) || rowNode is not JsonObject row) continue;
            JsonObject scene = GetOrCreateScene(scenes, sceneId, "dlg", missionId, match.Groups["scene"].Value);
            ((JsonArray)scene["summary"]!).Add(new JsonObject
            {
                ["id"] = rowId,
                ["order"] = ParseInt(match.Groups["order"].Value),
                ["text"] = ResolveText(row["id"], i18nRows),
                ["source"] = SourceRef("DialogSummaryTable", rowId, tableSet),
            });
        }

        foreach (var (sceneId, rowNode) in radioRows)
        {
            Match match = RadioScenePattern.Match(sceneId);
            if (!match.Success || !match.Groups["mission"].Value.Equals(missionId, StringComparison.OrdinalIgnoreCase)
                || rowNode is not JsonObject row || !SceneMatches(sceneId, sceneFilter))
                continue;
            JsonObject scene = GetOrCreateScene(scenes, sceneId, match.Groups["kind"].Value, missionId, match.Groups["scene"].Value);
            if (row["radioSingleDataList"] is not JsonArray entries) continue;
            foreach (JsonObject entry in entries.OfType<JsonObject>())
            {
                string? lineId = ScalarString(entry["id"]);
                if (string.IsNullOrWhiteSpace(lineId)) lineId = $"{sceneId}_{ScalarString(entry["index"]) ?? "0"}";
                ((JsonArray)scene["lines"]!).Add(new JsonObject
                {
                    ["id"] = lineId,
                    ["order"] = ParseInt(ScalarString(entry["index"]) ?? "0"),
                    ["actorId"] = ScalarString(entry["actorNameId"]),
                    ["actor"] = ResolveText(entry["actorName"], i18nRows),
                    ["text"] = ResolveText(entry["radioText"], i18nRows),
                    ["audio"] = ScalarString(entry["audioOverride"]),
                    ["emotion"] = entry["emotionType"]?.DeepClone(),
                    ["source"] = SourceRef("RadioTable", sceneId, tableSet),
                });
            }
        }

        foreach (var (sceneId, rowNode) in remoteRows)
        {
            Match match = RadioScenePattern.Match(sceneId);
            if (!match.Success || !match.Groups["mission"].Value.Equals(missionId, StringComparison.OrdinalIgnoreCase)
                || rowNode is not JsonObject row || !SceneMatches(sceneId, sceneFilter)) continue;
            JsonObject scene = GetOrCreateScene(scenes, sceneId, "remotecomm", missionId, match.Groups["scene"].Value);
            if (row["remoteCommSingleDataList"] is not JsonArray entries) continue;
            foreach (JsonObject entry in entries.OfType<JsonObject>())
                ((JsonArray)scene["lines"]!).Add(new JsonObject
                {
                    ["id"] = ScalarString(entry["singleId"]),
                    ["order"] = ParseInt(ScalarString(entry["index"]) ?? "0"),
                    ["actor"] = ResolveText(entry["actorName"], i18nRows),
                    ["text"] = ResolveText(entry["remoteCommText"], i18nRows),
                    ["audio"] = ScalarString(entry["voiceId"]),
                    ["source"] = SourceRef("RemoteCommonTable", sceneId, tableSet),
                });
        }

        foreach (var (sceneId, rowNode) in snsRows)
        {
            Match match = RadioScenePattern.Match(sceneId);
            if (!match.Success || !match.Groups["mission"].Value.Equals(missionId, StringComparison.OrdinalIgnoreCase)
                || rowNode is not JsonObject row || !SceneMatches(sceneId, sceneFilter)) continue;
            JsonObject scene = GetOrCreateScene(scenes, sceneId, "sns", missionId, match.Groups["scene"].Value);
            if (row["dialogContentData"] is not JsonObject contentMap) continue;
            var contents = contentMap.Select(pair => pair.Value)
                .OfType<JsonObject>()
                .Where(content => content["contentId"]?.GetValue<int>() >= 0)
                .OrderBy(content => content["contentId"]?.GetValue<int>() ?? int.MaxValue)
                .ToArray();
            foreach (JsonObject content in contents)
            {
                JsonObject? localized = content["content"] as JsonObject;
                ((JsonArray)scene["lines"]!).Add(new JsonObject
                {
                    ["id"] = $"{sceneId}_{content["contentId"]?.GetValue<int>() ?? 0:000}",
                    ["order"] = content["contentId"]?.GetValue<int>() ?? 0,
                    ["speaker"] = ScalarString(content["speaker"]),
                    ["text"] = ResolveText(localized, i18nRows),
                    ["source"] = SourceRef("SNSDialogTable", sceneId, tableSet),
                });
                if (content["dialogOptionIds"] is JsonArray optionIds)
                    foreach (string? optionId in optionIds.Select(ScalarString))
                        if (optionId is not null && snsOptionRows.TryGetValue(optionId, out JsonNode? optionNode)
                            && optionNode is JsonObject option)
                            ((JsonArray)scene["optionGroups"]!).Add(new JsonObject
                            {
                                ["order"] = content["contentId"]?.GetValue<int>() ?? 0,
                                ["options"] = new JsonArray(new JsonObject
                                {
                                    ["id"] = optionId,
                                    ["order"] = ScalarString(option["optionNextContentId"]),
                                    ["text"] = ResolveText(option["optionDesc"], i18nRows),
                                    ["nextContentId"] = option["optionNextContentId"]?.DeepClone(),
                                    ["source"] = SourceRef("SNSDialogOptionTable", optionId, tableSet),
                                }),
                            });
            }
        }

        foreach (var (rowId, rowNode) in textRows)
        {
            if (!TryParseSceneTextKey(rowId, out string? kind, out string? rowMission, out string? sceneId, out int lineOrder)
                || !rowMission!.Equals(missionId, StringComparison.OrdinalIgnoreCase)
                || rowNode is not JsonObject row)
                continue;
            string fullSceneId = $"{kind}_{rowMission}_{sceneId}";
            if (!SceneMatches(fullSceneId, sceneFilter)) continue;
            JsonObject scene = GetOrCreateScene(scenes, fullSceneId, kind!, missionId, sceneId!);
            ((JsonArray)scene["lines"]!).Add(new JsonObject
            {
                ["id"] = rowId,
                ["order"] = lineOrder,
                ["text"] = ResolveText(row["id"], i18nRows),
                ["source"] = SourceRef("TextTable", rowId, tableSet),
            });
        }

        AttachCutsceneTimelineEvidence(scenes, textRows, cutsceneTimelineEvidence);
        AttachDialogEvidence(scenes, dialogTimelineEvidence, dialogTreeEvidence);

        var runtimeReferences = tableSet.RuntimeStoryReferences
            .Where(reference => IsMissionReference(reference.Value, missionId))
            .OrderBy(reference => reference.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Offset)
            .ToList();
        foreach (StoryReference reference in runtimeReferences)
        {
            string? sceneId = ResolveRuntimeSceneId(reference.Value, scenes.Keys);
            if (sceneId is null || !SceneMatches(sceneId, sceneFilter)) continue;
            if (!scenes.ContainsKey(sceneId))
            {
                string kind = SceneKind(sceneId);
                GetOrCreateScene(scenes, sceneId, kind, missionId, SceneSuffix(sceneId));
            }
        }

        var sceneArray = new JsonArray();
        foreach (JsonObject scene in scenes.Values.OrderBy(SceneSortKey))
        {
            SortArray(scene["lines"] as JsonArray, "order");
            SortArray(scene["summary"] as JsonArray, "order");
            SortArray(scene["optionGroups"] as JsonArray, "order");
            foreach (JsonObject group in (scene["optionGroups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                SortArray(group["options"] as JsonArray, "order");
            sceneArray.Add(scene);
        }

        string? missionName = ResolveText(textRows.GetValueOrDefault($"{missionId}_name"), i18nRows);
        JsonObject? flow = tableSet.MissionRuntimeAssets.TryGetValue(missionId, out var runtimeAsset)
            ? BuildMissionFlow(runtimeAsset, missionId, scenes.Keys)
            : null;
        JsonObject order = BuildSceneOrder(sceneArray, flow, runtimeReferences);
        var warnings = new JsonArray();
        if (flow is null)
            warnings.Add("MissionRuntimeAsset was not found; cross-scene order uses fallback sorting.");
        else if (((JsonArray)order["edges"]!).Count == 0)
            warnings.Add("MissionRuntimeAsset was found, but it did not connect any exported scenes.");
        if (runtimeReferences.Count == 0)
            warnings.Add("No LevelScriptData/NpcProxyExDataTable story-reference evidence was found for this mission.");
        warnings.Add("Cutscene subtitle Timeline evidence is not attached yet.");
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = "story",
            ["mission"] = missionId,
            ["language"] = language,
            ["missionName"] = missionName,
            ["scenes"] = sceneArray,
            ["flow"] = flow,
            ["order"] = order,
            ["sources"] = new JsonObject
            {
                ["tables"] = new JsonArray(tableSet.Sources.Keys.OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => JsonValue.Create(value)).ToArray()),
            },
            ["warnings"] = warnings,
        };
    }

    private static JsonObject BuildMissionFlow(JsonObject raw, string missionId, IEnumerable<string> exportedSceneIds)
    {
        var quests = new List<(string Id, int FlowIndex, List<string> Prev, List<string> Refs, JsonObject Raw)>();
        if (raw["questDic"] is JsonObject questDic)
        {
            foreach (var pair in questDic)
            {
                if (pair.Value is not JsonObject quest) continue;
                string id = ScalarString(quest["questId"]) ?? pair.Key;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var refs = ExtractStoryRefs(quest)
                    .Where(reference => reference.StartsWith("dlg_", StringComparison.OrdinalIgnoreCase)
                        || reference.StartsWith("cutscene_", StringComparison.OrdinalIgnoreCase)
                        || reference.StartsWith("black_", StringComparison.OrdinalIgnoreCase)
                        || reference.StartsWith("remotecomm_", StringComparison.OrdinalIgnoreCase)
                        || reference.StartsWith("radio_", StringComparison.OrdinalIgnoreCase)
                        || reference.StartsWith("sns_", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var prev = ReadStringArray(quest["prevQuestIdList"]);
                int flowIndex = ParseInt(ScalarString(quest["flowIndex"]) ?? "0");
                quests.Add((id, flowIndex, prev, refs, quest));
            }
        }

        var questIds = quests.Select(quest => quest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loops = new JsonArray();

        int Depth(string id, List<string>? stack = null)
        {
            if (depth.TryGetValue(id, out int known)) return known;
            stack ??= new List<string>();
            if (!visiting.Add(id))
            {
                int start = stack.FindIndex(value => value.Equals(id, StringComparison.OrdinalIgnoreCase));
                var loop = new JsonArray();
                foreach (string value in (start >= 0 ? stack.Skip(start) : stack).Append(id)) loop.Add(value);
                loops.Add(new JsonObject { ["questIds"] = loop });
                return 0;
            }
            var quest = quests.FirstOrDefault(value => value.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            stack.Add(id);
            var previousDepths = quest.Prev.Where(questIds.Contains).Select(prev => Depth(prev, stack)).ToList();
            stack.RemoveAt(stack.Count - 1);
            visiting.Remove(id);
            int result = previousDepths.Count == 0 ? 0 : previousDepths.Max() + 1;
            depth[id] = result;
            return result;
        }

        foreach (var quest in quests) Depth(quest.Id);
        var exported = exportedSceneIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sceneQuestRefs = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        var groups = new JsonArray();
        var edges = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in quests.OrderBy(value => depth.GetValueOrDefault(value.Id)).ThenBy(value => value.FlowIndex).ThenBy(value => value.Id, StringComparer.OrdinalIgnoreCase))
        {
            var refs = quest.Refs.Where(exported.Contains).ToList();
            int layer = depth.GetValueOrDefault(quest.Id);
            for (int index = 0; index < refs.Count; index++)
            {
                orderMap.TryAdd(refs[index], layer * 1000 + index);
                if (!sceneQuestRefs.TryGetValue(refs[index], out var refsForScene))
                    sceneQuestRefs[refs[index]] = refsForScene = new JsonArray();
                if (!refsForScene.Any(value => value?.GetValue<string>()?.Equals(quest.Id, StringComparison.OrdinalIgnoreCase) == true))
                    refsForScene.Add(quest.Id);
            }
            var group = new JsonObject
            {
                ["questId"] = quest.Id,
                ["flowIndex"] = quest.FlowIndex,
                ["layer"] = layer,
                ["prevQuestIds"] = new JsonArray(quest.Prev.Select(value => JsonValue.Create(value)).ToArray()),
                ["sceneIds"] = new JsonArray(refs.Select(value => JsonValue.Create(value)).ToArray()),
                ["parallelLayer"] = quests.Count(value => depth.GetValueOrDefault(value.Id) == layer) > 1,
                ["source"] = "MissionRuntimeAsset.questDic[*]",
            };
            string? descriptionKey = ScalarString(quest.Raw["descriptionOverride"]?["key"]);
            if (!string.IsNullOrWhiteSpace(descriptionKey)) group["descriptionOverrideKey"] = descriptionKey;
            var objectiveDescriptions = new JsonArray();
            if (quest.Raw["objectiveList"] is JsonArray objectives)
            foreach (JsonObject objective in objectives.OfType<JsonObject>())
            {
                string? key = ScalarString(objective["description"]?["key"]);
                if (!string.IsNullOrWhiteSpace(key)) objectiveDescriptions.Add(key);
                if (objective["multipleDescription"] is JsonArray multiple)
                    foreach (JsonObject item in multiple.OfType<JsonObject>())
                    {
                        key = ScalarString(item["key"]);
                        if (!string.IsNullOrWhiteSpace(key)) objectiveDescriptions.Add(key);
                    }
            }
            if (objectiveDescriptions.Count > 0) group["objectiveDescriptionKeys"] = objectiveDescriptions;
            var tracking = CollectTrackingHints(quest.Raw);
            if (tracking.Count > 0) group["tracking"] = tracking;
            if (quest.Refs.Count > 0)
                group["storyReferences"] = new JsonArray(quest.Refs.Select(value => JsonValue.Create(value)).ToArray());
            groups.Add(group);
            if (refs.Count == 0) continue;
            foreach (string previousId in quest.Prev)
            {
                var previous = quests.FirstOrDefault(value => value.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase));
                if (previous.Id is null) continue;
                var previousRefs = previous.Refs.Where(exported.Contains).ToList();
                if (previousRefs.Count == 0) continue;
                string from = previousRefs[^1];
                string to = refs[0];
                if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) continue;
                string edgeKey = $"{from}\u001f{to}";
                if (!edges.TryGetValue(edgeKey, out var edge))
                {
                    edge = new JsonObject
                    {
                        ["from"] = from,
                        ["to"] = to,
                        ["kind"] = "questPrev",
                        ["questIds"] = new JsonArray(),
                        ["source"] = "MissionRuntimeAsset.questDic[*].prevQuestIdList",
                    };
                    edges[edgeKey] = edge;
                }
                var edgeQuestIds = (JsonArray)edge["questIds"]!;
                foreach (string edgeQuestId in new[] { previous.Id, quest.Id })
                    if (!edgeQuestIds.Any(value => value?.GetValue<string>()?.Equals(edgeQuestId, StringComparison.OrdinalIgnoreCase) == true))
                        edgeQuestIds.Add(edgeQuestId);
            }
        }

        return new JsonObject
        {
            ["missionId"] = missionId,
            ["levelId"] = ScalarString(raw["levelId"]),
            ["questCount"] = quests.Count,
            ["quests"] = groups,
            ["source"] = "MissionRuntimeAsset.questDic[*]",
            ["edges"] = new JsonArray(edges.Values.Select(edge => edge.DeepClone()).ToArray()),
            ["warnings"] = loops.Count == 0 ? new JsonArray() : new JsonArray("Quest predecessor cycle detected; affected depths are best effort."),
        };
    }

    private static JsonObject BuildSceneOrder(JsonArray scenes, JsonObject? flow, List<StoryReference> runtimeReferences)
    {
        var sceneIds = scenes.OfType<JsonObject>().Select(scene => scene["id"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runtimeOrder = new List<string>();
        var runtimeEvidence = new JsonArray();
        string levelId = flow?["levelId"]?.GetValue<string>() ?? "";
        foreach (StoryReference reference in runtimeReferences
                     .OrderBy(reference => RuntimeSourcePriority(reference, levelId))
                     .ThenBy(reference => reference.Source, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(reference => reference.Offset))
        {
            string? sceneId = ResolveRuntimeSceneId(reference.Value, sceneIds);
            if (sceneId is null) continue;
            if (!runtimeOrder.Contains(sceneId, StringComparer.OrdinalIgnoreCase)) runtimeOrder.Add(sceneId);
            runtimeEvidence.Add(new JsonObject
            {
                ["sceneId"] = sceneId,
                ["reference"] = reference.Value,
                ["kind"] = reference.EvidenceKind,
                ["source"] = reference.Source,
                ["offset"] = reference.Offset,
            });
        }
        if (flow is null && runtimeOrder.Count == 0)
        {
            return new JsonObject
            {
                ["method"] = "scene key fallback",
                ["confidence"] = "fallback",
                ["edges"] = new JsonArray(),
                ["sceneOrder"] = new JsonArray(scenes.OfType<JsonObject>().Select(scene => JsonValue.Create(scene["id"]?.GetValue<string>())).ToArray()),
                ["evidence"] = runtimeEvidence,
            };
        }
        var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (flow?["quests"] is JsonArray quests)
        foreach (JsonObject quest in quests.OfType<JsonObject>())
        {
            int layer = quest["layer"]?.GetValue<int>() ?? 0;
            if (quest["sceneIds"] is not JsonArray refs) continue;
            int index = 0;
            foreach (string? sceneId in refs.Select(value => value?.GetValue<string>()))
                if (sceneId is not null && sceneIds.Contains(sceneId)) orderMap.TryAdd(sceneId, layer * 1000 + index++);
        }
        var fallbackOrder = scenes.OfType<JsonObject>()
            .Select(scene => scene["id"]?.GetValue<string>() ?? "")
            .OrderBy(sceneId => orderMap.GetValueOrDefault(sceneId, int.MaxValue))
            .ThenBy(sceneId => sceneId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var order = runtimeOrder
            .Concat(fallbackOrder.Where(sceneId => !runtimeOrder.Contains(sceneId, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var edges = new JsonArray();
        for (int index = 1; index < runtimeOrder.Count; index++)
        {
            string from = runtimeOrder[index - 1];
            string to = runtimeOrder[index];
            if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) continue;
            edges.Add(new JsonObject
            {
                ["from"] = from,
                ["to"] = to,
                ["kind"] = "runtimeReferenceOrder",
                ["source"] = "LevelScriptData/NpcProxyExDataTable",
            });
        }
        if (flow?["edges"] is JsonArray flowEdges)
            foreach (JsonNode? edge in flowEdges)
                edges.Add(edge?.DeepClone());
        return new JsonObject
        {
            ["method"] = runtimeOrder.Count > 0
                ? "LevelScriptData reference order + questDagPartialOrder"
                : orderMap.Count == 0 ? "scene key fallback" : "questDagPartialOrder",
            ["confidence"] = runtimeOrder.Count > 0 ? "evidence" : orderMap.Count == sceneIds.Count ? "partial-order" : "mixed",
            ["note"] = "Quest predecessor edges establish layers; siblings in the same layer are not claimed to be chronological.",
            ["sceneOrder"] = new JsonArray(order.Select(value => JsonValue.Create(value)).ToArray()),
            ["edges"] = edges,
            ["evidence"] = runtimeEvidence,
        };
    }

    private static bool IsMissionReference(string value, string missionId)
        => value.Contains($"_{missionId}_", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith($"_{missionId}", StringComparison.OrdinalIgnoreCase);

    private static int RuntimeSourcePriority(StoryReference reference, string levelId)
    {
        if (reference.EvidenceKind.Equals("LevelScriptData", StringComparison.OrdinalIgnoreCase)
            && levelId.Length > 0
            && reference.Source.Contains($"LevelScriptData/{levelId}/", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (reference.EvidenceKind.Equals("NpcProxyExDataTable", StringComparison.OrdinalIgnoreCase)) return 1;
        if (reference.EvidenceKind.Equals("LevelData", StringComparison.OrdinalIgnoreCase)) return 2;
        if (reference.EvidenceKind.Equals("LevelScriptData", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    private static string? ResolveRuntimeSceneId(string value, IEnumerable<string> sceneIds)
    {
        string? exact = sceneIds.FirstOrDefault(scene => scene.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        return sceneIds
            .Where(scene => value.StartsWith(scene + "_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(scene => scene.Length)
            .FirstOrDefault();
    }

    private static string SceneKind(string sceneId)
    {
        int separator = sceneId.IndexOf('_');
        return separator > 0 ? sceneId[..separator].ToLowerInvariant() : "unknown";
    }

    private static IEnumerable<string> ExtractStoryRefs(JsonNode? node)
    {
        if (node is null) yield break;
        if (node is JsonValue value && value.TryGetValue<string>(out string? text))
        {
            foreach (Match match in Regex.Matches(text, @"(?<![A-Za-z0-9_])(?:f_|m_|fm_)?(?:dlg|cutscene|black|remotecomm|radio|sns)_[A-Za-z0-9_]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                yield return NormalizeStoryRef(match.Value);
            yield break;
        }
        if (node is JsonObject obj)
            foreach (JsonNode? child in obj.Select(pair => pair.Value).ToArray())
                foreach (string reference in ExtractStoryRefs(child)) yield return reference;
        if (node is JsonArray array)
            foreach (JsonNode? child in array)
                foreach (string reference in ExtractStoryRefs(child)) yield return reference;
    }

    private static JsonArray CollectTrackingHints(JsonNode node)
    {
        var result = new JsonArray();
        foreach (JsonObject obj in Objects(node))
        {
            string? sceneId = ScalarString(obj["sceneId"]);
            string? proxyId = ScalarString(obj["npcProxyId"]);
            if (string.IsNullOrWhiteSpace(sceneId) && string.IsNullOrWhiteSpace(proxyId)) continue;
            var hint = new JsonObject();
            if (!string.IsNullOrWhiteSpace(sceneId)) hint["sceneId"] = sceneId;
            if (!string.IsNullOrWhiteSpace(proxyId)) hint["npcProxyId"] = proxyId;
            if (obj["trackingPos"] is JsonObject position) hint["position"] = position.DeepClone();
            result.Add(hint);
        }
        return result;
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var property in obj)
                foreach (JsonObject child in Objects(property.Value)) yield return child;
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
                foreach (JsonObject nested in Objects(child)) yield return nested;
        }
    }

    private static string NormalizeStoryRef(string value)
    {
        string normalized = value;
        if (normalized.StartsWith("f_", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("m_", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("fm_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[(normalized.IndexOf('_') + 1)..];
        if (normalized.StartsWith("cs_video_", StringComparison.OrdinalIgnoreCase))
            normalized = "cutscene_" + normalized[9..];
        string[] parts = normalized.Split('_');
        if (parts.Length > 3 && parts[^1].Length >= 3 && int.TryParse(parts[^1], out _))
            normalized = string.Join('_', parts[..^1]);
        return normalized;
    }

    private static List<string> ReadStringArray(JsonNode? node)
        => node is JsonArray array
            ? array.Select(ScalarString).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToList()
            : new List<string>();

    private static JsonObject GetOrCreateScene(
        Dictionary<string, JsonObject> scenes,
        string sceneId,
        string kind,
        string missionId,
        string scene)
    {
        if (scenes.TryGetValue(sceneId, out var existing)) return existing;
        var created = new JsonObject
        {
            ["id"] = sceneId,
            ["kind"] = kind,
            ["mission"] = missionId,
            ["scene"] = scene,
            ["lines"] = new JsonArray(),
            ["summary"] = new JsonArray(),
            ["optionGroups"] = new JsonArray(),
            ["order"] = new JsonObject
            {
                ["method"] = "numeric scene/line suffix",
                ["confidence"] = "fallback",
            },
            ["sources"] = new JsonObject(),
        };
        scenes[sceneId] = created;
        return created;
    }

    private static void AttachCutsceneTimelineEvidence(
        Dictionary<string, JsonObject> scenes,
        Dictionary<string, JsonNode?> textRows,
        Dictionary<string, JsonArray> evidenceByKey)
    {
        foreach (var (sceneId, scene) in scenes)
        {
            if (!sceneId.StartsWith("cutscene_", StringComparison.OrdinalIgnoreCase)
                && !sceneId.StartsWith("black_", StringComparison.OrdinalIgnoreCase))
                continue;
            var merged = new JsonArray();
            if (evidenceByKey.TryGetValue(sceneId, out var directLines))
                foreach (JsonNode? line in directLines)
                {
                    JsonObject? copy = line?.DeepClone() as JsonObject;
                    if (copy is null) continue;
                    string? directId = ScalarString(copy["id"]);
                    if (directId is not null && textRows.ContainsKey(directId)) copy["textRowId"] = directId;
                    merged.Add(copy);
                }
            foreach (var (rowId, rowNode) in textRows)
            {
                if (!TryParseSceneTextKey(rowId, out _, out _, out string? rowScene, out _)
                    || rowScene is null)
                    continue;
                string fullRowScene = rowId.StartsWith("black_", StringComparison.OrdinalIgnoreCase)
                    ? $"black_{rowScene}"
                    : $"cutscene_{rowScene}";
                if (!fullRowScene.Equals(sceneId, StringComparison.OrdinalIgnoreCase)
                    || rowNode is not JsonObject row)
                    continue;
                string? numericId = ScalarString(row["id"]);
                if (numericId is null || !evidenceByKey.TryGetValue($"#{numericId}", out var numericLines)) continue;
                foreach (JsonNode? line in numericLines)
                {
                    JsonObject? copy = line?.DeepClone() as JsonObject;
                    if (copy is null) continue;
                    copy["textRowId"] = rowId;
                    merged.Add(copy);
                }
            }
            if (merged.Count == 0) continue;
            var ordered = merged.OfType<JsonObject>()
                .OrderBy(line => line["start"]?.GetValue<double>() ?? double.MaxValue)
                .ThenBy(line => line["trackOrder"]?.GetValue<int>() ?? int.MaxValue)
                .ThenBy(line => line["clipOrder"]?.GetValue<int>() ?? int.MaxValue)
                .ToArray();
            scene["timeline"] = new JsonObject
            {
                ["kind"] = "cutsceneSubtitle",
                ["confidence"] = "timeline",
                ["lines"] = new JsonArray(ordered.Select(line => line.DeepClone()).ToArray()),
            };
            var linesById = ((JsonArray)scene["lines"]!).OfType<JsonObject>()
                .Where(line => line["id"] is not null)
                .ToDictionary(line => line["id"]!.GetValue<string>(), line => line, StringComparer.OrdinalIgnoreCase);
            var reorderedLines = new List<JsonObject>();
            foreach (JsonObject timelineLine in ordered)
            {
                string? rowId = ScalarString(timelineLine["textRowId"]);
                if (rowId is not null && linesById.TryGetValue(rowId, out JsonObject? line)
                    && !reorderedLines.Contains(line)) reorderedLines.Add(line);
            }
            reorderedLines.AddRange(linesById.Values.Where(line => !reorderedLines.Contains(line)));
            ((JsonArray)scene["lines"]!).Clear();
            foreach (JsonObject line in reorderedLines) ((JsonArray)scene["lines"]!).Add(line);
            scene["order"] = new JsonObject
            {
                ["method"] = "Unity Timeline subtitle clips",
                ["confidence"] = "authored",
            };
        }
    }

    private static void AttachDialogEvidence(
        Dictionary<string, JsonObject> scenes,
        Dictionary<string, JsonArray> timelineEvidence,
        Dictionary<string, JsonArray> treeEvidence)
    {
        foreach (var (sceneId, scene) in scenes)
        {
            if (!sceneId.StartsWith("dlg_", StringComparison.OrdinalIgnoreCase)) continue;
            bool hasTimeline = timelineEvidence.TryGetValue(sceneId, out var timelines) && timelines.Count > 0;
            bool hasTree = treeEvidence.TryGetValue(sceneId, out var trees) && trees.Count > 0;
            if (!hasTimeline && !hasTree) continue;
            if (hasTimeline)
            {
                scene["timeline"] = new JsonObject
                {
                    ["kind"] = "dialogTimeline",
                    ["confidence"] = "authored",
                    ["timelines"] = timelines!.DeepClone(),
                };
                var lineIds = timelines!
                    .OfType<JsonObject>()
                    .SelectMany(timeline => (timeline["lines"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    .Select(line => ScalarString(line["id"]))
                    .Where(id => id is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                ReorderSceneLines(scene, lineIds);
                scene["order"] = new JsonObject
                {
                    ["method"] = "Unity Timeline/DialogTree evidence",
                    ["confidence"] = "authored",
                };
            }
            if (hasTree)
                scene["dialogTree"] = trees!.DeepClone();
        }
    }

    private static void ReorderSceneLines(JsonObject scene, IReadOnlyList<string> orderedIds)
    {
        if (orderedIds.Count == 0 || scene["lines"] is not JsonArray lineArray) return;
        var lines = lineArray.OfType<JsonObject>().ToArray();
        var byId = lines.Where(line => line["id"] is not null)
            .ToDictionary(line => line["id"]!.GetValue<string>(), line => line, StringComparer.OrdinalIgnoreCase);
        var reordered = new List<JsonObject>();
        foreach (string id in orderedIds)
            if (byId.TryGetValue(id, out JsonObject? line)) reordered.Add(line);
        reordered.AddRange(lines.Where(line => !reordered.Contains(line)));
        lineArray.Clear();
        foreach (JsonObject line in reordered) lineArray.Add(line);
    }

    private static bool TryParseSceneTextKey(
        string rowId,
        out string? kind,
        out string? mission,
        out string? scene,
        out int lineOrder)
    {
        kind = mission = scene = null;
        lineOrder = 0;
        Match match = CutsceneLinePattern.Match(rowId);
        if (match.Success)
        {
            kind = "cutscene";
            string group = match.Groups["group"].Value;
            if (!TrySplitSceneKey(group, out mission, out scene)) return false;
            lineOrder = ParseInt(match.Groups["line"].Value);
            return true;
        }
        match = Regex.Match(rowId, @"^(?<kind>radio|remotecomm|sns)_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)_(?<line>\d+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            kind = match.Groups["kind"].Value.ToLowerInvariant();
            mission = match.Groups["mission"].Value;
            scene = match.Groups["scene"].Value;
            lineOrder = ParseInt(match.Groups["line"].Value);
            return true;
        }
        match = BlackLinePattern.Match(rowId);
        if (match.Success)
        {
            kind = "black";
            string group = match.Groups["group"].Value;
            if (!TrySplitSceneKey(group, out mission, out scene)) return false;
            lineOrder = ParseInt(match.Groups["line"].Value);
            return true;
        }
        return false;
    }

    private static bool TrySplitSceneKey(string key, out string? mission, out string? scene)
    {
        mission = scene = null;
        int prefixSeparator = key.IndexOf('_');
        if (prefixSeparator <= 0 || prefixSeparator >= key.Length - 1) return false;
        string body = key[(prefixSeparator + 1)..];
        int separator = body.LastIndexOf('_');
        if (separator <= 0 || separator >= key.Length - 1) return false;
        mission = body[..separator];
        scene = body[(separator + 1)..];
        return Regex.IsMatch(scene, @"^\d+(?:d\d+)?$", RegexOptions.CultureInvariant);
    }

    private static bool SceneMatches(string sceneId, string? filter)
        => string.IsNullOrWhiteSpace(filter)
            || sceneId.Equals(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string SceneSuffix(string sceneId)
        => sceneId[(sceneId.LastIndexOf('_') + 1)..];

    private static string SceneSortKey(JsonObject scene)
        => $"{scene["kind"]?.GetValue<string>()}\u001f{scene["scene"]?.GetValue<string>()}\u001f{scene["id"]?.GetValue<string>()}";

    private static Dictionary<string, JsonNode?> GetTable(TableSet tableSet, string name)
        => tableSet.Tables.TryGetValue(name, out var table)
            ? table.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    private static string? ResolveText(JsonNode? raw, Dictionary<string, JsonNode?> i18nRows)
    {
        if (raw is null) return null;
        if (raw is JsonValue directValue && directValue.TryGetValue<string>(out string? direct)) return direct;
        string? id = raw switch
        {
            JsonObject obj => ScalarString(obj["id"]) ?? ScalarString(obj["key"]),
            JsonValue scalarValue => ScalarString(scalarValue),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(id) || !i18nRows.TryGetValue(id, out var row) || row is null)
            return null;
        if (row is JsonValue localizedValue && localizedValue.TryGetValue<string>(out string? localized)) return localized;
        if (row is JsonObject localizedRow)
            return ScalarString(localizedRow["text"]) ?? ScalarString(localizedRow["value"]);
        return null;
    }

    private static string? ScalarString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out string? text)) return text;
        if (value.TryGetValue<long>(out long integer)) return integer.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<ulong>(out ulong unsignedInteger)) return unsignedInteger.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<decimal>(out decimal decimalValue)) return decimalValue.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) ? result : 0;

    private static JsonObject SourceRef(string table, string rowId, TableSet tableSet)
    {
        var source = new JsonObject { ["table"] = table, ["rowId"] = rowId };
        if (tableSet.Sources.TryGetValue(table, out var sources))
            source["sources"] = new JsonArray(sources.Select(value => JsonValue.Create(value)).ToArray());
        return source;
    }

    private static void SortArray(JsonArray? array, string property)
    {
        if (array is null || array.Count < 2) return;
        var sorted = array.OfType<JsonObject>()
            .OrderBy(item => item[property]?.GetValue<int>() ?? 0)
            .ThenBy(item => item["id"]?.GetValue<string>(), StringComparer.Ordinal)
            .ToArray();
        array.Clear();
        foreach (JsonObject item in sorted) array.Add(item);
    }

    private static void WriteMission(string outPath, string missionId, string language, JsonObject payload, string? snapshotTimestamp)
    {
        string fileName = snapshotTimestamp is null
            ? $"{language}.json"
            : $"{language}_snapshot{snapshotTimestamp}.json";
        string directory = Path.Combine(outPath, missionId);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, fileName);
        File.WriteAllText(target, payload.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }
}
