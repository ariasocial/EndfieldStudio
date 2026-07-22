using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;

namespace AnimeStudio.Endfield.Cli;

/// <summary>
/// Builds one JSON document per DialogTextTable scene.
///
/// The export is table-backed and augments the source rows with Timeline
/// evidence when available. Numeric row order remains the documented fallback
/// when a matching Timeline cannot be recovered.
/// </summary>
internal static class DialogExporter
{
    private static readonly HashSet<string> RequiredTables = new(StringComparer.Ordinal)
    {
        "TextTable",
        "DialogTextTable",
        "DialogOptionTable",
        "DialogSummaryTable",
    };

    private static readonly Regex DialogLinePattern = new(
        @"^dlg_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)_(?<line>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DialogOptionPattern = new(
        @"^option_dlg_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)_(?<group>\d+)_(?<option>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DialogSummaryPattern = new(
        @"^summary_(?<mission>.+)_(?<scene>\d+(?:d\d+)?)_(?<index>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class Options
    {
        public string? VfsPath;
        public string? BaseVfsPath;
        public string? OutPath;
        public string Language = "CN";
        public string? DialogId;
        public bool Snapshot;
        public bool Help;
    }

    private sealed class TableSet
    {
        public Dictionary<string, JsonObject> Tables { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<string>> Sources { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = new();
    }

    public static int Run(ReadOnlySpan<string> args)
    {
        var options = ParseArgs(args);
        if (options.Help) return 0;
        if (options.VfsPath is null) throw new ArgumentException("--vfs is required");
        if (options.OutPath is null) throw new ArgumentException("--out is required");

        options.Language = options.Language.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(options.Language, @"^[A-Z]{2,8}$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"Invalid --language value: {options.Language}");

        Directory.CreateDirectory(options.OutPath);
        var tableSet = LoadTables(options);
        var languages = ResolveLanguages(options.Language, tableSet);
        Dictionary<string, JsonArray> timelineEvidence = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonArray> dialogTreeEvidence = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            string scratchPath = Path.Combine(Path.GetTempPath(), "endfield-dialog-timeline");
            var timelineScan = DialogTimelineScanner.Scan(
                options.VfsPath!,
                options.BaseVfsPath,
                scratchPath);
            timelineEvidence = timelineScan.ByDialogId;
            dialogTreeEvidence = timelineScan.DialogTreesByDialogId;
            Console.WriteLine(
                $"  Timeline evidence: {timelineScan.DialogsWithEvidence:N0} dialogs, "
                + $"{timelineScan.RootsFound:N0} roots, {timelineScan.GraphObjectsRead:N0} graph objects, "
                + $"DialogTree={timelineScan.DialogsWithDialogTreeEvidence:N0} dialogs, "
                + $"{timelineScan.BundlesFailed:N0} bundle failures");
            if (timelineScan.BundlesFailed > 0)
                tableSet.Warnings.Add($"Timeline scan skipped {timelineScan.BundlesFailed:N0} Bundle file(s).");
        }
        catch (DirectoryNotFoundException)
        {
            tableSet.Warnings.Add("Bundle block was not found; Timeline evidence was skipped.");
        }
        catch (FileNotFoundException ex)
        {
            tableSet.Warnings.Add($"Bundle block could not be read; Timeline evidence was skipped: {ex.Message}");
        }
        catch (Exception ex)
        {
            tableSet.Warnings.Add($"Timeline scan failed; table-backed export was retained: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(options.DialogId))
        {
            string dialogId = options.DialogId.Trim();
            string? snapshotTimestamp = options.Snapshot ? DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : null;
            int languageFilesWritten = 0;
            foreach (string language in languages)
            {
                var dialogs = BuildDialogs(tableSet, language, timelineEvidence, dialogTreeEvidence);
                if (!dialogs.TryGetValue(dialogId, out var payload))
                    continue;
                WriteDialog(options.OutPath, dialogId, language, payload, snapshotTimestamp);
                languageFilesWritten++;
            }
            if (languageFilesWritten == 0)
                throw new ArgumentException($"Dialog not found: {dialogId}");
            Console.WriteLine($"  Wrote {languageFilesWritten:N0} language file(s) for dialog: {dialogId}");
            return 0;
        }

        string? allSnapshotTimestamp = options.Snapshot
            ? DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            : null;
        int written = 0;
        foreach (string language in languages)
        {
            var dialogs = BuildDialogs(tableSet, language, timelineEvidence, dialogTreeEvidence);
            foreach (var (dialogId, payload) in dialogs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteDialog(options.OutPath, dialogId, language, payload, allSnapshotTimestamp);
                written++;
            }
        }

        Console.WriteLine($"  Wrote {written:N0} language file(s) to {Path.GetFullPath(options.OutPath)}");
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
                case "--dialog": options.DialogId = RequireValue(args, ref i, arg); break;
                case "--snapshot": options.Snapshot = true; break;
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
        Console.WriteLine("  endfield-dump dialog --vfs <path> --out <dir> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --base-vfs <path>    Base VFS used for hot-update fallback");
        Console.WriteLine("  --language <code|all> Localization code (default: CN; all=every available language)");
        Console.WriteLine("  --dialog <id>        Export one dialog instead of all dialogs");
        Console.WriteLine("  --snapshot           Write <language>_snapshot<timestamp>.json");
    }

    private static TableSet LoadTables(Options options)
    {
        var result = new TableSet();
        var primary = new VfsLoader(options.VfsPath!, Keys.ChaCha20Key, options.BaseVfsPath);

        // Load the base independently as well. Persistent metadata is not
        // necessarily a complete union of the base game's table rows.
        if (!string.IsNullOrWhiteSpace(options.BaseVfsPath))
        {
            var baseLoader = new VfsLoader(options.BaseVfsPath!, Keys.ChaCha20Key);
            LoadTableSource(baseLoader, "base", result);
        }
        LoadTableSource(primary, "primary", result);

        if (!string.Equals(options.Language, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            string i18nName = $"I18nTextTable_{options.Language}";
            if (!result.Tables.ContainsKey(i18nName))
                result.Warnings.Add($"Localization table not found: {i18nName}");
        }
        foreach (string tableName in RequiredTables)
        {
            if (!result.Tables.ContainsKey(tableName))
                result.Warnings.Add($"Required table not found: {tableName}");
        }

        return result;
    }

    private static List<string> ResolveLanguages(string requested, TableSet tableSet)
    {
        if (!string.Equals(requested, "ALL", StringComparison.OrdinalIgnoreCase))
            return new List<string> { requested };

        const string prefix = "I18nTextTable_";
        var languages = tableSet.Tables.Keys
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => name[prefix.Length..])
            .Where(code => Regex.IsMatch(code, @"^[A-Z]{2,8}$", RegexOptions.CultureInvariant))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
        if (languages.Count == 0)
            throw new InvalidOperationException("No localization tables were found for --language all");
        return languages;
    }

    private static void LoadTableSource(VfsLoader loader, string sourceName, TableSet result)
    {
        BlockMainInfo info;
        try
        {
            info = loader.LoadBlockInfo(BlockType.Table);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (FileNotFoundException)
        {
            return;
        }

        foreach (var chunk in info.Chunks)
        {
            foreach (var file in chunk.Files)
            {
                try
                {
                    var (rootName, json) = SparkBuffer.Parse(loader.ExtractFileToBytes(BlockType.Table, chunk, file));
                    if (!RequiredTables.Contains(rootName) && !rootName.StartsWith("I18nTextTable_", StringComparison.Ordinal))
                        continue;

                    if (JsonNode.Parse(json) is not JsonObject incoming)
                    {
                        result.Warnings.Add($"Table is not an object: {rootName} ({sourceName})");
                        continue;
                    }

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
                catch (FileNotFoundException ex)
                {
                    result.Warnings.Add($"Table chunk missing: {ex.Message}");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Table parse failed ({sourceName}): {ex.Message}");
                }
            }
        }
    }

    private static Dictionary<string, JsonObject> BuildDialogs(
        TableSet tableSet,
        string language,
        IReadOnlyDictionary<string, JsonArray> timelineEvidence,
        IReadOnlyDictionary<string, JsonArray> dialogTreeEvidence)
    {
        var dialogs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var dialogRows = GetTable(tableSet, "DialogTextTable");
        var optionRows = GetTable(tableSet, "DialogOptionTable");
        var summaryRows = GetTable(tableSet, "DialogSummaryTable");
        var textRows = GetTable(tableSet, "TextTable");
        var i18nRows = GetTable(tableSet, $"I18nTextTable_{language}");

        foreach (var (rowId, rowNode) in dialogRows)
        {
            if (rowNode is not JsonObject row) continue;
            var match = DialogLinePattern.Match(rowId);
            if (!match.Success) continue;

            string mission = match.Groups["mission"].Value;
            string scene = match.Groups["scene"].Value;
            string dialogId = $"dlg_{mission}_{scene}";
            int lineOrder = ParseInt(match.Groups["line"].Value);

            if (!dialogs.TryGetValue(dialogId, out var payload))
            {
                payload = CreateDialogPayload(dialogId, mission, scene, textRows, i18nRows, tableSet, language);
                dialogs[dialogId] = payload;
            }

            var lines = (JsonArray)payload["lines"]!;
            lines.Add(new JsonObject
            {
                ["id"] = rowId,
                ["order"] = lineOrder,
                ["actorId"] = StringOrNull(row, "actorNameId"),
                ["actor"] = ResolveText(row["actorName"], i18nRows),
                ["text"] = ResolveText(row["dialogText"], i18nRows),
                ["hint"] = ResolveText(row["hint"], i18nRows),
                ["audio"] = StringOrNull(row, "audioOverride"),
                ["emotion"] = row["emotionType"]?.DeepClone(),
                ["source"] = SourceRef("DialogTextTable", rowId, tableSet),
            });
        }

        foreach (var (rowId, rowNode) in optionRows)
        {
            if (rowNode is not JsonObject row) continue;
            var match = DialogOptionPattern.Match(rowId);
            if (!match.Success) continue;

            string mission = match.Groups["mission"].Value;
            string scene = match.Groups["scene"].Value;
            string dialogId = $"dlg_{mission}_{scene}";
            if (!dialogs.TryGetValue(dialogId, out var payload)) continue;

            int groupOrder = ParseInt(match.Groups["group"].Value);
            int optionOrder = ParseInt(match.Groups["option"].Value);
            var groups = (JsonArray)payload["optionGroups"]!;
            var group = groups.OfType<JsonObject>().FirstOrDefault(item =>
                item["order"]?.GetValue<int>() == groupOrder);
            if (group is null)
            {
                group = new JsonObject
                {
                    ["order"] = groupOrder,
                    ["options"] = new JsonArray(),
                };
                groups.Add(group);
            }

            ((JsonArray)group["options"]!).Add(new JsonObject
            {
                ["id"] = rowId,
                ["order"] = optionOrder,
                ["text"] = ResolveText(row["optionText"] ?? row["optionDesc"], i18nRows),
                ["source"] = SourceRef("DialogOptionTable", rowId, tableSet),
            });
        }

        foreach (var (rowId, rowNode) in summaryRows)
        {
            if (rowNode is not JsonObject row) continue;
            var match = DialogSummaryPattern.Match(rowId);
            if (!match.Success) continue;

            string dialogId = $"dlg_{match.Groups["mission"].Value}_{match.Groups["scene"].Value}";
            if (!dialogs.TryGetValue(dialogId, out var payload)) continue;

            ((JsonArray)payload["summary"]!).Add(new JsonObject
            {
                ["id"] = rowId,
                ["order"] = ParseInt(match.Groups["index"].Value),
                ["text"] = ResolveText(row["id"], i18nRows),
                ["source"] = SourceRef("DialogSummaryTable", rowId, tableSet),
            });
        }

        foreach (var payload in dialogs.Values)
        {
            SortArray(payload["lines"] as JsonArray, "order");
            SortArray(payload["summary"] as JsonArray, "order");
            SortArray(payload["optionGroups"] as JsonArray, "order");
            foreach (var group in (payload["optionGroups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                SortArray(group["options"] as JsonArray, "order");

            ApplyTimelineEvidence(payload, timelineEvidence);
            ApplyDialogTreeEvidence(payload, dialogTreeEvidence);
            var warnings = (JsonArray)payload["warnings"]!;
            foreach (string warning in tableSet.Warnings)
                if (!warning.StartsWith("Table parse failed", StringComparison.Ordinal))
                    warnings.Add(warning);
            if (payload["mission"] is not JsonObject mission || mission["name"] is null)
                warnings.Add("Mission display name was not found in TextTable/I18nTextTable.");
        }

        return dialogs;
    }

    private static void ApplyDialogTreeEvidence(
        JsonObject payload,
        IReadOnlyDictionary<string, JsonArray> dialogTreeEvidence)
    {
        string dialogId = payload["dialogId"]?.GetValue<string>() ?? string.Empty;
        if (!dialogTreeEvidence.TryGetValue(dialogId, out var trees) || trees.Count == 0)
            return;

        var sources = (JsonObject)payload["sources"]!;
        var warnings = (JsonArray)payload["warnings"]!;
        sources["dialogTree"] = trees.DeepClone();

        var lineIds = ((JsonArray)payload["lines"]!)
            .OfType<JsonObject>()
            .Select(line => line["id"]?.GetValue<string>())
            .Where(id => id is not null)
            .Select(id => id!)
            .ToArray();
        var best = trees.OfType<JsonObject>()
            .Select(tree =>
            {
                var entries = ((JsonArray)tree["lineIds"]!).OfType<JsonValue>()
                    .Select(value => value.TryGetValue<string>(out var id) ? id : null)
                    .Where(id => id is not null)
                    .Select(id => id!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select((id, index) => (id, index))
                    .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
                return (tree, entries, matched: lineIds.Count(entries.ContainsKey));
            })
            .OrderByDescending(item => item.matched)
            .ThenByDescending(item => item.entries.Count)
            .FirstOrDefault();
        if (best.tree is null || best.matched == 0) return;

        var branches = best.tree["branches"] as JsonArray ?? new JsonArray();
        if (branches.Count > 0)
        {
            payload["branchRoutes"] = branches.DeepClone();
            foreach (JsonObject group in ((JsonArray)payload["optionGroups"]!).OfType<JsonObject>())
            foreach (JsonObject option in ((JsonArray)group["options"]!).OfType<JsonObject>())
            {
                string? optionId = option["id"]?.GetValue<string>();
                if (optionId is null) continue;
                JsonObject? route = branches.OfType<JsonObject>().FirstOrDefault(branch =>
                    string.Equals(branch["optionId"]?.GetValue<string>(), optionId, StringComparison.OrdinalIgnoreCase));
                if (route is not null) option["dialogTreeRoute"] = route.DeepClone();
            }
            warnings.Add("DialogTree branch routes were recovered; branch-specific progression is represented as routes, not one global line order.");
        }

        foreach (JsonObject line in ((JsonArray)payload["lines"]!).OfType<JsonObject>())
        {
            string? id = line["id"]?.GetValue<string>();
            if (id is not null && best.entries.TryGetValue(id, out int order))
                line["dialogTreeOrder"] = order;
        }

        if (best.matched == lineIds.Length && branches.Count == 0)
        {
            foreach (JsonObject line in ((JsonArray)payload["lines"]!).OfType<JsonObject>())
            {
                string? id = line["id"]?.GetValue<string>();
                if (id is not null && best.entries.TryGetValue(id, out int order))
                {
                    line["tableOrder"] = line["order"]?.DeepClone();
                    line["order"] = order;
                }
            }
            SortArray((JsonArray)payload["lines"]!, "order");
            ((JsonObject)sources["order"]!)["method"] = "DialogTree graph order";
            ((JsonObject)sources["order"]!)["confidence"] = "recovered";
        }
    }

    private static void ApplyTimelineEvidence(
        JsonObject payload,
        IReadOnlyDictionary<string, JsonArray> timelineEvidence)
    {
        string dialogId = payload["dialogId"]?.GetValue<string>() ?? string.Empty;
        var warnings = (JsonArray)payload["warnings"]!;
        var sources = (JsonObject)payload["sources"]!;
        if (!timelineEvidence.TryGetValue(dialogId, out var timelines) || timelines.Count == 0)
        {
            ((JsonObject)sources["order"]!)["method"] = "DialogTextTable numeric row suffix";
            ((JsonObject)sources["order"]!)["confidence"] = "fallback";
            warnings.Add("Timeline/DialogTree evidence was not found; line order uses DialogTextTable numeric row suffixes.");
            return;
        }

        sources["timeline"] = timelines.DeepClone();
        var runtimeJumpClips = timelines.OfType<JsonObject>()
            .SelectMany(timeline => (timeline["runtimeJumpClips"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .ToArray();
        if (runtimeJumpClips.Length > 0)
        {
            sources["runtimeJump"] = new JsonArray(runtimeJumpClips.Select(item => item.DeepClone()).ToArray());
            warnings.Add("Runtime Jump Track evidence was recovered; option routing is retained as raw evidence and is not promoted to a single global order.");
        }
        var timelineOptions = timelines.OfType<JsonObject>()
            .SelectMany(timeline => ((JsonArray)timeline["options"]!).OfType<JsonObject>())
            .Where(option => option["id"]?.GetValue<string>() is not null)
            .GroupBy(option => option["id"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject group in ((JsonArray)payload["optionGroups"]!).OfType<JsonObject>())
        foreach (JsonObject option in ((JsonArray)group["options"]!).OfType<JsonObject>())
        {
            string? id = option["id"]?.GetValue<string>();
            if (id is null || !timelineOptions.TryGetValue(id, out var timelineOption)) continue;
            option["timelineOrder"] = timelineOption["clipOrder"]?.DeepClone();
            option["timelineStart"] = timelineOption["start"]?.DeepClone();
        }

        var lines = (JsonArray)payload["lines"]!;
        var lineIds = lines.OfType<JsonObject>()
            .Select(line => line["id"]?.GetValue<string>())
            .Where(id => id is not null)
            .Select(id => id!)
            .ToArray();
        var best = timelines.OfType<JsonObject>()
            .Select(timeline =>
            {
                var entries = ((JsonArray)timeline["lines"]!).OfType<JsonObject>()
                    .Select((line, index) => (line, index))
                    .Where(item => item.line["id"]?.GetValue<string>() is not null)
                    .GroupBy(item => item.line["id"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
                int matched = lineIds.Count(id => entries.ContainsKey(id));
                bool unique = entries.Count == ((JsonArray)timeline["lines"]!).Count;
                return (timeline, entries, matched, unique);
            })
            .OrderByDescending(item => item.matched)
            .ThenByDescending(item => item.unique)
            .FirstOrDefault();

        if (best.timeline is null || best.matched == 0)
        {
            ((JsonObject)sources["order"]!)["method"] = "Timeline evidence (unmatched)";
            ((JsonObject)sources["order"]!)["confidence"] = "unmatched";
            warnings.Add("Timeline objects were found, but no DialogTextTable line IDs could be matched.");
            return;
        }

        foreach (JsonObject line in lines.OfType<JsonObject>())
        {
            string? id = line["id"]?.GetValue<string>();
            if (id is not null && best.entries.TryGetValue(id, out int timelineOrder))
                line["timelineOrder"] = timelineOrder;
        }

        if (best.matched == lineIds.Length && best.unique)
        {
            foreach (JsonObject line in lines.OfType<JsonObject>())
            {
                string? id = line["id"]?.GetValue<string>();
                if (id is not null && best.entries.TryGetValue(id, out int timelineOrder))
                {
                    line["tableOrder"] = line["order"]?.DeepClone();
                    line["order"] = timelineOrder;
                }
            }
            SortArray(lines, "order");
            ((JsonObject)sources["order"]!)["method"] = "Timeline m_Start/track/clip order";
            ((JsonObject)sources["order"]!)["confidence"] = "recovered";
            warnings.Add("Line order was recovered from serialized Timeline evidence; server-side progression is not represented.");
        }
        else
        {
            ((JsonObject)sources["order"]!)["method"] = "DialogTextTable order + partial Timeline evidence";
            ((JsonObject)sources["order"]!)["confidence"] = "partial";
            warnings.Add($"Timeline evidence matched {best.matched}/{lineIds.Length} line(s); table order was retained.");
        }
    }

    private static JsonObject CreateDialogPayload(
        string dialogId,
        string mission,
        string scene,
        Dictionary<string, JsonNode?> textRows,
        Dictionary<string, JsonNode?> i18nRows,
        TableSet tableSet,
        string language)
    {
        string missionNameRowId = $"{mission}_name";
        JsonObject? missionNameRow = textRows.GetValueOrDefault(missionNameRowId) as JsonObject;
        string? missionName = missionNameRow is null ? null : ResolveText(missionNameRow["id"], i18nRows);

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["dialogId"] = dialogId,
            ["language"] = language,
            ["kind"] = "dlg",
            ["mission"] = new JsonObject
            {
                ["id"] = mission,
                ["name"] = missionName,
                ["nameSource"] = missionName is null
                    ? null
                    : SourceRef("TextTable", missionNameRowId, tableSet),
            },
            ["scene"] = scene,
            ["lines"] = new JsonArray(),
            ["summary"] = new JsonArray(),
            ["optionGroups"] = new JsonArray(),
            ["sources"] = new JsonObject
            {
                ["tables"] = new JsonArray(tableSet.Sources.Keys.OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => JsonValue.Create(value)).ToArray()),
                ["order"] = new JsonObject
                {
                    ["method"] = "DialogTextTable numeric row suffix",
                    ["confidence"] = "fallback",
                },
            },
            ["warnings"] = new JsonArray(),
        };
    }

    private static JsonObject SourceRef(string table, string rowId, TableSet tableSet)
    {
        var source = new JsonObject
        {
            ["table"] = table,
            ["rowId"] = rowId,
        };
        if (tableSet.Sources.TryGetValue(table, out var sources))
            source["sources"] = new JsonArray(sources.Select(value => JsonValue.Create(value)).ToArray());
        return source;
    }

    private static Dictionary<string, JsonNode?> GetTable(TableSet tableSet, string name)
        => tableSet.Tables.TryGetValue(name, out var table)
            ? table.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    private static string? ResolveText(JsonNode? raw, Dictionary<string, JsonNode?> i18nRows)
    {
        if (raw is null) return null;
        if (raw is JsonValue value && value.TryGetValue<string>(out var direct)) return direct;

        string? id = raw switch
        {
            JsonObject obj => StringOrNull(obj, "id") ?? StringOrNull(obj, "key"),
            JsonValue value => ScalarString(value),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (!i18nRows.TryGetValue(id, out var row) || row is null) return null;

        if (row is JsonValue localizedValue && localizedValue.TryGetValue<string>(out var localizedText))
            return localizedText;

        foreach (string field in new[] { "text", "value", "content", "str" })
        {
            string? text = row is JsonObject localizedRow ? StringOrNull(localizedRow, field) : null;
            if (text is not null) return text;
        }
        return null;
    }

    private static string? StringOrNull(JsonObject row, string property)
    {
        if (!row.TryGetPropertyValue(property, out var value) || value is null) return null;
        if (value is not JsonValue jsonValue) return null;
        return ScalarString(jsonValue);
    }

    private static string? ScalarString(JsonValue jsonValue)
    {
        if (jsonValue.TryGetValue<string>(out var text)) return text;
        if (jsonValue.TryGetValue<long>(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);
        if (jsonValue.TryGetValue<ulong>(out var unsignedInteger))
            return unsignedInteger.ToString(CultureInfo.InvariantCulture);
        if (jsonValue.TryGetValue<decimal>(out var decimalValue))
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) ? result : 0;

    private static void SortArray(JsonArray? array, string property)
    {
        if (array is null || array.Count < 2) return;
        var sorted = array.OfType<JsonObject>()
            .OrderBy(item => item[property]?.GetValue<int>() ?? 0)
            .ThenBy(item => item["id"]?.GetValue<string>(), StringComparer.Ordinal)
            .ToArray();
        array.Clear();
        foreach (var item in sorted) array.Add(item);
    }

    private static void WriteDialog(string outPath, string dialogId, string language, JsonObject payload, string? snapshotTimestamp)
    {
        string fileName = snapshotTimestamp is not null
            ? $"{language}_snapshot{snapshotTimestamp}.json"
            : $"{language}.json";
        string directory = Path.Combine(outPath, dialogId);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, fileName);
        File.WriteAllText(target, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
