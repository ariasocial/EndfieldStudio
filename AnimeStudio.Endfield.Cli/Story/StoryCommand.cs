using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeStudio.Endfield.Cli.Story;

internal static class StoryCommand
{
    internal sealed class Options
    {
        public string? VfsPath { get; set; }
        public string? BaseVfsPath { get; set; }
        public string? OutputPath { get; set; }
        public string Language { get; set; } = "JP";
        public string? Mission { get; set; }
        public string? Scene { get; set; }
        public string Timeline { get; set; } = "off";
        public string? Scratch { get; set; }
        public string? Overrides { get; set; }
        public bool Snapshot { get; set; }
    }

    public static int Run(ReadOnlySpan<string> args)
    {
        Options options = Parse(args, out bool help);
        if (help) return 0;
        if (options.VfsPath is null) throw new ArgumentException("--vfs is required");
        if (options.OutputPath is null) throw new ArgumentException("--out is required");
        if (options.Mission is null && options.Scene is null)
            throw new ArgumentException("--mission or --scene is required; automatic all-mission membership is not reliable");
        if (!Regex.IsMatch(options.Language, "^[A-Za-z]{2,8}$|^ALL$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            throw new ArgumentException("--language must be a language code or ALL");
        if (options.Timeline is not "off" and not "full") throw new ArgumentException("--timeline must be off or full");

        var repository = StoryTableRepository.Load(options);
        var overrides = StoryOverrides.Load(options.Overrides);
        StoryExporter.Export(repository, overrides, options);
        return 0;
    }

    private static Options Parse(ReadOnlySpan<string> args, out bool help)
    {
        var result = new Options();
        help = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--vfs": result.VfsPath = Value(args, ref i, arg); break;
                case "--base-vfs": result.BaseVfsPath = Value(args, ref i, arg); break;
                case "--out": result.OutputPath = Value(args, ref i, arg); break;
                case "--language": result.Language = Value(args, ref i, arg).ToUpperInvariant(); break;
                case "--mission": result.Mission = Value(args, ref i, arg); break;
                case "--scene": result.Scene = Value(args, ref i, arg); break;
                case "--timeline": result.Timeline = Value(args, ref i, arg).ToLowerInvariant(); break;
                case "--no-timeline": result.Timeline = "off"; break;
                case "--scratch": result.Scratch = Value(args, ref i, arg); break;
                case "--overrides": result.Overrides = Value(args, ref i, arg); break;
                case "--snapshot": result.Snapshot = true; break;
                case "--help": case "-h": PrintHelp(); help = true; return result;
                default: throw new ArgumentException($"Unknown story argument: {arg}");
            }
        }
        return result;
    }

    private static string Value(ReadOnlySpan<string> args, ref int index, string option)
        => ++index < args.Length ? args[index] : throw new ArgumentException($"{option} requires a value");

    private static void PrintHelp() => Console.WriteLine("""
        Usage: endfield-dump story --vfs <path> --out <dir> [options]
          --base-vfs <path>       Fallback VFS for metadata/chunks absent from --vfs
          --language <code|ALL>   Locale (default JP)
          --mission <id>          Mission to export (required unless --scene is used)
          --scene <id>            Normalized scene id (infers its mission when possible)
          --timeline off|full     Unity Timeline recovery hook (default off)
          --no-timeline           Alias for --timeline off
          --scratch <dir>         Temporary Timeline scanner workspace
          --overrides <json>      External graph/scene overrides
          --snapshot              Include source snapshot metadata
        """);
}

internal static class StoryOverrides
{
    public static JsonObject Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new JsonObject();
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject value)
            throw new ArgumentException("--overrides must contain a JSON object");
        return value;
    }
}
