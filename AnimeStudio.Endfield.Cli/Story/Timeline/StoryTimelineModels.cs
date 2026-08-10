using System.Text.Json.Nodes;

namespace AnimeStudio.Endfield.Cli.Story.Timeline;

/// <summary>Evidence recovered from Unity Timeline and DialogTree assets.</summary>
public sealed class StoryTimelineScanResult
{
    /// <summary>Timeline clip evidence keyed by the exact <c>dlg_*</c> scene ID.</summary>
    public Dictionary<string, JsonArray> DialogTimelinesByDialogId { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>DialogTree graph evidence keyed by the exact <c>dlg_*</c> scene ID.</summary>
    public Dictionary<string, JsonArray> DialogTreesByDialogId { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Subtitle Timeline evidence keyed by its exact scene ID. In particular,
    /// <c>cs_video_*</c> is deliberately not merged into <c>cutscene_*</c>.
    /// </summary>
    public Dictionary<string, JsonArray> CutscenesBySceneId { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long BundlesScanned { get; internal set; }
    public long BundlesFailed { get; internal set; }
    public long RootsFound { get; internal set; }
    public long GraphObjectsRead { get; internal set; }
    public long BundleCandidates { get; internal set; }
    public long BundlesReused { get; internal set; }
    public long BundlesQuickSkipped { get; internal set; }
    public long BundlesChanged { get; internal set; }

    internal bool HasEvidence => DialogTimelinesByDialogId.Count > 0
        || DialogTreesByDialogId.Count > 0
        || CutscenesBySceneId.Count > 0;
}

internal sealed class StoryBundleEvidence
{
    public Dictionary<string, JsonArray> DialogTimelines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonArray> DialogTrees { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonArray> Cutscenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long RootsFound { get; set; }
    public long GraphObjectsRead { get; set; }

    public static StoryBundleEvidence FromResult(StoryTimelineScanResult result) => new()
    {
        DialogTimelines = result.DialogTimelinesByDialogId,
        DialogTrees = result.DialogTreesByDialogId,
        Cutscenes = result.CutscenesBySceneId,
        RootsFound = result.RootsFound,
        GraphObjectsRead = result.GraphObjectsRead,
    };

    public StoryTimelineScanResult ToResult()
    {
        var result = new StoryTimelineScanResult
        {
            RootsFound = RootsFound,
            GraphObjectsRead = GraphObjectsRead,
        };
        Copy(DialogTimelines, result.DialogTimelinesByDialogId);
        Copy(DialogTrees, result.DialogTreesByDialogId);
        Copy(Cutscenes, result.CutscenesBySceneId);
        return result;
    }

    private static void Copy(Dictionary<string, JsonArray> source, Dictionary<string, JsonArray> destination)
    {
        foreach ((string key, JsonArray value) in source) destination[key] = value;
    }
}

internal sealed class StoryTimelineIndexManifest
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, StoryTimelineIndexEntry> Bundles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class StoryTimelineIndexEntry
{
    public string ContentKey { get; set; } = string.Empty;
    public string CandidateState { get; set; } = "unknown";
    public string? EvidenceFile { get; set; }
    public string? Error { get; set; }
    public string? QuickMission { get; set; }
}

internal sealed class StoryTimelineRecord
{
    public required string SourceFile { get; init; }
    public required long PathId { get; init; }
    public required int ClassId { get; init; }
    public required string Name { get; init; }
    public required JsonObject Payload { get; init; }
}
