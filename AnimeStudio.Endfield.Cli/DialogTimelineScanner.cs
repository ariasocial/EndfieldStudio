using System.Text.Json.Nodes;
using AnimeStudio.Endfield;
using AS = AnimeStudio;
using Newtonsoft.Json;

namespace AnimeStudio.Endfield.Cli;

/// <summary>
/// Finds Timeline object graphs inside Bundle files and converts only the
/// graph-shaped evidence needed by <see cref="DialogTimelineEvidence"/>.
/// The scan is intentionally conservative: an object is never treated as a
/// dialog line unless a serialized string contains a source-backed dlg_* ID.
/// </summary>
internal static class DialogTimelineScanner
{
    private const int MonoBehaviourClassId = 114;
    private const int PlayableDirectorClassId = 320;

    internal sealed class ScanResult
    {
        public Dictionary<string, JsonArray> ByDialogId { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long BundlesScanned { get; set; }
        public long BundlesFailed { get; set; }
        public long RootsFound { get; set; }
        public long GraphObjectsRead { get; set; }
        public long DialogsWithEvidence => ByDialogId.Count;
    }

    public static ScanResult Scan(string vfsPath, string? baseVfsPath, string scratchPath)
    {
        var result = new ScanResult();
        var loader = new VfsLoader(vfsPath, Keys.ChaCha20Key, baseVfsPath);
        BlockMainInfo block = loader.LoadBlockInfo(BlockType.Bundle);
        Directory.CreateDirectory(scratchPath);

        long total = block.Chunks.Sum(chunk => chunk.Files.Count(file => IsBundleFile(file)));
        long processed = 0;
        Console.WriteLine($"  scanning Timeline evidence from {total:N0} Bundle file(s)...");

        foreach (ChunkInfo chunk in block.Chunks)
        {
            foreach (AnimeStudio.Endfield.FileInfo file in chunk.Files)
            {
                if (!IsBundleFile(file)) continue;
                string bundleName = Path.GetFileName(file.FileName);
                string bundlePath = Path.Combine(scratchPath, bundleName);
                try
                {
                    using (var output = new FileStream(
                               bundlePath,
                               FileMode.Create,
                               FileAccess.Write,
                               FileShare.None,
                               64 * 1024,
                               FileOptions.SequentialScan))
                    {
                        loader.ExtractFile(BlockType.Bundle, chunk, file, output);
                    }

                    ScanBundle(bundlePath, result);
                    result.BundlesScanned++;
                }
                catch (FileNotFoundException ex)
                {
                    result.BundlesFailed++;
                    Console.Error.WriteLine($"  Timeline scan skipped {bundleName}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    result.BundlesFailed++;
                    Console.Error.WriteLine($"  Timeline scan failed {bundleName}: {ex.Message}");
                }
                finally
                {
                    try { File.Delete(bundlePath); } catch { }
                }

                processed++;
                if (processed % 1000 == 0 || processed == total)
                    Console.WriteLine($"  Timeline scan progress: {processed:N0}/{total:N0}; dialogs={result.DialogsWithEvidence:N0}");
            }
        }

        return result;
    }

    private static void ScanBundle(string bundlePath, ScanResult result)
    {
        var manager = new AS.AssetsManager
        {
            Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield),
            Silent = true,
            SkipProcess = true,
        };
        try
        {
            manager.LoadFiles(bundlePath);
            foreach (AS.SerializedFile serializedFile in manager.assetsFileList)
                ScanSerializedFile(serializedFile, manager.Game, result);
        }
        finally
        {
            manager.Clear();
        }
    }

    private static void ScanSerializedFile(AS.SerializedFile serializedFile, AS.Game game, ScanResult result)
    {
        var infoByPathId = serializedFile.m_Objects
            .GroupBy(info => info.m_PathID)
            .ToDictionary(group => group.Key, group => group.First());
        var rootInfos = new List<AS.ObjectInfo>();

        foreach (AS.ObjectInfo info in serializedFile.m_Objects)
        {
            if (info.classID == MonoBehaviourClassId)
            {
                string name = ReadMonoBehaviourName(serializedFile, info, game);
                if (name.Contains("dlgtl_", StringComparison.OrdinalIgnoreCase))
                    rootInfos.Add(info);
            }
            else if (info.classID == PlayableDirectorClassId)
            {
                // PlayableDirector normally points at a Timeline asset. Its
                // graph is followed below; actual line roots are still
                // selected by DialogTimelineEvidence.
                rootInfos.Add(info);
            }
        }

        result.RootsFound += rootInfos.Count;
        if (rootInfos.Count == 0) return;

        var records = new Dictionary<long, DialogTimelineRecord>();
        var pending = new Queue<AS.ObjectInfo>(rootInfos);
        var visited = new HashSet<long>();
        while (pending.Count > 0)
        {
            AS.ObjectInfo info = pending.Dequeue();
            if (!visited.Add(info.m_PathID)) continue;

            DialogTimelineRecord? record = ReadRecord(serializedFile, info, game);
            if (record is null) continue;
            records[info.m_PathID] = record;
            result.GraphObjectsRead++;

            foreach (long referenceId in FindGraphReferences(record.Payload))
            {
                if (infoByPathId.TryGetValue(referenceId, out var target))
                    pending.Enqueue(target);
            }
        }

        foreach (var (dialogId, timelines) in DialogTimelineEvidence.Recover(records.Values))
        {
            if (!result.ByDialogId.TryGetValue(dialogId, out var existing))
            {
                result.ByDialogId[dialogId] = timelines;
                continue;
            }
            foreach (JsonNode? timeline in timelines)
                existing.Add(timeline?.DeepClone());
        }
    }

    private static string ReadMonoBehaviourName(AS.SerializedFile serializedFile, AS.ObjectInfo info, AS.Game game)
    {
        try
        {
            var reader = new AS.ObjectReader(serializedFile.reader, serializedFile, info, game);
            var behaviour = new AS.MonoBehaviour(reader);
            return behaviour.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DialogTimelineRecord? ReadRecord(AS.SerializedFile serializedFile, AS.ObjectInfo info, AS.Game game)
    {
        try
        {
            var reader = new AS.ObjectReader(serializedFile.reader, serializedFile, info, game);
            AS.Object asset = info.classID == MonoBehaviourClassId
                ? new AS.MonoBehaviour(reader)
                : new AS.Object(reader);
            var type = asset.ToType();
            if (type is null) return null;

            JsonNode? payload = JsonNode.Parse(JsonConvert.SerializeObject(type));
            if (payload is not JsonObject payloadObject) return null;
            string name = asset.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = StringValue(payloadObject["m_Name"]) ?? string.Empty;
            return new DialogTimelineRecord
            {
                SourceFile = serializedFile.fileName,
                PathId = info.m_PathID,
                ClassId = info.classID,
                Name = name,
                Payload = payloadObject,
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<long> FindGraphReferences(JsonNode payload)
    {
        if (payload is not JsonObject obj) yield break;
        foreach (string property in new[] { "m_Tracks", "m_Children", "m_Clips", "m_Asset", "m_PlayableAsset" })
        {
            foreach (JsonNode? node in EnumerateNodes(obj[property]))
            {
                if (node is not JsonObject reference) continue;
                if (reference["m_FileID"] is not null
                    && Int64Value(reference["m_FileID"]) is long fileId
                    && fileId != 0)
                    continue;
                if (Int64Value(reference["m_PathID"]) is long pathId && pathId != 0)
                    yield return pathId;
            }
        }
    }

    private static IEnumerable<JsonNode?> EnumerateNodes(JsonNode? node)
    {
        if (node is null) yield break;
        yield return node;
        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
                foreach (JsonNode? nested in EnumerateNodes(child))
                    yield return nested;
        }
        else if (node is JsonObject obj)
        {
            foreach (JsonNode? child in obj.Values)
                foreach (JsonNode? nested in EnumerateNodes(child))
                    yield return nested;
        }
    }

    private static long? Int64Value(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<long>(out long number)) return number;
        if (node is JsonValue text && text.TryGetValue<string>(out string? raw)
            && long.TryParse(raw, out number))
            return number;
        return null;
    }

    private static string? StringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static bool IsBundleFile(AnimeStudio.Endfield.FileInfo file)
        => !string.IsNullOrWhiteSpace(file.FileName)
            && !file.FileName.EndsWith('/')
            && !file.FileName.EndsWith('\\');
}
