using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AnimeStudio.Endfield.Cli.Story.Timeline;

namespace AnimeStudio.Endfield.Cli.Story;

internal static class StoryExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex DialogLine = new(@"^dlg_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)_(?<line>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Scene = new(@"^(?<kind>dlg|radio|remotecomm|sns|cutscene|cs_video|black)_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TextLine = new(@"^(?<scene>(?<kind>cutscene|cs_video|black)_.+)_(?<line>\d+(?:d\d+)?)(?:_[fm])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MissionToken = new(@"(?:^|_)(?<mission>[a-z]\d+m\d+(?:d\d+)?)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Export(StoryTableRepository repository, JsonObject overrides, StoryCommand.Options options)
    {
        List<string> languages = options.Language == "ALL"
            ? repository.Tables.Keys.Where(n => n.StartsWith("I18nTextTable_", StringComparison.OrdinalIgnoreCase)).Select(n => n["I18nTextTable_".Length..]).Order().ToList()
            : [options.Language];
        if (languages.Count == 0) languages.Add("JP");
        bool exportAll = options.Mission?.Equals("all", StringComparison.OrdinalIgnoreCase) == true;
        string? requestedMission = exportAll ? null : options.Mission;
        if (requestedMission is null && options.Scene is not null)
            requestedMission = ParseScene(options.Scene)?.Mission ?? MissionFromId(Normalize(options.Scene));
        if (requestedMission is null && !exportAll)
            throw new ArgumentException($"Could not infer a mission from --scene {options.Scene}; specify --mission as well.");
        var missionIds = exportAll
            ? StoryMissionCatalog.ListedMissionIds(repository)
            : MissionIds(repository, requestedMission);
        if (missionIds.Count == 0) throw new InvalidOperationException("No matching story scenes were found.");
        StoryTimelineScanResult? timeline = null;
        string? timelineIndexPath = null;
        if (options.Timeline is "full" or "quick")
        {
            string scratch = options.Scratch ?? Path.Combine(Path.GetTempPath(), "endfield-story-timeline");
            timelineIndexPath = options.DisableTimelineIndex
                ? null
                : options.TimelineIndex ?? Path.Combine(options.OutputPath!, ".timeline-index");
            try
            {
                timeline = StoryTimelineScanner.Scan(
                    options.VfsPath!,
                    options.BaseVfsPath,
                    scratch,
                    requestedMission,
                    options.Timeline,
                    timelineIndexPath,
                    options.RebuildTimelineIndex,
                    options.Threads);
                if (options.Timeline == "full" && timeline.BundlesFailed > 0)
                    repository.Warnings.Add(
                        $"Full Timeline evidence is incomplete because {timeline.BundlesFailed:N0} Bundle file(s) could not be read.");
            }
            catch (Exception ex)
            {
                repository.Warnings.Add($"Timeline scan failed; table/runtime export was retained: {ex.Message}");
            }
        }
        int written = 0;
        foreach (string mission in missionIds)
            if (WriteMission(repository, overrides, options, mission, languages, timeline, timelineIndexPath)) written++;
        if (written == 0) throw new InvalidOperationException("No matching story scenes were found.");
        Console.WriteLine($"Wrote {written:N0} mission story export(s) to {Path.GetFullPath(options.OutputPath!)}");
    }

    internal static List<string> MissionIds(StoryTableRepository repo, string? requested)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in repo.Table("DialogTextTable").Select(p => p.Key)) if (DialogLine.Match(id) is { Success: true } m) ids.Add(m.Groups["mission"].Value);
        foreach (string table in new[] { "RadioTable", "RemoteCommonTable", "SNSDialogTable" })
            foreach (string id in repo.Table(table).Select(p => p.Key)) if (ParseScene(id) is { } scene) ids.Add(scene.Mission);
        foreach (string id in repo.Table("TextTable").Select(pair => pair.Key))
            if (TextLine.Match(id) is { Success: true } text && MissionFromId(text.Groups["scene"].Value) is string mission)
                ids.Add(mission);
        return ids.Where(id => requested is null || id.Equals(requested, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool WriteMission(
        StoryTableRepository repo,
        JsonObject overrides,
        StoryCommand.Options options,
        string mission,
        List<string> languages,
        StoryTimelineScanResult? timeline,
        string? timelineIndexPath)
    {
        var scenes = BuildScenes(repo, mission, options.Scene, timeline);
        if (scenes.Count == 0) return false;
        NormalizeLocalizationIds(scenes.Values);
        var graph = BuildGraph(repo, mission, scenes, options);
        JsonObject? overrideSummary = StoryGraphPostProcessor.ApplyOverrides(overrides, mission, scenes, graph);
        StoryGraphPostProcessor.RefreshDerived(graph, scenes.Keys);
        ApplySceneOrder(graph, scenes);
        string dir = Path.Combine(options.OutputPath!, mission); Directory.CreateDirectory(Path.Combine(dir, "locales"));
        var warnings = new JsonArray(repo.Warnings.Select(value => JsonValue.Create(value)).ToArray());
        var manifest = new JsonObject { ["missionId"] = mission, ["timeline"] = options.Timeline, ["scenes"] = new JsonArray(scenes.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray()), ["warnings"] = warnings };
        if (timeline is not null)
            manifest["timelineScan"] = new JsonObject
            {
                ["bundlesScanned"] = timeline.BundlesScanned,
                ["bundlesFailed"] = timeline.BundlesFailed,
                ["rootsFound"] = timeline.RootsFound,
                ["graphObjectsRead"] = timeline.GraphObjectsRead,
                ["bundleCandidates"] = timeline.BundleCandidates,
                ["bundlesReused"] = timeline.BundlesReused,
                ["bundlesChanged"] = timeline.BundlesChanged,
                ["bundlesQuickSkipped"] = timeline.BundlesQuickSkipped,
                ["coverage"] = options.Timeline == "quick"
                    ? "candidate-only"
                    : timeline.BundlesFailed == 0 ? "complete" : "incomplete",
                ["complete"] = options.Timeline == "full" && timeline.BundlesFailed == 0,
                ["indexPath"] = timelineIndexPath is null ? null : Path.GetFullPath(timelineIndexPath),
            };
        if (overrideSummary is not null && options.Overrides is not null)
            manifest["override"] = new JsonObject
            {
                ["path"] = Path.GetFullPath(options.Overrides),
                ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(options.Overrides))).ToLowerInvariant(),
                ["applied"] = overrideSummary,
            };
        if (options.Snapshot)
            manifest["sourceSnapshot"] = new JsonObject
            {
                ["baseVfs"] = options.BaseVfsPath,
                ["primaryVfs"] = options.VfsPath,
                ["tables"] = new JsonArray(repo.Tables.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray()),
                ["evidenceFiles"] = new JsonArray(repo.Evidence.Where(e => EvidenceBelongs(e, mission)).Select(e => e.File).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray()),
            };
        Write(Path.Combine(dir, "manifest.json"), manifest);
        Write(Path.Combine(dir, "graph.json"), graph);
        File.WriteAllLines(Path.Combine(dir, "evidence.jsonl"), repo.Evidence.Where(e => EvidenceBelongs(e, mission)).Select(e => UnescapePrintableUnicode(JsonSerializer.Serialize(e, JsonLineOptions))));
        Write(Path.Combine(dir, "unresolved.json"), new JsonObject { ["missionId"] = mission, ["warnings"] = warnings.DeepClone(), ["unresolvedNodes"] = graph["unresolved"]?.DeepClone() });
        foreach (string language in languages)
        {
            string tableName = "I18nTextTable_" + language;
            Write(Path.Combine(dir, "locales", language + ".json"), StoryLocalization.Build(scenes, language, repo.Table(tableName), repo.Tables.ContainsKey(tableName)));
        }
        return true;
    }

    private static Dictionary<string, JsonObject> BuildScenes(StoryTableRepository repo, string mission, string? filter, StoryTimelineScanResult? timeline)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var cutsceneTimeline = ResolveNumericCutsceneEvidence(repo, timeline);
        JsonObject Get(string id, string kind) { if (!result.TryGetValue(id, out var s)) result[id] = s = new JsonObject { ["id"] = id, ["kind"] = kind, ["lines"] = new JsonArray(), ["branches"] = new JsonArray(), ["sources"] = new JsonArray() }; return s; }
        var candidates = repo.Evidence
            .Where(e => e.SceneId is not null && EvidenceBelongs(e, mission))
            .Select(e => Normalize(e.SceneId!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (timeline is not null)
        {
            foreach (string id in timeline.DialogTimelinesByDialogId.Keys
                         .Concat(timeline.DialogTreesByDialogId.Keys)
                         .Concat(timeline.CutscenesBySceneId.Keys)
                         .Where(id => !id.StartsWith('#') && Belongs(id, mission)))
                candidates.Add(Normalize(id));
        }
        bool Included(string id) => Belongs(id, mission) || candidates.Contains(Normalize(id));
        foreach (string candidate in candidates)
        {
            if (ParseScene(candidate) is { } parsed) Get(parsed.Id, parsed.Kind);
            else if (SceneKind(candidate) is string kind) Get(candidate, kind);
        }
        foreach (var row in repo.Table("DialogTextTable"))
            if (DialogLine.Match(row.Key) is { Success: true } m)
            {
                string sceneId = $"dlg_{m.Groups["mission"].Value}_{m.Groups["scene"].Value}";
                if (Included(sceneId)) AddLine(Get(sceneId, "dlg"), row.Key, Int(m.Groups["line"].Value), row.Value, "DialogTextTable");
            }
        AddStructured("RadioTable", "radioSingleDataList", "radioText"); AddStructured("RemoteCommonTable", "remoteCommSingleDataList", "remoteCommText");
        foreach (var row in repo.Table("SNSDialogTable")) if (ParseScene(row.Key) is { } p && Included(p.Id) && row.Value is JsonObject value)
        {
            var scene = Get(p.Id, "sns");
            if (value["dialogContentData"] is JsonObject map)
                foreach (JsonObject entry in map.Select(x => x.Value).OfType<JsonObject>())
                {
                    string contentId = Scalar(entry["contentId"]) ?? "0";
                    ((JsonArray)scene["lines"]!).Add(new JsonObject
                    {
                        ["id"] = $"{p.Id}_{contentId}",
                        ["order"] = Int(contentId),
                        ["sourceOrder"] = Int(contentId),
                        ["value"] = entry["content"]?.DeepClone(),
                        ["speaker"] = entry["speaker"]?.DeepClone(),
                        ["contentType"] = entry["contentType"]?.DeepClone(),
                        ["preContentId"] = entry["preContentId"]?.DeepClone(),
                        ["nextContentId"] = entry["nextContentId"]?.DeepClone(),
                        ["isEnd"] = entry["isEnd"]?.DeepClone(),
                        ["dialogOptionIds"] = entry["dialogOptionIds"]?.DeepClone(),
                        ["source"] = "SNSDialogTable",
                    });
                }
        }
        AddSnsOptions();
        foreach (JsonObject sns in result.Values.Where(scene => string.Equals(Scalar(scene["kind"]), "sns", StringComparison.OrdinalIgnoreCase)))
            AddSnsFlow(sns);
        foreach (var row in repo.Table("TextTable")) if (TextLine.Match(row.Key) is { Success: true } m)
        {
            string sceneId = Normalize(m.Groups["scene"].Value);
            if (Included(sceneId)) AddLine(Get(sceneId, Normalize(m.Groups["kind"].Value)), row.Key, Int(m.Groups["line"].Value), row.Value, "TextTable");
        }
        foreach (var row in repo.Table("DialogOptionTable")) if (Regex.Match(row.Key, @"^option_(?<id>dlg_.+_\d+(?:d\d+)?)_(?<group>\d+)_(?<option>\d+)$", RegexOptions.IgnoreCase) is { Success: true } m && Included(m.Groups["id"].Value)) ((JsonArray)Get(m.Groups["id"].Value, "dlg")["branches"]!).Add(new JsonObject { ["id"] = row.Key, ["order"] = Int(m.Groups["group"].Value), ["choiceOrder"] = Int(m.Groups["option"].Value), ["source"] = "DialogOptionTable", ["value"] = row.Value?.DeepClone() });
        foreach (var scene in result.Values.ToArray())
            if (filter is null || scene["id"]?.GetValue<string>().Equals(Normalize(filter), StringComparison.OrdinalIgnoreCase) == true)
            {
                Sort(scene["lines"] as JsonArray);
                AddFallbackLineOrder(scene);
                AttachTimelineEvidence(scene, timeline, cutsceneTimeline);
                FinalizeLineOrder(scene);
            }
            else result.Remove(scene["id"]!.GetValue<string>());
        return result;
        void AddStructured(string table, string list, string text) { foreach (var row in repo.Table(table)) if (ParseScene(row.Key) is { } p && Included(p.Id) && row.Value is JsonObject v) { var s = Get(p.Id, p.Kind); if (v[list] is JsonArray a) foreach (JsonObject e in a.OfType<JsonObject>()) AddLine(s, Scalar(e["id"]) ?? Scalar(e["singleId"]) ?? $"{p.Id}_{Scalar(e["index"]) ?? "0"}", Int(Scalar(e["index"])), e, table); } }
        void AddSnsOptions()
        {
            foreach (var row in repo.Table("SNSDialogOptionTable"))
            {
                Match match = Regex.Match(row.Key, @"^option_(?<scene>sns_.+_\d+(?:d\d+)?)_(?<group>\d+)_(?<option>\d+)$", RegexOptions.IgnoreCase);
                if (!match.Success || !Included(match.Groups["scene"].Value) || row.Value is not JsonObject value) continue;
                string sceneId = Normalize(match.Groups["scene"].Value);
                JsonObject scene = Get(sceneId, "sns");
                int? target = Int(Scalar(value["optionNextContentId"]));
                string? from = FindSnsOptionOwner(scene, row.Key);
                ((JsonArray)scene["branches"]!).Add(new JsonObject
                {
                    ["id"] = row.Key,
                    ["order"] = Int(match.Groups["group"].Value),
                    ["choiceOrder"] = Int(match.Groups["option"].Value),
                    ["fromLineId"] = from,
                    ["targetLineId"] = target is null ? null : $"{sceneId}_{target}",
                    ["source"] = "SNSDialogOptionTable",
                    ["value"] = value.DeepClone(),
                });
            }
        }
    }

    private static string? FindSnsOptionOwner(JsonObject scene, string optionId)
        => (scene["lines"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(line => (line["dialogOptionIds"] as JsonArray)?.Any(id => string.Equals(Scalar(id), optionId, StringComparison.OrdinalIgnoreCase)) == true)?["id"]?.GetValue<string>();

    private static void AddSnsFlow(JsonObject scene)
    {
        string sceneId = Scalar(scene["id"])!;
        JsonObject[] lines = (scene["lines"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var ids = lines.Select(line => Scalar(line["id"])).Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = new JsonArray();
        var edges = new JsonArray();
        foreach (JsonObject line in lines)
        {
            string? from = Scalar(line["id"]);
            int? contentId = Int(from is null ? null : from[(sceneId.Length + 1)..]);
            int? previous = Int(Scalar(line["preContentId"]));
            int? next = Int(Scalar(line["nextContentId"]));
            if (from is null) continue;
            if (previous is null || previous <= 0 || !ids.Contains($"{sceneId}_{previous}")) roots.Add(from);
            if (next is not null && next >= 0 && ids.Contains($"{sceneId}_{next}"))
                edges.Add(new JsonObject { ["from"] = from, ["to"] = $"{sceneId}_{next}", ["kind"] = "nextContentId", ["status"] = "confirmed" });
            if (contentId == -1) line["terminal"] = true;
        }
        foreach (JsonObject choice in (scene["branches"] as JsonArray ?? []).OfType<JsonObject>())
            if (Scalar(choice["fromLineId"]) is string from && Scalar(choice["targetLineId"]) is string to && ids.Contains(to))
                edges.Add(new JsonObject { ["from"] = from, ["to"] = to, ["kind"] = "option", ["optionId"] = choice["id"]?.DeepClone(), ["status"] = "confirmed" });
        scene["flow"] = new JsonObject { ["roots"] = roots, ["edges"] = edges };
    }

    private static void AddFallbackLineOrder(JsonObject scene)
    {
        string kind = Scalar(scene["kind"]) ?? "unknown";
        scene["lineOrder"] = kind switch
        {
            "radio" or "remotecomm" => new JsonObject { ["source"] = "table index", ["status"] = "confirmed" },
            "sns" => new JsonObject { ["source"] = "preContentId/nextContentId/options graph", ["status"] = "confirmed-graph" },
            _ => new JsonObject
            {
                ["source"] = "deterministic table/id fallback",
                ["status"] = "unresolved-without-timeline",
                ["warning"] = "This array order is not asserted as authored chronology.",
            },
        };
    }

    private static Dictionary<string, JsonArray> ResolveNumericCutsceneEvidence(StoryTableRepository repo, StoryTimelineScanResult? timeline)
    {
        var result = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        if (timeline is null) return result;
        foreach (var pair in timeline.CutscenesBySceneId.Where(pair => !pair.Key.StartsWith('#')))
            result[pair.Key] = (JsonArray)pair.Value.DeepClone();
        var textRowGroups = repo.Table("TextTable")
            .Where(pair => pair.Value is JsonObject row && Scalar(row["id"]) is not null)
            .GroupBy(pair => Scalar(((JsonObject)pair.Value!)["id"])!, StringComparer.Ordinal)
            .ToArray();
        foreach (var duplicate in textRowGroups.Where(group => group.Key != "0" && group.Skip(1).Any()))
            repo.Warnings.Add($"Numeric TextTable id {duplicate.Key} maps to multiple rows; Timeline evidence was left unresolved.");
        var textRows = textRowGroups
            .Where(group => group.Key != "0" && !group.Skip(1).Any())
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
        foreach (var pair in timeline.CutscenesBySceneId.Where(pair => pair.Key.StartsWith('#')))
        {
            string numericId = pair.Key[1..];
            if (!textRows.TryGetValue(numericId, out string? rowId) || TextLine.Match(rowId) is not { Success: true } match) continue;
            string sceneId = Normalize(match.Groups["scene"].Value);
            if (!result.TryGetValue(sceneId, out JsonArray? evidence)) result[sceneId] = evidence = [];
            foreach (JsonObject item in pair.Value.OfType<JsonObject>())
            {
                JsonObject resolved = (JsonObject)item.DeepClone();
                resolved["numericTextId"] = numericId;
                resolved["id"] = rowId;
                evidence.Add(resolved);
            }
        }
        foreach (JsonArray evidence in result.Values)
        {
            JsonObject[] ordered = evidence.OfType<JsonObject>()
                .GroupBy(CutsceneEvidenceKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item["start"]?.GetValue<double?>() ?? double.MaxValue)
                .ThenBy(item => item["trackOrder"]?.GetValue<int?>() ?? int.MaxValue)
                .ThenBy(item => item["clipOrder"]?.GetValue<int?>() ?? int.MaxValue)
                .ToArray();
            evidence.Clear();
            int timeGroup = -1;
            string? previousStart = null;
            foreach (JsonObject item in ordered)
            {
                string? start = item["start"]?.ToJsonString();
                if (start is null || previousStart is null || !string.Equals(start, previousStart, StringComparison.Ordinal)) timeGroup++;
                int peers = start is null ? 0 : ordered.Count(other => string.Equals(other["start"]?.ToJsonString(), start, StringComparison.Ordinal));
                item["timeOrderGroup"] = start is null ? null : timeGroup;
                item["timeOrderStatus"] = start is null || peers > 1 ? "unordered-within-group" : "ordered-by-start-time";
                previousStart = start;
                evidence.Add(item);
            }
        }
        return result;
    }

    private static string CutsceneEvidenceKey(JsonObject item)
        => string.Join("\u001f",
            Scalar(item["id"]) ?? string.Empty,
            item["start"]?.ToJsonString() ?? string.Empty,
            item["duration"]?.ToJsonString() ?? string.Empty,
            item["trackOrder"]?.ToJsonString() ?? string.Empty,
            item["clipOrder"]?.ToJsonString() ?? string.Empty);

    private static void AttachTimelineEvidence(JsonObject scene, StoryTimelineScanResult? timeline, IReadOnlyDictionary<string, JsonArray> cutsceneTimeline)
    {
        if (timeline is null || Scalar(scene["id"]) is not string id) return;
        var evidence = new JsonObject();
        if (timeline.DialogTimelinesByDialogId.TryGetValue(id, out JsonArray? dialogTimeline))
            evidence["timeline"] = dialogTimeline.DeepClone();
        if (timeline.DialogTreesByDialogId.TryGetValue(id, out JsonArray? dialogTrees))
            evidence["dialogTree"] = dialogTrees.DeepClone();
        if (cutsceneTimeline.TryGetValue(id, out JsonArray? cutscene))
            evidence["timeline"] = cutscene.DeepClone();
        if (evidence.Count == 0) return;
        scene["evidence"] = evidence;

        var authoredGroups = new List<List<string>>();
        string? orderSource = null;
        string? orderStatus = null;
        bool dialogTreeFound = false;
        if (dialogTrees is not null)
        {
            JsonObject? tree = dialogTrees.OfType<JsonObject>()
                .OrderByDescending(item => (item["lineIds"] as JsonArray)?.Count ?? 0)
                .FirstOrDefault();
            if (tree?["lineIds"] is JsonArray lineIds)
            {
                dialogTreeFound = true;
                orderSource = "DialogTree";
                orderStatus = Scalar(tree["orderStatus"]);
                scene["flow"] = new JsonObject
                {
                    ["source"] = "DialogTree",
                    ["status"] = orderStatus,
                    ["nodes"] = tree["nodes"]?.DeepClone(),
                    ["edges"] = tree["edges"]?.DeepClone(),
                    ["projectedLineEdges"] = tree["projectedLineEdges"]?.DeepClone(),
                    ["branches"] = tree["branches"]?.DeepClone(),
                };
                MergeDialogTreeBranches(scene, tree);
                if (tree["lineOrderGroups"] is JsonArray lineOrderGroups)
                    foreach (JsonArray group in lineOrderGroups.OfType<JsonArray>())
                    {
                        List<string> values = group.Select(Scalar).Where(value => value is not null).Cast<string>().ToList();
                        if (values.Count > 0) authoredGroups.Add(values);
                    }
                else if (orderStatus == "total-by-dialog-tree-path")
                    authoredGroups.AddRange(lineIds.Select(Scalar).Where(value => value is not null).Cast<string>().Select(value => new List<string> { value }));
            }
        }
        if (!dialogTreeFound && dialogTimeline is not null)
        {
            JsonObject? candidate = dialogTimeline.OfType<JsonObject>()
                .OrderByDescending(item => (item["lines"] as JsonArray)?.Count ?? 0)
                .FirstOrDefault();
            if (candidate?["orderGroups"] is JsonArray groups)
            {
                foreach (JsonObject group in groups.OfType<JsonObject>())
                    if (group["lineIds"] is JsonArray ids)
                    {
                        List<string> values = ids.Select(Scalar).Where(value => value is not null).Cast<string>().ToList();
                        if (values.Count > 0) authoredGroups.Add(values);
                    }
                orderSource = "Timeline";
                orderStatus = Scalar(candidate["orderStatus"]);
            }
        }
        if (!dialogTreeFound && authoredGroups.Count == 0 && cutscene is not null)
        {
            CutsceneConsensusOrder consensus = BuildCutsceneConsensusOrder(cutscene);
            authoredGroups.AddRange(consensus.Groups);
            orderSource = "Timeline";
            orderStatus = consensus.IsPartial
                ? "partial-by-timeline-variant-consensus"
                : "total-by-timeline-variant-consensus";
            scene["timelineVariantCount"] = consensus.VariantCount;
        }
        if (scene["lines"] is JsonArray availableLines)
        {
            HashSet<string> availableIds = availableLines.OfType<JsonObject>()
                .Select(line => Scalar(line["id"]))
                .Where(id => id is not null).Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<List<string>> resolvedGroups = authoredGroups
                .Select(group => group.Where(availableIds.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                .Where(group => group.Count > 0)
                .ToList();
            authoredGroups.Clear();
            authoredGroups.AddRange(resolvedGroups);
            string[] uncovered = availableIds.Where(id => !authoredGroups.SelectMany(group => group).Contains(id, StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            scene["lineOrderCoverage"] = new JsonObject
            {
                ["covered"] = availableIds.Count - uncovered.Length,
                ["total"] = availableIds.Count,
                ["uncoveredLineIds"] = new JsonArray(uncovered.Select(value => JsonValue.Create(value)).ToArray()),
            };
            if (uncovered.Length > 0 && authoredGroups.Count > 0)
            {
                scene["lineOrderBasisStatus"] = orderStatus;
                orderStatus = "partial-with-uncovered-lines";
            }
        }
        if (authoredGroups.Count > 0 && scene["lines"] is JsonArray sceneLines)
        {
            ReorderByGroups(sceneLines, authoredGroups);
            scene["lineOrder"] = new JsonObject
            {
                ["source"] = orderSource,
                ["status"] = orderStatus,
                ["groups"] = new JsonArray(authoredGroups.Select(group => new JsonArray(group.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray())).ToArray()),
            };
        }
        else if (dialogTreeFound)
            scene["lineOrder"] = new JsonObject
            {
                ["source"] = orderSource,
                ["status"] = orderStatus,
                ["warning"] = "The DialogTree is a graph; no total array order was asserted.",
            };
    }

    private sealed record CutsceneConsensusOrder(List<List<string>> Groups, bool IsPartial, int VariantCount);

    private static CutsceneConsensusOrder BuildCutsceneConsensusOrder(JsonArray cutscene)
    {
        JsonObject[] entries = cutscene.OfType<JsonObject>()
            .Where(item => Scalar(item["id"]) is string id && !id.StartsWith('#'))
            .ToArray();
        var variants = entries
            .GroupBy(CutsceneVariantKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.GroupBy(item => Scalar(item["id"])!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(idGroup => idGroup.Key, idGroup => Median(idGroup.Select(item => Number(item["start"]))), StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);
        var medians = entries
            .GroupBy(item => Scalar(item["id"])!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Id = group.Key, Start = Median(group.Select(item => Number(item["start"]))) })
            .OrderBy(item => item.Start ?? double.MaxValue)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var groups = medians
            .GroupBy(item => item.Start, NullableDoubleEqualityComparer.Instance)
            .Select(group => group.Select(item => item.Id).ToList())
            .ToList();

        bool partial = medians.Any(item => item.Start is null)
            || groups.Any(group => group.Count > 1)
            || HasConflictingVariantOrder(variants, medians.Select(item => item.Id).ToArray());
        return new CutsceneConsensusOrder(groups, partial, variants.Count);
    }

    private static bool HasConflictingVariantOrder(
        IReadOnlyDictionary<string, Dictionary<string, double?>> variants,
        IReadOnlyList<string> ids)
    {
        for (int left = 0; left < ids.Count; left++)
        for (int right = left + 1; right < ids.Count; right++)
        {
            bool before = false, after = false, equal = false;
            foreach (Dictionary<string, double?> variant in variants.Values)
            {
                if (!variant.TryGetValue(ids[left], out double? leftStart) || leftStart is null
                    || !variant.TryGetValue(ids[right], out double? rightStart) || rightStart is null) continue;
                if (leftStart < rightStart) before = true;
                else if (leftStart > rightStart) after = true;
                else equal = true;
            }
            if (equal || before && after) return true;
        }
        return false;
    }

    private static string CutsceneVariantKey(JsonObject item)
        => Scalar(item["track"]?["sourceFile"])
            ?? Scalar(item["asset"]?["sourceFile"])
            ?? "unknown";

    private static double? Median(IEnumerable<double?> values)
    {
        double[] ordered = values.Where(value => value is not null).Select(value => value!.Value).Order().ToArray();
        if (ordered.Length == 0) return null;
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2.0 : ordered[middle];
    }

    private sealed class NullableDoubleEqualityComparer : IEqualityComparer<double?>
    {
        public static readonly NullableDoubleEqualityComparer Instance = new();
        public bool Equals(double? left, double? right) => left.Equals(right);
        public int GetHashCode(double? value) => value.GetHashCode();
    }

    private static void ReorderByGroups(JsonArray lines, IReadOnlyList<List<string>> groups)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < groups.Count; index++)
            foreach (string id in groups[index]) rank.TryAdd(id, index);
        var ordered = lines.OfType<JsonObject>()
            .Select((line, original) => (Line: line, Original: original, Rank: Scalar(line["id"]) is string id && rank.TryGetValue(id, out int value) ? value : int.MaxValue))
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Original)
            .Select(item => item.Line)
            .ToArray();
        lines.Clear();
        foreach (JsonObject line in ordered) lines.Add(line);
    }

    private static void MergeDialogTreeBranches(JsonObject scene, JsonObject tree)
    {
        if (tree["branches"] is not JsonArray treeBranches || scene["branches"] is not JsonArray choices) return;
        var byId = choices.OfType<JsonObject>()
            .Where(choice => Scalar(choice["id"]) is not null)
            .ToDictionary(choice => Scalar(choice["id"])!, StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject branch in treeBranches.OfType<JsonObject>())
        {
            string? optionId = Scalar(branch["optionId"]);
            if (optionId is null || !byId.TryGetValue(optionId, out JsonObject? choice)) continue;
            if (branch["afterLineId"] is not null) choice["afterLineId"] = branch["afterLineId"]!.DeepClone();
            if (branch["targetNodeId"] is not null) choice["targetNodeId"] = branch["targetNodeId"]!.DeepClone();
            if (branch["pathLineIds"] is JsonArray path)
            {
                choice["responseLineIds"] = path.DeepClone();
                if (path.FirstOrDefault() is JsonNode first) choice["targetLineId"] = first.DeepClone();
            }
            choice["flowSource"] = "DialogTree";
            choice["flowStatus"] = "inferred-by-serialized-option-connection-order";
            choice["flowWarning"] = "The DialogTree exposes option and successor arrays but no decoded per-option port key; their serialized positions are paired.";
        }
    }

    private static void FinalizeLineOrder(JsonObject scene)
    {
        if (scene["lines"] is not JsonArray lines) return;
        var groupById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (scene["lineOrder"]?["groups"] is JsonArray groups)
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                if (groups[groupIndex] is JsonArray group)
                    foreach (string id in group.Select(Scalar).Where(id => id is not null).Cast<string>())
                        groupById.TryAdd(id, groupIndex + 1);
        int resolved = 0;
        foreach (JsonObject line in lines.OfType<JsonObject>())
        {
            line["sourceOrder"] ??= line["order"]?.DeepClone();
            line["resolvedOrder"] = ++resolved;
            line["order"] = resolved;
            if (Scalar(line["id"]) is string id && groupById.TryGetValue(id, out int group))
                line["resolvedOrderGroup"] = group;
        }
    }

    private static JsonObject BuildGraph(StoryTableRepository repo, string mission, Dictionary<string, JsonObject> scenes, StoryCommand.Options options)
    {
        var nodes = new JsonArray(scenes.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(id => new JsonObject
        {
            ["id"] = id,
            ["status"] = scenes[id]["evidence"] is not null || repo.Evidence.Any(e => e.Kind == "level-script"
                    && string.Equals(Normalize(e.SceneId ?? ""), id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Scalar(e.Detail?["membershipEvidence"]), "scene id", StringComparison.OrdinalIgnoreCase)) ? "confirmed"
                : repo.Evidence.Any(e => string.Equals(Normalize(e.SceneId ?? ""), id, StringComparison.OrdinalIgnoreCase)) ? "referenced"
                : "unresolved",
        }).ToArray());
        var edges = new JsonArray();
        foreach (var group in repo.Evidence.Where(e => e.Kind == "level-script" && e.SceneId is not null && EvidenceBelongs(e, mission)).GroupBy(e => e.File + "|" + e.Source + "|" + Scalar(e.Detail?["chain"])))
        {
            // A decoded record chain is stronger than file co-occurrence, but
            // without a decoded action opcode/field it is not a direct scene transition.
            var ids = group.OrderBy(e => e.Detail?["offset"]?.GetValue<int>() ?? int.MaxValue).Select(e => Normalize(e.SceneId!)).Where(scenes.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 1; i < ids.Count; i++) edges.Add(new JsonObject { ["from"] = ids[i - 1], ["to"] = ids[i], ["status"] = "inferred", ["source"] = "LevelScriptData decoded record offset sequence", ["evidence"] = group.Key });
        }
        foreach (string video in scenes.Keys.Where(id => id.StartsWith("cs_video_", StringComparison.OrdinalIgnoreCase)))
        {
            string subtitle = "cutscene_" + video["cs_video_".Length..];
            if (scenes.ContainsKey(subtitle))
                edges.Add(new JsonObject
                {
                    ["from"] = video,
                    ["to"] = subtitle,
                    ["kind"] = "subtitleCandidate",
                    ["status"] = "unresolved",
                    ["source"] = "matching scene suffix only",
                    ["warning"] = "No direct cs_video-to-TextTable reference field was established.",
                });
        }
        var questEvidence = repo.Evidence.Where(e => e.Kind == "quest" && EvidenceBelongs(e, mission)).ToArray();
        var questEdges = new JsonArray(questEvidence
            .GroupBy(e => $"{Scalar(e.Detail?["fromQuestId"])}\u001f{Scalar(e.Detail?["toQuestId"])}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new JsonObject
            {
                ["from"] = group.First().Detail?["fromQuestId"]?.DeepClone(),
                ["to"] = group.First().Detail?["toQuestId"]?.DeepClone(),
                ["status"] = "confirmed",
                ["source"] = "MissionRuntimeAsset.prevQuestIdList",
                ["files"] = new JsonArray(group.Select(e => e.File).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray()),
                ["sourceLayers"] = new JsonArray(group.Select(e => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray()),
            }).ToArray());
        var questScenes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Link(string? questId, string? sceneId)
        {
            if (string.IsNullOrWhiteSpace(questId) || string.IsNullOrWhiteSpace(sceneId)) return;
            sceneId = Normalize(sceneId);
            if (!scenes.ContainsKey(sceneId)) return;
            if (!questScenes.TryGetValue(questId, out HashSet<string>? values)) questScenes[questId] = values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            values.Add(sceneId);
        }
        foreach (StoryEvidence evidence in repo.Evidence.Where(e => e.Kind == "trigger-scene-link" && EvidenceBelongs(e, mission)))
            Link(Scalar(evidence.Detail?["questId"]), evidence.SceneId);
        foreach (StoryEvidence evidence in repo.Evidence.Where(e => e.Kind == "client-action-story-ref" && EvidenceBelongs(e, mission)))
        {
            Match quest = Regex.Match(Scalar(evidence.Detail?["path"]) ?? string.Empty, @"\.questDic\.(?<id>[^.\[]+)", RegexOptions.IgnoreCase);
            if (quest.Success) Link(quest.Groups["id"].Value, evidence.SceneId);
        }
        var trackedNpc = repo.Evidence.Where(e => e.Kind == "quest-tracking-npc" && EvidenceBelongs(e, mission))
            .Select(e => (Quest: Scalar(e.Detail?["questId"]), Npc: Scalar(e.Detail?["npcProxyId"]))).Where(value => value.Quest is not null && value.Npc is not null).ToArray();
        var npcDialogs = repo.Evidence.Where(e => e.Kind == "npc-dialog-link" && EvidenceBelongs(e, mission))
            .Select(e => (Npc: Scalar(e.Detail?["npcProxyId"]), Scene: e.SceneId)).Where(value => value.Npc is not null && value.Scene is not null).ToArray();
        foreach (var tracking in trackedNpc)
        foreach (var dialog in npcDialogs.Where(value => string.Equals(value.Npc, tracking.Npc, StringComparison.OrdinalIgnoreCase)))
            Link(tracking.Quest, dialog.Scene);
        foreach (StoryEvidence quest in questEvidence)
        {
            string? fromQuest = Scalar(quest.Detail?["fromQuestId"]), toQuest = Scalar(quest.Detail?["toQuestId"]);
            if (fromQuest is null || toQuest is null || !questScenes.TryGetValue(fromQuest, out HashSet<string>? fromScenes) || !questScenes.TryGetValue(toQuest, out HashSet<string>? toScenes)) continue;
            foreach (string from in fromScenes)
            foreach (string to in toScenes)
                if (!from.Equals(to, StringComparison.OrdinalIgnoreCase)) edges.Add(new JsonObject
                {
                    ["from"] = from,
                    ["to"] = to,
                    ["kind"] = "questProgression",
                    ["status"] = "inferred",
                    ["source"] = "quest prerequisite plus trigger/NPC scene links",
                    ["evidence"] = new JsonObject { ["fromQuestId"] = fromQuest, ["toQuestId"] = toQuest },
                });
        }
        var branches = new JsonArray();
        foreach (JsonObject scene in scenes.Values)
        foreach (JsonObject choice in (scene["branches"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string branchId = choice["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
            var targets = SceneReferences(choice["value"], mission).Where(scenes.ContainsKey).ToArray();
            branches.Add(new JsonObject { ["id"] = branchId, ["from"] = scene["id"]?.DeepClone(), ["choice"] = choice.DeepClone(), ["targets"] = new JsonArray(targets.Select(value => JsonValue.Create(value)).ToArray()) });
            foreach (string target in targets) edges.Add(new JsonObject { ["from"] = scene["id"]?.DeepClone(), ["to"] = target, ["kind"] = "choice", ["branch"] = branchId, ["status"] = "confirmed", ["source"] = choice["source"]?.DeepClone() });
        }
        var connected = edges.OfType<JsonObject>().SelectMany(e => new[] { Scalar(e["from"]), Scalar(e["to"]) }).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disconnected = new JsonArray(scenes.Keys.Where(id => !connected.Contains(id)).Order(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray());
        var questMembership = new JsonArray(questScenes.SelectMany(pair => pair.Value.Select(scene => new JsonObject { ["questId"] = pair.Key, ["sceneId"] = scene })).ToArray());
        return new JsonObject
        {
            ["missionId"] = mission,
            ["presentation"] = "Confirmed edges require direct authored order. Quest/trigger/NPC joins are marked inferred; ID order is never chronology.",
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["branches"] = branches,
            ["questEdges"] = questEdges,
            ["questSceneMembership"] = questMembership,
            ["branchNodes"] = new JsonArray(scenes.Values.Where(s => (s["branches"] as JsonArray)?.Count > 0).Select(s => s["id"]!.DeepClone()).ToArray()),
            ["parallelNodes"] = new JsonArray(scenes.Values.Where(HasParallelTimelineTracks).Select(s => s["id"]!.DeepClone()).ToArray()),
            ["disconnected"] = disconnected,
            ["unresolved"] = new JsonArray(nodes.OfType<JsonObject>().Where(n => n["status"]?.GetValue<string>() == "unresolved").Select(n => n["id"]!.DeepClone()).ToArray()),
        };
    }

    private static bool HasParallelTimelineTracks(JsonObject scene)
    {
        if (scene["evidence"] is not JsonObject evidence || evidence["timeline"] is not JsonArray timelines) return false;
        JsonObject[] entries = timelines.OfType<JsonObject>().ToArray();
        if (entries.Any(item => item["orderGroups"] is JsonArray groups
                && groups.OfType<JsonObject>().Any(group => (group["lineIds"] as JsonArray)?.Count > 1))) return true;
        return entries
            .Where(item => item["start"] is not null && Scalar(item["id"]) is string id && !id.StartsWith('#'))
            .GroupBy(CutsceneVariantKey, StringComparer.Ordinal)
            .Any(variant => variant.GroupBy(item => Number(item["start"]))
                .Any(group => group.Key is not null
                    && group.Select(item => Scalar(item["id"])).Where(id => id is not null).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any()));
    }

    private static void ApplySceneOrder(JsonObject graph, IReadOnlyDictionary<string, JsonObject> scenes)
    {
        string[] ids = scenes.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var successors = ids.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject edge in (graph["edges"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string? status = Scalar(edge["status"]), from = Scalar(edge["from"]), to = Scalar(edge["to"]);
            if (status is not ("confirmed" or "inferred") || from is null || to is null || from.Equals(to, StringComparison.OrdinalIgnoreCase)
                || !successors.ContainsKey(from) || !successors.ContainsKey(to)) continue;
            successors[from].Add(to);
        }

        var components = StronglyConnectedComponents(ids, successors);
        var componentById = components.SelectMany((component, index) => component.Select(id => (id, index)))
            .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var componentPredecessors = Enumerable.Range(0, components.Count)
            .ToDictionary(index => index, _ => new HashSet<int>());
        foreach (string from in ids)
        foreach (string to in successors[from])
            if (componentById[from] != componentById[to]) componentPredecessors[componentById[to]].Add(componentById[from]);

        var remaining = Enumerable.Range(0, components.Count).ToHashSet();
        var groups = new JsonArray();
        var flattened = new List<string>();
        bool cyclic = components.Any(component => component.Count > 1);
        while (remaining.Count > 0)
        {
            int[] readyComponents = remaining.Where(index => componentPredecessors[index].All(previous => !remaining.Contains(previous)))
                .OrderBy(index => components[index][0], StringComparer.OrdinalIgnoreCase).ToArray();
            string[] ready = readyComponents.SelectMany(index => components[index]).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            groups.Add(new JsonArray(ready.Select(value => JsonValue.Create(value)).ToArray()));
            flattened.AddRange(ready);
            foreach (int index in readyComponents) remaining.Remove(index);
        }
        bool total = !cyclic && groups.OfType<JsonArray>().All(group => group.Count == 1);
        graph["sceneOrder"] = new JsonObject
        {
            ["status"] = total ? "total-by-evidence-graph" : cyclic ? "partial-with-cycle" : "partial-by-evidence-graph",
            ["groups"] = groups,
            ["cycles"] = new JsonArray(components.Where(component => component.Count > 1)
                .Select(component => new JsonArray(component.Select(value => JsonValue.Create(value)).ToArray())).ToArray()),
            ["warning"] = total ? null : "Scenes in the same group are not chronologically ordered. The scenes array remains deterministic, not asserted chronology.",
        };
        IEnumerable<string> outputIds = total ? flattened : ids;
        graph["scenes"] = new JsonArray(outputIds.Select(id => scenes[id].DeepClone()).ToArray());
    }

    private static List<List<string>> StronglyConnectedComponents(
        IReadOnlyList<string> ids,
        IReadOnlyDictionary<string, HashSet<string>> successors)
    {
        int nextIndex = 0;
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<List<string>>();
        foreach (string id in ids)
            if (!indexById.ContainsKey(id)) Visit(id);
        return result;

        void Visit(string id)
        {
            indexById[id] = lowById[id] = nextIndex++;
            stack.Push(id);
            onStack.Add(id);
            foreach (string next in successors[id].Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!indexById.ContainsKey(next))
                {
                    Visit(next);
                    lowById[id] = Math.Min(lowById[id], lowById[next]);
                }
                else if (onStack.Contains(next)) lowById[id] = Math.Min(lowById[id], indexById[next]);
            }
            if (lowById[id] != indexById[id]) return;
            var component = new List<string>();
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            } while (!current.Equals(id, StringComparison.OrdinalIgnoreCase));
            component.Sort(StringComparer.OrdinalIgnoreCase);
            result.Add(component);
        }
    }

    private static void NormalizeLocalizationIds(IEnumerable<JsonObject> scenes)
    {
        foreach (JsonObject scene in scenes) Visit(scene);
        static void Visit(JsonNode? node)
        {
            if (node is JsonObject map)
            {
                if (map["id"] is JsonValue idValue)
                {
                    if (idValue.TryGetValue<long>(out long longId)
                        && (map.ContainsKey("text") || longId is > 9_007_199_254_740_991L or < -9_007_199_254_740_991L))
                        map["id"] = longId.ToString(CultureInfo.InvariantCulture);
                    else if (idValue.TryGetValue<ulong>(out ulong ulongId)
                        && (map.ContainsKey("text") || ulongId > 9_007_199_254_740_991UL))
                        map["id"] = ulongId.ToString(CultureInfo.InvariantCulture);
                }
                if (map["pathId"] is JsonValue pathValue)
                {
                    if (pathValue.TryGetValue<long>(out long pathId)) map["pathId"] = pathId.ToString(CultureInfo.InvariantCulture);
                    else if (pathValue.TryGetValue<ulong>(out ulong unsignedPathId)) map["pathId"] = unsignedPathId.ToString(CultureInfo.InvariantCulture);
                }
                foreach (JsonNode? child in map.Select(pair => pair.Value).ToArray()) Visit(child);
            }
            else if (node is JsonArray array)
                foreach (JsonNode? child in array) Visit(child);
        }
    }

    private static void AddLine(JsonObject scene, string id, int? order, JsonNode? value, string source) => ((JsonArray)scene["lines"]!).Add(new JsonObject { ["id"] = id, ["order"] = order, ["sourceOrder"] = order, ["value"] = value?.DeepClone(), ["source"] = source });
    private static void Sort(JsonArray? items) { if (items is null) return; var ordered = items.OfType<JsonObject>().OrderBy(x => x["order"]?.GetValue<int?>() ?? int.MaxValue).ToArray(); items.Clear(); foreach (var x in ordered) items.Add(x); }
    private static (string Id, string Kind, string Mission)? ParseScene(string id) { var m = Scene.Match(Normalize(id)); return m.Success ? (Normalize(id), Normalize(m.Groups["kind"].Value), m.Groups["mission"].Value) : null; }
    private static string? SceneKind(string id)
    {
        string normalized = Normalize(id);
        foreach (string kind in new[] { "remotecomm", "cs_video", "cutscene", "radio", "black", "sns", "dlg" })
            if (normalized.StartsWith(kind + "_", StringComparison.OrdinalIgnoreCase)) return kind;
        return null;
    }
    private static string? MissionFromId(string id)
    {
        Match match = MissionToken.Match(id);
        return match.Success ? match.Groups["mission"].Value : null;
    }
    private static string Normalize(string id)
    {
        foreach (string prefix in new[] { "fm_", "f_", "m_" })
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Scene.IsMatch(id[prefix.Length..])) return id[prefix.Length..];
        return id;
    }
    private static bool Belongs(string id, string mission) => id.Contains($"_{mission}_", StringComparison.OrdinalIgnoreCase) || id.EndsWith("_" + mission, StringComparison.OrdinalIgnoreCase);
    private static bool EvidenceBelongs(StoryEvidence evidence, string mission)
    {
        string? evidenceMission = Scalar(evidence.Detail?["missionId"]);
        return !string.IsNullOrWhiteSpace(evidenceMission)
            ? string.Equals(evidenceMission, mission, StringComparison.OrdinalIgnoreCase)
            : evidence.SceneId is not null && Belongs(evidence.SceneId, mission);
    }
    private static int? Int(string? value) => int.TryParse(value, out int n) ? n : null;
    private static double? Number(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<double>(out double number)) return number;
        return node is JsonValue text && text.TryGetValue<string>(out string? raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }
    private static string? Scalar(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node?.ToJsonString().Trim('"');
    private static IEnumerable<string> SceneReferences(JsonNode? node, string mission)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            foreach (Match match in Scene.Matches(text))
                if (match.Groups["mission"].Value.Equals(mission, StringComparison.OrdinalIgnoreCase)) yield return match.Value;
        }
        else if (node is JsonObject map) foreach (var item in map) foreach (string found in SceneReferences(item.Value, mission)) yield return found;
        else if (node is JsonArray array) foreach (var item in array) foreach (string found in SceneReferences(item, mission)) yield return found;
    }
    private static string UnescapePrintableUnicode(string json)
        => Regex.Replace(json, @"\\u(?<code>[0-9a-fA-F]{4})", match =>
        {
            char value = (char)Convert.ToInt32(match.Groups["code"].Value, 16);
            return value >= ' ' && !char.IsSurrogate(value) && value is not '"' and not '\\'
                ? value.ToString()
                : match.Value;
        });

    private static void Write(string path, JsonNode node) => File.WriteAllText(path, UnescapePrintableUnicode(node.ToJsonString(JsonOptions)));
}
