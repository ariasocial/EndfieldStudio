using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeStudio.Endfield;
using AS = AnimeStudio;

namespace AnimeStudio.Endfield.Cli.Story.Timeline;

/// <summary>Indexed, parallel Bundle scan for source-backed story Timeline evidence.</summary>
public static class StoryTimelineScanner
{
    private const int MonoBehaviour = 114;
    private const int PlayableDirector = 320;
    private const int TextAsset = 49;
    private const long MemoryStreamLimit = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions DirectJsonOptions = new() { IncludeFields = true };

    private sealed record Candidate(
        VfsLoader Loader,
        ChunkInfo Chunk,
        AnimeStudio.Endfield.FileInfo File,
        string LogicalId,
        string ContentKey,
        StoryTimelineIndexEntry Entry);

    public static StoryTimelineScanResult Scan(
        string vfsPath,
        string? baseVfsPath,
        string scratchPath,
        string? missionFilter,
        string mode,
        string? indexPath,
        bool rebuildIndex,
        int threads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vfsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchPath);
        Directory.CreateDirectory(scratchPath);
        bool quick = mode.Equals("quick", StringComparison.OrdinalIgnoreCase);
        int workerCount = threads > 0 ? threads : Math.Min(4, Math.Max(1, Environment.ProcessorCount));
        StoryTimelineIndex? index = string.IsNullOrWhiteSpace(indexPath) ? null : new StoryTimelineIndex(indexPath, rebuildIndex);
        var inventory = BuildInventory(vfsPath, baseVfsPath, index);
        var currentIndex = new ConcurrentDictionary<string, StoryTimelineIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var toScan = new List<Candidate>();
        var result = new StoryTimelineScanResult { BundleCandidates = inventory.Count };
        object resultLock = new();
        object indexWriteLock = new();

        foreach (Candidate candidate in inventory)
        {
            if (index is not null && !rebuildIndex
                && index.TryRead(candidate.LogicalId, candidate.ContentKey, quick ? missionFilter : null, out StoryTimelineIndexEntry? cachedEntry, out StoryBundleEvidence? cachedEvidence))
            {
                StoryTimelineIndexEntry refreshed = candidate.Entry;
                StoryTimelineScanResult? cachedResult = cachedEvidence?.ToResult();
                bool emptyEvidence = cachedResult is not null && !cachedResult.HasEvidence;
                refreshed.CandidateState = emptyEvidence
                    ? (cachedEntry!.CandidateState.StartsWith("quick-", StringComparison.Ordinal) ? "quick-negative" : "no-candidate")
                    : cachedEntry!.CandidateState;
                refreshed.EvidenceFile = emptyEvidence ? null : cachedEntry.EvidenceFile;
                refreshed.QuickMission = cachedEntry.QuickMission;
                currentIndex[candidate.LogicalId] = refreshed;
                result.BundlesReused++;
                if (cachedResult is not null && !emptyEvidence) Merge(result, cachedResult, missionFilter);
                continue;
            }
            toScan.Add(candidate);
            currentIndex[candidate.LogicalId] = candidate.Entry;
        }
        result.BundlesChanged = toScan.Count;
        long completed = result.BundlesReused;
        long quickSkipped = 0;
        Console.WriteLine(
            $"  Story Timeline inventory: {inventory.Count:N0} Bundle file(s); "
            + $"cache-hit={result.BundlesReused:N0}; scan={toScan.Count:N0}; mode={mode}; threads={workerCount}");

        bool previousLoggerSilent = AS.Logger.Silent;
        bool previousProgressSilent = AS.Progress.Silent;
        AS.Logger.Silent = true;
        AS.Progress.Silent = true;
        try
        {
            Parallel.ForEach(
                toScan,
                new ParallelOptions { MaxDegreeOfParallelism = workerCount },
                () => NewManager(),
                (candidate, _, manager) =>
                {
                    StoryTimelineIndexEntry entry = candidate.Entry;
                    try
                    {
                        using Stream bundle = CreateBundleStream(candidate.File, scratchPath);
                        candidate.Loader.ExtractFile(BlockType.Bundle, candidate.Chunk, candidate.File, bundle);
                        bundle.Position = 0;
                        if (quick && !QuickMayContain(bundle, candidate.File.FileName, missionFilter))
                        {
                            // Reusable only by quick mode. Full mode deliberately
                            // treats this state as a miss to avoid false negatives.
                            if (index is not null) index.MarkQuickNegative(entry, missionFilter!);
                            else { entry.CandidateState = "quick-negative"; entry.QuickMission = missionFilter; }
                            Interlocked.Increment(ref quickSkipped);
                        }
                        else
                        {
                            var local = new StoryTimelineScanResult();
                            ScanBundle(bundle, Path.Combine(scratchPath, StableVirtualName(candidate)), manager, local, quick ? missionFilter : null, quick);
                            local.BundlesScanned = 1;
                            lock (resultLock) Merge(result, local, missionFilter);
                            if (index is not null)
                            {
                                lock (indexWriteLock)
                                {
                                    if (quick)
                                    {
                                        if (!local.HasEvidence) index.MarkQuickNegative(entry, missionFilter!);
                                        else index.WriteQuickEvidence(entry, StoryBundleEvidence.FromResult(local), missionFilter!);
                                    }
                                    else if (!local.HasEvidence) index.MarkEmpty(entry);
                                    else index.WriteEvidence(entry, StoryBundleEvidence.FromResult(local));
                                }
                            }
                            else if (quick)
                            {
                                entry.CandidateState = !local.HasEvidence ? "quick-negative" : "quick-candidate";
                                entry.QuickMission = missionFilter;
                            }
                            else entry.CandidateState = !local.HasEvidence ? "no-candidate" : "candidate";
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (resultLock)
                        {
                            result.BundlesFailed++;
                            Console.Error.WriteLine($"  Story Timeline scan skipped {candidate.File.FileName}: {ex.Message}");
                        }
                        if (index is not null) index.MarkUnknown(entry, ex);
                    }
                    finally
                    {
                        manager.Clear();
                        currentIndex[candidate.LogicalId] = entry;
                        long done = Interlocked.Increment(ref completed);
                        if (done % 1000 == 0 || done == inventory.Count)
                        {
                            lock (resultLock)
                                Console.WriteLine(
                                    $"  Story Timeline scan progress: {done:N0}/{inventory.Count:N0}; "
                                    + $"scanned={result.BundlesScanned:N0}; reused={result.BundlesReused:N0}; "
                                    + $"dialogs={result.DialogTimelinesByDialogId.Count:N0}; trees={result.DialogTreesByDialogId.Count:N0}; "
                                    + $"cutscenes={result.CutscenesBySceneId.Count:N0}");
                        }
                    }
                    return manager;
                },
                manager => manager.Clear());
        }
        finally
        {
            AS.Logger.Silent = previousLoggerSilent;
            AS.Progress.Silent = previousProgressSilent;
            result.BundlesQuickSkipped = quickSkipped;
            index?.Save(currentIndex);
        }
        SortEvidence(result.DialogTimelinesByDialogId);
        SortEvidence(result.DialogTreesByDialogId);
        SortEvidence(result.CutscenesBySceneId);
        return result;
    }

    private static List<Candidate> BuildInventory(string vfsPath, string? baseVfsPath, StoryTimelineIndex? index)
    {
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSource(new VfsLoader(vfsPath, Keys.ChaCha20Key, baseVfsPath));
        if (!string.IsNullOrWhiteSpace(baseVfsPath)) AddSource(new VfsLoader(baseVfsPath, Keys.ChaCha20Key));
        return candidates;

        void AddSource(VfsLoader loader)
        {
            BlockMainInfo block;
            try { block = loader.LoadBlockInfo(BlockType.Bundle); }
            catch (DirectoryNotFoundException) { return; }
            catch (FileNotFoundException) { return; }
            foreach (ChunkInfo chunk in block.Chunks)
            foreach (AnimeStudio.Endfield.FileInfo file in chunk.Files)
            {
                if (!IsBundleFile(file) || !seen.Add(file.FileName)) continue;
                string logicalId = file.FileName.ToLowerInvariant();
                string dataMd5 = Hex(file.FileDataMd5);
                string contentKey = $"story-bundle-v2\0{dataMd5}\0{file.Length.ToString(CultureInfo.InvariantCulture)}";
                StoryTimelineIndexEntry entry = index?.CreateEntry(contentKey)
                    ?? new StoryTimelineIndexEntry
                    {
                        ContentKey = contentKey,
                    };
                candidates.Add(new Candidate(loader, chunk, file, logicalId, contentKey, entry));
            }
        }
    }

    private static AS.AssetsManager NewManager() => new()
    {
        Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield),
        Silent = true,
        SkipProcess = true,
    };

    private static void ScanBundle(
        Stream bundle,
        string virtualPath,
        AS.AssetsManager manager,
        StoryTimelineScanResult result,
        string? mission,
        bool quick)
    {
        manager.LoadFileFromStream(virtualPath, bundle);
        foreach (AS.SerializedFile file in manager.assetsFileList) ScanFile(file, manager.Game, result, mission, quick);
    }

    private static void ScanFile(
        AS.SerializedFile file,
        AS.Game game,
        StoryTimelineScanResult result,
        string? mission,
        bool quick)
    {
        var infos = file.m_Objects.GroupBy(info => info.m_PathID).ToDictionary(group => group.Key, group => group.First());
        var roots = file.m_Objects.Where(info => IsCandidateRoot(file, info, game)).ToArray();
        if (quick && !string.IsNullOrWhiteSpace(mission))
            roots = roots.Where(info => RootName(file, info, game).Contains(mission, StringComparison.OrdinalIgnoreCase)).ToArray();
        result.RootsFound += roots.Length;
        var records = new Dictionary<long, StoryTimelineRecord>();
        var pending = new Queue<AS.ObjectInfo>(roots);
        var visited = new HashSet<long>();
        while (pending.Count > 0)
        {
            AS.ObjectInfo info = pending.Dequeue();
            if (!visited.Add(info.m_PathID) || Read(file, info, game) is not StoryTimelineRecord record) continue;
            records[info.m_PathID] = record;
            result.GraphObjectsRead++;
            foreach (long reference in GraphReferences(record.Payload))
                if (infos.TryGetValue(reference, out AS.ObjectInfo? target)) pending.Enqueue(target);
        }
        StoryTimelineEvidence.Recover(records.Values, result, mission);
    }

    private static StoryTimelineRecord? Read(AS.SerializedFile file, AS.ObjectInfo info, AS.Game game)
    {
        try
        {
            var reader = new AS.ObjectReader(file.reader, file, info, game);
            AS.Object asset;
            JsonObject payload;
            if (info.classID == TextAsset)
            {
                var text = new AS.TextAsset(reader);
                asset = text;
                payload = new JsonObject
                {
                    ["m_Name"] = text.Name,
                    ["m_Script"] = Convert.ToBase64String(text.m_Script ?? []),
                };
            }
            else
            {
                asset = info.classID == MonoBehaviour ? new AS.MonoBehaviour(reader) : new AS.Object(reader);
                payload = JsonSerializer.SerializeToNode(asset.ToType(), DirectJsonOptions) as JsonObject ?? new JsonObject();
            }
            return new StoryTimelineRecord
            {
                SourceFile = file.fileName,
                PathId = info.m_PathID,
                ClassId = info.classID,
                Name = asset.Name ?? string.Empty,
                Payload = payload,
            };
        }
        catch { return null; }
    }

    private static bool IsCandidateRoot(AS.SerializedFile file, AS.ObjectInfo info, AS.Game game)
    {
        if (info.classID == PlayableDirector) return true;
        if (info.classID is not (MonoBehaviour or TextAsset)) return false;
        string name = RootName(file, info, game);
        return info.classID == TextAsset
            ? name.Contains("dialogtree", StringComparison.OrdinalIgnoreCase) || name.Contains("dlg_", StringComparison.OrdinalIgnoreCase)
            : name.Contains("dlgtl_", StringComparison.OrdinalIgnoreCase) || name.Contains("timeline", StringComparison.OrdinalIgnoreCase)
                || name.Contains("cutscene", StringComparison.OrdinalIgnoreCase) || name.Contains("subtitle", StringComparison.OrdinalIgnoreCase)
                || name.Contains("cs_video_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("black_", StringComparison.OrdinalIgnoreCase);
    }

    private static string RootName(AS.SerializedFile file, AS.ObjectInfo info, AS.Game game)
    {
        try
        {
            var reader = new AS.ObjectReader(file.reader, file, info, game);
            return info.classID == TextAsset
                ? new AS.TextAsset(reader).Name ?? string.Empty
                : info.classID == MonoBehaviour
                    ? new AS.MonoBehaviour(reader).Name ?? string.Empty
                    : new AS.Object(reader).Name ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static void Merge(StoryTimelineScanResult destination, StoryTimelineScanResult source, string? mission)
    {
        MergeMap(destination.DialogTimelinesByDialogId, source.DialogTimelinesByDialogId, mission, keepNumeric: false);
        MergeMap(destination.DialogTreesByDialogId, source.DialogTreesByDialogId, mission, keepNumeric: false);
        MergeMap(destination.CutscenesBySceneId, source.CutscenesBySceneId, mission, keepNumeric: true);
        destination.BundlesScanned += source.BundlesScanned;
        destination.BundlesFailed += source.BundlesFailed;
        destination.RootsFound += source.RootsFound;
        destination.GraphObjectsRead += source.GraphObjectsRead;
    }

    private static void MergeMap(
        Dictionary<string, JsonArray> destination,
        Dictionary<string, JsonArray> source,
        string? mission,
        bool keepNumeric)
    {
        foreach ((string id, JsonArray values) in source)
        {
            if (!(keepNumeric && id.StartsWith('#')) && !MatchesMission(id, mission)) continue;
            if (!destination.TryGetValue(id, out JsonArray? existing)) destination[id] = existing = [];
            foreach (JsonNode? value in values) existing.Add(value?.DeepClone());
        }
    }

    private static void SortEvidence(Dictionary<string, JsonArray> evidence)
    {
        foreach (JsonArray values in evidence.Values)
        {
            JsonNode?[] ordered = values
                .OrderBy(value => value?.ToJsonString() ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
            values.Clear();
            foreach (JsonNode? value in ordered) values.Add(value);
        }
    }

    private static bool MatchesMission(string id, string? mission)
        => string.IsNullOrWhiteSpace(mission)
            || id.Contains("_" + mission + "_", StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("_" + mission, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<long> GraphReferences(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["m_PathID"] is JsonValue value && value.TryGetValue<long>(out long path) && path != 0
                && !(obj["m_FileID"] is JsonValue file && file.TryGetValue<long>(out long id) && id != 0)) yield return path;
            foreach (var pair in obj)
                if (pair.Value is not null)
                    foreach (long reference in GraphReferences(pair.Value)) yield return reference;
        }
        else if (node is JsonArray array)
            foreach (JsonNode? child in array)
                if (child is not null)
                    foreach (long reference in GraphReferences(child)) yield return reference;
    }

    private static Stream CreateBundleStream(AnimeStudio.Endfield.FileInfo file, string scratchPath)
    {
        if (file.Length <= MemoryStreamLimit && file.Length <= int.MaxValue)
            return new MemoryStream(checked((int)Math.Max(0, file.Length)));
        Directory.CreateDirectory(scratchPath);
        return new FileStream(
            Path.Combine(scratchPath, "large-" + Guid.NewGuid().ToString("N") + ".bundle"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

    private static bool QuickMayContain(Stream stream, string fileName, string? mission)
    {
        if (string.IsNullOrWhiteSpace(mission)) return true;
        if (fileName.Contains(mission, StringComparison.OrdinalIgnoreCase)) return true;
        byte[] lower = Encoding.ASCII.GetBytes(mission.ToLowerInvariant());
        byte[] upper = Encoding.ASCII.GetBytes(mission.ToUpperInvariant());
        stream.Position = 0;
        if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> segment))
        {
            ReadOnlySpan<byte> data = segment.AsSpan(0, checked((int)memory.Length));
            bool found = data.IndexOf(lower) >= 0 || data.IndexOf(upper) >= 0;
            stream.Position = 0;
            return found;
        }
        byte[] buffer = new byte[64 * 1024 + lower.Length - 1];
        int carry = 0;
        while (true)
        {
            int read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read == 0) break;
            int length = carry + read;
            ReadOnlySpan<byte> data = buffer.AsSpan(0, length);
            if (data.IndexOf(lower) >= 0 || data.IndexOf(upper) >= 0) { stream.Position = 0; return true; }
            carry = Math.Min(lower.Length - 1, length);
            data[^carry..].CopyTo(buffer);
        }
        stream.Position = 0;
        return false;
    }

    private static string StableVirtualName(Candidate candidate)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.LogicalId))).ToLowerInvariant();
        return hash + ".bundle";
    }

    private static bool IsBundleFile(AnimeStudio.Endfield.FileInfo file)
        => !string.IsNullOrWhiteSpace(file.FileName)
            && !file.FileName.EndsWith('/')
            && !file.FileName.EndsWith('\\');

    private static string Hex(UInt128 value) => value.ToString("x32", CultureInfo.InvariantCulture);
}
