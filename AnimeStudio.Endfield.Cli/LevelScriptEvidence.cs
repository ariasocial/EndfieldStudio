using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.Endfield.Cli;

/// <summary>
/// Minimal, evidence-only decoder for the serialized LevelScriptData record
/// envelope. The gameplay payloads are intentionally left opaque; the stable
/// record envelope (code, kind, localId, uid, nextId) and its length-prefixed
/// strings are enough to recover authored UID-chain scene order.
/// </summary>
internal static class LevelScriptEvidence
{
    private static readonly Regex UidPattern = new(
        "[0-9a-fA-F]{8}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StoryPattern = new(
        @"(?<![A-Za-z0-9_])(?:f_|m_|fm_)?(?:dlg|cutscene|cs_video|black|remotecomm|radio|sns)_[A-Za-z0-9_]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal sealed record StringHit(int Offset, string Text);

    internal sealed record Record(
        int Start,
        int Code,
        int Kind,
        int LocalId,
        string Uid,
        int NextId,
        int PayloadStart,
        List<StringHit> Strings);

    internal sealed record Chain(List<Record> Records);

    public static List<Chain> Decode(byte[] data)
    {
        var tagged = ExtractTaggedStrings(data);
        var plain = ExtractPlainStrings(data, tagged);
        var records = ExtractRecords(data, tagged, plain);
        if (records.Count == 0) return new List<Chain>();

        var uniqueTargets = records
            .GroupBy(record => record.LocalId)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var byStart = records.ToDictionary(record => record.Start);
        var targetStarts = records
            .Select(record => uniqueTargets.GetValueOrDefault(record.NextId)?.Start)
            .Where(start => start is not null)
            .Cast<int>()
            .ToHashSet();
        var entries = records
            .Where(record => !targetStarts.Contains(record.Start))
            .Select(record => record.Start)
            .ToList();
        var chains = new List<Chain>();
        var seen = new HashSet<int>();

        foreach (int entry in entries)
        {
            var chain = new List<Record>();
            int current = entry;
            while (byStart.TryGetValue(current, out Record? record) && seen.Add(record.Start))
            {
                chain.Add(record);
                if (!uniqueTargets.TryGetValue(record.NextId, out Record? next)) break;
                current = next.Start;
            }
            if (chain.Count > 0) chains.Add(new Chain(chain));
        }

        foreach (Record record in records)
            if (seen.Add(record.Start)) chains.Add(new Chain(new List<Record> { record }));

        return chains;
    }

    public static List<string> SceneKeys(Chain chain, string? missionId, out List<int> offsets)
    {
        var keys = new List<string>();
        offsets = new List<int>();
        foreach (Record record in chain.Records)
        foreach (StringHit hit in record.Strings.OrderBy(value => value.Offset))
        foreach (Match match in StoryPattern.Matches(hit.Text))
        {
            string value = Normalize(match.Value);
            if (!string.IsNullOrWhiteSpace(missionId)
                && !value.Contains($"_{missionId}_", StringComparison.OrdinalIgnoreCase)
                && !value.EndsWith($"_{missionId}", StringComparison.OrdinalIgnoreCase))
                continue;
            if (keys.Contains(value, StringComparer.OrdinalIgnoreCase)) continue;
            keys.Add(value);
            offsets.Add(hit.Offset);
        }
        return keys;
    }

    private static string Normalize(string value)
        => value.StartsWith("cs_video_", StringComparison.OrdinalIgnoreCase)
            ? "cutscene_" + value[9..]
            : value;

    private static List<StringHit> ExtractTaggedStrings(byte[] data)
    {
        var result = new List<StringHit>();
        for (int offset = 0; offset + 5 <= data.Length; offset++)
        {
            if (data[offset] != 0x04) continue;
            uint size = ReadUInt32(data, offset + 1);
            if (size == 0 || size > 120 || offset + 5 + size > data.Length) continue;
            var bytes = data.AsSpan(offset + 5, checked((int)size));
            if (!IsPrintableAscii(bytes)) continue;
            result.Add(new StringHit(offset, Encoding.ASCII.GetString(bytes)));
            offset += 4 + (int)size;
        }
        return result;
    }

    private static List<StringHit> ExtractPlainStrings(byte[] data, IReadOnlyList<StringHit> tagged)
    {
        var taggedOffsets = tagged.Select(hit => hit.Offset).ToHashSet();
        var result = new List<StringHit>();
        for (int offset = 0; offset + 4 <= data.Length; offset++)
        {
            uint size = ReadUInt32(data, offset);
            if (size < 3 || size > 120 || offset + 4 + size > data.Length) continue;
            if (offset > 0 && data[offset - 1] == 0x04 && taggedOffsets.Contains(offset - 1)) continue;
            var bytes = data.AsSpan(offset + 4, checked((int)size));
            if (!IsPrintableAscii(bytes)) continue;
            result.Add(new StringHit(offset, Encoding.ASCII.GetString(bytes)));
            offset += 3 + (int)size;
        }
        return result;
    }

    private static List<Record> ExtractRecords(
        byte[] data,
        IReadOnlyList<StringHit> tagged,
        IReadOnlyList<StringHit> plain)
    {
        var records = new List<Record>();
        var starts = new HashSet<int>();
        foreach (Match match in UidPattern.Matches(Encoding.ASCII.GetString(data)))
        {
            int uidOffset = match.Index;
            string uid = match.Value;
            if (!TryDecodeRecord(data, uidOffset, uid, out Record? record)
                || record is null
                || !starts.Add(record.Start)) continue;
            records.Add(record);
        }
        records.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (int index = 0; index < records.Count; index++)
        {
            int nextStart = index + 1 < records.Count ? records[index + 1].Start : data.Length;
            var hits = records[index].Strings;
            foreach (StringHit hit in tagged.Concat(plain)
                         .Where(hit => hit.Offset >= records[index].PayloadStart && hit.Offset < nextStart)
                         .OrderBy(hit => hit.Offset))
                if (!hits.Any(existing => existing.Offset == hit.Offset && existing.Text == hit.Text))
                    hits.Add(hit);
        }
        return records;
    }

    private static bool TryDecodeRecord(byte[] data, int uidOffset, string uid, out Record? record)
    {
        record = null;
        if (uidOffset >= 14)
        {
            int start = uidOffset - 14;
            if (start + 32 <= data.Length
                && data[start] == 0xFA
                && data[start + 4] == 0
                && data[start + 9] == 0
                && ReadUInt32(data, start + 10) == 8)
            {
                uint localId = ReadUInt32(data, start + 5);
                if (localId <= 0x1000)
                {
                    record = new Record(
                        start,
                        ReadUInt16(data, start + 1),
                        data[start + 3],
                        (int)localId,
                        uid,
                        ReadInt32(data, start + 28),
                        start + 32,
                        new List<StringHit>());
                    return true;
                }
            }
        }

        if (uidOffset < 12) return false;
        int plainStart = uidOffset - 12;
        if (plainStart + 30 > data.Length) return false;
        ushort code = ReadUInt16(data, plainStart);
        byte kind = data[plainStart + 2];
        uint local = ReadUInt32(data, plainStart + 3);
        if (code > 0x1FFF || kind > 0x10 || local > 0x1000
            || data[plainStart + 7] != 0
            || ReadUInt32(data, plainStart + 8) != 8) return false;
        record = new Record(
            plainStart,
            code,
            kind,
            (int)local,
            uid,
            ReadInt32(data, plainStart + 26),
            plainStart + 30,
            new List<StringHit>());
        return true;
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
            if (value < 0x20 || value > 0x7E) return false;
        return true;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
        => offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)) : (ushort)0;

    private static uint ReadUInt32(byte[] data, int offset)
        => offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) : 0;

    private static int ReadInt32(byte[] data, int offset)
        => offset + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) : -1;
}
