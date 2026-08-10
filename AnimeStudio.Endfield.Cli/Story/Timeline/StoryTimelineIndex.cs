using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AnimeStudio.Endfield.Cli.Story.Timeline;

/// <summary>
/// Rebuildable per-bundle evidence cache. Empty bundles are retained in the
/// manifest as negative candidate entries so subsequent exports do not reopen
/// them. Evidence files contain unfiltered scene data and are reusable across
/// missions and languages.
/// </summary>
internal sealed class StoryTimelineIndex
{
    public const int CurrentSchemaVersion = 3;
    private const string ManifestName = "manifest.json.gz";
    private readonly string _root;
    private readonly string _bundleRoot;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public StoryTimelineIndex(string root, bool rebuild)
    {
        _root = Path.GetFullPath(root);
        _bundleRoot = Path.Combine(_root, "bundles");
        Manifest = rebuild ? NewManifest() : ReadManifest();
    }

    public StoryTimelineIndexManifest Manifest { get; }

    public bool TryRead(
        string logicalId,
        string contentKey,
        string? quickMission,
        out StoryTimelineIndexEntry? entry,
        out StoryBundleEvidence? evidence)
    {
        evidence = null;
        if (!Manifest.Bundles.TryGetValue(logicalId, out entry)
            || entry.ContentKey != contentKey
            || entry.CandidateState == "unknown"
            || entry.CandidateState.StartsWith("quick-", StringComparison.Ordinal)
                && (quickMission is null || !string.Equals(entry.QuickMission, quickMission, StringComparison.OrdinalIgnoreCase))) return false;
        if (entry.CandidateState == "quick-negative") return true;
        if (entry.CandidateState == "no-candidate") return true;
        if (string.IsNullOrWhiteSpace(entry.EvidenceFile)) return false;
        string path = Path.Combine(_root, entry.EvidenceFile);
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            evidence = JsonSerializer.Deserialize<StoryBundleEvidence>(gzip, _json);
            return evidence is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return false;
        }
    }

    public StoryTimelineIndexEntry CreateEntry(string contentKey) => new()
    {
        ContentKey = contentKey,
        CandidateState = "unknown",
    };

    public void WriteEvidence(StoryTimelineIndexEntry entry, StoryBundleEvidence evidence)
    {
        Directory.CreateDirectory(_bundleRoot);
        string name = Hash(entry.ContentKey) + ".json.gz";
        string relative = Path.Combine("bundles", name);
        string path = Path.Combine(_root, relative);
        AtomicWrite(path, stream =>
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            JsonSerializer.Serialize(gzip, evidence, _json);
        });
        entry.CandidateState = "candidate";
        entry.EvidenceFile = relative;
        entry.Error = null;
        entry.QuickMission = null;
    }

    public void WriteQuickEvidence(StoryTimelineIndexEntry entry, StoryBundleEvidence evidence, string mission)
    {
        WriteEvidence(entry, evidence);
        entry.CandidateState = "quick-candidate";
        entry.QuickMission = mission;
    }

    public void MarkEmpty(StoryTimelineIndexEntry entry)
    {
        entry.CandidateState = "no-candidate";
        entry.EvidenceFile = null;
        entry.Error = null;
        entry.QuickMission = null;
    }

    public void MarkQuickNegative(StoryTimelineIndexEntry entry, string mission)
    {
        entry.CandidateState = "quick-negative";
        entry.EvidenceFile = null;
        entry.Error = null;
        entry.QuickMission = mission;
    }

    public void MarkUnknown(StoryTimelineIndexEntry entry, Exception error)
    {
        entry.CandidateState = "unknown";
        entry.EvidenceFile = null;
        entry.Error = error.Message;
        entry.QuickMission = null;
    }

    public void Save(IReadOnlyDictionary<string, StoryTimelineIndexEntry> current)
    {
        Manifest.SchemaVersion = CurrentSchemaVersion;
        // Concurrent scan completion order must not leak into the persisted
        // manifest. Stable ordering keeps rebuilds byte-for-byte comparable.
        Manifest.Bundles = current
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_root);
        AtomicWrite(Path.Combine(_root, ManifestName), stream =>
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            JsonSerializer.Serialize(gzip, Manifest, _json);
        });
        PruneEvidenceFiles(current.Values.Select(value => value.EvidenceFile));
    }

    private StoryTimelineIndexManifest ReadManifest()
    {
        try
        {
            string path = Path.Combine(_root, ManifestName);
            using var stream = File.OpenRead(path);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            StoryTimelineIndexManifest? manifest = JsonSerializer.Deserialize<StoryTimelineIndexManifest>(gzip, _json);
            return manifest?.SchemaVersion == CurrentSchemaVersion ? manifest : NewManifest();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return NewManifest();
        }
    }

    private void PruneEvidenceFiles(IEnumerable<string?> retainedFiles)
    {
        if (!Directory.Exists(_bundleRoot)) return;
        var retained = retainedFiles.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(Path.Combine(_root, value!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(_bundleRoot, "*.json.gz"))
            if (!retained.Contains(Path.GetFullPath(file)))
                try { File.Delete(file); } catch (IOException) { }
    }

    private static StoryTimelineIndexManifest NewManifest() => new() { SchemaVersion = CurrentSchemaVersion };

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void AtomicWrite(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}
