using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeStudio.Endfield.Cli.Story;

internal static class StoryMissionCatalog
{
    private static readonly Regex MissionId = new(
        @"^[a-z]+\d+(?:l\d+)*m\d+(?:d\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void Print(StoryTableRepository repository, string language)
    {
        JsonObject textTable = repository.Table("TextTable");
        List<string> missionIds = ListedMissionIds(repository);
        List<string> languages = language.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? repository.Tables.Keys
                .Where(name => name.StartsWith("I18nTextTable_", StringComparison.OrdinalIgnoreCase))
                .Select(name => name["I18nTextTable_".Length..].ToUpperInvariant())
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [language.ToUpperInvariant()];

        if (languages.Count == 0) languages.Add("JP");
        foreach (string locale in languages)
        {
            JsonObject i18n = repository.Table("I18nTextTable_" + locale);
            int idWidth = Math.Max("MISSION ID".Length, missionIds.Select(id => id.Length).DefaultIfEmpty(0).Max());
            Console.WriteLine($"[{locale}] exportable story missions");
            Console.WriteLine($"{"MISSION ID".PadRight(idWidth)}  NAME");
            int localized = 0;
            foreach (string missionId in missionIds)
            {
                string? name = ResolveName(textTable, i18n, missionId);
                if (name is not null) localized++;
                Console.WriteLine($"{missionId.PadRight(idWidth)}  {Printable(name) ?? "-"}");
            }
            Console.WriteLine($"Total: {missionIds.Count:N0}; localized: {localized:N0}; missing: {missionIds.Count - localized:N0}");
            if (locale != languages[^1]) Console.WriteLine();
        }
    }

    internal static List<string> ListedMissionIds(StoryTableRepository repository)
        => StoryExporter.MissionIds(repository, requested: null)
            .Where(missionId => MissionId.IsMatch(missionId))
            .ToList();

    private static string? Printable(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    private static string? ResolveName(JsonObject textTable, JsonObject i18n, string missionId)
    {
        JsonNode? nameNode = FindNameNode(textTable, missionId);
        if (nameNode is not JsonObject nameRow) return null;
        string? inline = Scalar(nameRow["text"]);
        if (!string.IsNullOrEmpty(inline)) return inline;
        string? textId = Scalar(nameRow["id"]);
        if (string.IsNullOrWhiteSpace(textId) || textId == "0") return null;
        return ResolveI18n(i18n[textId]);
    }

    private static JsonNode? FindNameNode(JsonObject textTable, string missionId)
        => textTable.FirstOrDefault(pair =>
            pair.Key.Equals(missionId + "_name", StringComparison.OrdinalIgnoreCase)).Value;

    private static string? ResolveI18n(JsonNode? node)
    {
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? direct))
            return string.IsNullOrWhiteSpace(direct) ? null : direct;
        if (node is not JsonObject row) return null;
        foreach (string field in new[] { "text", "value", "content", "str" })
            if (Scalar(row[field]) is string value && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    private static string? Scalar(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out string? text)
            ? text
            : node?.ToJsonString().Trim('"');
}
