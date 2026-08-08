using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.Endfield.Cli.Story;

/// <summary>Evidence-only decoder for LevelScriptData's stable record envelope.</summary>
internal static class LevelScriptEvidence
{
    private static readonly Regex Uid = new("[0-9a-fA-F]{8}", RegexOptions.Compiled);
    private static readonly Regex Scene = new(@"(?:f_|m_|fm_)?(?:dlg|cutscene|cs_video|black|remotecomm|radio|sns)_[A-Za-z0-9_]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    internal sealed record Record(int Start, int LocalId, int NextId, int PayloadStart, List<(int Offset, string Text)> Strings);
    internal sealed record Chain(List<Record> Records);

    public static List<Chain> Decode(byte[] data)
    {
        var strings = Strings(data); var records = new List<Record>(); var starts = new HashSet<int>();
        foreach (Match match in Uid.Matches(Encoding.ASCII.GetString(data)))
        {
            int start = match.Index - 14;
            if (start < 0 || start + 32 > data.Length || data[start] != 0xFA || data[start + 4] != 0 || data[start + 9] != 0 || U32(data, start + 10) != 8) continue;
            int local = (int)U32(data, start + 5);
            if (local > 0x1000 || !starts.Add(start)) continue;
            records.Add(new Record(start, local, I32(data, start + 28), start + 32, []));
        }
        records.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 0; i < records.Count; i++)
            records[i].Strings.AddRange(strings.Where(s => s.Offset >= records[i].PayloadStart && s.Offset < (i + 1 < records.Count ? records[i + 1].Start : data.Length)));
        var ids = records.GroupBy(r => r.LocalId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var targets = records.Select(r => ids.GetValueOrDefault(r.NextId)?.Start).OfType<int>().ToHashSet();
        var byStart = records.ToDictionary(r => r.Start); var seen = new HashSet<int>(); var chains = new List<Chain>();
        foreach (int start in records.Where(r => !targets.Contains(r.Start)).Select(r => r.Start)) AddChain(start);
        foreach (Record record in records) if (!seen.Contains(record.Start)) AddChain(record.Start);
        return chains;
        void AddChain(int start) { var list = new List<Record>(); for (int current = start; byStart.TryGetValue(current, out var r) && seen.Add(current); current = ids.TryGetValue(r.NextId, out var next) ? next.Start : -1) list.Add(r); if (list.Count > 0) chains.Add(new Chain(list)); }
    }

    public static List<(string SceneId, int Offset)> SceneKeys(Chain chain, string? mission)
    {
        var result = new List<(string SceneId, int Offset)>();
        foreach (var hit in chain.Records.SelectMany(r => r.Strings).OrderBy(s => s.Offset))
        foreach (Match match in Scene.Matches(hit.Text))
        {
            string value = Normalize(match.Value);
            if (mission is not null && !value.Contains($"_{mission}_", StringComparison.OrdinalIgnoreCase) && !value.EndsWith($"_{mission}", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Any(item => item.SceneId.Equals(value, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add((value, hit.Offset));
        }
        return result;
    }

    private static string Normalize(string value)
    {
        foreach (string prefix in new[] { "fm_", "f_", "m_" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
                break;
            }
        foreach (string suffix in new[] { "_Played", "Played", "_Done", "_Finished" })
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length];
                break;
            }
        return value;
    }

    private static List<(int Offset, string Text)> Strings(byte[] data)
    {
        var result = new List<(int, string)>();
        for (int i = 0; i + 5 <= data.Length; i++)
        {
            int sizeOffset = data[i] == 4 ? i + 1 : i; uint length = U32(data, sizeOffset);
            int textOffset = sizeOffset + 4;
            if (length < 3 || length > 120 || textOffset + length > data.Length) continue;
            ReadOnlySpan<byte> bytes = data.AsSpan(textOffset, (int)length);
            bool printable = true; foreach (byte value in bytes) if (value < 0x20 || value > 0x7e) { printable = false; break; }
            if (!printable) continue;
            result.Add((i, Encoding.ASCII.GetString(bytes))); i = textOffset + (int)length - 1;
        }
        return result;
    }
    private static uint U32(byte[] data, int offset) => offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) : 0;
    private static int I32(byte[] data, int offset) => offset + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) : -1;
}
