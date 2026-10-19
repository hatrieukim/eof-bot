using System.Text.RegularExpressions;

namespace EofBot.Bench;

public sealed record BenchQuestion(string Id, string Kind, string Question, string Expected, string Sources, bool Nhanh, bool Trung)
{
    // Follow-up questions (NT-01a/b/c) share one session; every other question gets its own
    public string Group => Kind == BenchSet.NoiTiep ? Id[..^1] : Id;

    public bool In(string tier) => tier switch
    {
        BenchSet.Nhanh => Nhanh,
        BenchSet.Trung => Trung,
        _ => true,
    };
}

public static partial class BenchSet
{
    public const string Nhanh = "nhanh", Trung = "trung", Full = "full";
    public static readonly string[] Tiers = [Nhanh, Trung, Full];
    public const string NoiTiep = "Nối tiếp", KhongCo = "Không có";

    // Only used to import the legacy md file. Table row: | ID | Quick | Kind | Question | Expected answer | Sources |
    public static List<BenchQuestion> Parse(string path)
    {
        var list = new List<BenchQuestion>();
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("| ")) continue;
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length != 6 || !IdRegex().IsMatch(cells[0])) continue;
            bool nhanh = cells[1].Contains('⚡');
            list.Add(new BenchQuestion(cells[0], cells[2], cells[3], cells[4], cells[5], nhanh, nhanh || cells[1].Contains('★')));
        }
        return list;
    }

    // First error found, null if valid
    public static string? Validate(BenchQuestion q, DocSnapshot docs, Dictionary<SourceRef, string> sections)
    {
        if (!IdRegex().IsMatch(q.Id)) return "Mã phải có dạng ABC-01 hoặc ABC-01a";
        if (q.Kind == NoiTiep && !char.IsLower(q.Id[^1])) return "Câu nối tiếp phải có mã kết thúc bằng chữ thường (NT-01a, NT-01b…)";
        if (string.IsNullOrWhiteSpace(q.Kind)) return "Thiếu loại";
        if (string.IsNullOrWhiteSpace(q.Question)) return "Thiếu câu hỏi";
        if (string.IsNullOrWhiteSpace(q.Expected)) return "Thiếu đáp án đúng";
        if (q.Kind == KhongCo) return null;

        var refs = Citations.ParseSources(q.Sources);
        if (refs.Count == 0) return "Nguồn phải có dạng: tên-file.md § 4.1, § 5; file-khác.md § 2";
        var loaded = docs.Files.Select(f => Path.GetFileName(f.Path)).ToHashSet();
        if (refs.FirstOrDefault(r => !loaded.Contains(r.File)) is { } f) return $"Không có file {f.File} trong docs đang nạp";
        if (refs.FirstOrDefault(r => r.Section is not null && !sections.ContainsKey(r)) is { } s)
            return $"Không có mục {s.Section} trong {s.File}";
        return null;
    }

    [GeneratedRegex(@"^[A-Z]{2,3}-\d{2}[a-z]?$")]
    private static partial Regex IdRegex();
}
