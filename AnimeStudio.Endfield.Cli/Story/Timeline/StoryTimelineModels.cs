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
}

internal sealed class StoryTimelineRecord
{
    public required string SourceFile { get; init; }
    public required long PathId { get; init; }
    public required int ClassId { get; init; }
    public required string Name { get; init; }
    public required JsonObject Payload { get; init; }
}
