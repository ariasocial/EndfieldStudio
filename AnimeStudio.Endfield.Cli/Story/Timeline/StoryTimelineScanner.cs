using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AnimeStudio.Endfield;
using AS = AnimeStudio;
using Newtonsoft.Json;

namespace AnimeStudio.Endfield.Cli.Story.Timeline;

/// <summary>Optional full Bundle scan for source-backed story Timeline evidence.</summary>
public static class StoryTimelineScanner
{
    private const int MonoBehaviour = 114;
    private const int PlayableDirector = 320;
    private const int TextAsset = 49;

    public static StoryTimelineScanResult Scan(string vfsPath, string? baseVfsPath, string scratchPath, string? missionFilter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vfsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchPath);
        var result = new StoryTimelineScanResult();
        Directory.CreateDirectory(scratchPath);
        var candidates = new List<(VfsLoader Loader, ChunkInfo Chunk, AnimeStudio.Endfield.FileInfo File)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Primary metadata wins. Its chunks may still physically live in base,
        // so the primary loader retains base as its chunk fallback. A separate
        // base metadata pass then adds only base-only logical bundle entries.
        AddSource(new VfsLoader(vfsPath, Keys.ChaCha20Key, baseVfsPath));
        if (!string.IsNullOrWhiteSpace(baseVfsPath))
            AddSource(new VfsLoader(baseVfsPath, Keys.ChaCha20Key));
        long total = candidates.Count;
        long processed = 0;
        Console.WriteLine($"  scanning Story Timeline evidence from {total:N0} Bundle file(s)...");
        foreach (var candidate in candidates)
        {
            VfsLoader loader = candidate.Loader;
            ChunkInfo chunk = candidate.Chunk;
            AnimeStudio.Endfield.FileInfo file = candidate.File;
            string path = Path.Combine(scratchPath, StableScratchName(chunk, file));
            try
            {
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan)) loader.ExtractFile(BlockType.Bundle, chunk, file, output);
                ScanBundle(path, result, missionFilter);
                result.BundlesScanned++;
            }
            catch (Exception ex)
            {
                result.BundlesFailed++;
                Console.Error.WriteLine($"  Story Timeline scan skipped {file.FileName}: {ex.Message}");
            }
            finally { try { File.Delete(path); } catch { } }
            processed++;
            if (processed % 1000 == 0 || processed == total)
                Console.WriteLine($"  Story Timeline scan progress: {processed:N0}/{total:N0}; dialogs={result.DialogTimelinesByDialogId.Count:N0}; trees={result.DialogTreesByDialogId.Count:N0}; cutscenes={result.CutscenesBySceneId.Count:N0}");
        }
        return result;

        void AddSource(VfsLoader loader)
        {
            BlockMainInfo block;
            try { block = loader.LoadBlockInfo(BlockType.Bundle); }
            catch (DirectoryNotFoundException) { return; }
            catch (FileNotFoundException) { return; }
            foreach (ChunkInfo chunk in block.Chunks)
            foreach (AnimeStudio.Endfield.FileInfo file in chunk.Files)
                if (IsBundleFile(file) && seen.Add(file.FileName)) candidates.Add((loader, chunk, file));
        }
    }

    private static void ScanBundle(string path, StoryTimelineScanResult result, string? mission)
    {
        var manager = new AS.AssetsManager { Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield), Silent = true, SkipProcess = true };
        try { manager.LoadFiles(path); foreach (AS.SerializedFile file in manager.assetsFileList) ScanFile(file, manager.Game, result, mission); }
        finally { manager.Clear(); }
    }

    private static void ScanFile(AS.SerializedFile file, AS.Game game, StoryTimelineScanResult result, string? mission)
    {
        var infos = file.m_Objects.GroupBy(info => info.m_PathID).ToDictionary(group => group.Key, group => group.First());
        var roots = file.m_Objects.Where(info => IsCandidateRoot(file, info, game)).ToArray();
        result.RootsFound += roots.Length;
        var records = new Dictionary<long, StoryTimelineRecord>(); var pending = new Queue<AS.ObjectInfo>(roots); var visited = new HashSet<long>();
        while (pending.Count > 0)
        {
            AS.ObjectInfo info = pending.Dequeue(); if (!visited.Add(info.m_PathID) || Read(file, info, game) is not StoryTimelineRecord record) continue;
            records[info.m_PathID] = record; result.GraphObjectsRead++;
            foreach (long reference in GraphReferences(record.Payload)) if (infos.TryGetValue(reference, out AS.ObjectInfo? target)) pending.Enqueue(target);
        }
        StoryTimelineEvidence.Recover(records.Values, result, mission);
    }

    private static StoryTimelineRecord? Read(AS.SerializedFile file, AS.ObjectInfo info, AS.Game game)
    {
        try
        {
            var reader = new AS.ObjectReader(file.reader, file, info, game); AS.Object asset; JsonObject payload;
            if (info.classID == TextAsset) { var text = new AS.TextAsset(reader); asset = text; payload = new JsonObject { ["m_Name"] = text.Name, ["m_Script"] = Convert.ToBase64String(text.m_Script ?? []) }; }
            else { asset = info.classID == MonoBehaviour ? new AS.MonoBehaviour(reader) : new AS.Object(reader); if (JsonNode.Parse(JsonConvert.SerializeObject(asset.ToType())) is not JsonObject parsed) return null; payload = parsed; }
            return new StoryTimelineRecord { SourceFile = file.fileName, PathId = info.m_PathID, ClassId = info.classID, Name = asset.Name ?? string.Empty, Payload = payload };
        }
        catch { return null; }
    }
    private static bool IsCandidateRoot(AS.SerializedFile file, AS.ObjectInfo info, AS.Game game)
    {
        if (info.classID == PlayableDirector) return true;
        if (info.classID is not (MonoBehaviour or TextAsset)) return false;
        try
        {
            var reader = new AS.ObjectReader(file.reader, file, info, game);
            string name = info.classID == TextAsset ? new AS.TextAsset(reader).Name ?? string.Empty : new AS.MonoBehaviour(reader).Name ?? string.Empty;
            return info.classID == TextAsset
                ? name.Contains("dialogtree", StringComparison.OrdinalIgnoreCase) || name.Contains("dlg_", StringComparison.OrdinalIgnoreCase)
                : name.Contains("dlgtl_", StringComparison.OrdinalIgnoreCase) || name.Contains("timeline", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("cutscene", StringComparison.OrdinalIgnoreCase) || name.Contains("subtitle", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("cs_video_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("black_", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private static IEnumerable<long> GraphReferences(JsonNode node) { if (node is JsonObject obj) { if (obj["m_PathID"] is JsonValue value && value.TryGetValue<long>(out long path) && path != 0 && !(obj["m_FileID"] is JsonValue file && file.TryGetValue<long>(out long id) && id != 0)) yield return path; foreach (var pair in obj) foreach (long reference in GraphReferences(pair.Value!)) yield return reference; } else if (node is JsonArray array) foreach (JsonNode? child in array) if (child is not null) foreach (long reference in GraphReferences(child)) yield return reference; }
    private static string StableScratchName(ChunkInfo chunk, AnimeStudio.Endfield.FileInfo file)
    {
        string source = $"{chunk.FileName()}\u001f{file.FileName}\u001f{file.Offset}\u001f{file.Length}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hash + ".bundle";
    }
    private static bool IsBundleFile(AnimeStudio.Endfield.FileInfo file)
        => !string.IsNullOrWhiteSpace(file.FileName)
            && !file.FileName.EndsWith('/')
            && !file.FileName.EndsWith('\\');
}
